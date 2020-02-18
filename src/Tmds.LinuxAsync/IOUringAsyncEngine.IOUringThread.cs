using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using static Tmds.Linux.LibC;

namespace Tmds.LinuxAsync
{
    public partial class IOUringAsyncEngine
    {
        sealed class IOUringThread : IDisposable
        {
            private const int PipeKey = -1;
            private const int StateBlocked = 1;
            private const int StateNotBlocked = 0;

            private readonly Dictionary<int, IOUringAsyncContext> _asyncContexts; // TODO: make this a HashSet?
            private readonly Thread _thread;
            private IOUringExecutionQueue? _iouring;
            private CloseSafeHandle? _pipeReadEnd; // TODO: make this an eventfd
            private CloseSafeHandle? _pipeWriteEnd;
            private readonly byte[] _dummyReadBuffer = new byte[8];
            private bool _disposed;
            private int _blockedState;

            struct ScheduledAction
            {
                public IOUringAsyncContext? AsyncContext;
                public Action<IOUringThread, IOUringAsyncContext?> Action;
            }

            private readonly object _actionQueueGate = new object();
            private List<ScheduledAction> _scheduledActions;
            private List<ScheduledAction> _executingActions;

            public IOUringThread()
            {
                _asyncContexts = new Dictionary<int, IOUringAsyncContext>();

                CreateResources();

                _scheduledActions = new List<ScheduledAction>(1024);
                _executingActions = new List<ScheduledAction>(1024);
                _blockedState = StateBlocked;

                _thread = new Thread(EventLoop);
                _thread.IsBackground = true;
                _thread.Start();
            }

            private bool IoUringMayWait()
            {
                bool mayWait;
                lock (_actionQueueGate)
                {
                    // We can only wait when there are no scheduled actions we must run.
                    mayWait = _scheduledActions.Count == 0;
                    if (mayWait)
                    {
                        Volatile.Write(ref _blockedState, StateBlocked);
                    }
                }
                return mayWait;
            }

            private unsafe void EventLoop()
            {
                try
                {
                    IOUringExecutionQueue iouring = _iouring!;
                    while (!_disposed)
                    {
                        iouring.SubmitAndWait((object s) => ((IOUringThread)s).IoUringMayWait(), this);
                        Volatile.Write(ref _blockedState, StateNotBlocked);

                        iouring.ExecuteCompletions();

                        ExecuteScheduledActions();
                    }

                    // Execute actions that got posted before we were disposed.
                    ExecuteScheduledActions();

                    // Complete pending async operations.
                    iouring.Dispose();

                    IOUringAsyncContext[] contexts;
                    lock (_asyncContexts)
                    {
                        contexts = new IOUringAsyncContext[_asyncContexts.Count];
                        _asyncContexts.Values.CopyTo(contexts, 0);
                        _asyncContexts.Clear();
                    }
                    foreach (var context in contexts)
                    {
                        context.Dispose();
                    }

                    FreeResources();
                }
                catch (Exception e)
                {
                    Environment.FailFast(e.ToString());
                }
            }

            internal AsyncContext CreateContext(SafeHandle handle)
            {
                lock (_asyncContexts)
                {
                    if (_disposed)
                    {
                        ThrowHelper.ThrowObjectDisposedException<IOUringThread>();
                    }

                    IOUringAsyncContext context = new IOUringAsyncContext(this, handle);

                    _asyncContexts.Add(context.Key, context);

                    return context;
                }
            }

            public void RemoveContext(int key)
            {
                lock (_asyncContexts)
                {
                    _asyncContexts.Remove(key);
                }
            }

            public unsafe void Post(Action<IOUringThread, IOUringAsyncContext?> action, IOUringAsyncContext? context)
            {
                // TODO: maybe special case when this is called from the IOUringThread itself.

                int blockingState;
                lock (_actionQueueGate)
                {
                    if (_disposed)
                    {
                        ThrowHelper.ThrowObjectDisposedException<IOUringThread>();
                    }

                    blockingState = Interlocked.CompareExchange(ref _blockedState, StateNotBlocked, StateBlocked);
                    _scheduledActions.Add(new ScheduledAction
                    {
                        AsyncContext = context,
                        Action = action
                    });
                }

                if (blockingState == StateBlocked)
                {
                    WriteToPipe();
                }
            }

            private unsafe void WriteToPipe()
            {
                Span<byte> buffer = stackalloc byte[8];
                int rv = IoPal.Write(_pipeWriteEnd!, buffer);
                if (rv == -1)
                {
                    if (errno != EAGAIN)
                    {
                        PlatformException.Throw();
                    }
                }
            }

            private unsafe void ExecuteScheduledActions()
            {
                List<ScheduledAction> actionQueue;
                lock (_actionQueueGate)
                {
                    actionQueue = _scheduledActions;
                    _scheduledActions = _executingActions;
                    _executingActions = actionQueue;
                }

                if (actionQueue.Count > 0)
                {
                    foreach (var scheduleAction in actionQueue)
                    {
                        scheduleAction.Action(this, scheduleAction.AsyncContext);
                    }
                    actionQueue.Clear();
                }
            }

            private bool HasScheduledActions
            {
                get
                {
                    lock (_actionQueueGate)
                    {
                        return _scheduledActions.Count > 0;
                    }
                }
            }

            internal void ExecuteQueuedReads(IOUringAsyncContext context)
            {
                context.ExecuteQueuedReads(_iouring, AsyncOperationResult.NoResult);
            }


            internal void ExecuteQueuedWrites(IOUringAsyncContext context)
            {
                context.ExecuteQueuedWrites(_iouring, AsyncOperationResult.NoResult);
            }

            public void Dispose()
            {
                lock (_asyncContexts)
                {
                    if (_disposed)
                    {
                        return;
                    }
                    _disposed = true;
                }

                WriteToPipe();

                _thread.Join();
            }

            private void AddReadFromPipe()
            {
                _iouring!.AddRead(_pipeReadEnd!, _dummyReadBuffer,
                (AsyncExecutionQueue queue, AsyncOperationResult asyncResult, object? state, int data) =>
                    {
                        // TODO: do we need to do a volatile read of _disposed?
                        if (asyncResult.Value == 8 || asyncResult.Errno == EAGAIN)
                        {
                            ((IOUringThread)state!).AddReadFromPipe();
                        }
                        else if (asyncResult.IsError)
                        {
                            PlatformException.Throw(asyncResult.Errno);
                        }
                        else
                        {
                            ((IOUringThread)state!).AddReadFromPipe();
                            // ThrowHelper.ThrowIndexOutOfRange(asyncResult.Value); // TODO...
                        }
                    }
                , this, 0);
            }

            private unsafe void CreateResources()
            {
                try
                {
                    _iouring = new IOUringExecutionQueue();

                    _pipeReadEnd = new CloseSafeHandle();
                    _pipeWriteEnd = new CloseSafeHandle();
                    int* pipeFds = stackalloc int[2];
                    int rv = pipe2(pipeFds, O_CLOEXEC | O_NONBLOCK);
                    if (rv == -1)
                    {
                        PlatformException.Throw();
                    }
                    _pipeReadEnd.SetHandle(pipeFds[0]);
                    _pipeWriteEnd.SetHandle(pipeFds[1]);

                    AddReadFromPipe();
                }
                catch
                {
                    FreeResources();

                    throw;
                }
            }

            private void FreeResources()
            {
                _iouring?.Dispose();
                _pipeReadEnd?.Dispose();
                _pipeWriteEnd?.Dispose();
            }
        }
    }

}
using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using static Tmds.Linux.LibC;

namespace Tmds.LinuxAsync
{
    public partial class IOUringAsyncEngine
    {
        sealed class IOUringThread : PipeScheduler, IDisposable
        {
            private const int PipeKey = -1;
            private const int StateBlocked = 1;
            private const int StateNotBlocked = 0;

            private readonly Dictionary<int, IOUringAsyncContext> _asyncContexts; // TODO: make this a HashSet?
            private readonly Thread _thread;
            private IOUringExecutionQueue? _iouring;
            private CloseSafeHandle? _eventFd;
            private readonly byte[] _dummyReadBuffer = new byte[8];
            private bool _disposed;
            private int _blockedState;

            internal AsyncExecutionQueue ExecutionQueue => _iouring!;

            public bool BatchOnIOThread { get; }

            public bool IsCurrentThread => object.ReferenceEquals(_thread, Thread.CurrentThread);

            struct ScheduledAction
            {
                public object? State;
                public Action<object?> Action;
            }

            private readonly object _actionQueueGate = new object();
            private List<ScheduledAction> _scheduledActions;
            private List<ScheduledAction> _executingActions;

            public IOUringThread(bool batchOnIOThread)
            {
                BatchOnIOThread = batchOnIOThread;
                _asyncContexts = new Dictionary<int, IOUringAsyncContext>();

                CreateResources();

                _scheduledActions = new List<ScheduledAction>(1024);
                _executingActions = new List<ScheduledAction>(1024);
                _blockedState = StateBlocked;

                _thread = new Thread(EventLoop);
                _thread.IsBackground = true;
                _thread.Start();
            }

            private unsafe void EventLoop()
            {
                try
                {
                    IOUringExecutionQueue iouring = _iouring!;
                    while (!_disposed)
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
                        iouring.SubmitAndWait(mayWait);
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

            public override void Schedule(Action<object?> action, object? state)
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
                        State = state,
                        Action = action
                    });
                }

                if (blockingState == StateBlocked)
                {
                    WriteToEventFd();
                }
            }

            private unsafe void WriteToEventFd()
            {
                Span<byte> buffer = stackalloc byte[8];
                buffer[7] = 1;
                int rv = IoPal.Write(_eventFd!, buffer);
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
                        scheduleAction.Action(scheduleAction.State);
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

                WriteToEventFd();

                _thread.Join();
            }

            private void AddReadFromEventFd()
            {
                _iouring!.AddRead(_eventFd!, _dummyReadBuffer,
                (AsyncOperationResult asyncResult, object? state, int data) =>
                    {
                        // TODO: do we need to do a volatile read of _disposed?
                        if (asyncResult.Errno == EAGAIN || (!asyncResult.IsError && asyncResult.Value == 8))
                        {
                            ((IOUringThread)state!).AddReadFromEventFd();
                        }
                        else if (asyncResult.IsError)
                        {
                            PlatformException.Throw(asyncResult.Errno);
                        }
                        else
                        {
                            ThrowHelper.ThrowIndexOutOfRange(asyncResult.Value);
                        }
                    }
                , this, 0);
            }

            private unsafe void CreateResources()
            {
                try
                {
                    _iouring = new IOUringExecutionQueue();

                    _eventFd = new CloseSafeHandle();
                    int rv = eventfd(0, EFD_CLOEXEC | EFD_NONBLOCK);
                    if (rv == -1)
                    {
                        PlatformException.Throw();
                    }
                    _eventFd.SetHandle(rv);

                    AddReadFromEventFd();
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
                _eventFd?.Dispose();
            }
        }
    }

}
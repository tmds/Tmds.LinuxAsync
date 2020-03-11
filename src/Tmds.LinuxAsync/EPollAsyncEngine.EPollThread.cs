using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using System.Threading;
using Tmds.Linux;
using static Tmds.Linux.LibC;

namespace Tmds.LinuxAsync
{
    public partial class EPollAsyncEngine
    {
        sealed class EPollThread : PipeScheduler, IDisposable
        {
            private const int EventBufferLength = 512;
            private const int PipeKey = -1;
            private const int StateBlocked = 1;
            private const int StateNotBlocked = 0;

            private readonly Dictionary<int, EPollAsyncContext> _asyncContexts;
            private readonly Thread _thread;
            private LinuxAio? _asyncExecutionQueue;
            private int _epollFd = -1;
            private CloseSafeHandle? _pipeReadEnd;
            private CloseSafeHandle? _pipeWriteEnd;
            private byte[]? _dummyReadBuffer;
            private bool _disposed;
            private int _blockedState;

            internal AsyncExecutionQueue? ExecutionQueue => _asyncExecutionQueue;
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

            public EPollThread(bool useLinuxAio, bool batchOnIOThread)
            {
                _asyncContexts = new Dictionary<int, EPollAsyncContext>();
                BatchOnIOThread = batchOnIOThread;

                CreateResources(useLinuxAio);

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
                    var eventBuffer = stackalloc epoll_event[EventBufferLength];
                    List<EPollAsyncContext?> asyncContextsForEvents = new List<EPollAsyncContext?>();

                    bool running = true;
                    int epollTimeout = -1;
                    while (running)
                    {
                        int rv = epoll_wait(_epollFd, eventBuffer, EventBufferLength, epollTimeout);
                        Volatile.Write(ref _blockedState, StateNotBlocked);

                        if (rv == -1)
                        {
                            if (LibC.errno == EINTR)
                            {
                                continue;
                            }

                            PlatformException.Throw();
                        }

                        lock (_asyncContexts)
                        {
                            for (int i = 0; i < rv; i++)
                            {
                                int key = eventBuffer[i].data.fd;
                                if (_asyncContexts.TryGetValue(key, out EPollAsyncContext? eventContext))
                                {
                                    asyncContextsForEvents.Add(eventContext);
                                }
                                else
                                {
                                    if (key == PipeKey)
                                    {
                                        running = !_disposed;
                                        ReadFromPipe(_asyncExecutionQueue);
                                    }
                                    asyncContextsForEvents.Add(null);
                                }
                            }
                        }

                        for (int i = 0; i < rv; i++)
                        {
                            EPollAsyncContext? context = asyncContextsForEvents[i];
                            if (context != null)
                            {
                                context.HandleEvents(eventBuffer[i].events);
                            }
                        }
                        asyncContextsForEvents.Clear();

                        bool actionsRemaining = false;

                        // Run this twice, executions cause more executions.
                        for (int i = 0; i < 2; i++)
                        {
                            // First execute scheduled actions, they can add to the exection queue.
                            ExecuteScheduledActions();
                            if (_asyncExecutionQueue != null)
                            {
                                actionsRemaining = _asyncExecutionQueue.ExecuteOperations();
                            }
                        }

                        if (!actionsRemaining)
                        {
                            // Check if there are scheduled actions remaining.
                            lock (_actionQueueGate)
                            {
                                actionsRemaining = _scheduledActions.Count > 0;
                                if (!actionsRemaining)
                                {
                                    Volatile.Write(ref _blockedState, StateBlocked);
                                }
                            }
                        }
                        epollTimeout = actionsRemaining ? 0 : -1;
                    }

                    // Execute actions that got posted before we were disposed.
                    ExecuteScheduledActions();

                    // Complete pending async operations.
                    _asyncExecutionQueue?.Dispose();

                    EPollAsyncContext[] contexts;
                    lock (_asyncContexts)
                    {
                        contexts = new EPollAsyncContext[_asyncContexts.Count];
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
                        ThrowHelper.ThrowObjectDisposedException<EPollThread>();
                    }

                    EPollAsyncContext context = new EPollAsyncContext(this, handle); // TODO: move this outside lock.

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
                // TODO: maybe special case when this is called from the EPollThread itself.

                int blockingState;
                lock (_actionQueueGate)
                {
                    if (_disposed)
                    {
                        ThrowHelper.ThrowObjectDisposedException<EPollThread>();
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
                    WriteToPipe();
                }
            }

            private unsafe void WriteToPipe()
            {
                Span<byte> buffer = stackalloc byte[1];
                int rv = IoPal.Write(_pipeWriteEnd!, buffer);
                if (rv == -1)
                {
                    if (errno != EAGAIN)
                    {
                        PlatformException.Throw();
                    }
                }
            }

            private unsafe void ReadFromPipe(AsyncExecutionQueue? executionEngine)
            {
                if (executionEngine == null)
                {
                    Span<byte> buffer = stackalloc byte[128];
                    int rv = IoPal.Read(_pipeReadEnd!, buffer);
                    if (rv == -1)
                    {
                        if (errno != EAGAIN)
                        {
                            PlatformException.Throw();
                        }
                    }
                }
                else
                {
                    if (_dummyReadBuffer == null)
                    {
                        _dummyReadBuffer = new byte[128];
                    }
                    executionEngine.AddRead(_pipeReadEnd!, _dummyReadBuffer,
                        (AsyncOperationResult result, object? state, int data) =>
                        {
                            if (result.IsError && result.Errno != EAGAIN)
                            {
                                PlatformException.Throw();
                            }
                        }, state: null, data: 0);
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

            public unsafe void Control(int op, int fd, int events, int key)
            {
                epoll_event ev = default;
                ev.events = events;
                ev.data.fd = key;
                int rv = epoll_ctl(_epollFd, op, fd, &ev);
                if (rv != 0)
                {
                    PlatformException.Throw();
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

                WriteToPipe();

                _thread.Join();
            }

            private unsafe void CreateResources(bool useLinuxAio)
            {
                try
                {
                    if (useLinuxAio)
                    {
                        _asyncExecutionQueue = new LinuxAio();
                    }

                    _epollFd = epoll_create1(EPOLL_CLOEXEC);
                    if (_epollFd == -1)
                    {
                        PlatformException.Throw();
                    }

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

                    Control(EPOLL_CTL_ADD, pipeFds[0], EPOLLIN, PipeKey);
                }
                catch
                {
                    FreeResources();

                    throw;
                }
            }

            private void FreeResources()
            {
                _asyncExecutionQueue?.Dispose();

                if (_epollFd != -1)
                {
                    close(_epollFd);
                }
                _pipeReadEnd?.Dispose();
                _pipeWriteEnd?.Dispose();
            }
        }
    }

}
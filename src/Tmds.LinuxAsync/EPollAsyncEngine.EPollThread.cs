using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Tmds.Linux;
using static Tmds.Linux.LibC;

namespace Tmds.LinuxAsync
{
    public partial class EPollAsyncEngine
    {
        sealed class EPollThread : IDisposable
        {
            private const int EventBufferLength = 512;
            private const int PipeKey = -1;
            private const int EPollBlocked = 1;
            private const int EPollNotBlocked = 0;
            private const byte PipeActionsPending = 1;
            private const byte PipeEAgain = 2;
            private const byte PipeDisposed = 2;

            private readonly Dictionary<int, EPollAsyncContext> _asyncContexts;
            private readonly Thread _thread;
            private int _epollFd = -1;
            private int _pipeWriteFd = -1;
            private int _pipeReadFd = -1;
            private bool _disposed;
            private int _epollState;

            struct ScheduledAction
            {
                public EPollAsyncContext? AsyncContext;
                public Action<EPollThread, EPollAsyncContext?> Action;
            }

            private readonly object _actionQueueGate = new object();
            private List<ScheduledAction> _scheduledActions;
            private List<ScheduledAction> _executingActions;

            public EPollThread()
            {
                _asyncContexts = new Dictionary<int, EPollAsyncContext>();

                CreateNativeResources();

                _scheduledActions = new List<ScheduledAction>(1024);
                _executingActions = new List<ScheduledAction>(1024);
                _epollState = EPollBlocked;

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

                        Volatile.Write(ref _epollState, EPollNotBlocked);

                        if (rv == -1)
                        {
                            if (LibC.errno == EINTR)
                            {
                                continue;
                            }

                            PlatformException.Throw();
                        }

                        bool actionsRemaining = ExecuteScheduledActions();

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
                                        byte b;
                                        do
                                        {
                                            b = ReadFromPipe();
                                            if (b == PipeDisposed)
                                            {
                                                running = false;
                                            }
                                        } while (b != PipeEAgain);
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

                        epollTimeout = actionsRemaining ? 0 : -1;
                    }

                    // Execute actions that got posted before we were disposed.
                    ExecuteScheduledActions();

                    lock (_asyncContexts)
                    {
                        var contexts = _asyncContexts;
                        _asyncContexts.Clear();

                        foreach (var context in contexts.Values)
                        {
                            context.Dispose();
                        }
                    }

                    FreeNativeResources();
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

                    EPollAsyncContext context = new EPollAsyncContext(this, handle);

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

            public unsafe void Post(Action<EPollThread, EPollAsyncContext?> action, EPollAsyncContext? context)
            {
                int epollState;
                lock (_actionQueueGate)
                {
                    if (_disposed)
                    {
                        ThrowHelper.ThrowObjectDisposedException<EPollThread>();
                    }

                    epollState = Interlocked.CompareExchange(ref _epollState, EPollNotBlocked, EPollBlocked);
                    _scheduledActions.Add(new ScheduledAction
                    {
                        AsyncContext = context,
                        Action = action
                    });

                    // TODO: this can be moved outside the lock when _pipeWriteFd is a SafeHandle that gets Disposed.
                    if (epollState == EPollBlocked)
                    {
                        WriteToPipe(PipeActionsPending);
                    }
                }
            }

            private unsafe void WriteToPipe(byte b)
            {
                int rv;
                do
                {
                    rv = (int)write(_pipeWriteFd, &b, 1);
                } while (rv == -1 && errno == EINTR);
                if (rv == -1)
                {
                    PlatformException.Throw();
                }
            }

            private unsafe byte ReadFromPipe()
            {
                byte b;
                int rv;
                do
                {
                    rv = (int)write(_pipeWriteFd, &b, 1);
                } while (rv == -1 && errno == EINTR);
                if (rv == -1)
                {
                    if (errno == EAGAIN)
                    {
                        return PipeEAgain;
                    }
                    else
                    {
                        PlatformException.Throw();
                    }
                }
                return b;
            }

            private unsafe bool ExecuteScheduledActions()
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

                bool actionsRemaining = false;
                lock (_actionQueueGate)
                {
                    if (_scheduledActions.Count > 0)
                    {
                        actionsRemaining = true;
                    }
                    else
                    {
                        Volatile.Write(ref _epollState, EPollBlocked);
                    }
                }
                return actionsRemaining;
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

                WriteToPipe(PipeDisposed);

                _thread.Join();
            }

            private unsafe void CreateNativeResources()
            {
                try
                {
                    _epollFd = epoll_create1(EPOLL_CLOEXEC);
                    if (_epollFd == -1)
                    {
                        PlatformException.Throw();
                    }

                    int* pipeFds = stackalloc int[2];
                    int rv = pipe2(pipeFds, O_CLOEXEC | O_NONBLOCK);
                    if (rv == -1)
                    {
                        PlatformException.Throw();
                    }
                    _pipeReadFd = pipeFds[0];
                    _pipeWriteFd = pipeFds[1];

                    Control(EPOLL_CTL_ADD, _pipeReadFd, EPOLLIN | EPOLLET, PipeKey);
                }
                catch
                {
                    FreeNativeResources();

                    throw;
                }
            }

            private void FreeNativeResources()
            {
                if (_epollFd != -1)
                {
                    close(_epollFd);
                }
                if (_pipeWriteFd != -1)
                {
                    close(_pipeWriteFd);
                }
                if (_pipeReadFd != -1)
                {
                    close(_pipeReadFd);
                }
            }
        }
    }

}
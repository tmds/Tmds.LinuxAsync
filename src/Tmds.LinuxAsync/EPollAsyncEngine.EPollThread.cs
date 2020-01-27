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

            private readonly Dictionary<int, EPollAsyncContext> _asyncContexts;
            private readonly Thread _thread;
            private int _epollFd = -1;
            private bool _disposed;

            public EPollThread()
            {
                _asyncContexts = new Dictionary<int, EPollAsyncContext>();

                CreateNativeResources();

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
                    while (running)
                    {
                        int rv = epoll_wait(_epollFd, eventBuffer, EventBufferLength, timeout: -1);
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
                                    if (key == PipeKey) // TODO
                                    {
                                        running = !_disposed;
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
                    }

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

            // EPollAsyncContext holds a lock so this isn't called from a disposed AsyncContext.
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

                    // TODO: make the Thread Stop.

                    _thread.Join();
                }
            }

            private void CreateNativeResources()
            {
                try
                {
                    _epollFd = epoll_create1(EPOLL_CLOEXEC);
                    if (_epollFd == -1)
                    {
                        PlatformException.Throw();
                    }
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
            }
        }
    }

}
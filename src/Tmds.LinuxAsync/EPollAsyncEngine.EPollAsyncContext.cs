using System;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using static Tmds.Linux.LibC;

namespace Tmds.LinuxAsync
{
    public partial class EPollAsyncEngine
    {
        sealed class EPollAsyncContext : AsyncContext
        {
            private readonly EPollThread _epoll;
            private SafeHandle? _handle;
            private int _fd;

            public int Key => _fd;

            public override PipeScheduler? IOThreadScheduler => _epoll;

            private Queue ReadQueue => (Queue)_readQueue!;
            private Queue WriteQueue => (Queue)_writeQueue!;

            public EPollAsyncContext(EPollThread thread, SafeHandle handle)
            {
                _epoll = thread;
                _writeQueue = new Queue(thread);
                _readQueue = new Queue(thread);
                bool success = false;
                handle.DangerousAddRef(ref success);
                _fd = handle.DangerousGetHandle().ToInt32();
                _handle = handle;

                SocketPal.SetNonBlocking(handle);
                _epoll.Control(EPOLL_CTL_ADD, _fd, EPOLLIN | EPOLLOUT | EPOLLET, Key);
            }

            public override void Dispose()
            {
                bool dispose = ReadQueue.Dispose();
                if (!dispose)
                {
                    // Already disposed.
                    return;
                }
                WriteQueue.Dispose();

                _epoll.RemoveContext(Key);

                if (_handle != null)
                {
                    _handle.DangerousRelease();
                    _fd = -1;
                    _handle = null;
                }
            }

            public void HandleEvents(int events)
            {
                if ((events & EPOLLERR) != 0)
                {
                    events |= POLLIN | POLLOUT;
                }
                if ((events & POLLIN) != 0)
                {
                    ReadQueue.ExecuteQueued(triggeredByPoll: true);
                }
                if ((events & POLLOUT) != 0)
                {
                    WriteQueue.ExecuteQueued(triggeredByPoll: true);
                }
            }
        }
    }
}
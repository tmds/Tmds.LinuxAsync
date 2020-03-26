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
            private readonly Queue _writeQueue;
            private readonly Queue _readQueue;
            private SafeHandle? _handle;
            private int _fd;
            private bool _setToNonBlocking;

            public int Key => _fd;

            public override PipeScheduler? IOThreadScheduler => _epoll;

            public EPollAsyncContext(EPollThread thread, SafeHandle handle)
            {
                _epoll = thread;
                _writeQueue = new Queue(thread);
                _readQueue = new Queue(thread);
                bool success = false;
                handle.DangerousAddRef(ref success);
                _fd = handle.DangerousGetHandle().ToInt32();
                _handle = handle;

                _epoll.Control(EPOLL_CTL_ADD, _fd, EPOLLIN | EPOLLOUT | EPOLLET, Key);
            }

            public override void Dispose()
            {
                bool dispose = _readQueue.Dispose();
                if (!dispose)
                {
                    // Already disposed.
                    return;
                }
                _writeQueue.Dispose();

                _epoll.RemoveContext(Key);

                if (_handle != null)
                {
                    _handle.DangerousRelease();
                    _fd = -1;
                    _handle = null;
                }
            }

            public override bool ExecuteAsync(AsyncOperation operation, bool preferSync)
            {
                EnsureNonBlocking();

                try
                {
                    operation.CurrentAsyncContext = this;

                    if (operation.IsReadNotWrite)
                    {
                        return _readQueue.ExecuteAsync(operation, preferSync);
                    }
                    else
                    {
                        return _writeQueue.ExecuteAsync(operation, preferSync);
                    }
                }
                catch
                {
                    operation.Status = OperationStatus.CancelledSync;
                    operation.Complete();

                    throw;
                }
            }

            private void EnsureNonBlocking()
            {
                SafeHandle? handle = _handle;
                if (handle == null)
                {
                    // We've been disposed.
                    return;
                }

                if (!_setToNonBlocking)
                {
                    SocketPal.SetNonBlocking(handle);
                    _setToNonBlocking = true;
                }
            }

            internal override void TryCancelAndComplete(AsyncOperation operation, OperationStatus status)
            {
                if (operation.IsReadNotWrite)
                {
                    _readQueue.TryCancelAndComplete(operation, status);
                }
                else
                {
                    _writeQueue.TryCancelAndComplete(operation, status);
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
                    _readQueue.ExecuteQueued(triggeredByPoll: true);
                }
                if ((events & POLLOUT) != 0)
                {
                    _writeQueue.ExecuteQueued(triggeredByPoll: true);
                }
            }
        }
    }
}
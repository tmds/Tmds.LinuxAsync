using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using System.Threading;
using static Tmds.Linux.LibC;

namespace Tmds.LinuxAsync
{
    public partial class IOUringAsyncEngine
    {
        sealed class IOUringAsyncContext : AsyncContext
        {
            private readonly IOUringThread _iouring;
            private readonly Queue _writeQueue;
            private readonly Queue _readQueue;
            private SafeHandle? _handle;
            private int _fd;
            private bool _setToNonBlocking;

            public int Key => _fd;

            public SafeHandle Handle => _handle!;

            public override PipeScheduler? IOThreadScheduler => _iouring;

            public IOUringAsyncContext(IOUringThread thread, SafeHandle handle)
            {
                _iouring = thread;
                _writeQueue = new Queue(thread, this);
                _readQueue = new Queue(thread, this);
                bool success = false;
                handle.DangerousAddRef(ref success);
                _fd = handle.DangerousGetHandle().ToInt32();
                _handle = handle;
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
                _iouring.RemoveContext(Key);

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
        }
    }
}
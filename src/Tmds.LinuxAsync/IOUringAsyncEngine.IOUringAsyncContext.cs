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
            private SafeHandle? _handle;
            private int _fd;

            public int Key => _fd;

            public SafeHandle Handle => _handle!;

            public override PipeScheduler? IOThreadScheduler => _iouring;

            private Queue ReadQueue => (Queue)_readQueue!;
            private Queue WriteQueue => (Queue)_writeQueue!;

            public IOUringAsyncContext(IOUringThread thread, SafeHandle handle)
            {
                _iouring = thread;
                _writeQueue = new Queue(thread, this, readNotWrite: false);
                _readQueue = new Queue(thread, this, readNotWrite: true);
                bool success = false;
                handle.DangerousAddRef(ref success);
                _fd = handle.DangerousGetHandle().ToInt32();
                _handle = handle;
                SocketPal.SetNonBlocking(handle);
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
                _iouring.RemoveContext(Key);

                if (_handle != null)
                {
                    _handle.DangerousRelease();
                    _fd = -1;
                    _handle = null;
                }
            }
        }
    }
}
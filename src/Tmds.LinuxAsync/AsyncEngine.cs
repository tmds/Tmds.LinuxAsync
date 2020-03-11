using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Tmds.LinuxAsync
{
    // Runs async operations, factory of AsyncContexts.
    // Derived classes use specific OS APIs.
    public abstract class AsyncEngine : IDisposable
    {
        private static AsyncEngine? _socketEngine;

        // AsyncEngine used by Socket.
        public static AsyncEngine SocketEngine
        {
            get
            {
                if (_socketEngine == null &&
                    Volatile.Read(ref _socketEngine) == null)
                {
                    // Provide a default.
                    var engine = new EPollAsyncEngine(threadCount: Environment.ProcessorCount, useLinuxAio: true, batchOnIOThread: true);
                    if (Interlocked.CompareExchange(ref _socketEngine, engine, null) != null)
                    {
                        engine.Dispose();
                    }
                }
                return _socketEngine!;
            }
            set
            {
                // This may be set once.
                if (Interlocked.CompareExchange(ref _socketEngine, value, null) != null)
                {
                    ThrowHelper.ThrowInvalidOperationException();
                }
            }
        }

        // Dispose the engine and all associated AsyncContexts.
        public abstract void Dispose();

        // Create an AsyncContext.
        internal abstract AsyncContext CreateContext(SafeHandle handle);
    }
}

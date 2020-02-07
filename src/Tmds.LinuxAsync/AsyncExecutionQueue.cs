using System;
using System.Runtime.InteropServices;

namespace Tmds.LinuxAsync
{
    struct AsyncOperationResult
    {
        public int Result;
    }

    delegate void AsyncExecutionCallback(AsyncExecutionQueue queue, AsyncOperationResult result, object? state, int data);

    // Supports batching operations for execution.
    abstract class AsyncExecutionQueue : IDisposable
    {
        // Add a read.
        public abstract void AddRead(SafeHandle handle, Memory<byte> memory, AsyncExecutionCallback callback, object? state, int data);
        // Add a write.
        public abstract void AddWrite(SafeHandle handle, Memory<byte> memory, AsyncExecutionCallback callback, object? state, int data);

        abstract protected void Dispose(bool disposing);

        ~AsyncExecutionQueue()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
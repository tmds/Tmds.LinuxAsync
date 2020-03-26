using System;
using System.Threading;
using System.IO.Pipelines;

namespace Tmds.LinuxAsync
{
    // Manages executions of async operations for a handle.
    // Derived classes work with a specific SocketEngine.
    abstract class AsyncContext : IDisposable
    {
        // Cancels async operations and disposes the context.
        public abstract void Dispose();

        // Execute an async operation.
        public abstract bool ExecuteAsync(AsyncOperation operation, bool preferSync = true);

        // Cancel AsyncOperation.
        internal abstract void TryCancelAndComplete(AsyncOperation asyncOperation, OperationStatus status);

        // operation caching
        private AsyncOperation? _cachedReadOperation;
        private AsyncOperation? _cachedWriteOperation;

        public T RentReadOperation<T>() where T : AsyncOperation, new()
            => (T?)Interlocked.Exchange(ref _cachedReadOperation, null) ?? new T();

        public T RentWriteOperation<T>() where T : AsyncOperation, new()
            => (T?)Interlocked.Exchange(ref _cachedWriteOperation, null) ?? new T();

        public void ReturnReadOperation(AsyncOperation state)
            => Volatile.Write(ref _cachedReadOperation, state);

        public void ReturnWriteOperation(AsyncOperation state)
            => Volatile.Write(ref _cachedWriteOperation, state);

        public abstract PipeScheduler? IOThreadScheduler { get; }
    }
}

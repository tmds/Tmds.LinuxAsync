using System;
using System.Threading;
using System.IO.Pipelines;
using System.Diagnostics;

namespace Tmds.LinuxAsync
{
    // Manages executions of async operations for a handle.
    // Derived classes work with a specific SocketEngine.
    abstract class AsyncContext : IDisposable
    {
        // Cancels async operations and disposes the context.
        public abstract void Dispose();

        public bool ExecuteReadAsync(AsyncOperation operation, bool preferSync = true)
        {
            return ExecuteAsync(operation, preferSync, _readQueue!);
        }

        public bool ExecuteWriteAsync(AsyncOperation operation, bool preferSync = true)
        {
            return ExecuteAsync(operation, preferSync, _writeQueue!);
        }

        private static bool ExecuteAsync(AsyncOperation operation, bool preferSync, AsyncOperationQueueBase queue)
        {
            try
            {
                operation.CurrentQueue = queue;

                return queue.ExecuteAsync(operation, preferSync);
            }
            catch
            {
                operation.Status = OperationStatus.CancelledSync;
                operation.Complete();

                throw;
            }
        }

        protected AsyncOperationQueueBase? _readQueue;
        protected AsyncOperationQueueBase? _writeQueue;

        public T RentReadOperation<T>() where T : AsyncOperation, new()
            => _readQueue!.RentOperation<T>();

        public T RentWriteOperation<T>() where T : AsyncOperation, new()
            => _writeQueue!.RentOperation<T>();

        public abstract PipeScheduler? IOThreadScheduler { get; }
    }
}

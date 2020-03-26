using System;
using System.Diagnostics;
using System.Threading;

namespace Tmds.LinuxAsync
{
    enum AsyncExecutionResult
    {
        Finished,
        WaitForPoll,
        Executing,
        Cancelled
    }

    // Represents operation that is executed on AsyncContext.
    // Derived classes:
    // * Provide an implement for trying to execute the operation without blocking.
    // * Handle signalling completion to the user.
    abstract class AsyncOperation
    {
        protected AsyncOperation()
        {
            Next = this;
        }

        // AsyncContext on whith the operation is performed.
        // This value gets set by AsyncContext, and cleared by the AsyncOperation.
        public AsyncContext? CurrentAsyncContext { get; set; }

        // Should this operation be polled for input, or output by the AsyncEngine.
        public abstract bool IsReadNotWrite { get; }

        // Can be used to create a queue of AsyncOperations.
        public AsyncOperation? Next;

        private int _status;
        // Holds requested completion flags for cancellation, and final completion flags.
        public OperationStatus Status { get => (OperationStatus)_status; set => _status = (int)value; }

        // Was cancellation requested while the operation is executing.
        public bool IsCancellationRequested => (Status & OperationStatus.CancellationRequested) != 0;

        public bool VolatileReadIsCancellationRequested()
            => ((OperationStatus)Volatile.Read(ref _status) & OperationStatus.CancellationRequested) != 0;

        public OperationStatus CompareExchangeStatus(OperationStatus status, OperationStatus comparand)
        {
            return (OperationStatus)Interlocked.CompareExchange(ref _status, (int)status, (int)comparand);
        }

        // Completes the AsyncOperation.
        public abstract void Complete();

        // Try to execute the operation. Returns true when done, false it should be tried again.
        public abstract bool TryExecuteSync();

        // Asynchronously executes the operation on the io-thread.
        // The AsyncExecutionQueue when provided may be used to batch operations.
        // When the operation can make use of the executionQueue, AsyncExecutionResult.Executing is returned.
        // When the operation cannot make use of the queue, the operation is attempted synchronously
        // and WaitForPoll/Finished is returned.
        public abstract AsyncExecutionResult TryExecuteAsync(bool triggeredByPoll, AsyncExecutionQueue? executionQueue, AsyncExecutionCallback? callback, object? state, int data);

        // Handles the result from the ExecutionQueue,
        // Returns Executing if the operation should be tried immediately,
        // WaitForPoll if the operation should be tried when the handle is ready.
        // Finished/Cancelled when the operation is finished.
        public abstract AsyncExecutionResult HandleAsyncResult(AsyncOperationResult result);

        // Requests operation to be cancelled.
        public void TryCancelAndComplete(OperationStatus status = OperationStatus.None)
        {
            AsyncContext? context = CurrentAsyncContext;
            // When context is null, the operation completed already.
            if (context != null)
            {
                context.TryCancelAndComplete(this, status);
            }
        }

        protected void ReturnThis()
        {
            AsyncContext asyncContext = CurrentAsyncContext!;
            CurrentAsyncContext = null;

            // We don't re-use operations that were cancelled async,
            // because cancellation is detected via StatusFlags.
            if ((Status & OperationStatus.CancelledSync) != OperationStatus.Cancelled)
            {
                Status = OperationStatus.None;

                if (IsReadNotWrite)
                {
                    asyncContext.ReturnReadOperation(this);
                }
                else
                {
                    asyncContext.ReturnWriteOperation(this);
                }
            }
        }
    }
}
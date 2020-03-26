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

        public abstract AsyncExecutionResult TryExecuteAsync(bool triggeredByPoll, AsyncExecutionQueue? executionQueue, AsyncExecutionCallback? callback, object? state, int data);

        public abstract AsyncExecutionResult HandleAsyncResultAndContinue(AsyncOperationResult result, AsyncExecutionQueue executionQueue, AsyncExecutionCallback? callback, object? state, int data);

        // Continues execution of this operation.
        // When the operation is finished, AsyncExecutionResult.Finished is returned.
        // The executionQueue, when not null, can be used to batch operations.
        //   The callback, state, and data arguments must be passed on to the executionQueue.
        // When the executionQueue is used, AsyncExecutionResult.Executing is returned.
        // When the batched operations completes, the method is called again and 'result' has a value.
        // The execution queue may or may not support poll operations (ExecutionQueue.SupportsPolling).
        // In case there is no execution queue, or the queue does not support polling, the method
        // can return WaitForPoll. The method will be called again when poll indicates the handle is ready,
        // (and triggeredByPoll is true).
        // When asyncOnly is set, the execution queue must be used. If it cannot be used, WaitForPoll
        // must be returned.
        // When cancellationRequested is set, the operation must finish with
        //   AsyncExecutionResult.Finished when the operation completed using 'result'; and
        //   AsyncOperationResult.Cancelled otherwise.
        // public abstract AsyncExecutionResult TryExecute(bool triggeredByPoll, bool cancellationRequested, bool asyncOnly, AsyncExecutionQueue? executionQueue, AsyncExecutionCallback? callback, object? state, int data, AsyncOperationResult result);

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
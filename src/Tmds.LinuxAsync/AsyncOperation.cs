using System;
using System.Diagnostics;

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
        sealed class AsyncOperationSentinel : AsyncOperation
        {
            public override bool IsReadNotWrite
                => throw new System.InvalidOperationException();
            public override void Complete()
                => throw new System.InvalidOperationException();
            public override AsyncExecutionResult TryExecute(bool triggeredByPoll, bool cancellationRequested, bool asyncOnly, AsyncExecutionQueue? executionQueue, AsyncExecutionCallback? callback, object? state, int data, AsyncOperationResult result)
                => throw new System.InvalidOperationException();
        }

        public static readonly AsyncOperation DisposedSentinel = new AsyncOperationSentinel();

        // AsyncContext on whith the operation is performed.
        // This value gets set by AsyncContext, and cleared by the AsyncOperation.
        public AsyncContext? CurrentAsyncContext { get; set; }

        // Should this operation be polled for input, or output by the AsyncEngine.
        public abstract bool IsReadNotWrite { get; }

        // Can be used to create a queue of AsyncOperations.
        public AsyncOperation? Next;



        // Track state of the AsyncOperation while it is executing to support cancellation.
        // Thread safety is the caller's responsibility.

        // Is the operation being executed.
        public bool IsExecuting { get; set; }

        // Was cancellation requested while the operation is executing.
        public bool IsCancellationRequested => (CompletionFlags & OperationCompletionFlags.OperationCancelled) != 0;

        // Holds requested completion flags for cancellation, and final completion flags.
        public OperationCompletionFlags CompletionFlags { get; set; }

        // Requests the operation to be marked as cancelled.
        // Returns CancellationRequestResult.Cancelled when the operation was cancelled synchronously.
        // Returns CancellationRequestResult.Requested when the operation is marked for async cancellation.
        public CancellationRequestResult RequestCancellationAsync(OperationCompletionFlags flags)
        {
            Debug.Assert((CompletionFlags & (OperationCompletionFlags.OperationFinished | OperationCompletionFlags.OperationCancelled)) == 0);

            CompletionFlags = OperationCompletionFlags.CompletedCanceled | flags;
            return IsExecuting ? CancellationRequestResult.Requested : CancellationRequestResult.Cancelled;
        }
        // Completes the AsyncOperation.
        public abstract void Complete();

        // Try to execute the operation. Returns true when done, false it should be tried again.
        public bool TryExecuteSync()
            => TryExecute(triggeredByPoll: false, cancellationRequested: false, asyncOnly: false, executionQueue: null, callback: null, state: null, data: 0, AsyncOperationResult.NoResult) == AsyncExecutionResult.Finished;

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
        public abstract AsyncExecutionResult TryExecute(bool triggeredByPoll, bool cancellationRequested, bool asyncOnly, AsyncExecutionQueue? executionQueue, AsyncExecutionCallback? callback, object? state, int data, AsyncOperationResult result);

        // Requests operation to be cancelled.
        public void TryCancelAndComplete(OperationCompletionFlags completionFlags = OperationCompletionFlags.None)
        {
            AsyncContext? context = CurrentAsyncContext;
            // When context is null, the operation completed already.
            if (context != null)
            {
                context.TryCancelAndComplete(this, completionFlags);
            }
        }
    }
}
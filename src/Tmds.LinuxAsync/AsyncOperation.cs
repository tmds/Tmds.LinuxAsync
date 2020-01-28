namespace Tmds.LinuxAsync
{
    // Represents operation that is executed on AsyncContext.
    // Derived classes:
    // * Provide an implement for trying to execute the operation without blocking.
    // * Handle signalling completion to the user.
    abstract class AsyncOperation
    {
        // AsyncContext on whith the operation is performed.
        // This value gets set by AsyncContext, and cleared by the AsyncOperation.
        public AsyncContext? CurrentAsyncContext { get; set; }

        // Should this operation be polled for input, or output by the AsyncEngine.
        public abstract bool IsReadNotWrite { get; }

        // Can be used to create a queue of AsyncOperations.
        public AsyncOperation? Next { get; set; }

        // Completes the AsyncOperation.
        public abstract void Complete(OperationCompletionFlags completionStatus);

        // Try to execute the operation. Returns true when done, false it should be tried again.
        public abstract bool TryExecute(bool isSync);

        // Requests operation to be cancelled.
        public bool TryCancelAndComplete(OperationCompletionFlags completionFlags = OperationCompletionFlags.None)
        {
            AsyncContext? context = CurrentAsyncContext;
            if (context != null)
            {
                return context.TryCancelAndComplete(this, completionFlags);
            }
            else
            {
                return false;
            }
        }
    }
}
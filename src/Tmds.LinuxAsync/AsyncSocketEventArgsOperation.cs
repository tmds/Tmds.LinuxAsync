using System.Diagnostics;
using System.Threading;

namespace Tmds.LinuxAsync
{
    // AsyncOperation for executing SocketAsyncEventArgs operation.
    sealed class AsyncSocketEventArgsOperation : AsyncSocketOperation, IThreadPoolWorkItem
    {
        public AsyncSocketEventArgsOperation(SocketAsyncEventArgs saea) :
            base(saea)
        { }

        public override void Complete()
        {
            Debug.Assert((CompletionFlags & (OperationCompletionFlags.OperationCancelled | OperationCompletionFlags.OperationFinished)) != 0);

            bool runContinuationsAsync = Saea.RunContinuationsAsynchronously;

            ResetOperationState();

            bool completeSync = (CompletionFlags & OperationCompletionFlags.CompletedSync) != 0;
            if (completeSync || !runContinuationsAsync)
            {
                ((IThreadPoolWorkItem)this).Execute();
            }
            else
            {
                ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
            }
        }

        void IThreadPoolWorkItem.Execute()
        {
            // Capture state.
            OperationCompletionFlags completionStatus = CompletionFlags;

            // Reset state.
            CompletionFlags = OperationCompletionFlags.None;
            CurrentAsyncContext = null;

            // Complete.
            Saea.Complete(completionStatus);
        }
    }
}
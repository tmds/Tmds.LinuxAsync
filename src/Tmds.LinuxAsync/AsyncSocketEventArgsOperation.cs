using System.Threading;

namespace Tmds.LinuxAsync
{
    // AsyncOperation for executing SocketAsyncEventArgs operation.
    sealed class AsyncSocketEventArgsOperation : AsyncSocketOperation, IThreadPoolWorkItem
    {
        OperationCompletionFlags _competionStatus;

        public AsyncSocketEventArgsOperation(SocketAsyncEventArgs saea) :
            base(saea)
        { }

        public override void Complete(OperationCompletionFlags competionStatus)
        {
            ResetOperationState();

            _competionStatus = competionStatus;

            bool completeSync = (competionStatus & OperationCompletionFlags.CompletedSync) != 0;
            if (completeSync)
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
            OperationCompletionFlags completionStatus = _competionStatus;

            // Reset state.
            _competionStatus = OperationCompletionFlags.None;
            CurrentAsyncContext = null;

            // Complete.
            Saea.Complete(completionStatus);
        }
    }
}
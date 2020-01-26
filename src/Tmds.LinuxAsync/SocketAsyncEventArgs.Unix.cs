using System.Net.Sockets;
using System.Threading;

namespace Tmds.LinuxAsync
{
    public partial class SocketAsyncEventArgs
    {
        // Used for executing this operation on an AsyncContext.
        internal AsyncSocketOperation AsyncOperation { get; }

        public SocketAsyncEventArgs(bool unsafeSuppressExecutionContextFlow = false) :
            this(false, null)
        { }

        internal SocketAsyncEventArgs(bool unsafeSuppressExecutionContextFlow, AsyncSocketOperation? asyncOperation)
        {
            _flowExecutionContext = !unsafeSuppressExecutionContextFlow;
            AsyncOperation = asyncOperation ?? new AsyncSocketEventArgsOperation(this);
        }

        internal void Complete(OperationCompletionFlags completionFlags)
        {
            bool cancelled = (completionFlags & OperationCompletionFlags.OperationCancelled) != 0;
            if (cancelled)
            {
                SocketError = SocketError.OperationAborted;
            }

            // Reset state.
            ExecutionContext? context = _context;
            _context = null;
            CurrentSocket = null;
            CurrentOperation = System.Net.Sockets.SocketAsyncOperation.None;

            // Call OnCompleted only when completed async.
            bool completedSync = (completionFlags & OperationCompletionFlags.CompletedSync) != 0;
            if (completedSync)
            {
                if (context == null)
                {
                    OnCompleted(this);
                }
                else
                {
                    ExecutionContext.Run(context, s_executionCallback, this);
                }
            }
        }
    }
}
using System.Net.Sockets;
using System.Threading;

namespace Tmds.LinuxAsync
{
    public partial class SocketAsyncEventArgs
    {
        private AsyncOperation? _operation; // for caching/cancelling.

        internal AsyncOperation StartReceiveOperation(Socket socket)
        {
            StartOperationCommon(socket, SocketAsyncOperation.Receive);
            var op = (_operation as SaeaReceiveOperation) ?? new SaeaReceiveOperation(this);
            op.Configure(socket, MemoryBuffer, BufferList);
            _operation = op;
            return op;
        }

        internal AsyncOperation StartSendOperation(Socket socket)
        {
            StartOperationCommon(socket, SocketAsyncOperation.Send);
            var op = (_operation as SaeaSendOperation) ?? new SaeaSendOperation(this);
            op.Configure(socket, MemoryBuffer, BufferList);
            _operation = op;
            return op;
        }

        internal AsyncOperation StartAcceptOperation(Socket socket)
        {
            StartOperationCommon(socket, SocketAsyncOperation.Accept);
            var op = (_operation as SaeaAcceptOperation) ?? new SaeaAcceptOperation(this);
            op.Configure(socket);
            _operation = op;
            return op;
        }

        internal AsyncOperation StartConnectOperation(Socket socket)
        {
            StartOperationCommon(socket, SocketAsyncOperation.Connect);
            var op = (_operation as SaeaConnectOperation) ?? new SaeaConnectOperation(this);
            op.Configure(socket, RemoteEndPoint!);
            _operation = op;
            return op;
        }

        public bool RunContinuationsAsynchronously { get; set; } = true;
        public bool PreferSynchronousCompletion { get; set; } = true;

        public SocketAsyncEventArgs(bool unsafeSuppressExecutionContextFlow = false)
        {
            _flowExecutionContext = !unsafeSuppressExecutionContextFlow;
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
            bool completedAsync = (completionFlags & OperationCompletionFlags.CompletedSync) == 0;
            if (completedAsync)
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
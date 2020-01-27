using System;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks.Sources;

namespace Tmds.LinuxAsync
{
    // AsyncOperation for executing an awaitable socket operation.
    sealed class AwaitableSocketOperation : AsyncSocketOperation, IValueTaskSource<int>, IValueTaskSource<Socket>
    {
        private ManualResetValueTaskSourceCore<int> _vts;
        private OperationCompletionFlags _completionFlags;
        private bool _readNotWrite;
        private CancellationTokenRegistration _ctr;

        public AwaitableSocketOperation() :
            base()
        {
            Saea = new SocketAsyncEventArgs(unsafeSuppressExecutionContextFlow: true, this);
            // Use ThreadPool when there is no other ExecutionContext.
            _vts.RunContinuationsAsynchronously = true;
        }

        public void RegisterCancellation(CancellationToken cancellationToken)
        {
            _ctr = cancellationToken.UnsafeRegister(s =>
            {
                AwaitableSocketOperation operation = (AwaitableSocketOperation)s!;
                operation.TryCancelAndComplete(OperationCompletionFlags.CancelledByToken);
            }, this);
        }

        public int GetResult(short token)
        {
            // Complete _vts 
            _vts.GetResult(token);

            // Capture values.
            int bytesTransferred = Saea.BytesTransferred;
            SocketError socketError = Saea.SocketError;
            bool cancelledByToken = (_completionFlags & OperationCompletionFlags.CancelledByToken) != 0;

            // Reset this object and allow it to be reused.
            ResetAndReturnThis();

            ThrowForSocketError(socketError, cancelledByToken);
            return bytesTransferred;
        }

        private static void ThrowForSocketError(SocketError socketError, bool cancelledByToken)
        {
            if (socketError != System.Net.Sockets.SocketError.Success)
            {
                if (cancelledByToken)
                {
                    throw new OperationCanceledException();
                }
                throw new SocketException((int)socketError);
            }
        }

        Socket IValueTaskSource<Socket>.GetResult(short token)
        {
            // Complete _vts 
            _vts.GetResult(token);

            // Capture values.
            Socket? socket = Saea.AcceptSocket;
            Saea.AcceptSocket = null; // Don't hold a reference.
            SocketError socketError = Saea.SocketError;
            bool cancelledByToken = (_completionFlags & OperationCompletionFlags.CancelledByToken) != 0;

            // Reset this object and allow it to be reused.
            ResetAndReturnThis();

            ThrowForSocketError(socketError, cancelledByToken);
            return socket!;
        }

        public ValueTaskSourceStatus GetStatus(short token)
            => _vts.GetStatus(token);

        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            => _vts.OnCompleted(continuation, state, token, flags);

        public override void Complete(OperationCompletionFlags completionFlags)
        {
            ResetOperationState();
            _readNotWrite = IsReadNotWrite;
            Saea.Complete(completionFlags);

            _vts.SetResult(0);
        }

        private void ResetAndReturnThis()
        {
            AsyncContext asyncContext = CurrentAsyncContext!;
            CurrentAsyncContext = null;
            _completionFlags = OperationCompletionFlags.None;
            _vts.Reset();

            if (_readNotWrite)
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
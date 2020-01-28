using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks.Sources;

namespace Tmds.LinuxAsync
{
    // AsyncOperation for executing an awaitable socket operation.
    sealed class AwaitableSocketOperation : AsyncSocketOperation, IValueTaskSource<int>, IValueTaskSource<Socket>, IValueTaskSource
    {
        private static ManualResetEventSlim s_completedSentinel = new ManualResetEventSlim();

        private ManualResetValueTaskSourceCore<int> _vts;

        private CancellationTokenRegistration _ctr;
        private ManualResetEventSlim? _mre;

        private OperationCompletionFlags _completionFlags;
        private bool _readNotWrite;

        public short Version => _vts.Version;

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

        public void GetResult(short token)
        {
            // Capture values.
            SocketError socketError = Saea.SocketError;
            OperationCompletionFlags completionFlags = _completionFlags;

            // Reset this object and allow it to be reused.
            ResetAndReturnThis();

            ThrowForSocketError(socketError, completionFlags);
        }

        int IValueTaskSource<int>.GetResult(short token)
        {
            // Capture values.
            int bytesTransferred = Saea.BytesTransferred;
            SocketError socketError = Saea.SocketError;
            OperationCompletionFlags completionFlags = _completionFlags;

            // Reset this object and allow it to be reused.
            ResetAndReturnThis();

            ThrowForSocketError(socketError, completionFlags);
            return bytesTransferred;
        }

        Socket IValueTaskSource<Socket>.GetResult(short token)
        {
            // Capture values.
            Socket? socket = Saea.AcceptSocket;
            Saea.AcceptSocket = null; // Don't hold a reference.
            SocketError socketError = Saea.SocketError;
            OperationCompletionFlags completionFlags = _completionFlags;

            // Reset this object and allow it to be reused.
            ResetAndReturnThis();

            ThrowForSocketError(socketError, completionFlags);
            return socket!;
        }

        private static void ThrowForSocketError(SocketError socketError, OperationCompletionFlags completionFlags)
        {
            if (socketError != System.Net.Sockets.SocketError.Success)
            {
                bool cancelledByToken = (completionFlags & OperationCompletionFlags.CancelledByToken) != 0;
                if (cancelledByToken)
                {
                    throw new OperationCanceledException();
                }
                bool cancelledByTimeout = (completionFlags & OperationCompletionFlags.CancelledByTimeout) != 0;
                if (cancelledByTimeout)
                {
                    socketError = SocketError.TimedOut;
                }
                throw new SocketException((int)socketError);
            }
        }

        public ValueTaskSourceStatus GetStatus(short token)
            => _vts.GetStatus(token);

        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            => _vts.OnCompleted(continuation, state, token, flags);

        public override void Complete(OperationCompletionFlags completionFlags)
        {
            ResetOperationState();
            _readNotWrite = IsReadNotWrite;
            _completionFlags = completionFlags;
            Saea.Complete(completionFlags);

            _vts.SetResult(0);

            ManualResetEventSlim? mre = Interlocked.Exchange(ref _mre, s_completedSentinel);
            // This ManualResetEventSlim is used to wait until the operation completed.
            // After that a direct call is made to get the result.
            mre?.Set();
        }

        private void ResetAndReturnThis()
        {
            // Capture context for return.
            AsyncContext asyncContext = CurrentAsyncContext!;

            // Reset
            _vts.Reset();
            _ctr.Dispose();
            _mre = null;
            CurrentAsyncContext = null;
            _completionFlags = OperationCompletionFlags.None;

            // Return
            if (_readNotWrite)
            {
                asyncContext.ReturnReadOperation(this);
            }
            else
            {
                asyncContext.ReturnWriteOperation(this);
            }
        }

        // After calling this method, the ManualResetEvent must be used to Wait
        // until the operation has completed.
        // Then a call can be made to get the result.
        public void SetCompletedEvent(ManualResetEventSlim mre)
        {
            if (Interlocked.CompareExchange(ref _mre, mre, null) != null)
            {
                // Already completed.
                _mre.Set();
            }
        }
    }
}
using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks.Sources;
using static Tmds.Linux.LibC;

namespace Tmds.LinuxAsync
{
    sealed class AwaitableSocketAcceptOperation : AsyncAcceptOperation, IValueTaskSource<Socket>
    {
        private AwaitableSocketOperationCore<Socket?> _core;

        public short Version => _core.Version;

        public AwaitableSocketAcceptOperation()
            => _core.Init();

        public void RegisterCancellation(CancellationToken cancellationToken)
            => _core.RegisterCancellation(cancellationToken);

        public Socket GetResult(short token)
        {
            var result = _core.GetResult(token);
            ResetAndReturnThis();
            return result.GetValue()!;
        }

        public ValueTaskSourceStatus GetStatus(short token)
            => _core.GetStatus(token);

        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            => _core.OnCompleted(continuation, state, token, flags);

        public override void Complete()
        {
            SocketError socketError = SocketError;
            Socket? acceptedSocket = AcceptedSocket;
            OperationStatus status = Status;
            ResetOperationState();

            if ((status & OperationStatus.CancelledSync) == OperationStatus.CancelledSync)
            {
                // Caller threw an exception which prevents further use of this.
                ResetAndReturnThis();
            }
            else
            {
                _core.SetResult(acceptedSocket, socketError, status);
            }
        }

        private void ResetAndReturnThis()
        {
            _core.Reset();
            ReturnThis();
        }
    }

    sealed class SaeaAcceptOperation : AsyncAcceptOperation, IThreadPoolWorkItem
    {
        public readonly SocketAsyncEventArgs _saea;
        public SaeaAcceptOperation(SocketAsyncEventArgs saea)
        {
            _saea = saea;
        }

        public override void Complete()
        {
            SetSaeaResult();
            ResetOperationState();

            bool runContinuationsAsync = _saea.RunContinuationsAsynchronously;
            bool completeSync = (Status & OperationStatus.Sync) != 0;
            if (completeSync || !runContinuationsAsync)
            {
                ((IThreadPoolWorkItem)this).Execute();
            }
            else
            {
                ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
            }
        }

        private void SetSaeaResult()
        {
            _saea.SocketError = SocketError;
            _saea.AcceptSocket = AcceptedSocket;
        }

        void IThreadPoolWorkItem.Execute()
        {
            // Capture state.
            OperationStatus completionStatus = Status;

            // Reset state.
            Status = OperationStatus.None;
            CurrentAsyncContext = null;

            // Complete.
            _saea.Complete(completionStatus);
        }
    }

    // Base class for AsyncSocketEventArgsOperation and AwaitableSocketOperation.
    // Implements operation execution.
    // Derived classes implement completion.
    abstract class AsyncAcceptOperation : AsyncOperation
    {
        private Socket? Socket;
        protected Socket? AcceptedSocket;
        protected SocketError SocketError;

        public void Configure(Socket socket)
        {
            Socket = socket;
        }

        public override bool IsReadNotWrite => true;

        public override bool TryExecuteSync()
        {
            Socket socket = Socket!;
            (SocketError socketError, Socket? acceptedSocket) = SocketPal.Accept(socket.SafeHandle);
            AcceptedSocket = acceptedSocket;
            if (socketError == SocketError.WouldBlock)
            {
                return false;
            }
            SocketError = socketError;
            return true;
        }

        public override AsyncExecutionResult TryExecuteAsync(bool triggeredByPoll, AsyncExecutionQueue? executionQueue, AsyncExecutionCallback? callback, object? state, int data)
        {
            if (executionQueue != null && executionQueue.SupportsPolling == true)
            {
                Socket socket = Socket!;
                executionQueue!.AddPollIn(socket.SafeHandle, callback!, state, data);
                return AsyncExecutionResult.Executing;
            }
            else
            {
                bool finished = TryExecuteSync();
                return finished ? AsyncExecutionResult.Finished : AsyncExecutionResult.WaitForPoll;
            }
        }

        public override AsyncExecutionResult HandleAsyncResult(AsyncOperationResult asyncResult)
        {
            if (asyncResult.Errno == ECANCELED)
            {
                return AsyncExecutionResult.Cancelled;
            }

            // poll says we're ready
            bool finished = TryExecuteSync();
            return finished ? AsyncExecutionResult.Finished : AsyncExecutionResult.WaitForPoll;
        }

        protected void ResetOperationState()
        {
            Socket = null;
            AcceptedSocket = null;
            SocketError = SocketError.SocketError;
        }
    }
}
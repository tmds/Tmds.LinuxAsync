using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks.Sources;
using static Tmds.Linux.LibC;

namespace Tmds.LinuxAsync
{
    sealed class AwaitableSocketConnectOperation : AsyncConnectOperation, IValueTaskSource
    {
        private AwaitableSocketOperationCore<int> _core;

        public short Version => _core.Version;

        public void SetCompletedEvent(ManualResetEventSlim mre) => _core.SetCompletedEvent(mre);

        public AwaitableSocketConnectOperation()
            => _core.Init();

        public void RegisterCancellation(CancellationToken cancellationToken)
            => _core.RegisterCancellation(cancellationToken);

        public void GetResult(short token)
        {
            var result = _core.GetResult(token);
            ResetAndReturnThis();
            result.GetValue();
        }

        public ValueTaskSourceStatus GetStatus(short token)
            => _core.GetStatus(token);

        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            => _core.OnCompleted(continuation, state, token, flags);

        public override void Complete()
        {
            SocketError socketError = SocketError;
            OperationStatus status = Status;
            ResetOperationState();

            if ((status & OperationStatus.CancelledSync) == OperationStatus.CancelledSync)
            {
                // Caller threw an exception which prevents further use of this.
                ResetAndReturnThis();
            }
            else
            {
                _core.SetResult(0, socketError, status);
            }
        }

        private void ResetAndReturnThis()
        {
            _core.Reset();
            ReturnThis();
        }
    }

    sealed class SaeaConnectOperation : AsyncConnectOperation, IThreadPoolWorkItem
    {
        public readonly SocketAsyncEventArgs _saea;
        public SaeaConnectOperation(SocketAsyncEventArgs saea)
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
        }

        void IThreadPoolWorkItem.Execute()
        {
            // Capture state.
            OperationStatus completionStatus = Status;

            // Reset state.
            Status = OperationStatus.None;
            CurrentQueue = null;

            // Complete.
            _saea.Complete(completionStatus);
        }
    }

    // Base class for AsyncSocketEventArgsOperation and AwaitableSocketOperation.
    // Implements operation execution.
    // Derived classes implement completion.
    abstract class AsyncConnectOperation : AsyncOperation
    {
        private Socket? Socket;
        private EndPoint? EndPoint;
        private bool _connectCalled;
        protected SocketError SocketError;

        public void Configure(Socket socket, EndPoint endPoint)
        {
            Socket = socket;
            EndPoint = endPoint;

            if (endPoint.GetType() != typeof(IPEndPoint))
            {
                throw new NotSupportedException();
            }
        }

        public override bool TryExecuteSync()
        {
            Socket socket = Socket!;
            IPEndPoint ipEndPoint = (IPEndPoint)EndPoint!;
            SocketError socketError = SocketPal.Connect(socket.SafeHandle, ipEndPoint);
            if (socketError == SocketError.InProgress)
            {
                return false;
            }
            SocketError = socketError;
            return true;
        }

        public override AsyncExecutionResult TryExecuteEpollAsync(bool triggeredByPoll, AsyncExecutionQueue? executionQueue, IAsyncExecutionResultHandler callback)
        {
            if (!_connectCalled)
            {
                _connectCalled = true;
                bool finished = TryExecuteSync();
                return finished ? AsyncExecutionResult.Finished : AsyncExecutionResult.WaitForPoll;
            }
            else
            {
                if (triggeredByPoll)
                {
                    // TODO: read SOL_SOCKET, SO_ERROR to get errorcode...
                    SocketError = SocketError.Success;
                    return AsyncExecutionResult.Finished;
                }
                return AsyncExecutionResult.WaitForPoll;
            }
        }

        public override AsyncExecutionResult TryExecuteIOUringAsync(AsyncExecutionQueue executionQueue, IAsyncExecutionResultHandler callback, int key)
        {
            Socket socket = Socket!;
            executionQueue!.AddPollOut(socket.SafeHandle, callback!, key);
            return AsyncExecutionResult.Executing;
        }

        public override AsyncExecutionResult HandleAsyncResult(AsyncOperationResult asyncResult)
        {
            if (asyncResult.Errno == ECANCELED)
            {
                return AsyncExecutionResult.Cancelled;
            }

            // poll says we're ready
            // TODO: read SOL_SOCKET, SO_ERROR to get errorcode...
            SocketError = SocketError.Success;
            return AsyncExecutionResult.Finished;
        }

        protected void ResetOperationState()
        {
            Socket = null;
            EndPoint = null;
            _connectCalled = false;
            SocketError = SocketError.SocketError;
        }
    }
}
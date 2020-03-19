using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks.Sources;

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
            Debug.Assert((CompletionFlags & (OperationCompletionFlags.OperationCancelled | OperationCompletionFlags.OperationFinished)) != 0);

            SocketError socketError = SocketError;
            OperationCompletionFlags completionFlags = CompletionFlags;
            ResetOperationState();

            if ((completionFlags & OperationCompletionFlags.CompletedCanceledSync) == OperationCompletionFlags.CompletedCanceledSync)
            {
                // Caller threw an exception which prevents further use of this.
                ResetAndReturnThis();
            }
            else
            {
                _core.SetResult(0, socketError, completionFlags);
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
            Debug.Assert((CompletionFlags & (OperationCompletionFlags.OperationCancelled | OperationCompletionFlags.OperationFinished)) != 0);

            SetSaeaResult();
            ResetOperationState();

            bool runContinuationsAsync = _saea.RunContinuationsAsynchronously;
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

        private void SetSaeaResult()
        {
            _saea.SocketError = SocketError;
        }

        void IThreadPoolWorkItem.Execute()
        {
            // Capture state.
            OperationCompletionFlags completionStatus = CompletionFlags;

            // Reset state.
            CompletionFlags = OperationCompletionFlags.None;
            CurrentAsyncContext = null;

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
        }

        public override bool IsReadNotWrite => false;

        public override AsyncExecutionResult TryExecute(bool triggeredByPoll, bool isCancellationRequested, bool asyncOnly, AsyncExecutionQueue? executionQueue, AsyncExecutionCallback? callback, object? state, int data, AsyncOperationResult asyncResult)
        {
            Socket socket = Socket!;
            IPEndPoint? ipEndPoint = EndPoint as IPEndPoint;

            if (ipEndPoint == null)
            {
                ThrowHelper.ThrowInvalidOperationException();
            }

            // When there is a pollable executionQueue, use it to poll, and then try the operation.
            bool hasPollableExecutionQueue = executionQueue?.SupportsPolling == true;
            bool trySync = !hasPollableExecutionQueue && !asyncOnly;
            if (trySync || asyncResult.HasResult)
            {
                SocketError socketError;
                if (!_connectCalled)
                {
                    socketError = SocketPal.Connect(socket.SafeHandle, ipEndPoint);
                    _connectCalled = true;
                }
                else
                {
                    // TODO: read SOL_SOCKET, SO_ERROR to get errorcode...
                    socketError = SocketError.Success;
                }
                if (socketError != SocketError.InProgress)
                {
                    SocketError = socketError;
                    return AsyncExecutionResult.Finished;
                }
            }

            if (isCancellationRequested)
            {
                SocketError = SocketError.OperationAborted;
                return AsyncExecutionResult.Cancelled;
            }

            // poll
            if (hasPollableExecutionQueue)
            {
                executionQueue!.AddPollOut(socket.SafeHandle, callback!, state, data);
                return AsyncExecutionResult.Executing;
            }
            else
            {
                return AsyncExecutionResult.WaitForPoll;
            }
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
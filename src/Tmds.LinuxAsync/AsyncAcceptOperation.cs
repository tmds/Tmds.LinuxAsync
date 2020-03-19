using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks.Sources;

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
            Debug.Assert((CompletionFlags & (OperationCompletionFlags.OperationCancelled | OperationCompletionFlags.OperationFinished)) != 0);

            SocketError socketError = SocketError;
            Socket? acceptedSocket = AcceptedSocket;
            OperationCompletionFlags completionFlags = CompletionFlags;
            ResetOperationState();

            if ((completionFlags & OperationCompletionFlags.CompletedCanceledSync) == OperationCompletionFlags.CompletedCanceledSync)
            {
                // Caller threw an exception which prevents further use of this.
                ResetAndReturnThis();
            }
            else
            {
                _core.SetResult(acceptedSocket, socketError, completionFlags);
                AcceptedSocket = null;
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
            _saea.AcceptSocket = AcceptedSocket;
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

        public override AsyncExecutionResult TryExecute(bool triggeredByPoll, bool isCancellationRequested, bool asyncOnly, AsyncExecutionQueue? executionQueue, AsyncExecutionCallback? callback, object? state, int data, AsyncOperationResult asyncResult)
        {
            Socket socket = Socket!;

            // When there is a pollable executionQueue, use it to poll, and then try the operation.
            bool hasPollableExecutionQueue = executionQueue?.SupportsPolling == true;
            bool trySync = !hasPollableExecutionQueue && !asyncOnly;
            if (trySync || asyncResult.HasResult)
            {
                (SocketError socketError, Socket? acceptedSocket) = SocketPal.Accept(socket.SafeHandle);
                if (socketError != SocketError.WouldBlock)
                {
                    SocketError = socketError;
                    AcceptedSocket = acceptedSocket;
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
                executionQueue!.AddPollIn(socket.SafeHandle, callback!, state, data); ;
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
            AcceptedSocket = null;
            SocketError = SocketError.SocketError;
        }
    }
}
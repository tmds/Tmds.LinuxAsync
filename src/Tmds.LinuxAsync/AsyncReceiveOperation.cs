using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks.Sources;
using static Tmds.Linux.LibC;

namespace Tmds.LinuxAsync
{
    sealed class AwaitableSocketReceiveOperation : AsyncReceiveOperation, IValueTaskSource<int>
    {
        private AwaitableSocketOperationCore<int> _core;

        public short Version => _core.Version;

        public AwaitableSocketReceiveOperation()
            => _core.Init();

        public void RegisterCancellation(CancellationToken cancellationToken)
            => _core.RegisterCancellation(cancellationToken);

        public int GetResult(short token)
        {
            var result = _core.GetResult(token);
            ResetAndReturnThis();
            return result.GetValue();
        }

        public ValueTaskSourceStatus GetStatus(short token)
            => _core.GetStatus(token);

        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            => _core.OnCompleted(continuation, state, token, flags);

        public override void Complete()
        {
            Debug.Assert((CompletionFlags & (OperationCompletionFlags.OperationCancelled | OperationCompletionFlags.OperationFinished)) != 0);

            int bytesTransferred = BytesTransferred;
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
                _core.SetResult(bytesTransferred, socketError, completionFlags);
            }
        }

        private void ResetAndReturnThis()
        {
            _core.Reset();
            ReturnThis();
        }
    }

    sealed class SaeaReceiveOperation : AsyncReceiveOperation, IThreadPoolWorkItem
    {
        public readonly SocketAsyncEventArgs _saea;
        public SaeaReceiveOperation(SocketAsyncEventArgs saea)
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
            _saea.BytesTransferred = BytesTransferred;
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
    abstract class AsyncReceiveOperation : AsyncOperation
    {
        private Socket? Socket;
        private Memory<byte> MemoryBuffer;
        protected SocketError SocketError;
        protected int BytesTransferred;

        public void Configure(Socket socket, Memory<byte> memory, IList<ArraySegment<byte>>? bufferList)
        {
            if (bufferList != null)
            {
                throw new NotSupportedException();
            }

            Socket = socket;
            MemoryBuffer = memory;
        }

        public override bool IsReadNotWrite => true;

        public override AsyncExecutionResult TryExecute(bool triggeredByPoll, bool isCancellationRequested, bool asyncOnly, AsyncExecutionQueue? executionQueue, AsyncExecutionCallback? callback, object? state, int data, AsyncOperationResult asyncResult)
        {
            SocketError socketError = SocketError.SocketError;
            int bytesTransferred = -1;
            AsyncExecutionResult result = AsyncExecutionResult.Executing;
            if (asyncResult.HasResult)
            {
                if (asyncResult.IsError)
                {
                    if (asyncResult.Errno == EINTR)
                    {
                        result = AsyncExecutionResult.Executing;
                    }
                    else if (asyncResult.Errno == EAGAIN)
                    {
                        result = AsyncExecutionResult.WaitForPoll;
                    }
                    else
                    {
                        bytesTransferred = 0;
                        socketError = SocketPal.GetSocketErrorForErrno(asyncResult.Errno);
                        result = AsyncExecutionResult.Finished;
                    }
                }
                else
                {
                    bytesTransferred = asyncResult.IntValue;
                    socketError = SocketError.Success;
                    result = AsyncExecutionResult.Finished;
                }
            }

            if (isCancellationRequested && result != AsyncExecutionResult.Finished)
            {
                SocketError = SocketError.OperationAborted;
                return AsyncExecutionResult.Cancelled;
            }

            // When there is a pollable executionQueue, use it to poll, and then try the operation.
            if (result == AsyncExecutionResult.Executing ||
                (result == AsyncExecutionResult.WaitForPoll && executionQueue?.SupportsPolling == true))
            {
                Memory<byte> memory = MemoryBuffer;
                bool isPollingReadable = memory.Length == 0; // A zero-byte read is a poll.
                if (triggeredByPoll && isPollingReadable)
                {
                    // No need to make a syscall, poll told us we're readable.
                    (socketError, bytesTransferred) = (SocketError.Success, 0);
                    result = AsyncExecutionResult.Finished;
                }
                else
                {
                    Socket socket = Socket!;

                    // Using Linux AIO executionQueue, we can't check when there is no
                    // data available. Instead of return value EAGAIN, a 0-byte read returns '0'.
                    if (executionQueue != null &&
                        (!isPollingReadable || executionQueue.SupportsPolling)) // Don't use Linux AIO for 0-byte reads.
                    {
                        executionQueue.AddRead(socket.SafeHandle, memory, callback!, state, data);
                        result = AsyncExecutionResult.Executing;
                    }
                    else if (result == AsyncExecutionResult.Executing)
                    {
                        if (asyncOnly)
                        {
                            result = AsyncExecutionResult.WaitForPoll;
                        }
                        else
                        {
                            (socketError, bytesTransferred) = SocketPal.Recv(socket.SafeHandle, memory);
                            result = socketError == SocketError.WouldBlock ? AsyncExecutionResult.WaitForPoll : AsyncExecutionResult.Finished;
                        }
                    }
                }
            }

            if (result == AsyncExecutionResult.Finished)
            {
                Debug.Assert(bytesTransferred != -1);
                BytesTransferred = bytesTransferred;
                SocketError = socketError;
            }

            return result;
        }

        protected void ResetOperationState()
        {
            Socket = null;
            MemoryBuffer = default;
            SocketError = SocketError.SocketError;
            BytesTransferred = 0;
        }
    }
}
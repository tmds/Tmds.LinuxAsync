using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks.Sources;
using static Tmds.Linux.LibC;

namespace Tmds.LinuxAsync
{
    sealed class AwaitableSocketSendOperation : AsyncSendOperation, IValueTaskSource<int>
    {
        private AwaitableSocketOperationCore<int> _core;

        public short Version => _core.Version;

        public AwaitableSocketSendOperation()
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

    sealed class SaeaSendOperation : AsyncSendOperation, IThreadPoolWorkItem
    {
        public readonly SocketAsyncEventArgs _saea;
        public SaeaSendOperation(SocketAsyncEventArgs saea)
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
    abstract class AsyncSendOperation : AsyncOperation
    {
        private Socket? Socket;
        private Memory<byte> MemoryBuffer;
        private IList<ArraySegment<byte>>? BufferList;
        private int _bufferIndex;
        protected SocketError SocketError;
        protected int BytesTransferred;

        public void Configure(Socket socket, Memory<byte> memory, IList<ArraySegment<byte>>? bufferList)
        {
            Socket = socket;
            MemoryBuffer = memory;
            BufferList = bufferList;
        }

        public override bool IsReadNotWrite => false;

        public override AsyncExecutionResult TryExecute(bool triggeredByPoll, bool isCancellationRequested, bool asyncOnly, AsyncExecutionQueue? executionQueue, AsyncExecutionCallback? callback, object? state, int data, AsyncOperationResult asyncResult)
        {
            IList<ArraySegment<byte>>? bufferList = BufferList;

            if (bufferList == null)
            {
                return SendSingleBuffer(MemoryBuffer, isCancellationRequested, asyncOnly, executionQueue, callback, state, data, asyncResult);
            }
            else
            {
                return SendMultipleBuffers(bufferList, isCancellationRequested, asyncOnly, executionQueue, callback, state, data, asyncResult);
            }
        }

        private unsafe AsyncExecutionResult SendMultipleBuffers(IList<ArraySegment<byte>> buffers, bool isCancellationRequested, bool asyncOnly, AsyncExecutionQueue? executionQueue, AsyncExecutionCallback? callback, object? state, int data, AsyncOperationResult asyncResult)
        {
            // TODO: really support multi-buffer sends...
            for (; _bufferIndex < buffers.Count; _bufferIndex++)
            {
                AsyncExecutionResult bufferSendResult = SendSingleBuffer(buffers[_bufferIndex], isCancellationRequested, asyncOnly, executionQueue, callback, state, data, asyncResult);
                if (bufferSendResult == AsyncExecutionResult.WaitForPoll || bufferSendResult == AsyncExecutionResult.Executing)
                {
                    return bufferSendResult;
                }
                if (SocketError != SocketError.Success)
                {
                    break;
                }
                BytesTransferred = 0; // TODO... not really correct
            }
            return AsyncExecutionResult.Finished;
        }

        private AsyncExecutionResult SendSingleBuffer(Memory<byte> memory, bool isCancellationRequested, bool asyncOnly, AsyncExecutionQueue? executionQueue, AsyncExecutionCallback? callback, object? state, int data, AsyncOperationResult asyncResult)
        {
            SocketError socketError = SocketError.SocketError;
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
                        socketError = SocketPal.GetSocketErrorForErrno(asyncResult.Errno);
                        result = AsyncExecutionResult.Finished;
                    }
                }
                else
                {
                    BytesTransferred += asyncResult.IntValue;
                    if (BytesTransferred == memory.Length)
                    {
                        socketError = SocketError.Success;
                        result = AsyncExecutionResult.Finished;
                    }
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
                Socket socket = Socket!;
                if (socket == null)
                {
                    ThrowHelper.ThrowInvalidOperationException();
                }

                if (executionQueue != null)
                {
                    Memory<byte> remaining = memory.Slice(BytesTransferred);
                    executionQueue.AddWrite(socket.SafeHandle, remaining, callback!, state, data);
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
                        while (true)
                        {
                            Memory<byte> remaining = memory.Slice(BytesTransferred);
                            int bytesTransferred;
                            (socketError, bytesTransferred) = SocketPal.Send(socket.SafeHandle, remaining);
                            if (socketError == SocketError.Success)
                            {
                                BytesTransferred += bytesTransferred;
                                if (BytesTransferred == memory.Length || bytesTransferred == 0)
                                {
                                    break;
                                }
                            }
                            else
                            {
                                break;
                            }
                        }
                        result = socketError == SocketError.WouldBlock ? AsyncExecutionResult.WaitForPoll : AsyncExecutionResult.Finished;
                    }
                }
            }

            if (result == AsyncExecutionResult.Finished)
            {
                SocketError = socketError;
            }

            return result;
        }

        protected void ResetOperationState()
        {
            Socket = null;
            MemoryBuffer = default;
            BufferList = null;
            _bufferIndex = 0;
            SocketError = SocketError.SocketError;
            BytesTransferred = 0;
        }
    }
}
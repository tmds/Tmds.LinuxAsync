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
            int bytesTransferred = BytesTransferred;
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
                _core.SetResult(bytesTransferred, socketError, status);
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
            _saea.BytesTransferred = BytesTransferred;
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
    abstract class AsyncSendOperation : AsyncOperation
    {
        private Socket? Socket;
        private Memory<byte> MemoryBuffer;
        private IList<ArraySegment<byte>>? BufferList;
        private int _bufferIndex;
        private int _bufferOffset;
        protected SocketError SocketError;
        protected int BytesTransferred;

        public void Configure(Socket socket, Memory<byte> memory, IList<ArraySegment<byte>>? bufferList)
        {
            Socket = socket;
            MemoryBuffer = memory;
            BufferList = bufferList;
        }

        public override bool IsReadNotWrite => false;

        public override bool TryExecuteSync()
        {
            IList<ArraySegment<byte>>? bufferList = BufferList;

            if (bufferList == null)
            {
                return TryExecuteSingleBufferSync(MemoryBuffer);
            }
            else
            {
                return TryExecuteMultipleBuffersSync(bufferList);
            }
        }

        private bool TryExecuteSingleBufferSync(Memory<byte> memory)
        {
            Socket socket = Socket!;
            (SocketError socketError, int bytesTransferred) = SendMemory(socket, memory);
            BytesTransferred += bytesTransferred;
            if (socketError == SocketError.WouldBlock)
            {
                return false;
            }
            SocketError = socketError;
            return true;
        }

        private static (SocketError socketError, int bytesTransferred) SendMemory(Socket socket, Memory<byte> memory)
        {
            int bytesTransferredTotal = 0;
            while (true)
            {
                Memory<byte> remaining = memory.Slice(bytesTransferredTotal);
                (SocketError socketError, int bytesTransferred) = SocketPal.Send(socket.SafeHandle, remaining);
                bytesTransferredTotal += bytesTransferred;
                if (socketError == SocketError.Success)
                {
                    if (bytesTransferredTotal != memory.Length && bytesTransferred != 0)
                    {
                        continue;
                    }
                }
                return (socketError, bytesTransferredTotal);
            }
        }

        private bool TryExecuteMultipleBuffersSync(IList<ArraySegment<byte>> buffers)
        {
            Socket socket = Socket!;
            for (; _bufferIndex < buffers.Count; _bufferIndex++)
            {
                Memory<byte> memory = buffers[_bufferIndex].Slice(_bufferOffset);
                (SocketError socketError, int bytesTransferred) = SendMemory(socket, memory);
                _bufferOffset += bytesTransferred;
                BytesTransferred += bytesTransferred;
                if (socketError == SocketError.WouldBlock)
                {
                    return false;
                }
                else if (socketError == SocketError.Success && bytesTransferred == memory.Length)
                {
                    _bufferOffset = 0;
                    continue;
                }
                else
                {
                    SocketError = socketError;
                    return true;
                }
            }
            SocketError = SocketError.Success;
            return true;
        }

        public override AsyncExecutionResult TryExecuteAsync(bool triggeredByPoll, AsyncExecutionQueue? executionQueue, AsyncExecutionCallback? callback, object? state, int data)
        {
            IList<ArraySegment<byte>>? bufferList = BufferList;

            if (bufferList == null)
            {
                return TryExecuteSingleBufferAsync(MemoryBuffer.Slice(BytesTransferred), executionQueue, callback, state, data);
            }
            else
            {
                return TryExecuteMultipleBuffersAsync(bufferList, executionQueue, callback, state, data);
            }
        }

        private AsyncExecutionResult TryExecuteMultipleBuffersAsync(IList<ArraySegment<byte>> buffers, AsyncExecutionQueue? executionQueue, AsyncExecutionCallback? callback, object? state, int data)
        {
            if (executionQueue != null)
            {
                Socket socket = Socket!;
                Memory<byte> memory = buffers[_bufferIndex].Slice(_bufferOffset);
                executionQueue.AddWrite(socket.SafeHandle, memory, callback!, state, data);
                return AsyncExecutionResult.Executing;
            }
            else
            {
                bool finished = TryExecuteMultipleBuffersSync(buffers);
                return finished ? AsyncExecutionResult.Finished : AsyncExecutionResult.WaitForPoll;
            }
        }

        private AsyncExecutionResult TryExecuteSingleBufferAsync(Memory<byte> memory, AsyncExecutionQueue? executionQueue, AsyncExecutionCallback? callback, object? state, int data)
        {
            if (executionQueue != null)
            {
                Socket socket = Socket!;
                executionQueue.AddWrite(socket.SafeHandle, memory, callback!, state, data);
                return AsyncExecutionResult.Executing;
            }
            else
            {
                bool finished = TryExecuteSingleBufferSync(memory);
                return finished ? AsyncExecutionResult.Finished : AsyncExecutionResult.WaitForPoll;
            }
        }

        public override AsyncExecutionResult HandleAsyncResult(AsyncOperationResult asyncResult)
        {
            if (asyncResult.Errno == 0)
            {
                BytesTransferred += asyncResult.IntValue;

                IList<ArraySegment<byte>>? bufferList = BufferList;
                if (bufferList == null)
                {
                    if (BytesTransferred == MemoryBuffer.Length)
                    {
                        SocketError = SocketError.Success;
                        return AsyncExecutionResult.Finished;
                    }
                }
                else
                {
                    _bufferOffset += asyncResult.IntValue;
                    if (_bufferOffset == bufferList[_bufferIndex].Count)
                    {
                        _bufferOffset = 0;
                        _bufferIndex++;
                        if (_bufferIndex == bufferList.Count)
                        {
                            SocketError = SocketError.Success;
                            return AsyncExecutionResult.Finished;
                        }
                    }
                }

                return AsyncExecutionResult.Executing;
            }
            else if (asyncResult.Errno == EINTR)
            {
                return AsyncExecutionResult.Executing;
            }
            else if (asyncResult.Errno == ECANCELED)
            {
                return AsyncExecutionResult.Cancelled;
            }
            else if (asyncResult.Errno == EAGAIN)
            {
                return AsyncExecutionResult.WaitForPoll;
            }
            else
            {
                SocketError = SocketPal.GetSocketErrorForErrno(asyncResult.Errno);
                return AsyncExecutionResult.Finished;
            }
        }

        protected void ResetOperationState()
        {
            Socket = null;
            MemoryBuffer = default;
            BufferList = null;
            _bufferIndex = 0;
            _bufferOffset = 0;
            SocketError = SocketError.SocketError;
            BytesTransferred = 0;
        }
    }
}
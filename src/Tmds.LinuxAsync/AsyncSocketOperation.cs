using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Tmds.Linux;

namespace Tmds.LinuxAsync
{
    // Base class for AsyncSocketEventArgsOperation and AwaitableSocketOperation.
    // Implements operation execution.
    // Derived classes implement completion.
    abstract class AsyncSocketOperation : AsyncOperation
    {
        private SocketAsyncEventArgs? _saea;
        protected internal SocketAsyncEventArgs Saea { get => _saea!; protected set => _saea = value; }

        // state for on-going operations
        private int _bytesTransferredTotal;
        private int _bufferIndex;
        private int _bufferOffset;

        public AsyncSocketOperation(SocketAsyncEventArgs sea)
        {
            Saea = sea;
        }

        protected AsyncSocketOperation()
        { }

        public override bool IsReadNotWrite
            => IsOperationReadNotWrite(Saea.CurrentOperation);

        public static bool IsOperationReadNotWrite(SocketAsyncOperation operation)
        {
            switch (operation)
            {
                case SocketAsyncOperation.Receive:
                case SocketAsyncOperation.Accept:
                    return true;
                case SocketAsyncOperation.Send:
                case SocketAsyncOperation.Connect:
                    return false;
                // case SocketAsyncOperation.ReceiveFrom:
                // case SocketAsyncOperation.ReceiveMessageFrom:
                // case SocketAsyncOperation.SendPackets:
                // case SocketAsyncOperation.SendTo:
                // case SocketAsyncOperation.Accept:
                // case SocketAsyncOperation.Connect:
                // case SocketAsyncOperation.Disconnect:
                case SocketAsyncOperation.None:
                    ThrowHelper.ThrowInvalidOperationException();
                    break;
                default:
                    ThrowHelper.ThrowIndexOutOfRange(operation);
                    break;
            }
            return false;
        }

        public override AsyncExecutionResult TryExecute(bool triggeredByPoll, AsyncExecutionQueue? executionQueue, AsyncExecutionCallback? callback, object? state, int data, AsyncOperationResult? asyncResult)
        {
            AsyncExecutionResult result = AsyncExecutionResult.WouldBlock;

            SocketAsyncOperation currentOperation = Saea.CurrentOperation;
            switch (Saea.CurrentOperation)
            {
                case SocketAsyncOperation.Receive:
                    result = TryExecuteReceive(triggeredByPoll, executionQueue, callback, state, data, asyncResult);
                    break;
                case SocketAsyncOperation.Send:
                    result = TryExecuteSend(executionQueue, callback, state, data, asyncResult);
                    break;
                case SocketAsyncOperation.Accept:
                    result = TryExecuteAccept();
                    break;
                case SocketAsyncOperation.Connect:
                    result = TryExecuteConnect();
                    break;
                case SocketAsyncOperation.None:
                    ThrowHelper.ThrowInvalidOperationException();
                    break;
                default:
                    ThrowHelper.ThrowIndexOutOfRange(currentOperation);
                    break;
            }

            return result;
        }

        private unsafe AsyncExecutionResult TryExecuteReceive(bool triggeredByPoll, AsyncExecutionQueue? executionQueue, AsyncExecutionCallback? callback, object? state, int data, AsyncOperationResult? asyncResult)
        {
            Memory<byte> memory = Saea.MemoryBuffer;

            bool isPollingReadable = memory.Length == 0; // A zero-byte read is a poll.

            SocketError socketError;
            int bytesTransferred;
            if (TryGetBytesTransferredResult(asyncResult, out bytesTransferred, out socketError))
            { }
            else
            {
                if (triggeredByPoll && isPollingReadable)
                {
                    // No need to make a call, assume we're readable.
                    (socketError, bytesTransferred) = (SocketError.Success, 0);
                }
                else
                {
                    Socket? socket = Saea.CurrentSocket;
                    if (socket == null)
                    {
                        ThrowHelper.ThrowInvalidOperationException();
                    }

                    if (executionQueue != null &&
                        !isPollingReadable) // Linux AIO returns Success instead of WouldBlock for zero-byte reads. (TODO)
                    {
                        executionQueue.AddRead(socket.SafeHandle, memory, callback!, state, data);
                        return AsyncExecutionResult.Executing;
                    }
                    else
                    {
                        (socketError, bytesTransferred) = SocketPal.Recv(socket.SafeHandle, memory);
                    }
                }
            }

            bool finished = socketError != SocketError.WouldBlock;

            if (finished)
            {
                Saea.BytesTransferred = bytesTransferred;
                Saea.SocketError = socketError;
            }

            return finished ? AsyncExecutionResult.Finished : AsyncExecutionResult.WouldBlock;
        }

        private unsafe AsyncExecutionResult TryExecuteSend(AsyncExecutionQueue? executionQueue, AsyncExecutionCallback? callback, object? state, int data, AsyncOperationResult? result)
        {
            IList<ArraySegment<byte>>? bufferList = Saea.BufferList;

            if (bufferList == null)
            {
                return SendSingleBuffer(Saea.MemoryBuffer, executionQueue, callback, state, data, result);
            }
            else
            {
                return SendMultipleBuffers(bufferList, executionQueue, callback, state, data, result);
            }
        }

        private unsafe AsyncExecutionResult SendMultipleBuffers(IList<ArraySegment<byte>> buffers, AsyncExecutionQueue? executionQueue, AsyncExecutionCallback? callback, object? state, int data, AsyncOperationResult? result)
        {
            // TODO: use executionQueue

            Socket? socket = Saea.CurrentSocket;

            if (socket == null)
            {
                ThrowHelper.ThrowInvalidOperationException();
            }

            int bufferIndex = _bufferIndex;
            int bufferOffset = _bufferOffset;

            int iovLength = buffers.Count - bufferIndex; // TODO: limit stackalloc
            GCHandle* handles = stackalloc GCHandle[iovLength];
            iovec* iovecs = stackalloc iovec[iovLength];

            SocketError socketError;
            int bytesTransferred;
            try
            {
                for (int i = 0; i < iovLength; i++, bufferOffset = 0)
                {
                    ArraySegment<byte> buffer = buffers[bufferIndex + i];

                    handles[i] = GCHandle.Alloc(buffer.Array, GCHandleType.Pinned);
                    iovecs[i].iov_base = &((byte*)handles[i].AddrOfPinnedObject())[buffer.Offset + bufferOffset];
                    iovecs[i].iov_len = buffer.Count - bufferOffset;
                }

                msghdr msg = default;
                msg.msg_iov = iovecs;
                msg.msg_iovlen = iovLength;

                (socketError, bytesTransferred) = SocketPal.SendMsg(socket.SafeHandle, &msg);
            }
            finally
            {
                // Free GC handles.
                for (int i = 0; i < iovLength; i++)
                {
                    if (handles[i].IsAllocated)
                    {
                        handles[i].Free();
                    }
                }
            }

            bool finished = socketError != SocketError.WouldBlock;
            if (socketError == SocketError.Success && bytesTransferred > 0)
            {
                _bytesTransferredTotal += bytesTransferred;

                int endIndex = bufferIndex, endOffset = _bufferOffset, unconsumed = bytesTransferred;
                for (; endIndex < buffers.Count && unconsumed > 0; endIndex++, endOffset = 0)
                {
                    int space = buffers[endIndex].Count - endOffset;
                    if (space > unconsumed)
                    {
                        endOffset += unconsumed;
                        break;
                    }
                    unconsumed -= space;
                }
                _bufferIndex = endIndex;
                _bufferOffset = endOffset;

                finished = _bufferIndex == buffers.Count;
            }
            return finished ? AsyncExecutionResult.Finished : AsyncExecutionResult.WouldBlock;
        }

        private AsyncExecutionResult SendSingleBuffer(Memory<byte> memory, AsyncExecutionQueue? executionQueue, AsyncExecutionCallback? callback, object? state, int data, AsyncOperationResult? asyncResult)
        {
            SocketError socketError;
            int bytesTransferred;
            bool finished = false;
            if (TryGetBytesTransferredResult(asyncResult, out bytesTransferred, out socketError))
            {
                finished = socketError != SocketError.WouldBlock;
                if (socketError == SocketError.Success && bytesTransferred > 0)
                {
                    _bytesTransferredTotal += bytesTransferred;
                    finished = _bytesTransferredTotal == memory.Length;
                }
            }
            if (!finished)
            {
                Socket? socket = Saea.CurrentSocket;
                if (socket == null)
                {
                    ThrowHelper.ThrowInvalidOperationException();
                }

                Memory<byte> remaining = memory.Slice(_bytesTransferredTotal);
                if (executionQueue != null)
                {
                    executionQueue.AddWrite(socket.SafeHandle, remaining, callback!, state, data);
                    return AsyncExecutionResult.Executing;
                }
                else
                {
                    (socketError, bytesTransferred) = SocketPal.Send(socket.SafeHandle, remaining);
                    if (socketError == SocketError.Success && bytesTransferred > 0)
                    {
                        _bytesTransferredTotal += bytesTransferred;
                        finished = _bytesTransferredTotal == memory.Length;
                    }
                }
            }
            return finished ? AsyncExecutionResult.Finished : AsyncExecutionResult.WouldBlock;
        }

        private AsyncExecutionResult TryExecuteConnect()
        {
            Socket? socket = Saea.CurrentSocket;
            IPEndPoint? ipEndPoint = Saea.RemoteEndPoint as IPEndPoint;

            if (socket == null)
            {
                ThrowHelper.ThrowInvalidOperationException();
            }
            if (ipEndPoint == null)
            {
                ThrowHelper.ThrowInvalidOperationException();
            }

            SocketError socketError = SocketPal.Connect(socket.SafeHandle, ipEndPoint);

            bool finished = socketError != SocketError.WouldBlock && socketError != SocketError.InProgress;

            if (finished)
            {
                Saea.SocketError = socketError;
            }

            return finished ? AsyncExecutionResult.Finished : AsyncExecutionResult.WouldBlock;
        }

        private AsyncExecutionResult TryExecuteAccept()
        {
            Socket? socket = Saea.CurrentSocket;

            if (socket == null)
            {
                ThrowHelper.ThrowInvalidOperationException();
            }

            (SocketError socketError, Socket? acceptedSocket) = SocketPal.Accept(socket.SafeHandle);

            bool finished = socketError != SocketError.WouldBlock;

            if (finished)
            {
                Saea.SocketError = socketError;
                Saea.AcceptSocket = acceptedSocket;
            }

            return finished ? AsyncExecutionResult.Finished : AsyncExecutionResult.WouldBlock;
        }

        protected void ResetOperationState()
        {
            _bytesTransferredTotal = 0;
            _bufferIndex = 0;
            _bufferOffset = 0;
        }

        private static bool TryGetBytesTransferredResult(AsyncOperationResult? asyncResult, out int bytesTransferred, out SocketError socketError)
        {
            if (asyncResult.HasValue)
            {
                int rv = asyncResult.Value.Result;
                if (rv < 0)
                {
                    socketError = SocketPal.GetSocketErrorForErrno(-rv);
                    bytesTransferred = 0;
                }
                else
                {
                    socketError = SocketError.Success;
                    bytesTransferred = rv;
                }
                return true;
            }
            else
            {
                socketError = SocketError.SocketError;
                bytesTransferred = -1;
                return false;
            }
        }
    }
}
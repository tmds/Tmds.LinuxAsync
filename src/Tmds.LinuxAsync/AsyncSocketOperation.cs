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

        public override bool TryExecute(bool isSync)
        {
            bool finished = false;

            SocketAsyncOperation currentOperation = Saea.CurrentOperation;
            switch (Saea.CurrentOperation)
            {
                case SocketAsyncOperation.Receive:
                    finished = TryExecuteReceive(isSync);
                    break;
                case SocketAsyncOperation.Send:
                    finished = TryExecuteSend();
                    break;
                case SocketAsyncOperation.Accept:
                    finished = TryExecuteAccept();
                    break;
                case SocketAsyncOperation.Connect:
                    finished = TryExecuteConnect();
                    break;
                case SocketAsyncOperation.None:
                    ThrowHelper.ThrowInvalidOperationException();
                    break;
                default:
                    ThrowHelper.ThrowIndexOutOfRange(currentOperation);
                    break;
            }

            return finished;
        }

        private unsafe bool TryExecuteReceive(bool isSync)
        {
            Socket? socket = Saea.CurrentSocket;
            Memory<byte> memory = Saea.MemoryBuffer;

            if (socket == null)
            {
                ThrowHelper.ThrowInvalidOperationException();
            }

            SocketError socketError;
            int bytesTransferred;
            if (!isSync && memory.Length == 0)
            {
                // Zero-byte read to check readable.
                (socketError, bytesTransferred) = (SocketError.Success, 0);
            }
            else
            {
                (socketError, bytesTransferred) = SocketPal.Recv(socket.SafeHandle, memory);
            }

            bool finished = socketError != SocketError.WouldBlock;

            if (finished)
            {
                Saea.BytesTransferred = bytesTransferred;
                Saea.SocketError = socketError;
            }

            return finished;
        }

        private unsafe bool TryExecuteSend()
        {
            Socket? socket = Saea.CurrentSocket;

            if (socket == null)
            {
                ThrowHelper.ThrowInvalidOperationException();
            }

            IList<ArraySegment<byte>>? bufferList = Saea.BufferList;

            bool finished;
            SocketError socketError;
            if (bufferList == null)
            {
                (finished, socketError) = SendSingleBuffer(socket, Saea.MemoryBuffer);
            }
            else
            {
                (finished, socketError) = SendMultipleBuffers(socket, bufferList);
            }

            if (finished)
            {
                Saea.BytesTransferred = _bytesTransferredTotal;
                Saea.SocketError = socketError;
            }

            return finished;
        }

        private unsafe (bool finished, SocketError socketError) SendMultipleBuffers(Socket socket, IList<ArraySegment<byte>> buffers)
        {
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

            return (finished, socketError);
        }

        private (bool finished, SocketError socketError) SendSingleBuffer(Socket socket, Memory<byte> memory)
        {
            Memory<byte> remaining = memory.Slice(_bytesTransferredTotal);
            (SocketError socketError, int bytesTransferred) = SocketPal.Send(socket.SafeHandle, remaining);

            bool finished = socketError != SocketError.WouldBlock;
            if (socketError == SocketError.Success && bytesTransferred > 0)
            {
                _bytesTransferredTotal += bytesTransferred;
                finished = _bytesTransferredTotal == memory.Length;
            }
            return (finished, socketError);
        }

        private bool TryExecuteConnect()
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

            return finished;
        }

        private bool TryExecuteAccept()
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

            return finished;
        }

        protected void ResetOperationState()
        {
            _bytesTransferredTotal = 0;
            _bufferIndex = 0;
            _bufferOffset = 0;
        }
    }
}
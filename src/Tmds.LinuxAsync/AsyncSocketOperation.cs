using System;
using System.Net;
using System.Net.Sockets;

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

        public AsyncSocketOperation(SocketAsyncEventArgs sea)
        {
            Saea = sea;
        }

        protected AsyncSocketOperation()
        { }

        public override bool IsReadNotWrite
        {
            get
            {
                SocketAsyncOperation currentOperation = Saea.CurrentOperation;
                switch (currentOperation)
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
                        return false;
                    default:
                        ThrowHelper.ThrowIndexOutOfRange(currentOperation);
                        return false;
                }
            }
        }

        public override bool TryExecute()
        {
            bool finished = false;

            SocketAsyncOperation currentOperation = Saea.CurrentOperation;
            switch (Saea.CurrentOperation)
            {
                case SocketAsyncOperation.Receive:
                    finished = TryExecuteReceive();
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

        private unsafe bool TryExecuteReceive()
        {
            Socket? socket = Saea.CurrentSocket;
            Memory<byte> memory = Saea.MemoryBuffer;

            if (socket == null)
            {
                ThrowHelper.ThrowInvalidOperationException();
            }

            (SocketError socketError, int bytesTransferred) = SocketPal.Recv(socket.SafeHandle, memory);

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
            Memory<byte> memory = Saea.MemoryBuffer;

            if (socket == null)
            {
                ThrowHelper.ThrowInvalidOperationException();
            }

            Memory<byte> remaining = memory.Slice(_bytesTransferredTotal);
            (SocketError socketError, int bytesTransferred) = SocketPal.Send(socket.SafeHandle, remaining);

            bool finished = socketError != SocketError.WouldBlock;
            if (socketError == SocketError.Success && bytesTransferred > 0)
            {
                _bytesTransferredTotal += bytesTransferred;
                finished = _bytesTransferredTotal == memory.Length;
            }

            if (finished)
            {
                Saea.BytesTransferred = bytesTransferred;
                Saea.SocketError = socketError;
            }

            return finished;
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
        }
    }
}
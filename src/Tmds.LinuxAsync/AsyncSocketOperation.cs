using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using static Tmds.Linux.LibC;

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
        private bool _connectCalled;

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

        public override AsyncExecutionResult TryExecute(bool triggeredByPoll, bool isCancellationRequested, bool asyncOnly, AsyncExecutionQueue? executionQueue, AsyncExecutionCallback? callback, object? state, int data, AsyncOperationResult asyncResult)
        {
            AsyncExecutionResult result;

            SocketAsyncOperation currentOperation = Saea.CurrentOperation;
            switch (Saea.CurrentOperation)
            {
                case SocketAsyncOperation.Receive:
                    result = TryExecuteReceive(triggeredByPoll, isCancellationRequested, asyncOnly, executionQueue, callback, state, data, asyncResult);
                    break;
                case SocketAsyncOperation.Send:
                    result = TryExecuteSend(isCancellationRequested, asyncOnly, executionQueue, callback, state, data, asyncResult);
                    break;
                case SocketAsyncOperation.Accept:
                    result = TryExecuteAccept(isCancellationRequested, asyncOnly, executionQueue, callback, state, data, asyncResult);
                    break;
                case SocketAsyncOperation.Connect:
                    result = TryExecuteConnect(isCancellationRequested, asyncOnly, executionQueue, callback, state, data, asyncResult);
                    break;
                case SocketAsyncOperation.None:
                    result = AsyncExecutionResult.Finished;
                    ThrowHelper.ThrowInvalidOperationException();
                    break;
                default:
                    result = AsyncExecutionResult.Finished;
                    ThrowHelper.ThrowIndexOutOfRange(currentOperation);
                    break;
            }

            return result;
        }

        private unsafe AsyncExecutionResult TryExecuteReceive(bool triggeredByPoll, bool isCancellationRequested, bool asyncOnly, AsyncExecutionQueue? executionQueue, AsyncExecutionCallback? callback, object? state, int data, AsyncOperationResult asyncResult)
        {
            IList<ArraySegment<byte>>? bufferList = Saea.BufferList;
            if (bufferList != null)
            {
                throw new NotSupportedException();
            }

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
                Saea.BytesTransferred = 0;
                Saea.SocketError = SocketError.OperationAborted;
                return AsyncExecutionResult.Cancelled;
            }

            // When there is a pollable executionQueue, use it to poll, and then try the operation.
            if (result == AsyncExecutionResult.Executing ||
                (result == AsyncExecutionResult.WaitForPoll && executionQueue?.SupportsPolling == true))
            {
                Memory<byte> memory = Saea.MemoryBuffer;
                bool isPollingReadable = memory.Length == 0; // A zero-byte read is a poll.
                if (triggeredByPoll && isPollingReadable)
                {
                    // No need to make a syscall, poll told us we're readable.
                    (socketError, bytesTransferred) = (SocketError.Success, 0);
                    result = AsyncExecutionResult.Finished;
                }
                else
                {
                    Socket? socket = Saea.CurrentSocket;
                    if (socket == null)
                    {
                        ThrowHelper.ThrowInvalidOperationException();
                    }

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
                Saea.BytesTransferred = bytesTransferred;
                Saea.SocketError = socketError;
            }

            return result;
        }

        private unsafe AsyncExecutionResult TryExecuteSend(bool isCancellationRequested, bool asyncOnly, AsyncExecutionQueue? executionQueue, AsyncExecutionCallback? callback, object? state, int data, AsyncOperationResult result)
        {
            IList<ArraySegment<byte>>? bufferList = Saea.BufferList;

            if (bufferList == null)
            {
                return SendSingleBuffer(Saea.MemoryBuffer, isCancellationRequested, asyncOnly, executionQueue, callback, state, data, result);
            }
            else
            {
                return SendMultipleBuffers(bufferList, isCancellationRequested, asyncOnly, executionQueue, callback, state, data, result);
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
                if (Saea.SocketError != SocketError.Success)
                {
                    break;
                }
                _bytesTransferredTotal = 0;
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
                    _bytesTransferredTotal += asyncResult.IntValue;
                    if (_bytesTransferredTotal == memory.Length)
                    {
                        socketError = SocketError.Success;
                        result = AsyncExecutionResult.Finished;
                    }
                }
            }

            if (isCancellationRequested && result != AsyncExecutionResult.Finished)
            {
                Saea.BytesTransferred = _bytesTransferredTotal;
                Saea.SocketError = SocketError.OperationAborted;
                return AsyncExecutionResult.Cancelled;
            }

            // When there is a pollable executionQueue, use it to poll, and then try the operation.
            if (result == AsyncExecutionResult.Executing ||
                (result == AsyncExecutionResult.WaitForPoll && executionQueue?.SupportsPolling == true))
            {
                Socket? socket = Saea.CurrentSocket;
                if (socket == null)
                {
                    ThrowHelper.ThrowInvalidOperationException();
                }

                if (executionQueue != null)
                {
                    Memory<byte> remaining = memory.Slice(_bytesTransferredTotal);
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
                            Memory<byte> remaining = memory.Slice(_bytesTransferredTotal);
                            int bytesTransferred;
                            (socketError, bytesTransferred) = SocketPal.Send(socket.SafeHandle, remaining);
                            if (socketError == SocketError.Success)
                            {
                                _bytesTransferredTotal += bytesTransferred;
                                if (_bytesTransferredTotal == memory.Length || bytesTransferred == 0)
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
                Saea.BytesTransferred = _bytesTransferredTotal;
                Saea.SocketError = socketError;
            }

            return result;
        }

        private AsyncExecutionResult TryExecuteConnect(bool isCancellationRequested, bool asyncOnly, AsyncExecutionQueue? executionQueue, AsyncExecutionCallback? callback, object? state, int data, AsyncOperationResult asyncResult)
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
                    Saea.SocketError = socketError;
                    return AsyncExecutionResult.Finished;
                }
            }

            if (isCancellationRequested)
            {
                Saea.SocketError = SocketError.OperationAborted;
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

        private AsyncExecutionResult TryExecuteAccept(bool isCancellationRequested, bool asyncOnly, AsyncExecutionQueue? executionQueue, AsyncExecutionCallback? callback, object? state, int data, AsyncOperationResult asyncResult)
        {
            Socket? socket = Saea.CurrentSocket;

            if (socket == null)
            {
                ThrowHelper.ThrowInvalidOperationException();
            }

            // When there is a pollable executionQueue, use it to poll, and then try the operation.
            bool hasPollableExecutionQueue = executionQueue?.SupportsPolling == true;
            bool trySync = !hasPollableExecutionQueue && !asyncOnly;
            if (trySync || asyncResult.HasResult)
            {
                (SocketError socketError, Socket? acceptedSocket) = SocketPal.Accept(socket.SafeHandle);
                if (socketError != SocketError.WouldBlock)
                {
                    Saea.SocketError = socketError;
                    Saea.AcceptSocket = acceptedSocket;
                    return AsyncExecutionResult.Finished;
                }
            }

            if (isCancellationRequested)
            {
                Saea.SocketError = SocketError.OperationAborted;
                return AsyncExecutionResult.Cancelled;
            }

            // poll
            if (hasPollableExecutionQueue)
            {
                executionQueue!.AddPollIn(socket.SafeHandle, callback!, state, data);;
                return AsyncExecutionResult.Executing;
            }
            else
            {
                return AsyncExecutionResult.WaitForPoll;
            }
        }

        protected void ResetOperationState()
        {
            _bytesTransferredTotal = 0;
            _bufferIndex = 0;
            _connectCalled = false;
        }
    }
}
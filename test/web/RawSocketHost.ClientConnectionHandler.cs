using System;
using System.Net.Sockets;
using System.Text;
using Socket = Tmds.LinuxAsync.Socket;
using SocketAsyncEventArgs = Tmds.LinuxAsync.SocketAsyncEventArgs;

namespace web
{
    public partial class RawSocketHost<TSocket, TSocketEventArgs, TSocketHandler>
    {
        private class ClientConnectionHandler
        {
            private readonly TSocketHandler _socketHandler;
            private readonly TSocket _socket;
            private readonly byte[] _sendBuffer = Encoding.ASCII.GetBytes(Response);
            private readonly byte[] _receiveBuffer = new byte[BufferSize];
            private int _bytesReceieved;

            private readonly TSocketEventArgs _receiveArgs;
            private readonly TSocketEventArgs _sendArgs;

            public ClientConnectionHandler(TSocket socket, TSocketHandler socketHandler, 
                bool deferSends, bool deferReceives, bool runContinuationsAsynchronously)
            {
                _socket = socket;
                _socketHandler = socketHandler;

                _sendArgs = socketHandler.CreateSocketEventArgs(
                    !deferSends, 
                    runContinuationsAsynchronously,
                    (s, a) => RequestReceive());
                socketHandler.SetBuffer(_sendArgs, _sendBuffer, 0, _sendBuffer.Length);
                
                _receiveArgs = socketHandler.CreateSocketEventArgs(
                    !deferReceives, 
                    runContinuationsAsynchronously,
                    (s, a) => HandleReceive());
            }
            
            public void HandleClient()
            {
                RequestReceive();
            }

            private void RequestReceive()
            {
                try
                {
                    _socketHandler.SetBuffer(_receiveArgs, 
                        _receiveBuffer, 
                        _bytesReceieved,
                        _receiveBuffer.Length - _bytesReceieved);

                    if (!_socketHandler.ReceiveAsync(_socket, _receiveArgs))
                    {
                        HandleReceive();
                    }
                }
                catch
                {
                    Cleanup();
                }
            }

            private void HandleReceive()
            {
                (int byteCount, SocketError error) = _socketHandler.GetStatus(_receiveArgs);

                if (error == SocketError.Success && byteCount != 0)
                {
                    _bytesReceieved += byteCount;
                    if (RequestComplete())
                    {
                        _bytesReceieved = 0;
                        SendResponse();
                    }
                    else
                    {
                        RequestReceive();
                    }
                }
                else
                {
                    Cleanup();
                }
            }
            
            private bool RequestComplete()
            {
                return _bytesReceieved > 4 && _receiveBuffer.AsSpan(_bytesReceieved - 4, 4).SequenceEqual(RequestEnd);
            }

            private void SendResponse()
            {
                try
                {
                    if (!_socketHandler.SendAsync(_socket, _sendArgs))
                    {
                        RequestReceive();
                    }
                }
                catch
                {
                    Cleanup();
                }
            }

            private void Cleanup()
            {
                _receiveArgs.Dispose();
                _sendArgs.Dispose();
                _socket.Dispose();
            }
        }
    }
}
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Tmds.LinuxAsync.Transport;

using Socket = Tmds.LinuxAsync.Socket;
using SocketAsyncEventArgs = Tmds.LinuxAsync.SocketAsyncEventArgs;

namespace web
{
    public class RawSocketHost
    {
        private const int BufferSize = 512;
        private const string Response =
            "HTTP/1.1 200 OK\r\nDate: Tue, 31 Mar 2020 14:49:06 GMT\r\nContent-Type: application/json\r\nServer: Kestrel\r\nContent-Length: 27\r\n\r\n{\"message\":\"Hello, World!\"}";

        private static ReadOnlySpan<byte> RequestEnd => new byte[] {13, 10, 13, 10}; // "\r\n\r\n"
        
        private readonly string[] _args;
        private readonly CommandLineOptions _options;

        private IPEndPoint _serverEndpoint;

        public RawSocketHost(CommandLineOptions options, string[] args)
        {
            _options = options;
            _args = args;
            
            ConfigurationBuilder bld = new ConfigurationBuilder();
            bld.AddCommandLine(_args);
            var cfg = bld.Build();
            string url = cfg["urls"];

            string[] data = url.Substring(7).Split(':');
            IPAddress ip = IPAddress.Parse(data[0]);
            int port = int.Parse(data[1]);

            _serverEndpoint = new IPEndPoint(ip, port);
        }

        public void Run()
        {
            RunAsync().GetAwaiter().GetResult();
        }

        public async Task RunAsync()
        {
            using Socket listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(_serverEndpoint);
            listener.Listen(1024);
            Console.WriteLine($"Raw server listening on {_serverEndpoint}");

            bool deferSends = _options.DeferSends == true;
            bool deferReceives = _options.DeferReceives == true;
            bool runContinuationsAsynchronously =
                _options.SocketContinuationScheduler == SocketContinuationScheduler.ThreadPool;

            while (true)
            {
                Socket handlerSocket = await listener.AcceptAsync();
                
                ClientConnectionHandler clientHandler = 
                    new ClientConnectionHandler(handlerSocket, deferSends, deferReceives, runContinuationsAsynchronously);

                clientHandler.HandleClient();
            }
        }

        class ClientConnectionHandler
        {
            private readonly Socket _socket;
            private readonly byte[] _sendBuffer = Encoding.ASCII.GetBytes(Response);
            private readonly byte[] _receiveBuffer = new byte[BufferSize];
            private int _bytesReceieved;

            private readonly SocketAsyncEventArgs _receiveArgs;
            private readonly SocketAsyncEventArgs _sendArgs;
            
            public ClientConnectionHandler(Socket socket, bool deferSends, bool deferReceives, bool runContinuationsAsynchronously)
            {
                _socket = socket;
                _sendArgs = new SocketAsyncEventArgs()
                {
                    PreferSynchronousCompletion = !deferSends,
                    RunContinuationsAsynchronously = runContinuationsAsynchronously
                };
                _sendArgs.SetBuffer(_sendBuffer);
                _sendArgs.Completed += (s, a) => RequestReceive();

                _receiveArgs = new SocketAsyncEventArgs()
                {
                    PreferSynchronousCompletion = !deferReceives,
                    RunContinuationsAsynchronously = runContinuationsAsynchronously
                };
                
                _receiveArgs.Completed += (s,a) => HandleReceive();
            }
            
            public void HandleClient()
            {
                RequestReceive();
            }

            private void RequestReceive()
            {
                try
                {
                    _receiveArgs.SetBuffer(_receiveBuffer, _bytesReceieved, _receiveBuffer.Length - _bytesReceieved);
                    
                    if (!_socket.ReceiveAsync(_receiveArgs))
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
                int byteCount = _receiveArgs.BytesTransferred;    
                SocketError error = _receiveArgs.SocketError;

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
                    if (!_socket.SendAsync(_sendArgs))
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
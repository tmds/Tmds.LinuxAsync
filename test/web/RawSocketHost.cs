using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Tmds.LinuxAsync.Transport;

using Socket = Tmds.LinuxAsync.Socket;

namespace web
{
    public partial class RawSocketHost<TSocket, TSocketEventArgs, TSocketHandler>
        where TSocket : IDisposable
        where TSocketEventArgs : IDisposable
        where TSocketHandler : struct, ISocketHandler<TSocket, TSocketEventArgs>
    {
        private readonly TSocketHandler _socketHandler;
        private const int BufferSize = 512;
        private const string Response =
            "HTTP/1.1 200 OK\r\nDate: Tue, 31 Mar 2020 14:49:06 GMT\r\nContent-Type: application/json\r\nServer: Kestrel\r\nContent-Length: 27\r\n\r\n{\"message\":\"Hello, World!\"}";

        private static ReadOnlySpan<byte> RequestEnd => new byte[] {13, 10, 13, 10}; // "\r\n\r\n"
        
        private readonly string[] _args;
        private readonly CommandLineOptions _options;

        private IPEndPoint _serverEndpoint;

        public RawSocketHost(CommandLineOptions options, string[] args, TSocketHandler socketHandler)
        {
            _options = options;
            _args = args;
            _socketHandler = socketHandler;

            ConfigurationBuilder bld = new ConfigurationBuilder();
            bld.AddCommandLine(_args);
            var cfg = bld.Build();
            string url = cfg["urls"] ?? cfg["server.urls"];

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
            using TSocket listener = _socketHandler.CreateListenerSocket(_serverEndpoint, 1024);
            Console.WriteLine($"Raw server listening on {_serverEndpoint}");

            bool deferSends = _options.DeferSends == true;
            bool deferReceives = _options.DeferReceives == true;
            bool runContinuationsAsynchronously =
                _options.SocketContinuationScheduler == SocketContinuationScheduler.ThreadPool;

            while (true)
            {
                TSocket handlerSocket = await _socketHandler.AcceptAsync(listener);
                
                ClientConnectionHandler clientHandler = 
                    new ClientConnectionHandler(handlerSocket, _socketHandler, deferSends, deferReceives, runContinuationsAsynchronously);

                clientHandler.HandleClient();
            }
        }

        
    }
}
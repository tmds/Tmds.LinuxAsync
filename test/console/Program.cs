using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Socket = Tmds.LinuxAsync.Socket;
using SocketAsyncEventArgs = Tmds.LinuxAsync.SocketAsyncEventArgs;
using System.Text;

namespace console
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listenSocket.Bind(new IPEndPoint(IPAddress.Any, 0));
            listenSocket.Listen(1);
            var clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await clientSocket.ConnectAsync(listenSocket.LocalEndPoint);
            var acceptedSocket = await listenSocket.AcceptAsync();
            await acceptedSocket.SendAsync(Encoding.UTF8.GetBytes("Hello world!"));
            byte[] receiveBuffer = new byte[1000];
            int bytesReceived = await clientSocket.ReceiveAsync(receiveBuffer);
            Console.WriteLine(Encoding.UTF8.GetString(receiveBuffer, 0, bytesReceived));
        }
    }
}

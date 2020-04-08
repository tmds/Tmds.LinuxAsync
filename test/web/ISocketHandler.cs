using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using SocketAsyncEventArgs = Tmds.LinuxAsync.SocketAsyncEventArgs;

namespace web
{
    public interface ISocketHandler<TSocket, TSocketEventArgs> 
        where TSocket : IDisposable
        where TSocketEventArgs : IDisposable
    {
        TSocket CreateListenerSocket(IPEndPoint endPoint, int backlog);
        Task<TSocket> AcceptAsync(TSocket socket);
        TSocketEventArgs CreateSocketEventArgs(bool preferSynchronousCompletion, bool runContinuationsAsynchronously, EventHandler<TSocketEventArgs> completed);

        void SetBuffer(TSocketEventArgs args, byte[] buffer, int offset, int count);

        bool SendAsync(TSocket socket, TSocketEventArgs args);

        bool ReceiveAsync(TSocket socket, TSocketEventArgs args);

        (int bytesTransferred, SocketError error) GetStatus(TSocketEventArgs args);
    }

    struct SystemNetSocketHandler : ISocketHandler<System.Net.Sockets.Socket, System.Net.Sockets.SocketAsyncEventArgs>
    {
        public Socket CreateListenerSocket(IPEndPoint endPoint, int backlog)
        {
            var socket = new System.Net.Sockets.Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(endPoint);
            socket.Listen(backlog);
            return socket;
        }

        public Task<Socket> AcceptAsync(Socket socket) => socket.AcceptAsync();

        public System.Net.Sockets.SocketAsyncEventArgs CreateSocketEventArgs(bool preferSynchronousCompletion, bool runContinuationsAsynchronously,
            EventHandler<System.Net.Sockets.SocketAsyncEventArgs> completed)
        {
            var result = new System.Net.Sockets.SocketAsyncEventArgs();
            result.Completed += completed;
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetBuffer(System.Net.Sockets.SocketAsyncEventArgs args, byte[] buffer, int offset, int count) 
            => args.SetBuffer(buffer, offset, count);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool SendAsync(Socket socket, System.Net.Sockets.SocketAsyncEventArgs args) 
            => socket.SendAsync(args);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ReceiveAsync(Socket socket, System.Net.Sockets.SocketAsyncEventArgs args) =>
            socket.ReceiveAsync(args);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public (int bytesTransferred, SocketError error) GetStatus(System.Net.Sockets.SocketAsyncEventArgs args)
        {
            return (args.BytesTransferred, args.SocketError);
        }
    }

    struct TmdsSocketHandler : ISocketHandler<Tmds.LinuxAsync.Socket, Tmds.LinuxAsync.SocketAsyncEventArgs>
    {
        public Tmds.LinuxAsync.Socket CreateListenerSocket(IPEndPoint endPoint, int backlog)
        {
            var socket = new Tmds.LinuxAsync.Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(endPoint);
            socket.Listen(backlog);
            return socket;
        }

        public Task<Tmds.LinuxAsync.Socket> AcceptAsync(Tmds.LinuxAsync.Socket socket) => socket.AcceptAsync();

        public SocketAsyncEventArgs CreateSocketEventArgs(bool preferSynchronousCompletion, bool runContinuationsAsynchronously,
            EventHandler<SocketAsyncEventArgs> completed)
        {
            var result = new Tmds.LinuxAsync.SocketAsyncEventArgs()
            {
                PreferSynchronousCompletion = preferSynchronousCompletion,
                RunContinuationsAsynchronously = runContinuationsAsynchronously
            };
            result.Completed += completed;
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetBuffer(SocketAsyncEventArgs args, byte[] buffer, int offset, int count) 
            => args.SetBuffer(buffer, offset, count);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool SendAsync(Tmds.LinuxAsync.Socket socket, SocketAsyncEventArgs args)
            => socket.SendAsync(args);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ReceiveAsync(Tmds.LinuxAsync.Socket socket, SocketAsyncEventArgs args)
            => socket.ReceiveAsync(args);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public (int bytesTransferred, SocketError error) GetStatus(SocketAsyncEventArgs args)
        {
            return (args.BytesTransferred, args.SocketError);
        }
    }
}
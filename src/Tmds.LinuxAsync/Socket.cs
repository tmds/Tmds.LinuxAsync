using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Tmds.LinuxAsync
{
    public sealed class Socket : IDisposable
    {
        private readonly System.Net.Sockets.Socket _innerSocket;
        internal AsyncContext AsyncContext { get; }

        public Socket(AddressFamily addressFamily, SocketType socketType, ProtocolType protocolType)
        {
            _innerSocket = new System.Net.Sockets.Socket(addressFamily, socketType, protocolType);
            AsyncContext = AsyncEngine.SocketEngine.CreateContext(_innerSocket.SafeHandle);
        }

        internal Socket(System.Net.Sockets.Socket innerSocket)
        {
            _innerSocket = innerSocket;
            AsyncContext = AsyncEngine.SocketEngine.CreateContext(_innerSocket.SafeHandle);
        }

        public SafeSocketHandle SafeHandle => _innerSocket.SafeHandle;

        // Delegate to _innerSocket.
        public void Bind(EndPoint localEP) => _innerSocket.Bind(localEP);
        public void Listen(int backlog) => _innerSocket.Listen(backlog);
        public void Connect(EndPoint remoteEP) => _innerSocket.Connect(remoteEP);
        public EndPoint LocalEndPoint  => _innerSocket.LocalEndPoint;

        // Dispose.
        private void Dispose(bool disposing)
        {
            AsyncContext?.Dispose();
            _innerSocket?.Dispose();
        }

        public void Dispose() => Dispose(true);

        ~Socket() => Dispose(false);

        // Operations.
        public bool ReceiveAsync(SocketAsyncEventArgs e)
            => ExecuteAsync(SocketAsyncOperation.Receive, e);
        public bool SendAsync(SocketAsyncEventArgs e)
            => ExecuteAsync(SocketAsyncOperation.Send, e);

        public bool AcceptAsync(SocketAsyncEventArgs e)
            => ExecuteAsync(SocketAsyncOperation.Accept, e);

        public bool ConnectAsync(SocketAsyncEventArgs e)
            => ExecuteAsync(SocketAsyncOperation.Connect, e);

        private bool ExecuteAsync(SocketAsyncOperation operation, SocketAsyncEventArgs e)
        {
            e.StartOperationCommon(this, operation);
            return AsyncContext.ExecuteAsync(e.AsyncOperation);
        }

        public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            AwaitableSocketOperation asyncOperation = RentReadOperation();
            SocketAsyncEventArgs e = asyncOperation.Saea;
            e.SetBuffer(buffer);
            e.StartOperationCommon(this, SocketAsyncOperation.Receive);
            bool pending = AsyncContext.ExecuteAsync(asyncOperation);
            if (pending)
            {
                asyncOperation.RegisterCancellation(cancellationToken);
            }
            return new ValueTask<int>(asyncOperation, 0); // TODO: token
        }

        public ValueTask<int> SendAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            AwaitableSocketOperation asyncOperation = RentReadOperation();
            SocketAsyncEventArgs e = asyncOperation.Saea;
            e.SetBuffer(buffer);
            e.StartOperationCommon(this, SocketAsyncOperation.Send);
            bool pending = AsyncContext.ExecuteAsync(asyncOperation);
            if (pending)
            {
                asyncOperation.RegisterCancellation(cancellationToken);
            }
            return new ValueTask<int>(asyncOperation, 0); // TODO: token
        }

        public ValueTask<int> ConnectAsync(EndPoint endPoint, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            AwaitableSocketOperation asyncOperation = RentReadOperation();
            SocketAsyncEventArgs e = asyncOperation.Saea;
            e.RemoteEndPoint = endPoint;
            e.StartOperationCommon(this, SocketAsyncOperation.Connect);
            bool pending = AsyncContext.ExecuteAsync(asyncOperation);
            if (pending)
            {
                asyncOperation.RegisterCancellation(cancellationToken);
            }
            return new ValueTask<int>(asyncOperation, 0); // TODO: token
        }

        public Task<Socket> AcceptAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            AwaitableSocketOperation asyncOperation = RentReadOperation();
            SocketAsyncEventArgs e = asyncOperation.Saea;
            e.StartOperationCommon(this, SocketAsyncOperation.Accept);
            bool pending = AsyncContext.ExecuteAsync(asyncOperation);
            if (pending)
            {
                asyncOperation.RegisterCancellation(cancellationToken);
            }
            return new ValueTask<Socket>(asyncOperation, 0).AsTask();
        }

        private AwaitableSocketOperation RentReadOperation()
            => AsyncContext.RentReadOperation<AwaitableSocketOperation>();
    }
}

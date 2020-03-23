using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.IO.Pipelines;

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
        public EndPoint LocalEndPoint  => _innerSocket.LocalEndPoint;
        public EndPoint RemoteEndPoint  => _innerSocket.RemoteEndPoint;
        public bool NoDelay { get => _innerSocket.NoDelay; set => _innerSocket.NoDelay = value; }
        public bool DualMode { get => _innerSocket.DualMode; set => _innerSocket.DualMode = value; }
        public void Shutdown(SocketShutdown how) => _innerSocket.Shutdown(how);

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
        {
            var op = e.StartReceiveOperation(this);
            return AsyncContext.ExecuteAsync(op, e.PreferSynchronousCompletion);
        }

        public bool SendAsync(SocketAsyncEventArgs e)
        {
            var op = e.StartSendOperation(this);
            return AsyncContext.ExecuteAsync(op, e.PreferSynchronousCompletion);
        }

        public bool AcceptAsync(SocketAsyncEventArgs e)
        {
            var op = e.StartAcceptOperation(this);
            return AsyncContext.ExecuteAsync(op, e.PreferSynchronousCompletion);
        }

        public bool ConnectAsync(SocketAsyncEventArgs e)
        {
            var op = e.StartConnectOperation(this);
            return AsyncContext.ExecuteAsync(op, e.PreferSynchronousCompletion);
        }

        public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var asyncOperation = AsyncContext.RentReadOperation<AwaitableSocketReceiveOperation>();
            asyncOperation.Configure(this, buffer, bufferList: null);
            bool pending = AsyncContext.ExecuteAsync(asyncOperation);
            if (pending)
            {
                asyncOperation.RegisterCancellation(cancellationToken);
            }
            return new ValueTask<int>(asyncOperation, asyncOperation.Version);
        }

        public ValueTask<int> SendAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var asyncOperation = AsyncContext.RentWriteOperation<AwaitableSocketSendOperation>();
            asyncOperation.Configure(this, buffer, bufferList: null);
            bool pending = AsyncContext.ExecuteAsync(asyncOperation);
            if (pending)
            {
                asyncOperation.RegisterCancellation(cancellationToken);
            }
            return new ValueTask<int>(asyncOperation, asyncOperation.Version);
        }

        public ValueTask ConnectAsync(EndPoint endPoint, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var asyncOperation = AsyncContext.RentWriteOperation<AwaitableSocketConnectOperation>();
            asyncOperation.Configure(this, endPoint);
            bool pending = AsyncContext.ExecuteAsync(asyncOperation);
            if (pending)
            {
                asyncOperation.RegisterCancellation(cancellationToken);
            }
            return new ValueTask(asyncOperation, asyncOperation.Version);
        }

        public Task<Socket> AcceptAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var asyncOperation = AsyncContext.RentReadOperation<AwaitableSocketAcceptOperation>();
            asyncOperation.Configure(this);
            bool pending = AsyncContext.ExecuteAsync(asyncOperation);
            if (pending)
            {
                asyncOperation.RegisterCancellation(cancellationToken);
            }
            return new ValueTask<Socket>(asyncOperation, asyncOperation.Version).AsTask();
        }

        // Sync over Async implementation example.
        public void Connect(EndPoint endPoint, int msTimeout)
        {
            var asyncOperation = AsyncContext.RentWriteOperation<AwaitableSocketConnectOperation>();
            asyncOperation.Configure(this, endPoint);
            bool pending = AsyncContext.ExecuteAsync(asyncOperation);
            if (pending)
            {
                using var mre = new ManualResetEventSlim();
                asyncOperation.SetCompletedEvent(mre);
                bool timedOut = !mre.Wait(msTimeout);
                if (timedOut)
                {
                    asyncOperation.TryCancelAndComplete(OperationStatus.CancelledByTimeout);
                }
                mre.Wait();
            }
            asyncOperation.GetResult(token: asyncOperation.Version);
        }

        public PipeScheduler? IOThreadScheduler => AsyncContext.IOThreadScheduler;
    }
}

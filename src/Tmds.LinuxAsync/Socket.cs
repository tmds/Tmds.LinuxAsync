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
            return AsyncContext.ExecuteAsync(e.AsyncOperation, e.PreferSynchronousCompletion);
        }

        public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            SocketAsyncOperation operation = SocketAsyncOperation.Receive;
            AwaitableSocketOperation asyncOperation = RentAsyncOperation(operation);
            SocketAsyncEventArgs e = asyncOperation.Saea;
            e.SetBuffer(buffer);
            e.StartOperationCommon(this, operation);
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

            SocketAsyncOperation operation = SocketAsyncOperation.Send;
            AwaitableSocketOperation asyncOperation = RentAsyncOperation(operation);
            SocketAsyncEventArgs e = asyncOperation.Saea;
            e.SetBuffer(buffer);
            e.StartOperationCommon(this, operation);
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

            SocketAsyncOperation operation = SocketAsyncOperation.Connect;
            AwaitableSocketOperation asyncOperation = RentAsyncOperation(operation);
            SocketAsyncEventArgs e = asyncOperation.Saea;
            e.RemoteEndPoint = endPoint;
            e.StartOperationCommon(this, operation);
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

            SocketAsyncOperation operation = SocketAsyncOperation.Accept;
            AwaitableSocketOperation asyncOperation = RentAsyncOperation(operation);
            SocketAsyncEventArgs e = asyncOperation.Saea;
            e.StartOperationCommon(this, operation);
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
            SocketAsyncOperation operation = SocketAsyncOperation.Connect;
            AwaitableSocketOperation asyncOperation = RentAsyncOperation(operation);
            SocketAsyncEventArgs e = asyncOperation.Saea;
            e.RemoteEndPoint = endPoint;
            e.StartOperationCommon(this, operation);
            bool pending = AsyncContext.ExecuteAsync(asyncOperation);
            if (pending)
            {
                using var mre = new ManualResetEventSlim();
                asyncOperation.SetCompletedEvent(mre);
                bool timedOut = !mre.Wait(msTimeout);
                if (timedOut)
                {
                    asyncOperation.TryCancelAndComplete(OperationCompletionFlags.CancelledByTimeout);
                }
                mre.Wait();
            }
            asyncOperation.GetResult(token: asyncOperation.Version);
        }

        private AwaitableSocketOperation RentAsyncOperation(SocketAsyncOperation operation)
        {
            if (AsyncSocketOperation.IsOperationReadNotWrite(operation))
            {
                return AsyncContext.RentReadOperation<AwaitableSocketOperation>();
            }
            else
            {
                return AsyncContext.RentWriteOperation<AwaitableSocketOperation>();
            }
        }

        public PipeScheduler? IOThreadScheduler => AsyncContext.IOThreadScheduler;
    }
}

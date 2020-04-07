using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Tmds.LinuxAsync
{
    public partial class SocketAsyncEventArgs : EventArgs, IDisposable
    {
        private static readonly ContextCallback s_executionCallback = ExecutionCallback;

        // Single buffer.
        private Memory<byte> _buffer;
        private int _offset;
        private int _count;
        // private bool _bufferIsExplicitArray;

        IList<ArraySegment<byte>>? _bufferList;

        // Misc state variables.
        private readonly bool _flowExecutionContext;
        private ExecutionContext? _context;
        internal Socket? CurrentSocket { get; set; }
        internal System.Net.Sockets.SocketAsyncOperation CurrentOperation { get; private set; }

        internal void StartOperationCommon(Socket socket, System.Net.Sockets.SocketAsyncOperation operation)
        {
            CurrentSocket = socket;
            CurrentOperation = operation;

            if (_flowExecutionContext)
            {
                _context = ExecutionContext.Capture();
            }
        }

        public SocketFlags SocketFlags { get; set; }
        public SocketError SocketError { get; set; }
        public int SendPacketsSendSize { get; set; }
        public TransmitFileOptions SendPacketsFlags { get; set; }
        public SendPacketsElement[]? SendPacketsElements { get; set; }
        public EndPoint? RemoteEndPoint { get; set; }
        public IPPacketInformation? ReceiveMessageFromPacketInfo { get; }
        public System.Net.Sockets.SocketAsyncOperation LastOperation { get; }
        public bool DisconnectReuseSocket { get; set; }
        public Socket? ConnectSocket { get; }
        public Exception? ConnectByNameError { get; }
        public int BytesTransferred { get; internal set; }
        public IList<ArraySegment<byte>>? BufferList { get => _bufferList; set => _bufferList = value; }
        public byte[]? Buffer { get; }
        public Socket? AcceptSocket { get; set; }
        public object? UserToken { get; set; }

        public Memory<byte> MemoryBuffer => _buffer;
        public int Offset => _offset;
        public int Count => _count;

        public event EventHandler<SocketAsyncEventArgs>? Completed;

        public void Dispose() { }
        public void SetBuffer(Memory<byte> buffer)
        {
            // StartConfiguring();
            // try
            // {
            //     if (buffer.Length != 0 && _bufferList != null)
            //     {
            //         throw new ArgumentException(SR.Format(SR.net_ambiguousbuffers, nameof(BufferList)));
            //     }
            _buffer = buffer;
            _offset = 0;
            _count = buffer.Length;

            // _bufferIsExplicitArray = false;
            // }
            // finally
            // {
            //     Complete();
            // }
        }

        public void SetBuffer(int offset, int count) { throw new NotImplementedException(); }
        public void SetBuffer(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
            {
                // Clear out existing buffer.
                _buffer = default;
                _offset = 0;
                _count = 0;
                // _bufferIsExplicitArray = false;
            }
            else
            {
                _buffer = buffer;
                _offset = offset;
                _count = count;
            }
        }

        protected virtual void OnCompleted(SocketAsyncEventArgs e)
        {
            Completed?.Invoke(e.CurrentSocket, e);
        }

        private static void ExecutionCallback(object? state)
        {
            var thisRef = (SocketAsyncEventArgs)state!;
            thisRef.OnCompleted(thisRef);
        }
    }
}
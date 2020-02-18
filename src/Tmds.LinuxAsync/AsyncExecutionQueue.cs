using System;
using System.Runtime.InteropServices;

namespace Tmds.LinuxAsync
{
    struct AsyncOperationResult
    {
        public AsyncOperationResult(long res)
        {
            _result = res;
        }

        public static AsyncOperationResult NoResult => new AsyncOperationResult(long.MinValue);

        private long _result;

        public bool HasResult => _result != long.MinValue;

        public bool IsError
        {
            get
            {
                VerifyHasResult();
                return _result < 0;
            }
        }

        public bool IsCancelledError
        {
            get
            {
                VerifyHasResult();
                return _result == -Tmds.Linux.LibC.ECANCELED;
            }
        }

        public int Errno
        {
            get
            {
                VerifyHasResult();
                long v = _result;
                if (v < 0)
                {
                    return (int)-v;
                }
                else
                {
                    return 0;
                }
            }
        }

        public long Value
        {
            get
            {
                long v = _result;
                if (v < 0)
                {
                    ThrowHelper.ThrowInvalidOperationException();
                }
                return v;
            }
        }

        public int IntValue => (int)Value;

        private void VerifyHasResult()
        {
            if (!HasResult)
            {
                ThrowHelper.ThrowInvalidOperationException();
            }
        }
    }

    delegate void AsyncExecutionCallback(AsyncOperationResult result, object? state, int data);

    // Supports batching operations for execution.
    abstract class AsyncExecutionQueue : IDisposable
    {
        // Add a read.
        public abstract void AddRead(SafeHandle handle, Memory<byte> memory, AsyncExecutionCallback callback, object? state, int data);
        // Add a write.
        public abstract void AddWrite(SafeHandle handle, Memory<byte> memory, AsyncExecutionCallback callback, object? state, int data);
        // Add a poll in.
        public abstract void AddPollIn(SafeHandle handle, AsyncExecutionCallback asyncExecutionCallback, object? state, int data);
        // Add a poll out.
        public abstract void AddPollOut(SafeHandle handle, AsyncExecutionCallback asyncExecutionCallback, object? state, int data);

        // Indicates support for PollIn/Out and 0-byte reads.
        public bool SupportsPolling { get; }

        protected AsyncExecutionQueue(bool supportsPolling)
        {
            SupportsPolling = supportsPolling;
        }

        abstract protected void Dispose(bool disposing);

        ~AsyncExecutionQueue()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
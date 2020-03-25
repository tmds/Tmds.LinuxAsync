using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks.Sources;

namespace Tmds.LinuxAsync
{
    struct AwaitableSocketOperationCore<T>
    {
        // TODO: move
        private static readonly ManualResetEventSlim s_completedSentinel = new ManualResetEventSlim();

        public void Init()
        {
            // Use ThreadPool when there is no other ExecutionContext.
            _vts.RunContinuationsAsynchronously = true;
        }

        public void SetCompletedEvent(ManualResetEventSlim mre)
        {
            if (Interlocked.CompareExchange(ref _mre, mre, null) != null)
            {
                // Already completed.
                _mre.Set();
            }
        }

        public void RegisterCancellation(CancellationToken cancellationToken)
        {
            _ctr = cancellationToken.UnsafeRegister(s =>
            {
                var operation = (AsyncOperation)s!;
                operation.TryCancelAndComplete(OperationStatus.CancelledByToken);
            }, this);
        }

        public ValueTaskSourceStatus GetStatus(short token)
            => _vts.GetStatus(token);

        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            => _vts.OnCompleted(continuation, state, token, flags);

        public void Reset()
        {
            _vts.Reset();
            _ctr.Dispose();
            _mre = null;
        }

        public void SetResult(T result, SocketError socketError, OperationStatus status)
        {
            _socketError = socketError;
            _status = status;
            _vts.SetResult(result);

            ManualResetEventSlim? mre = Interlocked.Exchange(ref _mre, s_completedSentinel);
            // This ManualResetEventSlim is used to wait until the operation completed.
            // After that a direct call is made to get the result.
            mre?.Set();
        }

        public struct Result
        {
            private T _value;
            private SocketError _socketError;
            private OperationStatus _status;

            public Result(T value, SocketError socketError, OperationStatus status)
            {
                _value = value;
                _socketError = socketError;
                _status = status;
            }

            public T GetValue()
            {
                if (_socketError != System.Net.Sockets.SocketError.Success)
                {
                    bool cancelledByToken = (_status & OperationStatus.CancelledByToken) != 0;
                    if (cancelledByToken)
                    {
                        throw new OperationCanceledException();
                    }
                    bool cancelledByTimeout = (_status & OperationStatus.CancelledByTimeout) != 0;
                    if (cancelledByTimeout)
                    {
                        _socketError = SocketError.TimedOut;
                    }
                    throw new SocketException((int)_socketError);
                }

                return _value;
            }
        }

        public Result GetResult(short token)
        {
            T value = _vts.GetResult(token);
            return new Result(value, _socketError, _status);
        }

        private ManualResetValueTaskSourceCore<T> _vts;
        private CancellationTokenRegistration _ctr;
        private ManualResetEventSlim? _mre;
        private SocketError _socketError;
        private OperationStatus _status;

        public short Version => _vts.Version;
    }
}
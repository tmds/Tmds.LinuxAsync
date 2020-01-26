using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using static Tmds.Linux.LibC;

namespace Tmds.LinuxAsync
{
    public partial class EPollAsyncEngine
    {
        sealed class EPollAsyncContext : AsyncContext
        {
            enum State
            {
                Unregistered = 0,
                Registered = 1,
                Disposed = 8
            }

            private readonly object _readGate = new object();
            private readonly object _writeGate = new object();
            private AsyncOperation? _writeTail;
            private AsyncOperation? _readTail;
            private readonly EPollThread _epoll;
            private SafeHandle? _handle;
            private int _fd;
            private State _state;
            private bool _setToNonBlocking;

            public int Key => _fd;

            public EPollAsyncContext(EPollThread thread, SafeHandle handle)
            {
                _epoll = thread;
                bool success = false;
                handle.DangerousAddRef(ref success);
                _fd = handle.DangerousGetHandle().ToInt32();
                _handle = handle;
            }

            public override void Dispose()
            {
                AsyncOperation? readTail;
                AsyncOperation? writeTail;

                lock (_readGate)
                    lock (_writeGate)
                    {
                        if (_state == State.Disposed)
                        {
                            return;
                        }
                        _state = State.Disposed;

                        _epoll.RemoveContext(Key);

                        readTail = _readTail;
                        _readTail = null;
                        writeTail = _writeTail;
                        _writeTail = null;

                        if (_handle != null)
                        {
                            _handle.DangerousRelease();
                            _fd = -1;
                            _handle = null;
                        }
                    }

                CompleteOperationsCancelled(ref readTail);
                CompleteOperationsCancelled(ref writeTail);

                static void CompleteOperationsCancelled(ref AsyncOperation? tail)
                {
                    while (TryQueueTakeFirst(ref tail, out AsyncOperation? op))
                    {
                        op.Complete(OperationCompletionFlags.CompletedCanceled);
                    }
                }
            }

            public void HandleEvents(int events)
            {
                // Pick up the error by reading and writing.
                if ((events & EPOLLERR) != 0)
                {
                    events |= POLLIN | POLLOUT;
                }

                AsyncOperation? completedTail = null;

                bool configureEpoll = false;

                // Try reading and writing.
                bool tryReading = (events & POLLIN) != 0;
                if (tryReading)
                {
                    TryExecuteQueuedOperations(_readGate, ref _readTail, ref completedTail, ref configureEpoll);
                }
                bool tryWriting = (events & POLLOUT) != 0;
                if (tryWriting)
                {
                    TryExecuteQueuedOperations(_writeGate, ref _writeTail, ref completedTail, ref configureEpoll);
                }

                // Complete operations.
                while (TryQueueTakeFirst(ref completedTail, out AsyncOperation? completedOp))
                {
                    completedOp.Complete(OperationCompletionFlags.CompletedFinishedAsync);
                }

                if (configureEpoll)
                {
                    ConfigureEpoll();
                }

                static void TryExecuteQueuedOperations(object gate, ref AsyncOperation? tail, ref AsyncOperation? completedTail, ref bool configureEpoll)
                {
                    lock (gate)
                    {
                        AsyncOperation? op = QueueGetFirst(tail);
                        while (op != null)
                        {
                            if (op.TryExecute())
                            {
                                QueueRemove(ref tail, op);
                                QueueAdd(ref completedTail, op);
                                op = QueueGetFirst(tail);
                            }
                            else
                            {
                                break;
                            }
                        }
                        configureEpoll = tail != null;
                    }
                }
            }

            public override bool ExecuteAsync(AsyncOperation operation)
            {
                EnsureNonBlocking();

                bool executed;
                bool configureEpoll;

                if (operation.IsReadNotWrite)
                {
                    lock (_readGate)
                    {
                        if (_state == State.Disposed)
                        {
                            ThrowHelper.ThrowObjectDisposedException<AsyncContext>();
                        }

                        operation.CurrentAsyncContext = this;

                        executed = false;
                        configureEpoll = _readTail == null;

                        QueueAdd(ref _readTail, operation);
                    }
                }
                else
                {
                    lock (_writeGate)
                    {
                        if (_state == State.Disposed)
                        {
                            ThrowHelper.ThrowObjectDisposedException<AsyncContext>();
                        }

                        operation.CurrentAsyncContext = this;
                        executed = _writeTail == null && operation.TryExecute();

                        if (executed)
                        {
                            configureEpoll = false;
                        }
                        else
                        {
                            configureEpoll = _writeTail == null;

                            QueueAdd(ref _writeTail, operation);
                        }
                    }
                }

                if (executed)
                {
                    operation.Complete(OperationCompletionFlags.CompletedFinishedSync);
                }
                if (configureEpoll)
                {
                    ConfigureEpoll();
                }

                return !executed;
            }

            private void EnsureNonBlocking()
            {
                SafeHandle? handle = _handle;
                if (handle == null)
                {
                    // We've been disposed.
                    return;
                }

                if (!_setToNonBlocking)
                {
                    SocketPal.SetNonBlocking(handle);
                    _setToNonBlocking = true;
                }
            }

            private void ConfigureEpoll()
            {
                // Lock to avoid concurrent running with Dispose.
                lock (_readGate)
                    lock (_writeGate)
                    {
                        State state = _state;
                        if (_state == State.Disposed)
                        {
                            ThrowHelper.ThrowObjectDisposedException<AsyncContext>();
                        }

                        bool wasRegistered = _state == State.Registered;

                        int controlEvents = EPOLLONESHOT |
                                            (_readTail != null ? EPOLLIN : 0) |
                                            (_writeTail != null ? EPOLLOUT : 0);

                        _epoll.Control(wasRegistered ? EPOLL_CTL_MOD : EPOLL_CTL_ADD,
                                            _fd,
                                            controlEvents | EPOLLONESHOT,
                                            _fd);

                        if (!wasRegistered)
                        {
                            _state = State.Registered;
                        }
                    }
            }

            internal override bool TryCancelAndComplete(AsyncOperation operation, OperationCompletionFlags flags)
            {
                bool found = false;

                if (operation.IsReadNotWrite)
                {
                    lock (_readGate)
                    {
                        found = QueueRemove(ref _readTail, operation);
                    }
                }
                else
                {
                    lock (_writeGate)
                    {
                        found = QueueRemove(ref _writeTail, operation);
                    }
                }

                if (found)
                {
                    operation.Complete(OperationCompletionFlags.CompletedCanceled | flags);
                }

                return found;
            }

            // Queue operations.
            private static bool TryQueueTakeFirst(ref AsyncOperation? tail, [NotNullWhen(true)]out AsyncOperation? first)
            {
                first = tail?.Next;
                if (first != null)
                {
                    QueueRemove(ref tail, first);
                    return true;
                }
                else
                {
                    return false;
                }
            }

            private static AsyncOperation? QueueGetFirst(AsyncOperation? tail)
                => tail?.Next;

            private static void QueueAdd(ref AsyncOperation? tail, AsyncOperation operation)
            {
                Debug.Assert(operation.Next == null);
                operation.Next = operation;

                if (tail != null)
                {
                    operation.Next = tail.Next;
                    tail.Next = operation;
                }

                tail = operation;
            }

            private static bool QueueRemove(ref AsyncOperation? tail, AsyncOperation operation)
            {
                AsyncOperation? tail_ = tail;
                if (tail_ == null)
                {
                    return false;
                }

                if (tail_ == operation)
                {
                    if (tail_.Next == operation)
                    {
                        tail = null;
                    }
                    else
                    {
                        AsyncOperation newTail = tail_.Next!;
                        AsyncOperation newTailNext = newTail.Next!;
                        while (newTailNext != tail_)
                        {
                            newTail = newTailNext;
                        }
                        tail = newTail;
                    }

                    operation.Next = null;
                    return true;
                }
                else
                {
                    AsyncOperation it = tail_;
                    do
                    {
                        AsyncOperation next = it.Next!;
                        if (next == operation)
                        {
                            it.Next = next.Next;
                            operation.Next = null;
                            return true;
                        }
                        it = next;
                    } while (it != tail_);
                }

                return false;
            }
        }
    }
}
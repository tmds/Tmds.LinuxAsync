using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Threading;
using static Tmds.Linux.LibC;

namespace Tmds.LinuxAsync
{
    public partial class EPollAsyncEngine
    {
        sealed class EPollAsyncContext : AsyncContext
        {
            sealed class AsyncOperationSentinel : AsyncOperation
            {
                public override bool IsReadNotWrite => throw new System.InvalidOperationException();

                public override void Complete()
                {
                    throw new System.InvalidOperationException();
                }

                public override AsyncExecutionResult TryExecute(bool isSync, AsyncExecutionQueue? executionQueue, AsyncExecutionCallback? callback, object? state, int data, AsyncOperationResult? result)
                {
                    throw new System.InvalidOperationException();
                }
            }

            private static readonly AsyncOperationSentinel DisposedSentinel = new AsyncOperationSentinel();

            private readonly object _readGate = new object();
            private readonly object _writeGate = new object();
            private AsyncOperation? _writeTail;
            private AsyncOperation? _readTail;
            private readonly EPollThread _epoll;
            private SafeHandle? _handle;
            private int _fd;
            private bool _setToNonBlocking;
            private int _eventCounter;

            public int Key => _fd;

            public EPollAsyncContext(EPollThread thread, SafeHandle handle)
            {
                _epoll = thread;
                bool success = false;
                handle.DangerousAddRef(ref success);
                _fd = handle.DangerousGetHandle().ToInt32();
                _handle = handle;

                _epoll.Control(EPOLL_CTL_ADD, _fd, EPOLLIN | EPOLLOUT | EPOLLET, Key);
            }

            public override void Dispose()
            {
                AsyncOperation? readTail;
                AsyncOperation? writeTail;

                lock (_readGate)
                {
                    readTail = _readTail;

                    // Already disposed?
                    if (readTail == DisposedSentinel)
                    {
                        return;
                    }

                    _readTail = DisposedSentinel;
                }
                lock (_writeGate)
                {
                    writeTail = _writeTail;
                    _writeTail = DisposedSentinel;
                }

                CompleteOperationsCancelled(ref readTail);
                CompleteOperationsCancelled(ref writeTail);

                _epoll.RemoveContext(Key);

                if (_handle != null)
                {
                    _handle.DangerousRelease();
                    _fd = -1;
                    _handle = null;
                }

                static void CompleteOperationsCancelled(ref AsyncOperation? tail)
                {
                    while (TryQueueTakeFirst(ref tail, out AsyncOperation? op))
                    {
                        op.CompletionFlags = OperationCompletionFlags.CompletedCanceled;
                        op.Complete();
                    }
                }
            }

            public void ExecuteQueuedOperations(int events, bool triggeredByPoll, AsyncExecutionQueue? executionQueue, AsyncOperationResult? asyncResult = default)
            {
                // Pick up the error by reading and writing.
                if ((events & EPOLLERR) != 0)
                {
                    events |= POLLIN | POLLOUT;
                }

                AsyncOperation? completedTail = null;

                // Try reading and writing.
                bool tryReading = (events & POLLIN) != 0;
                if (tryReading)
                {
                    lock (_readGate)
                    {
                        _eventCounter++;

                        if (_readTail is { } && _readTail != DisposedSentinel)
                        {
                            AsyncOperation? op = QueueGetFirst(_readTail);
                            while (op != null)
                            {
                                // We're executing and waiting for an async result.
                                if (op.IsExecuting && !asyncResult.HasValue)
                                {
                                    break;
                                }

                                AsyncExecutionResult result = op.TryExecute(triggeredByPoll, executionQueue,
                                    (AsyncExecutionQueue queue, AsyncOperationResult aResult, object? state, int data)
                                        => ((EPollAsyncContext)state!).ExecuteQueuedOperations(POLLIN, triggeredByPoll: false, queue, aResult)
                                , state: this, data: 0, asyncResult);

                                op.IsExecuting = result == AsyncExecutionResult.Executing;

                                // Operation finished, set CompletionFlags.
                                if (result == AsyncExecutionResult.Finished)
                                {
                                    op.CompletionFlags = OperationCompletionFlags.CompletedFinishedAsync;
                                }
                                // Operation got cancelled during execution.
                                if (result == AsyncExecutionResult.WouldBlock && op.IsCancellationRequested)
                                {
                                    Debug.Assert((op.CompletionFlags & OperationCompletionFlags.OperationCancelled) != 0);
                                    result = AsyncExecutionResult.Finished;
                                }

                                if (result == AsyncExecutionResult.Finished)
                                {
                                    QueueRemove(ref _readTail, op);
                                    QueueAdd(ref completedTail, op);
                                    op = QueueGetFirst(_readTail);
                                    continue;
                                }
                                break;
                            }
                        }
                    }
                }
                bool tryWriting = (events & POLLOUT) != 0;
                if (tryWriting)
                {
                    lock (_writeGate)
                    {
                        _eventCounter++;

                        if (_writeTail is { } && _writeTail != DisposedSentinel)
                        {
                            AsyncOperation? op = QueueGetFirst(_writeTail);
                            while (op != null)
                            {
                                // We're executing and waiting for an async result.
                                if (op.IsExecuting && !asyncResult.HasValue)
                                {
                                    break;
                                }

                                AsyncExecutionResult result = op.TryExecute(triggeredByPoll, executionQueue,
                                    (AsyncExecutionQueue queue, AsyncOperationResult aResult, object? state, int data)
                                        => ((EPollAsyncContext)state!).ExecuteQueuedOperations(POLLOUT, triggeredByPoll: false, queue, aResult)
                                , state: this, data: 0, asyncResult);

                                op.IsExecuting = result == AsyncExecutionResult.Executing;

                                // Operation finished, set CompletionFlags.
                                if (result == AsyncExecutionResult.Finished)
                                {
                                    op.CompletionFlags = OperationCompletionFlags.CompletedFinishedAsync;
                                }
                                // Operation got cancelled during execution.
                                if (result == AsyncExecutionResult.WouldBlock && op.IsCancellationRequested)
                                {
                                    Debug.Assert((op.CompletionFlags & OperationCompletionFlags.OperationCancelled) != 0);
                                    result = AsyncExecutionResult.Finished;
                                }

                                if (result == AsyncExecutionResult.Finished)
                                {
                                    QueueRemove(ref _writeTail, op);
                                    QueueAdd(ref completedTail, op);
                                    op = QueueGetFirst(_writeTail);
                                    continue;
                                }
                                break;
                            }
                        }
                    }
                }

                // Complete operations.
                while (TryQueueTakeFirst(ref completedTail, out AsyncOperation? completedOp))
                {
                    completedOp.Complete();
                }
            }

            public override bool ExecuteAsync(AsyncOperation operation, bool preferSync)
            {
                EnsureNonBlocking();

                try
                {
                    operation.CurrentAsyncContext = this;

                    bool executed = false;

                    if (operation.IsReadNotWrite)
                    {
                        int? eventCounterSnapshot = null;

                        // Try executing without a lock.
                        if (preferSync && Volatile.Read(ref _readTail) == null)
                        {
                            eventCounterSnapshot = Volatile.Read(ref _eventCounter);
                            executed = operation.TryExecuteSync();
                        }

                        if (!executed)
                        {
                            bool postCheck = false;
                            lock (_readGate)
                            {
                                if (_readTail == DisposedSentinel)
                                {
                                    ThrowHelper.ThrowObjectDisposedException<AsyncContext>();
                                }

                                bool isQueueEmpty = _readTail == null;

                                // Execute under lock.
                                if (isQueueEmpty && preferSync && _eventCounter != eventCounterSnapshot)
                                {
                                    executed = operation.TryExecuteSync();
                                }

                                if (!executed)
                                {
                                    QueueAdd(ref _readTail, operation);
                                    postCheck = isQueueEmpty && !preferSync;
                                }
                            }
                            if (postCheck)
                            {
                                _epoll.Post((EPollThread thread, EPollAsyncContext? context) => thread.ExecuteQueuedOperations(POLLIN, context!), this);
                            }
                        }
                    }
                    else
                    {
                        int? eventCounterSnapshot = null;

                        // Try executing without a lock.
                        if (preferSync && Volatile.Read(ref _writeTail) == null)
                        {
                            eventCounterSnapshot = Volatile.Read(ref _eventCounter);
                            executed = operation.TryExecuteSync();
                        }

                        if (!executed)
                        {
                            bool postCheck = false;
                            lock (_writeGate)
                            {
                                if (_writeTail == DisposedSentinel)
                                {
                                    ThrowHelper.ThrowObjectDisposedException<AsyncContext>();
                                }

                                bool isQueueEmpty = _writeTail == null;

                                // Execute under lock.
                                if (isQueueEmpty && preferSync && _eventCounter != eventCounterSnapshot)
                                {
                                    executed = operation.TryExecuteSync();
                                }

                                if (!executed)
                                {
                                    QueueAdd(ref _writeTail, operation);
                                    postCheck = isQueueEmpty && !preferSync;
                                }
                            }
                            if (postCheck)
                            {
                                _epoll.Post((EPollThread thread, EPollAsyncContext? context) => thread.ExecuteQueuedOperations(POLLOUT, context!), this);
                            }
                        }
                    }

                    if (executed)
                    {
                        operation.CompletionFlags = OperationCompletionFlags.CompletedFinishedSync;
                        operation.Complete();
                    }

                    return !executed;
                }
                catch
                {
                    operation.Next = null;

                    bool cancelled = operation.RequestCancellationAsync(OperationCompletionFlags.CompletedCanceledSync);
                    Debug.Assert(cancelled);
                    if (cancelled)
                    {
                        operation.Complete();
                    }

                    throw;
                }
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

            internal override void TryCancelAndComplete(AsyncOperation operation, OperationCompletionFlags flags)
            {
                bool cancelled = false;

                if (operation.IsReadNotWrite)
                {
                    lock (_readGate)
                    {
                        cancelled = operation.RequestCancellationAsync(OperationCompletionFlags.CompletedCanceled | flags);
                        if (cancelled)
                        {
                            QueueRemove(ref _readTail, operation);
                        }
                    }
                }
                else
                {
                    lock (_writeGate)
                    {
                        cancelled = operation.RequestCancellationAsync(OperationCompletionFlags.CompletedCanceled | flags);
                        if (cancelled)
                        {
                            QueueRemove(ref _writeTail, operation);
                        }
                    }
                }

                if (cancelled)
                {
                    operation.Complete();
                }
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
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using static Tmds.Linux.LibC;

namespace Tmds.LinuxAsync
{
    public partial class EPollAsyncEngine
    {
        sealed class Queue
        {
            private readonly EPollThread _thread;
            private int _eventCounter;

            public Queue(EPollThread thread)
            {
                _thread = thread;
            }

            private object Gate => this;
            private AsyncOperation? _tail;

            public bool Dispose()
            {
                AsyncOperation? tail;
                lock (Gate)
                {
                    tail = _tail;

                    // Already disposed?
                    if (tail == AsyncOperation.DisposedSentinel)
                    {
                        return false;
                    }

                    _tail = AsyncOperation.DisposedSentinel;
                }

                CompleteOperationsCancelled(ref tail);

                return true;

                static void CompleteOperationsCancelled(ref AsyncOperation? tail)
                {
                    while (TryQueueTakeFirst(ref tail, out AsyncOperation? op))
                    {
                        op.CompletionFlags = OperationCompletionFlags.CompletedCanceled;
                        op.Complete();
                    }
                }
            }

            public void ExecuteQueued(bool triggeredByPoll, AsyncOperationResult asyncResult)
            {
                AsyncOperation? completedTail = null;

                lock (Gate)
                {
                    _eventCounter++;

                    if (_tail is { } && _tail != AsyncOperation.DisposedSentinel)
                    {
                        AsyncOperation? op = QueueGetFirst(_tail);
                        while (op != null)
                        {
                            // We're executing and waiting for an async result.
                            if (op.IsExecuting && !asyncResult.HasResult)
                            {
                                break;
                            }

                            AsyncExecutionResult result = op.TryExecute(triggeredByPoll, _thread.ExecutionQueue,
                                    (AsyncOperationResult aResult, object? state, int data)
                                        => ((Queue)state!).ExecuteQueued(triggeredByPoll: false, aResult)
                                    , state: this, data: 0, asyncResult);
                            // Operation finished, set CompletionFlags.
                            if (result == AsyncExecutionResult.Finished)
                            {
                                op.CompletionFlags = OperationCompletionFlags.CompletedFinishedAsync;
                            }
                            // Operation cancellation requested during execution.
                            else if (result == AsyncExecutionResult.WaitForPoll && op.IsCancellationRequested)
                            {
                                Debug.Assert((op.CompletionFlags & OperationCompletionFlags.OperationCancelled) != 0);
                                result = AsyncExecutionResult.Finished;
                            }
                            op.IsExecuting = result == AsyncExecutionResult.Executing;

                            if (result == AsyncExecutionResult.Finished)
                            {
                                QueueRemove(ref _tail, op);
                                QueueAdd(ref completedTail, op);
                                op = QueueGetFirst(_tail);
                                continue;
                            }
                            break;
                        }
                    }
                }

                // Complete operations.
                while (TryQueueTakeFirst(ref completedTail, out AsyncOperation? completedOp))
                {
                    completedOp.Complete();
                }
            }

            public bool ExecuteAsync(AsyncOperation operation, bool preferSync)
            {
                bool executed = false;
                int? eventCounterSnapshot = null;

                // Try executing without a lock.
                if (preferSync && Volatile.Read(ref _tail) == null)
                {
                    eventCounterSnapshot = Volatile.Read(ref _eventCounter);
                    executed = operation.TryExecuteSync();
                }

                if (!executed)
                {
                    bool postCheck = false;
                    lock (Gate)
                    {
                        if (_tail == AsyncOperation.DisposedSentinel)
                        {
                            ThrowHelper.ThrowObjectDisposedException<AsyncContext>();
                        }

                        bool isQueueEmpty = _tail == null;

                        // Execute under lock.
                        if (isQueueEmpty && preferSync && _eventCounter != eventCounterSnapshot)
                        {
                            executed = operation.TryExecuteSync();
                        }

                        if (!executed)
                        {
                            QueueAdd(ref _tail, operation);
                            postCheck = isQueueEmpty && !preferSync;
                        }
                    }
                    if (postCheck)
                    {
                        _thread.Post((object? s) => ((Queue)s!).ExecuteQueued(triggeredByPoll: false, AsyncOperationResult.NoResult), this);
                    }
                }

                if (executed)
                {
                    operation.CompletionFlags = OperationCompletionFlags.CompletedFinishedSync;
                    operation.Complete();
                }

                return !executed;
            }

            public void TryCancelAndComplete(AsyncOperation operation, OperationCompletionFlags flags)
            {
                bool cancelled = false;

                lock (Gate)
                {
                    cancelled = operation.RequestCancellationAsync(OperationCompletionFlags.CompletedCanceled | flags);
                    if (cancelled)
                    {
                        QueueRemove(ref _tail, operation);
                    }
                }

                if (cancelled)
                {
                    operation.Complete();
                }
            }

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
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using static Tmds.Linux.LibC;

namespace Tmds.LinuxAsync
{
    public partial class IOUringAsyncEngine
    {
        sealed class Queue : AsyncOperationQueueBase
        {
            private readonly IOUringThread _thread;

            public Queue(IOUringThread thread)
            {
                _thread = thread;
            }

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

            public void ExecuteQueued(AsyncOperationResult asyncResult)
            {
                AsyncOperation? completedTail = null;

                lock (Gate)
                {
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

                            AsyncExecutionResult result;
                            if (asyncResult.HasResult && asyncResult.IsCancelledError)
                            {
                                // Operation got cancelled during execution.
                                result = AsyncExecutionResult.Finished;
                            }
                            else
                            {
                                int data = op.IsReadNotWrite ? POLLIN : POLLOUT;
                                result = op.TryExecute(triggeredByPoll: false, _thread.ExecutionQueue,
                                    (AsyncOperationResult aResult, object? state, int data)
                                        => ((Queue)state!).ExecuteQueued(aResult)
                                , state: this, data, asyncResult);
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

                // Try executing without a lock.
                if (preferSync && Volatile.Read(ref _tail) == null)
                {
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

                        postCheck = _tail == null;
                        QueueAdd(ref _tail, operation);
                    }
                    if (postCheck)
                    {
                        _thread.Post((object? s) => ((Queue)s!).ExecuteQueued(AsyncOperationResult.NoResult), this);
                    }
                }

                if (executed)
                {
                    operation.CompletionFlags = OperationCompletionFlags.CompletedFinishedSync;
                    operation.Complete();
                }

                return !executed;
            }
        }
    }
}
using System.Diagnostics;
using System.Threading;

namespace Tmds.LinuxAsync
{
    public partial class EPollAsyncEngine
    {
        sealed class Queue : AsyncOperationQueueBase
        {
            private readonly EPollThread _thread;
            private int _eventCounter;

            public Queue(EPollThread thread)
            {
                _thread = thread;
            }

            public bool Dispose()
            {
                // TODO: handle cancellation and wait for execution to finish.
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

                            bool isCancellationRequested = op.IsCancellationRequested;

                            AsyncExecutionResult result = op.TryExecute(triggeredByPoll, isCancellationRequested, _thread.ExecutionQueue,
                                    (AsyncOperationResult aResult, object? state, int data)
                                        => ((Queue)state!).ExecuteQueued(triggeredByPoll: false, aResult)
                                    , state: this, data: 0, asyncResult);

                            if (isCancellationRequested)
                            {
                                Debug.Assert(result == AsyncExecutionResult.Finished || result == AsyncExecutionResult.Cancelled);
                            }
                            // Operation finished, set CompletionFlags.
                            if (result == AsyncExecutionResult.Finished)
                            {
                                op.CompletionFlags = OperationCompletionFlags.CompletedFinishedAsync;
                            }
                            else if (result == AsyncExecutionResult.Cancelled)
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
                CancellationRequestResult result;

                lock (Gate)
                {
                    if (operation.IsCancellationRequested)
                    {
                        return;
                    }

                    result = RequestCancellationAsync(operation, flags);
                }

                if (result == CancellationRequestResult.Cancelled)
                {
                    operation.Complete();
                }
            }
        }
    }
}
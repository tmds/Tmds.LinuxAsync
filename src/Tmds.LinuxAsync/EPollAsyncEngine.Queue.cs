using System;
using System.Diagnostics;
using System.Threading;

namespace Tmds.LinuxAsync
{
    public partial class EPollAsyncEngine
    {
        sealed class Queue : AsyncOperationQueueBase, IAsyncExecutionResultHandler
        {
            private readonly EPollThread _thread;
            private int _eventCounter;
            private AsyncOperation? _executingOperation;

            public Queue(EPollThread thread)
            {
                _thread = thread;
            }

            public bool Dispose()
            {
                AsyncOperation? queue = Interlocked.Exchange(ref _queue, DisposedSentinel);

                // already disposed
                if (queue == DisposedSentinel)
                {
                    return false;
                }

                if (queue != null)
                {
                    AsyncOperation? gate = queue as AsyncOperationGate;
                    if (gate != null)
                    {
                        // Synchronize with Enqueue.
                        lock (gate)
                        { }

                        AsyncOperation? last = gate.Next;
                        if (last != null)
                        {
                            AsyncOperation op = gate.Next!;
                            do
                            {
                                AsyncOperation next = op.Next!;

                                op.Next = op; // point to self.
                                TryCancelAndComplete(op, OperationStatus.None, wait: true);

                                if (op == last)
                                {
                                    break;
                                }
                                op = next;
                            } while (true);
                        }
                    }
                    else
                    {
                        // queue is single operation
                        TryCancelAndComplete(queue, OperationStatus.None, wait: true);
                    }
                }

                return true;
            }

            void IAsyncExecutionResultHandler.HandleAsyncResult(AsyncOperationResult aResult)
            {
                AsyncOperation? op = _executingOperation!;

                AsyncExecutionResult result = op.HandleAsyncResult(aResult);

                if (result != AsyncExecutionResult.Finished && op.IsCancellationRequested)
                {
                    result = AsyncExecutionResult.Cancelled;
                }

                if (result == AsyncExecutionResult.Executing)
                {
                    result = op.TryExecuteEpollAsync(triggeredByPoll: false, _thread.ExecutionQueue, this);
                    if (result == AsyncExecutionResult.Executing)
                    {
                        return;
                    }
                }

                _executingOperation = null;

                AsyncOperation? next = CompleteOperationAndGetNext(op, result);

                if (next != null)
                {
                    ExecuteQueued(triggeredByPoll: false, next);
                }
            }

            private AsyncOperation? CompleteOperationAndGetNext(AsyncOperation op, AsyncExecutionResult result)
            {
                if (result == AsyncExecutionResult.Finished)
                {
                    op.Status = OperationStatus.Completed;
                }
                else // AsyncExecutionResult.WaitForPoll or Cancelled
                {
                    if (result == AsyncExecutionResult.WaitForPoll)
                    {
                        OperationStatus previous = op.CompareExchangeStatus(OperationStatus.Queued, OperationStatus.Executing);
                        if (previous == OperationStatus.Executing)
                        {
                            // We've changed from executing to queued.
                            return null;
                        }
                        Debug.Assert((previous & OperationStatus.CancellationRequested) != 0);
                    }
                    op.Status = (op.Status & ~(OperationStatus.CancellationRequested | OperationStatus.Executing)) | OperationStatus.Cancelled;
                }

                AsyncOperation? next = DequeueFirstAndGetNext(op);

                op.Complete();

                return next;
            }

            public void ExecuteQueued(bool triggeredByPoll, AsyncOperation? op = null)
            {
                if (triggeredByPoll)
                {
                    Interlocked.Increment(ref _eventCounter);
                }

                op ??= QueueGetFirst();
                do
                {
                    var spin = new SpinWait();
                    while (true)
                    {
                        if (op is null)
                        {
                            return;
                        }
                        OperationStatus previous = op.CompareExchangeStatus(OperationStatus.Executing, OperationStatus.Queued);
                        if (previous == OperationStatus.Queued)
                        {
                            // We've changed from queued to executing.
                            break;
                        }
                        else if ((previous & OperationStatus.Executing) != 0) // Also set when CancellationRequested.
                        {
                            // Already executing.
                            return;
                        }
                        // Operation was cancelled, but not yet removed from queue.
                        Debug.Assert((previous & OperationStatus.Cancelled) != 0);
                        spin.SpinOnce();
                        op = QueueGetFirst();
                    }

                    AsyncExecutionResult result = op.TryExecuteEpollAsync(triggeredByPoll, _thread.ExecutionQueue, this);

                    if (result == AsyncExecutionResult.Executing)
                    {
                        _executingOperation = op;
                        return;                        
                    }

                    op = CompleteOperationAndGetNext(op, result);
                } while (op != null);
            }

            public override bool ExecuteAsync(AsyncOperation operation, bool preferSync)
            {
                int? eventCounterSnapshot = null;

                bool batchOnPollThread = _thread.BatchOnIOThread // Avoid overhead of _thread.IsCurrentThread
                    && _thread.IsCurrentThread;

                if (!batchOnPollThread && preferSync)
                {
                    if (Volatile.Read(ref _queue) == null)
                    {
                        eventCounterSnapshot = Volatile.Read(ref _eventCounter);
                        bool finished = operation.TryExecuteSync();
                        if (finished)
                        {
                            operation.Status = OperationStatus.CompletedSync;
                            operation.Complete();
                            return false;
                        }
                    }
                }

                bool postToIOThread = false;
                bool isFirst = EnqueueAndGetIsFirst(operation);
                if (isFirst)
                {
                    if (batchOnPollThread)
                    {
                        ExecuteQueued(triggeredByPoll: false, operation);
                    }
                    else if (preferSync)
                    {
                        postToIOThread = _eventCounter != eventCounterSnapshot;
                    }
                    else
                    {
                        postToIOThread = true;
                    }
                }

                if (postToIOThread)
                {
                    _thread.Schedule((object? s) => ((Queue)s!).ExecuteQueued(triggeredByPoll: false), this);
                }

                return true;
            }

            public override void TryCancelAndComplete(AsyncOperation operation, OperationStatus flags)
                => TryCancelAndComplete(operation, flags, wait: false);

            private void TryCancelAndComplete(AsyncOperation operation, OperationStatus flags, bool wait)
            {
                OperationStatus previous = OperationStatus.Queued;
                do
                {
                    OperationStatus actual;
                    if (previous == OperationStatus.Queued)
                    {
                        actual = operation.CompareExchangeStatus(OperationStatus.Cancelled | flags, OperationStatus.Queued);
                    }
                    else
                    {
                        actual = operation.CompareExchangeStatus(OperationStatus.CancellationRequested | OperationStatus.Executing | flags, OperationStatus.Executing);
                    }
                    if (actual == previous)
                    {
                        break;
                    }
                    previous = actual;
                } while (previous == OperationStatus.Executing || previous == OperationStatus.Queued);

                if (previous == OperationStatus.Queued)
                {
                    RemoveQueued(operation);
                    operation.Complete();
                }
                else if (previous == OperationStatus.Executing && wait)
                {
                    SpinWait spin = new SpinWait();
                    while (operation.VolatileReadIsCancellationRequested())
                    {
                        spin.SpinOnce();
                    }
                }
            }
        }
    }
}
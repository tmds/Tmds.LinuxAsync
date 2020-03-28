using System.Diagnostics;
using System.Threading;
using static Tmds.Linux.LibC;

namespace Tmds.LinuxAsync
{
    public partial class IOUringAsyncEngine
    {
        sealed class Queue : AsyncOperationQueueBase, IAsyncExecutionResultHandler
        {
            private readonly IOUringThread _thread;
            private readonly IOUringAsyncContext _context;
            private AsyncOperation? _executingOperation;
            private AsyncOperation? _cancellingOperation;
            private int _keyForOperation;

            public Queue(IOUringThread thread, IOUringAsyncContext context, bool readNotWrite)
            {
                _thread = thread;
                _context = context;
                // This is used to distinguish on-going read from write when cancelling.
                _keyForOperation = readNotWrite ? POLLIN : POLLOUT;
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

                if (result == AsyncExecutionResult.Executing || result == AsyncExecutionResult.WaitForPoll)
                {
                    result = op.TryExecuteIOUringAsync(_thread.ExecutionQueue, this, _keyForOperation);
                    Debug.Assert(result == AsyncExecutionResult.Executing);
                    return;
                }

                _executingOperation = null;

                AsyncOperation? next = CompleteOperationAndGetNext(op, result);

                if (next != null)
                {
                    ExecuteQueued(next);
                }
            }

            private AsyncOperation? CompleteOperationAndGetNext(AsyncOperation op, AsyncExecutionResult result)
            {
                if (result == AsyncExecutionResult.Finished)
                {
                    op.Status = OperationStatus.Completed;
                }
                else // Cancelled
                {
                    op.Status = (op.Status & ~(OperationStatus.CancellationRequested | OperationStatus.Executing)) | OperationStatus.Cancelled;
                }

                Volatile.Write(ref _cancellingOperation, null);

                AsyncOperation? next = DequeueFirstAndGetNext(op);

                op.Complete();

                return next;
            }

            public void ExecuteQueued(AsyncOperation? op = null)
            {
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

                    AsyncExecutionResult result = op.TryExecuteIOUringAsync(_thread.ExecutionQueue, this, _keyForOperation);

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
                bool batchOnPollThread = _thread.BatchOnIOThread // Avoid overhead of _thread.IsCurrentThread
                    && _thread.IsCurrentThread;

                if (!batchOnPollThread && preferSync)
                {
                    if (Volatile.Read(ref _queue) == null)
                    {
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
                        ExecuteQueued(operation);
                    }
                    else
                    {
                        postToIOThread = true;
                    }
                }

                if (postToIOThread)
                {
                    _thread.Schedule((object? s) => ((Queue)s!).ExecuteQueued(), this);
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
                else if (previous == OperationStatus.Executing)
                {
                    while (Interlocked.CompareExchange(ref _cancellingOperation, operation, null) != null)
                    {
                        // multiple operations asked to be cancelled while executing, all but one must be finished by now.
                        if (!operation.VolatileReadIsCancellationRequested())
                        {
                            return;
                        }
                    }

                    _thread.Schedule((object? s) => ((Queue)s!).CancelExecuting(), this);

                    if (wait)
                    {
                        SpinWait spin = new SpinWait();
                        while (operation.VolatileReadIsCancellationRequested())
                        {
                            spin.SpinOnce();
                        }
                    }
                }
            }

            private void CancelExecuting()
            {
                AsyncOperation? operation = Interlocked.Exchange(ref _cancellingOperation, null);
                if (operation?.IsCancellationRequested == true)
                {
                    _thread.ExecutionQueue.AddCancel(_context.Handle, _keyForOperation);
                }
            }
        }
    }
}
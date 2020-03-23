using System;
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
            private AsyncOperation? _executingOperation;

            public Queue(EPollThread thread)
            {
                _thread = thread;
            }

            public bool Dispose()
            {
                AsyncOperation? queue = Interlocked.Exchange(ref _tail, AsyncOperation.DisposedSentinel);

                // already disposed
                if (queue == AsyncOperation.DisposedSentinel)
                {
                    return false;
                }

                if (queue != null)
                {
                    AsyncOperation? gate = queue as AsyncOperation.AsyncOperationGate;
                    if (gate != null)
                    {
                        // Synchronize with Enqueue.
                        lock (gate)
                        { }

                        throw new NotImplementedException(); // TODO
                    }
                    else
                    {
                        // queue is single operation
                        TryCancelAndComplete(queue, OperationStatus.None, wait: true);
                    }
                }

                return true;
            }

            private void HandleAsyncResult(AsyncOperationResult aResult)
            {
                AsyncOperation? op = _executingOperation!;

                AsyncExecutionResult result = op.TryExecute(triggeredByPoll: false, cancellationRequested: op.IsCancellationRequested, asyncOnly: false, _thread.ExecutionQueue,
                                                    (AsyncOperationResult aResult, object? state, int data)
                                                        => ((Queue)state!).HandleAsyncResult(aResult)
                                                    , state: this, data: 0, aResult);

                if (result == AsyncExecutionResult.Executing)
                {
                    return;                        
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
                    OperationStatus previous = op.CompareExchangeStatus(OperationStatus.Queued, OperationStatus.Executing);
                    if (previous == OperationStatus.Executing)
                    {
                        // We've changed from executing to queued.
                        return null;
                    }
                    Debug.Assert((previous & OperationStatus.CancellationRequested) != 0);
                    op.Status = (op.Status & ~(OperationStatus.CancellationRequested | OperationStatus.Executing)) | OperationStatus.Cancelled;
                }

                AsyncOperation? next = DequeueFirstAndGetNext(op);

                op.Complete();

                return next;
            }

            private AsyncOperation? DequeueFirstAndGetNext(AsyncOperation first)
            {
                AsyncOperation? previous = Interlocked.CompareExchange(ref _tail, null, first);
                if (object.ReferenceEquals(previous, first) || object.ReferenceEquals(previous, AsyncOperation.DisposedSentinel))
                {
                    return null;
                }
                AsyncOperation? gate = previous as AsyncOperation.AsyncOperationGate;
                if (gate == null)
                {
                    ThrowHelper.ThrowInvalidOperationException();
                }
                lock (gate)
                {
                    if (gate.Next == first) // single element
                    {
                        Debug.Assert(first.Next == first);
                        gate.Next = null;
                        return null;
                    }
                    else
                    {
                        AsyncOperation? tail = gate.Next;
                        Debug.Assert(tail != null);
                        Debug.Assert(tail.Next == first);
                        tail.Next = first.Next;
                        first.Next = null;
                        return tail.Next;
                    }
                }
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

                    AsyncExecutionResult result = op.TryExecute(triggeredByPoll, cancellationRequested: false, asyncOnly: false, _thread.ExecutionQueue,
                                                        (AsyncOperationResult aResult, object? state, int data)
                                                            => ((Queue)state!).HandleAsyncResult(aResult)
                                                        , state: this, data: 0, AsyncOperationResult.NoResult);

                    if (result == AsyncExecutionResult.Executing)
                    {
                        _executingOperation = op;
                        return;                        
                    }

                    op = CompleteOperationAndGetNext(op, result);
                } while (op != null);
            }

            public bool ExecuteAsync(AsyncOperation operation, bool preferSync)
            {
                int? eventCounterSnapshot = null;

                bool batchOnPollThread = _thread.BatchOnIOThread // Avoid overhead of _thread.IsCurrentThread
                    && _thread.IsCurrentThread;

                if (!batchOnPollThread && preferSync)
                {
                    if (Volatile.Read(ref _tail) == null)
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

            public void TryCancelAndComplete(AsyncOperation operation, OperationStatus flags, bool wait = false)
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
                if (previous == OperationStatus.Executing && wait)
                {
                    SpinWait spin = new SpinWait();
                    while ((operation.VolatileReadStatus() & OperationStatus.CancellationRequested) != 0)
                    {
                        spin.SpinOnce();
                    }
                }
            }

            private void RemoveQueued(AsyncOperation operation)
            {
                AsyncOperation? previous = Interlocked.CompareExchange(ref _tail, null, operation);
                if (object.ReferenceEquals(previous, operation))
                {
                    return;
                }
                if (previous is AsyncOperation.AsyncOperationGate gate)
                {
                    lock (gate)
                    {
                        if (gate.Next == operation && operation.Next == operation) // single element
                        {
                            gate.Next = null;
                            return;
                        }
                        else
                        {
                            throw new NotImplementedException(); // TODO
                        }
                    }
                }
            }

            private AsyncOperation? QueueGetFirst()
            {
                AsyncOperation? op = Volatile.Read(ref _tail);
                if (op is null || object.ReferenceEquals(op, AsyncOperation.DisposedSentinel))
                {
                    return null;
                }
                if (op.GetType() == typeof(AsyncOperation.AsyncOperationGate))
                {
                    AsyncOperation gate = op;
                    lock (gate)
                    {
                        op = gate.Next?.Next;
                    }
                }
                return op;
            }

            private bool EnqueueAndGetIsFirst(AsyncOperation operation)
            {
                operation.Next = operation;
                operation.Status = OperationStatus.Queued;
                AsyncOperation? previousTail = Interlocked.CompareExchange(ref _tail, operation, null);
                if (previousTail is null)
                {
                    return true;
                }

                return EnqueueSlow(operation, previousTail);
            }

            private bool EnqueueSlow(AsyncOperation operation, AsyncOperation? previousTail)
            {
                SpinWait spin = new SpinWait();
                while (true)
                {
                    if (object.ReferenceEquals(previousTail, AsyncOperation.DisposedSentinel))
                    {
                        operation.Next = null;
                        operation.Status = OperationStatus.None;
                        ThrowHelper.ThrowObjectDisposedException<AsyncContext>();
                    }
                    else
                    {
                        AsyncOperation.AsyncOperationGate? gate = previousTail as AsyncOperation.AsyncOperationGate;

                        // Install the gate.
                        if (gate == null)
                        {
                            AsyncOperation singleOperation = previousTail!;
                            gate = new AsyncOperation.AsyncOperationGate();
                            gate.Next = singleOperation;
                            previousTail = Interlocked.CompareExchange(ref _tail, gate, singleOperation);
                            if (previousTail != singleOperation)
                            {
                                if (previousTail is null)
                                {
                                    previousTail = Interlocked.CompareExchange(ref _tail, operation, null);
                                    if (previousTail is null)
                                    {
                                        return true;
                                    }
                                }
                                spin.SpinOnce();
                                continue;
                            }
                        }

                        lock (gate)
                        {
                            if (object.ReferenceEquals(_tail, AsyncOperation.DisposedSentinel))
                            {
                                continue;
                            }
                            // skip gate
                            previousTail = gate.Next;

                            if (previousTail == null)
                            {
                                gate.Next = operation;
                                return true;
                            }
                            else
                            {
                                operation.Next = previousTail.Next;
                                previousTail.Next = operation;
                                gate.Next = operation;
                                return false;
                            }
                        }
                    }
                }
            }

        }
    }
}
using System.Diagnostics;
using System.Threading;

namespace Tmds.LinuxAsync
{
    class AsyncOperationQueueBase
    {
        // _queue contains the executing/pending operations.
        // Though Sockets can have multiple pending operations, the
        // common case is there is only one.
        //
        // _queue has one of these values:
        //
        //    * null:                  the queue is empty.
        //    * DisposedSentinel:      the queue was disposed.
        //    * is AsyncOperationGate: the queue contained several elements at some point.
        //   
        //              _queue is not an operation. It is used with lock to synchronize
        //              access to the list of operations.
        //
        //              _queue = gate -> last -> first -> second -> ...
        //                                 ^                         |
        //                                 +-------------------------+
        //
        //    * otherwise:              the queue contains a single operation.
        protected AsyncOperation? _queue;

        protected void RemoveQueued(AsyncOperation operation)
        {
            AsyncOperation? queue = Interlocked.CompareExchange(ref _queue, null, operation);
            if (object.ReferenceEquals(queue, operation))
            {
                return;
            }
            if (queue is object && IsGate(queue))
            {
                lock (queue)
                {
                    if (queue.Next == operation) // We're the last
                    {
                        if (operation.Next == operation) // We're the only
                        {
                            queue.Next = null; // empty
                        }
                        else
                        {
                            // Find newTail
                            AsyncOperation newTail = operation.Next!;
                            {
                                AsyncOperation newTailNext = newTail.Next!;
                                while (newTailNext != operation)
                                {
                                    newTail = newTailNext;
                                }
                            }
                            newTail.Next = operation.Next; // tail point to first
                            queue.Next = newTail;           // gate points to tail
                            operation.Next = operation;    // point to self
                        }
                    }
                    AsyncOperation? tail = queue.Next;
                    if (tail != null)
                    {
                        AsyncOperation it = tail;
                        do
                        {
                            AsyncOperation next = it.Next!;
                            if (next == operation)
                            {
                                it.Next = operation.Next;   // skip operation
                                operation.Next = operation; // point to self
                                return;
                            }
                            it = next;
                        } while (it != tail);
                    }
                }
            }
        }

        protected AsyncOperation? QueueGetFirst()
        {
            AsyncOperation? queue = Volatile.Read(ref _queue);
            if (queue is null || IsDisposed(queue))
            {
                return null;
            }
            if (IsGate(queue))
            {
                lock (queue)
                {
                    return queue.Next?.Next;
                }
            }
            else
            {
                return queue;
            }
        }

        protected bool EnqueueAndGetIsFirst(AsyncOperation operation)
        {
            Debug.Assert(operation.Next == operation); // non-queued point to self.
            operation.Status = OperationStatus.Queued;
            AsyncOperation? queue = Interlocked.CompareExchange(ref _queue, operation, null);
            if (queue is null)
            {
                return true;
            }

            return EnqueueSlowAndGetIsFirst(operation, queue);
        }

        protected bool EnqueueSlowAndGetIsFirst(AsyncOperation operation, AsyncOperation? queue)
        {
            Debug.Assert(queue != null);

            SpinWait spin = new SpinWait();
            while (true)
            {
                if (IsDisposed(queue))
                {
                    operation.Status = OperationStatus.None;
                    ThrowHelper.ThrowObjectDisposedException<AsyncContext>();
                }
                else
                {
                    // Install a gate.
                    if (!IsGate(queue))
                    {
                        AsyncOperation singleOperation = queue;
                        Debug.Assert(singleOperation.Next == singleOperation);

                        AsyncOperation gate = new AsyncOperationGate();
                        gate.Next = singleOperation;
                        queue = Interlocked.CompareExchange(ref _queue, gate, singleOperation);
                        if (queue != singleOperation)
                        {
                            if (queue is null)
                            {
                                queue = Interlocked.CompareExchange(ref _queue, operation, null);
                                if (queue is null)
                                {
                                    return true;
                                }
                            }
                            spin.SpinOnce();
                            continue;
                        }
                    }

                    lock (queue)
                    {
                        if (object.ReferenceEquals(_queue, DisposedSentinel))
                        {
                            continue;
                        }
                        AsyncOperation? tail = queue.Next;

                        if (tail == null) // empty queue
                        {
                            queue.Next = operation;
                            return true;
                        }
                        else
                        {
                            operation.Next = tail.Next; // new tail points to first
                            tail.Next = operation;      // enqueue: previous tail points to new tail
                            queue.Next = operation;     // gate points to new tail
                            return false;
                        }
                    }
                }
            }
        }

        protected AsyncOperation? DequeueFirstAndGetNext(AsyncOperation first)
        {
            AsyncOperation? queue = Interlocked.CompareExchange(ref _queue, null, first);
            Debug.Assert(queue != null);
            if (object.ReferenceEquals(queue, first) || IsDisposed(queue))
            {
                return null;
            }

            Debug.Assert(IsGate(queue));
            lock (queue)
            {
                if (queue.Next == first) // we're the last -> single element
                {
                    Debug.Assert(first.Next == first); // verify we're a single element list
                    queue.Next = null;
                    return null;
                }
                else
                {
                    AsyncOperation? tail = queue.Next;
                    Debug.Assert(tail != null); // there is an element
                    Debug.Assert(tail.Next == first); // we're first
                    tail.Next = first.Next; // skip operation
                    first.Next = first;     // point to self
                    return tail.Next;
                }
            }
        }

        protected static readonly AsyncOperation DisposedSentinel = new AsyncOperationSentinel();

        sealed class AsyncOperationSentinel : AsyncOperation
        {
            public override bool IsReadNotWrite
                => throw new System.InvalidOperationException();
            public override void Complete()
                => throw new System.InvalidOperationException();
            public override AsyncExecutionResult TryExecute(bool triggeredByPoll, bool cancellationRequested, bool asyncOnly, AsyncExecutionQueue? executionQueue, AsyncExecutionCallback? callback, object? state, int data, AsyncOperationResult result)
                => throw new System.InvalidOperationException();
        }

        protected sealed class AsyncOperationGate : AsyncOperation
        {
            public override bool IsReadNotWrite
                => throw new System.InvalidOperationException();
            public override void Complete()
                => throw new System.InvalidOperationException();
            public override AsyncExecutionResult TryExecute(bool triggeredByPoll, bool cancellationRequested, bool asyncOnly, AsyncExecutionQueue? executionQueue, AsyncExecutionCallback? callback, object? state, int data, AsyncOperationResult result)
                => throw new System.InvalidOperationException();
        }

        private static bool IsDisposed(AsyncOperation queue)
            => object.ReferenceEquals(queue, DisposedSentinel);

        private static bool IsGate(AsyncOperation queue)
            => queue.GetType() == typeof(AsyncOperationGate);
    }
}
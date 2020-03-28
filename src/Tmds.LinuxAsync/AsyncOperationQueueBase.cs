using System.Diagnostics;
using System.Threading;

namespace Tmds.LinuxAsync
{
    abstract class AsyncOperationQueueBase
    {
        private AsyncOperation? _cachedOperation;

        public void ReturnOperation(AsyncOperation operation)
            => Volatile.Write(ref _cachedOperation, operation);

        public T RentOperation<T>() where T : AsyncOperation, new()
            => (T?)Interlocked.Exchange(ref _cachedOperation, null) ?? new T();

        // _queue contains the executing/pending operations.
        // Though Sockets can have multiple pending operations, the
        // common case is there is only one.
        //
        // _queue has one of these values:
        //
        //    * null:                  the queue is empty.
        //    * DisposedSentinel:      the queue was disposed.
        //    * x:                     the queue contains a single operation x.
        //    * is AsyncOperationGate: the queue contained several elements at some point.
        //   
        //              _queue is not an operation. It is used to synchronize
        //              access to the list of operations.
        //
        //              _queue = gate -> last -> first -> second -> ...
        //                                 ^                         |
        //                                 +-------------------------+
        //
        protected AsyncOperation? _queue;

        public abstract void TryCancelAndComplete(AsyncOperation operation, OperationStatus flags);

        public abstract bool ExecuteAsync(AsyncOperation operation, bool preferSync);

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
                            // Find newLast
                            AsyncOperation newLast = operation.Next!;
                            {
                                AsyncOperation newLastNext = newLast.Next!;
                                while (newLastNext != operation)
                                {
                                    newLast = newLastNext;
                                }
                            }
                            newLast.Next = operation.Next; // last point to first
                            queue.Next = newLast;          // gate points to last
                            operation.Next = operation;    // point to self
                        }
                    }
                    AsyncOperation? last = queue.Next;
                    if (last != null)
                    {
                        AsyncOperation it = last;
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
                        } while (it != last);
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
                        queue = gate;
                    }

                    lock (queue)
                    {
                        if (object.ReferenceEquals(_queue, DisposedSentinel))
                        {
                            continue;
                        }

                        AsyncOperation? last = queue.Next;
                        if (last == null) // empty queue
                        {
                            queue.Next = operation;
                            return true;
                        }
                        else
                        {
                            queue.Next = operation;     // gate points to new last
                            operation.Next = last.Next; // new last points to first
                            last.Next = operation;      // previous last points to new last
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
                    AsyncOperation? last = queue.Next;
                    Debug.Assert(last != null); // there is an element
                    Debug.Assert(last.Next == first); // we're first
                    last.Next = first.Next; // skip operation
                    first.Next = first;     // point to self
                    return last.Next;
                }
            }
        }

        protected static readonly AsyncOperation DisposedSentinel = new AsyncOperationSentinel();

        sealed class AsyncOperationSentinel : AsyncOperation
        {
            public override void Complete()
                => throw new System.InvalidOperationException();
            public override bool TryExecuteSync()
                => throw new System.InvalidOperationException();
            public override AsyncExecutionResult TryExecuteEpollAsync(bool triggeredByPoll, AsyncExecutionQueue? executionQueue, IAsyncExecutionResultHandler callback)
                => throw new System.InvalidOperationException();
            public override AsyncExecutionResult TryExecuteIOUringAsync(AsyncExecutionQueue executionQueue, IAsyncExecutionResultHandler callback, int key)
                => throw new System.InvalidOperationException();
            public override AsyncExecutionResult HandleAsyncResult(AsyncOperationResult result)
                => throw new System.InvalidOperationException();
        }

        protected sealed class AsyncOperationGate : AsyncOperation
        {
            public override void Complete()
                => throw new System.InvalidOperationException();
            public override bool TryExecuteSync()
                => throw new System.InvalidOperationException();
            public override AsyncExecutionResult TryExecuteEpollAsync(bool triggeredByPoll, AsyncExecutionQueue? executionQueue, IAsyncExecutionResultHandler callback)
                => throw new System.InvalidOperationException();
            public override AsyncExecutionResult TryExecuteIOUringAsync(AsyncExecutionQueue executionQueue, IAsyncExecutionResultHandler callback, int key)
                => throw new System.InvalidOperationException();
            public override AsyncExecutionResult HandleAsyncResult(AsyncOperationResult result)
                => throw new System.InvalidOperationException();
        }

        private static bool IsDisposed(AsyncOperation queue)
            => object.ReferenceEquals(queue, DisposedSentinel);

        private static bool IsGate(AsyncOperation queue)
            => queue.GetType() == typeof(AsyncOperationGate);
    }
}
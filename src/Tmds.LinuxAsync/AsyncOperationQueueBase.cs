using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Tmds.LinuxAsync
{
    class AsyncOperationQueueBase
    {
        protected object Gate => this;
        protected AsyncOperation? _tail;

        protected static bool TryQueueTakeFirst(ref AsyncOperation? tail, [NotNullWhen(true)]out AsyncOperation? first)
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

        protected static AsyncOperation? QueueGetFirst(AsyncOperation? tail)
            => tail?.Next;

        protected static void QueueAdd(ref AsyncOperation? tail, AsyncOperation operation)
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

        protected static bool QueueRemove(ref AsyncOperation? tail, AsyncOperation operation)
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

        // Requests cancellation of a queued operation.
        // If the operation is not on the queue (because it already completed), NotFound is returned.
        // If the operation is not executing, it is removed from the queue and Cancelled is returned.
        // If the operation is executing, it stays on the queue, the operation gets marked as cancellation requested,
        // and Requested is returned.
        protected CancellationRequestResult RequestCancellationAsync(AsyncOperation operation, OperationCompletionFlags flags)
        {
            CancellationRequestResult result = CancellationRequestResult.NotFound;
            if (_tail == operation) // We're the last operation
            {
                result = operation.RequestCancellationAsync(flags);
                if (result == CancellationRequestResult.Cancelled)
                {
                    if (operation.Next == operation) // We're the only operation.
                    {
                        _tail = null;
                    }
                    else
                    {
                        // Update tail
                        while (_tail!.Next != operation)
                        {
                            _tail = _tail.Next;
                        }
                        // Update head
                        _tail.Next = operation.Next;
                    }
                    operation.Next = null;
                }
            }
            else if (_tail != null) // The list is multiple operations and we're not the last
            {
                ref AsyncOperation nextOperation = ref _tail.Next!;
                do
                {
                    if (nextOperation == operation)
                    {
                        result = operation.RequestCancellationAsync(flags);
                        if (result == CancellationRequestResult.Cancelled)
                        {
                            nextOperation = operation.Next!;
                            operation.Next = null;
                        }
                        break;
                    }
                } while (nextOperation != _tail);
            }
            return result;
        }
    }
}
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
    }
}
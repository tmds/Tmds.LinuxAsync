﻿using System.Diagnostics;
using System.Threading;
using static Tmds.Linux.LibC;

namespace Tmds.LinuxAsync
{
    public partial class IOUringAsyncEngine
    {
        sealed class Queue : AsyncOperationQueueBase
        {
            private readonly IOUringThread _thread;
            private readonly IOUringAsyncContext _context;

            public Queue(IOUringThread thread, IOUringAsyncContext context)
            {
                _thread = thread;
                _context = context;
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

                            bool isCancellationRequested = op.IsCancellationRequested;

                            AsyncExecutionResult result = op.TryExecute(triggeredByPoll: false, isCancellationRequested, _thread.ExecutionQueue,
                                (AsyncOperationResult aResult, object? state, int data)
                                    => ((Queue)state!).ExecuteQueued(aResult)
                            , state: this, DataForOperation(op), asyncResult);

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

                // TODO: handle _thread.IsCurrentThread

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
                        // TODO: an alternative could be to add the operation to the executionqueue here directly.
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
                    if (result == CancellationRequestResult.Requested)
                    {
                        // TODO: an alternative could be to add the operation to the executionqueue here directly.
                        _thread.Post((object? s) => ((Queue)s!).CancelExecuting(), this);
                    }
                }

                if (result == CancellationRequestResult.Cancelled)
                {
                    operation.Complete();
                }
            }

            private void CancelExecuting()
            {
                lock (Gate)
                {
                    AsyncOperation? op = _tail?.Next;
                    if (op != null && op.IsCancellationRequested)
                    {
                        _thread.ExecutionQueue.AddCancel(_context.Handle, DataForOperation(op));
                    }
                }
            }

            private int DataForOperation(AsyncOperation op)
                => op.IsReadNotWrite ? POLLIN : POLLOUT;
        }
    }
}
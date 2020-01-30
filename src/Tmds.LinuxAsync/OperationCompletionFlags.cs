using System;

namespace Tmds.LinuxAsync
{
    [Flags]
    enum OperationCompletionFlags
    {
        None = 0,

        // Flags
        CompletedSync = 1,      // AsyncContext.ExecuteAsync finished synchronously.
        CancelledByToken = 2,   // AsyncOperation was cancelled by a CancellationToken.
        CancelledByTimeout = 4, // AsyncOperation was cancelled by a CancellationToken.
        OperationFinished = 8,   // Operation completed by executing.
        OperationCancelled = 16, // operation completed by cancelling.

        CompletedFinishedSync = OperationFinished | CompletedSync,
        CompletedFinishedAsync = OperationFinished,
        CompletedCanceled = OperationCancelled,
        CompletedCanceledSync = OperationCancelled | CompletedSync,
    }
}
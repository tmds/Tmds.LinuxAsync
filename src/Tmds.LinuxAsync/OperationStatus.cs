using System;

namespace Tmds.LinuxAsync
{
    [Flags]
    enum OperationStatus
    {
        None = 0,

        Queued = 1 << 0,    // operation is queued.
        Executing = 1 << 1, // operation is executing.
        Completed = 1 << 2, // operation completed by executing.
        Cancelled = 1 << 3, // operation completed by cancelling.

        // Flags
        Sync = 1 << 4,                  // AsyncContext.ExecuteAsync finished synchronously.
        CancelledByToken = 1 << 5,      // AsyncOperation was cancelled by a CancellationToken.
        CancelledByTimeout = 1 << 6,    // AsyncOperation was cancelled by a CancellationToken.
        CancellationRequested = 1 << 7, // operation requested to be cancelled.

        CompletedSync = Completed | Sync,
        CancelledSync = Cancelled | Sync,
    }
}
// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO.Pipelines;
using System.Net.Sockets;

namespace Tmds.LinuxAsync.Transport.Internal
{
    internal abstract class SocketSenderReceiverBase : IDisposable
    {
        protected readonly Socket _socket;
        protected readonly SocketAwaitableEventArgs _awaitableEventArgs;

        protected SocketSenderReceiverBase(Socket socket, PipeScheduler scheduler, bool runContinuationsAsynchronously, bool preferSynchronousCompletion)
        {
            _socket = socket;
            _awaitableEventArgs = new SocketAwaitableEventArgs(scheduler);
            _awaitableEventArgs.RunContinuationsAsynchronously = runContinuationsAsynchronously;
            _awaitableEventArgs.PreferSynchronousCompletion = preferSynchronousCompletion;
        }

        public void Dispose() => _awaitableEventArgs.Dispose();
    }
}

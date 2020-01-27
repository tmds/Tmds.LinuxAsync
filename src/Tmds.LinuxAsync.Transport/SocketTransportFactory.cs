// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Tmds.LinuxAsync.Transport.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Tmds.LinuxAsync.Transport
{
    public sealed class SocketTransportFactory : IConnectionListenerFactory
    {
        private readonly SocketTransportOptions _options;
        private readonly SocketsTrace _trace;

        public SocketTransportFactory(
            IOptions<SocketTransportOptions> options,
            ILoggerFactory loggerFactory)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (loggerFactory == null)
            {
                throw new ArgumentNullException(nameof(loggerFactory));
            }

            _options = options.Value;
            var logger = loggerFactory.CreateLogger("Tmds.LinuxAsync.Transport");
            _trace = new SocketsTrace(logger);
        }

        public ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
        {
            var transport = new SocketConnectionListener(endpoint, _options, _trace);
            transport.Bind();
            return new ValueTask<IConnectionListener>(transport);
        }
    }
}

// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using Microsoft.AspNetCore.Connections;
using Tmds.LinuxAsync.Transport;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.AspNetCore.Hosting
{
    /// <summary>
    /// <see cref="IWebHostBuilder" /> extension methods to configure the Socket transport to be used by Kestrel.
    /// </summary>
    public static class WebHostBuilderSocketExtensions
    {
        /// <summary>
        /// Specify Sockets as the transport to be used by Kestrel.
        /// </summary>
        /// <param name="hostBuilder">
        /// The Microsoft.AspNetCore.Hosting.IWebHostBuilder to configure.
        /// </param>
        /// <returns>
        /// The Microsoft.AspNetCore.Hosting.IWebHostBuilder.
        /// </returns>
        public static IWebHostBuilder UseLinuxAsyncSockets(this IWebHostBuilder hostBuilder)
        {
            return hostBuilder.ConfigureServices(services =>
            {
                services.AddSingleton<IConnectionListenerFactory, SocketTransportFactory>();
            });
        }

        /// <summary>
        /// Specify Sockets as the transport to be used by Kestrel.
        /// </summary>
        /// <param name="hostBuilder">
        /// The Microsoft.AspNetCore.Hosting.IWebHostBuilder to configure.
        /// </param>
        /// <param name="configureOptions">
        /// A callback to configure Libuv options.
        /// </param>
        /// <returns>
        /// The Microsoft.AspNetCore.Hosting.IWebHostBuilder.
        /// </returns>
        public static IWebHostBuilder UseLinuxAsyncSockets(this IWebHostBuilder hostBuilder, Action<SocketTransportOptions> configureOptions)
        {
            return hostBuilder.UseLinuxAsyncSockets().ConfigureServices(services =>
            {
                services.Configure(configureOptions);
            });
        }
    }
}

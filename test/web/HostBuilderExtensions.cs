using System.IO.Pipelines;
using IoUring.Transport;
using Tmds.LinuxAsync.Transport;
using web;

namespace Microsoft.AspNetCore.Hosting 
{
    internal static class HostBuilderExtensions
    {
        public static IWebHostBuilder ConfigureForCommandOptions(this IWebHostBuilder webBuilder, CommandLineOptions commandLineOptions)
        {
            switch (commandLineOptions.SocketEngine)
            {
                case SocketEngineType.IOUringTransport:
                    webBuilder.ConfigureServices(serviceCollection =>
                        serviceCollection.AddIoUringTransport(options =>
                        {
                            options.ThreadCount = commandLineOptions.ThreadCount;
                            options.ApplicationSchedulingMode = commandLineOptions.InputScheduler == InputScheduler.Inline ?
                                PipeScheduler.Inline : PipeScheduler.ThreadPool;
                        }));
                    break;
                case SocketEngineType.LinuxTransport:
                    webBuilder.UseLinuxTransport(options =>
                    {
                        options.ThreadCount = commandLineOptions.ThreadCount;
                        options.DeferSend = commandLineOptions.DeferSends.Value;
                        options.ApplicationSchedulingMode = commandLineOptions.InputScheduler == InputScheduler.Inline ?
                            PipeScheduler.Inline : PipeScheduler.ThreadPool;
                    });
                    break;
                case SocketEngineType.DefaultTransport:
                    webBuilder.UseSockets(options =>
                    {
                        options.IOQueueCount = commandLineOptions.ThreadCount;
                    });
                    break;
                default:
                    webBuilder.UseLinuxAsyncSockets(options =>
                        {
                            options.DeferSends = commandLineOptions.DeferSends.Value;
                            options.DeferReceives = commandLineOptions.DeferReceives.Value;
                            options.DontAllocateMemoryForIdleConnections = commandLineOptions.DontAllocateMemoryForIdleConnections.Value;
                            options.OutputScheduler = commandLineOptions.OutputScheduler;
                            options.InputScheduler = commandLineOptions.InputScheduler;
                            options.SocketContinuationScheduler = commandLineOptions.SocketContinuationScheduler;
                        }
                    );
                    break;
            }

            return webBuilder;
        }
    }
}
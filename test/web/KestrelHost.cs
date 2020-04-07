using System.IO.Pipelines;
using IoUring.Transport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Tmds.LinuxAsync.Transport;

namespace web
{
    public class KestrelHost
    {
        private string[] _args;
        private readonly CommandLineOptions _options;

        public KestrelHost(CommandLineOptions options, string[] args)
        {
            _options = options;
            _args = args;
        }

        public void Run()
        {
            CreateHostBuilder(_args, _options).Build().Run();
        }
        
        private static IHostBuilder CreateHostBuilder(string[] args, CommandLineOptions commandLineOptions)
        {
            return Host.CreateDefaultBuilder(args)
#if RELEASE
                .ConfigureLogging(loggingBuilder => loggingBuilder.ClearProviders())
#endif
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseStartup<Startup>();

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
                                options.ApplicationSchedulingMode= commandLineOptions.InputScheduler == InputScheduler.Inline ?
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
                });
        }
    }
}
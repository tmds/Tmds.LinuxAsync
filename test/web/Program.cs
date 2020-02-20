using System;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Tmds.LinuxAsync;

namespace web
{
    public class Program
    {
        public static void Main(string[] args)
        {
            (bool isSuccess, CommandLineOptions options)  = ConsoleLineArgumentsParser.ParseArguments(args);

            if (isSuccess)
            {
                CreateHostBuilder(args, options).Build().Run();
            }
        }

        public static IHostBuilder CreateHostBuilder(string[] args, CommandLineOptions commandLineOptions)
        {
            AsyncEngine.SocketEngine = CreateAsyncEngine(commandLineOptions);

            return Host.CreateDefaultBuilder(args)
#if RELEASE
                .ConfigureLogging(loggingBuilder => loggingBuilder.ClearProviders())
#endif
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseStartup<Startup>();
                    webBuilder.UseLinuxAsyncSockets(options =>
                        {
                            options.DispatchContinuations = commandLineOptions.DispatchContinuations.Value;
                            options.DeferSends = commandLineOptions.DeferSends.Value;
                            options.DeferReceives = commandLineOptions.DeferReceives.Value;
                        }
                    );
                });
        }

        private static AsyncEngine CreateAsyncEngine(CommandLineOptions commandLineOptions)
        {
            switch (commandLineOptions.SocketEngine)
            {
                case SocketEngineType.EPoll:
                    return new EPollAsyncEngine(threadCount: commandLineOptions.ThreadCount, useLinuxAio: commandLineOptions.UseAio.Value);
                case SocketEngineType.IOUring:
                    return new IOUringAsyncEngine(threadCount: commandLineOptions.ThreadCount);
                default:
                    throw new NotSupportedException($"{commandLineOptions.SocketEngine} is not supported");
            }
        }
    }
}

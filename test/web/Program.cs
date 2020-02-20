using System;
using System.IO;
using CommandLine;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tmds.LinuxAsync;

namespace web
{
    public enum SocketEngineType
    {
        EPoll,
        IOUring
    }

    public class CommandLineOptions
    {
        [Option('e', "engine", Required = false, Default = SocketEngineType.IOUring, HelpText = "EPoll/IOUring")]
        public SocketEngineType SocketEngine { get; set; }

        [Option('t', "thread-count", Required = false, Default = 1, HelpText = "Thread Count, default value is 1")]
        public int ThreadCount { get; set; }

        [Option('a', "aio", Required = false, Default = true, HelpText = "Use Linux AIO")]
        public bool UseAio { get; set; }

        [Option('c', "dispatch-continuations", Required = false, Default = false, HelpText = "Dispatch Continuations")]
        public bool DispatchContinuations { get; set; }

        [Option('s', "defer-sends", Required = false, Default = true, HelpText = "Defer Sends")]
        public bool DeferSends { get; set; }

        [Option('r', "defer-receives", Required = false, Default = true, HelpText = "Defer Receives")]
        public bool DeferReceives { get; set; }
    }

    public class Program
    {
        public static void Main(string[] args)
        {
            using (var parser = CreateParser())
            {
                parser
                    .ParseArguments<CommandLineOptions>(args)
                    .WithParsed(options => CreateHostBuilder(args, options).Build().Run());
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
                            options.DispatchContinuations = commandLineOptions.DispatchContinuations;
                            options.DeferSends = commandLineOptions.DeferSends;
                            options.DeferReceives = commandLineOptions.DeferReceives;
                        }
                    );
                });
        }

        private static AsyncEngine CreateAsyncEngine(CommandLineOptions commandLineOptions)
        {
            switch (commandLineOptions.SocketEngine)
            {
                case SocketEngineType.EPoll:
                    return new EPollAsyncEngine(threadCount: commandLineOptions.ThreadCount, useLinuxAio: commandLineOptions.UseAio);
                case SocketEngineType.IOUring:
                    return new IOUringAsyncEngine(threadCount: commandLineOptions.ThreadCount);
                default:
                    throw new NotSupportedException($"{commandLineOptions.SocketEngine} is not supported");
            }
        }

        private static Parser CreateParser()
            => new Parser(settings =>
            {
                settings.CaseInsensitiveEnumValues = true;
                settings.CaseSensitive = false;
                settings.EnableDashDash = true;
                settings.HelpWriter = Console.Out;
                settings.IgnoreUnknownArguments = true; // for args that we pass to Host.CreateDefaultBuilder()
                settings.MaximumDisplayWidth = GetMaximumDisplayWidth();
            });

        private static int GetMaximumDisplayWidth()
        {
            const int MinimumDisplayWidth = 80;

            try
            {
                return Math.Max(MinimumDisplayWidth, Console.WindowWidth);
            }
            catch (IOException)
            {
                return MinimumDisplayWidth;
            }
        }
    }
}

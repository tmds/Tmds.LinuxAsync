using System.IO.Pipelines;
using IoUring.Transport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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

                    webBuilder.ConfigureForCommandOptions(commandLineOptions);
                });
        }
    }
}
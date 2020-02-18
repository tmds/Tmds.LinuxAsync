using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tmds.LinuxAsync;

namespace web
{
    public class Program
    {
        public static void Main(string[] args)
        {
            // AsyncEngine.SocketEngine = new EPollAsyncEngine(
            //                                 threadCount: Environment.ProcessorCount,
            //                                 useLinuxAio: true);

            AsyncEngine.SocketEngine = new IOUringAsyncEngine(
                                            threadCount: Environment.ProcessorCount);

            CreateHostBuilder(args).Build().Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseStartup<Startup>();
                    webBuilder.UseLinuxAsyncSockets(options =>
                        {
                            options.DispatchContinuations = false;
                            options.DeferSends = true;
                            options.DeferReceives = true;
                        }
                    );
                });
    }
}

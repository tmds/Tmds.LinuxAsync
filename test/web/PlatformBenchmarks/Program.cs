// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
#if DATABASE
using Npgsql;
using MySql.Data.MySqlClient;
#endif
using web;

namespace PlatformBenchmarks
{
    public class Program
    {
        public static string[] Args;

        public static void PlatformMain(string[] args, CommandLineOptions commandLineOptions)
        {
            Args = args;

            Console.WriteLine(BenchmarkApplication.ApplicationName);
#if !DATABASE
            Console.WriteLine(BenchmarkApplication.Paths.Plaintext);
            Console.WriteLine(BenchmarkApplication.Paths.Json);
#else
            Console.WriteLine(BenchmarkApplication.Paths.Fortunes);
            Console.WriteLine(BenchmarkApplication.Paths.SingleQuery);
            Console.WriteLine(BenchmarkApplication.Paths.Updates);
            Console.WriteLine(BenchmarkApplication.Paths.MultipleQueries);
#endif
            DateHeader.SyncDateTimer();

            var host = BuildWebHost(args, commandLineOptions);
            var config = (IConfiguration)host.Services.GetService(typeof(IConfiguration));
            BatchUpdateString.DatabaseServer = config.Get<AppSettings>().Database;
            host.Run();
        }

        public static IWebHost BuildWebHost(string[] args, CommandLineOptions commandLineOptions)
        {
            var config = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json")
                .AddEnvironmentVariables(prefix: "ASPNETCORE_")
                .AddCommandLine(args)
                .Build();

            var appSettings = config.Get<AppSettings>();
#if DATABASE
            Console.WriteLine($"Database: {appSettings.Database}");
            Console.WriteLine($"Database: {appSettings.ConnectionString}");

            if (appSettings.Database == DatabaseServer.PostgreSql)
            {
                BenchmarkApplication.Db = new RawDb(new ConcurrentRandom(), NpgsqlFactory.Instance, appSettings);
            }
            else if (appSettings.Database == DatabaseServer.MySql)
            {
                BenchmarkApplication.Db = new RawDb(new ConcurrentRandom(), MySqlClientFactory.Instance, appSettings);
            }
#endif

            var host = new WebHostBuilder()
                .UseBenchmarksConfiguration(config)
                .ConfigureForCommandOptions(commandLineOptions)
                .UseKestrel((context, options) =>
                {
                    var endPoint = context.Configuration.CreateIPEndPoint();

                    options.Listen(endPoint, builder =>
                    {
                        builder.UseHttpApplication<BenchmarkApplication>();
                    });
                })
                .UseStartup<Startup>()
                .Build();

            return host;
        }
    }
}

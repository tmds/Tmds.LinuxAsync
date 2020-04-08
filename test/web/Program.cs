using System;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Tmds.LinuxAsync;
using IoUring.Transport;
using System.IO.Pipelines;
using Tmds.LinuxAsync.Transport;
using Socket = System.Net.Sockets.Socket;
using SocketAsyncEventArgs = System.Net.Sockets.SocketAsyncEventArgs;
#if RELEASE
using Microsoft.Extensions.Logging;
#endif

namespace web
{
    public class Program
    {
        public static void Main(string[] args)
        {
            (bool isSuccess, CommandLineOptions options) = ConsoleLineArgumentsParser.ParseArguments(args);

            if (isSuccess)
            {
                AsyncEngine.SocketEngine = CreateAsyncEngine(options);

                if (options.RawSocket)
                {
                    if (options.SocketEngine == SocketEngineType.DefaultTransport)
                    {
                        var handler = new SystemNetSocketHandler();
                        var host = new RawSocketHost<Socket, SocketAsyncEventArgs>(options, args, handler);
                        host.Run();
                    }
                    else
                    {
                        var handler = new TmdsSocketHandler();
                        var host = new RawSocketHost<Tmds.LinuxAsync.Socket, Tmds.LinuxAsync.SocketAsyncEventArgs>(options, args, handler);
                        host.Run();
                    }
                }
                else
                {
                    KestrelHost host = new KestrelHost(options, args);
                    host.Run();
                }
            }
        }

        private static AsyncEngine CreateAsyncEngine(CommandLineOptions commandLineOptions)
        {
            bool batchOnIOThread = commandLineOptions.SocketContinuationScheduler == SocketContinuationScheduler.Inline ||
                                          commandLineOptions.OutputScheduler == OutputScheduler.IOThread;
            switch (commandLineOptions.SocketEngine)
            {
                case SocketEngineType.EPoll:
                    return new EPollAsyncEngine(threadCount: commandLineOptions.ThreadCount,
                        useLinuxAio: commandLineOptions.UseAio.Value,
                        batchOnIOThread);
                case SocketEngineType.IOUring:
                    return new IOUringAsyncEngine(threadCount: commandLineOptions.ThreadCount,
                        batchOnIOThread);
                case SocketEngineType.IOUringTransport:
                case SocketEngineType.LinuxTransport: 
                case SocketEngineType.DefaultTransport:
                    // Create EPollAsyncEngine with threadCount of zero.
                    return new EPollAsyncEngine(threadCount: 0,
                        useLinuxAio: false,
                        batchOnIOThread);
                default:
                    throw new NotSupportedException($"{commandLineOptions.SocketEngine} is not supported");
            }
        }
    }
}

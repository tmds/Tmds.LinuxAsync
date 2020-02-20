using web;
using Xunit;

namespace UnitTests
{
    public class ConsoleLineArgumentsParserTests
    {
        [Theory]
        [InlineData(SocketEngineType.EPoll, "-e", "epoll")]
        [InlineData(SocketEngineType.EPoll, "-e=epoll")]
        [InlineData(SocketEngineType.EPoll, "--engine", "epoll")]
        [InlineData(SocketEngineType.EPoll, "--engine=epoll")]
        [InlineData(SocketEngineType.IOUring, "-e", "IOUring")]
        [InlineData(SocketEngineType.IOUring, "-e=IOUring")]
        [InlineData(SocketEngineType.IOUring, "--engine", "IOUring")]
        [InlineData(SocketEngineType.IOUring, "--engine=IOUring")]
        public void EngineCanBeParsed(SocketEngineType expectedEngineType, params string[] args)
        {
            (bool isSuccess, CommandLineOptions commandLineOptions) = ConsoleLineArgumentsParser.ParseArguments(args);

            Assert.True(isSuccess);
            Assert.Equal(expectedEngineType, commandLineOptions.SocketEngine);
        }

        [Theory]
        [InlineData(2, "-t", "2")]
        [InlineData(2, "-t=2")]
        [InlineData(2, "--thread-count", "2")]
        public void ThreadCountCanBeParsed(int expectedThreadCount, params string[] args)
        {
            (bool isSuccess, CommandLineOptions commandLineOptions) = ConsoleLineArgumentsParser.ParseArguments(args);

            Assert.True(isSuccess);
            Assert.Equal(expectedThreadCount, commandLineOptions.ThreadCount);
        }

        [Theory]
        [InlineData(true, "-a", "true")]
        [InlineData(true, "-a=true")]
        [InlineData(true, "--aio", "true")]
        [InlineData(false, "-a", "false")]
        [InlineData(false, "-a=false")]
        [InlineData(false, "--aio", "false")]
        public void AioCanBeParsed(bool expectedAio, params string[] args)
        {
            (bool isSuccess, CommandLineOptions commandLineOptions) = ConsoleLineArgumentsParser.ParseArguments(args);

            Assert.True(isSuccess);
            Assert.Equal(expectedAio, commandLineOptions.UseAio);
        }
    }
}

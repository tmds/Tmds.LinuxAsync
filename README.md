This repo is meant for trying out implementations for the async engine that powers .NET Core `Socket` implementation on Linux. The .NET Core implementation is in [SocketAsyncEngine.Unix.cs](https://github.com/dotnet/runtime/blob/master/src/libraries/System.Net.Sockets/src/System/Net/Sockets/SocketAsyncEngine.Unix.cs) and related classes.

To compare implementations they need to be benchmarks, so the repo will expose a `Socket` class that provides a subset of `System.Net.Sockets.Socket` members. These members need to have the same semantics as those of the _real_ Socket. Having this `Socket` type should allow re-using existing benchmarks written against the `Socket` class. On top of this `Socket` we'll also make an ASP.NET Transport implementation, which will allow to run ASP.NET Core benchmarks (like TechEmpower scenarios).

The async engine implementation should also allow implementing async operations for pipe-type `FileStream`.

For accessing native system functions [Tmds.LibC](https://github.com/tmds/Tmds.LibC) is used. This avoid having to include a native shim library.
[tkp1n/IoUring](https://github.com/tkp1n/IoUring) is used for `io_uring` APIs.

# Tmds.LinuxAsync

Two async engines implementations are provided. They can be configured an used by setting `AsyncEngine.SocketEngine`:
```c#
// epoll-based with linux AIO for batching
AsyncEngine.SocketEngine = new EPollAsyncEngine(
                                threadCount: Environment.ProcessorCount,
                                useLinuxAio: true);

// io_uring-based
AsyncEngine.SocketEngine = new IOUringAsyncEngine(
                                threadCount: Environment.ProcessorCount);
```

To use the `Socket` from this package, add these to the top of your code file:

```c#
using Socket = Tmds.LinuxAsync.Socket;
using SocketAsyncEventArgs = Tmds.LinuxAsync.SocketAsyncEventArgs;
```

Additional properties are provided on `SocketAsyncEventArgs`:

```c#
class SocketAsyncEventArgs
{
  public bool RunContinuationsAsynchronously { get; set; } = true;
  public bool PreferSynchronousCompletion { get; set; } = true;
}
```

Setting `RunContinuationsAsynchronously` to `false` allows `SocketAsyncEngine` to invoke callbacks directly from the `epoll`/`io_uring` thread. This avoid context switching to the `ThreadPool`.

# Tmds.LinuxAsync.Transport

This is a copy of ASP.NET Core [Transport.Sockets](https://github.com/dotnet/aspnetcore/tree/master/src/Servers/Kestrel/Transport.Sockets) that uses `Tmds.LinuxAsync.Socket` instead of `System.Net.Sockets.Socket`.

The Transport can be used in ASP.NET Core by calling the `UseLinuxAsyncSockets` `IWebHostBuilder` extension methods.

```c#
public enum OutputScheduler
{
    IOQueue,
    Inline,
    IOThread,
    ThreadPool
}
public enum InputScheduler
{
    Inline,
    ThreadPool
}
public enum SocketContinuationScheduler
{
    Inline,
    ThreadPool
}
class SocketTransportOptions
{
  public bool DispatchContinuations { get; set; } = true; // Sets RunContinuationsAsynchronously
  public bool DeferSends { get; set; } = false;           // Sets !PreferSynchronousCompletion for sends
  public bool DeferReceives { get; set; } = false;        // Sets !PreferSynchronousCompletion for receives
  public OutputScheduler OutputScheduler { get; set; } = OutputScheduler.IOQueue;
  public InputScheduler InputScheduler { get; set; } = InputScheduler.ThreadPool;
  public SocketContinuationScheduler SocketContinuationScheduler { get; set; } = SocketContinuationScheduler.ThreadPool;
  public bool DontAllocateMemoryForIdleConnections { get; set; } = true;
}
```

Setting `OutputScheduler` to `IOQueue`/`IOThread`/`ThreadPool` defers write operations to an `IOQueue`/`IOThread`. If more data is written to the `Pipe` before the operation is executed on the `IOQueue`/`IOThread``ThreadPool`, it will be part of a single write operation.

Setting `ApplicationCodeIsNonBlocking` to `true` causes reads from ASP.NET Core to not get deferred to the `ThreadPool`.
**Note** something in ASP.NET Core still defers the HTTP Handler to run on the `ThreadPool`.

Setting `DontAllocateMemoryForIdleConnections` to `false` will allocate a buffer for every connection up-front. For idle connections, this is a waste of memory. Setting this to `true` eliminates a syscall to wait for data to be available.

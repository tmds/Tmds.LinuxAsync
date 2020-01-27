This repo is meant for trying out implementations for the async engine that powers .NET Core `Socket` implementation on Linux. The .NET Core implementation is in [SocketAsyncEngine.Unix.cs](https://github.com/dotnet/runtime/blob/master/src/libraries/System.Net.Sockets/src/System/Net/Sockets/SocketAsyncEngine.Unix.cs) and related classes.

To compare implementations they need to be benchmarks, so the repo will expose a `Socket` class that provides a subset of `System.Net.Sockets.Socket` members. These members need to have the same semantics as those of the _real_ Socket. Having this `Socket` type should allow re-using existing benchmarks written against the `Socket` class. On top of this `Socket` we'll also make an ASP.NET Transport implementation, which will allow to run ASP.NET Core benchmarks (like TechEmpower scenarios).

The async engine implementation should also allow implementing async operations for pipe-type `FileStream`.

For accessing native system functions [Tmds.LibC](https://github.com/tmds/Tmds.LibC) is used. This avoid having to include a native shim library.

# Tmds.LinuxAsync

To use the `Socket` from this package, add these to the top of your code file:

```c#
using Socket = Tmds.LinuxAsync.Socket;
using SocketAsyncEventArgs = Tmds.LinuxAsync.SocketAsyncEventArgs;
```

# Tmds.LinuxAsync.Transport

This is a copy of ASP.NET Core [Transport.Sockets](https://github.com/dotnet/aspnetcore/tree/master/src/Servers/Kestrel/Transport.Sockets) that uses `Tmds.LinuxAsync.Socket` instead of `System.Net.Sockets.Socket`.

The Transport can be used in ASP.NET Core by calling the `UseLinuxAsyncSockets` `IWebHostBuilder` extension methods.
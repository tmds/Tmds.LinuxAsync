using System;
using System.Buffers;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using Tmds.Linux;
using static Tmds.Linux.LibC;

namespace Tmds.LinuxAsync
{
    static class SocketPal
    {
        public static unsafe (SocketError socketError, int bytesTransferred) Recv(SafeHandle handle, Memory<byte> memory)
        {
            int bytesTransferred;
            SocketError socketError;
            using MemoryHandle pinned = memory.Pin();

            int fd = handle.DangerousGetHandle().ToInt32(); // TODO: make safe
            int rv;
            do
            {
                rv = (int)recv(fd, pinned.Pointer, memory.Length, 0);
            } while (rv == -1 && errno == EINTR);

            if (rv < 0)
            {
                bytesTransferred = 0;
                socketError = GetSocketErrorForErrno(errno);
            }
            else
            {
                bytesTransferred = rv;
                socketError = SocketError.Success;
            }

            return (socketError, bytesTransferred);
        }

        public static unsafe (SocketError socketError, int bytesTransferred) Send(SafeHandle handle, Memory<byte> memory)
        {
            int bytesTransferred;
            SocketError socketError;
            using MemoryHandle pinned = memory.Pin();

            int fd = handle.DangerousGetHandle().ToInt32(); // TODO: make safe
            int rv;
            do
            {
                rv = (int)send(fd, pinned.Pointer, memory.Length, 0);
            } while (rv == -1 && errno == EINTR);

            if (rv < 0)
            {
                bytesTransferred = 0;
                socketError = GetSocketErrorForErrno(errno);
            }
            else
            {
                bytesTransferred = rv;
                socketError = SocketError.Success;
            }

            return (socketError, bytesTransferred);
        }

        public static SocketError GetSocketErrorForErrno(int errno)
        {
            if (errno == EACCES) { return SocketError.AccessDenied; }
            else if (errno == EADDRINUSE) { return SocketError.AddressAlreadyInUse; }
            else if (errno == EADDRNOTAVAIL) { return SocketError.AddressNotAvailable; }
            else if (errno == EAFNOSUPPORT) { return SocketError.AddressFamilyNotSupported; }
            else if (errno == EAGAIN) { return SocketError.WouldBlock; }
            else if (errno == EALREADY) { return SocketError.AlreadyInProgress; }
            else if (errno == EBADF) { return SocketError.OperationAborted; }
            else if (errno == ECANCELED) { return SocketError.OperationAborted; }
            else if (errno == ECONNABORTED) { return SocketError.ConnectionAborted; }
            else if (errno == ECONNREFUSED) { return SocketError.ConnectionRefused; }
            else if (errno == ECONNRESET) { return SocketError.ConnectionReset; }
            else if (errno == EDESTADDRREQ) { return SocketError.DestinationAddressRequired; }
            else if (errno == EFAULT) { return SocketError.Fault; }
            else if (errno == EHOSTDOWN) { return SocketError.HostDown; }
            else if (errno == ENXIO) { return SocketError.HostNotFound; }
            else if (errno == EHOSTUNREACH) { return SocketError.HostUnreachable; }
            else if (errno == EINPROGRESS) { return SocketError.InProgress; }
            else if (errno == EINTR) { return SocketError.Interrupted; }
            else if (errno == EINVAL) { return SocketError.InvalidArgument; }
            else if (errno == EISCONN) { return SocketError.IsConnected; }
            else if (errno == EMFILE) { return SocketError.TooManyOpenSockets; }
            else if (errno == EMSGSIZE) { return SocketError.MessageSize; }
            else if (errno == ENETDOWN) { return SocketError.NetworkDown; }
            else if (errno == ENETRESET) { return SocketError.NetworkReset; }
            else if (errno == ENETUNREACH) { return SocketError.NetworkUnreachable; }
            else if (errno == ENFILE) { return SocketError.TooManyOpenSockets; }
            else if (errno == ENOBUFS) { return SocketError.NoBufferSpaceAvailable; }
            else if (errno == ENODATA) { return SocketError.NoData; }
            else if (errno == ENOENT) { return SocketError.AddressNotAvailable; }
            else if (errno == ENOPROTOOPT) { return SocketError.ProtocolOption; }
            else if (errno == ENOTCONN) { return SocketError.NotConnected; }
            else if (errno == ENOTSOCK) { return SocketError.NotSocket; }
            else if (errno == ENOTSUP) { return SocketError.OperationNotSupported; }
            else if (errno == EPERM) { return SocketError.AccessDenied; }
            else if (errno == EPIPE) { return SocketError.Shutdown; }
            else if (errno == EPFNOSUPPORT) { return SocketError.ProtocolFamilyNotSupported; }
            else if (errno == EPROTONOSUPPORT) { return SocketError.ProtocolNotSupported; }
            else if (errno == EPROTOTYPE) { return SocketError.ProtocolType; }
            else if (errno == ESOCKTNOSUPPORT) { return SocketError.SocketNotSupported; }
            else if (errno == ESHUTDOWN) { return SocketError.Disconnecting; }
            else if (errno == 0) { return SocketError.Success; }
            else if (errno == ETIMEDOUT) { return SocketError.TimedOut; }
            else
            {
                ThrowHelper.ThrowIndexOutOfRange(errno);
                return SocketError.SocketError;
            }
        }

        public unsafe static SocketError Connect(SafeHandle handle, IPEndPoint ipEndPoint)
        {
            sockaddr_storage address;
            ToSockAddr(ipEndPoint, &address, out int addressLength);

            int fd = handle.DangerousGetHandle().ToInt32(); // TODO: make safe

            int rv;
            do
            {
                rv = connect(fd, (sockaddr*)&address, addressLength);
            } while (rv == -1 && errno == EINTR);

            return rv == -1 ? GetSocketErrorForErrno(errno) : SocketError.Success;
        }

        internal static unsafe (SocketError socketError, int bytesTransferred) SendMsg(SafeSocketHandle handle, msghdr* msghdr)
        {
            int bytesTransferred;
            SocketError socketError;

            int fd = handle.DangerousGetHandle().ToInt32(); // TODO: make safe
            int rv;
            do
            {
                rv = (int)sendmsg(fd, msghdr, 0);
            } while (rv == -1 && errno == EINTR);

            if (rv < 0)
            {
                bytesTransferred = 0;
                socketError = GetSocketErrorForErrno(errno);
            }
            else
            {
                bytesTransferred = rv;
                socketError = SocketError.Success;
            }

            return (socketError, bytesTransferred);
        }

        public unsafe static (SocketError socketError, Socket? acceptedSocket) Accept(SafeHandle handle)
        {
            int fd = handle.DangerousGetHandle().ToInt32(); // TODO: make safe

            int rv;
            do
            {
                rv = accept4(fd, (sockaddr*)0, (socklen_t*)0, SOCK_CLOEXEC);
            } while (rv == -1 && errno == EINTR);

            SocketError socketError = rv == -1 ? GetSocketErrorForErrno(errno) : SocketError.Success;
            Socket? acceptedSocket = rv == -1 ? null : CreateSocketFromFd(rv, s_reflectionMethods);

            return (socketError, acceptedSocket);
        }

        public static unsafe void ToSockAddr(this IPEndPoint ipEndPoint, sockaddr_storage* addr, out int length)
        {
            if (ipEndPoint.AddressFamily == AddressFamily.InterNetwork)
            {
                sockaddr_in* addrIn = (sockaddr_in*)addr;
                addrIn->sin_family = AF_INET;
                addrIn->sin_port = htons((ushort)ipEndPoint.Port);
                int bytesWritten;
                ipEndPoint.Address.TryWriteBytes(new Span<byte>(addrIn->sin_addr.s_addr, 4), out bytesWritten);
                length = SizeOf.sockaddr_in;
            }
            else if (ipEndPoint.AddressFamily == AddressFamily.InterNetworkV6)
            {
                sockaddr_in6* addrIn = (sockaddr_in6*)addr;
                addrIn->sin6_family = AF_INET6;
                addrIn->sin6_port = htons((ushort)ipEndPoint.Port);
                addrIn->sin6_flowinfo = 0;
                addrIn->sin6_scope_id = 0;
                int bytesWritten;
                ipEndPoint.Address.TryWriteBytes(new Span<byte>(addrIn->sin6_addr.s6_addr, 16), out bytesWritten);
                length = SizeOf.sockaddr_in6;
            }
            else
            {
                length = 0;
            }
        }

        public static void SetNonBlocking(SafeHandle handle)
        {
            int fd = handle.DangerousGetHandle().ToInt32(); // TODO: make safe
            int flags = fcntl(fd, F_GETFL, 0);
            if (flags == -1)
            {
                PlatformException.Throw();
            }

            flags |= O_NONBLOCK;

            int rv = fcntl(fd, F_SETFL, flags);
            if (rv == -1)
            {
                PlatformException.Throw();
            }
        }

        // Copied from Tmds.Systemd.
#nullable disable
        private static Socket CreateSocketFromFd(int fd, ReflectionMethods reflectionMethods)
        {
            // set CLOEXEC
            fcntl(fd, F_SETFD, FD_CLOEXEC);

            // static unsafe SafeCloseSocket CreateSocket(IntPtr fileDescriptor)
            var fileDescriptor = new IntPtr(fd);
            var safeCloseSocket = reflectionMethods.SafeCloseSocketCreate.Invoke(null, new object[] { fileDescriptor });

            // private Socket(SafeCloseSocket fd)
            var socket = reflectionMethods.SocketConstructor.Invoke(new[] { safeCloseSocket });

            // // private bool _isListening = false;
            // bool listening = GetSockOpt(fd, SO_ACCEPTCONM) != 0;
            // reflectionMethods.IsListening.SetValue(socket, listening);

            EndPoint endPoint;
            AddressFamily addressFamily = ConvertAddressFamily(GetSockOpt(fd, SO_DOMAIN));
            if (addressFamily == AddressFamily.InterNetwork)
            {
                endPoint = new IPEndPoint(IPAddress.Any, 0);
            }
            else if (addressFamily == AddressFamily.InterNetworkV6)
            {
                endPoint = new IPEndPoint(IPAddress.IPv6Any, 0);
            }
            else if (addressFamily == AddressFamily.Unix)
            {
                // public UnixDomainSocketEndPoint(string path)
                endPoint = (EndPoint)reflectionMethods.UnixDomainSocketEndPointConstructor.Invoke(new[] { "/" });
            }
            else
            {
                throw new NotSupportedException($"Unknown address family: {addressFamily}.");
            }
            // internal EndPoint _rightEndPoint;
            reflectionMethods.RightEndPoint.SetValue(socket, endPoint);
            // private AddressFamily _addressFamily;
            reflectionMethods.AddressFamily.SetValue(socket, addressFamily);

            SocketType sockType = ConvertSocketType(GetSockOpt(fd, SO_TYPE));
            // private SocketType _socketType;
            reflectionMethods.SocketType.SetValue(socket, sockType);

            ProtocolType protocolType = ConvertProtocolType(GetSockOpt(fd, SO_PROTOCOL));
            // private ProtocolType _protocolType;
            reflectionMethods.ProtocolType.SetValue(socket, protocolType);

            return new Socket((System.Net.Sockets.Socket)socket);
        }

        static ReflectionMethods s_reflectionMethods = LookupMethods();

        private class ReflectionMethods
        {
            public MethodInfo SafeCloseSocketCreate;
            public ConstructorInfo SocketConstructor;
            public FieldInfo RightEndPoint;
            public FieldInfo IsListening;
            public FieldInfo SocketType;
            public FieldInfo AddressFamily;
            public FieldInfo ProtocolType;
            public ConstructorInfo UnixDomainSocketEndPointConstructor;
        }

        private static ReflectionMethods LookupMethods()
        {
            Assembly socketAssembly = typeof(System.Net.Sockets.Socket).GetTypeInfo().Assembly;
            Type safeCloseSocketType = socketAssembly.GetType("System.Net.Sockets.SafeSocketHandle") ?? // .NET Core 3.0+
                                       socketAssembly.GetType("System.Net.Sockets.SafeCloseSocket");
            if (safeCloseSocketType == null)
            {
                ThrowNotSupported(nameof(safeCloseSocketType));
            }
            MethodInfo safeCloseSocketCreate = safeCloseSocketType.GetTypeInfo().GetMethod("CreateSocket", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, null, new[] { typeof(IntPtr) }, null);
            if (safeCloseSocketCreate == null)
            {
                // .NET 5
                Type socketPalType = socketAssembly.GetType("System.Net.Sockets.SocketPal");
                safeCloseSocketCreate = socketPalType.GetTypeInfo().GetMethod("CreateSocket", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, null, new[] { typeof(IntPtr) }, null);
                if (safeCloseSocketCreate == null)
                {
                    ThrowNotSupported(nameof(safeCloseSocketCreate));
                }
            }
            ConstructorInfo socketConstructor = typeof(System.Net.Sockets.Socket).GetTypeInfo().GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { safeCloseSocketType }, null);
            if (socketConstructor == null)
            {
                ThrowNotSupported(nameof(socketConstructor));
            }
            FieldInfo rightEndPoint = typeof(System.Net.Sockets.Socket).GetTypeInfo().GetField("_rightEndPoint", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (rightEndPoint == null)
            {
                ThrowNotSupported(nameof(rightEndPoint));
            }
            FieldInfo isListening = typeof(System.Net.Sockets.Socket).GetTypeInfo().GetField("_isListening", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (isListening == null)
            {
                ThrowNotSupported(nameof(isListening));
            }
            FieldInfo socketType = typeof(System.Net.Sockets.Socket).GetTypeInfo().GetField("_socketType", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (socketType == null)
            {
                ThrowNotSupported(nameof(socketType));
            }
            FieldInfo addressFamily = typeof(System.Net.Sockets.Socket).GetTypeInfo().GetField("_addressFamily", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (addressFamily == null)
            {
                ThrowNotSupported(nameof(addressFamily));
            }
            FieldInfo protocolType = typeof(System.Net.Sockets.Socket).GetTypeInfo().GetField("_protocolType", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (protocolType == null)
            {
                ThrowNotSupported(nameof(protocolType));
            }

            // .NET Core 2.1+
            Type unixDomainSocketEndPointType = socketAssembly.GetType("System.Net.Sockets.UnixDomainSocketEndPoint");
            if (unixDomainSocketEndPointType == null)
            {
                // .NET Core 2.0
                Assembly pipeStreamAssembly = typeof(PipeStream).GetTypeInfo().Assembly;
                unixDomainSocketEndPointType = pipeStreamAssembly.GetType("System.Net.Sockets.UnixDomainSocketEndPoint");
            }
            if (unixDomainSocketEndPointType == null)
            {
                ThrowNotSupported(nameof(unixDomainSocketEndPointType));
            }
            ConstructorInfo unixDomainSocketEndPointConstructor = unixDomainSocketEndPointType.GetTypeInfo().GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(string) }, null);
            if (unixDomainSocketEndPointConstructor == null)
            {
                ThrowNotSupported(nameof(unixDomainSocketEndPointConstructor));
            }
            return new ReflectionMethods
            {
                SafeCloseSocketCreate = safeCloseSocketCreate,
                SocketConstructor = socketConstructor,
                RightEndPoint = rightEndPoint,
                IsListening = isListening,
                SocketType = socketType,
                AddressFamily = addressFamily,
                ProtocolType = protocolType,
                UnixDomainSocketEndPointConstructor = unixDomainSocketEndPointConstructor
            };
        }

        private static void ThrowNotSupported(string var)
        {
            throw new NotSupportedException($"Creating a Socket from a file descriptor is not supported on this platform. '{var}' not found.");
        }

        private static SocketType ConvertSocketType(int socketType)
        {
            if (socketType == SOCK_STREAM)
            {
                return SocketType.Stream;
            }
            else if (socketType == SOCK_DGRAM)
            {
                return SocketType.Dgram;
            }
            else if (socketType == SOCK_RAW)
            {
                return SocketType.Raw;
            }
            else if (socketType == SOCK_RDM)
            {
                return SocketType.Rdm;
            }
            else if (socketType == SOCK_SEQPACKET)
            {
                return SocketType.Seqpacket;
            }
            else
            {
                throw new NotSupportedException($"Unknown socket type: SO_TYPE={socketType}.");
            }
        }

        private static AddressFamily ConvertAddressFamily(int addressFamily)
        {
            if (addressFamily == AF_INET)
            {
                return AddressFamily.InterNetwork;
            }
            else if (addressFamily == AF_INET6)
            {
                return AddressFamily.InterNetworkV6;
            }
            else if (addressFamily == AF_UNIX)
            {
                return AddressFamily.Unix;
            }
            else
            {
                throw new NotSupportedException($"Unknown Address Family: SO_DOMAIN={addressFamily}.");
            }
        }

        private static ProtocolType ConvertProtocolType(int protocolType)
        {
            if (protocolType == IPPROTO_ICMP)
            {
                return ProtocolType.Icmp;
            }
            else if (protocolType == IPPROTO_ICMPV6)
            {
                return ProtocolType.IcmpV6;
            }
            else if (protocolType == IPPROTO_TCP)
            {
                return ProtocolType.Tcp;
            }
            else if (protocolType == IPPROTO_UDP)
            {
                return ProtocolType.Udp;
            }
            else
            {
                throw new NotSupportedException($"Unknown protocol type: SO_PROTOCOL={protocolType}.");
            }
        }

        private static unsafe int GetSockOpt(int fd, int optname)
        {
            int val = 0;
            socklen_t optlen = 4;
            int rv = getsockopt(fd, SOL_SOCKET, optname, (byte*)&val, &optlen);
            if (rv == -1)
            {
                PlatformException.Throw();
            }
            return val;
        }
    }
#nullable restore
}
using System;
using System.Buffers;
using System.Runtime.InteropServices;
using Tmds.Linux;
using static Tmds.Linux.LibC;

namespace Tmds.LinuxAsync
{
    static class IoPal
    {
        public static unsafe int Write(SafeHandle handle, ReadOnlySpan<byte> span)
        {
            bool refAdded = false;
            try
            {
                handle.DangerousAddRef(ref refAdded);

                int rv;
                fixed (byte* ptr = span)
                {
                    do
                    {
                        rv = (int)write(handle.DangerousGetHandle().ToInt32(), ptr, span.Length);
                    } while (rv == -1 && errno == EINTR);
                }

                return rv;
            }
            finally
            {
                if (refAdded)
                    handle.DangerousRelease();
            }
        }

        public static unsafe int Read(SafeHandle handle, Span<byte> span)
        {
            bool refAdded = false;
            try
            {
                handle.DangerousAddRef(ref refAdded);

                int rv;
                fixed (byte* ptr = span)
                {
                    do
                    {
                        rv = (int)read(handle.DangerousGetHandle().ToInt32(), ptr, span.Length);
                    } while (rv == -1 && errno == EINTR);
                }

                return rv;
            }
            finally
            {
                if (refAdded)
                    handle.DangerousRelease();
            }
        }
    }
}
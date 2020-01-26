using System;
using System.Diagnostics.CodeAnalysis;

namespace Tmds.LinuxAsync
{
    static class ThrowHelper
    {
        [DoesNotReturn]
        public static void ThrowInvalidOperationException()
        {
            throw new InvalidOperationException();
        }

        [DoesNotReturn]
        public static void ThrowObjectDisposedException<T>()
        {
            throw new ObjectDisposedException(typeof(T).FullName);
        }

        [DoesNotReturn]
        internal static void ThrowIndexOutOfRange<T>(T value)
        {
            throw new IndexOutOfRangeException($"({typeof(T).FullName}){value}");
        }
    }
}

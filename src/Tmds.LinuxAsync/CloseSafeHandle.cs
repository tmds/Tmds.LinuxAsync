using System;
using System.Runtime.InteropServices;

namespace Tmds.LinuxAsync
{
    internal class CloseSafeHandle : SafeHandle
    {
        public CloseSafeHandle()
            : base(new IntPtr(-1), true)
        { }

        protected CloseSafeHandle(int handle)
            : base(new IntPtr(handle), true)
        { }

        internal void SetHandle(int descriptor)
        {
            base.SetHandle((IntPtr)descriptor);
        }

        public override bool IsInvalid
        {
            get { return handle == new IntPtr(-1); }
        }

        protected override bool ReleaseHandle()
        {
            int rv = Tmds.Linux.LibC.close(handle.ToInt32());
            return rv == 0;
        }
    }
}
using System.Runtime.InteropServices;

namespace Tmds.LinuxAsync
{
    public sealed partial class EPollAsyncEngine : AsyncEngine
    {
        private readonly EPollThread _thread; // TODO: multi-threading

        public EPollAsyncEngine()
        {
            _thread = new EPollThread();
        }

        public override void Dispose()
        {
            _thread.Dispose();
        }

        internal override AsyncContext CreateContext(SafeHandle handle)
            => _thread.CreateContext(handle);
    }
}

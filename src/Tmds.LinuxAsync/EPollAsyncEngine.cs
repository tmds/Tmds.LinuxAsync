using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Tmds.LinuxAsync
{
    public sealed partial class EPollAsyncEngine : AsyncEngine
    {
        private readonly EPollThread[] _threads;
        private int _previousThreadIdx = -1;

        public EPollAsyncEngine(int threadCount, bool useLinuxAio)
        {
            _threads = new EPollThread[threadCount];
            for (int i = 0; i < _threads.Length; i++)
            {
                _threads[i] = new EPollThread(useLinuxAio);
            }
        }

        public override void Dispose()
        {
            foreach (var thread in _threads)
            {
                thread.Dispose();
            }
        }

        internal override AsyncContext CreateContext(SafeHandle handle)
        {
            int threadIdx = (int)((uint)Interlocked.Increment(ref _previousThreadIdx) % (uint)_threads.Length);
            return _threads[threadIdx].CreateContext(handle);
        }
    }
}

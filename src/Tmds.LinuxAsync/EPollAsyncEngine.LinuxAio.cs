using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Tmds.Linux;
using static Tmds.Linux.LibC;

namespace Tmds.LinuxAsync
{
    public partial class EPollAsyncEngine
    {
        sealed class LinuxAio : AsyncExecutionQueue
        {
            private const int MemoryAlignment = 8;
            private const int IocbLength = 512;
            private const int NrEvents = 512; // TODO
            private const int IovsPerIocb = 8;

            struct Operation
            {
                public bool IsReadNotWrite;
                public SafeHandle Handle;
                public Memory<byte> Memory;
                public IAsyncExecutionResultHandler ResultHandler;
            }

            private List<Operation>? _scheduledOperations;
            private List<Operation>? _cachedOperationsList;
            private readonly List<MemoryHandle> _memoryHandles;
            private AsyncOperationResult[]? _results;

            private readonly aio_context_t _ctx;
            private readonly IntPtr _aioEventsMemory;
            private readonly IntPtr _aioCbsMemory;
            private readonly IntPtr _aioCbsTableMemory;
            // private readonly IntPtr _ioVectorTableMemory;

            private bool _disposed;

            private unsafe io_event* AioEvents => (io_event*)Align(_aioEventsMemory);
            private unsafe iocb* AioCbs => (iocb*)Align(_aioCbsMemory);
            private unsafe iocb** AioCbsTable => (iocb**)Align(_aioCbsTableMemory);
            // private unsafe iovec* IoVectorTable => (iovec*)Align(_ioVectorTableMemory); // TODO

            public unsafe LinuxAio()
            {
                _memoryHandles = new List<MemoryHandle>();
                try
                {
                    // Memory
                    _aioEventsMemory = AllocMemory(sizeof(io_event) * IocbLength);
                    _aioCbsMemory = AllocMemory(sizeof(iocb) * IocbLength);
                    _aioCbsTableMemory = AllocMemory(IntPtr.Size * IocbLength);
                    // _ioVectorTableMemory = AllocMemory(SizeOf.iovec * IovsPerIocb * _ioCbLength);
                    _scheduledOperations = new List<Operation>();
                    for (int i = 0; i < IocbLength; i++)
                    {
                        AioCbsTable[i] = &AioCbs[i];
                    }

                    // Aio
                    aio_context_t ctx;
                    int rv = io_setup(NrEvents, &ctx);
                    if (rv == -1)
                    {
                        PlatformException.Throw();
                    }
                    _ctx = ctx;
                }
                catch
                {
                    FreeResources();
                }
            }

            public override void AddRead(SafeHandle handle, Memory<byte> memory, IAsyncExecutionResultHandler callback, int data)
            {
                if (memory.Length == 0)
                {
                    ThrowHelper.ThrowArgumentException(nameof(memory));
                }
                if (_scheduledOperations == null)
                {
                    _scheduledOperations = new List<Operation>();
                }
                _scheduledOperations.Add(new Operation { Handle = handle, Memory = memory, IsReadNotWrite = true, ResultHandler = callback });
            }

            public override void AddWrite(SafeHandle handle, Memory<byte> memory, IAsyncExecutionResultHandler callback, int data)
            {
                if (_scheduledOperations == null)
                {
                    _scheduledOperations = new List<Operation>();
                }
                _scheduledOperations.Add(new Operation { Handle = handle, Memory = memory, IsReadNotWrite = false, ResultHandler = callback });
            }

            public unsafe bool ExecuteOperations()
            {
                List<Operation>? scheduled = _scheduledOperations;
                if (scheduled == null)
                {
                    return false;
                }
                _scheduledOperations = _cachedOperationsList;
                _cachedOperationsList = null;

                if (_results == null || _results.Length < scheduled.Count)
                {
                    _results = new AsyncOperationResult[Math.Max(scheduled.Count, 2 * (_results?.Length ?? 0))];
                }
                AsyncOperationResult[] results = _results;

                int queueLength = scheduled.Count;
                int queueOffset = 0;
                while (queueOffset < queueLength)
                {
                    int nr = Math.Min((queueLength - queueOffset), IocbLength);
                    try
                    {
                        iocb* aioCbs = AioCbs;
                        for (int i = queueOffset; i < (queueOffset + nr); i++)
                        {
                            Operation op = scheduled[i];
                            int fd = op.Handle.DangerousGetHandle().ToInt32(); // TODO: make safe

                            MemoryHandle handle = op.Memory.Pin();
                            _memoryHandles.Add(handle);

                            aioCbs->aio_fildes = fd;
                            aioCbs->aio_data = (ulong)i;
                            aioCbs->aio_lio_opcode = op.IsReadNotWrite ? IOCB_CMD_PREAD : IOCB_CMD_PWRITE;
                            aioCbs->aio_buf = (ulong)handle.Pointer;
                            aioCbs->aio_nbytes = (ulong)op.Memory.Length;
                            aioCbs++;
                        }
                        iocb** iocbpp = AioCbsTable;
                        int toSubmit = nr;
                        while (toSubmit > 0)
                        {
                            int rv = io_submit(_ctx, toSubmit, iocbpp);
                            if (rv == -1)
                            {
                                PlatformException.Throw();
                            }
                            int toReceive = rv;
                            toSubmit -= rv;
                            iocbpp += rv;
                            while (toReceive > 0)
                            {
                                do
                                {
                                    rv = IoGetEvents(_ctx, toReceive, AioEvents);
                                } while (rv == -1 && errno == EINTR);
                                if (rv == -1)
                                {
                                    PlatformException.Throw();
                                }
                                io_event* events = AioEvents;
                                for (int i = 0; i < rv; i++)
                                {
                                    results[(int)events->data] = new AsyncOperationResult(events->res);
                                    events++;
                                }
                                toReceive -= rv;
                            }
                        }
                    }
                    finally
                    {
                        foreach (var handle in _memoryHandles)
                        {
                            handle.Dispose();
                        }
                        _memoryHandles.Clear();
                    }

                    // Callbacks
                    for (int i = queueOffset; i < (queueOffset + nr); i++)
                    {
                        Operation op = scheduled[i];
                        op.ResultHandler.HandleAsyncResult(results[i]);
                    }

                    queueOffset += nr;
                }

                scheduled.Clear();
                if (_scheduledOperations == null)
                {
                    _scheduledOperations = scheduled;
                    return false;
                }
                else
                {
                    _cachedOperationsList = scheduled;
                    return _scheduledOperations.Count > 0;
                }
            }

            private static unsafe int IoGetEvents(aio_context_t ctx, int nr, io_event* events)
            {
                Debug.Assert(nr != 0);

                // Check the ring buffer to avoid making a syscall.
                aio_ring* pRing = ctx.ring;
                if (pRing->magic == 0xa10a10a1 && pRing->incompat_features == 0)
                {
                    int head = (int)pRing->head;
                    int tail = (int)pRing->tail;
                    int available = tail - head;
                    if (available < 0)
                    {
                        available += (int)pRing->nr;
                    }
                    if (available >= nr)
                    {
                        io_event* ringEvents = (io_event*)((byte*)pRing + pRing->header_length);
                        io_event* start = ringEvents + head;
                        io_event* end = start + nr;
                        if (head + nr > pRing->nr)
                        {
                            end -= pRing->nr;
                        }
                        if (end > start)
                        {
                            Copy(start, end, events);
                        }
                        else
                        {
                            io_event* eventsEnd = Copy(start, ringEvents + pRing->nr, events);
                            Copy(ringEvents, end, eventsEnd);
                        }
                        head += nr;
                        if (head >= pRing->nr)
                        {
                            head -= (int)pRing->nr;
                        }
                        pRing->head = (uint)head;
                        return nr;
                    }
                }

                return io_getevents(ctx, nr, nr, events, null);
            }

            private static unsafe io_event* Copy(io_event* start, io_event* end, io_event* dst)
            {
                uint byteCount = (uint)((byte*)end - (byte*)start);
                Unsafe.CopyBlock(dst, start, byteCount);
                return (io_event*)((byte*)dst + byteCount);
            }

            private unsafe void* Align(IntPtr p)
            {
                ulong pointer = (ulong)p;
                pointer += MemoryAlignment - 1;
                pointer &= ~(ulong)(MemoryAlignment - 1);
                return (void*)pointer;
            }
            private unsafe IntPtr AllocMemory(int length)
            {
                IntPtr res = Marshal.AllocHGlobal(length + MemoryAlignment - 1);
                Span<byte> span = new Span<byte>(Align(res), length);
                span.Clear();
                return res;
            }

            protected unsafe override void Dispose(bool disposing)
            {
                // TODO: complete pending operations.

                FreeResources();
            }

            private unsafe void FreeResources()
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;

                if (_aioEventsMemory != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_aioEventsMemory);
                }
                if (_aioCbsMemory != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_aioCbsMemory);
                }
                if (_aioCbsTableMemory != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_aioCbsTableMemory);
                }
                // if (_ioVectorTableMemory != IntPtr.Zero)
                // {
                //     Marshal.FreeHGlobal(_ioVectorTableMemory);
                // }
                if (_ctx.ring != null)
                {
                    io_destroy(_ctx);
                }
            }

            public override void AddPollIn(SafeHandle handle, IAsyncExecutionResultHandler asyncExecutionCallback, int data)
            {
                throw new NotSupportedException();
            }

            public override void AddPollOut(SafeHandle handle, IAsyncExecutionResultHandler asyncExecutionCallback, int data)
            {
                throw new NotSupportedException();
            }

            public override void AddCancel(SafeHandle handle, int data)
            {
                throw new NotSupportedException();
            }
        }
    }
}
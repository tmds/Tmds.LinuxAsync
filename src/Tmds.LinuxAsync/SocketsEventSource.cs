using System.Diagnostics.Tracing;

namespace Tmds.LinuxAsync
{
    [EventSource(Name = "SocketsEventSource")]
    internal class SocketsEventSource : EventSource
    {
        [Event(1, Level = EventLevel.LogAlways)]
        public void SocketEventsPerWait(int eventsCount) => WriteEvent(1, eventsCount);

        public static SocketsEventSource Log = new SocketsEventSource();
    }
}
using System.Diagnostics.Tracing;

namespace Tmds.LinuxAsync
{
    [EventSource(Name = "SocketsEventSource")]
    internal class SocketsEventSource : EventSource
    {
        private readonly EventCounter _socketEventsPerWait;

        private SocketsEventSource()
        {
            _socketEventsPerWait = new EventCounter("EventsPerWait", this);
        }

        private static readonly SocketsEventSource s_source = new SocketsEventSource();

        public static void SocketEventsPerWait(int eventsCount)
            => s_source._socketEventsPerWait.WriteMetric(eventsCount);
    }
}
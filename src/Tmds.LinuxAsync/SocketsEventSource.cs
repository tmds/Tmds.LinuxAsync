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

        public void SocketEventsPerWait(int eventsCount)
            => _socketEventsPerWait.WriteMetric(eventsCount);

        public static readonly SocketsEventSource Log = new SocketsEventSource();
    }
}
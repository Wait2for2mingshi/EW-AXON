using System;

namespace EW_Assistant.Services
{
    public sealed class PerformanceEvent
    {
        public string EventType { get; set; } = string.Empty;
        public DateTime StartTime { get; set; }
        public string RelatedProcess { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }
}

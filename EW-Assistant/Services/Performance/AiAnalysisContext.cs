using System;
using System.Collections.Generic;

namespace EW_Assistant.Services
{
    public sealed class AiAnalysisContext
    {
        public DateTime Timestamp { get; set; }
        public float CurrentCpuUsage { get; set; }
        public float AverageCpuUsage5Min { get; set; }
        public string TopProcessSummary { get; set; } = string.Empty;
        public string EventSummary { get; set; } = string.Empty;
        public string HistoricalComparison { get; set; } = string.Empty;
        public List<ProcessSnapshot> TopProcesses { get; set; } = new List<ProcessSnapshot>();
        public List<PerformanceEvent> RecentEvents { get; set; } = new List<PerformanceEvent>();
    }
}

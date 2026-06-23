using System;

namespace EW_Assistant.Services
{
    public sealed class MemorySnapshot
    {
        public DateTime Timestamp { get; set; }
        public long TotalMemoryMb { get; set; }
        public long UsedMemoryMb { get; set; }
        public long AvailableMemoryMb { get; set; }
        public float UsagePercent { get; set; }
    }
}

using System;

namespace EW_Assistant.Services
{
    public sealed class DiskUsageSnapshot
    {
        public string DriveLetter { get; set; } = string.Empty;
        public long TotalGb { get; set; }
        public long FreeGb { get; set; }
        public float UsagePercent { get; set; }
    }
}

using System;

namespace EW_Assistant.Services
{
    public sealed class AiAnalysisRecord
    {
        public DateTime Timestamp { get; set; }
        public string Severity { get; set; } = "Info";
        public string SeverityLabel { get; set; } = "提示";
        public string Summary { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
    }
}

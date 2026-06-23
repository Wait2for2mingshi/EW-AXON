using System;

namespace EW_Assistant.Services
{
    public static class AiAnalysisRecordBuilder
    {
        public static AiAnalysisRecord Build(string content, DateTime timestamp)
        {
            var normalized = DifyOutputSanitizer.Clean(content);
            var severity = InferSeverity(normalized);
            return new AiAnalysisRecord
            {
                Timestamp = timestamp,
                Severity = severity,
                SeverityLabel = BuildSeverityLabel(severity),
                Content = normalized,
                Summary = BuildSummary(normalized)
            };
        }

        private static string InferSeverity(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "Info";

            var value = text.ToLowerInvariant();
            if (value.Contains("严重") || value.Contains("高风险") || value.Contains("高危") || value.Contains("critical") || value.Contains("紧急"))
                return "Critical";
            if (value.Contains("警告") || value.Contains("warning") || value.Contains("中风险") || value.Contains("异常"))
                return "Warning";
            if (value.Contains("提示") || value.Contains("低风险") || value.Contains("轻微"))
                return "Info";

            return "Info";
        }

        private static string BuildSummary(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "无分析内容";

            var normalized = text.Replace("\r", " ").Replace("\n", " ").Trim();
            if (normalized.Length <= 120)
                return normalized;

            return normalized.Substring(0, 120) + "...";
        }

        private static string BuildSeverityLabel(string severity)
        {
            switch (severity)
            {
                case "Critical":
                    return "严重";
                case "Warning":
                    return "警告";
                default:
                    return "提示";
            }
        }
    }
}

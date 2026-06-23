using System;
using System.Collections.Generic;

namespace EW_Assistant.Warnings
{
    /// <summary>
    /// 预警工单记录，用于闭环状态跟踪。
    /// </summary>
    public class WarningTicketRecord
    {
        /// <summary>唯一指纹（通常由规则和时间段组成）。</summary>
        public string Fingerprint { get; set; }
        public string RuleId { get; set; }
        public string RuleName { get; set; }
        public string Level { get; set; }
        public string Type { get; set; }

        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }

        public string Summary { get; set; }
        public string MetricName { get; set; }
        public double CurrentValue { get; set; }
        public double? BaselineValue { get; set; }
        public double? ThresholdValue { get; set; }

        /// <summary>工单状态：Active / Acknowledged / Ignored / Processed / Resolved。</summary>
        public string Status { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime FirstSeen { get; set; }
        public DateTime LastSeen { get; set; }
        public DateTime? AcknowledgedAt { get; set; }
        public DateTime? ProcessedAt { get; set; }
        public DateTime? IgnoredUntil { get; set; }
        /// <summary>累计出现次数，用于抑制重复提示。</summary>
        public int OccurrenceCount { get; set; }
    }
}

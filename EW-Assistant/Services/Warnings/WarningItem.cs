using System;

namespace EW_Assistant.Warnings
{
    /// <summary>
    /// 预警条目模型，供后续 UI / AI 消费。
    /// </summary>
    public class WarningItem
    {
        /// <summary>预警唯一键（通常由 RuleId + 时间段拼接）。</summary>
        public string Key { get; set; }
        public string RuleId { get; set; }           // 规则标识，例如 LOW_YIELD_ABS
        public string RuleName { get; set; }         // 规则展示名称

        public string Level { get; set; }            // "Critical" / "Warning" / "Info"
        public string Type { get; set; }             // "Yield" / "Throughput" / "Alarm" / "Combined"

        public DateTime StartTime { get; set; }      // 覆盖起始时间
        public DateTime EndTime { get; set; }        // 覆盖结束时间
        public DateTime FirstDetected { get; set; }  // 第一次触发时间
        public DateTime LastDetected { get; set; }   // 最近一次触发时间

        public string MetricName { get; set; }       // 相关指标名（良率 / 产量 / 报警次数等）
        public double CurrentValue { get; set; }     // 当前值
        public double? BaselineValue { get; set; }   // 基线值
        public double? ThresholdValue { get; set; }  // 阈值

        public string Status { get; set; }           // Active / Closed
        public string Summary { get; set; }          // 简要描述
    }
}

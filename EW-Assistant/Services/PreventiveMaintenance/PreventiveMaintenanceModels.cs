using System;
using System.Collections.Generic;

namespace EW_Assistant.Services.PreventiveMaintenance
{
    public sealed class PreventiveMaintenanceReport
    {
        public DateTime GeneratedAt { get; set; }
        public DateTime AnchorDate { get; set; }
        public int WindowDays { get; set; }
        public DateTime CurrentStart { get; set; }
        public DateTime CurrentEnd { get; set; }
        public DateTime BaselineStart { get; set; }
        public DateTime BaselineEnd { get; set; }
        public string AlarmRoot { get; set; } = string.Empty;
        public string ProductionRoot { get; set; } = string.Empty;
        public int OverallRiskScore { get; set; }
        public string OverallRiskLevel { get; set; } = "低风险";
        public string OverallSummary { get; set; } = string.Empty;
        public int CurrentAlarmCount { get; set; }
        public int BaselineAlarmCount { get; set; }
        public double CurrentDowntimeMinutes { get; set; }
        public double BaselineDowntimeMinutes { get; set; }
        public int CurrentPass { get; set; }
        public int CurrentFail { get; set; }
        public int BaselinePass { get; set; }
        public int BaselineFail { get; set; }
        public double CurrentYield { get; set; }
        public double BaselineYield { get; set; }
        public IList<PreventiveRiskItem> RiskItems { get; set; } = new List<PreventiveRiskItem>();
        public IList<PreventiveDailyTrend> DailyTrends { get; set; } = new List<PreventiveDailyTrend>();
        public IList<PreventiveCategoryTrend> CategoryTrends { get; set; } = new List<PreventiveCategoryTrend>();
    }

    public sealed class PreventiveRiskItem
    {
        public string Code { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public int RiskScore { get; set; }
        public string RiskLevel { get; set; } = "低风险";
        public int CurrentCount { get; set; }
        public int BaselineCount { get; set; }
        public int CountDelta { get; set; }
        public double CountRatio { get; set; }
        public double CurrentDowntimeMinutes { get; set; }
        public double BaselineDowntimeMinutes { get; set; }
        public double DowntimeDeltaMinutes { get; set; }
        public int ActiveDays { get; set; }
        public string ReasonSummary { get; set; } = string.Empty;
        public string SuggestedChecks { get; set; } = string.Empty;
    }

    public sealed class PreventiveDailyTrend
    {
        public DateTime Day { get; set; }
        public string DayText => Day.ToString("MM-dd");
        public int AlarmCount { get; set; }
        public double DowntimeMinutes { get; set; }
        public int Pass { get; set; }
        public int Fail { get; set; }
        public int Total => Pass + Fail;
        public double Yield => Total <= 0 ? 0d : (double)Pass / Total;
        public string YieldText => Total <= 0 ? "-" : Yield.ToString("P2");
    }

    public sealed class PreventiveCategoryTrend
    {
        public string Category { get; set; } = string.Empty;
        public int CurrentCount { get; set; }
        public int BaselineCount { get; set; }
        public int Delta => CurrentCount - BaselineCount;
        public double CurrentDowntimeMinutes { get; set; }
        public double BaselineDowntimeMinutes { get; set; }
    }

    public sealed class PreventiveMaintenanceAiResult
    {
        public bool IsSuccess { get; set; }
        public string Markdown { get; set; } = string.Empty;
        public string ErrorMessage { get; set; } = string.Empty;
    }
}

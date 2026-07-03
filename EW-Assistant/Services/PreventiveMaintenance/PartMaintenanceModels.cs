using System;
using System.Collections.Generic;

namespace EW_Assistant.Services.PreventiveMaintenance
{
    public enum PartMaintenanceKind
    {
        Cylinder,
        Vacuum
    }

    public sealed class PartMaintenanceFileSummary
    {
        public DateTime Date { get; set; }
        public PartMaintenanceKind Kind { get; set; }
        public string SourceName { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public int SampleCount { get; set; }
        public int AbnormalCount { get; set; }
        public double NumericAverage { get; set; }
        public double NumericMax { get; set; }
        public bool HasNumericValue { get; set; }
        public bool HasAbnormal => AbnormalCount > 0;
        public List<PartMaintenanceComponentFileSummary> Components { get; } = new List<PartMaintenanceComponentFileSummary>();
    }

    public sealed class PartMaintenanceComponentFileSummary
    {
        public DateTime Date { get; set; }
        public PartMaintenanceKind Kind { get; set; }
        public string ComponentName { get; set; } = string.Empty;
        public string SourceName { get; set; } = string.Empty;
        public int SampleCount { get; set; }
        public int AbnormalCount { get; set; }
        public double AverageValue { get; set; }
        public double MaxValue { get; set; }
        public double LatestValue { get; set; }
        public bool HasNumericValue { get; set; }
        public bool HasAbnormal => AbnormalCount > 0;
    }

    public sealed class PartMaintenanceTrendPoint
    {
        public DateTime Date { get; set; }
        public PartMaintenanceKind Kind { get; set; }
        public string Label => Date.ToString("MM-dd");
        public int FileCount { get; set; }
        public int SampleCount { get; set; }
        public int AbnormalCount { get; set; }
        public double Value { get; set; }
        public bool HasNumericValue { get; set; }
        public bool HasAbnormal { get; set; }
        public string SourceNames { get; set; } = string.Empty;
    }

    public sealed class PartMaintenanceRisk
    {
        public string PartName { get; set; } = string.Empty;
        public string RiskLevel { get; set; } = "低风险";
        public int RiskScore { get; set; }
        public string Summary { get; set; } = string.Empty;
        public string Suggestion { get; set; } = string.Empty;
    }

    public sealed class PartMaintenanceComponentStatus
    {
        public string PartType { get; set; } = string.Empty;
        public string ComponentName { get; set; } = string.Empty;
        public string SourceNames { get; set; } = string.Empty;
        public int SampleCount { get; set; }
        public int AbnormalCount { get; set; }
        public double AverageValue { get; set; }
        public double MaxValue { get; set; }
        public double LatestValue { get; set; }
        public double HomeAverageValue { get; set; }
        public double HomeMaxValue { get; set; }
        public double HomeLatestValue { get; set; }
        public string HomeRiskLevel { get; set; } = string.Empty;
        public int HomeRiskScore { get; set; }
        public double WorkAverageValue { get; set; }
        public double WorkMaxValue { get; set; }
        public double WorkLatestValue { get; set; }
        public string WorkRiskLevel { get; set; } = string.Empty;
        public int WorkRiskScore { get; set; }
        public string RiskLevel { get; set; } = "正常";
        public int RiskScore { get; set; }
        public string Summary { get; set; } = string.Empty;
        public string Suggestion { get; set; } = string.Empty;
        public List<PartMaintenanceTrendPoint> Trend { get; } = new List<PartMaintenanceTrendPoint>();
    }

    public sealed class PartMaintenanceReport
    {
        public string RootPath { get; set; } = string.Empty;
        public DateTime? LatestDate { get; set; }
        public int FileCount { get; set; }
        public List<PartMaintenanceTrendPoint> CylinderTrend { get; } = new List<PartMaintenanceTrendPoint>();
        public List<PartMaintenanceTrendPoint> VacuumTrend { get; } = new List<PartMaintenanceTrendPoint>();
        public List<PartMaintenanceComponentStatus> CylinderStatuses { get; } = new List<PartMaintenanceComponentStatus>();
        public List<PartMaintenanceComponentStatus> VacuumStatuses { get; } = new List<PartMaintenanceComponentStatus>();
        public List<PartMaintenanceRisk> Risks { get; } = new List<PartMaintenanceRisk>();
        public string StatusMessage { get; set; } = string.Empty;
    }
}

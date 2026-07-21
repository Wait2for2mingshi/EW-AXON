using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

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
        public string Label => Date.ToString("MM-dd");
        public int SampleCount { get; set; }
        public int AbnormalCount { get; set; }
        public double Value { get; set; }
        public bool HasAbnormal { get; set; }
    }

    public sealed class PartMaintenanceComponentStatus : INotifyPropertyChanged
    {
        private string _aiSuggestionTitle = "AI分析建议";
        private string _aiSuggestionDisplayText = string.Empty;
        private string _aiSuggestionFailureMessage = string.Empty;
        private bool _isAiSuggestionRunning;
        private double _aiSuggestionProgress;

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
        public string Suggestion { get; set; } = string.Empty;
        public List<PartMaintenanceTrendPoint> Trend { get; } = new List<PartMaintenanceTrendPoint>();
        public List<PartMaintenanceTrendPoint> HomeTrend { get; } = new List<PartMaintenanceTrendPoint>();
        public List<PartMaintenanceTrendPoint> WorkTrend { get; } = new List<PartMaintenanceTrendPoint>();
        public string AiSuggestionTitle { get => _aiSuggestionTitle; set => Set(ref _aiSuggestionTitle, value ?? string.Empty); }
        public string AiSuggestionDisplayText { get => _aiSuggestionDisplayText; set => Set(ref _aiSuggestionDisplayText, value ?? string.Empty); }
        public string AiSuggestionFailureMessage { get => _aiSuggestionFailureMessage; set => Set(ref _aiSuggestionFailureMessage, value ?? string.Empty); }
        public bool IsAiSuggestionRunning { get => _isAiSuggestionRunning; set => Set(ref _isAiSuggestionRunning, value); }
        public double AiSuggestionProgress { get => _aiSuggestionProgress; set => Set(ref _aiSuggestionProgress, value); }

        public event PropertyChangedEventHandler PropertyChanged;

        private void Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return;

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public sealed class PartMaintenanceReport
    {
        public DateTime? LatestDate { get; set; }
        public int FileCount { get; set; }
        public List<PartMaintenanceComponentStatus> CylinderStatuses { get; } = new List<PartMaintenanceComponentStatus>();
        public List<PartMaintenanceComponentStatus> VacuumStatuses { get; } = new List<PartMaintenanceComponentStatus>();
        public string StatusMessage { get; set; } = string.Empty;
    }
}

using System;
using System.Collections.Generic;

namespace EW_Assistant.Domain.MachineStates
{
    /// <summary>
    /// MACHINESTATE CSV 的单行状态片段。
    /// </summary>
    public class MachineStateRecord
    {
        public int RowNumber { get; set; }
        public DateTime? StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public double DurationSeconds { get; set; }
        public string StateCode { get; set; }
        public string MachineState { get; set; }
        public string Entity { get; set; }
        public string Station { get; set; }
        public string Line { get; set; }
        public string Rfid { get; set; }
        public string ErrorCode { get; set; }
        public string ErrorMessage { get; set; }
        public string ErrorDetail { get; set; }
    }

    public class MachineStateReadResult
    {
        public MachineStateReadResult()
        {
            Records = new List<MachineStateRecord>();
            Warnings = new List<string>();
        }

        public string SourceFilePath { get; set; }
        public string SourceFileName { get; set; }
        public string SourceHash { get; set; }
        public int RowCount { get; set; }
        public IList<MachineStateRecord> Records { get; set; }
        public IList<string> Warnings { get; set; }
    }

    public class MachineStateAnalysisData
    {
        public MachineStateAnalysisData()
        {
            StateSummaries = new List<MachineStateStateSummary>();
            Hourly = new List<MachineStateHourStat>();
            TopErrors = new List<MachineStateErrorSummary>();
            TopEntities = new List<MachineStateTargetSummary>();
            TopStations = new List<MachineStateTargetSummary>();
            TopLines = new List<MachineStateTargetSummary>();
            Warnings = new List<string>();
        }

        public string SourceFileName { get; set; }
        public string SourceFilePath { get; set; }
        public string SourceHash { get; set; }
        public DateTime GeneratedAt { get; set; }
        public int RowCount { get; set; }
        public int ParsedRowCount { get; set; }
        public int SkippedRowCount { get; set; }
        public int DeviceCount { get; set; }
        public int StationCount { get; set; }
        public int LineCount { get; set; }
        public DateTime? StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public double TotalObservedSeconds { get; set; }
        public double RunningSeconds { get; set; }
        public double IdleSeconds { get; set; }
        public double AbnormalSeconds { get; set; }
        public double PlannedDowntimeSeconds { get; set; }
        public double MaintenanceSeconds { get; set; }
        public double UnknownSeconds { get; set; }
        public double RunningRate { get; set; }
        public double IdleRate { get; set; }
        public double AbnormalRate { get; set; }
        public double PlannedDowntimeRate { get; set; }
        public double MaintenanceRate { get; set; }
        public double UnknownRate { get; set; }
        public IList<MachineStateStateSummary> StateSummaries { get; set; }
        public IList<MachineStateHourStat> Hourly { get; set; }
        public IList<MachineStateErrorSummary> TopErrors { get; set; }
        public IList<MachineStateTargetSummary> TopEntities { get; set; }
        public IList<MachineStateTargetSummary> TopStations { get; set; }
        public IList<MachineStateTargetSummary> TopLines { get; set; }
        public IList<string> Warnings { get; set; }
    }

    public class MachineStateStateSummary
    {
        public string StateCode { get; set; }
        public string StateName { get; set; }
        public string Category { get; set; }
        public int Count { get; set; }
        public double DurationSeconds { get; set; }
        public double Rate { get; set; }
    }

    public class MachineStateHourStat
    {
        public DateTime Hour { get; set; }
        public double RunningSeconds { get; set; }
        public double IdleSeconds { get; set; }
        public double AbnormalSeconds { get; set; }
        public double PlannedDowntimeSeconds { get; set; }
        public double MaintenanceSeconds { get; set; }
        public double UnknownSeconds { get; set; }
        public double TotalSeconds { get; set; }
    }

    public class MachineStateErrorSummary
    {
        public string ErrorMessage { get; set; }
        public string ErrorCode { get; set; }
        public string Category { get; set; }
        public int Count { get; set; }
        public double DurationSeconds { get; set; }
        public double Rate { get; set; }
    }

    public class MachineStateTargetSummary
    {
        public string Name { get; set; }
        public int Count { get; set; }
        public double DowntimeSeconds { get; set; }
        public double AbnormalSeconds { get; set; }
        public double PlannedDowntimeSeconds { get; set; }
        public double MaintenanceSeconds { get; set; }
    }

    public class MachineStateReportInfo
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string DateLabel { get; set; }
        public string FilePath { get; set; }
        public string FileName { get; set; }
        public DateTime GeneratedAt { get; set; }
        public DateTime? StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public string SourceHash { get; set; }
        public long FileSize { get; set; }
        public string FileSizeText { get; set; }
    }
}

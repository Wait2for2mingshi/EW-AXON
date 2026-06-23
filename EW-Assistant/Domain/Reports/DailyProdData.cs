using System;
using System.Collections.Generic;

namespace EW_Assistant.Domain.Reports
{
    /// <summary>
    /// 当日产能报表的本地统计数据。
    /// </summary>
    public class DailyProdData
    {
        public DailyProdData()
        {
            PeakHours = new List<DailyProdHourStat>();
            ValleyHours = new List<DailyProdHourStat>();
            Downtimes = new List<DowntimeWindow>();
            Hours = new List<DailyProdHourStat>();
            Warnings = new List<string>();
            MetricDefinitions = new List<ReportMetricDefinition>();
            StateSummary = new ReportMachineStateSummary();
        }

        public DateTime Date { get; set; }
        public string Machine { get; set; }

        public int DayPass { get; set; }
        public int DayFail { get; set; }
        public int DayTotal { get; set; }
        public double DayYield { get; set; }
        public double AvgUph { get; set; }
        public double RunningUph { get; set; }
        public int Tossing { get; set; }
        public double TossingRate { get; set; }
        public int ActiveHours { get; set; }
        public double ActiveRate { get; set; }
        public double Cv { get; set; }

        public IList<DailyProdHourStat> PeakHours { get; set; }
        public IList<DailyProdHourStat> ValleyHours { get; set; }
        public IList<DowntimeWindow> Downtimes { get; set; }
        public IList<DailyProdHourStat> Hours { get; set; }
        public IList<ReportMetricDefinition> MetricDefinitions { get; set; }
        public ReportMachineStateSummary StateSummary { get; set; }
        public IList<string> Warnings { get; set; }

        public bool HasStateData
        {
            get { return StateSummary != null && StateSummary.TotalObservedSeconds > 0d; }
        }

        public bool ShouldSerializeStateSummary()
        {
            return HasStateData;
        }

        public bool ShouldSerializeRunningUph()
        {
            return HasStateData;
        }
    }

    public class DailyProdHourStat
    {
        public int Hour { get; set; }
        public int Pass { get; set; }
        public int Fail { get; set; }
        public int Total { get; set; }
        public double Yield { get; set; }
        public double Uph { get; set; }
        public int Tossing { get; set; }
        public double TossingRate { get; set; }
        public double RunningSeconds { get; set; }
        public double IdleSeconds { get; set; }
        public double LineDownSeconds { get; set; }
        public double StateUnknownSeconds { get; set; }
    }

    public class DowntimeWindow
    {
        public int StartHour { get; set; }
        public int EndHour { get; set; }
        public int DurationHours { get; set; }
    }

    public class ReportMetricDefinition
    {
        public string Name { get; set; }
        public string Description { get; set; }
    }

    public class ReportMachineStateSummary
    {
        public ReportMachineStateSummary()
        {
            SourceFileNames = new List<string>();
            Hours = new List<ReportMachineStateHourStat>();
            Days = new List<ReportMachineStateDayStat>();
            TopLineDownAlarms = new List<ReportMachineStateAlarmStat>();
            Warnings = new List<string>();
        }

        public string SourceRoot { get; set; }
        public IList<string> SourceFileNames { get; set; }
        public int RowCount { get; set; }
        public int ParsedRowCount { get; set; }
        public DateTime? StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public double TotalObservedSeconds { get; set; }
        public double RunningSeconds { get; set; }
        public double IdleSeconds { get; set; }
        public double LineDownSeconds { get; set; }
        public double OtherSeconds { get; set; }
        public double RunningRate { get; set; }
        public double IdleRate { get; set; }
        public double LineDownRate { get; set; }
        public IList<ReportMachineStateHourStat> Hours { get; set; }
        public IList<ReportMachineStateDayStat> Days { get; set; }
        public IList<ReportMachineStateAlarmStat> TopLineDownAlarms { get; set; }
        public IList<string> Warnings { get; set; }
    }

    public class ReportMachineStateHourStat
    {
        public int Hour { get; set; }
        public double RunningSeconds { get; set; }
        public double IdleSeconds { get; set; }
        public double LineDownSeconds { get; set; }
        public double OtherSeconds { get; set; }
        public double TotalSeconds { get; set; }
        public double RunningRate { get; set; }
        public double IdleRate { get; set; }
        public double LineDownRate { get; set; }
    }

    public class ReportMachineStateDayStat
    {
        public DateTime Date { get; set; }
        public double RunningSeconds { get; set; }
        public double IdleSeconds { get; set; }
        public double LineDownSeconds { get; set; }
        public double OtherSeconds { get; set; }
        public double TotalSeconds { get; set; }
        public double RunningRate { get; set; }
        public double IdleRate { get; set; }
        public double LineDownRate { get; set; }
    }

    public class ReportMachineStateAlarmStat
    {
        public string ErrorCode { get; set; }
        public string ErrorMessage { get; set; }
        public int Count { get; set; }
        public double DurationSeconds { get; set; }
    }
}

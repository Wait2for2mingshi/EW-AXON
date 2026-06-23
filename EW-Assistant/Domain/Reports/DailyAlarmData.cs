using System;
using System.Collections.Generic;

namespace EW_Assistant.Domain.Reports
{
    /// <summary>
    /// 当日报警报表的本地统计数据。
    /// </summary>
    public class DailyAlarmData
    {
        public DailyAlarmData()
        {
            Hours = new List<DailyAlarmHourStat>();
            TopAlarms = new List<AlarmCategoryStat>();
            Warnings = new List<string>();
        }

        public DateTime Date { get; set; }
        public int DayAlarmCount { get; set; }
        public double DayAlarmSeconds { get; set; }
        public double AvgPerAlarmSeconds { get; set; }
        public int ActiveHours { get; set; }
        public HourWindow PeakHour { get; set; }
        public IList<DailyAlarmHourStat> Hours { get; set; }
        public IList<AlarmCategoryStat> TopAlarms { get; set; }
        public IList<string> Warnings { get; set; }
    }

    public class DailyAlarmHourStat
    {
        public int Hour { get; set; }
        public int Pass { get; set; }
        public int Fail { get; set; }
        public int Total { get; set; }
        public double Yield { get; set; }

        public int AlarmCount { get; set; }
        public double AlarmSeconds { get; set; }
        public string TopAlarmCode { get; set; }
        public double TopAlarmSeconds { get; set; }
        public string TopAlarmContent { get; set; }
    }

    public class HourWindow
    {
        public int Hour { get; set; }
        public double Seconds { get; set; }
    }

    public class AlarmCategoryStat
    {
        public string Code { get; set; }
        public string Content { get; set; }
        public int Count { get; set; }
        public double DurationSeconds { get; set; }
    }
}

using System;
using System.Collections.Generic;

namespace EW_Assistant.Domain.Reports
{
    public class WeeklyAlarmData
    {
        public WeeklyAlarmData()
        {
            ByDay = new List<WeeklyAlarmDay>();
            Top = new List<WeeklyAlarmTop>();
            Warnings = new List<string>();
        }

        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }

        public int TotalCount { get; set; }
        public double TotalDurationSeconds { get; set; }
        public int ActiveHours { get; set; }
        public int LowYieldHours { get; set; }
        public double PearsonAlarmYield { get; set; }

        public IList<WeeklyAlarmDay> ByDay { get; set; }
        public IList<WeeklyAlarmTop> Top { get; set; }
        public IList<string> Warnings { get; set; }
    }

    public class WeeklyAlarmDay
    {
        public DateTime Date { get; set; }
        public int AlarmCount { get; set; }
        public double AlarmSeconds { get; set; }
        public double Yield { get; set; }
        public string TopAlarm { get; set; }
    }

    public class WeeklyAlarmTop
    {
        public string Code { get; set; }
        public string Content { get; set; }
        public int Count { get; set; }
        public double DurationSeconds { get; set; }
    }
}

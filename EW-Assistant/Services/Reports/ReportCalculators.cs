using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EW_Assistant.Domain.Reports;
using EW_Assistant.Warnings;

namespace EW_Assistant.Services.Reports
{
    /// <summary>
    /// 本地 CSV 计算当日产能数据，补齐 24 小时缺口并提取峰值/低谷/停机区间。
    /// </summary>
    public class DailyProdCalculator
    {
        private readonly ProductionCsvReader _reader;
        private readonly MachineStateReportReader _stateReader;

        public DailyProdCalculator(ProductionCsvReader reader = null, MachineStateReportReader stateReader = null)
        {
            _reader = reader ?? new ProductionCsvReader();
            _stateReader = stateReader ?? new MachineStateReportReader();
        }

        /// <summary>
        /// 汇总指定日期的小时级产能数据，并生成峰谷、停机等辅助信息。
        /// </summary>
        public DailyProdData Calculate(DateTime date)
        {
            var data = new DailyProdData
            {
                Date = date.Date
            };

            try
            {
                var start = date.Date;
                var end = start.AddDays(1);
                var rows = _reader.GetProductionRange(start, end) ?? new List<ProductionHourRecord>();

                var hours = new List<DailyProdHourStat>();
                // 固定补齐 0-23 点，缺失小时填 0，避免后续图表出现断层
                for (int h = 0; h < 24; h++)
                {
                    var rec = rows.FirstOrDefault(r => r.Hour.Hour == h);
                    var pass = rec != null ? rec.Pass : 0;
                    var fail = rec != null ? rec.Fail : 0;
                    var total = pass + fail;
                    var yield = total > 0 ? (double)pass / total : 0d;
                    hours.Add(new DailyProdHourStat
                    {
                        Hour = h,
                        Pass = pass,
                        Fail = fail,
                        Total = total,
                        Yield = yield,
                        Uph = total,
                        Tossing = fail,
                        TossingRate = total > 0 ? (double)fail / total : 0d
                    });
                }

                data.Hours = hours;
                data.DayPass = hours.Sum(x => x.Pass);
                data.DayFail = hours.Sum(x => x.Fail);
                data.DayTotal = data.DayPass + data.DayFail;
                data.DayYield = data.DayTotal > 0 ? (double)data.DayPass / data.DayTotal : 0d;
                data.Tossing = data.DayFail;
                data.TossingRate = data.DayTotal > 0 ? (double)data.Tossing / data.DayTotal : 0d;
                data.ActiveHours = hours.Count(x => x.Total > 0);
                data.AvgUph = data.ActiveHours > 0 ? data.DayTotal / (double)data.ActiveHours : 0d;
                data.ActiveRate = 24 > 0 ? (double)data.ActiveHours / 24d : 0d;
                data.Cv = CalculateCv(hours.Select(h => h.Total));
                ApplyStateSummary(data, start, end);
                data.MetricDefinitions = BuildMetricDefinitions(data.HasStateData);

                MarkPeaksAndValleys(hours, data);
                data.Downtimes = DetectDowntimes(hours);
                if (rows.Count == 0)
                {
                    data.Warnings.Add("未读取到任何小时产能数据，可能缺少当日 CSV。");
                }
            }
            catch (Exception ex)
            {
                data.Warnings.Add("计算产能数据时发生错误：" + ex.Message);
            }

            return data;
        }

        private void ApplyStateSummary(DailyProdData data, DateTime start, DateTime end)
        {
            try
            {
                data.StateSummary = _stateReader.ReadRange(start, end);
                var stateByHour = (data.StateSummary.Hours ?? new List<ReportMachineStateHourStat>())
                    .ToDictionary(h => h.Hour, h => h);

                foreach (var hour in data.Hours ?? new List<DailyProdHourStat>())
                {
                    ReportMachineStateHourStat stateHour;
                    if (!stateByHour.TryGetValue(hour.Hour, out stateHour))
                    {
                        continue;
                    }

                    hour.RunningSeconds = stateHour.RunningSeconds;
                    hour.IdleSeconds = stateHour.IdleSeconds;
                    hour.LineDownSeconds = stateHour.LineDownSeconds;
                    hour.StateUnknownSeconds = stateHour.OtherSeconds;
                }

                data.RunningUph = data.StateSummary.RunningSeconds > 0d
                    ? data.DayTotal / (data.StateSummary.RunningSeconds / 3600d)
                    : 0d;
                AppendStateWarnings(data.Warnings, data.StateSummary);
            }
            catch (Exception ex)
            {
                data.Warnings.Add("读取状态记录表时发生错误：" + ex.Message);
            }
        }

        /// <summary>
        /// 计算变异系数（标准差/均值），用于衡量产能波动。
        /// </summary>
        private static double CalculateCv(IEnumerable<int> values)
        {
            var arr = values?.ToList() ?? new List<int>();
            if (arr.Count == 0) return 0d;
            var mean = arr.Average();
            if (mean == 0) return 0d;
            var variance = arr.Average(v => Math.Pow(v - mean, 2));
            var stddev = Math.Sqrt(variance);
            return stddev / mean;
        }

        /// <summary>
        /// 选取非零小时的前 3 个峰值与低谷，供 UI 标记。
        /// </summary>
        private static void MarkPeaksAndValleys(IList<DailyProdHourStat> hours, DailyProdData data)
        {
            var nonZero = hours.Where(h => h.Total > 0).OrderByDescending(h => h.Total).ToList();
            data.PeakHours = nonZero.Take(3).ToList();
            data.ValleyHours = nonZero.OrderBy(h => h.Total).Take(3).ToList();
        }

        /// <summary>
        /// 检测连续无产出的时段，输出按时长降序的无产出窗口。
        /// </summary>
        private static IList<DowntimeWindow> DetectDowntimes(IList<DailyProdHourStat> hours)
        {
            var list = new List<DowntimeWindow>();
            int start = -1;
            for (int i = 0; i < hours.Count; i++)
            {
                if (hours[i].Total <= 0)
                {
                    if (start < 0) start = i;
                }
                else
                {
                    if (start >= 0)
                    {
                        list.Add(new DowntimeWindow
                        {
                            StartHour = start,
                            EndHour = i,
                            DurationHours = i - start
                        });
                        start = -1;
                    }
                }
            }

            if (start >= 0)
            {
                list.Add(new DowntimeWindow
                {
                    StartHour = start,
                    EndHour = 24,
                    DurationHours = 24 - start
                });
            }

            return list.OrderByDescending(d => d.DurationHours).ThenBy(d => d.StartHour).ToList();
        }

        internal static IList<ReportMetricDefinition> BuildMetricDefinitions(bool includeStateMetrics = false)
        {
            var definitions = new List<ReportMetricDefinition>();
            if (includeStateMetrics)
            {
                definitions.Add(new ReportMetricDefinition { Name = "状态码 1", Description = "正常运行，设备处于有效生产/运行状态。" });
                definitions.Add(new ReportMetricDefinition { Name = "状态码 2", Description = "空闲，设备未处于生产运行，但也未进入 LINEDOWN 报警。" });
                definitions.Add(new ReportMetricDefinition { Name = "状态码 5", Description = "停线报警（LINEDOWN），设备因报警或异常进入停线状态。" });
            }

            definitions.Add(new ReportMetricDefinition { Name = "单位小时产出（UPH）", Description = "按小时桶统计的单位小时产出；无状态记录时按有产出小时计算均值。" });
            if (includeStateMetrics)
            {
                definitions.Add(new ReportMetricDefinition { Name = "运行 UPH", Description = "总产量 / 状态记录表正常运行小时，用于衡量有效运行期间的产出能力。" });
            }
            definitions.Add(new ReportMetricDefinition { Name = "良率（Yield）", Description = "产能良率，按 PASS / (PASS + 抛料/不良) 计算。" });
            definitions.Add(new ReportMetricDefinition { Name = "抛料/不良（Tossing）", Description = "抛料/不良数量，来自 FAIL、NG 或抛料记录。" });
            return definitions;
        }

        internal static void AppendStateWarnings(IList<string> warnings, ReportMachineStateSummary summary)
        {
            if (warnings == null || summary == null || summary.Warnings == null)
            {
                return;
            }

            foreach (var warning in summary.Warnings.Take(8))
            {
                warnings.Add("状态记录表：" + warning);
            }
        }

    }

    /// <summary>
    /// 本地 CSV 计算当日报警数据，并与产能数据对齐，用于报表/看板展示。
    /// </summary>
    public class DailyAlarmCalculator
    {
        private readonly AlarmCsvReader _alarmReader;
        private readonly ProductionCsvReader _prodReader;

        public DailyAlarmCalculator(AlarmCsvReader alarmReader = null, ProductionCsvReader prodReader = null)
        {
            _alarmReader = alarmReader ?? new AlarmCsvReader();
            _prodReader = prodReader ?? new ProductionCsvReader();
        }

        /// <summary>
        /// 汇总指定日期的报警与产能，补齐 24 小时空窗并计算均值、峰值等指标。
        /// </summary>
        public DailyAlarmData Calculate(DateTime date)
        {
            var data = new DailyAlarmData { Date = date.Date };
            try
            {
                var start = date.Date;
                var end = start.AddDays(1);

                var prodRows = _prodReader.GetProductionRange(start, end) ?? new List<ProductionHourRecord>();
                var alarmRows = _alarmReader.GetAlarms(start, end) ?? new List<AlarmHourStat>();

                var hours = new List<DailyAlarmHourStat>();
                for (int h = 0; h < 24; h++)
                {
                    var p = prodRows.FirstOrDefault(r => r.Hour.Hour == h);
                    var alarmHour = alarmRows.Where(a => a.Hour.Hour == h).ToList();

                    var pass = p != null ? p.Pass : 0;
                    var fail = p != null ? p.Fail : 0;
                    var total = pass + fail;
                    var yield = total > 0 ? (double)pass / total : 0d;

                    var alarmCount = alarmHour.Sum(a => a.Count);
                    var alarmSeconds = alarmHour.Sum(a => a.DowntimeMinutes) * 60d;
                    var top = alarmHour.OrderByDescending(a => a.DowntimeMinutes).FirstOrDefault();

                    hours.Add(new DailyAlarmHourStat
                    {
                        Hour = h,
                        Pass = pass,
                        Fail = fail,
                        Total = total,
                        Yield = yield,
                        AlarmCount = alarmCount,
                        AlarmSeconds = alarmSeconds,
                        TopAlarmCode = top != null ? top.Code : "无",
                        TopAlarmSeconds = top != null ? top.DowntimeMinutes * 60d : 0d,
                        TopAlarmContent = string.IsNullOrWhiteSpace(top?.Message) ? "无" : top.Message
                    });
                }

                data.Hours = hours;
                data.DayAlarmCount = hours.Sum(x => x.AlarmCount);
                data.DayAlarmSeconds = hours.Sum(x => x.AlarmSeconds);
                data.ActiveHours = hours.Count(h => h.AlarmSeconds > 0);
                data.AvgPerAlarmSeconds = data.DayAlarmCount > 0 ? data.DayAlarmSeconds / data.DayAlarmCount : 0d;
                data.TopAlarms = alarmRows.GroupBy(a => a.Code ?? "UNKNOWN")
                    .Select(g => new AlarmCategoryStat
                    {
                        Code = string.IsNullOrWhiteSpace(g.Key) ? "UNKNOWN" : g.Key,
                        Content = g.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.Message))?.Message ?? "无",
                        Count = g.Sum(x => x.Count),
                        DurationSeconds = g.Sum(x => x.DowntimeMinutes) * 60d
                    })
                    .OrderByDescending(x => x.DurationSeconds)
                    .Take(8)
                    .ToList();

                var peak = hours.OrderByDescending(h => h.AlarmSeconds).FirstOrDefault(h => h.AlarmSeconds > 0);
                if (peak != null)
                {
                    data.PeakHour = new HourWindow { Hour = peak.Hour, Seconds = peak.AlarmSeconds };
                }

                if (prodRows.Count == 0)
                {
                    data.Warnings.Add("未读取到产能 CSV，产能列填 0。");
                }
                if (alarmRows.Count == 0)
                {
                    data.Warnings.Add("未读取到报警 CSV，报警列填 0。");
                }
            }
            catch (Exception ex)
            {
                data.Warnings.Add("计算报警数据时发生错误：" + ex.Message);
            }

            return data;
        }

    }

    internal static class DailyAlarmStatExtensions
    {
        /// <summary>
        /// 计算当前小时的单次平均报警时长（秒），无报警时返回 0。
        /// </summary>
        public static double AvgSeconds(this DailyAlarmHourStat h)
        {
            if (h == null || h.AlarmCount <= 0) return 0d;
            return h.AlarmSeconds / Math.Max(1, h.AlarmCount);
        }
    }

    /// <summary>
    /// 本地计算周产能，输出与 MCP 工具一致的字段（总量、均值、波动、峰谷）。
    /// </summary>
    public class WeeklyProdCalculator
    {
        private readonly ProductionCsvReader _reader;
        private readonly MachineStateReportReader _stateReader;

        public WeeklyProdCalculator(ProductionCsvReader reader = null, MachineStateReportReader stateReader = null)
        {
            _reader = reader ?? new ProductionCsvReader();
            _stateReader = stateReader ?? new MachineStateReportReader();
        }

        /// <summary>
        /// 汇总起止日期（含）之间的每日产能，计算均值/中位数/波动率等统计。
        /// </summary>
        public WeeklyProdData Calculate(DateTime start, DateTime end)
        {
            var data = new WeeklyProdData
            {
                StartDate = start.Date,
                EndDate = end.Date
            };

            var days = new List<WeeklyProdDay>();
            try
            {
                var day = start.Date;
                while (day <= end.Date)
                {
                    var rows = _reader.GetProductionRange(day, day.AddDays(1)) ?? new List<ProductionHourRecord>();
                    var pass = rows.Sum(r => r.Pass);
                    var fail = rows.Sum(r => r.Fail);
                    var total = pass + fail;
                    var yield = total > 0 ? (double)pass / total : 0d;
                    var activeHours = rows.Count(r => r.Pass + r.Fail > 0);
                    var warning = rows.Count == 0 ? "缺少当天产能数据" : string.Empty;
                    days.Add(new WeeklyProdDay
                    {
                        Date = day,
                        Pass = pass,
                        Fail = fail,
                        Total = total,
                        Yield = yield,
                        ActiveHours = activeHours,
                        Uph = activeHours > 0 ? total / (double)activeHours : 0d,
                        Tossing = fail,
                        TossingRate = total > 0 ? (double)fail / total : 0d,
                        Note = warning
                    });
                    if (!string.IsNullOrWhiteSpace(warning))
                    {
                        data.Warnings.Add(day.ToString("yyyy-MM-dd") + " 缺少产能数据");
                    }
                    day = day.AddDays(1);
                }

                data.Days = days;
                data.Pass = days.Sum(d => d.Pass);
                data.Fail = days.Sum(d => d.Fail);
                data.Total = data.Pass + data.Fail;
                data.Yield = data.Total > 0 ? (double)data.Pass / data.Total : 0d;
                data.Tossing = data.Fail;
                data.TossingRate = data.Total > 0 ? (double)data.Tossing / data.Total : 0d;
                var totalActiveHours = days.Sum(d => d.ActiveHours);
                data.AvgUph = totalActiveHours > 0 ? data.Total / (double)totalActiveHours : 0d;
                data.AvgYield = days.Count > 0 ? days.Average(d => d.Yield) : 0d;
                data.MedianTotal = ComputeMedian(days.Select(d => d.Total).ToList());
                data.Volatility = ComputeCv(days.Select(d => d.Total));
                ApplyStateSummary(data, start.Date, end.Date.AddDays(1));
                data.MetricDefinitions = DailyProdCalculator.BuildMetricDefinitions(data.HasStateData);
                data.LastDay = days.LastOrDefault();
                if (days.Count > 0 && data.LastDay != null)
                {
                    var avgTotal = days.Average(d => d.Total);
                    var avgYield = data.AvgYield;
                    data.LastDayDelta = new Delta
                    {
                        TotalDelta = avgTotal == 0 ? 0 : (data.LastDay.Total - avgTotal) / Math.Max(1, avgTotal),
                        YieldDelta = avgYield == 0 ? 0 : (data.LastDay.Yield - avgYield) / Math.Max(0.0001, avgYield)
                    };
                }

                data.BestDays = days.OrderByDescending(d => d.Total).Take(3).ToList();
                data.WorstDays = days.OrderBy(d => d.Total).Take(3).ToList();
            }
            catch (Exception ex)
            {
                data.Warnings.Add("计算周产能时出错：" + ex.Message);
            }

            return data;
        }

        private void ApplyStateSummary(WeeklyProdData data, DateTime start, DateTime end)
        {
            try
            {
                data.StateSummary = _stateReader.ReadRange(start, end);
                var stateByDay = (data.StateSummary.Days ?? new List<ReportMachineStateDayStat>())
                    .ToDictionary(d => d.Date.Date, d => d);

                foreach (var day in data.Days ?? new List<WeeklyProdDay>())
                {
                    ReportMachineStateDayStat stateDay;
                    if (!stateByDay.TryGetValue(day.Date.Date, out stateDay))
                    {
                        continue;
                    }

                    day.RunningSeconds = stateDay.RunningSeconds;
                    day.IdleSeconds = stateDay.IdleSeconds;
                    day.LineDownSeconds = stateDay.LineDownSeconds;
                    day.RunningRate = stateDay.RunningRate;
                    day.LineDownRate = stateDay.LineDownRate;
                }

                data.RunningUph = data.StateSummary.RunningSeconds > 0d
                    ? data.Total / (data.StateSummary.RunningSeconds / 3600d)
                    : 0d;
                DailyProdCalculator.AppendStateWarnings(data.Warnings, data.StateSummary);
            }
            catch (Exception ex)
            {
                data.Warnings.Add("读取状态记录表时发生错误：" + ex.Message);
            }
        }

        private static int ComputeMedian(IList<int> list)
        {
            if (list == null || list.Count == 0) return 0;
            var arr = list.OrderBy(x => x).ToList();
            int mid = arr.Count / 2;
            if (arr.Count % 2 == 0)
            {
                return (int)Math.Round((arr[mid - 1] + arr[mid]) / 2.0);
            }
            return arr[mid];
        }

        private static double ComputeCv(IEnumerable<int> values)
        {
            var arr = values?.ToList() ?? new List<int>();
            if (arr.Count == 0) return 0d;
            var mean = arr.Average();
            if (mean == 0) return 0d;
            var variance = arr.Average(v => Math.Pow(v - mean, 2));
            var stddev = Math.Sqrt(variance);
            return stddev / mean;
        }
    }

    /// <summary>
    /// 本地计算报警周报数据，对齐 MCP 读取方式，输出 TopN 与每日汇总。
    /// </summary>
    public class WeeklyAlarmCalculator
    {
        private readonly AlarmCsvReader _alarmReader;

        public WeeklyAlarmCalculator(AlarmCsvReader alarmReader = null)
        {
            _alarmReader = alarmReader ?? new AlarmCsvReader();
        }

        public WeeklyAlarmData Calculate(DateTime start, DateTime end)
        {
            var data = new WeeklyAlarmData
            {
                StartDate = start.Date,
                EndDate = end.Date
            };

            try
            {
                var startAt = start.Date;
                var endAt = end.Date.AddDays(1);
                var rows = _alarmReader.GetAlarms(startAt, endAt) ?? new List<AlarmHourStat>();

                data.TotalCount = rows.Sum(r => r.Count);
                data.TotalDurationSeconds = rows.Sum(r => r.DowntimeMinutes) * 60d;
                data.ActiveHours = rows.Select(r => r.Hour).Distinct().Count();

                data.ByDay = rows.GroupBy(r => r.Hour.Date)
                    .Select(g => new WeeklyAlarmDay
                    {
                        Date = g.Key,
                        AlarmCount = g.Sum(x => x.Count),
                        AlarmSeconds = g.Sum(x => x.DowntimeMinutes) * 60d,
                        Yield = 0d,
                        TopAlarm = g.OrderByDescending(x => x.DowntimeMinutes).FirstOrDefault()?.Code
                    })
                    .OrderBy(d => d.Date)
                    .ToList();

                data.Top = rows.GroupBy(r => r.Code ?? "UNKNOWN")
                    .Select(g => new WeeklyAlarmTop
                    {
                        Code = string.IsNullOrWhiteSpace(g.Key) ? "UNKNOWN" : g.Key,
                        Content = g.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.Message))?.Message ?? "无",
                        Count = g.Sum(x => x.Count),
                        DurationSeconds = g.Sum(x => x.DowntimeMinutes) * 60d
                    })
                    .OrderByDescending(t => t.DurationSeconds)
                    .Take(10)
                    .ToList();
            }
            catch (Exception ex)
            {
                data.Warnings.Add("计算报警周报时出错：" + ex.Message);
            }

            return data;
        }
    }
}

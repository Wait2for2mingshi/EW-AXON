using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using EW_Assistant.Domain.Reports;

namespace EW_Assistant.Services.Reports
{
    /// <summary>
    /// 报表 Markdown 渲染集合。
    /// </summary>
    public static class DailyProdReportMarkdownFormatter
    {
        public static string Render(DailyProdData data, string analysisMarkdown)
        {
            var sb = new StringBuilder();
            var hasStateData = HasStateData(data.StateSummary);
            sb.AppendLine(hasStateData
                ? "# 状态增强产能日报（" + data.Date.ToString("yyyy-MM-dd") + "）"
                : "# 今日产能日报（" + data.Date.ToString("yyyy-MM-dd") + "）");
            sb.AppendLine();

            AppendKpiTable(sb, data);
            AppendMetricDefinitions(sb, data.MetricDefinitions, hasStateData);
            AppendDowntimeAndPeaks(sb, data);
            AppendStateOverview(sb, data.StateSummary);
            AppendHourlyTable(sb, data);

            if (!string.IsNullOrWhiteSpace(analysisMarkdown))
            {
                sb.AppendLine();
                sb.AppendLine(analysisMarkdown.Trim());
            }

            if (data.Warnings != null && data.Warnings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## 注意事项");
                foreach (var w in data.Warnings)
                {
                    sb.AppendLine("- " + w);
                }
            }

            return sb.ToString();
        }

        private static void AppendKpiTable(StringBuilder sb, DailyProdData data)
        {
            var hasStateData = HasStateData(data.StateSummary);
            sb.AppendLine(hasStateData ? "## 概览指标（含状态记录）" : "## 概览指标");
            sb.AppendLine("| 指标 | 数值 |");
            sb.AppendLine("|---|---:|");
            sb.AppendLine("| PASS（良品） | " + FormatInt(data.DayPass) + " |");
            sb.AppendLine("| FAIL（不良） | " + FormatInt(data.DayFail) + " |");
            sb.AppendLine("| 总产量 | " + FormatInt(data.DayTotal) + " |");
            sb.AppendLine("| **良率** | **" + FormatPercent(data.DayYield) + "** |");
            sb.AppendLine("| UPH（有产出小时均值） | " + FormatDouble(data.AvgUph) + " |");
            if (hasStateData)
            {
                sb.AppendLine("| 运行 UPH | " + FormatDouble(data.RunningUph) + " |");
            }
            sb.AppendLine("| 抛料/不良（Tossing） | " + FormatInt(data.Tossing) + " |");
            sb.AppendLine("| 抛料率 | " + FormatPercent(data.TossingRate) + " |");
            sb.AppendLine("| **活跃小时** | **" + data.ActiveHours + " 小时** |");
            sb.AppendLine("| **近似稼动率** | **" + FormatPercent(data.ActiveRate) + "** |");
            AppendStateKpiRows(sb, data.StateSummary);

            var peakText = BuildPeakValleyText(data.PeakHours);
            var valleyText = BuildPeakValleyText(data.ValleyHours);
            var downtimeText = BuildDowntimeText(data.Downtimes);

            sb.AppendLine("| 峰值小时 | " + (string.IsNullOrWhiteSpace(peakText) ? "无" : peakText) + " |");
            sb.AppendLine("| 低谷小时 | " + (string.IsNullOrWhiteSpace(valleyText) ? "无" : valleyText) + " |");
            sb.AppendLine("| 最长无产出窗口 | " + (string.IsNullOrWhiteSpace(downtimeText) ? "无" : downtimeText) + " |");
            sb.AppendLine("| 波动指数（CV） | " + FormatPercent(data.Cv) + " |");
            sb.AppendLine();
        }

        private static void AppendMetricDefinitions(StringBuilder sb, IList<ReportMetricDefinition> definitions, bool hasStateData)
        {
            if (definitions == null || definitions.Count == 0)
            {
                return;
            }

            sb.AppendLine("## 指标说明");
            foreach (var item in definitions)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Name))
                {
                    continue;
                }

                if (!hasStateData && IsStateMetricDefinition(item))
                {
                    continue;
                }

                sb.AppendLine("- **" + item.Name + "**：" + (item.Description ?? string.Empty));
            }
            sb.AppendLine();
        }

        private static bool IsStateMetricDefinition(ReportMetricDefinition item)
        {
            var name = item?.Name ?? string.Empty;
            return name.IndexOf("状态码", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("运行 UPH", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void AppendStateKpiRows(StringBuilder sb, ReportMachineStateSummary state)
        {
            if (!HasStateData(state))
            {
                return;
            }

            sb.AppendLine("| 正常运行（状态码1） | " + FormatSeconds(state.RunningSeconds) + " / " + FormatPercent(state.RunningRate) + " |");
            sb.AppendLine("| 空闲（状态码2） | " + FormatSeconds(state.IdleSeconds) + " / " + FormatPercent(state.IdleRate) + " |");
            sb.AppendLine("| 停线报警（状态码5/LINEDOWN） | " + FormatSeconds(state.LineDownSeconds) + " / " + FormatPercent(state.LineDownRate) + " |");
        }

        private static void AppendDowntimeAndPeaks(StringBuilder sb, DailyProdData data)
        {
            if (data.Downtimes != null && data.Downtimes.Count > 0)
            {
                sb.AppendLine("> 无产出窗口：" + BuildDowntimeText(data.Downtimes));
                sb.AppendLine();
            }
        }

        private static void AppendStateOverview(StringBuilder sb, ReportMachineStateSummary state)
        {
            if (!HasStateData(state))
            {
                return;
            }

            sb.AppendLine("## 状态记录摘要");
            sb.AppendLine("| 状态 | 时长 | 占比 |");
            sb.AppendLine("|---|---:|---:|");
            sb.AppendLine("| 正常运行 | " + FormatSeconds(state.RunningSeconds) + " | " + FormatPercent(state.RunningRate) + " |");
            sb.AppendLine("| 空闲 | " + FormatSeconds(state.IdleSeconds) + " | " + FormatPercent(state.IdleRate) + " |");
            sb.AppendLine("| LINEDOWN 停线报警 | " + FormatSeconds(state.LineDownSeconds) + " | " + FormatPercent(state.LineDownRate) + " |");
            sb.AppendLine();

            if (state.TopLineDownAlarms != null && state.TopLineDownAlarms.Count > 0)
            {
                sb.AppendLine("### Top LINEDOWN");
                sb.AppendLine("| 代码 | 内容 | 次数 | 时长 |");
                sb.AppendLine("|---|---|---:|---:|");
                foreach (var item in state.TopLineDownAlarms.Take(5))
                {
                    sb.AppendLine("| " + (string.IsNullOrWhiteSpace(item.ErrorCode) ? "无" : item.ErrorCode) +
                                  " | " + (string.IsNullOrWhiteSpace(item.ErrorMessage) ? "无" : item.ErrorMessage) +
                                  " | " + FormatInt(item.Count) +
                                  " | " + FormatSeconds(item.DurationSeconds) + " |");
                }
                sb.AppendLine();
            }
        }

        private static void AppendHourlyTable(StringBuilder sb, DailyProdData data)
        {
            var hasStateData = HasStateData(data.StateSummary);
            sb.AppendLine(hasStateData ? "## 逐小时产能与状态明细" : "## 逐小时明细");
            sb.AppendLine(hasStateData
                ? "| 时段 | PASS | 抛料/不良 | 总量 | UPH | 良率 | 状态概况 |"
                : "| 时段 | PASS | 抛料/不良 | 总量 | UPH | 良率 |");
            sb.AppendLine(hasStateData
                ? "|---|---:|---:|---:|---:|---:|---|"
                : "|---|---:|---:|---:|---:|---:|");

            foreach (var h in data.Hours.OrderBy(x => x.Hour))
            {
                var time = string.Format(CultureInfo.InvariantCulture, "{0:00}:00–{1:00}:00", h.Hour, (h.Hour + 1) % 24);
                sb.Append("| " + time + " | ");
                sb.Append(FormatInt(h.Pass) + " | ");
                sb.Append(FormatInt(h.Tossing) + " | ");
                sb.Append(FormatInt(h.Total) + " | ");
                sb.Append(FormatDouble(h.Uph) + " | ");
                sb.Append(FormatPercent(h.Yield) + " |");
                if (hasStateData)
                {
                    sb.Append(" " + BuildHourStateText(h) + " |");
                }
                sb.AppendLine();
            }

            sb.AppendLine();
        }

        private static string BuildPeakValleyText(IList<DailyProdHourStat> list)
        {
            if (list == null || list.Count == 0) return string.Empty;
            return string.Join("；", list.Select(h =>
                string.Format(CultureInfo.InvariantCulture, "{0:00}:00（{1}件）", h.Hour, FormatInt(h.Total))));
        }

        private static string BuildDowntimeText(IList<DowntimeWindow> windows)
        {
            if (windows == null || windows.Count == 0) return string.Empty;
            var win = windows.OrderByDescending(w => w.DurationHours).ThenBy(w => w.StartHour).First();
            return string.Format(CultureInfo.InvariantCulture,
                "{0:00}:00–{1:00}:00，{2} 小时", win.StartHour, win.EndHour, win.DurationHours);
        }

        private static string FormatInt(int value)
        {
            return value.ToString("N0", CultureInfo.InvariantCulture);
        }

        private static string FormatDouble(double value)
        {
            return value.ToString("0.0", CultureInfo.InvariantCulture);
        }

        private static string FormatPercent(double value)
        {
            return (value * 100).ToString("0.00", CultureInfo.InvariantCulture) + "%";
        }

        private static string FormatSeconds(double seconds)
        {
            var mins = seconds / 60d;
            if (mins >= 60d)
            {
                return (mins / 60d).ToString("0.0", CultureInfo.InvariantCulture) + "h";
            }

            return mins.ToString("0.0", CultureInfo.InvariantCulture) + "m";
        }

        private static string BuildHourStateText(DailyProdHourStat h)
        {
            var total = h.RunningSeconds + h.IdleSeconds + h.LineDownSeconds + h.StateUnknownSeconds;
            if (total <= 0d)
            {
                return "无状态记录";
            }

            return "运行 " + FormatSeconds(h.RunningSeconds) +
                   "；空闲 " + FormatSeconds(h.IdleSeconds) +
                   "；停线报警 " + FormatSeconds(h.LineDownSeconds);
        }

        private static bool HasStateData(ReportMachineStateSummary state)
        {
            return state != null && state.TotalObservedSeconds > 0d;
        }
    }

    public static class DailyAlarmReportMarkdownFormatter
    {
        public static string Render(DailyAlarmData data, string analysisMarkdown)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# 报警日报（" + data.Date.ToString("yyyy-MM-dd") + "）");
            sb.AppendLine();
            AppendKpi(sb, data);
            AppendHourly(sb, data);

            if (!string.IsNullOrWhiteSpace(analysisMarkdown))
            {
                sb.AppendLine();
                sb.AppendLine(analysisMarkdown.Trim());
            }

            if (data.Warnings != null && data.Warnings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## 注意事项");
                foreach (var w in data.Warnings) sb.AppendLine("- " + w);
            }

            return sb.ToString();
        }

        private static void AppendKpi(StringBuilder sb, DailyAlarmData data)
        {
            sb.AppendLine("## 概览指标");
            sb.AppendLine("| 指标 | 数值 |");
            sb.AppendLine("|:---|---:|");
            sb.AppendLine("| 报警条数 | " + data.DayAlarmCount.ToString("N0", CultureInfo.InvariantCulture) + " |");
            sb.AppendLine("| 报警总时长 | " + FormatSeconds(data.DayAlarmSeconds) + " |");
            sb.AppendLine("| 平均单次(秒) | " + data.AvgPerAlarmSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " |");
            sb.AppendLine("| 活跃小时 | " + data.ActiveHours + "/24 |");
            sb.AppendLine("| 峰值小时 | " + (data.PeakHour != null ? HourRange(data.PeakHour.Hour) + "（" + FormatSeconds(data.PeakHour.Seconds) + "）" : "无") + " |");
            sb.AppendLine();
        }

        private static void AppendHourly(StringBuilder sb, DailyAlarmData data)
        {
            sb.AppendLine("## 逐小时明细");
            sb.AppendLine("| 时段 | 条数 | 时长 | 平均(s) | Top代码 | Top内容 |");
            sb.AppendLine("|:---|---:|---:|---:|:---|:---|");

            foreach (var h in data.Hours.OrderBy(x => x.Hour))
            {
                sb.Append("| " + HourRange(h.Hour) + " | ");
                sb.Append(h.AlarmCount.ToString("N0", CultureInfo.InvariantCulture) + " | ");
                sb.Append(FormatSeconds(h.AlarmSeconds) + " | ");
                sb.Append(h.AvgSeconds().ToString("0.0", CultureInfo.InvariantCulture) + " | ");
                sb.Append((h.TopAlarmCode ?? "无") + " | ");
                sb.Append((string.IsNullOrWhiteSpace(h.TopAlarmContent) ? "无" : h.TopAlarmContent) + " |");
                sb.AppendLine();
            }

            sb.AppendLine();
        }

        private static string HourRange(int h)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:00}:00–{1:00}:00", h, (h + 1) % 24);
        }

        private static string FormatSeconds(double seconds)
        {
            var mins = seconds / 60d;
            if (mins >= 60)
            {
                return (mins / 60d).ToString("0.0", CultureInfo.InvariantCulture) + "h";
            }
            return mins.ToString("0.0", CultureInfo.InvariantCulture) + "m";
        }
    }

    public static class WeeklyProdReportMarkdownFormatter
    {
        public static string Render(WeeklyProdData data, string analysisMarkdown)
        {
            var sb = new StringBuilder();
            var hasStateData = HasStateData(data.StateSummary);
            sb.AppendLine(hasStateData
                ? "# 状态增强产能周报（" + data.StartDate.ToString("yyyy-MM-dd") + " ~ " + data.EndDate.ToString("yyyy-MM-dd") + "）"
                : "# 产能周报（" + data.StartDate.ToString("yyyy-MM-dd") + " ~ " + data.EndDate.ToString("yyyy-MM-dd") + "）");
            sb.AppendLine();
            AppendKpi(sb, data);
            AppendMetricDefinitions(sb, data.MetricDefinitions, hasStateData);
            AppendStateOverview(sb, data.StateSummary);
            AppendDays(sb, data);

            if (!string.IsNullOrWhiteSpace(analysisMarkdown))
            {
                sb.AppendLine();
                sb.AppendLine(analysisMarkdown.Trim());
            }

            if (data.Warnings != null && data.Warnings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## 异常/缺失");
                foreach (var w in data.Warnings) sb.AppendLine("- " + w);
            }

            return sb.ToString();
        }

        private static void AppendKpi(StringBuilder sb, WeeklyProdData data)
        {
            var hasStateData = HasStateData(data.StateSummary);
            sb.AppendLine(hasStateData ? "## 周度KPI（含状态记录）" : "## 周度KPI");
            sb.AppendLine("| 指标 | 数值 | 说明 |");
            sb.AppendLine("|---|---:|---|");
            sb.AppendLine("| PASS 总量 | " + FormatInt(data.Pass) + " | 一周内通过品数量总和 |");
            sb.AppendLine("| FAIL 总量 | " + FormatInt(data.Fail) + " | 一周内不良品数量总和 |");
            sb.AppendLine("| 总产量 | " + FormatInt(data.Total) + " | 一周内实际测试的总数量 |");
            sb.AppendLine("| 周整体良率 | " + FormatPercent(data.Yield) + " | 以一周总产量为基准的整体通过率 |");
            sb.AppendLine("| 平均 UPH | " + FormatDouble(data.AvgUph) + " | 总产量 / 有产出小时 |");
            if (hasStateData)
            {
                sb.AppendLine("| 运行 UPH | " + FormatDouble(data.RunningUph) + " | 总产量 / 状态记录表正常运行小时 |");
            }
            sb.AppendLine("| 抛料/不良总量（Tossing） | " + FormatInt(data.Tossing) + " | FAIL/NG/抛料数量总和 |");
            sb.AppendLine("| 抛料率 | " + FormatPercent(data.TossingRate) + " | 抛料/不良 / 总产量 |");
            sb.AppendLine("| 周均良率 | " + FormatPercent(data.AvgYield) + " | 7 天良率平均 |");
            sb.AppendLine("| 中位产量 | " + FormatInt(data.MedianTotal) + " | 7 天日产量中位数 |");
            sb.AppendLine("| 产量波动 | " + FormatPercent(data.Volatility) + " | 日产量的变异系数（CV） |");
            AppendStateKpiRows(sb, data.StateSummary);
            if (data.LastDay != null && data.LastDayDelta != null)
            {
                sb.AppendLine("| 最后1天 vs 周均 | " +
                    FormatPercent(data.LastDayDelta.TotalDelta) + " / " +
                    FormatPercent(data.LastDayDelta.YieldDelta) + " | 产量/良率 相对周均的增减 |");
            }
            sb.AppendLine();
        }

        private static void AppendDays(StringBuilder sb, WeeklyProdData data)
        {
            var hasStateData = HasStateData(data.StateSummary);
            sb.AppendLine(hasStateData ? "## 日度产能与状态表现" : "## 日度表现");
            sb.AppendLine(hasStateData
                ? "| 日期 | PASS | 抛料/不良 | 总量 | UPH | 良率 | 运行 | 空闲 | 停线报警 | 备注 |"
                : "| 日期 | PASS | 抛料/不良 | 总量 | UPH | 良率 | 备注 |");
            sb.AppendLine(hasStateData
                ? "|---|---:|---:|---:|---:|---:|---:|---:|---:|---|"
                : "|---|---:|---:|---:|---:|---:|---|");
            foreach (var d in data.Days.OrderBy(x => x.Date))
            {
                sb.Append("| " + d.Date.ToString("yyyy-MM-dd") + " | ");
                sb.Append(FormatInt(d.Pass) + " | ");
                sb.Append(FormatInt(d.Tossing) + " | ");
                sb.Append(FormatInt(d.Total) + " | ");
                sb.Append(FormatDouble(d.Uph) + " | ");
                sb.Append(FormatPercent(d.Yield) + " | ");
                if (hasStateData)
                {
                    sb.Append(FormatSeconds(d.RunningSeconds) + " | ");
                    sb.Append(FormatSeconds(d.IdleSeconds) + " | ");
                    sb.Append(FormatSeconds(d.LineDownSeconds) + " | ");
                }
                sb.Append((string.IsNullOrWhiteSpace(d.Note) ? "正常" : d.Note) + " |");
                sb.AppendLine();
            }
            sb.AppendLine();
        }

        private static void AppendMetricDefinitions(StringBuilder sb, IList<ReportMetricDefinition> definitions, bool hasStateData)
        {
            if (definitions == null || definitions.Count == 0)
            {
                return;
            }

            sb.AppendLine("## 指标说明");
            foreach (var item in definitions)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Name))
                {
                    continue;
                }

                if (!hasStateData && IsStateMetricDefinition(item))
                {
                    continue;
                }

                sb.AppendLine("- **" + item.Name + "**：" + (item.Description ?? string.Empty));
            }
            sb.AppendLine();
        }

        private static bool IsStateMetricDefinition(ReportMetricDefinition item)
        {
            var name = item?.Name ?? string.Empty;
            return name.IndexOf("状态码", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("运行 UPH", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void AppendStateKpiRows(StringBuilder sb, ReportMachineStateSummary state)
        {
            if (!HasStateData(state))
            {
                return;
            }

            sb.AppendLine("| 正常运行（状态码1） | " + FormatSeconds(state.RunningSeconds) + " / " + FormatPercent(state.RunningRate) + " | 一周内状态记录表累计 |");
            sb.AppendLine("| 空闲（状态码2） | " + FormatSeconds(state.IdleSeconds) + " / " + FormatPercent(state.IdleRate) + " | 一周内状态记录表累计 |");
            sb.AppendLine("| 停线报警（状态码5/LINEDOWN） | " + FormatSeconds(state.LineDownSeconds) + " / " + FormatPercent(state.LineDownRate) + " | 一周内状态记录表累计 |");
        }

        private static void AppendStateOverview(StringBuilder sb, ReportMachineStateSummary state)
        {
            if (!HasStateData(state))
            {
                return;
            }

            sb.AppendLine("## 状态记录摘要");
            sb.AppendLine("| 状态 | 时长 | 占比 |");
            sb.AppendLine("|---|---:|---:|");
            sb.AppendLine("| 正常运行 | " + FormatSeconds(state.RunningSeconds) + " | " + FormatPercent(state.RunningRate) + " |");
            sb.AppendLine("| 空闲 | " + FormatSeconds(state.IdleSeconds) + " | " + FormatPercent(state.IdleRate) + " |");
            sb.AppendLine("| LINEDOWN 停线报警 | " + FormatSeconds(state.LineDownSeconds) + " | " + FormatPercent(state.LineDownRate) + " |");
            sb.AppendLine();

            if (state.TopLineDownAlarms != null && state.TopLineDownAlarms.Count > 0)
            {
                sb.AppendLine("### Top LINEDOWN");
                sb.AppendLine("| 代码 | 内容 | 次数 | 时长 |");
                sb.AppendLine("|---|---|---:|---:|");
                foreach (var item in state.TopLineDownAlarms.Take(5))
                {
                    sb.AppendLine("| " + (string.IsNullOrWhiteSpace(item.ErrorCode) ? "无" : item.ErrorCode) +
                                  " | " + (string.IsNullOrWhiteSpace(item.ErrorMessage) ? "无" : item.ErrorMessage) +
                                  " | " + FormatInt(item.Count) +
                                  " | " + FormatSeconds(item.DurationSeconds) + " |");
                }
                sb.AppendLine();
            }
        }

        private static string FormatInt(int value) => value.ToString("N0", CultureInfo.InvariantCulture);
        private static string FormatDouble(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);
        private static string FormatPercent(double value) => (value * 100).ToString("0.00", CultureInfo.InvariantCulture) + "%";
        private static string FormatSeconds(double seconds)
        {
            var mins = seconds / 60d;
            if (mins >= 60d)
            {
                return (mins / 60d).ToString("0.0", CultureInfo.InvariantCulture) + "h";
            }

            return mins.ToString("0.0", CultureInfo.InvariantCulture) + "m";
        }

        private static bool HasStateData(ReportMachineStateSummary state)
        {
            return state != null && state.TotalObservedSeconds > 0d;
        }
    }

    public static class WeeklyAlarmReportMarkdownFormatter
    {
        public static string Render(WeeklyAlarmData data, string analysisMarkdown)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# 报警周报（" + data.StartDate.ToString("yyyy-MM-dd") + " ~ " + data.EndDate.ToString("yyyy-MM-dd") + "）");
            sb.AppendLine();
            AppendKpi(sb, data);
            AppendDaily(sb, data);
            AppendTop(sb, data);

            if (!string.IsNullOrWhiteSpace(analysisMarkdown))
            {
                sb.AppendLine();
                sb.AppendLine(analysisMarkdown.Trim());
            }

            if (data.Warnings != null && data.Warnings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## 异常/缺失");
                foreach (var w in data.Warnings) sb.AppendLine("- " + w);
            }

            return sb.ToString();
        }

        private static void AppendKpi(StringBuilder sb, WeeklyAlarmData data)
        {
            sb.AppendLine("## 周度KPI");
            sb.AppendLine("| 指标 | 数值 | 说明 |");
            sb.AppendLine("|---|---:|---|");
            sb.AppendLine("| 报警次数 | " + FormatInt(data.TotalCount) + " | 一周内记录到的报警总次数 |");
            sb.AppendLine("| 报警总时长 | " + FormatSeconds(data.TotalDurationSeconds) + " | 一周内报警累计时长 |");
            sb.AppendLine("| 平均单次时长 | " + FormatSeconds(data.TotalCount > 0 ? data.TotalDurationSeconds / data.TotalCount : 0d) + " | 每次报警平均持续时间 |");
            sb.AppendLine("| 覆盖小时数 | " + data.ActiveHours + "/168 | 本周有报警发生的小时数 |");
            sb.AppendLine();
        }

        private static void AppendDaily(StringBuilder sb, WeeklyAlarmData data)
        {
            sb.AppendLine("## 日度趋势");
            sb.AppendLine("| 日期 | 报警次数 | 报警时长 | Top 报警（概括） |");
            sb.AppendLine("|---|---:|---:|---|");
            foreach (var d in data.ByDay.OrderBy(x => x.Date))
            {
                sb.Append("| " + d.Date.ToString("yyyy-MM-dd") + " | ");
                sb.Append(FormatInt(d.AlarmCount) + " | ");
                sb.Append(FormatSeconds(d.AlarmSeconds) + " | ");
                sb.Append((string.IsNullOrWhiteSpace(d.TopAlarm) ? "无" : d.TopAlarm) + " |");
                sb.AppendLine();
            }
            sb.AppendLine();
        }

        private static void AppendTop(StringBuilder sb, WeeklyAlarmData data)
        {
            sb.AppendLine("## Top 报警");
            sb.AppendLine("| 代码 | 内容 | 次数 | 时长 |");
            sb.AppendLine("|---|---|---:|---:|");
            foreach (var t in data.Top)
            {
                sb.Append("| " + t.Code + " | " + (string.IsNullOrWhiteSpace(t.Content) ? "无" : t.Content) + " | ");
                sb.Append(FormatInt(t.Count) + " | " + FormatSeconds(t.DurationSeconds) + " |");
                sb.AppendLine();
            }
            sb.AppendLine();
        }

        private static string FormatInt(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

        private static string FormatSeconds(double seconds)
        {
            var mins = seconds / 60d;
            if (mins >= 60)
            {
                return (mins / 60d).ToString("0.0", CultureInfo.InvariantCulture) + "h";
            }
            return mins.ToString("0.0", CultureInfo.InvariantCulture) + "m";
        }
    }
}

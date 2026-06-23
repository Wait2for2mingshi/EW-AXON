using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Windows.Media;
using EW_Assistant.Domain.Reports;
using EW_Assistant.Services;
using Newtonsoft.Json;

namespace EW_Assistant.Services.Reports
{
    public class StructuredReportPreviewBuilder
    {
        private static readonly string[] s_chartPalette =
        {
            "#2563EB",
            "#F97316",
            "#16A34A",
            "#EAB308",
            "#7C3AED",
            "#DC2626",
            "#0891B2",
            "#EA580C"
        };

        private readonly ReportStorageService _storage;

        public StructuredReportPreviewBuilder(ReportStorageService storage = null)
        {
            _storage = storage ?? new ReportStorageService();
        }

        public StructuredReportPreview Build(ReportInfo info)
        {
            if (info == null)
            {
                return BuildEmptyPreview("请选择左侧报表以查看图文预览。");
            }

            var preview = new StructuredReportPreview();
            var markdown = SafeReadMarkdown(info);
            var analysisMarkdown = ResolveAnalysisMarkdown(info, markdown, out var needsRegenerateHint);
            var snapshot = ResolveSnapshot(info);

            if (snapshot == null)
            {
                preview.AnalysisMarkdown = analysisMarkdown;
                preview.Notes.Add("当前报表缺少结构化数据快照，图表与明细已停用。若要稳定查看完整图文内容，请点击“重新生成”。");
                if (needsRegenerateHint)
                {
                    preview.Notes.Add("该报表属于旧版纯 Markdown 产物，建议点击“重新生成”以获得独立分析正文和更稳定的图文预览。");
                }

                return preview;
            }

            switch (info.Type)
            {
                case ReportType.DailyProd:
                    BuildDailyProd(preview, snapshot as DailyProdData);
                    break;
                case ReportType.DailyAlarm:
                    BuildDailyAlarm(preview, snapshot as DailyAlarmData);
                    break;
                case ReportType.WeeklyProd:
                    BuildWeeklyProd(preview, snapshot as WeeklyProdData);
                    break;
                case ReportType.WeeklyAlarm:
                    BuildWeeklyAlarm(preview, snapshot as WeeklyAlarmData);
                    break;
                default:
                    return BuildEmptyPreview("暂不支持该报表类型的结构化预览。");
            }

            preview.AnalysisMarkdown = analysisMarkdown;
            if (needsRegenerateHint)
            {
                preview.Notes.Add("该报表属于旧版纯 Markdown 产物，建议点击“重新生成”以获得独立分析正文和更稳定的图文预览。");
            }

            return preview;
        }

        private StructuredReportPreview BuildEmptyPreview(string message)
        {
            return new StructuredReportPreview
            {
                AnalysisMarkdown = BuildHintMarkdown(message)
            };
        }

        private string SafeReadMarkdown(ReportInfo info)
        {
            try
            {
                return _storage.ReadReportContent(info);
            }
            catch
            {
                return string.Empty;
            }
        }

        private string ResolveAnalysisMarkdown(ReportInfo info, string fullMarkdown, out bool needsRegenerateHint)
        {
            needsRegenerateHint = false;

            try
            {
                var analysis = _storage.ReadReportAnalysisContent(info);
                if (!string.IsNullOrWhiteSpace(analysis))
                {
                    return DifyOutputSanitizer.Clean(analysis);
                }
            }
            catch
            {
                // 回退到历史 Markdown 提取
            }

            var extracted = ReportAnalysisMarkdownExtractor.Extract(info.Type, fullMarkdown);
            if (!string.IsNullOrWhiteSpace(extracted))
            {
                needsRegenerateHint = true;
                return DifyOutputSanitizer.Clean(extracted);
            }

            needsRegenerateHint = true;
            return BuildHintMarkdown("未找到独立分析正文，当前图表仍可查看；如需更完整的 AI 分析，请点击“重新生成”。");
        }

        private object ResolveSnapshot(ReportInfo info)
        {
            try
            {
                var json = _storage.ReadReportDataSnapshot(info);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return null;
                }

                switch (info.Type)
                {
                    case ReportType.DailyProd:
                        return JsonConvert.DeserializeObject<DailyProdData>(json);
                    case ReportType.DailyAlarm:
                        return JsonConvert.DeserializeObject<DailyAlarmData>(json);
                    case ReportType.WeeklyProd:
                        return JsonConvert.DeserializeObject<WeeklyProdData>(json);
                    case ReportType.WeeklyAlarm:
                        return JsonConvert.DeserializeObject<WeeklyAlarmData>(json);
                    default:
                        return null;
                }
            }
            catch
            {
                return null;
            }
        }

        private void BuildDailyProd(StructuredReportPreview preview, DailyProdData data)
        {
            var hours = (data.Hours ?? new List<DailyProdHourStat>()).OrderBy(x => x.Hour).ToList();
            var hasStateData = HasStateData(data.StateSummary);

            preview.Kpis = hasStateData
                ? new List<ReportPreviewKpi>
                {
                    CreateKpi("总产量", FormatInt(data.DayTotal), "PASS " + FormatInt(data.DayPass) + " / FAIL " + FormatInt(data.DayFail), "#2563EB"),
                    CreateKpi("当日良率", FormatPercent(data.DayYield), "低良率小时 " + hours.Count(h => h.Total > 0 && h.Yield < 0.85) + " 个", "#16A34A"),
                    CreateKpi("UPH", FormatDouble(data.AvgUph), "运行 UPH " + FormatDouble(data.RunningUph), "#0891B2"),
                    CreateKpi("正常运行", FormatPercent(data.StateSummary.RunningRate), FormatDuration(data.StateSummary.RunningSeconds), "#16A34A"),
                    CreateKpi("空闲", FormatPercent(data.StateSummary.IdleRate), FormatDuration(data.StateSummary.IdleSeconds), "#EAB308"),
                    CreateKpi("停线报警", FormatPercent(data.StateSummary.LineDownRate), FormatDuration(data.StateSummary.LineDownSeconds), "#DC2626")
                }
                : new List<ReportPreviewKpi>
            {
                CreateKpi("总产量", FormatInt(data.DayTotal), "PASS " + FormatInt(data.DayPass) + " / FAIL " + FormatInt(data.DayFail), "#2563EB"),
                CreateKpi("当日良率", FormatPercent(data.DayYield), "低良率小时 " + hours.Count(h => h.Total > 0 && h.Yield < 0.85) + " 个", "#16A34A"),
                CreateKpi("UPH", FormatDouble(data.AvgUph), "按有产出小时均值", "#0891B2"),
                CreateKpi("抛料/不良", FormatInt(data.Tossing), "占比 " + FormatPercent(data.TossingRate), "#F97316"),
                CreateKpi("活跃小时", data.ActiveHours + "/24", "稼动率 " + FormatPercent(data.ActiveRate), "#D97706"),
                CreateKpi("产量波动", FormatPercent(data.Cv), BuildDowntimeHint(data.Downtimes), "#7C3AED")
            };

            preview.PrimaryChart = new ReportChartDefinition
            {
                Title = "24 小时 PASS / FAIL 分布",
                Subtitle = "按小时堆叠，直观看出每个时段的产出结构",
                Kind = ReportChartKind.StackedBar,
                ValueFormat = ReportChartValueFormat.Number,
                Labels = hours.Select(h => HourLabel(h.Hour)).ToList(),
                EmptyHint = "当天没有读取到产能明细。"
            };
            preview.PrimaryChart.Series.Add(CreateSeries("PASS", "#2563EB", hours.Select(h => (double)h.Pass)));
            preview.PrimaryChart.Series.Add(CreateSeries("FAIL", "#F97316", hours.Select(h => (double)h.Fail)));
            preview.PrimaryChart.DataPoints = hours
                .Select(h => CreateDataPoint(
                    HourLabel(h.Hour),
                    HourRange(h.Hour) + " 产能结构",
                    h.Total > 0
                        ? "该小时总产量 " + FormatInt(h.Total) + "，良率 " + FormatPercent(h.Yield) + "。"
                        : "该小时无产出记录。",
                    BuildSingleRowTable(
                        new[] { "时段", "PASS", "FAIL", "总量", "良率" },
                        HourRange(h.Hour), FormatInt(h.Pass), FormatInt(h.Fail), FormatInt(h.Total), FormatPercent(h.Yield)),
                    CreateMetric("PASS", FormatInt(h.Pass), "#2563EB"),
                    CreateMetric("FAIL", FormatInt(h.Fail), "#F97316"),
                    CreateMetric("总量", FormatInt(h.Total), "#0F172A"),
                    CreateMetric("良率", FormatPercent(h.Yield), "#16A34A")))
                .ToList();

            preview.SecondaryChart = new ReportChartDefinition
            {
                Title = "逐小时良率走势",
                Subtitle = "单位：%，便于快速定位低良率时段",
                Kind = ReportChartKind.Line,
                ValueFormat = ReportChartValueFormat.Percent,
                Labels = hours.Select(h => HourLabel(h.Hour)).ToList(),
                EmptyHint = "当天没有可用于绘图的良率数据。"
            };
            preview.SecondaryChart.Series.Add(CreateSeries("良率", "#16A34A", hours.Select(h => h.Yield * 100d)));
            preview.SecondaryChart.DataPoints = hours
                .Select(h => CreateDataPoint(
                    HourLabel(h.Hour),
                    HourRange(h.Hour) + " 良率详情",
                    h.Total > 0
                        ? "该小时良率为 " + FormatPercent(h.Yield) + "，总产量 " + FormatInt(h.Total) + "。"
                        : "该小时无产出，良率趋势仅作占位展示。",
                    BuildSingleRowTable(
                        new[] { "时段", "PASS", "FAIL", "总量", "良率" },
                        HourRange(h.Hour), FormatInt(h.Pass), FormatInt(h.Fail), FormatInt(h.Total), FormatPercent(h.Yield)),
                    CreateMetric("良率", FormatPercent(h.Yield), "#16A34A"),
                    CreateMetric("总量", FormatInt(h.Total), "#0F172A"),
                    CreateMetric("PASS", FormatInt(h.Pass), "#2563EB"),
                    CreateMetric("FAIL", FormatInt(h.Fail), "#F97316")))
                .ToList();

            preview.TertiaryChart = hasStateData
                ? new ReportChartDefinition
                {
                    Title = "运行 / 空闲 / LINEDOWN 占比",
                    Subtitle = "来自状态记录表，和产能无产出窗口独立看待",
                    Kind = ReportChartKind.Donut,
                    ValueFormat = ReportChartValueFormat.DurationMinutes,
                    CenterText = FormatDuration(data.StateSummary.TotalObservedSeconds),
                    CenterSubtext = "状态覆盖",
                    EmptyHint = "当天没有状态记录分布。"
                }
                : new ReportChartDefinition
            {
                Title = "PASS / FAIL 占比",
                Subtitle = "从日维度看整体产出结构",
                Kind = ReportChartKind.Donut,
                ValueFormat = ReportChartValueFormat.Number,
                CenterText = FormatPercent(data.DayYield),
                CenterSubtext = "整体良率",
                EmptyHint = "当天没有产出记录。"
            };
            if (hasStateData)
            {
                foreach (var item in BuildStateDistributionSeries(data.StateSummary))
                {
                    preview.TertiaryChart.Series.Add(item);
                }
                preview.TertiaryChart.DataPoints = BuildStateDistributionDataPoints(data.StateSummary, "当日状态分布");
            }
            else
            {
                preview.TertiaryChart.Series.Add(CreateSeries("PASS", "#2563EB", new[] { (double)data.DayPass }));
                preview.TertiaryChart.Series.Add(CreateSeries("FAIL", "#F97316", new[] { (double)data.DayFail }));
                preview.TertiaryChart.DataPoints = BuildPassFailDataPoints(data.DayPass, data.DayFail, "PASS 占比详情", "FAIL 占比详情");
            }

            preview.DetailTitle = hasStateData ? "24 小时产能与状态明细" : "24 小时产能明细";
            preview.DetailTable = BuildDailyProdTable(hours);

            AppendWarnings(preview.Notes, data.Warnings);
            if (data.PeakHours != null && data.PeakHours.Count > 0)
            {
                preview.Notes.Add("峰值小时：" + string.Join("、", data.PeakHours.Select(h => HourLabel(h.Hour) + "（" + FormatInt(h.Total) + "）")));
            }
        }

        private void BuildDailyAlarm(StructuredReportPreview preview, DailyAlarmData data)
        {
            var hours = (data.Hours ?? new List<DailyAlarmHourStat>()).OrderBy(x => x.Hour).ToList();
            var topAlarms = BuildDailyAlarmTopList(data);

            preview.Kpis = new List<ReportPreviewKpi>
            {
                CreateKpi("报警条数", FormatInt(data.DayAlarmCount), "活跃小时 " + data.ActiveHours + "/24", "#DC2626"),
                CreateKpi("报警总时长", FormatDuration(data.DayAlarmSeconds), data.PeakHour != null ? "峰值 " + HourLabel(data.PeakHour.Hour) : "当天无峰值时段", "#EA580C"),
                CreateKpi("平均单次", FormatSeconds(data.AvgPerAlarmSeconds), topAlarms.Count > 0 ? "Top 报警 " + topAlarms[0].Code : "当天暂无报警分类", "#2563EB"),
                CreateKpi("高发时段", data.PeakHour != null ? HourLabel(data.PeakHour.Hour) : "无", BuildTopAlarmHint(topAlarms), "#7C3AED")
            };

            preview.PrimaryChart = new ReportChartDefinition
            {
                Title = "逐小时报警次数",
                Subtitle = "按小时识别高发区间",
                Kind = ReportChartKind.Bar,
                ValueFormat = ReportChartValueFormat.Number,
                Labels = hours.Select(h => HourLabel(h.Hour)).ToList(),
                EmptyHint = "当天没有读取到报警次数。"
            };
            preview.PrimaryChart.Series.Add(CreateSeries("报警次数", "#DC2626", hours.Select(h => (double)h.AlarmCount)));
            preview.PrimaryChart.DataPoints = hours
                .Select(h => CreateDataPoint(
                    HourLabel(h.Hour),
                    HourRange(h.Hour) + " 报警次数详情",
                    h.AlarmCount > 0
                        ? "该小时报警 " + FormatInt(h.AlarmCount) + " 条，累计 " + FormatDuration(h.AlarmSeconds) + "。"
                        : "该小时无报警记录。",
                    BuildSingleRowTable(
                        new[] { "时段", "报警条数", "报警时长", "平均单次", "Top代码", "Top内容" },
                        HourRange(h.Hour),
                        FormatInt(h.AlarmCount),
                        FormatDuration(h.AlarmSeconds),
                        FormatSeconds(h.AvgSeconds()),
                        string.IsNullOrWhiteSpace(h.TopAlarmCode) ? "无" : h.TopAlarmCode,
                        string.IsNullOrWhiteSpace(h.TopAlarmContent) ? "无" : h.TopAlarmContent),
                    CreateMetric("报警条数", FormatInt(h.AlarmCount), "#DC2626"),
                    CreateMetric("报警时长", FormatDuration(h.AlarmSeconds), "#F97316"),
                    CreateMetric("平均单次", FormatSeconds(h.AvgSeconds()), "#2563EB")))
                .ToList();

            preview.SecondaryChart = new ReportChartDefinition
            {
                Title = "逐小时报警时长",
                Subtitle = "单位：分钟，观察长时报警集中区间",
                Kind = ReportChartKind.Line,
                ValueFormat = ReportChartValueFormat.DurationMinutes,
                Labels = hours.Select(h => HourLabel(h.Hour)).ToList(),
                EmptyHint = "当天没有读取到报警时长。"
            };
            preview.SecondaryChart.Series.Add(CreateSeries("报警时长", "#F97316", hours.Select(h => h.AlarmSeconds / 60d)));
            preview.SecondaryChart.DataPoints = hours
                .Select(h => CreateDataPoint(
                    HourLabel(h.Hour),
                    HourRange(h.Hour) + " 报警时长详情",
                    h.AlarmSeconds > 0
                        ? "该小时报警累计 " + FormatDuration(h.AlarmSeconds) + "，平均单次 " + FormatSeconds(h.AvgSeconds()) + "。"
                        : "该小时无报警时长记录。",
                    BuildSingleRowTable(
                        new[] { "时段", "报警条数", "报警时长", "平均单次", "Top代码", "Top内容" },
                        HourRange(h.Hour),
                        FormatInt(h.AlarmCount),
                        FormatDuration(h.AlarmSeconds),
                        FormatSeconds(h.AvgSeconds()),
                        string.IsNullOrWhiteSpace(h.TopAlarmCode) ? "无" : h.TopAlarmCode,
                        string.IsNullOrWhiteSpace(h.TopAlarmContent) ? "无" : h.TopAlarmContent),
                    CreateMetric("报警时长", FormatDuration(h.AlarmSeconds), "#F97316"),
                    CreateMetric("报警条数", FormatInt(h.AlarmCount), "#DC2626"),
                    CreateMetric("Top代码", string.IsNullOrWhiteSpace(h.TopAlarmCode) ? "无" : h.TopAlarmCode, "#7C3AED")))
                .ToList();

            preview.TertiaryChart = new ReportChartDefinition
            {
                Title = "Top 报警时长占比",
                Subtitle = "优先观察长时报警的主导类别",
                Kind = ReportChartKind.Donut,
                ValueFormat = ReportChartValueFormat.DurationMinutes,
                CenterText = FormatDuration(data.DayAlarmSeconds),
                CenterSubtext = "累计时长",
                EmptyHint = "当天没有 Top 报警分布。"
            };
            foreach (var item in BuildTopAlarmDonutSeries(topAlarms))
            {
                preview.TertiaryChart.Series.Add(item);
            }
            preview.TertiaryChart.DataPoints = BuildAlarmCategoryDataPoints(topAlarms, data.DayAlarmSeconds, "当日报警类别");

            preview.DetailTitle = "24 小时报警明细";
            preview.DetailTable = BuildDailyAlarmTable(hours);

            AppendWarnings(preview.Notes, data.Warnings);
            if (data.PeakHour != null)
            {
                preview.Notes.Add("峰值报警时段：" + HourLabel(data.PeakHour.Hour) + "，累计时长 " + FormatDuration(data.PeakHour.Seconds) + "。");
            }
        }

        private void BuildWeeklyProd(StructuredReportPreview preview, WeeklyProdData data)
        {
            var days = (data.Days ?? new List<WeeklyProdDay>()).OrderBy(x => x.Date).ToList();
            var hasStateData = HasStateData(data.StateSummary);

            preview.Kpis = hasStateData
                ? new List<ReportPreviewKpi>
                {
                    CreateKpi("周总产量", FormatInt(data.Total), "PASS " + FormatInt(data.Pass) + " / FAIL " + FormatInt(data.Fail), "#2563EB"),
                    CreateKpi("周整体良率", FormatPercent(data.Yield), "按全周总产量计算", "#16A34A"),
                    CreateKpi("平均 UPH", FormatDouble(data.AvgUph), "运行 UPH " + FormatDouble(data.RunningUph), "#0891B2"),
                    CreateKpi("正常运行", FormatPercent(data.StateSummary.RunningRate), FormatDuration(data.StateSummary.RunningSeconds), "#16A34A"),
                    CreateKpi("空闲", FormatPercent(data.StateSummary.IdleRate), FormatDuration(data.StateSummary.IdleSeconds), "#EAB308"),
                    CreateKpi("停线报警", FormatPercent(data.StateSummary.LineDownRate), FormatDuration(data.StateSummary.LineDownSeconds), "#DC2626")
                }
                : new List<ReportPreviewKpi>
            {
                CreateKpi("周总产量", FormatInt(data.Total), "PASS " + FormatInt(data.Pass) + " / FAIL " + FormatInt(data.Fail), "#2563EB"),
                CreateKpi("周整体良率", FormatPercent(data.Yield), "按全周总产量计算", "#16A34A"),
                CreateKpi("平均 UPH", FormatDouble(data.AvgUph), "按有产出小时均值", "#0891B2"),
                CreateKpi("抛料/不良", FormatInt(data.Tossing), "占比 " + FormatPercent(data.TossingRate), "#F97316"),
                CreateKpi("周均良率", FormatPercent(data.AvgYield), "中位产量 " + FormatInt(data.MedianTotal), "#0891B2"),
                CreateKpi("产量波动", FormatPercent(data.Volatility), BuildLastDayDeltaHint(data), "#7C3AED")
            };

            preview.PrimaryChart = new ReportChartDefinition
            {
                Title = "每日 PASS / FAIL",
                Subtitle = "看一周内各天的产出结构",
                Kind = ReportChartKind.StackedBar,
                ValueFormat = ReportChartValueFormat.Number,
                Labels = days.Select(d => DayLabel(d.Date)).ToList(),
                EmptyHint = "当前周报没有日度产能数据。"
            };
            preview.PrimaryChart.Series.Add(CreateSeries("PASS", "#2563EB", days.Select(d => (double)d.Pass)));
            preview.PrimaryChart.Series.Add(CreateSeries("FAIL", "#F97316", days.Select(d => (double)d.Fail)));
            preview.PrimaryChart.DataPoints = days
                .Select(d => CreateDataPoint(
                    DayLabel(d.Date),
                    d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " 产能结构",
                    "当日产量 " + FormatInt(d.Total) + "，良率 " + FormatPercent(d.Yield) + "。",
                    BuildSingleRowTable(
                        new[] { "日期", "PASS", "FAIL", "总量", "良率", "备注" },
                        d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        FormatInt(d.Pass),
                        FormatInt(d.Fail),
                        FormatInt(d.Total),
                        FormatPercent(d.Yield),
                        string.IsNullOrWhiteSpace(d.Note) ? "正常" : d.Note),
                    CreateMetric("PASS", FormatInt(d.Pass), "#2563EB"),
                    CreateMetric("FAIL", FormatInt(d.Fail), "#F97316"),
                    CreateMetric("总量", FormatInt(d.Total), "#0F172A"),
                    CreateMetric("良率", FormatPercent(d.Yield), "#16A34A")))
                .ToList();

            preview.SecondaryChart = new ReportChartDefinition
            {
                Title = "每日良率走势",
                Subtitle = "单位：%，方便对比最佳/较弱日期",
                Kind = ReportChartKind.Line,
                ValueFormat = ReportChartValueFormat.Percent,
                Labels = days.Select(d => DayLabel(d.Date)).ToList(),
                EmptyHint = "当前周报没有可用于绘图的良率数据。"
            };
            preview.SecondaryChart.Series.Add(CreateSeries("良率", "#16A34A", days.Select(d => d.Yield * 100d)));
            preview.SecondaryChart.DataPoints = days
                .Select(d => CreateDataPoint(
                    DayLabel(d.Date),
                    d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " 良率详情",
                    "当天良率 " + FormatPercent(d.Yield) + "，总产量 " + FormatInt(d.Total) + "。",
                    BuildSingleRowTable(
                        new[] { "日期", "PASS", "FAIL", "总量", "良率", "备注" },
                        d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        FormatInt(d.Pass),
                        FormatInt(d.Fail),
                        FormatInt(d.Total),
                        FormatPercent(d.Yield),
                        string.IsNullOrWhiteSpace(d.Note) ? "正常" : d.Note),
                    CreateMetric("良率", FormatPercent(d.Yield), "#16A34A"),
                    CreateMetric("总量", FormatInt(d.Total), "#0F172A"),
                    CreateMetric("备注", string.IsNullOrWhiteSpace(d.Note) ? "正常" : d.Note, "#64748B")))
                .ToList();

            preview.TertiaryChart = hasStateData
                ? new ReportChartDefinition
                {
                    Title = "运行 / 空闲 / LINEDOWN 占比",
                    Subtitle = "来自状态记录表，和产能趋势分开呈现",
                    Kind = ReportChartKind.Donut,
                    ValueFormat = ReportChartValueFormat.DurationMinutes,
                    CenterText = FormatDuration(data.StateSummary.TotalObservedSeconds),
                    CenterSubtext = "状态覆盖",
                    EmptyHint = "当前周报没有状态记录分布。"
                }
                : new ReportChartDefinition
            {
                Title = "PASS / FAIL 占比",
                Subtitle = "从周维度看整体产出结构",
                Kind = ReportChartKind.Donut,
                ValueFormat = ReportChartValueFormat.Number,
                CenterText = FormatPercent(data.Yield),
                CenterSubtext = "周整体良率",
                EmptyHint = "当前周报没有 PASS / FAIL 分布。"
            };
            if (hasStateData)
            {
                foreach (var item in BuildStateDistributionSeries(data.StateSummary))
                {
                    preview.TertiaryChart.Series.Add(item);
                }
                preview.TertiaryChart.DataPoints = BuildStateDistributionDataPoints(data.StateSummary, "本周状态分布");
            }
            else
            {
                preview.TertiaryChart.Series.Add(CreateSeries("PASS", "#2563EB", new[] { (double)data.Pass }));
                preview.TertiaryChart.Series.Add(CreateSeries("FAIL", "#F97316", new[] { (double)data.Fail }));
                preview.TertiaryChart.DataPoints = BuildPassFailDataPoints(data.Pass, data.Fail, "周 PASS 占比详情", "周 FAIL 占比详情");
            }

            preview.DetailTitle = hasStateData ? "日度产能与状态明细" : "日度产能明细";
            preview.DetailTable = BuildWeeklyProdTable(days);

            AppendWarnings(preview.Notes, data.Warnings);
            if (data.BestDays != null && data.BestDays.Count > 0)
            {
                preview.Notes.Add("最佳产出日：" + string.Join("、", data.BestDays.Select(d => DayLabel(d.Date) + "（" + FormatInt(d.Total) + "）")));
            }
        }

        private void BuildWeeklyAlarm(StructuredReportPreview preview, WeeklyAlarmData data)
        {
            var days = (data.ByDay ?? new List<WeeklyAlarmDay>()).OrderBy(x => x.Date).ToList();
            var averageSeconds = data.TotalCount > 0 ? data.TotalDurationSeconds / data.TotalCount : 0d;

            preview.Kpis = new List<ReportPreviewKpi>
            {
                CreateKpi("报警次数", FormatInt(data.TotalCount), "一周累计报警数量", "#DC2626"),
                CreateKpi("报警总时长", FormatDuration(data.TotalDurationSeconds), "覆盖小时 " + data.ActiveHours + "/168", "#EA580C"),
                CreateKpi("平均单次", FormatSeconds(averageSeconds), data.Top != null && data.Top.Count > 0 ? "Top 报警 " + data.Top[0].Code : "暂无 Top 报警", "#2563EB"),
                CreateKpi("日均报警", days.Count > 0 ? (data.TotalCount / (double)Math.Max(1, days.Count)).ToString("0.0", CultureInfo.InvariantCulture) : "0", "按周内有记录日期平均", "#7C3AED")
            };

            preview.PrimaryChart = new ReportChartDefinition
            {
                Title = "日度报警次数",
                Subtitle = "按天观察周内报警波动",
                Kind = ReportChartKind.Bar,
                ValueFormat = ReportChartValueFormat.Number,
                Labels = days.Select(d => DayLabel(d.Date)).ToList(),
                EmptyHint = "当前周报没有日度报警次数。"
            };
            preview.PrimaryChart.Series.Add(CreateSeries("报警次数", "#DC2626", days.Select(d => (double)d.AlarmCount)));
            preview.PrimaryChart.DataPoints = days
                .Select(d => CreateDataPoint(
                    DayLabel(d.Date),
                    d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " 报警次数详情",
                    "当天报警 " + FormatInt(d.AlarmCount) + " 条，累计 " + FormatDuration(d.AlarmSeconds) + "。",
                    BuildSingleRowTable(
                        new[] { "日期", "报警次数", "报警时长", "平均单次", "Top 报警" },
                        d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        FormatInt(d.AlarmCount),
                        FormatDuration(d.AlarmSeconds),
                        FormatSeconds(d.AlarmCount > 0 ? d.AlarmSeconds / d.AlarmCount : 0d),
                        string.IsNullOrWhiteSpace(d.TopAlarm) ? "无" : d.TopAlarm),
                    CreateMetric("报警次数", FormatInt(d.AlarmCount), "#DC2626"),
                    CreateMetric("报警时长", FormatDuration(d.AlarmSeconds), "#F97316"),
                    CreateMetric("Top 报警", string.IsNullOrWhiteSpace(d.TopAlarm) ? "无" : d.TopAlarm, "#7C3AED")))
                .ToList();

            preview.SecondaryChart = new ReportChartDefinition
            {
                Title = "日度报警时长",
                Subtitle = "单位：分钟，便于识别长时报警集中日",
                Kind = ReportChartKind.Line,
                ValueFormat = ReportChartValueFormat.DurationMinutes,
                Labels = days.Select(d => DayLabel(d.Date)).ToList(),
                EmptyHint = "当前周报没有日度报警时长。"
            };
            preview.SecondaryChart.Series.Add(CreateSeries("报警时长", "#F97316", days.Select(d => d.AlarmSeconds / 60d)));
            preview.SecondaryChart.DataPoints = days
                .Select(d => CreateDataPoint(
                    DayLabel(d.Date),
                    d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " 报警时长详情",
                    "当天报警累计 " + FormatDuration(d.AlarmSeconds) + "，平均单次 " + FormatSeconds(d.AlarmCount > 0 ? d.AlarmSeconds / d.AlarmCount : 0d) + "。",
                    BuildSingleRowTable(
                        new[] { "日期", "报警次数", "报警时长", "平均单次", "Top 报警" },
                        d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        FormatInt(d.AlarmCount),
                        FormatDuration(d.AlarmSeconds),
                        FormatSeconds(d.AlarmCount > 0 ? d.AlarmSeconds / d.AlarmCount : 0d),
                        string.IsNullOrWhiteSpace(d.TopAlarm) ? "无" : d.TopAlarm),
                    CreateMetric("报警时长", FormatDuration(d.AlarmSeconds), "#F97316"),
                    CreateMetric("报警次数", FormatInt(d.AlarmCount), "#DC2626"),
                    CreateMetric("平均单次", FormatSeconds(d.AlarmCount > 0 ? d.AlarmSeconds / d.AlarmCount : 0d), "#2563EB")))
                .ToList();

            preview.TertiaryChart = new ReportChartDefinition
            {
                Title = "Top 报警时长占比",
                Subtitle = "优先关注时长占比高的报警类别",
                Kind = ReportChartKind.Donut,
                ValueFormat = ReportChartValueFormat.DurationMinutes,
                CenterText = FormatDuration(data.TotalDurationSeconds),
                CenterSubtext = "周累计时长",
                EmptyHint = "当前周报没有 Top 报警分布。"
            };
            foreach (var item in BuildTopAlarmDonutSeries(MapWeeklyTop(data.Top)))
            {
                preview.TertiaryChart.Series.Add(item);
            }
            preview.TertiaryChart.DataPoints = BuildAlarmCategoryDataPoints(MapWeeklyTop(data.Top), data.TotalDurationSeconds, "本周报警类别");

            preview.DetailTitle = "日度报警明细";
            preview.DetailTable = BuildWeeklyAlarmTable(days);

            AppendWarnings(preview.Notes, data.Warnings);
            if (data.Top != null && data.Top.Count > 0)
            {
                preview.Notes.Add("本周 Top 报警：" + string.Join("、", data.Top.Take(3).Select(t => t.Code + "（" + FormatDuration(t.DurationSeconds) + "）")));
            }
        }

        private static DataTable BuildDailyProdTable(IList<DailyProdHourStat> hours)
        {
            var hasStateData = HasStateData(hours);
            var table = hasStateData
                ? CreateTable("时段", "PASS", "抛料/不良", "总量", "UPH", "良率", "运行", "空闲", "停线报警")
                : CreateTable("时段", "PASS", "抛料/不良", "总量", "UPH", "良率");
            foreach (var item in hours)
            {
                if (hasStateData)
                {
                    table.Rows.Add(
                        HourRange(item.Hour),
                        FormatInt(item.Pass),
                        FormatInt(item.Tossing),
                        FormatInt(item.Total),
                        FormatDouble(item.Uph),
                        FormatPercent(item.Yield),
                        FormatDuration(item.RunningSeconds),
                        FormatDuration(item.IdleSeconds),
                        FormatDuration(item.LineDownSeconds));
                }
                else
                {
                    table.Rows.Add(
                        HourRange(item.Hour),
                        FormatInt(item.Pass),
                        FormatInt(item.Tossing),
                        FormatInt(item.Total),
                        FormatDouble(item.Uph),
                        FormatPercent(item.Yield));
                }
            }

            return table;
        }

        private static DataTable BuildDailyAlarmTable(IList<DailyAlarmHourStat> hours)
        {
            var table = CreateTable("时段", "报警条数", "报警时长", "平均单次", "Top代码", "Top内容");
            foreach (var item in hours)
            {
                table.Rows.Add(
                    HourRange(item.Hour),
                    FormatInt(item.AlarmCount),
                    FormatDuration(item.AlarmSeconds),
                    FormatSeconds(item.AvgSeconds()),
                    string.IsNullOrWhiteSpace(item.TopAlarmCode) ? "无" : item.TopAlarmCode,
                    string.IsNullOrWhiteSpace(item.TopAlarmContent) ? "无" : item.TopAlarmContent);
            }

            return table;
        }

        private static DataTable BuildWeeklyProdTable(IList<WeeklyProdDay> days)
        {
            var hasStateData = HasStateData(days);
            var table = hasStateData
                ? CreateTable("日期", "PASS", "抛料/不良", "总量", "UPH", "良率", "运行", "空闲", "停线报警", "备注")
                : CreateTable("日期", "PASS", "抛料/不良", "总量", "UPH", "良率", "备注");
            foreach (var item in days)
            {
                if (hasStateData)
                {
                    table.Rows.Add(
                        item.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        FormatInt(item.Pass),
                        FormatInt(item.Tossing),
                        FormatInt(item.Total),
                        FormatDouble(item.Uph),
                        FormatPercent(item.Yield),
                        FormatDuration(item.RunningSeconds),
                        FormatDuration(item.IdleSeconds),
                        FormatDuration(item.LineDownSeconds),
                        string.IsNullOrWhiteSpace(item.Note) ? "正常" : item.Note);
                }
                else
                {
                    table.Rows.Add(
                        item.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        FormatInt(item.Pass),
                        FormatInt(item.Tossing),
                        FormatInt(item.Total),
                        FormatDouble(item.Uph),
                        FormatPercent(item.Yield),
                        string.IsNullOrWhiteSpace(item.Note) ? "正常" : item.Note);
                }
            }

            return table;
        }

        private static DataTable BuildWeeklyAlarmTable(IList<WeeklyAlarmDay> days)
        {
            var table = CreateTable("日期", "报警次数", "报警时长", "平均单次", "Top 报警");
            foreach (var item in days)
            {
                var averageSeconds = item.AlarmCount > 0 ? item.AlarmSeconds / item.AlarmCount : 0d;
                table.Rows.Add(
                    item.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    FormatInt(item.AlarmCount),
                    FormatDuration(item.AlarmSeconds),
                    FormatSeconds(averageSeconds),
                    string.IsNullOrWhiteSpace(item.TopAlarm) ? "无" : item.TopAlarm);
            }

            return table;
        }

        private static DataTable CreateTable(params string[] columns)
        {
            var table = new DataTable();
            foreach (var column in columns)
            {
                table.Columns.Add(column, typeof(string));
            }

            return table;
        }

        private static IList<AlarmCategoryStat> BuildDailyAlarmTopList(DailyAlarmData data)
        {
            var direct = (data.TopAlarms ?? new List<AlarmCategoryStat>())
                .Where(x => x != null && x.DurationSeconds > 0)
                .OrderByDescending(x => x.DurationSeconds)
                .ToList();
            if (direct.Count > 0)
            {
                return direct;
            }

            return (data.Hours ?? new List<DailyAlarmHourStat>())
                .Where(x => !string.IsNullOrWhiteSpace(x.TopAlarmCode) && x.TopAlarmCode != "无" && x.TopAlarmSeconds > 0)
                .GroupBy(x => x.TopAlarmCode)
                .Select(g => new AlarmCategoryStat
                {
                    Code = g.Key,
                    Content = g.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.TopAlarmContent))?.TopAlarmContent ?? "无",
                    Count = g.Sum(x => x.AlarmCount),
                    DurationSeconds = g.Sum(x => x.TopAlarmSeconds)
                })
                .OrderByDescending(x => x.DurationSeconds)
                .ToList();
        }

        private static IList<AlarmCategoryStat> MapWeeklyTop(IList<WeeklyAlarmTop> top)
        {
            return (top ?? new List<WeeklyAlarmTop>())
                .Where(x => x != null && x.DurationSeconds > 0)
                .Select(x => new AlarmCategoryStat
                {
                    Code = x.Code,
                    Content = x.Content,
                    Count = x.Count,
                    DurationSeconds = x.DurationSeconds
                })
                .ToList();
        }

        private static IList<ReportChartSeriesDefinition> BuildTopAlarmDonutSeries(IList<AlarmCategoryStat> topAlarms)
        {
            var result = new List<ReportChartSeriesDefinition>();
            if (topAlarms == null || topAlarms.Count == 0)
            {
                return result;
            }

            var ranked = topAlarms.OrderByDescending(x => x.DurationSeconds).ToList();
            var topFive = ranked.Take(5).ToList();
            var others = ranked.Skip(5).Sum(x => x.DurationSeconds);

            for (int i = 0; i < topFive.Count; i++)
            {
                var item = topFive[i];
                result.Add(CreateSeries(item.Code, s_chartPalette[i % s_chartPalette.Length], new[] { item.DurationSeconds / 60d }));
            }

            if (others > 0)
            {
                result.Add(CreateSeries("其他", "#94A3B8", new[] { others / 60d }));
            }

            return result;
        }

        private static IList<ReportChartSeriesDefinition> BuildStateDistributionSeries(ReportMachineStateSummary state)
        {
            var result = new List<ReportChartSeriesDefinition>();
            if (!HasStateData(state))
            {
                return result;
            }

            result.Add(CreateSeries("正常运行", "#16A34A", new[] { state.RunningSeconds / 60d }));
            result.Add(CreateSeries("空闲", "#EAB308", new[] { state.IdleSeconds / 60d }));
            result.Add(CreateSeries("LINEDOWN", "#DC2626", new[] { state.LineDownSeconds / 60d }));
            if (state.OtherSeconds > 0d)
            {
                result.Add(CreateSeries("其他", "#94A3B8", new[] { state.OtherSeconds / 60d }));
            }

            return result;
        }

        private static IList<ReportChartDataPoint> BuildStateDistributionDataPoints(ReportMachineStateSummary state, string scopeText)
        {
            var result = new List<ReportChartDataPoint>();
            if (!HasStateData(state))
            {
                return result;
            }

            result.Add(BuildStateDistributionDataPoint(scopeText, "正常运行", state.RunningSeconds, state.RunningRate, "#16A34A"));
            result.Add(BuildStateDistributionDataPoint(scopeText, "空闲", state.IdleSeconds, state.IdleRate, "#EAB308"));
            result.Add(BuildStateDistributionDataPoint(scopeText, "LINEDOWN", state.LineDownSeconds, state.LineDownRate, "#DC2626"));
            if (state.OtherSeconds > 0d)
            {
                var otherRate = state.TotalObservedSeconds > 0d ? state.OtherSeconds / state.TotalObservedSeconds : 0d;
                result.Add(BuildStateDistributionDataPoint(scopeText, "其他", state.OtherSeconds, otherRate, "#94A3B8"));
            }

            return result;
        }

        private static ReportChartDataPoint BuildStateDistributionDataPoint(string scopeText, string label, double seconds, double rate, string colorHex)
        {
            return CreateDataPoint(
                label,
                scopeText + " " + label + " 详情",
                label + " 累计 " + FormatDuration(seconds) + "，占状态覆盖时长 " + FormatPercent(rate) + "。",
                BuildSingleRowTable(
                    new[] { "状态", "时长", "占比" },
                    label,
                    FormatDuration(seconds),
                    FormatPercent(rate)),
                CreateMetric("时长", FormatDuration(seconds), colorHex),
                CreateMetric("占比", FormatPercent(rate), colorHex));
        }

        private static IList<ReportChartDataPoint> BuildPassFailDataPoints(int pass, int fail, string passTitle, string failTitle)
        {
            var total = pass + fail;
            var passRate = total > 0 ? pass / (double)total : 0d;
            var failRate = total > 0 ? fail / (double)total : 0d;
            return new List<ReportChartDataPoint>
            {
                CreateDataPoint(
                    "PASS",
                    passTitle,
                    "PASS 总数 " + FormatInt(pass) + "，占总产量 " + FormatPercent(passRate) + "。",
                    BuildSingleRowTable(
                        new[] { "类型", "数量", "占比" },
                        "PASS", FormatInt(pass), FormatPercent(passRate)),
                    CreateMetric("数量", FormatInt(pass), "#2563EB"),
                    CreateMetric("占比", FormatPercent(passRate), "#2563EB")),
                CreateDataPoint(
                    "FAIL",
                    failTitle,
                    "FAIL 总数 " + FormatInt(fail) + "，占总产量 " + FormatPercent(failRate) + "。",
                    BuildSingleRowTable(
                        new[] { "类型", "数量", "占比" },
                        "FAIL", FormatInt(fail), FormatPercent(failRate)),
                    CreateMetric("数量", FormatInt(fail), "#F97316"),
                    CreateMetric("占比", FormatPercent(failRate), "#F97316"))
            };
        }

        private static ReportPreviewKpi CreateKpi(string title, string value, string hint, string colorHex)
        {
            return new ReportPreviewKpi
            {
                Title = title,
                Value = value,
                Hint = hint,
                AccentBrush = CreateBrush(colorHex)
            };
        }

        private static ReportChartMetricItem CreateMetric(string name, string value, string colorHex)
        {
            return new ReportChartMetricItem
            {
                Name = name,
                Value = value,
                AccentBrush = CreateBrush(colorHex)
            };
        }

        private static ReportChartDataPoint CreateDataPoint(
            string label,
            string detailTitle,
            string summary,
            DataTable detailTable,
            params ReportChartMetricItem[] metrics)
        {
            return new ReportChartDataPoint
            {
                Label = label,
                DetailTitle = detailTitle,
                Summary = summary,
                DetailTable = detailTable,
                Metrics = metrics != null
                    ? metrics.Where(m => m != null).ToList()
                    : new List<ReportChartMetricItem>()
            };
        }

        private static DataTable BuildSingleRowTable(IList<string> columns, params string[] values)
        {
            var table = CreateTable((columns ?? new List<string>()).ToArray());
            if (table.Columns.Count == 0)
            {
                return table;
            }

            var rowValues = new object[table.Columns.Count];
            for (int i = 0; i < rowValues.Length; i++)
            {
                rowValues[i] = values != null && i < values.Length ? values[i] : string.Empty;
            }

            table.Rows.Add(rowValues);
            return table;
        }

        private static ReportChartSeriesDefinition CreateSeries(string name, string colorHex, IEnumerable<double> values)
        {
            return new ReportChartSeriesDefinition
            {
                Name = name,
                ColorHex = colorHex,
                LegendBrush = CreateBrush(colorHex),
                Values = values != null ? values.ToList() : new List<double>()
            };
        }

        private static IList<ReportChartDataPoint> BuildAlarmCategoryDataPoints(IList<AlarmCategoryStat> topAlarms, double totalDurationSeconds, string scopeText)
        {
            var result = new List<ReportChartDataPoint>();
            if (topAlarms == null || topAlarms.Count == 0)
            {
                return result;
            }

            var ranked = topAlarms.OrderByDescending(x => x.DurationSeconds).ToList();
            var topFive = ranked.Take(5).ToList();
            var others = ranked.Skip(5).ToList();

            foreach (var item in topFive.Select((value, index) => new { value, index }))
            {
                var share = totalDurationSeconds > 0 ? item.value.DurationSeconds / totalDurationSeconds : 0d;
                result.Add(CreateDataPoint(
                    item.value.Code,
                    scopeText + " " + item.value.Code + " 详情",
                    item.value.Code + " 累计 " + FormatDuration(item.value.DurationSeconds) + "，占总体时长 " + FormatPercent(share) + "。",
                    BuildSingleRowTable(
                        new[] { "报警代码", "内容", "次数", "时长", "占比" },
                        item.value.Code,
                        string.IsNullOrWhiteSpace(item.value.Content) ? "无" : item.value.Content,
                        FormatInt(item.value.Count),
                        FormatDuration(item.value.DurationSeconds),
                        FormatPercent(share)),
                    CreateMetric("报警代码", item.value.Code, s_chartPalette[item.index % s_chartPalette.Length]),
                    CreateMetric("次数", FormatInt(item.value.Count), "#2563EB"),
                    CreateMetric("时长", FormatDuration(item.value.DurationSeconds), "#F97316"),
                    CreateMetric("占比", FormatPercent(share), "#16A34A")));
            }

            if (others.Count > 0)
            {
                var otherDuration = others.Sum(x => x.DurationSeconds);
                var otherCount = others.Sum(x => x.Count);
                var share = totalDurationSeconds > 0 ? otherDuration / totalDurationSeconds : 0d;
                result.Add(CreateDataPoint(
                    "其他",
                    scopeText + " 其他报警详情",
                    "除前五项外，其余报警累计 " + FormatDuration(otherDuration) + "，占总体时长 " + FormatPercent(share) + "。",
                    BuildSingleRowTable(
                        new[] { "类别", "报警项数", "次数合计", "时长", "占比" },
                        "其他",
                        FormatInt(others.Count),
                        FormatInt(otherCount),
                        FormatDuration(otherDuration),
                        FormatPercent(share)),
                    CreateMetric("报警项数", FormatInt(others.Count), "#94A3B8"),
                    CreateMetric("次数合计", FormatInt(otherCount), "#2563EB"),
                    CreateMetric("时长", FormatDuration(otherDuration), "#F97316"),
                    CreateMetric("占比", FormatPercent(share), "#16A34A")));
            }

            return result;
        }

        private static Brush CreateBrush(string colorHex)
        {
            try
            {
                var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(colorHex);
                if (brush.CanFreeze)
                {
                    brush.Freeze();
                }

                return brush;
            }
            catch
            {
                return Brushes.SlateBlue;
            }
        }

        private static void AppendWarnings(IList<string> notes, IList<string> warnings)
        {
            if (notes == null || warnings == null)
            {
                return;
            }

            foreach (var warning in warnings.Where(w => !string.IsNullOrWhiteSpace(w)))
            {
                notes.Add(warning);
            }
        }

        private static string BuildHintMarkdown(string message)
        {
            return "> " + (string.IsNullOrWhiteSpace(message) ? "暂无分析正文。" : message.Trim());
        }

        private static string BuildDowntimeHint(IList<DowntimeWindow> windows)
        {
            if (windows == null || windows.Count == 0)
            {
                return "当天未识别到连续无产出窗口";
            }

            var longest = windows.OrderByDescending(w => w.DurationHours).ThenBy(w => w.StartHour).First();
            return "最长无产出 " + HourRange(longest.StartHour, longest.EndHour) + "，持续 " + longest.DurationHours + " 小时";
        }

        private static string BuildLastDayDeltaHint(WeeklyProdData data)
        {
            if (data == null || data.LastDay == null || data.LastDayDelta == null)
            {
                return "最后一天与周均对比数据不足";
            }

            return "最后一天 vs 周均：产量 " + FormatSignedPercent(data.LastDayDelta.TotalDelta) + "，良率 " + FormatSignedPercent(data.LastDayDelta.YieldDelta);
        }

        private static string BuildTopAlarmHint(IList<AlarmCategoryStat> topAlarms)
        {
            if (topAlarms == null || topAlarms.Count == 0)
            {
                return "当天暂无主导报警类别";
            }

            var top = topAlarms[0];
            return top.Code + " 占比最高，累计 " + FormatDuration(top.DurationSeconds);
        }

        private static string HourLabel(int hour)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:00}:00", hour);
        }

        private static string DayLabel(DateTime day)
        {
            return day.ToString("MM-dd", CultureInfo.InvariantCulture);
        }

        private static string HourRange(int startHour)
        {
            return HourRange(startHour, (startHour + 1) % 24);
        }

        private static string HourRange(int startHour, int endHour)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:00}:00-{1:00}:00", startHour, endHour);
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
            return (value * 100d).ToString("0.00", CultureInfo.InvariantCulture) + "%";
        }

        private static string FormatSignedPercent(double value)
        {
            return value >= 0
                ? "+" + (value * 100d).ToString("0.00", CultureInfo.InvariantCulture) + "%"
                : (value * 100d).ToString("0.00", CultureInfo.InvariantCulture) + "%";
        }

        private static string FormatSeconds(double seconds)
        {
            return seconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";
        }

        private static string FormatDuration(double seconds)
        {
            if (seconds <= 0)
            {
                return "0m";
            }

            var minutes = seconds / 60d;
            if (minutes >= 60d)
            {
                return (minutes / 60d).ToString("0.0", CultureInfo.InvariantCulture) + "h";
            }

            return minutes.ToString("0.0", CultureInfo.InvariantCulture) + "m";
        }

        private static bool HasStateData(ReportMachineStateSummary state)
        {
            return state != null && state.TotalObservedSeconds > 0d;
        }

        private static bool HasStateData(IEnumerable<DailyProdHourStat> hours)
        {
            return (hours ?? new List<DailyProdHourStat>())
                .Any(h => h.RunningSeconds + h.IdleSeconds + h.LineDownSeconds + h.StateUnknownSeconds > 0d);
        }

        private static bool HasStateData(IEnumerable<WeeklyProdDay> days)
        {
            return (days ?? new List<WeeklyProdDay>())
                .Any(d => d.RunningSeconds + d.IdleSeconds + d.LineDownSeconds > 0d);
        }
    }
}

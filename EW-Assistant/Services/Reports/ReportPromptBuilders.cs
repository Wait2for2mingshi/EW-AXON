using System;
using System.Linq;
using System.Text;
using EW_Assistant.Domain.Reports;
using Newtonsoft.Json;

namespace EW_Assistant.Services.Reports
{
    /// <summary>
    /// 报表 Prompt 构造集合，按报表类型生成 REPORT_TASK/REPORT_DATA_JSON。
    /// </summary>
    public static class DailyProdReportPromptBuilder
    {
        public static ReportPromptPayload BuildPayload(DailyProdData data)
        {
            var task = BuildTaskText(data).Trim();
            var json = BuildDataJson(data);

            var sb = new StringBuilder();
            sb.AppendLine("【REPORT_TASK】");
            sb.AppendLine(task);
            sb.AppendLine();
            sb.AppendLine("【REPORT_DATA_JSON】");
            sb.AppendLine(json);

            return new ReportPromptPayload
            {
                ReportTask = task,
                ReportDataJson = json,
                CombinedPrompt = sb.ToString()
            };
        }

        public static string BuildUserPrompt(DailyProdData data)
        {
            return BuildPayload(data).CombinedPrompt;
        }

        private static string BuildTaskText(DailyProdData data)
        {
            if (data != null && data.HasStateData)
            {
                return BuildStateEnhancedTaskText(data);
            }

            return BuildProductionOnlyTaskText(data);
        }

        private static string BuildDataJson(DailyProdData data)
        {
            if (data == null || data.HasStateData)
            {
                return JsonConvert.SerializeObject(data, Formatting.None);
            }

            var payload = new
            {
                data.Date,
                data.Machine,
                HasStateData = false,
                data.DayPass,
                data.DayFail,
                data.DayTotal,
                data.DayYield,
                data.AvgUph,
                data.Tossing,
                data.TossingRate,
                data.ActiveHours,
                data.ActiveRate,
                data.Cv,
                PeakHours = MapProductionHours(data.PeakHours),
                ValleyHours = MapProductionHours(data.ValleyHours),
                data.Downtimes,
                Hours = MapProductionHours(data.Hours),
                data.MetricDefinitions,
                data.Warnings
            };
            return JsonConvert.SerializeObject(payload, Formatting.None);
        }

        private static object MapProductionHours(System.Collections.Generic.IEnumerable<DailyProdHourStat> hours)
        {
            return (hours ?? new DailyProdHourStat[0]).Select(h => new
            {
                h.Hour,
                h.Pass,
                h.Fail,
                h.Total,
                h.Yield,
                h.Uph,
                h.Tossing,
                h.TossingRate
            }).ToList();
        }

        private static string BuildProductionOnlyTaskText(DailyProdData data)
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Format("你是一名工厂产能分析师，请基于生产 CSV 统计结果编写 **{0:yyyy-MM-dd} 的当日产能报表分析**。", data.Date));
            sb.AppendLine("当前设备没有可用状态记录表数据，本次报表必须使用“普通产能日报”说辞；不要引用状态记录表、运行/空闲/LINEDOWN、状态码或运行 UPH。");
            sb.AppendLine("报表页中的 KPI、图表、逐小时明细已经由本地程序生成；你只负责输出“AI 分析正文”，不要重复抄写表格或逐项罗列 JSON。");
            sb.AppendLine("数据来源仅包含生产 CSV；本地已计算出逐小时产能、UPH、Yield、Tossing、峰值/低谷、无产出窗口等结构化字段。");
            sb.AppendLine("UPH 为单位小时产出；Yield=PASS/(PASS+Tossing)；Tossing 为 FAIL/NG/抛料数量。");
            sb.AppendLine("你需要输出以下章节（只做文字分析，不再调用工具）：");
            sb.AppendLine("1) ## 产能趋势：结合峰值/低谷、UPH、低良率与 Tossing 时段，给出 4-6 条业务可读的洞察。");
            sb.AppendLine("2) ## 产能改进建议：给出 3-5 条可执行建议，明确建议关注的时段、指标或动作。");
            sb.AppendLine("3) ## 数据注意事项：列出数据缺失或异常（若无则写“无”）。");
            sb.AppendLine();
            sb.AppendLine("写作要求：");
            sb.AppendLine("- 使用中文商务语气，避免开发术语；");
            sb.AppendLine("- 正文优先使用中文指标名，英文缩写只在必要时作为括注出现；");
            sb.AppendLine("- 结论必须基于提供的 JSON 数据，不要编造；");
            sb.AppendLine("- 重点关注产量节奏、良率、UPH、Tossing 和无产出窗口，以及可执行的改进措施；");
            sb.AppendLine("- 如 JSON 中因兼容出现空的状态字段，也必须视为不可用状态数据，不得据此生成状态结论；");
            sb.AppendLine("- 不要输出总标题，不要重复“概览指标/逐小时明细”等固定章节名；");
            sb.AppendLine("- Downtimes 表示连续无产出的小时窗口，仅用于普通产能视角分析。");
            sb.AppendLine();
            sb.AppendLine("下方提供当日的 JSON 数据，请充分利用。");

            return sb.ToString();
        }

        private static string BuildStateEnhancedTaskText(DailyProdData data)
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Format("你是一名工厂产能分析师，请基于生产 CSV + 状态记录表统计结果编写 **{0:yyyy-MM-dd} 的状态增强产能日报分析**。", data.Date));
            sb.AppendLine("当前设备已读取到有效状态记录表，本次报表必须使用“状态增强日报”说辞；请把产能、良率、Tossing 与运行/空闲/LINEDOWN 状态分开分析。");
            sb.AppendLine("报表页中的 KPI、图表、逐小时明细已经由本地程序生成；你只负责输出“AI 分析正文”，不要重复抄写表格或逐项罗列 JSON。");
            sb.AppendLine("数据来源包括生产 CSV 与状态记录表；本地已计算逐小时产能、UPH、运行 UPH、Yield、Tossing、状态时长、峰值/低谷、无产出窗口等结构化字段。");
            sb.AppendLine("状态码说明：1=正常运行，2=空闲，5=LINEDOWN-报警；运行 UPH=总产量/正常运行小时；Yield=PASS/(PASS+Tossing)，Tossing 为 FAIL/NG/抛料数量。");
            sb.AppendLine("你需要输出以下章节（只做文字分析，不再调用工具）：");
            sb.AppendLine("1) ## 状态与产能洞察：结合峰值/低谷、运行/空闲/LINEDOWN、运行 UPH、低良率与 Tossing 时段，给出 4-6 条业务可读的洞察。");
            sb.AppendLine("2) ## 状态驱动改进建议：给出 3-5 条可执行建议，明确建议关注的时段、状态、指标或动作。");
            sb.AppendLine("3) ## 数据注意事项：列出状态记录表或生产数据缺失/异常（若无则写“无”）。");
            sb.AppendLine();
            sb.AppendLine("写作要求：");
            sb.AppendLine("- 使用中文商务语气，避免开发术语；");
            sb.AppendLine("- 正文优先使用中文指标名，英文缩写只在必要时作为括注出现；");
            sb.AppendLine("- 结论必须基于提供的 JSON 数据，不要编造；");
            sb.AppendLine("- 明确区分“无产出窗口”和状态记录表中的“LINEDOWN 停线报警”，两者不能互相替代；");
            sb.AppendLine("- 重点关注运行/空闲/LINEDOWN 对 UPH、Yield、Tossing 的影响，以及可执行的改进措施；");
            sb.AppendLine("- 不要输出总标题，不要重复“概览指标/逐小时明细”等固定章节名。");
            sb.AppendLine();
            sb.AppendLine("下方提供当日的 JSON 数据，请充分利用。");

            return sb.ToString();
        }
    }

    public static class DailyAlarmReportPromptBuilder
    {
        public static ReportPromptPayload BuildPayload(DailyAlarmData data)
        {
            var task = BuildTaskText(data).Trim();
            var json = JsonConvert.SerializeObject(data, Formatting.None);
            var sb = new StringBuilder();
            sb.AppendLine("【REPORT_TASK】");
            sb.AppendLine(task);
            sb.AppendLine();
            sb.AppendLine("【REPORT_DATA_JSON】");
            sb.AppendLine(json);

            return new ReportPromptPayload
            {
                ReportTask = task,
                ReportDataJson = json,
                CombinedPrompt = sb.ToString()
            };
        }

        public static string BuildUserPrompt(DailyAlarmData data)
        {
            return BuildPayload(data).CombinedPrompt;
        }

        private static string BuildTaskText(DailyAlarmData data)
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Format("你是一名报警分析师，基于本地 CSV 统计出的结构化数据，撰写 **{0:yyyy-MM-dd} 的报警日报分析**。", data.Date));
            sb.AppendLine("报表页中的 KPI、图表、逐小时明细已经由本地程序生成；你只负责输出“AI 分析正文”，不要重复抄写表格。");
            sb.AppendLine("数据字段说明：hours 包含每小时的产能与报警条数/时长/Top 报警；dayAlarmCount/dayAlarmSeconds/peakHour 等已预计算。");
            sb.AppendLine("输出章节：");
            sb.AppendLine("1) ## 当日观察：概括报警强度、峰值时段、对产能的影响。");
            sb.AppendLine("2) ## 重点报警解读：指出报警高发/低发时段的规律，可结合逐小时报警次数、报警时长和 Top 报警。");
            sb.AppendLine("3) ## 处理建议：给出 4-6 条可执行建议，关联高发或长时报警。");
            sb.AppendLine("4) ## 数据注意事项：引用 warnings 或补充数据缺失说明（无则写“无”）。");
            sb.AppendLine("要求：中文商务语气，结论基于 JSON，不要编造，避免接口/字段名，条目化表达，不要重复固定表格内容。");
            return sb.ToString();
        }
    }

    public static class WeeklyProdReportPromptBuilder
    {
        public static ReportPromptPayload BuildPayload(WeeklyProdData data)
        {
            var task = BuildTaskText(data).Trim();
            var json = BuildDataJson(data);
            var sb = new StringBuilder();
            sb.AppendLine("【REPORT_TASK】");
            sb.AppendLine(task);
            sb.AppendLine();
            sb.AppendLine("【REPORT_DATA_JSON】");
            sb.AppendLine(json);

            return new ReportPromptPayload
            {
                ReportTask = task,
                ReportDataJson = json,
                CombinedPrompt = sb.ToString()
            };
        }

        public static string BuildUserPrompt(WeeklyProdData data)
        {
            return BuildPayload(data).CombinedPrompt;
        }

        private static string BuildTaskText(WeeklyProdData data)
        {
            if (data != null && data.HasStateData)
            {
                return BuildStateEnhancedTaskText(data);
            }

            return BuildProductionOnlyTaskText(data);
        }

        private static string BuildDataJson(WeeklyProdData data)
        {
            if (data == null || data.HasStateData)
            {
                return JsonConvert.SerializeObject(data, Formatting.None);
            }

            var payload = new
            {
                data.StartDate,
                data.EndDate,
                HasStateData = false,
                data.Pass,
                data.Fail,
                data.Total,
                data.Yield,
                data.AvgYield,
                data.MedianTotal,
                data.Volatility,
                LastDay = MapProductionDay(data.LastDay),
                data.LastDayDelta,
                data.AvgUph,
                data.Tossing,
                data.TossingRate,
                data.MetricDefinitions,
                BestDays = MapProductionDays(data.BestDays),
                WorstDays = MapProductionDays(data.WorstDays),
                Days = MapProductionDays(data.Days),
                data.Warnings
            };
            return JsonConvert.SerializeObject(payload, Formatting.None);
        }

        private static object MapProductionDays(System.Collections.Generic.IEnumerable<WeeklyProdDay> days)
        {
            return (days ?? new WeeklyProdDay[0]).Select(MapProductionDay).ToList();
        }

        private static object MapProductionDay(WeeklyProdDay day)
        {
            if (day == null)
            {
                return null;
            }

            return new
            {
                day.Date,
                day.Pass,
                day.Fail,
                day.Total,
                day.Yield,
                day.ActiveHours,
                day.Uph,
                day.Tossing,
                day.TossingRate,
                day.Note
            };
        }

        private static string BuildProductionOnlyTaskText(WeeklyProdData data)
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Format("请基于生产 CSV 统计出的结构化数据，撰写 **{0:yyyy-MM-dd} ~ {1:yyyy-MM-dd}（上一自然周）** 的普通产能周报分析。", data.StartDate, data.EndDate));
            sb.AppendLine("当前设备没有可用状态记录表数据，本次周报必须使用“普通产能周报”说辞；不要引用状态记录表、运行/空闲/LINEDOWN、状态码或运行 UPH。");
            sb.AppendLine("报表页中的 KPI、图表、日度明细已经由本地程序生成；你只负责输出“AI 分析正文”，不要复写周度 KPI 表或日度表。");
            sb.AppendLine("数据字段包含 days/bestDays/worstDays/lastDayDelta/warnings，以及 UPH、Yield、Tossing 等普通产能字段。");
            sb.AppendLine("UPH 为单位小时产出；Yield=PASS/(PASS+Tossing)；Tossing 为 FAIL/NG/抛料数量。");
            sb.AppendLine("输出章节：");
            sb.AppendLine("1) ## 周度产能总结：概括本周产量、UPH、Yield、Tossing 和最后一天相对周均的变化。");
            sb.AppendLine("2) ## 重点产能洞察：结合 bestDays/worstDays、日度趋势和低良率/高 Tossing 日期，给 4-6 条洞察。");
            sb.AppendLine("3) ## 产能风险与改进：给 2-4 条措施，明确验证指标。");
            sb.AppendLine("4) ## 数据注意事项：引用 warnings（无则写“无”）。");
            sb.AppendLine("要求：中文商务语气，依据 JSON，不要编造；不要重复固定 KPI 表和日度表；正文优先使用中文指标名，英文缩写只在必要时作为括注出现；如 JSON 中因兼容出现空的状态字段，也不得据此生成状态结论。");
            return sb.ToString();
        }

        private static string BuildStateEnhancedTaskText(WeeklyProdData data)
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Format("请基于生产 CSV + 状态记录表统计出的结构化数据，撰写 **{0:yyyy-MM-dd} ~ {1:yyyy-MM-dd}（上一自然周）** 的状态增强产能周报分析。", data.StartDate, data.EndDate));
            sb.AppendLine("当前设备已读取到有效状态记录表，本次周报必须使用“状态增强周报”说辞；请把产能趋势、良率、Tossing 与运行/空闲/LINEDOWN 状态分开分析。");
            sb.AppendLine("报表页中的 KPI、图表、日度明细已经由本地程序生成；你只负责输出“AI 分析正文”，不要复写周度 KPI 表或日度表。");
            sb.AppendLine("数据字段包含 days/bestDays/worstDays/lastDayDelta/warnings，并包含状态记录表 stateSummary、UPH、运行 UPH、Yield、Tossing 等字段。");
            sb.AppendLine("状态码说明：1=正常运行，2=空闲，5=LINEDOWN-报警；运行 UPH=总产量/正常运行小时；Yield=PASS/(PASS+Tossing)，Tossing 为 FAIL/NG/抛料数量。");
            sb.AppendLine("输出章节：");
            sb.AppendLine("1) ## 状态增强周度总结：概括本周产量、UPH、运行 UPH、Yield、Tossing、状态结构和最后一天相对周均的变化。");
            sb.AppendLine("2) ## 状态与产能洞察：结合 bestDays/worstDays、运行/空闲/LINEDOWN、低良率/高 Tossing 日期，给 4-6 条洞察。");
            sb.AppendLine("3) ## 状态驱动风险与改进：给 2-4 条措施，明确验证指标。");
            sb.AppendLine("4) ## 数据注意事项：引用 warnings，特别说明状态记录表缺口或异常（无则写“无”）。");
            sb.AppendLine("要求：中文商务语气，依据 JSON，不要编造；明确区分产能无产出与 LINEDOWN 停线报警；不要重复固定 KPI 表和日度表；正文优先使用中文指标名，英文缩写只在必要时作为括注出现。");
            return sb.ToString();
        }
    }

    public static class WeeklyAlarmReportPromptBuilder
    {
        public static ReportPromptPayload BuildPayload(WeeklyAlarmData data)
        {
            var task = BuildTaskText(data).Trim();
            var json = JsonConvert.SerializeObject(data, Formatting.None);
            var sb = new StringBuilder();
            sb.AppendLine("【REPORT_TASK】");
            sb.AppendLine(task);
            sb.AppendLine();
            sb.AppendLine("【REPORT_DATA_JSON】");
            sb.AppendLine(json);

            return new ReportPromptPayload
            {
                ReportTask = task,
                ReportDataJson = json,
                CombinedPrompt = sb.ToString()
            };
        }

        public static string BuildUserPrompt(WeeklyAlarmData data)
        {
            return BuildPayload(data).CombinedPrompt;
        }

        private static string BuildTaskText(WeeklyAlarmData data)
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Format("请基于本地报警 CSV 统计出的结构化数据，撰写 **{0:yyyy-MM-dd} ~ {1:yyyy-MM-dd}（上一自然周）** 的报警周报分析。", data.StartDate, data.EndDate));
            sb.AppendLine("报表页中的 KPI、趋势图、Top 报警图和日度表已经由本地程序生成；你只负责输出“AI 分析正文”，不要重复抄写固定表格。");
            sb.AppendLine("数据包含周度 totals/byDay/top，字段含义与 AlarmCsvTools/ProdAlarmTools 的聚合相近。");
            sb.AppendLine("输出章节：");
            sb.AppendLine("1) ## AI 周度总结：概括报警强度、波动日期和整体风险水平。");
            sb.AppendLine("2) ## Top 报警解读：结合 top 列表分析主要报警的影响和可能原因。");
            sb.AppendLine("3) ## 处置建议：给 3-5 条可执行措施或样本案例。");
            sb.AppendLine("4) ## 数据注意事项：引用 warnings（无则写“无”）。");
            sb.AppendLine("要求：中文商务语气，不要编造数据，条理清晰，不要重复固定 KPI 与日度表。");
            return sb.ToString();
        }
    }
}

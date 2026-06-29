using EW_Assistant.Warnings;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace EW_Assistant.Services.PreventiveMaintenance
{
    public sealed class PreventiveMaintenanceAnalyzer
    {
        private readonly AlarmCsvReader _alarmReader;
        private readonly ProductionCsvReader _productionReader;

        public PreventiveMaintenanceAnalyzer(AlarmCsvReader alarmReader = null, ProductionCsvReader productionReader = null)
        {
            _alarmReader = alarmReader ?? new AlarmCsvReader(ResolveAlarmRoot(LocalDataConfig.AlarmCsvRoot));
            _productionReader = productionReader ?? new ProductionCsvReader(ResolveProductionRoot(LocalDataConfig.ProductionCsvRoot));
        }

        public PreventiveMaintenanceReport Analyze(int windowDays, DateTime? now = null)
        {
            if (windowDays <= 0)
            {
                windowDays = 7;
            }

            var today = (now ?? DateTime.Now).Date;
            var anchor = FindAnchorDate(today);
            var currentStart = anchor.AddDays(-(windowDays - 1));
            var currentEnd = anchor.AddDays(1);
            var baselineStart = currentStart.AddDays(-windowDays);
            var baselineEnd = currentStart;

            var currentAlarms = _alarmReader.GetAlarms(currentStart, currentEnd).ToList();
            var baselineAlarms = _alarmReader.GetAlarms(baselineStart, baselineEnd).ToList();
            var currentProduction = _productionReader.GetProductionRange(currentStart, currentEnd).ToList();
            var baselineProduction = _productionReader.GetProductionRange(baselineStart, baselineEnd).ToList();

            var currentPass = currentProduction.Sum(x => x.Pass);
            var currentFail = currentProduction.Sum(x => x.Fail);
            var baselinePass = baselineProduction.Sum(x => x.Pass);
            var baselineFail = baselineProduction.Sum(x => x.Fail);

            var report = new PreventiveMaintenanceReport
            {
                GeneratedAt = DateTime.Now,
                AnchorDate = anchor,
                WindowDays = windowDays,
                CurrentStart = currentStart,
                CurrentEnd = currentEnd.AddTicks(-1),
                BaselineStart = baselineStart,
                BaselineEnd = baselineEnd.AddTicks(-1),
                AlarmRoot = _alarmReader.Root,
                ProductionRoot = _productionReader.Root,
                CurrentAlarmCount = currentAlarms.Sum(x => x.Count),
                BaselineAlarmCount = baselineAlarms.Sum(x => x.Count),
                CurrentDowntimeMinutes = currentAlarms.Sum(x => x.DowntimeMinutes),
                BaselineDowntimeMinutes = baselineAlarms.Sum(x => x.DowntimeMinutes),
                CurrentPass = currentPass,
                CurrentFail = currentFail,
                BaselinePass = baselinePass,
                BaselineFail = baselineFail,
                CurrentYield = Yield(currentPass, currentFail),
                BaselineYield = Yield(baselinePass, baselineFail)
            };

            report.RiskItems = BuildRiskItems(currentAlarms, baselineAlarms, report);
            report.CategoryTrends = BuildCategoryTrends(currentAlarms, baselineAlarms);
            report.DailyTrends = BuildDailyTrends(currentStart, currentEnd, currentAlarms, currentProduction);
            report.OverallRiskScore = CalculateOverallRisk(report);
            report.OverallRiskLevel = ToRiskLevel(report.OverallRiskScore);
            report.OverallSummary = BuildLocalSummary(report);
            return report;
        }

        private DateTime FindAnchorDate(DateTime today)
        {
            var latest = new List<DateTime>();
            latest.AddRange(FindDatesFromDirectory(_alarmReader.Root, @"^(?<y>20\d{2})-(?<m>\d{2})-(?<d>\d{2})"));
            latest.AddRange(FindDatesFromDirectory(_productionReader.Root, @"(?<ymd>20\d{6})"));

            var max = latest.Where(x => x <= today).DefaultIfEmpty(today).Max();
            return max == default(DateTime) ? today : max.Date;
        }

        private static IEnumerable<DateTime> FindDatesFromDirectory(string root, string pattern)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                yield break;
            }

            foreach (var file in Directory.EnumerateFiles(root, "*.csv", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileNameWithoutExtension(file) ?? string.Empty;
                var match = Regex.Match(name, pattern, RegexOptions.IgnoreCase);
                if (!match.Success)
                {
                    continue;
                }

                DateTime day;
                if (match.Groups["ymd"].Success)
                {
                    if (DateTime.TryParseExact(match.Groups["ymd"].Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day))
                    {
                        yield return day.Date;
                    }
                }
                else
                {
                    int y;
                    int m;
                    int d;
                    if (int.TryParse(match.Groups["y"].Value, out y)
                        && int.TryParse(match.Groups["m"].Value, out m)
                        && int.TryParse(match.Groups["d"].Value, out d))
                    {
                        DateTime? parsed = null;
                        try
                        {
                            parsed = new DateTime(y, m, d);
                        }
                        catch
                        {
                            // ignore invalid file names
                        }

                        if (parsed.HasValue)
                        {
                            yield return parsed.Value;
                        }
                    }
                }
            }
        }

        private static IList<PreventiveRiskItem> BuildRiskItems(
            IList<AlarmHourStat> currentAlarms,
            IList<AlarmHourStat> baselineAlarms,
            PreventiveMaintenanceReport report)
        {
            var baselineByCode = baselineAlarms
                .GroupBy(x => NormalizeKey(x.Code))
                .ToDictionary(x => x.Key, x => x.ToList(), StringComparer.OrdinalIgnoreCase);

            var productionPenalty = 0;
            if (report.CurrentFail > report.BaselineFail && report.CurrentYield + 0.005 < report.BaselineYield)
            {
                productionPenalty = 12;
            }
            else if (report.CurrentYield + 0.003 < report.BaselineYield)
            {
                productionPenalty = 8;
            }

            var result = new List<PreventiveRiskItem>();
            foreach (var group in currentAlarms.GroupBy(x => NormalizeKey(x.Code)))
            {
                var rows = group.ToList();
                baselineByCode.TryGetValue(group.Key, out var baselineRows);
                baselineRows = baselineRows ?? new List<AlarmHourStat>();

                var currentCount = rows.Sum(x => x.Count);
                var baselineCount = baselineRows.Sum(x => x.Count);
                var currentDowntime = rows.Sum(x => x.DowntimeMinutes);
                var baselineDowntime = baselineRows.Sum(x => x.DowntimeMinutes);
                var activeDays = rows.Select(x => x.Hour.Date).Distinct().Count();
                var code = FirstNonEmpty(rows.Select(x => x.Code)) ?? group.Key;
                var message = FirstNonEmpty(rows.Select(x => x.Message)) ?? string.Empty;
                var category = FirstNonEmpty(rows.Select(x => x.Category)) ?? "未分类";
                var score = CalculateRiskScore(
                    currentCount,
                    baselineCount,
                    currentDowntime,
                    baselineDowntime,
                    activeDays,
                    productionPenalty,
                    category,
                    message);

                result.Add(new PreventiveRiskItem
                {
                    Code = code,
                    Message = message,
                    Category = category,
                    RiskScore = score,
                    RiskLevel = ToRiskLevel(score),
                    CurrentCount = currentCount,
                    BaselineCount = baselineCount,
                    CountDelta = currentCount - baselineCount,
                    CountRatio = (currentCount + 1d) / (baselineCount + 1d),
                    CurrentDowntimeMinutes = currentDowntime,
                    BaselineDowntimeMinutes = baselineDowntime,
                    DowntimeDeltaMinutes = currentDowntime - baselineDowntime,
                    ActiveDays = activeDays,
                    ReasonSummary = BuildReason(currentCount, baselineCount, currentDowntime, baselineDowntime, activeDays, report),
                    SuggestedChecks = BuildSuggestedChecks(category, message)
                });
            }

            return result
                .OrderByDescending(x => x.RiskScore)
                .ThenByDescending(x => x.CurrentCount)
                .ThenByDescending(x => x.CurrentDowntimeMinutes)
                .Take(12)
                .ToList();
        }

        private static IList<PreventiveCategoryTrend> BuildCategoryTrends(
            IList<AlarmHourStat> currentAlarms,
            IList<AlarmHourStat> baselineAlarms)
        {
            var current = currentAlarms
                .GroupBy(x => string.IsNullOrWhiteSpace(x.Category) ? "未分类" : x.Category)
                .ToDictionary(x => x.Key, x => new
                {
                    Count = x.Sum(r => r.Count),
                    Downtime = x.Sum(r => r.DowntimeMinutes)
                }, StringComparer.OrdinalIgnoreCase);

            var baseline = baselineAlarms
                .GroupBy(x => string.IsNullOrWhiteSpace(x.Category) ? "未分类" : x.Category)
                .ToDictionary(x => x.Key, x => new
                {
                    Count = x.Sum(r => r.Count),
                    Downtime = x.Sum(r => r.DowntimeMinutes)
                }, StringComparer.OrdinalIgnoreCase);

            return current.Keys.Union(baseline.Keys, StringComparer.OrdinalIgnoreCase)
                .Select(key =>
                {
                    current.TryGetValue(key, out var cur);
                    baseline.TryGetValue(key, out var baseValue);
                    return new PreventiveCategoryTrend
                    {
                        Category = key,
                        CurrentCount = cur?.Count ?? 0,
                        BaselineCount = baseValue?.Count ?? 0,
                        CurrentDowntimeMinutes = cur?.Downtime ?? 0d,
                        BaselineDowntimeMinutes = baseValue?.Downtime ?? 0d
                    };
                })
                .OrderByDescending(x => x.CurrentCount)
                .ThenByDescending(x => x.CurrentDowntimeMinutes)
                .Take(8)
                .ToList();
        }

        private static IList<PreventiveDailyTrend> BuildDailyTrends(
            DateTime start,
            DateTime end,
            IList<AlarmHourStat> alarms,
            IList<ProductionHourRecord> production)
        {
            var alarmByDay = alarms
                .GroupBy(x => x.Hour.Date)
                .ToDictionary(x => x.Key, x => new
                {
                    Count = x.Sum(r => r.Count),
                    Downtime = x.Sum(r => r.DowntimeMinutes)
                });

            var productionByDay = production
                .GroupBy(x => x.Hour.Date)
                .ToDictionary(x => x.Key, x => new
                {
                    Pass = x.Sum(r => r.Pass),
                    Fail = x.Sum(r => r.Fail)
                });

            var result = new List<PreventiveDailyTrend>();
            for (var day = start.Date; day < end.Date; day = day.AddDays(1))
            {
                alarmByDay.TryGetValue(day, out var alarm);
                productionByDay.TryGetValue(day, out var prod);
                result.Add(new PreventiveDailyTrend
                {
                    Day = day,
                    AlarmCount = alarm?.Count ?? 0,
                    DowntimeMinutes = alarm?.Downtime ?? 0d,
                    Pass = prod?.Pass ?? 0,
                    Fail = prod?.Fail ?? 0
                });
            }

            return result;
        }

        private static int CalculateRiskScore(
            int currentCount,
            int baselineCount,
            double currentDowntime,
            double baselineDowntime,
            int activeDays,
            int productionPenalty,
            string category,
            string message)
        {
            var score = 0;
            if (currentCount >= 10) score += 25;
            else if (currentCount >= 5) score += 18;
            else if (currentCount >= 2) score += 10;
            else if (currentCount > 0) score += 5;

            var countDelta = currentCount - baselineCount;
            if (countDelta > 0) score += Math.Min(25, countDelta * 4);
            var ratio = (currentCount + 1d) / (baselineCount + 1d);
            if (ratio >= 3d) score += 18;
            else if (ratio >= 2d) score += 12;
            else if (ratio >= 1.5d) score += 6;

            if (currentDowntime >= 60d) score += 15;
            else if (currentDowntime >= 20d) score += 10;
            else if (currentDowntime >= 5d) score += 5;

            var downtimeDelta = currentDowntime - baselineDowntime;
            if (downtimeDelta > 0) score += Math.Min(15, (int)Math.Ceiling(downtimeDelta / 5d));
            if (activeDays >= 4) score += 10;
            else if (activeDays >= 2) score += 5;

            score += GetEquipmentPenalty(category, message);
            score += productionPenalty;
            return Math.Max(0, Math.Min(100, score));
        }

        private static int CalculateOverallRisk(PreventiveMaintenanceReport report)
        {
            if (report.RiskItems == null || report.RiskItems.Count == 0)
            {
                return 0;
            }

            var top = report.RiskItems.Take(3).ToList();
            var score = top.Count == 0 ? 0 : (int)Math.Round(top.Average(x => x.RiskScore));
            if (report.CurrentAlarmCount > report.BaselineAlarmCount * 1.5 && report.CurrentAlarmCount >= 5)
            {
                score += 8;
            }
            if (report.CurrentYield + 0.005 < report.BaselineYield)
            {
                score += 8;
            }

            return Math.Max(0, Math.Min(100, score));
        }

        private static int GetEquipmentPenalty(string category, string message)
        {
            var text = ((category ?? string.Empty) + " " + (message ?? string.Empty)).ToLowerInvariant();
            if (ContainsAny(text, "气缸", "夹爪", "感应", "传感", "顶升", "取料", "放料", "ccd", "视觉", "相机"))
            {
                return 15;
            }
            if (ContainsAny(text, "mes", "上传", "查询", "数据"))
            {
                return 5;
            }
            return 8;
        }

        private static string BuildSuggestedChecks(string category, string message)
        {
            var text = (category ?? string.Empty) + " " + (message ?? string.Empty);
            if (ContainsAny(text, "气缸", "夹爪", "顶升"))
            {
                return "检查气压、气缸动作顺畅性、磁性开关反馈、夹爪/顶升机构卡滞与节流阀设定。";
            }
            if (ContainsAny(text, "感应", "传感"))
            {
                return "检查传感器安装位置、触发距离、线缆接头、IO 输入稳定性和遮挡污染。";
            }
            if (ContainsAny(text, "CCD", "ccd", "视觉", "相机", "拍照"))
            {
                return "检查镜头/光源清洁度、相机固定、拍照触发、产品位置偏移和视觉阈值。";
            }
            if (ContainsAny(text, "MES", "mes", "上传", "查询", "数据"))
            {
                return "检查 MES 网络连通、接口响应、工站数据缓存、条码/工单信息完整性。";
            }
            return "结合报警位置做机构清洁、紧固、线缆接头、动作节拍和复位稳定性点检。";
        }

        private static string BuildReason(
            int currentCount,
            int baselineCount,
            double currentDowntime,
            double baselineDowntime,
            int activeDays,
            PreventiveMaintenanceReport report)
        {
            var parts = new List<string>();
            var countDelta = currentCount - baselineCount;
            if (countDelta > 0)
            {
                parts.Add($"次数较上一窗口增加 {countDelta} 次");
            }
            else
            {
                parts.Add($"当前窗口出现 {currentCount} 次");
            }

            var downtimeDelta = currentDowntime - baselineDowntime;
            if (downtimeDelta > 1d)
            {
                parts.Add($"报警时长增加 {downtimeDelta:F1} 分钟");
            }
            if (activeDays >= 2)
            {
                parts.Add($"分布在 {activeDays} 天，存在复发迹象");
            }
            if (report.CurrentYield + 0.005 < report.BaselineYield)
            {
                parts.Add("同期良率下降");
            }

            return string.Join("；", parts);
        }

        private static string BuildLocalSummary(PreventiveMaintenanceReport report)
        {
            if (report.RiskItems == null || report.RiskItems.Count == 0)
            {
                return "当前窗口未发现明显报警风险，建议继续保持常规点检。";
            }

            var top = report.RiskItems.First();
            return $"当前设备健康风险为 {report.OverallRiskLevel}（{report.OverallRiskScore} 分）。重点关注 {top.Code}：{top.Message}，{top.ReasonSummary}。建议：{top.SuggestedChecks}";
        }

        private static string ToRiskLevel(int score)
        {
            if (score >= 70) return "高风险";
            if (score >= 40) return "中风险";
            return "低风险";
        }

        private static string NormalizeKey(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "UNKNOWN" : value.Trim();
        }

        private static string FirstNonEmpty(IEnumerable<string> values)
        {
            return values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        }

        private static bool ContainsAny(string text, params string[] keywords)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            foreach (var keyword in keywords)
            {
                if (!string.IsNullOrWhiteSpace(keyword)
                    && text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static double Yield(int pass, int fail)
        {
            var total = pass + fail;
            return total <= 0 ? 0d : (double)pass / total;
        }

        private static string ResolveAlarmRoot(string configuredRoot)
        {
            var candidates = new List<string>();
            AddCandidate(candidates, configuredRoot);
            AddCandidate(candidates, CombineIfRooted(configuredRoot, "报警信息Log"));
            AddCandidate(candidates, @"D:\zhuomian\Data\报警信息Log");

            return candidates.FirstOrDefault(HasAlarmFiles) ?? configuredRoot;
        }

        private static string ResolveProductionRoot(string configuredRoot)
        {
            var candidates = new List<string>();
            AddCandidate(candidates, configuredRoot);
            AddCandidate(candidates, CombineIfRooted(configuredRoot, "小时产量"));
            AddCandidate(candidates, CombineIfRooted(configuredRoot, Path.Combine("生产数据", "小时产量")));
            AddCandidate(candidates, @"D:\zhuomian\Data\生产数据\小时产量");

            return candidates.FirstOrDefault(HasProductionFiles) ?? configuredRoot;
        }

        private static void AddCandidate(ICollection<string> candidates, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var normalized = path.Trim();
            if (!candidates.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(normalized);
            }
        }

        private static string CombineIfRooted(string root, string child)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                return string.Empty;
            }

            try
            {
                return Path.Combine(root.Trim(), child);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool HasAlarmFiles(string root)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                {
                    return false;
                }

                return Directory.EnumerateFiles(root, "*.csv", SearchOption.TopDirectoryOnly)
                    .Any(path => Regex.IsMatch(Path.GetFileName(path) ?? string.Empty, @"^20\d{2}-\d{2}-\d{2}(?:-.*)?\.csv$", RegexOptions.IgnoreCase));
            }
            catch
            {
                return false;
            }
        }

        private static bool HasProductionFiles(string root)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                {
                    return false;
                }

                return Directory.EnumerateFiles(root, "*.csv", SearchOption.TopDirectoryOnly)
                    .Any(path => Regex.IsMatch(Path.GetFileName(path) ?? string.Empty, @"小时产量20\d{6}\.csv$", RegexOptions.IgnoreCase));
            }
            catch
            {
                return false;
            }
        }
    }
}

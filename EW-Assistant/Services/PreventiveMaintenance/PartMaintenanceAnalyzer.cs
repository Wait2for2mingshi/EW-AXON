using EW_Assistant.Warnings;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace EW_Assistant.Services.PreventiveMaintenance
{
    public sealed class PartMaintenanceAnalyzer
    {
        private const string FallbackPartCsvPath = @"D:\zhuomian\T66_TCT_寿命监控Log_20260701\寿命监控Log";

        public PartMaintenanceReport Analyze(string rootPath)
        {
            var report = new PartMaintenanceReport
            {
                RootPath = (rootPath ?? string.Empty).Trim()
            };

            if (string.IsNullOrWhiteSpace(report.RootPath))
            {
                report.RootPath = FallbackPartCsvPath;
                report.StatusMessage = "未配置零件 CSV 地址，已尝试读取测试目录。";
            }

            if (!Directory.Exists(report.RootPath))
            {
                var configuredPath = report.RootPath;
                if (!TryUseFallbackPath(report, "配置目录不存在：" + configuredPath + "，已尝试读取测试目录。"))
                {
                    report.StatusMessage = "零件 CSV 文件夹不存在：" + configuredPath + "；测试目录也不存在：" + FallbackPartCsvPath;
                    BuildRisks(report);
                    return report;
                }
            }

            var files = ListCsvFiles(report.RootPath);
            if (files.Count == 0 && TryUseFallbackPath(report, "配置目录没有 CSV 文件，已读取测试目录。"))
            {
                files = ListCsvFiles(report.RootPath);
            }

            report.FileCount = files.Count;

            var summaries = new List<PartMaintenanceFileSummary>();
            foreach (var file in files)
            {
                if (TryReadFile(file, out var summary))
                {
                    summaries.Add(summary);
                }
            }

            if (summaries.Count == 0)
            {
                if (TryUseFallbackPath(report, files.Count == 0 ? "配置目录没有 CSV 文件，已读取测试目录。" : "配置目录 CSV 未识别到气缸或真空吸数据，已读取测试目录。"))
                {
                    files = ListCsvFiles(report.RootPath);
                    report.FileCount = files.Count;
                    foreach (var file in files)
                    {
                        if (TryReadFile(file, out var summary))
                        {
                            summaries.Add(summary);
                        }
                    }
                }

                if (summaries.Count == 0)
                {
                    report.StatusMessage = files.Count == 0
                        ? "当前文件夹没有 CSV 文件。"
                        : "当前 CSV 未识别到气缸或真空吸数据。";
                    BuildRisks(report);
                    return report;
                }
            }

            report.LatestDate = summaries.Max(x => x.Date);
            BuildTrend(report.CylinderTrend, summaries.Where(x => x.Kind == PartMaintenanceKind.Cylinder), PartMaintenanceKind.Cylinder);
            BuildTrend(report.VacuumTrend, summaries.Where(x => x.Kind == PartMaintenanceKind.Vacuum), PartMaintenanceKind.Vacuum);
            BuildComponentStatuses(report.CylinderStatuses, summaries.SelectMany(x => x.Components).Where(x => x.Kind == PartMaintenanceKind.Cylinder), PartMaintenanceKind.Cylinder);
            BuildComponentStatuses(report.VacuumStatuses, summaries.SelectMany(x => x.Components).Where(x => x.Kind == PartMaintenanceKind.Vacuum), PartMaintenanceKind.Vacuum);
            report.StatusMessage = (string.Equals(report.RootPath, FallbackPartCsvPath, StringComparison.OrdinalIgnoreCase) ? "已读取测试目录；" : string.Empty)
                                   + "已读取 " + report.FileCount + " 个 CSV，识别 " + summaries.Count + " 个有效零件数据文件。";
            BuildRisks(report);
            return report;
        }

        private static List<string> ListCsvFiles(string rootPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
                return new List<string>();

            return Directory.EnumerateFiles(rootPath, "*.csv", SearchOption.TopDirectoryOnly)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool TryUseFallbackPath(PartMaintenanceReport report, string statusMessage)
        {
            if (report == null)
                return false;

            if (string.Equals(report.RootPath, FallbackPartCsvPath, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!Directory.Exists(FallbackPartCsvPath))
                return false;

            report.RootPath = FallbackPartCsvPath;
            report.StatusMessage = statusMessage;
            return true;
        }

        private static bool TryReadFile(string file, out PartMaintenanceFileSummary summary)
        {
            summary = null;
            var name = Path.GetFileName(file) ?? string.Empty;
            if (!TryClassify(name, out var kind))
                return false;

            var date = ParseDateFromFileName(name);
            var sourceName = string.Empty;
            var sampleCount = 0;
            var abnormalCount = 0;
            var numericValues = new List<double>();

            try
            {
                using (var reader = CsvEncoding.OpenReader(file))
                {
                    var headerLine = reader.ReadLine();
                    var headers = SplitCsvLine(headerLine);
                    sourceName = BuildFileSourceName(name);
                    var separator = DetectSeparator(headerLine);
                    var componentAccumulators = new Dictionary<int, ComponentAccumulator>();

                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line))
                            continue;

                        var cells = SplitCsvLine(line, separator);
                        for (var i = 0; i < cells.Count; i++)
                        {
                            var header = i < headers.Count ? headers[i] : string.Empty;
                            var cell = (cells[i] ?? string.Empty).Trim();
                            if (string.IsNullOrWhiteSpace(cell) || ShouldSkipCell(header, cell))
                                continue;

                            var component = GetOrCreateComponentAccumulator(componentAccumulators, i, header, kind, date, sourceName);
                            if (IsAbnormalText(cell))
                            {
                                abnormalCount++;
                                sampleCount++;
                                component.AddAbnormal();
                                continue;
                            }

                            if (IsNormalText(cell))
                            {
                                sampleCount++;
                                component.AddNormal();
                                continue;
                            }

                            if (TryParseNumber(cell, out var value))
                            {
                                if (IsAbnormalIndicatorHeader(header))
                                {
                                    if (Math.Abs(value) > 0.0001d)
                                    {
                                        abnormalCount++;
                                        component.AddAbnormal();
                                    }
                                    else
                                    {
                                        component.AddNormal();
                                    }

                                    sampleCount++;
                                    continue;
                                }

                                numericValues.Add(value);
                                sampleCount++;
                                component.AddValue(value);
                            }
                        }
                    }

                    var componentSummaries = componentAccumulators.Values
                        .Where(x => x.SampleCount > 0)
                        .Select(x => x.ToSummary())
                        .ToList();

                    summary = new PartMaintenanceFileSummary
                    {
                        Date = date,
                        Kind = kind,
                        SourceName = sourceName,
                        FileName = name,
                        SampleCount = sampleCount,
                        AbnormalCount = abnormalCount,
                        HasNumericValue = numericValues.Count > 0,
                        NumericAverage = numericValues.Count == 0 ? 0d : numericValues.Average(),
                        NumericMax = numericValues.Count == 0 ? 0d : numericValues.Max()
                    };
                    summary.Components.AddRange(componentSummaries);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static void BuildTrend(ICollection<PartMaintenanceTrendPoint> target, IEnumerable<PartMaintenanceFileSummary> source, PartMaintenanceKind kind)
        {
            foreach (var group in source.GroupBy(x => x.Date.Date).OrderBy(x => x.Key))
            {
                var files = group.ToList();
                var numericFiles = files.Where(x => x.HasNumericValue).ToList();
                var hasNumeric = numericFiles.Count > 0;
                var value = hasNumeric
                    ? numericFiles.Average(x => x.NumericAverage)
                    : CalculateAbnormalRate(files) * 100d;

                target.Add(new PartMaintenanceTrendPoint
                {
                    Date = group.Key,
                    Kind = kind,
                    FileCount = files.Count,
                    SampleCount = files.Sum(x => x.SampleCount),
                    AbnormalCount = files.Sum(x => x.AbnormalCount),
                    HasAbnormal = files.Any(x => x.HasAbnormal),
                    HasNumericValue = hasNumeric,
                    Value = value,
                    SourceNames = string.Join(" / ", files.Select(x => x.SourceName).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct())
                });
            }
        }

        private static void BuildComponentStatuses(ICollection<PartMaintenanceComponentStatus> target, IEnumerable<PartMaintenanceComponentFileSummary> source, PartMaintenanceKind kind)
        {
            foreach (var group in source
                .Where(x => !string.IsNullOrWhiteSpace(x.ComponentName))
                .GroupBy(x => x.ComponentName.Trim())
                .OrderByDescending(x => CalculateComponentRiskScore(x.ToList(), kind))
                .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                var items = group.ToList();
                var numericItems = items.Where(x => x.HasNumericValue).ToList();
                var sampleCount = items.Sum(x => x.SampleCount);
                var abnormalCount = items.Sum(x => x.AbnormalCount);
                var averageValue = numericItems.Count == 0 ? 0d : numericItems.Average(x => x.AverageValue);
                var maxValue = numericItems.Count == 0 ? 0d : numericItems.Max(x => x.MaxValue);
                var latestItem = items.OrderByDescending(x => x.Date).FirstOrDefault(x => x.HasNumericValue) ?? items.OrderByDescending(x => x.Date).First();
                var score = CalculateComponentRiskScore(items, kind);
                var level = score >= 70 ? "高风险" : score >= 40 ? "中风险" : score > 0 ? "低风险" : "正常";

                target.Add(new PartMaintenanceComponentStatus
                {
                    PartType = kind == PartMaintenanceKind.Cylinder ? "气缸" : "真空吸",
                    ComponentName = group.Key,
                    SourceNames = string.Join(" / ", items.Select(x => x.SourceName).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct()),
                    SampleCount = sampleCount,
                    AbnormalCount = abnormalCount,
                    AverageValue = averageValue,
                    MaxValue = maxValue,
                    LatestValue = latestItem.HasNumericValue ? latestItem.LatestValue : 0d,
                    RiskLevel = level,
                    RiskScore = score,
                    Summary = BuildComponentSummary(group.Key, items, score),
                    Suggestion = kind == PartMaintenanceKind.Cylinder
                        ? "检查气压、电磁阀、密封圈、导轨阻力、原位/动位传感器和线缆接触。"
                        : "检查吸嘴磨损/堵塞、真空管路漏气、过滤器、真空发生器和吸附位置。"
                });
            }
        }

        private static int CalculateComponentRiskScore(IList<PartMaintenanceComponentFileSummary> items, PartMaintenanceKind kind)
        {
            if (items == null || items.Count == 0)
                return 0;

            var score = 0;
            var abnormalCount = items.Sum(x => x.AbnormalCount);
            if (abnormalCount > 0)
                score += Math.Min(60, abnormalCount * 15);

            var numericItems = items.Where(x => x.HasNumericValue).ToList();
            if (numericItems.Count > 0)
            {
                var max = numericItems.Max(x => x.MaxValue);
                var avg = numericItems.Average(x => x.AverageValue);
                var latest = numericItems.OrderByDescending(x => x.Date).First().LatestValue;

                if (kind == PartMaintenanceKind.Cylinder)
                {
                    if (max >= 1.0d) score += 45;
                    else if (max >= 0.7d) score += 30;
                    else if (max >= 0.5d) score += 18;
                    else if (max >= 0.35d) score += 8;
                }
                else
                {
                    if (max >= 60d) score += 45;
                    else if (max >= 45d) score += 30;
                    else if (max >= 30d) score += 18;
                    else if (max >= 15d) score += 8;
                }

                if (avg > 0d && latest > avg * 1.3d)
                    score += 15;
            }

            var activeSources = items.Select(x => x.SourceName).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().Count();
            if (kind == PartMaintenanceKind.Cylinder && activeSources >= 2 && abnormalCount > 0)
                score += 10;

            return Math.Max(0, Math.Min(100, score));
        }

        private static string BuildComponentSummary(string componentName, IList<PartMaintenanceComponentFileSummary> items, int score)
        {
            var latest = items.OrderByDescending(x => x.Date).First();
            var abnormalCount = items.Sum(x => x.AbnormalCount);
            var numericItems = items.Where(x => x.HasNumericValue).ToList();
            if (numericItems.Count == 0)
                return componentName + " 最近日期 " + latest.Date.ToString("yyyy-MM-dd") + "，异常记录 " + abnormalCount + " 条。";

            var max = numericItems.Max(x => x.MaxValue);
            var avg = numericItems.Average(x => x.AverageValue);
            return componentName + " 平均 " + avg.ToString("0.###") + "，最大 " + max.ToString("0.###") + "，异常 " + abnormalCount + " 条，风险分 " + score + "。";
        }

        private static void BuildRisks(PartMaintenanceReport report)
        {
            report.Risks.Clear();
            report.Risks.Add(BuildRisk("气缸", report.CylinderTrend, true));
            report.Risks.Add(BuildRisk("真空吸", report.VacuumTrend, false));
        }

        private static PartMaintenanceRisk BuildRisk(string partName, IList<PartMaintenanceTrendPoint> trend, bool isCylinder)
        {
            if (trend == null || trend.Count == 0)
            {
                return new PartMaintenanceRisk
                {
                    PartName = partName,
                    RiskLevel = "无数据",
                    RiskScore = 0,
                    Summary = "未读取到" + partName + " CSV 数据。",
                    Suggestion = "确认设置中的零件 CSV 地址是否正确，文件名是否包含“" + (isCylinder ? "气缸原位/气缸动位" : "真空吸") + "”。"
                };
            }

            var latest = trend[trend.Count - 1];
            var abnormalDays = trend.Count(x => x.HasAbnormal);
            var abnormalCount = trend.Sum(x => x.AbnormalCount);
            var score = abnormalDays * 25 + Math.Min(30, abnormalCount * 5);

            if (trend.Count >= 2)
            {
                var previous = trend[trend.Count - 2];
                if (latest.Value > previous.Value * 1.2d && latest.Value - previous.Value > 0.01d)
                {
                    score += 20;
                }
            }

            if (trend.Count >= 4)
            {
                var firstHalf = trend.Take(trend.Count / 2).Average(x => x.Value);
                var secondHalf = trend.Skip(trend.Count / 2).Average(x => x.Value);
                if (secondHalf > firstHalf * 1.2d && secondHalf - firstHalf > 0.01d)
                {
                    score += 15;
                }
            }

            score = Math.Max(0, Math.Min(100, score));
            var level = score >= 70 ? "高风险" : score >= 40 ? "中风险" : score > 0 ? "低风险" : "正常";
            var unit = latest.HasNumericValue ? "趋势值" : "异常率";
            var summary = partName + "最近数据点 " + latest.Date.ToString("yyyy-MM-dd") + "，" + unit + " " + latest.Value.ToString("0.###") + "，异常记录 " + latest.AbnormalCount + " 条。";
            if (isCylinder && latest.HasAbnormal)
            {
                summary += " 气缸原位或动位任一数据异常，已按气缸异常处理。";
            }

            return new PartMaintenanceRisk
            {
                PartName = partName,
                RiskLevel = level,
                RiskScore = score,
                Summary = summary,
                Suggestion = isCylinder
                    ? "建议检查气压稳定性、电磁阀响应、气缸密封圈、导轨阻力、原位/动位传感器固定与线缆接触。"
                    : "建议检查吸嘴磨损和堵塞、真空管路漏气、过滤器、真空发生器、产品接触面位置与吸附时间。"
            };
        }

        private static double CalculateAbnormalRate(IList<PartMaintenanceFileSummary> files)
        {
            var sampleCount = files.Sum(x => x.SampleCount);
            if (sampleCount <= 0)
                return files.Any(x => x.HasAbnormal) ? 1d : 0d;

            return (double)files.Sum(x => x.AbnormalCount) / sampleCount;
        }

        private static bool TryClassify(string fileName, out PartMaintenanceKind kind)
        {
            if (fileName.IndexOf("真空吸", StringComparison.OrdinalIgnoreCase) >= 0
                || fileName.IndexOf("真空", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                kind = PartMaintenanceKind.Vacuum;
                return true;
            }

            if (fileName.IndexOf("气缸", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                kind = PartMaintenanceKind.Cylinder;
                return true;
            }

            kind = PartMaintenanceKind.Cylinder;
            return false;
        }

        private static DateTime ParseDateFromFileName(string fileName)
        {
            var digits = new string((fileName ?? string.Empty).TakeWhile(char.IsDigit).ToArray());
            if (digits.Length >= 8
                && DateTime.TryParseExact(digits.Substring(0, 8), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var compact))
            {
                return compact.Date;
            }

            if (!string.IsNullOrWhiteSpace(fileName))
            {
                for (var i = 0; i <= fileName.Length - 10; i++)
                {
                    var candidate = fileName.Substring(i, 10);
                    if (DateTime.TryParseExact(candidate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dashed))
                        return dashed.Date;
                }
            }

            return DateTime.Today;
        }

        private static ComponentAccumulator GetOrCreateComponentAccumulator(
            IDictionary<int, ComponentAccumulator> accumulators,
            int index,
            string header,
            PartMaintenanceKind kind,
            DateTime date,
            string sourceName)
        {
            if (accumulators.TryGetValue(index, out var accumulator))
                return accumulator;

            var componentName = (header ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(componentName))
                componentName = "未命名零件" + (index + 1).ToString(CultureInfo.InvariantCulture);

            accumulator = new ComponentAccumulator(date, kind, componentName, sourceName);
            accumulators[index] = accumulator;
            return accumulator;
        }

        private static string BuildFileSourceName(string fileName)
        {
            var name = Path.GetFileNameWithoutExtension(fileName) ?? string.Empty;
            if (ContainsAny(name, "原位"))
                return "原位运动";
            if (ContainsAny(name, "动位"))
                return "动位运动";
            if (ContainsAny(name, "真空吸", "真空"))
                return "真空吸";

            return name;
        }

        private static char DetectSeparator(string line)
        {
            if (string.IsNullOrEmpty(line))
                return ',';

            var candidates = new[] { ',', ';', '\t' };
            return candidates.OrderByDescending(c => line.Count(x => x == c)).First();
        }

        private static List<string> SplitCsvLine(string line)
        {
            return SplitCsvLine(line, DetectSeparator(line));
        }

        private static List<string> SplitCsvLine(string line, char separator)
        {
            var result = new List<string>();
            if (line == null)
                return result;

            var current = new System.Text.StringBuilder();
            var inQuotes = false;
            for (var i = 0; i < line.Length; i++)
            {
                var ch = line[i];
                if (ch == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                    continue;
                }

                if (ch == separator && !inQuotes)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(ch);
                }
            }

            result.Add(current.ToString());
            return result;
        }

        private static bool ShouldSkipCell(string header, string cell)
        {
            var text = ((header ?? string.Empty) + " " + (cell ?? string.Empty)).Trim();
            if (ContainsAny(header, "日期", "时间", "time", "date"))
                return true;

            if (DateTime.TryParse(cell, out _))
                return true;

            if (cell.Length == 8 && int.TryParse(cell, out var number) && number >= 20200101 && number <= 20991231)
                return true;

            return string.IsNullOrWhiteSpace(text);
        }

        private static bool IsAbnormalText(string text)
        {
            return ContainsAny(text, "异常", "NG", "NOK", "FAIL", "FALSE", "ERROR", "ERR", "超时", "报警", "ALARM");
        }

        private static bool IsNormalText(string text)
        {
            return ContainsAny(text, "正常", "OK", "PASS", "TRUE");
        }

        private static bool IsAbnormalIndicatorHeader(string header)
        {
            return ContainsAny(header, "异常", "NG", "NOK", "FAIL", "ERROR", "ERR", "报警", "ALARM", "结果", "判定", "状态");
        }

        private static bool TryParseNumber(string text, out double value)
        {
            text = (text ?? string.Empty).Trim().TrimEnd('%');
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                || double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
        }

        private static bool ContainsAny(string text, params string[] keywords)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            foreach (var keyword in keywords)
            {
                if (!string.IsNullOrWhiteSpace(keyword)
                    && text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        private sealed class ComponentAccumulator
        {
            private readonly List<double> _values = new List<double>();

            public ComponentAccumulator(DateTime date, PartMaintenanceKind kind, string componentName, string sourceName)
            {
                Date = date;
                Kind = kind;
                ComponentName = componentName;
                SourceName = sourceName;
            }

            public DateTime Date { get; }
            public PartMaintenanceKind Kind { get; }
            public string ComponentName { get; }
            public string SourceName { get; }
            public int SampleCount { get; private set; }
            public int AbnormalCount { get; private set; }

            public void AddValue(double value)
            {
                _values.Add(value);
                SampleCount++;
            }

            public void AddNormal()
            {
                SampleCount++;
            }

            public void AddAbnormal()
            {
                AbnormalCount++;
                SampleCount++;
            }

            public PartMaintenanceComponentFileSummary ToSummary()
            {
                return new PartMaintenanceComponentFileSummary
                {
                    Date = Date,
                    Kind = Kind,
                    ComponentName = ComponentName,
                    SourceName = SourceName,
                    SampleCount = SampleCount,
                    AbnormalCount = AbnormalCount,
                    HasNumericValue = _values.Count > 0,
                    AverageValue = _values.Count == 0 ? 0d : _values.Average(),
                    MaxValue = _values.Count == 0 ? 0d : _values.Max(),
                    LatestValue = _values.Count == 0 ? 0d : _values[_values.Count - 1]
                };
            }
        }
    }
}

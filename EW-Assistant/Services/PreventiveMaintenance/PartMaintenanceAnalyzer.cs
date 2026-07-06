using EW_Assistant.Warnings;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace EW_Assistant.Services.PreventiveMaintenance
{
    public sealed class PartMaintenanceAnalyzer
    {
        private const string FallbackPartCsvPath = @"D:\zhuomian\T66_TCT_寿命监控Log_20260701";
        private const string FileSummaryCachePath = @"D:\DataAI\preventive_maintenance_file_summary_cache.json";
        private const int FileSummaryCacheVersion = 2;
        private const int MaxDateDirectoryProbeDays = 62;
        private static readonly object s_fileSummaryCacheLock = new object();
        private static FileSummaryCache s_fileSummaryCache;

        public PartMaintenanceReport Analyze(string rootPath)
        {
            return Analyze(rootPath, null, null);
        }

        public DateTime? GetLatestDataDate(string rootPath)
        {
            var normalizedRootPath = ResolvePreferredRootPath(rootPath);

            if (!Directory.Exists(normalizedRootPath))
                return null;

            var files = ListCsvFiles(normalizedRootPath);
            DateTime? latest = null;
            foreach (var file in files)
            {
                var date = ParseDateFromFilePath(file).Date;
                if (!latest.HasValue || date > latest.Value)
                    latest = date;
            }

            return latest;
        }

        private static string ResolvePreferredRootPath(string rootPath)
        {
            var normalizedRootPath = (rootPath ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalizedRootPath))
                return FallbackPartCsvPath;

            if (IsDriveRootPath(normalizedRootPath) && Directory.Exists(FallbackPartCsvPath))
                return FallbackPartCsvPath;

            return normalizedRootPath;
        }

        private static bool IsDriveRootPath(string path)
        {
            try
            {
                var full = Path.GetFullPath(path ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var root = Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return !string.IsNullOrWhiteSpace(root)
                       && string.Equals(full, root, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public PartMaintenanceReport Analyze(string rootPath, DateTime? startDate, DateTime? endDate)
        {
            return Analyze(rootPath, startDate, endDate, CancellationToken.None);
        }

        public PartMaintenanceReport Analyze(string rootPath, DateTime? startDate, DateTime? endDate, CancellationToken cancellationToken)
        {
            var hasDateFilter = HasDateFilter(startDate, endDate);
            cancellationToken.ThrowIfCancellationRequested();
            var report = new PartMaintenanceReport
            {
                RootPath = ResolvePreferredRootPath(rootPath)
            };

            if (!Directory.Exists(report.RootPath))
            {
                var configuredPath = report.RootPath;
                if (!TryUseFallbackPath(report))
                {
                    report.StatusMessage = "零件 CSV 文件夹不存在：" + configuredPath + "；测试目录也不存在：" + FallbackPartCsvPath;
                    BuildRisks(report);
                    return report;
                }
            }

            var files = ListCsvFiles(report.RootPath, startDate, endDate);
            if (files.Count == 0 && TryUseFallbackPath(report))
            {
                files = ListCsvFiles(report.RootPath, startDate, endDate);
            }

            report.FileCount = files.Count;

            if (files.Count == 0)
            {
                report.StatusMessage = HasDateFilter(startDate, endDate)
                    ? "当前筛选范围没有 CSV 文件。" + BuildRangeStatusSuffix(report.RootPath, startDate, endDate)
                    : "当前文件夹没有 CSV 文件。路径：" + report.RootPath;
                BuildRisks(report);
                return report;
            }

            var summaries = GetSummariesForFiles(files, cancellationToken);

            if (summaries.Count == 0)
            {
                if (!hasDateFilter
                    && TryUseFallbackPath(report))
                {
                    files = ListCsvFiles(report.RootPath);
                    report.FileCount = files.Count;
                    summaries = GetSummariesForFiles(files, cancellationToken);
                }

                if (summaries.Count == 0)
                {
                    report.StatusMessage = files.Count == 0
                        ? "当前文件夹没有 CSV 文件。路径：" + report.RootPath
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

        private static string BuildRangeStatusSuffix(string rootPath, DateTime? startDate, DateTime? endDate)
        {
            if (!HasDateFilter(startDate, endDate))
                return "路径：" + rootPath;

            GetNormalizedDateRange(startDate, endDate, out var start, out var end);
            return "路径：" + rootPath + "；范围：" + start.ToString("yyyy-MM-dd") + " ~ " + end.ToString("yyyy-MM-dd");
        }

        private static List<PartMaintenanceFileSummary> GetSummariesForFiles(IList<string> files, CancellationToken cancellationToken)
        {
            var summaries = new List<PartMaintenanceFileSummary>();
            if (files == null || files.Count == 0)
                return summaries;

            var cacheChanged = false;
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PartMaintenanceFileSummary cachedSummary;
                lock (s_fileSummaryCacheLock)
                {
                    if (GetFileSummaryCacheUnsafe().TryGet(file, out cachedSummary))
                    {
                        summaries.Add(cachedSummary);
                        continue;
                    }
                }

                if (!TryReadFile(file, cancellationToken, out var summary))
                    continue;

                summaries.Add(summary);
                lock (s_fileSummaryCacheLock)
                {
                    GetFileSummaryCacheUnsafe().Upsert(file, summary);
                    cacheChanged = true;
                }
            }

            if (cacheChanged)
            {
                lock (s_fileSummaryCacheLock)
                {
                    GetFileSummaryCacheUnsafe().Save();
                }
            }

            return summaries;
        }

        private static FileSummaryCache GetFileSummaryCacheUnsafe()
        {
            if (s_fileSummaryCache == null)
                s_fileSummaryCache = FileSummaryCache.Load(FileSummaryCachePath);

            return s_fileSummaryCache;
        }

        private static bool IsFileInDateRange(string file, DateTime? startDate, DateTime? endDate)
        {
            if (!HasDateFilter(startDate, endDate))
                return true;

            GetNormalizedDateRange(startDate, endDate, out var start, out var end);
            return IsFileInDateRange(file, start, end);
        }

        private static bool IsFileInDateRange(string file, DateTime start, DateTime end)
        {
            var name = Path.GetFileName(file) ?? string.Empty;
            return TryParseDateFromFileName(name, out var date) && date >= start && date <= end;
        }

        private static bool IsNameInDateRange(string name, DateTime? startDate, DateTime? endDate)
        {
            if (!HasDateFilter(startDate, endDate))
                return true;

            GetNormalizedDateRange(startDate, endDate, out var start, out var end);
            return TryParseDateFromFileName(name, out var date) && date >= start && date <= end;
        }

        private static void GetNormalizedDateRange(DateTime? startDate, DateTime? endDate, out DateTime start, out DateTime end)
        {
            start = startDate.HasValue ? startDate.Value.Date : DateTime.MinValue.Date;
            end = endDate.HasValue ? endDate.Value.Date : DateTime.MaxValue.Date;
            if (start <= end)
                return;

            var temp = start;
            start = end;
            end = temp;
        }

        private static bool HasDateFilter(DateTime? startDate, DateTime? endDate)
        {
            return startDate.HasValue || endDate.HasValue;
        }

        private static List<string> ListCsvFiles(string rootPath)
        {
            return ListCsvFiles(rootPath, null, null);
        }

        private static List<string> ListCsvFiles(string rootPath, DateTime? startDate, DateTime? endDate)
        {
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
                return new List<string>();

            var hasDateFilter = HasDateFilter(startDate, endDate);
            if (hasDateFilter)
                return ListCsvFilesInDateRange(rootPath, startDate, endDate);

            var topLevelFiles = SafeEnumerateFiles(rootPath, "*.csv")
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (topLevelFiles.Count > 0)
                return topLevelFiles;

            return SafeEnumerateDirectories(rootPath)
                .SelectMany(x => SafeEnumerateFiles(x, "*.csv"))
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<string> ListCsvFilesInDateRange(string rootPath, DateTime? startDate, DateTime? endDate)
        {
            GetNormalizedDateRange(startDate, endDate, out var start, out var end);

            var topLevelFiles = SafeEnumerateFiles(rootPath, "*.csv")
                .Where(file => IsFileInDateRange(file, start, end))
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (topLevelFiles.Count > 0)
                return topLevelFiles;

            var childLevelFiles = SafeEnumerateDirectories(rootPath)
                .SelectMany(x => SafeEnumerateFiles(x, "*.csv"))
                .Where(file => IsFileInDateRange(file, start, end))
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (childLevelFiles.Count > 0)
                return childLevelFiles;

            var datedDirectories = SafeEnumerateDateDirectories(rootPath, start, end)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (datedDirectories.Count == 0)
                return new List<string>();

            return datedDirectories
                .SelectMany(x => SafeEnumerateFiles(x, "*.csv"))
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static IEnumerable<string> SafeEnumerateDateDirectories(string rootPath, DateTime start, DateTime end)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dayCount = (end.Date - start.Date).TotalDays + 1d;
            if (dayCount > 0d && dayCount <= MaxDateDirectoryProbeDays)
            {
                foreach (var pattern in BuildDateDirectorySearchPatterns(start, end))
                {
                    foreach (var dir in SafeEnumerateDirectories(rootPath, pattern))
                    {
                        result.Add(dir);
                    }
                }

                return result;
            }

            foreach (var dir in SafeEnumerateDirectories(rootPath))
            {
                if (IsNameInDateRange(Path.GetFileName(dir), start, end))
                {
                    result.Add(dir);
                }
            }

            return result;
        }

        private static IEnumerable<string> BuildDateDirectorySearchPatterns(DateTime start, DateTime end)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var date = start.Date; date <= end.Date; date = date.AddDays(1))
            {
                foreach (var token in new[]
                {
                    date.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
                    date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    date.ToString("yyyy_MM_dd", CultureInfo.InvariantCulture),
                    date.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture),
                    date.ToString("yyyy年MM月dd日", CultureInfo.InvariantCulture)
                })
                {
                    var pattern = "*" + token + "*";
                    if (seen.Add(pattern))
                        yield return pattern;
                }
            }
        }

        private static bool IsDateInRange(DateTime date, DateTime start, DateTime end)
        {
            var value = date.Date;
            return value >= start.Date && value <= end.Date;
        }

        private static string GetLastPathSegment(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            return Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? string.Empty;
        }

        private static IEnumerable<string> SafeEnumerateFiles(string rootPath, string pattern)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
                    return Enumerable.Empty<string>();

                return Directory.EnumerateFiles(rootPath, pattern, SearchOption.TopDirectoryOnly).ToList();
            }
            catch (UnauthorizedAccessException)
            {
                return Enumerable.Empty<string>();
            }
            catch (IOException)
            {
                return Enumerable.Empty<string>();
            }
        }

        private static IEnumerable<string> SafeEnumerateDirectories(string rootPath)
        {
            return SafeEnumerateDirectories(rootPath, "*");
        }

        private static IEnumerable<string> SafeEnumerateDirectories(string rootPath, string pattern)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
                    return Enumerable.Empty<string>();

                return Directory.EnumerateDirectories(rootPath, string.IsNullOrWhiteSpace(pattern) ? "*" : pattern, SearchOption.TopDirectoryOnly).ToList();
            }
            catch (UnauthorizedAccessException)
            {
                return Enumerable.Empty<string>();
            }
            catch (IOException)
            {
                return Enumerable.Empty<string>();
            }
        }

        private static bool TryUseFallbackPath(PartMaintenanceReport report)
        {
            if (report == null)
                return false;

            if (string.Equals(report.RootPath, FallbackPartCsvPath, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!Directory.Exists(FallbackPartCsvPath))
                return false;

            report.RootPath = FallbackPartCsvPath;
            return true;
        }

        private static bool TryReadFile(string file, out PartMaintenanceFileSummary summary)
        {
            return TryReadFile(file, CancellationToken.None, out summary);
        }

        private static bool TryReadFile(string file, CancellationToken cancellationToken, out PartMaintenanceFileSummary summary)
        {
            summary = null;
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file) ?? string.Empty;
            if (!TryClassify(name, out var kind))
                return false;

            var date = ParseDateFromFilePath(file);
            var sourceName = string.Empty;
            var sampleCount = 0;
            var abnormalCount = 0;
            var numericCount = 0;
            var numericSum = 0d;
            var numericMax = 0d;

            try
            {
                using (var reader = CsvEncoding.OpenReader(file))
                {
                    var headerLine = reader.ReadLine();
                    var headers = SplitCsvLine(headerLine);
                    sourceName = BuildFileSourceName(name);
                    var separator = DetectSeparator(headerLine);
                    var componentAccumulators = new Dictionary<int, ComponentAccumulator>();
                    var skipColumns = BuildSkipColumnFlags(headers);
                    var abnormalIndicatorColumns = BuildAbnormalIndicatorColumnFlags(headers);

                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (string.IsNullOrWhiteSpace(line))
                            continue;

                        var cells = SplitCsvLine(line, separator);
                        for (var i = 0; i < cells.Count; i++)
                        {
                            var header = i < headers.Count ? headers[i] : string.Empty;
                            var cell = (cells[i] ?? string.Empty).Trim();
                            if (string.IsNullOrWhiteSpace(cell)
                                || IsSkipColumn(skipColumns, i)
                                || LooksLikeDateCell(cell))
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
                                if (IsAbnormalIndicatorColumn(abnormalIndicatorColumns, i))
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

                                numericSum += value;
                                numericCount++;
                                if (numericCount == 1 || value > numericMax)
                                    numericMax = value;
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
                        HasNumericValue = numericCount > 0,
                        NumericAverage = numericCount == 0 ? 0d : numericSum / numericCount,
                        NumericMax = numericCount == 0 ? 0d : numericMax
                    };
                    summary.Components.AddRange(componentSummaries);
                    return true;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
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
            var groups = source
                .Where(x => !string.IsNullOrWhiteSpace(x.ComponentName))
                .GroupBy(x => x.ComponentName.Trim())
                .Select(x => BuildComponentStatus(x.Key, x.ToList(), kind))
                .OrderByDescending(x => x.RiskScore)
                .ThenBy(x => x.ComponentName, StringComparer.OrdinalIgnoreCase);

            foreach (var status in groups)
            {
                target.Add(status);
            }
        }

        private static PartMaintenanceComponentStatus BuildComponentStatus(string componentName, IList<PartMaintenanceComponentFileSummary> items, PartMaintenanceKind kind)
        {
            var numericItems = items.Where(x => x.HasNumericValue).ToList();
            var sampleCount = items.Sum(x => x.SampleCount);
            var abnormalCount = items.Sum(x => x.AbnormalCount);
            var averageValue = numericItems.Count == 0 ? 0d : numericItems.Average(x => x.AverageValue);
            var maxValue = numericItems.Count == 0 ? 0d : numericItems.Max(x => x.MaxValue);
            var latestItem = items.OrderByDescending(x => x.Date).FirstOrDefault(x => x.HasNumericValue) ?? items.OrderByDescending(x => x.Date).First();

            var homeItems = items.Where(x => IsHomeSource(x.SourceName)).ToList();
            var workItems = items.Where(x => IsWorkSource(x.SourceName)).ToList();
            var score = kind == PartMaintenanceKind.Cylinder
                ? Math.Max(CalculateComponentRiskScore(homeItems, kind), CalculateComponentRiskScore(workItems, kind))
                : CalculateComponentRiskScore(items, kind);

            var status = new PartMaintenanceComponentStatus
            {
                PartType = kind == PartMaintenanceKind.Cylinder ? "气缸" : "真空吸",
                ComponentName = componentName,
                SourceNames = string.Join(" / ", items.Select(x => x.SourceName).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct()),
                SampleCount = sampleCount,
                AbnormalCount = abnormalCount,
                AverageValue = averageValue,
                MaxValue = maxValue,
                LatestValue = latestItem.HasNumericValue ? latestItem.LatestValue : 0d,
                RiskLevel = ToRiskLevel(score),
                RiskScore = score,
                Summary = BuildComponentSummary(componentName, items, score),
                Suggestion = string.Empty
            };

            if (kind == PartMaintenanceKind.Cylinder)
            {
                ApplyDirectionalStatus(status, homeItems, isHome: true);
                ApplyDirectionalStatus(status, workItems, isHome: false);
                status.Summary = componentName + " 原位风险 " + status.HomeRiskLevel + "(" + status.HomeRiskScore + ")，动位风险 " + status.WorkRiskLevel + "(" + status.WorkRiskScore + ")。";
                status.Trend.AddRange(BuildComponentTrend(status.WorkRiskScore >= status.HomeRiskScore ? workItems : homeItems, kind));
            }
            else
            {
                status.Trend.AddRange(BuildComponentTrend(items, kind));
            }

            status.Suggestion = BuildAiStyleSuggestion(status, items, kind);
            return status;
        }

        private static string BuildAiStyleSuggestion(
            PartMaintenanceComponentStatus status,
            IList<PartMaintenanceComponentFileSummary> items,
            PartMaintenanceKind kind)
        {
            if (status == null)
                return string.Empty;

            var latest = items == null
                ? null
                : items.OrderByDescending(x => x.Date).FirstOrDefault(x => x.HasNumericValue)
                  ?? items.OrderByDescending(x => x.Date).FirstOrDefault();

            var trend = BuildTrendJudgement(status.Trend);
            var abnormalText = status.AbnormalCount > 0
                ? "累计发现异常记录 " + status.AbnormalCount + " 条"
                : "暂未发现明确异常记录";

            var sb = new StringBuilder();
            sb.AppendLine("先看结论：" + status.ComponentName + " 目前属于" + status.RiskLevel + "，风险分 " + status.RiskScore + "/100。");

            if (kind == PartMaintenanceKind.Cylinder)
            {
                var focus = status.WorkRiskScore >= status.HomeRiskScore ? "动位" : "原位";
                var focusAverage = status.WorkRiskScore >= status.HomeRiskScore ? status.WorkAverageValue : status.HomeAverageValue;
                var focusMax = status.WorkRiskScore >= status.HomeRiskScore ? status.WorkMaxValue : status.HomeMaxValue;
                var focusLatest = status.WorkRiskScore >= status.HomeRiskScore ? status.WorkLatestValue : status.HomeLatestValue;

                sb.AppendLine("我会优先看" + focus + "侧：原位是 " + status.HomeRiskLevel + "(" + status.HomeRiskScore + ")，动位是 "
                              + status.WorkRiskLevel + "(" + status.WorkRiskScore + ")，目前" + focus + "侧更值得先确认。");
                sb.AppendLine("从数据看，" + focus + "均值 " + FormatMetric(focusAverage)
                              + "，最大 " + FormatMetric(focusMax)
                              + "，最新 " + FormatMetric(focusLatest) + "；" + abnormalText + "，还没到必须停机处理的程度，但建议不要放着不管。");
                sb.AppendLine("处理建议：先看气压有没有波动、节流阀有没有被调过；如果这两项正常，再查电磁阀响应、气缸密封圈、导轨阻力，最后确认"
                              + focus + "传感器位置和线缆接触。");
            }
            else
            {
                sb.AppendLine("从数据看，当前样本 " + status.SampleCount + " 条，均值 " + FormatMetric(status.AverageValue)
                              + "，最大 " + FormatMetric(status.MaxValue)
                              + "，最新 " + FormatMetric(status.LatestValue) + "；" + abnormalText + "。整体看更像是吸附状态在变差，需要先做一次现场确认。");
                sb.AppendLine("处理建议：先看吸嘴端面有没有磨损、堵塞或偏位；如果吸嘴没问题，再查真空管路漏气、过滤器污染、真空发生器供气，最后确认产品接触面和吸附时间。");
            }

            if (latest != null)
            {
                sb.AppendLine("后续观察：最近数据日期是 " + latest.Date.ToString("yyyy-MM-dd") + "，" + trend
                              + "。处理后建议连续看 1-2 个班次，只要最大值和异常次数能回落，就说明方向基本对。");
            }
            else
            {
                sb.AppendLine("后续观察：当前样本还不够，建议先补齐近期 CSV，再判断是不是持续劣化。");
            }

            return sb.ToString().Trim();
        }

        private static string BuildTrendJudgement(IList<PartMaintenanceTrendPoint> trend)
        {
            if (trend == null || trend.Count < 2)
                return "趋势样本偏少，暂按当前风险分处理";

            var latest = trend[trend.Count - 1].Value;
            var previous = trend[trend.Count - 2].Value;
            if (latest > previous * 1.2d && latest - previous > 0.01d)
                return "最近一次较前次上升明显，存在劣化苗头";

            if (latest < previous * 0.85d && previous - latest > 0.01d)
                return "最近一次较前次有所回落，可继续验证稳定性";

            if (trend.Count >= 4)
            {
                var firstHalf = trend.Take(trend.Count / 2).Average(x => x.Value);
                var secondHalf = trend.Skip(trend.Count / 2).Average(x => x.Value);
                if (secondHalf > firstHalf * 1.2d && secondHalf - firstHalf > 0.01d)
                    return "后半段均值高于前半段，建议按轻度劣化处理";
            }

            return "趋势暂未出现明显跳变";
        }

        private static string FormatMetric(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static List<PartMaintenanceTrendPoint> BuildComponentTrend(IList<PartMaintenanceComponentFileSummary> items, PartMaintenanceKind kind)
        {
            var result = new List<PartMaintenanceTrendPoint>();
            if (items == null || items.Count == 0)
                return result;

            foreach (var group in items.GroupBy(x => x.Date.Date).OrderBy(x => x.Key))
            {
                var list = group.ToList();
                var numericItems = list.Where(x => x.HasNumericValue).ToList();
                var hasNumeric = numericItems.Count > 0;
                var sampleCount = list.Sum(x => x.SampleCount);
                var abnormalCount = list.Sum(x => x.AbnormalCount);
                result.Add(new PartMaintenanceTrendPoint
                {
                    Date = group.Key,
                    Kind = kind,
                    FileCount = list.Count,
                    SampleCount = sampleCount,
                    AbnormalCount = abnormalCount,
                    HasNumericValue = hasNumeric,
                    HasAbnormal = abnormalCount > 0,
                    Value = hasNumeric ? numericItems.Average(x => x.AverageValue) : (sampleCount <= 0 ? 0d : (double)abnormalCount / sampleCount * 100d),
                    SourceNames = string.Join(" / ", list.Select(x => x.SourceName).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct())
                });
            }

            return result;
        }

        private static void ApplyDirectionalStatus(PartMaintenanceComponentStatus status, IList<PartMaintenanceComponentFileSummary> items, bool isHome)
        {
            var numericItems = items.Where(x => x.HasNumericValue).ToList();
            var average = numericItems.Count == 0 ? 0d : numericItems.Average(x => x.AverageValue);
            var max = numericItems.Count == 0 ? 0d : numericItems.Max(x => x.MaxValue);
            var latestItem = items.OrderByDescending(x => x.Date).FirstOrDefault(x => x.HasNumericValue);
            var latest = latestItem == null ? 0d : latestItem.LatestValue;
            var score = CalculateComponentRiskScore(items, PartMaintenanceKind.Cylinder);
            var level = ToRiskLevel(score);

            if (isHome)
            {
                status.HomeAverageValue = average;
                status.HomeMaxValue = max;
                status.HomeLatestValue = latest;
                status.HomeRiskScore = score;
                status.HomeRiskLevel = level;
            }
            else
            {
                status.WorkAverageValue = average;
                status.WorkMaxValue = max;
                status.WorkLatestValue = latest;
                status.WorkRiskScore = score;
                status.WorkRiskLevel = level;
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

        private static string ToRiskLevel(int score)
        {
            if (score >= 70) return "高风险";
            if (score >= 40) return "中风险";
            if (score > 0) return "低风险";
            return "正常";
        }

        private static bool IsHomeSource(string sourceName)
        {
            return ContainsAny(sourceName, "原位");
        }

        private static bool IsWorkSource(string sourceName)
        {
            return ContainsAny(sourceName, "动位");
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

        private static DateTime ParseDateFromFilePath(string file)
        {
            if (TryParseDateFromFileName(Path.GetFileName(file), out var fileDate))
                return fileDate;

            if (TryParseDateFromFileName(GetLastPathSegment(Path.GetDirectoryName(file)), out var directoryDate))
                return directoryDate;

            return DateTime.Today;
        }

        private static bool TryParseDateFromFileName(string fileName, out DateTime date)
        {
            var digits = new string((fileName ?? string.Empty).TakeWhile(char.IsDigit).ToArray());
            if (digits.Length >= 8
                && DateTime.TryParseExact(digits.Substring(0, 8), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var compact))
            {
                date = compact.Date;
                return true;
            }

            if (!string.IsNullOrWhiteSpace(fileName))
            {
                var formats = new[] { "yyyy-MM-dd", "yyyy_MM_dd", "yyyy.MM.dd" };
                for (var i = 0; i <= fileName.Length - 10; i++)
                {
                    var candidate = fileName.Substring(i, 10);
                    foreach (var format in formats)
                    {
                        if (DateTime.TryParseExact(candidate, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                        {
                            date = parsed.Date;
                            return true;
                        }
                    }
                }

                for (var i = 0; i <= fileName.Length - 11; i++)
                {
                    var candidate = fileName.Substring(i, 11);
                    if (DateTime.TryParseExact(candidate, "yyyy年MM月dd日", CultureInfo.InvariantCulture, DateTimeStyles.None, out var chineseDate))
                    {
                        date = chineseDate.Date;
                        return true;
                    }
                }

                for (var i = 0; i <= fileName.Length - 8; i++)
                {
                    var candidate = fileName.Substring(i, 8);
                    if (DateTime.TryParseExact(candidate, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var compactInside))
                    {
                        date = compactInside.Date;
                        return true;
                    }
                }
            }

            date = default(DateTime);
            return false;
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

        private static List<bool> BuildSkipColumnFlags(IList<string> headers)
        {
            var result = new List<bool>();
            if (headers == null)
                return result;

            for (var i = 0; i < headers.Count; i++)
            {
                result.Add(ContainsAny(headers[i], "日期", "时间", "time", "date"));
            }

            return result;
        }

        private static List<bool> BuildAbnormalIndicatorColumnFlags(IList<string> headers)
        {
            var result = new List<bool>();
            if (headers == null)
                return result;

            for (var i = 0; i < headers.Count; i++)
            {
                result.Add(IsAbnormalIndicatorHeader(headers[i]));
            }

            return result;
        }

        private static bool IsSkipColumn(IList<bool> skipColumns, int index)
        {
            return skipColumns != null && index >= 0 && index < skipColumns.Count && skipColumns[index];
        }

        private static bool IsAbnormalIndicatorColumn(IList<bool> abnormalIndicatorColumns, int index)
        {
            return abnormalIndicatorColumns != null && index >= 0 && index < abnormalIndicatorColumns.Count && abnormalIndicatorColumns[index];
        }

        private static bool LooksLikeDateCell(string cell)
        {
            if (string.IsNullOrWhiteSpace(cell))
                return false;

            cell = cell.Trim();
            if (cell.Length == 8 && int.TryParse(cell, out var compact) && compact >= 20200101 && compact <= 20991231)
                return true;

            if (cell.Length >= 10)
            {
                var candidate = cell.Length > 10 ? cell.Substring(0, 10) : cell;
                return DateTime.TryParseExact(candidate, new[] { "yyyy-MM-dd", "yyyy_MM_dd", "yyyy.MM.dd" },
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
            }

            return false;
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

        private sealed class FileSummaryCache
        {
            private readonly string _filePath;
            private readonly Dictionary<string, FileSummaryCacheEntry> _entries =
                new Dictionary<string, FileSummaryCacheEntry>(StringComparer.OrdinalIgnoreCase);

            private FileSummaryCache(string filePath)
            {
                _filePath = filePath;
            }

            public static FileSummaryCache Load(string filePath)
            {
                var cache = new FileSummaryCache(filePath);
                try
                {
                    if (!File.Exists(filePath))
                        return cache;

                    var json = File.ReadAllText(filePath, Encoding.UTF8);
                    if (string.IsNullOrWhiteSpace(json))
                        return cache;

                    var entries = JsonConvert.DeserializeObject<List<FileSummaryCacheEntry>>(json);
                    if (entries == null)
                        return cache;

                    foreach (var entry in entries)
                    {
                        if (entry == null || string.IsNullOrWhiteSpace(entry.FilePath) || entry.Summary == null)
                            continue;

                        cache._entries[entry.FilePath] = entry;
                    }
                }
                catch
                {
                    // 缓存读取失败不影响主流程，直接重新分析原始 CSV。
                }

                return cache;
            }

            public bool TryGet(string file, out PartMaintenanceFileSummary summary)
            {
                summary = null;
                try
                {
                    var key = NormalizePath(file);
                    if (!_entries.TryGetValue(key, out var entry))
                        return false;

                    var info = new FileInfo(file);
                    if (!info.Exists
                        || entry.Version != FileSummaryCacheVersion
                        || entry.Length != info.Length
                        || entry.LastWriteUtcTicks != info.LastWriteTimeUtc.Ticks
                        || entry.Summary == null)
                    {
                        return false;
                    }

                    summary = entry.Summary;
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            public void Upsert(string file, PartMaintenanceFileSummary summary)
            {
                if (summary == null)
                    return;

                try
                {
                    var info = new FileInfo(file);
                    if (!info.Exists)
                        return;

                    var key = NormalizePath(file);
                    _entries[key] = new FileSummaryCacheEntry
                    {
                        Version = FileSummaryCacheVersion,
                        FilePath = key,
                        Length = info.Length,
                        LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
                        Summary = summary
                    };
                }
                catch
                {
                    // 单文件缓存失败不影响主流程。
                }
            }

            public void Save()
            {
                try
                {
                    var dir = Path.GetDirectoryName(_filePath);
                    if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    var entries = _entries.Values
                        .Where(x => x != null && !string.IsNullOrWhiteSpace(x.FilePath) && x.Summary != null)
                        .Where(x => File.Exists(x.FilePath))
                        .OrderBy(x => x.FilePath, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    var json = JsonConvert.SerializeObject(entries, Formatting.Indented);
                    var temp = _filePath + ".tmp";
                    File.WriteAllText(temp, json, Encoding.UTF8);
                    File.Copy(temp, _filePath, true);
                    File.Delete(temp);
                }
                catch
                {
                    // 缓存写入失败不影响主流程。
                }
            }

            private static string NormalizePath(string file)
            {
                return Path.GetFullPath(file ?? string.Empty);
            }
        }

        private sealed class FileSummaryCacheEntry
        {
            public int Version { get; set; }
            public string FilePath { get; set; }
            public long Length { get; set; }
            public long LastWriteUtcTicks { get; set; }
            public PartMaintenanceFileSummary Summary { get; set; }
        }

        private sealed class ComponentAccumulator
        {
            private int _valueCount;
            private double _valueSum;
            private double _valueMax;
            private double _latestValue;

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
                _valueSum += value;
                _valueCount++;
                _latestValue = value;
                if (_valueCount == 1 || value > _valueMax)
                    _valueMax = value;
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
                    HasNumericValue = _valueCount > 0,
                    AverageValue = _valueCount == 0 ? 0d : _valueSum / _valueCount,
                    MaxValue = _valueCount == 0 ? 0d : _valueMax,
                    LatestValue = _valueCount == 0 ? 0d : _latestValue
                };
            }
        }
    }
}

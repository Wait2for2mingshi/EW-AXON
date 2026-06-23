using System;
using System.Collections.Generic;
using System.Linq;
using EW_Assistant.Domain.MachineStates;

namespace EW_Assistant.Services.MachineStates
{
    /// <summary>
    /// 将 MACHINESTATE 状态片段聚合为 AI 与页面共用的数据快照。
    /// </summary>
    public class MachineStateAnalyzer
    {
        public MachineStateAnalysisData Analyze(MachineStateReadResult readResult)
        {
            if (readResult == null)
            {
                throw new ArgumentNullException("readResult");
            }

            var records = (readResult.Records ?? new List<MachineStateRecord>())
                .Where(r => r != null && r.DurationSeconds > 0d)
                .ToList();

            var data = new MachineStateAnalysisData
            {
                SourceFileName = readResult.SourceFileName,
                SourceFilePath = readResult.SourceFilePath,
                SourceHash = readResult.SourceHash,
                GeneratedAt = DateTime.Now,
                RowCount = readResult.RowCount,
                ParsedRowCount = records.Count,
                SkippedRowCount = Math.Max(0, readResult.RowCount - records.Count),
                DeviceCount = records.Select(r => NormalizeName(r.Entity)).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                StationCount = records.Select(r => NormalizeName(r.Station)).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                LineCount = records.Select(r => NormalizeName(r.Line)).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            };

            foreach (var warning in readResult.Warnings ?? new List<string>())
            {
                data.Warnings.Add(warning);
            }

            if (records.Count == 0)
            {
                data.Warnings.Add("没有可用于分析的有效状态片段。");
                return data;
            }

            var startTimes = records.Where(r => r.StartTime.HasValue).Select(r => r.StartTime.Value).ToList();
            var endTimes = records.Select(GetEffectiveEndTime).Where(t => t.HasValue).Select(t => t.Value).ToList();
            data.StartTime = startTimes.Count > 0 ? (DateTime?)startTimes.Min() : null;
            data.EndTime = endTimes.Count > 0 ? (DateTime?)endTimes.Max() : null;

            var stateBuckets = new Dictionary<string, StateBucket>(StringComparer.OrdinalIgnoreCase);
            var hourlyBuckets = new Dictionary<DateTime, MachineStateHourStat>();
            var errorBuckets = new Dictionary<string, ErrorBucket>(StringComparer.OrdinalIgnoreCase);
            var entityBuckets = new Dictionary<string, TargetBucket>(StringComparer.OrdinalIgnoreCase);
            var stationBuckets = new Dictionary<string, TargetBucket>(StringComparer.OrdinalIgnoreCase);
            var lineBuckets = new Dictionary<string, TargetBucket>(StringComparer.OrdinalIgnoreCase);

            foreach (var record in records)
            {
                var category = Classify(record);
                var duration = Math.Max(0d, record.DurationSeconds);
                data.TotalObservedSeconds += duration;
                AddCategoryDuration(data, category, duration);

                var stateKey = BuildStateKey(record.StateCode, category);
                StateBucket stateBucket;
                if (!stateBuckets.TryGetValue(stateKey, out stateBucket))
                {
                    stateBucket = new StateBucket
                    {
                        StateCode = string.IsNullOrWhiteSpace(record.StateCode) ? "UNKNOWN" : record.StateCode,
                        StateName = ResolveStateName(record.StateCode),
                        Category = ResolveCategoryName(category)
                    };
                    stateBuckets.Add(stateKey, stateBucket);
                }

                stateBucket.Count += 1;
                stateBucket.DurationSeconds += duration;

                AddHourlyDuration(hourlyBuckets, record, category, duration);
                AddError(errorBuckets, record, category, duration);
                AddTarget(entityBuckets, NormalizeName(record.Entity), category, duration);
                AddTarget(stationBuckets, NormalizeName(record.Station), category, duration);
                AddTarget(lineBuckets, NormalizeName(record.Line), category, duration);
            }

            ApplyRates(data);
            data.StateSummaries = stateBuckets.Values
                .Select(b => new MachineStateStateSummary
                {
                    StateCode = b.StateCode,
                    StateName = b.StateName,
                    Category = b.Category,
                    Count = b.Count,
                    DurationSeconds = b.DurationSeconds,
                    Rate = SafeRate(b.DurationSeconds, data.TotalObservedSeconds)
                })
                .OrderByDescending(s => s.DurationSeconds)
                .ThenBy(s => s.StateCode)
                .ToList();

            data.Hourly = hourlyBuckets.Values.OrderBy(h => h.Hour).ToList();
            data.TopErrors = errorBuckets.Values
                .OrderByDescending(e => e.DurationSeconds)
                .ThenByDescending(e => e.Count)
                .Take(12)
                .Select(e => new MachineStateErrorSummary
                {
                    ErrorMessage = e.ErrorMessage,
                    ErrorCode = e.ErrorCode,
                    Category = ResolveCategoryName(e.Category),
                    Count = e.Count,
                    DurationSeconds = e.DurationSeconds,
                    Rate = SafeRate(e.DurationSeconds, data.TotalObservedSeconds)
                })
                .ToList();

            data.TopEntities = BuildTargetSummaries(entityBuckets);
            data.TopStations = BuildTargetSummaries(stationBuckets);
            data.TopLines = BuildTargetSummaries(lineBuckets);

            if (data.Hourly.Count == 0)
            {
                data.Warnings.Add("有效记录缺少 start_time，无法生成小时趋势。");
            }

            return data;
        }

        private static void AddHourlyDuration(IDictionary<DateTime, MachineStateHourStat> buckets, MachineStateRecord record, MachineStateCategory category, double duration)
        {
            if (!record.StartTime.HasValue || duration <= 0d)
            {
                return;
            }

            var cursor = record.StartTime.Value;
            var end = cursor.AddSeconds(duration);
            var guard = 0;

            while (cursor < end && guard < 10000)
            {
                guard++;
                var hour = new DateTime(cursor.Year, cursor.Month, cursor.Day, cursor.Hour, 0, 0, cursor.Kind);
                var next = hour.AddHours(1);
                var segmentEnd = end < next ? end : next;
                var seconds = Math.Max(0d, (segmentEnd - cursor).TotalSeconds);
                if (seconds <= 0d)
                {
                    break;
                }

                MachineStateHourStat stat;
                if (!buckets.TryGetValue(hour, out stat))
                {
                    stat = new MachineStateHourStat { Hour = hour };
                    buckets.Add(hour, stat);
                }

                AddCategoryDuration(stat, category, seconds);
                stat.TotalSeconds += seconds;
                cursor = segmentEnd;
            }
        }

        private static void AddError(IDictionary<string, ErrorBucket> buckets, MachineStateRecord record, MachineStateCategory category, double duration)
        {
            var message = NormalizeName(record.ErrorMessage);
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            var code = NormalizeName(record.ErrorCode);
            var key = (code ?? string.Empty) + "|" + message + "|" + category;
            ErrorBucket bucket;
            if (!buckets.TryGetValue(key, out bucket))
            {
                bucket = new ErrorBucket
                {
                    ErrorCode = code,
                    ErrorMessage = message,
                    Category = category
                };
                buckets.Add(key, bucket);
            }

            bucket.Count += 1;
            bucket.DurationSeconds += duration;
        }

        private static void AddTarget(IDictionary<string, TargetBucket> buckets, string name, MachineStateCategory category, double duration)
        {
            if (string.IsNullOrWhiteSpace(name) || !IsDowntimeCategory(category))
            {
                return;
            }

            TargetBucket bucket;
            if (!buckets.TryGetValue(name, out bucket))
            {
                bucket = new TargetBucket { Name = name };
                buckets.Add(name, bucket);
            }

            bucket.Count += 1;
            bucket.DowntimeSeconds += duration;
            if (category == MachineStateCategory.Abnormal)
            {
                bucket.AbnormalSeconds += duration;
            }
            else if (category == MachineStateCategory.PlannedDowntime)
            {
                bucket.PlannedDowntimeSeconds += duration;
            }
            else if (category == MachineStateCategory.Maintenance)
            {
                bucket.MaintenanceSeconds += duration;
            }
        }

        private static IList<MachineStateTargetSummary> BuildTargetSummaries(IDictionary<string, TargetBucket> buckets)
        {
            return buckets.Values
                .OrderByDescending(t => t.DowntimeSeconds)
                .ThenByDescending(t => t.Count)
                .Take(12)
                .Select(t => new MachineStateTargetSummary
                {
                    Name = t.Name,
                    Count = t.Count,
                    DowntimeSeconds = t.DowntimeSeconds,
                    AbnormalSeconds = t.AbnormalSeconds,
                    PlannedDowntimeSeconds = t.PlannedDowntimeSeconds,
                    MaintenanceSeconds = t.MaintenanceSeconds
                })
                .ToList();
        }

        private static DateTime? GetEffectiveEndTime(MachineStateRecord record)
        {
            if (record == null)
            {
                return null;
            }

            if (record.StartTime.HasValue && record.DurationSeconds > 0d)
            {
                return record.StartTime.Value.AddSeconds(record.DurationSeconds);
            }

            return record.EndTime;
        }

        private static MachineStateCategory Classify(MachineStateRecord record)
        {
            if (IsPlannedDowntime(record))
            {
                return MachineStateCategory.PlannedDowntime;
            }

            var code = !string.IsNullOrWhiteSpace(record.StateCode) ? record.StateCode.Trim() : (record.MachineState ?? string.Empty).Trim();
            switch (code)
            {
                case "1":
                    return MachineStateCategory.Running;
                case "2":
                    return MachineStateCategory.Idle;
                case "4":
                    return MachineStateCategory.Maintenance;
                case "5":
                    return MachineStateCategory.Abnormal;
                default:
                    return MachineStateCategory.Unknown;
            }
        }

        private static bool IsPlannedDowntime(MachineStateRecord record)
        {
            var message = record == null ? null : record.ErrorMessage;
            return !string.IsNullOrWhiteSpace(message) &&
                   message.IndexOf("plan downtime", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ResolveStateName(string code)
        {
            switch ((code ?? string.Empty).Trim())
            {
                case "1":
                    return "正常运行";
                case "2":
                    return "空闲";
                case "4":
                    return "日常维护";
                case "5":
                    return "LINEDOWN-报警";
                default:
                    return "未知状态";
            }
        }

        private static string ResolveCategoryName(MachineStateCategory category)
        {
            switch (category)
            {
                case MachineStateCategory.Running:
                    return "正常运行";
                case MachineStateCategory.Idle:
                    return "空闲";
                case MachineStateCategory.Abnormal:
                    return "LINEDOWN-报警";
                case MachineStateCategory.PlannedDowntime:
                    return "计划停机";
                case MachineStateCategory.Maintenance:
                    return "日常维护";
                default:
                    return "未知";
            }
        }

        private static string BuildStateKey(string stateCode, MachineStateCategory category)
        {
            return (string.IsNullOrWhiteSpace(stateCode) ? "UNKNOWN" : stateCode.Trim()) + "|" + category;
        }

        private static bool IsDowntimeCategory(MachineStateCategory category)
        {
            return category == MachineStateCategory.Abnormal ||
                   category == MachineStateCategory.PlannedDowntime ||
                   category == MachineStateCategory.Maintenance;
        }

        private static void AddCategoryDuration(MachineStateAnalysisData data, MachineStateCategory category, double seconds)
        {
            switch (category)
            {
                case MachineStateCategory.Running:
                    data.RunningSeconds += seconds;
                    break;
                case MachineStateCategory.Idle:
                    data.IdleSeconds += seconds;
                    break;
                case MachineStateCategory.Abnormal:
                    data.AbnormalSeconds += seconds;
                    break;
                case MachineStateCategory.PlannedDowntime:
                    data.PlannedDowntimeSeconds += seconds;
                    break;
                case MachineStateCategory.Maintenance:
                    data.MaintenanceSeconds += seconds;
                    break;
                default:
                    data.UnknownSeconds += seconds;
                    break;
            }
        }

        private static void AddCategoryDuration(MachineStateHourStat data, MachineStateCategory category, double seconds)
        {
            switch (category)
            {
                case MachineStateCategory.Running:
                    data.RunningSeconds += seconds;
                    break;
                case MachineStateCategory.Idle:
                    data.IdleSeconds += seconds;
                    break;
                case MachineStateCategory.Abnormal:
                    data.AbnormalSeconds += seconds;
                    break;
                case MachineStateCategory.PlannedDowntime:
                    data.PlannedDowntimeSeconds += seconds;
                    break;
                case MachineStateCategory.Maintenance:
                    data.MaintenanceSeconds += seconds;
                    break;
                default:
                    data.UnknownSeconds += seconds;
                    break;
            }
        }

        private static void ApplyRates(MachineStateAnalysisData data)
        {
            data.RunningRate = SafeRate(data.RunningSeconds, data.TotalObservedSeconds);
            data.IdleRate = SafeRate(data.IdleSeconds, data.TotalObservedSeconds);
            data.AbnormalRate = SafeRate(data.AbnormalSeconds, data.TotalObservedSeconds);
            data.PlannedDowntimeRate = SafeRate(data.PlannedDowntimeSeconds, data.TotalObservedSeconds);
            data.MaintenanceRate = SafeRate(data.MaintenanceSeconds, data.TotalObservedSeconds);
            data.UnknownRate = SafeRate(data.UnknownSeconds, data.TotalObservedSeconds);
        }

        private static double SafeRate(double value, double total)
        {
            return total > 0d ? value / total : 0d;
        }

        private static string NormalizeName(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private enum MachineStateCategory
        {
            Running,
            Idle,
            Abnormal,
            PlannedDowntime,
            Maintenance,
            Unknown
        }

        private sealed class StateBucket
        {
            public string StateCode { get; set; }
            public string StateName { get; set; }
            public string Category { get; set; }
            public int Count { get; set; }
            public double DurationSeconds { get; set; }
        }

        private sealed class ErrorBucket
        {
            public string ErrorMessage { get; set; }
            public string ErrorCode { get; set; }
            public MachineStateCategory Category { get; set; }
            public int Count { get; set; }
            public double DurationSeconds { get; set; }
        }

        private sealed class TargetBucket
        {
            public string Name { get; set; }
            public int Count { get; set; }
            public double DowntimeSeconds { get; set; }
            public double AbnormalSeconds { get; set; }
            public double PlannedDowntimeSeconds { get; set; }
            public double MaintenanceSeconds { get; set; }
        }
    }
}

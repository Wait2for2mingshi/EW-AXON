using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using EW_Assistant.Domain.MachineStates;
using EW_Assistant.Domain.Reports;
using EW_Assistant.Services.MachineStates;
using EW_Assistant.Warnings;

namespace EW_Assistant.Services.Reports
{
    /// <summary>
    /// 报表侧状态记录表读取器，按日期读取 yyyy-MM-dd-状态记录表.csv 并转为轻量摘要。
    /// </summary>
    public class MachineStateReportReader
    {
        private readonly string _root;
        private readonly MachineStateCsvReader _reader;
        private readonly MachineStateAnalyzer _analyzer;

        public MachineStateReportReader(string root = null, MachineStateCsvReader reader = null, MachineStateAnalyzer analyzer = null)
        {
            _root = string.IsNullOrWhiteSpace(root) ? LocalDataConfig.MachineStateCsvRoot : root;
            _reader = reader ?? new MachineStateCsvReader();
            _analyzer = analyzer ?? new MachineStateAnalyzer();
        }

        public ReportMachineStateSummary ReadRange(DateTime start, DateTime end)
        {
            var summary = new ReportMachineStateSummary
            {
                SourceRoot = _root
            };

            if (string.IsNullOrWhiteSpace(_root))
            {
                return summary;
            }

            if (end <= start)
            {
                summary.Warnings.Add("状态记录表读取时间范围无效。");
                return summary;
            }

            var files = EnumerateFiles(start, end).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (files.Count == 0)
            {
                ApplyRates(summary);
                return summary;
            }

            var topAlarms = new Dictionary<string, ReportMachineStateAlarmStat>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                try
                {
                    var read = _reader.Read(file);
                    var clippedRead = ClipReadResult(read, start, end);
                    var data = _analyzer.Analyze(clippedRead);
                    Merge(summary, data, start, end, topAlarms);
                }
                catch (Exception ex)
                {
                    summary.Warnings.Add(Path.GetFileName(file) + " 读取失败：" + ex.Message);
                }
            }

            summary.TopLineDownAlarms = topAlarms.Values
                .OrderByDescending(x => x.DurationSeconds)
                .ThenByDescending(x => x.Count)
                .Take(8)
                .ToList();
            summary.Hours = summary.Hours.OrderBy(h => h.Hour).ToList();
            summary.Days = summary.Days.OrderBy(d => d.Date).ToList();
            ApplyRates(summary);
            return summary;
        }

        private static MachineStateReadResult ClipReadResult(MachineStateReadResult source, DateTime start, DateTime end)
        {
            var result = new MachineStateReadResult();
            if (source == null)
            {
                return result;
            }

            result.SourceFilePath = source.SourceFilePath;
            result.SourceFileName = source.SourceFileName;
            result.SourceHash = source.SourceHash;
            result.RowCount = source.RowCount;

            foreach (var warning in source.Warnings ?? new List<string>())
            {
                result.Warnings.Add(warning);
            }

            foreach (var record in source.Records ?? new List<MachineStateRecord>())
            {
                var clipped = ClipRecord(record, start, end);
                if (clipped != null)
                {
                    result.Records.Add(clipped);
                }
            }

            return result;
        }

        private static MachineStateRecord ClipRecord(MachineStateRecord record, DateTime start, DateTime end)
        {
            if (record == null || record.DurationSeconds <= 0d)
            {
                return null;
            }

            DateTime? recordStart = record.StartTime;
            DateTime? recordEnd = record.EndTime;
            if (!recordEnd.HasValue && recordStart.HasValue)
            {
                recordEnd = recordStart.Value.AddSeconds(record.DurationSeconds);
            }
            if (!recordStart.HasValue && recordEnd.HasValue)
            {
                recordStart = recordEnd.Value.AddSeconds(-record.DurationSeconds);
            }
            if (!recordStart.HasValue || !recordEnd.HasValue)
            {
                return null;
            }

            var clippedStart = recordStart.Value < start ? start : recordStart.Value;
            var clippedEnd = recordEnd.Value > end ? end : recordEnd.Value;
            if (clippedEnd <= clippedStart)
            {
                return null;
            }

            return new MachineStateRecord
            {
                RowNumber = record.RowNumber,
                StartTime = clippedStart,
                EndTime = clippedEnd,
                DurationSeconds = (clippedEnd - clippedStart).TotalSeconds,
                StateCode = record.StateCode,
                MachineState = record.MachineState,
                Entity = record.Entity,
                Station = record.Station,
                Line = record.Line,
                Rfid = record.Rfid,
                ErrorCode = record.ErrorCode,
                ErrorMessage = record.ErrorMessage,
                ErrorDetail = record.ErrorDetail
            };
        }

        private IEnumerable<string> EnumerateFiles(DateTime start, DateTime end)
        {
            if (string.IsNullOrWhiteSpace(_root))
            {
                yield break;
            }

            if (File.Exists(_root))
            {
                if (MatchesRange(_root, start, end))
                {
                    yield return _root;
                }

                yield break;
            }

            if (!Directory.Exists(_root))
            {
                yield break;
            }

            var endDayExclusive = end.TimeOfDay == TimeSpan.Zero ? end.Date : end.Date.AddDays(1);
            for (var day = start.Date; day < endDayExclusive; day = day.AddDays(1))
            {
                foreach (var file in FindDayFiles(day))
                {
                    yield return file;
                }
            }
        }

        private IEnumerable<string> FindDayFiles(DateTime day)
        {
            var names = new[]
            {
                day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "-状态记录表.csv",
                day.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "-状态记录表.csv",
                day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "-MACHINESTATE.csv",
                "MACHINESTATE_" + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".csv"
            };

            foreach (var name in names)
            {
                var direct = Path.Combine(_root, name);
                if (File.Exists(direct))
                {
                    yield return direct;
                }

                var dayDir = Path.Combine(_root, day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                var nested = Path.Combine(dayDir, name);
                if (File.Exists(nested))
                {
                    yield return nested;
                }
            }

            foreach (var file in ScanStateFiles(_root, day))
            {
                yield return file;
            }

            var dir = Path.Combine(_root, day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            foreach (var file in ScanStateFiles(dir, day))
            {
                yield return file;
            }
        }

        private static IEnumerable<string> ScanStateFiles(string directory, DateTime day)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                yield break;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory, "*.csv", SearchOption.TopDirectoryOnly).ToList();
            }
            catch
            {
                yield break;
            }

            foreach (var file in files)
            {
                var name = Path.GetFileName(file) ?? string.Empty;
                if (!LooksLikeStateFile(name))
                {
                    continue;
                }

                DateTime fileDay;
                if (TryParseDayFromFileName(name, out fileDay) && fileDay.Date == day.Date)
                {
                    yield return file;
                }
            }
        }

        private static bool MatchesRange(string path, DateTime start, DateTime end)
        {
            DateTime day;
            if (!TryParseDayFromFileName(Path.GetFileName(path) ?? string.Empty, out day))
            {
                return start.Date == end.Date || start.Date.AddDays(1) == end.Date;
            }

            return day.Date >= start.Date && day.Date < end.Date;
        }

        private static bool LooksLikeStateFile(string name)
        {
            return !string.IsNullOrWhiteSpace(name) &&
                   (name.IndexOf("状态记录表", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("状态", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("MACHINESTATE", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool TryParseDayFromFileName(string fileName, out DateTime day)
        {
            day = default(DateTime);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            var match = Regex.Match(fileName, @"(?<date>20\d{2}[-_]?\d{2}[-_]?\d{2})");
            if (!match.Success)
            {
                return false;
            }

            var text = match.Groups["date"].Value.Replace("_", "-");
            var formats = text.Contains("-")
                ? new[] { "yyyy-MM-dd" }
                : new[] { "yyyyMMdd" };
            return DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
        }

        private static void Merge(
            ReportMachineStateSummary summary,
            MachineStateAnalysisData data,
            DateTime start,
            DateTime end,
            IDictionary<string, ReportMachineStateAlarmStat> topAlarms)
        {
            if (data == null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(data.SourceFileName) && !summary.SourceFileNames.Contains(data.SourceFileName))
            {
                summary.SourceFileNames.Add(data.SourceFileName);
            }

            summary.RowCount += data.RowCount;
            summary.ParsedRowCount += data.ParsedRowCount;
            summary.StartTime = Min(summary.StartTime, data.StartTime);
            summary.EndTime = Max(summary.EndTime, data.EndTime);
            summary.RunningSeconds += data.RunningSeconds;
            summary.IdleSeconds += data.IdleSeconds;
            summary.LineDownSeconds += data.AbnormalSeconds;
            summary.OtherSeconds += data.PlannedDowntimeSeconds + data.MaintenanceSeconds + data.UnknownSeconds;
            summary.TotalObservedSeconds += data.TotalObservedSeconds;

            foreach (var warning in data.Warnings ?? new List<string>())
            {
                if (!string.IsNullOrWhiteSpace(warning))
                {
                    summary.Warnings.Add((data.SourceFileName ?? "状态记录表") + "：" + warning);
                }
            }

            foreach (var hour in data.Hourly ?? new List<MachineStateHourStat>())
            {
                if (hour.Hour < start || hour.Hour >= end)
                {
                    continue;
                }

                var targetHour = GetOrAddHour(summary.Hours, hour.Hour.Hour);
                AddHour(targetHour, hour);

                var targetDay = GetOrAddDay(summary.Days, hour.Hour.Date);
                AddDay(targetDay, hour);
            }

            foreach (var item in data.TopErrors ?? new List<MachineStateErrorSummary>())
            {
                if (item == null || string.IsNullOrWhiteSpace(item.ErrorMessage))
                {
                    continue;
                }

                if (!IsLineDownCategory(item.Category))
                {
                    continue;
                }

                var key = (item.ErrorCode ?? string.Empty) + "|" + item.ErrorMessage;
                ReportMachineStateAlarmStat bucket;
                if (!topAlarms.TryGetValue(key, out bucket))
                {
                    bucket = new ReportMachineStateAlarmStat
                    {
                        ErrorCode = item.ErrorCode,
                        ErrorMessage = item.ErrorMessage
                    };
                    topAlarms.Add(key, bucket);
                }

                bucket.Count += item.Count;
                bucket.DurationSeconds += item.DurationSeconds;
            }
        }

        private static ReportMachineStateHourStat GetOrAddHour(IList<ReportMachineStateHourStat> list, int hour)
        {
            var item = list.FirstOrDefault(x => x.Hour == hour);
            if (item != null)
            {
                return item;
            }

            item = new ReportMachineStateHourStat { Hour = hour };
            list.Add(item);
            return item;
        }

        private static ReportMachineStateDayStat GetOrAddDay(IList<ReportMachineStateDayStat> list, DateTime day)
        {
            var item = list.FirstOrDefault(x => x.Date.Date == day.Date);
            if (item != null)
            {
                return item;
            }

            item = new ReportMachineStateDayStat { Date = day.Date };
            list.Add(item);
            return item;
        }

        private static void AddHour(ReportMachineStateHourStat target, MachineStateHourStat source)
        {
            target.RunningSeconds += source.RunningSeconds;
            target.IdleSeconds += source.IdleSeconds;
            target.LineDownSeconds += source.AbnormalSeconds;
            target.OtherSeconds += source.PlannedDowntimeSeconds + source.MaintenanceSeconds + source.UnknownSeconds;
            target.TotalSeconds += source.TotalSeconds;
            target.RunningRate = SafeRate(target.RunningSeconds, target.TotalSeconds);
            target.IdleRate = SafeRate(target.IdleSeconds, target.TotalSeconds);
            target.LineDownRate = SafeRate(target.LineDownSeconds, target.TotalSeconds);
        }

        private static void AddDay(ReportMachineStateDayStat target, MachineStateHourStat source)
        {
            target.RunningSeconds += source.RunningSeconds;
            target.IdleSeconds += source.IdleSeconds;
            target.LineDownSeconds += source.AbnormalSeconds;
            target.OtherSeconds += source.PlannedDowntimeSeconds + source.MaintenanceSeconds + source.UnknownSeconds;
            target.TotalSeconds += source.TotalSeconds;
            target.RunningRate = SafeRate(target.RunningSeconds, target.TotalSeconds);
            target.IdleRate = SafeRate(target.IdleSeconds, target.TotalSeconds);
            target.LineDownRate = SafeRate(target.LineDownSeconds, target.TotalSeconds);
        }

        private static void ApplyRates(ReportMachineStateSummary summary)
        {
            if (summary == null)
            {
                return;
            }

            summary.RunningRate = SafeRate(summary.RunningSeconds, summary.TotalObservedSeconds);
            summary.IdleRate = SafeRate(summary.IdleSeconds, summary.TotalObservedSeconds);
            summary.LineDownRate = SafeRate(summary.LineDownSeconds, summary.TotalObservedSeconds);
        }

        private static DateTime? Min(DateTime? left, DateTime? right)
        {
            if (!left.HasValue) return right;
            if (!right.HasValue) return left;
            return left.Value <= right.Value ? left : right;
        }

        private static DateTime? Max(DateTime? left, DateTime? right)
        {
            if (!left.HasValue) return right;
            if (!right.HasValue) return left;
            return left.Value >= right.Value ? left : right;
        }

        private static double SafeRate(double value, double total)
        {
            return total > 0d ? value / total : 0d;
        }

        private static bool IsLineDownCategory(string category)
        {
            if (string.IsNullOrWhiteSpace(category))
            {
                return false;
            }

            return category.IndexOf("异常", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   category.IndexOf("LINEDOWN", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   category.IndexOf("报警", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}

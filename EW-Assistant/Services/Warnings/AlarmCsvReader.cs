using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace EW_Assistant.Warnings
{
    /// <summary>
    /// 读取报警记录，并按小时/报警码聚合，兼容 Watch 目录与不同分隔符/编码。
    /// </summary>
    public class AlarmCsvReader
    {
        private readonly string _root;

        public AlarmCsvReader(string root = null)
        {
            _root = string.IsNullOrWhiteSpace(root) ? LocalDataConfig.AlarmCsvRoot : root;
        }

        /// <summary>取最近 24 小时报警，便于快速生成当天图表。</summary>
        public IList<AlarmHourStat> GetLast24HoursAlarms(DateTime now)
        {
            return GetAlarms(now.AddHours(-24), now);
        }

        /// <summary>
        /// 指定时间范围内的报警按小时聚合，优先按文件名日期匹配，缺失字段保持兼容。
        /// </summary>
        public IList<AlarmHourStat> GetAlarms(DateTime start, DateTime end)
        {
            var result = new Dictionary<string, AlarmHourStat>(StringComparer.OrdinalIgnoreCase);
            var watchMode = LocalDataConfig.WatchMode;

            foreach (var file in EnumerateFiles(start, end, watchMode))
            {
                var fileDate = GuessDateFromFile(file);
                foreach (var row in ReadRows(file, fileDate))
                {
                    var occur = row.StartTime ?? row.EndTime;
                    if (!occur.HasValue) continue;
                    var at = occur.Value;

                    if (at < start || at >= end) continue;

                    var hour = new DateTime(at.Year, at.Month, at.Day, at.Hour, 0, 0, at.Kind);
                    var ruleKey = $"{(string.IsNullOrWhiteSpace(row.Code) ? "UNKNOWN" : row.Code)}|{hour:yyyyMMddHH}";

                    if (!result.TryGetValue(ruleKey, out var stat))
                    {
                        stat = new AlarmHourStat
                        {
                            Hour = hour,
                            Code = string.IsNullOrWhiteSpace(row.Code) ? "UNKNOWN" : row.Code,
                            Message = row.Message,
                            Category = row.Category
                        };
                        result.Add(ruleKey, stat);
                    }

                    stat.Count += 1;
                    stat.DowntimeMinutes += row.DowntimeMinutes ?? 0d;
                    if (string.IsNullOrWhiteSpace(stat.Message) && !string.IsNullOrWhiteSpace(row.Message))
                    {
                        stat.Message = row.Message;
                    }
                    if (string.IsNullOrWhiteSpace(stat.Category) && !string.IsNullOrWhiteSpace(row.Category))
                    {
                        stat.Category = row.Category;
                    }
                }
            }

            return result.Values.OrderBy(x => x.Hour).ToList();
        }

        /// <summary>实际使用的报警根目录（若构造时为空则为配置目录）。</summary>
        public string Root => _root;

        /// <summary>遍历时间范围内可能包含的 CSV 文件，支持 Watch 模式的子目录结构。</summary>
        private IEnumerable<string> EnumerateFiles(DateTime start, DateTime end, bool watchMode)
        {
            if (!Directory.Exists(_root)) yield break;

            if (watchMode)
            {
                foreach (var path in EnumerateWatchModeFiles(start, end))
                {
                    yield return path;
                }
                yield break;
            }

            var dir = new DirectoryInfo(_root);
            var minDay = start.Date.AddDays(-1);
            var maxDay = end.Date;
            foreach (var file in dir.EnumerateFiles("*.csv", SearchOption.TopDirectoryOnly)
                                    .OrderByDescending(f => f.LastWriteTime))
            {
                if (!IsAlarmLogFileName(file.Name)) continue;
                if (!TryParseDayFromFileName(file.Name, out var day)) continue;
                if (day < minDay || day > maxDay) continue;
                // 拷贝文件改名可能保留旧的 LastWriteTime，改用文件名日期过滤
                yield return file.FullName;
            }
        }

        /// <summary>Watch 模式：逐日子目录中选择最晚写入的 CSV 文件。</summary>
        private IEnumerable<string> EnumerateWatchModeFiles(DateTime start, DateTime end)
        {
            var startDay = start.Date.AddDays(-1);
            var endDay = end.Date;
            var dir = new DirectoryInfo(_root);

            var dayDirs = dir.EnumerateDirectories()
                .Select(d => new { Dir = d, Day = ParseDayFromDirectory(d.Name) })
                .Where(x => x.Day.HasValue && x.Day.Value >= startDay && x.Day.Value <= endDay)
                .OrderByDescending(x => x.Day.Value);

            foreach (var d in dayDirs)
            {
                var file = PickLatestCsv(d.Dir, d.Day.Value);
                if (!string.IsNullOrWhiteSpace(file))
                {
                    yield return file;
                }
            }
        }

        private static DateTime? ParseDayFromDirectory(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            if (DateTime.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            {
                return day.Date;
            }
            return null;
        }

        private static string? PickLatestCsv(DirectoryInfo dir, DateTime day)
        {
            if (dir == null || !dir.Exists) return null;
            var file = dir.EnumerateFiles("*.csv", SearchOption.TopDirectoryOnly)
                          .Where(f => MatchesDayFileName(f.Name, day))
                          .OrderByDescending(f => f.LastWriteTime)
                          .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                          .FirstOrDefault();
            return file?.FullName;
        }

        /// <summary>读取单个 CSV，按列名/格式自适应解析并产出 AlarmRow。</summary>
        private IEnumerable<AlarmRow> ReadRows(string path, DateTime fileDate)
        {
            if (!File.Exists(path)) yield break;

            using (var reader = CsvEncoding.OpenReader(path))
            {
                string headerLine = null;
                while (headerLine == null && !reader.EndOfStream)
                {
                    var line = reader.ReadLine();
                    if (!string.IsNullOrWhiteSpace(line)) headerLine = line;
                }

                if (string.IsNullOrEmpty(headerLine)) yield break;

                var delimiter = DetectDelimiter(headerLine);
                var headers = headerLine.Split(new[] { delimiter }, StringSplitOptions.None)
                    .Select(NormalizeHeader)
                    .ToArray();

                var codeIndex = FindIndex(headers, "code", "报警代码", "alarmcode", "报警编号", "错误编码");
                var messageIndex = FindIndex(headers, "message", "content", "报警内容", "描述", "错误信息");
                var categoryIndex = FindIndex(headers, "category", "类别", "type", "错误类型");
                var startIndex = FindIndex(headers, "starttime", "开始时间", "start", "起始时间");
                var endIndex = FindIndex(headers, "endtime", "结束时间", "end");
                var durationIndex = FindIndex(headers, "duration", "时长", "持续", "durationsec", "时长秒", "持续时间", "维修耗时", "报警时间(s)", "报警时长(s)");

                while (!reader.EndOfStream)
                {
                    var line = reader.ReadLine();
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var cells = line.Split(new[] { delimiter }, StringSplitOptions.None);
                    if (cells.Length == 0) continue;

                    var start = ParseDateTime(GetCell(cells, startIndex), fileDate);
                    var end = ParseDateTime(GetCell(cells, endIndex), fileDate);
                    var downtime = ParseNullableDouble(GetCell(cells, durationIndex));
                    if (downtime.HasValue)
                    {
                        downtime = downtime.Value / 60d; // CSV 默认秒，转为分钟
                    }
                    else if (start.HasValue && end.HasValue)
                    {
                        downtime = (end.Value - start.Value).TotalMinutes;
                    }

                    yield return new AlarmRow
                    {
                        Code = GetCell(cells, codeIndex),
                        Message = GetCell(cells, messageIndex),
                        Category = GetCell(cells, categoryIndex),
                        StartTime = start,
                        EndTime = end,
                        DowntimeMinutes = downtime
                    };
                }
            }
        }

        private static string GetCell(string[] cells, int index)
        {
            if (index < 0 || index >= cells.Length) return null;
            return NormalizeCell(cells[index]);
        }

        private static string NormalizeHeader(string header)
        {
            if (header == null) return string.Empty;
            var normalized = header.Trim().Trim('"', '\'', '“', '”');
            return normalized.Replace(" ", string.Empty).Replace("\t", string.Empty).ToLowerInvariant();
        }

        /// <summary>去除单元格外围引号并做简单的空白清理，避免被双引号包裹时解析失败。</summary>
        private static string NormalizeCell(string cell)
        {
            if (string.IsNullOrWhiteSpace(cell)) return null;
            var text = cell.Trim();
            if (text.Length >= 2)
            {
                var first = text[0];
                var last = text[text.Length - 1];
                if ((first == '"' && last == '"') || (first == '“' && last == '”') || (first == '\'' && last == '\''))
                {
                    text = text.Substring(1, text.Length - 2);
                    text = text.Replace("\"\"", "\"");
                }
            }
            return text.Trim();
        }

        private static int FindIndex(string[] headers, params string[] names)
        {
            for (var i = 0; i < headers.Length; i++)
            {
                foreach (var n in names)
                {
                    if (string.Equals(headers[i], NormalizeHeader(n), StringComparison.OrdinalIgnoreCase))
                        return i;
                }
            }
            return -1;
        }

        private static char DetectDelimiter(string line)
        {
            var candidates = new[] { ',', ';', '\t' };
            var best = candidates
                .Select(c => new { c, count = line.Count(ch => ch == c) })
                .OrderByDescending(x => x.count)
                .FirstOrDefault();

            return best == null || best.count == 0 ? ',' : best.c;
        }

        private static double? ParseNullableDouble(string value)
        {
            if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) return v;
            if (TimeSpan.TryParse(value, out var ts)) return ts.TotalSeconds;
            return null;
        }

        private static DateTime? ParseDateTime(string value, DateTime fallbackDate)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var text = value.Trim();

            if (TimeSpan.TryParse(text, out var ts))
            {
                return fallbackDate.Date + ts;
            }

            if (text.Length >= 5 && TimeSpan.TryParseExact(text, "hh\\:mm", CultureInfo.InvariantCulture, out ts))
            {
                return fallbackDate.Date + ts;
            }

            if (int.TryParse(text, out var hour) && hour >= 0 && hour <= 23)
            {
                return fallbackDate.Date.AddHours(hour);
            }

            var formats = new[] { "yyyyMMddHHmmss", "yyyyMMddHHmm", "yyyyMMddHH" };
            foreach (var fmt in formats)
            {
                if (DateTime.TryParseExact(text, fmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                {
                    return dt;
                }
            }

            if (DateTime.TryParse(text, out var parsed)) return parsed;

            return null;
        }

        private static DateTime GuessDateFromFile(string path)
        {
            var fileName = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
            if (TryParseDayFromFileName($"{fileName}.csv", out var parsedDay)) return parsedDay;

            var dirName = Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty) ?? string.Empty;
            if (DateTime.TryParseExact(dirName, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var byDir))
            {
                return byDir.Date;
            }

            return File.GetLastWriteTime(path).Date;
        }

        private static bool TryParseDayFromFileName(string fileName, out DateTime day)
        {
            day = default;
            if (string.IsNullOrWhiteSpace(fileName)) return false;

            var match = Regex.Match(fileName.Trim(), @"^(?<y>20\d{2})-(?<m>\d{2})-(?<d>\d{2})(?:-报警记录表)?\.csv$", RegexOptions.IgnoreCase);
            if (match.Success
                && int.TryParse(match.Groups["y"].Value, out var y)
                && int.TryParse(match.Groups["m"].Value, out var m)
                && int.TryParse(match.Groups["d"].Value, out var d))
            {
                try
                {
                    day = new DateTime(y, m, d);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        private static bool IsAlarmLogFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return false;
            var name = fileName.Trim();
            return Regex.IsMatch(name, @"^\d{4}-\d{2}-\d{2}-报警记录表\.csv$", RegexOptions.IgnoreCase)
                   || Regex.IsMatch(name, @"^\d{4}-\d{2}-\d{2}\.csv$", RegexOptions.IgnoreCase);
        }

        private static bool MatchesDayFileName(string fileName, DateTime day)
        {
            var withSuffix = $"{day:yyyy-MM-dd}-报警记录表.csv";
            var plain = $"{day:yyyy-MM-dd}.csv";
            return string.Equals(fileName, withSuffix, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(fileName, plain, StringComparison.OrdinalIgnoreCase);
        }

        private sealed class AlarmRow
        {
            public string Code { get; set; }
            public string Message { get; set; }
            public string Category { get; set; }
            public DateTime? StartTime { get; set; }
            public DateTime? EndTime { get; set; }
            public double? DowntimeMinutes { get; set; }
        }
    }

    /// <summary>
    /// 按小时聚合的报警信息。
    /// </summary>
    public class AlarmHourStat
    {
        public DateTime Hour { get; set; }
        public string Code { get; set; }
        public string Message { get; set; }
        public string Category { get; set; }
        public int Count { get; set; }
        public double DowntimeMinutes { get; set; }
    }
}

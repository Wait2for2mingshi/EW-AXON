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
    /// 最近 N 天报警 CSV 的读取与缓存，统一供 Dashboard/AlarmView 使用。
    /// </summary>
    public static class AlarmLogCache
    {
        public sealed class DayRaw
        {
            /// <summary>日期（仅日期部分）。</summary>
            public DateTime Date { get; set; }
            /// <summary>是否未找到文件或解析失败。</summary>
            public bool Missing { get; set; }
            /// <summary>命中的文件路径。</summary>
            public string? FilePath { get; set; }
            /// <summary>实际使用的编码（探测失败则为 null）。</summary>
            public Encoding? EncodingUsed { get; set; }
            /// <summary>原始行内容（保留空行，供上层重跑解析）。</summary>
            public string[] LinesAll { get; set; } = Array.Empty<string>();
        }

        private static readonly object _swapLock = new();
        private static IReadOnlyDictionary<DateTime, DayRaw> _byDay = new Dictionary<DateTime, DayRaw>();

        /// <summary>
        /// 读取目录下最近 N 天的报警 CSV，并缓存原始行；失败的日期标记 Missing，便于 UI 显示缺口。
        /// </summary>
        public static void LoadRecent(string? root, int days = 7)
        {
            if (days <= 0) days = 7;
            var dir = string.IsNullOrWhiteSpace(root) ? LocalDataConfig.AlarmCsvRoot : root;
            var watchMode = LocalDataConfig.WatchMode;

            var map = new Dictionary<DateTime, DayRaw>();

            for (int i = days - 1; i >= 0; i--)
            {
                var day = DateTime.Today.AddDays(-i).Date;
                if (!TrySeekCsv(dir, day, watchMode, out var file))
                {
                    map[day] = new DayRaw { Date = day, Missing = true };
                    continue;
                }

                try
                {
                    var enc = DetectEncoding();
                    var lines = ReadAllLinesShared(file, enc, keepEmpty: true);
                    map[day] = new DayRaw
                    {
                        Date = day,
                        FilePath = file,
                        Missing = false,
                        EncodingUsed = enc,
                        LinesAll = lines.ToArray()
                    };
                }
                catch
                {
                    map[day] = new DayRaw { Date = day, FilePath = file, Missing = true };
                }
            }

            lock (_swapLock)
            {
                _byDay = new Dictionary<DateTime, DayRaw>(map);
            }
        }

        /// <summary>按天取缓存（可能 Missing），不触发磁盘读取。</summary>
        public static DayRaw? GetDayRaw(DateTime date)
        {
            var d = date.Date;
            lock (_swapLock)
            {
                return _byDay.TryGetValue(d, out var v) ? v : null;
            }
        }

        /// <summary>获取最近 N 天的缓存快照，按时间顺序返回。</summary>
        public static IReadOnlyList<DayRaw> SnapshotRecent(int days = 7)
        {
            if (days <= 0) days = 7;
            IReadOnlyDictionary<DateTime, DayRaw> snap;
            lock (_swapLock) snap = _byDay;

            var list = new List<DayRaw>(days);
            for (int i = days - 1; i >= 0; i--)
            {
                var d = DateTime.Today.AddDays(-i).Date;
                list.Add(snap.TryGetValue(d, out var v) ? v : new DayRaw { Date = d, Missing = true });
            }
            return list;
        }

        // ===== 内部工具：定位文件/编码/读取 =====
        private static bool TrySeekCsv(string dir, DateTime day, bool watchMode, out string? file)
        {
            file = null;
            try
            {
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;

                if (watchMode)
                {
                    var dayDir = Path.Combine(dir, day.ToString("yyyy-MM-dd"));
                    if (!Directory.Exists(dayDir)) return false;

                    var files = Directory.GetFiles(dayDir, "*.csv", SearchOption.TopDirectoryOnly);
                    file = PickCsvForDay(day, files);
                    return !string.IsNullOrWhiteSpace(file);
                }

                var all = Directory.GetFiles(dir, "*.csv", SearchOption.TopDirectoryOnly);
                file = PickCsvForDay(day, all);
                return !string.IsNullOrWhiteSpace(file);
            }
            catch
            {
                return false;
            }
        }

        private static string? PickCsvForDay(DateTime day, IEnumerable<string> files)
        {
            if (files == null) return null;
            var expectedNames = new[]
            {
                $"{day:yyyy-MM-dd}-报警记录表.csv",
                $"{day:yyyy-MM-dd}.csv"
            };

            foreach (var expected in expectedNames)
            {
                var hit = files.FirstOrDefault(f =>
                    string.Equals(Path.GetFileName(f), expected, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(hit)) return hit;
            }

            return null;
        }

        private static List<string> ReadAllLinesShared(string path, Encoding enc, bool keepEmpty)
        {
            var list = new List<string>();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs, enc, detectEncodingFromByteOrderMarks: true);
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                if (keepEmpty) list.Add(line);
                else if (!string.IsNullOrWhiteSpace(line)) list.Add(line);
            }
            return list;
        }

        private static Encoding DetectEncoding()
        {
            try { return Encoding.GetEncoding("GB2312"); }
            catch
            {
                try { return Encoding.GetEncoding("GB18030"); }
                catch { return new UTF8Encoding(false); }
            }
        }
    }

    /// <summary>
    /// 基于缓存的报警统计与解析工具。
    /// </summary>
    public static class AlarmLogCompute
    {
        public sealed class AlarmRecord
        {
            public DateTime Start { get; set; }
            public string Code { get; set; } = "";
            public string Category { get; set; } = "";
            public double Seconds { get; set; }
            public string Content { get; set; } = "";
            public string FileName { get; set; } = "";
        }

        public static IReadOnlyList<(DateTime Date, int Count, bool Missing)> GetDailyCountsFromCache(int days = 7)
        {
            var raws = AlarmLogCache.SnapshotRecent(days);
            var list = new List<(DateTime, int, bool)>(raws.Count);

            foreach (var r in raws)
            {
                if (r.Missing || r.LinesAll.Length == 0)
                {
                    list.Add((r.Date, 0, true));
                    continue;
                }

                var (startRow, _) = TryDetectHeader(r.LinesAll);
                int cnt = 0;
                for (int i = startRow; i < r.LinesAll.Length; i++)
                {
                    var line = r.LinesAll[i];
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    cnt++;
                }

                list.Add((r.Date, cnt, false));
            }
            return list;
        }

        public static List<(string Category, int Count)> GetTopCategoriesFromCache(DateTime date, int topN = 5)
        {
            var raw = AlarmLogCache.GetDayRaw(date);
            if (raw is null || raw.Missing || raw.LinesAll.Length == 0) return new();

            var (startRow, headerCols) = TryDetectHeader(raw.LinesAll);
            int idxCategory = FindHeaderIndex(headerCols,
                "报警类别", "报警类型", "类别", "Type", "Category", "错误类型");

            var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = startRow; i < raw.LinesAll.Length; i++)
            {
                var line = raw.LinesAll[i];
                if (string.IsNullOrWhiteSpace(line)) continue;
                var cols = SmartSplit(line);

                string key;
                if (idxCategory >= 0 && idxCategory < cols.Length)
                    key = NormalizeCell(cols[idxCategory]);
                else
                    key = "Unknown";

                if (key.Length == 0) key = "Unknown";

                dict[key] = dict.TryGetValue(key, out var c) ? c + 1 : 1;
            }

            return dict.OrderByDescending(kv => kv.Value)
                       .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                       .Take(Math.Max(1, topN))
                       .Select(kv => (kv.Key, kv.Value))
                       .ToList();
        }

        public static int[] GetHourlyCountsFromCache(DateTime date)
        {
            var result = new int[24];

            var raw = AlarmLogCache.GetDayRaw(date);
            if (raw is null || raw.Missing || raw.LinesAll is null || raw.LinesAll.Length == 0)
                return result;

            var (startRow, headerCols) = TryDetectHeader(raw.LinesAll);

            int idxDate = FindHeaderIndex(headerCols, "日期", "Date");
            int idxStart = FindHeaderIndex(headerCols, "开始时间", "发生时间", "Start", "StartTime", "时间", "起始时间");
            int idxHour = FindHeaderIndex(headerCols,
                "小时", "Hour", "HOUR",
                "时间", "发生时间", "时刻", "时段",
                "时间戳", "Timestamp", "DateTime", "报警时间", "开始时间", "起始时间");

            for (int i = startRow; i < raw.LinesAll.Length; i++)
            {
                var line = raw.LinesAll[i];
                if (string.IsNullOrWhiteSpace(line)) continue;

                var cols = SmartSplit(line);

                int hour = -1;
                DateTime ts;

                if (TryParseStartLoose(
                        idxStart >= 0 && idxStart < cols.Length ? cols[idxStart] : null,
                        idxDate >= 0 && idxDate < cols.Length ? cols[idxDate] : null,
                        date,
                        out ts))
                {
                    if (ts < date.Date || ts >= date.Date.AddDays(1)) continue;
                    hour = ts.Hour;
                }

                if (hour < 0 && idxHour >= 0 && idxHour < cols.Length)
                    hour = ExtractHourLoose(cols[idxHour]);

                if (hour < 0 || hour > 23) continue;
                result[hour]++;
            }

            return result;
        }

        public static double[] GetHourlySecondsFromCache(DateTime date)
        {
            var result = new double[24];

            var raw = AlarmLogCache.GetDayRaw(date);
            if (raw is null || raw.Missing || raw.LinesAll is null || raw.LinesAll.Length == 0)
                return result;

            var (startRow, headerCols) = TryDetectHeader(raw.LinesAll);

            int idxDate = FindHeaderIndex(headerCols, "日期", "Date");
            int idxStart = FindHeaderIndex(headerCols, "开始时间", "发生时间", "Start", "StartTime", "时间", "起始时间");
            int idxHour = FindHeaderIndex(headerCols,
                "小时", "Hour", "HOUR",
                "时间", "发生时间", "时刻", "时段",
                "时间戳", "Timestamp", "DateTime", "报警时间", "开始时间", "起始时间");
            int idxSeconds = FindHeaderIndex(headerCols, "报警时间(s)", "报警时长(s)", "持续时间(s)", "Seconds", "Duration", "维修耗时");

            for (int i = startRow; i < raw.LinesAll.Length; i++)
            {
                var line = raw.LinesAll[i];
                if (string.IsNullOrWhiteSpace(line)) continue;

                var cols = SmartSplit(line);
                int hour = -1;
                DateTime ts;

                if (TryParseStartLoose(
                        idxStart >= 0 && idxStart < cols.Length ? cols[idxStart] : null,
                        idxDate >= 0 && idxDate < cols.Length ? cols[idxDate] : null,
                        date,
                        out ts))
                {
                    if (ts < date.Date || ts >= date.Date.AddDays(1)) continue;
                    hour = ts.Hour;
                }

                if (hour < 0 && idxHour >= 0 && idxHour < cols.Length)
                    hour = ExtractHourLoose(cols[idxHour]);

                if (hour < 0 || hour > 23) continue;

                double sec = ParseSecondsFlexible(idxSeconds >= 0 && idxSeconds < cols.Length ? cols[idxSeconds] : null);
                if (sec > 0) result[hour] += sec;
            }

            return result;
        }

        public static List<(string Category, double Seconds)> GetCategoryDurationsFromCache(DateTime date)
        {
            var raw = AlarmLogCache.GetDayRaw(date);
            if (raw is null || raw.Missing || raw.LinesAll is null || raw.LinesAll.Length == 0) return new();

            var (startRow, headerCols) = TryDetectHeader(raw.LinesAll);
            int idxCategory = FindHeaderIndex(headerCols, "报警类别", "报警类型", "类别", "Type", "Category", "错误类型");
            int idxSeconds = FindHeaderIndex(headerCols, "报警时间(s)", "报警时长(s)", "持续时间(s)", "Seconds", "Duration", "维修耗时");

            var dict = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            for (int i = startRow; i < raw.LinesAll.Length; i++)
            {
                var line = raw.LinesAll[i];
                if (string.IsNullOrWhiteSpace(line)) continue;
                var cols = SmartSplit(line);

                string cat = (idxCategory >= 0 && idxCategory < cols.Length) ? NormalizeCell(cols[idxCategory]) : "Unknown";
                if (string.IsNullOrEmpty(cat)) cat = "Unknown";

                double sec = ParseSecondsFlexible(idxSeconds >= 0 && idxSeconds < cols.Length ? cols[idxSeconds] : null);
                if (sec > 0)
                    dict[cat] = dict.TryGetValue(cat, out var acc) ? acc + sec : sec;
            }

            return dict
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => (kv.Key, kv.Value))
                .ToList();
        }

        public static List<(string Category, int Count, double Seconds)> GetCategoryStatsFromCache(DateTime date)
        {
            var raw = AlarmLogCache.GetDayRaw(date);
            if (raw is null || raw.Missing || raw.LinesAll is null || raw.LinesAll.Length == 0) return new();

            var (startRow, headerCols) = TryDetectHeader(raw.LinesAll);
            int idxDate = FindHeaderIndex(headerCols, "日期", "Date");
            int idxStart = FindHeaderIndex(headerCols, "开始时间", "发生时间", "Start", "StartTime", "时间", "起始时间");
            int idxHour = FindHeaderIndex(headerCols,
                "小时", "Hour", "HOUR",
                "时间", "发生时间", "时刻", "时段",
                "时间戳", "Timestamp", "DateTime", "报警时间", "开始时间", "起始时间");
            int idxCategory = FindHeaderIndex(headerCols, "报警类别", "报警类型", "类别", "Type", "Category", "错误类型");
            int idxSeconds = FindHeaderIndex(headerCols, "报警时间(s)", "报警时长(s)", "持续时间(s)", "Seconds", "Duration", "维修耗时");

            var dict = new Dictionary<string, (int Count, double Seconds)>(StringComparer.OrdinalIgnoreCase);

            for (int i = startRow; i < raw.LinesAll.Length; i++)
            {
                var line = raw.LinesAll[i];
                if (string.IsNullOrWhiteSpace(line)) continue;
                var cols = SmartSplit(line);

                int hour = -1;
                DateTime ts;

                if (TryParseStartLoose(
                        idxStart >= 0 && idxStart < cols.Length ? cols[idxStart] : null,
                        idxDate >= 0 && idxDate < cols.Length ? cols[idxDate] : null,
                        date,
                        out ts))
                {
                    if (ts < date.Date || ts >= date.Date.AddDays(1)) continue;
                    hour = ts.Hour;
                }

                if (hour < 0 && idxHour >= 0 && idxHour < cols.Length)
                    hour = ExtractHourLoose(cols[idxHour]);

                if (hour < 0 || hour > 23) continue;

                string cat = (idxCategory >= 0 && idxCategory < cols.Length) ? NormalizeCell(cols[idxCategory]) : "Unknown";
                if (string.IsNullOrWhiteSpace(cat)) cat = "Unknown";

                double sec = ParseSecondsFlexible(idxSeconds >= 0 && idxSeconds < cols.Length ? cols[idxSeconds] : null);

                if (!dict.TryGetValue(cat, out var v)) v = (0, 0.0);
                dict[cat] = (v.Count + 1, v.Seconds + Math.Max(0, sec));
            }

            return dict
                .OrderByDescending(kv => kv.Value.Seconds)
                .ThenByDescending(kv => kv.Value.Count)
                .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => (kv.Key, kv.Value.Count, kv.Value.Seconds))
                .ToList();
        }

        public static IEnumerable<AlarmRecord> EnumerateRecordsFromCache(DateTime day)
        {
            var raw = AlarmLogCache.GetDayRaw(day);
            if (raw is null || raw.Missing || raw.LinesAll is null || raw.LinesAll.Length == 0)
                yield break;

            var (startRow, headerCols) = TryDetectHeader(raw.LinesAll);
            int idxDate = FindHeaderIndex(headerCols, "日期", "Date");
            int idxStart = FindHeaderIndex(headerCols, "开始时间", "发生时间", "Start", "StartTime", "时间", "起始时间");
            int idxCode = FindHeaderIndex(headerCols, "报警代码", "代码", "Code", "错误编码");
            int idxCat = FindHeaderIndex(headerCols, "报警类别", "类别", "Category", "Type", "报警类型", "错误类型");
            int idxSeconds = FindHeaderIndex(headerCols, "报警时间(s)", "报警时长(s)", "持续时间(s)", "Seconds", "Duration", "维修耗时");
            int idxMsg = FindHeaderIndex(headerCols, "报警内容", "描述", "Message", "Content", "备注", "错误信息");

            for (int i = startRow; i < raw.LinesAll.Length; i++)
            {
                var line = raw.LinesAll[i];
                if (string.IsNullOrWhiteSpace(line)) continue;

                var cols = SmartSplit(line);
                if (!TryParseStartLoose(
                        idxStart >= 0 && idxStart < cols.Length ? cols[idxStart] : null,
                        idxDate >= 0 && idxDate < cols.Length ? cols[idxDate] : null,
                        day, out var ts))
                    continue;

                if (ts < day.Date || ts >= day.Date.AddDays(1)) continue;

                var code = NormalizeCell(idxCode >= 0 && idxCode < cols.Length ? cols[idxCode] : "");
                var cat = NormalizeCell(idxCat >= 0 && idxCat < cols.Length ? cols[idxCat] : "");
                var msg = NormalizeCell(idxMsg >= 0 && idxMsg < cols.Length ? cols[idxMsg] : "");
                var sec = ParseSecondsFlexible(idxSeconds >= 0 && idxSeconds < cols.Length ? cols[idxSeconds] : null);

                yield return new AlarmRecord
                {
                    Start = ts,
                    Code = string.IsNullOrWhiteSpace(code) ? "Unknown" : code,
                    Category = string.IsNullOrWhiteSpace(cat) ? "Unknown" : cat,
                    Seconds = Math.Max(0, sec),
                    Content = msg,
                    FileName = Path.GetFileName(raw.FilePath ?? "")
                };
            }
        }

        // ===== 解析辅助 =====
        private static int FindHeaderIndex(string[] header, params string[] keys)
        {
            if (header == null || header.Length == 0) return -1;

            for (int i = 0; i < header.Length; i++)
            {
                var t = NormalizeCell(header[i]).Trim();
                foreach (var k in keys)
                {
                    if (string.Equals(t, k, StringComparison.OrdinalIgnoreCase))
                        return i;
                }
            }
            return -1;
        }

        private static string NormalizeCell(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            s = s.Trim();

            if (s.Length >= 2)
            {
                char a = s[0], b = s[s.Length - 1];
                if ((a == '"' && b == '"') || (a == '“' && b == '”') || (a == '\'' && b == '\''))
                {
                    s = s.Substring(1, s.Length - 2);
                    s = s.Replace("\"\"", "\"");
                }
            }
            return s.Trim();
        }

        private static (int startRow, string[] headerCols) TryDetectHeader(string[] lines)
        {
            for (int i = 0; i < lines.Length; i++)
            {
                var l = lines[i];
                if (string.IsNullOrWhiteSpace(l)) continue;

                var cols = SmartSplit(l);
                if (cols.Length < 2) continue;

                var joined = string.Join("|", cols);
                var joinedNorm = StripAllQuotes(joined).ToLowerInvariant();

                if (joinedNorm.Contains("时") || joinedNorm.Contains("date") ||
                    joinedNorm.Contains("类") || joinedNorm.Contains("type") ||
                    joinedNorm.Contains("category") || joinedNorm.Contains("code"))
                {
                    return (Math.Min(i + 1, lines.Length), cols);
                }
            }
            return (0, Array.Empty<string>());
        }

        private static string StripAllQuotes(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("\"", "").Replace("“", "").Replace("”", "").Replace("'", "");
        }

        private static string[] SmartSplit(string line)
        {
            if (line == null) return new[] { string.Empty };
            if (line.Length == 0) return new[] { string.Empty };

            char delim = ChooseDelimiter(line);
            if (delim == '\0') return new[] { line };

            var list = new List<string>(16);
            var sb = new StringBuilder(line.Length);
            bool inAsciiQuote = false;
            bool inCnQuote = false;

            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];

                if (!inCnQuote && ch == '"')
                {
                    if (inAsciiQuote)
                    {
                        bool isEscaped = (i + 1 < line.Length && line[i + 1] == '"');
                        if (isEscaped)
                        {
                            sb.Append('"');
                            i++;
                        }
                        else
                        {
                            inAsciiQuote = false;
                        }
                    }
                    else
                    {
                        inAsciiQuote = true;
                    }
                    continue;
                }
                else if (!inAsciiQuote && (ch == '“' || ch == '”'))
                {
                    if (!inCnQuote && ch == '“') { inCnQuote = true; continue; }
                    if (inCnQuote && ch == '”') { inCnQuote = false; continue; }
                }

                if (!inAsciiQuote && !inCnQuote && ch == delim)
                {
                    list.Add(NormalizeCell(sb.ToString()));
                    sb.Length = 0;
                    continue;
                }

                sb.Append(ch);
            }

            list.Add(NormalizeCell(sb.ToString()));
            return list.ToArray();
        }

        private static char ChooseDelimiter(string line)
        {
            int cComma = 0, cTab = 0, cSemi = 0, cCComma = 0;
            bool inAsciiQuote = false, inCnQuote = false;

            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];

                if (!inCnQuote && ch == '"')
                {
                    if (inAsciiQuote)
                    {
                        bool isEscaped = (i + 1 < line.Length && line[i + 1] == '"');
                        if (isEscaped) { i++; } else { inAsciiQuote = false; }
                    }
                    else
                    {
                        inAsciiQuote = true;
                    }
                    continue;
                }
                else if (!inAsciiQuote && (ch == '“' || ch == '”'))
                {
                    if (!inCnQuote && ch == '“') { inCnQuote = true; continue; }
                    if (inCnQuote && ch == '”') { inCnQuote = false; continue; }
                }

                if (!inAsciiQuote && !inCnQuote)
                {
                    if (ch == ',') cComma++;
                    else if (ch == '\t') cTab++;
                    else if (ch == ';') cSemi++;
                    else if (ch == '，') cCComma++;
                }
            }

            if (cComma > 0) return ',';
            if (cTab > 0) return '\t';
            if (cSemi > 0) return ';';
            if (cCComma > 0) return '，';
            return '\0';
        }

        private static bool TryParseStartLoose(string? timeCell, string? dateCell, DateTime day, out DateTime ts)
        {
            ts = default;

            string t = NormalizeCell(timeCell ?? string.Empty);
            string d = NormalizeCell(dateCell ?? string.Empty);

            if (!string.IsNullOrWhiteSpace(d) &&
                (DateTime.TryParse(d, CultureInfo.CurrentCulture, DateTimeStyles.None, out var datePart) ||
                 DateTime.TryParse(d, CultureInfo.InvariantCulture, DateTimeStyles.None, out datePart)))
            {
                if (!string.IsNullOrWhiteSpace(t))
                {
                    if (TimeSpan.TryParse(t, out var tod) ||
                        TimeSpan.TryParseExact(
                            t,
                            new[] { @"h\:m", @"h\:mm", @"hh\:mm", @"h\:mm\:ss", @"hh\:mm\:ss" },
                            CultureInfo.InvariantCulture,
                            out tod))
                    {
                        ts = datePart.Date + tod;
                        return true;
                    }

                    if (DateTime.TryParse(t, CultureInfo.CurrentCulture, DateTimeStyles.None, out ts) ||
                        DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.None, out ts))
                    {
                        return true;
                    }
                }

                ts = datePart.Date;
                return true;
            }

            if (!string.IsNullOrWhiteSpace(t))
            {
                if (TimeSpan.TryParse(t, out var tod) ||
                    TimeSpan.TryParseExact(
                        t,
                        new[] { @"h\:m", @"h\:mm", @"hh\:mm", @"h\:mm\:ss", @"hh\:mm\:ss" },
                        CultureInfo.InvariantCulture,
                        out tod))
                {
                    ts = day.Date + tod;
                    return true;
                }

                if (DateTime.TryParse(t, CultureInfo.CurrentCulture, DateTimeStyles.None, out ts) ||
                    DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.None, out ts))
                {
                    return true;
                }
            }

            return false;
        }

        private static int ExtractHourLoose(string s)
        {
            s = NormalizeCell(s);
            if (string.IsNullOrWhiteSpace(s)) return -1;
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int h) && h >= 0 && h <= 23) return h;
            if (DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.None, out var dt)) return dt.Hour;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt)) return dt.Hour;
            var digits = new string(s.TakeWhile(char.IsDigit).ToArray());
            if (digits.Length > 0 && int.TryParse(digits, out h) && h >= 0 && h <= 23) return h;
            return -1;
        }

        private static double ParseSecondsFlexible(string? raw)
        {
            var s = NormalizeCell(raw ?? string.Empty).Trim().ToLowerInvariant();

            if (double.TryParse(s.TrimEnd('s'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ||
                double.TryParse(s.TrimEnd('s'), NumberStyles.Float, CultureInfo.CurrentCulture, out v))
                return Math.Max(0, v);

            var m = Regex.Match(s, @"(?:(\d+(?:\.\d+)?)\s*h)?\s*(?:(\d+(?:\.\d+)?)\s*m)?\s*(?:(\d+(?:\.\d+)?)\s*s)?");
            if (m.Success)
            {
                double h = m.Groups[1].Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
                double min = m.Groups[2].Success ? double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
                double sec = m.Groups[3].Success ? double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
                return h * 3600 + min * 60 + sec;
            }
            return 0;
        }
    }
}

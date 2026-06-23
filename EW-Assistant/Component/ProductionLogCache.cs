using EW_Assistant.Warnings;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace EW_Assistant.Services
{
    /// <summary>
    /// 最近 N 天产能 CSV 的集中读取与缓存，供 Dashboard/ProductionBoard 复用。
    /// </summary>
    public static class ProductionLogCache
    {
        public sealed class DayData
        {
            /// <summary>日期（仅日期部分）。</summary>
            public DateTime Date { get; set; }

            /// <summary>是否未成功加载该日数据（文件缺失或解析失败）。</summary>
            public bool Missing { get; set; }

            /// <summary>原始单表路径；分表模式下为 OK/NG 其中任意一个可用的路径。</summary>
            public string? FilePath { get; set; }

            /// <summary>分表模式下 OK 表路径。</summary>
            public string? OkFilePath { get; set; }

            /// <summary>分表模式下 NG 表路径。</summary>
            public string? NgFilePath { get; set; }

            /// <summary>按小时聚合的良品数量（0-23）。</summary>
            public int[] HourPass { get; } = new int[24];

            /// <summary>按小时聚合的不良数量（0-23）。</summary>
            public int[] HourFail { get; } = new int[24];

            /// <summary>当日良品总数。</summary>
            public int SumPass => HourPass.Sum();

            /// <summary>当日不良总数。</summary>
            public int SumFail => HourFail.Sum();

            /// <summary>当日总产出。</summary>
            public int Total => SumPass + SumFail;
        }

        private static readonly object _lock = new();
        private static IReadOnlyDictionary<DateTime, DayData> _byDay = new Dictionary<DateTime, DayData>();
        private static string _cacheRoot = string.Empty;
        private static string _cachePrefix = string.Empty;
        private static int _cacheDays = 7;
        private static DateTime _lastLoad = DateTime.MinValue;
        private static bool _cacheUseSplitTables;
        private static readonly string[] _splitOkHeaders = new[] { "产出时间", "產出時間" };
        private static readonly string[] _splitNgHeaders = new[] { "抛料开始时间", "抛料时间", "抛料開始時間" };

        /// <summary>
        /// 加载并缓存最近 N 天的产能 CSV；短时间内重复调用会复用内存快照，force=true 时强制重读磁盘。
        /// </summary>
        public static void LoadRecent(string? root, string filePrefix, int days = 7, bool force = false)
        {
            if (days <= 0) days = 7;
            var dir = string.IsNullOrWhiteSpace(root) ? LocalDataConfig.ProductionCsvRoot : root;
            var prefix = string.IsNullOrWhiteSpace(filePrefix) ? "小时产量" : filePrefix;
            var useSplit = LocalDataConfig.UseOkNgSplitTables;
            var now = DateTime.Now;

            lock (_lock)
            {
                if (!force &&
                    string.Equals(_cacheRoot, dir, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(_cachePrefix, prefix, StringComparison.OrdinalIgnoreCase) &&
                    _cacheDays == days &&
                    _cacheUseSplitTables == useSplit &&
                    (now - _lastLoad) < TimeSpan.FromSeconds(3))
                {
                    return;
                }
            }

            var map = new Dictionary<DateTime, DayData>(days);
            var watchMode = LocalDataConfig.WatchMode;

            for (int i = days - 1; i >= 0; i--)
            {
                var day = DateTime.Today.AddDays(-i).Date;
                var data = new DayData { Date = day, Missing = true };

                if (useSplit)
                {
                    if (TrySeekSplitCsv(dir, day, watchMode, out var okFile, out var ngFile))
                    {
                        try
                        {
                            if (TryLoadDayFromSplit(okFile, ngFile, day, data))
                            {
                                data.Missing = false;
                                data.OkFilePath = okFile;
                                data.NgFilePath = ngFile;
                                data.FilePath = okFile ?? ngFile;
                            }
                        }
                        catch
                        {
                            data.Missing = true;
                        }
                    }
                }
                else if (TrySeekCsv(dir, prefix, day, watchMode, out var file))
                {
                    try
                    {
                        if (TryLoadDay(file, data))
                        {
                            data.Missing = false;
                            data.FilePath = file;
                        }
                    }
                    catch
                    {
                        data.Missing = true;
                    }
                }

                map[day] = data;
            }

            lock (_lock)
            {
                _byDay = map;
                _cacheRoot = dir;
                _cachePrefix = prefix;
                _cacheDays = days;
                _lastLoad = now;
                _cacheUseSplitTables = useSplit;
            }
        }

        /// <summary>取指定日期的缓存，不触发磁盘读取。</summary>
        public static DayData? GetDay(DateTime date)
        {
            lock (_lock)
            {
                return _byDay.TryGetValue(date.Date, out var v) ? v : null;
            }
        }

        /// <summary>按时间顺序返回最近 N 天的缓存快照。</summary>
        public static IReadOnlyList<DayData> SnapshotRecent(int days = 7)
        {
            if (days <= 0) days = 7;
            IReadOnlyDictionary<DateTime, DayData> snap;
            lock (_lock) snap = _byDay;

            var list = new List<DayData>(days);
            for (int i = days - 1; i >= 0; i--)
            {
                var d = DateTime.Today.AddDays(-i).Date;
                list.Add(snap.TryGetValue(d, out var v) ? v : new DayData { Date = d, Missing = true });
            }
            return list;
        }

        /// <summary>
        /// 在单表模式下尝试定位指定日期的 CSV；支持 Watch 目录（yyyy-MM-dd 子目录）与根目录模糊匹配。
        /// </summary>
        private static bool TrySeekCsv(string dir, string prefix, DateTime day, bool watchMode, out string? file)
        {
            file = null;
            try
            {
                if (string.IsNullOrWhiteSpace(dir)) return false;

                if (watchMode)
                {
                    var dayDir = Path.Combine(dir, day.ToString("yyyy-MM-dd"));
                    if (Directory.Exists(dayDir))
                    {
                        var expect = Path.Combine(dayDir, $"{prefix}{day:yyyyMMdd}.csv");
                        if (File.Exists(expect)) { file = expect; return true; }
                        var fallback = Directory.GetFiles(dayDir, $"{prefix}{day:yyyyMMdd}*.csv").FirstOrDefault();
                        if (!string.IsNullOrWhiteSpace(fallback)) { file = fallback; return true; }
                    }
                }

                if (!Directory.Exists(dir)) return false;

                var name = Path.Combine(dir, $"{prefix}{day:yyyyMMdd}.csv");
                if (File.Exists(name)) { file = name; return true; }

                var alt = Directory.GetFiles(dir, $"{prefix}{day:yyyyMMdd}*.csv").FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(alt)) { file = alt; return true; }
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 在分表模式下尝试定位 OK/NG 两个 CSV，任意存在即视为可加载。
        /// </summary>
        private static bool TrySeekSplitCsv(string dir, DateTime day, bool watchMode, out string? okFile, out string? ngFile)
        {
            okFile = null; ngFile = null;
            try
            {
                if (string.IsNullOrWhiteSpace(dir)) return false;

                if (watchMode)
                {
                    var dayDir = Path.Combine(dir, day.ToString("yyyy-MM-dd"));
                    okFile = FindSplitFile(dayDir, day, true);
                    ngFile = FindSplitFile(dayDir, day, false);
                }

                if (string.IsNullOrWhiteSpace(okFile))
                    okFile = FindSplitFile(dir, day, true);
                if (string.IsNullOrWhiteSpace(ngFile))
                    ngFile = FindSplitFile(dir, day, false);

                return !string.IsNullOrWhiteSpace(okFile) || !string.IsNullOrWhiteSpace(ngFile);
            }
            catch
            {
                okFile = null; ngFile = null;
                return false;
            }
        }

        private static string? FindSplitFile(string dir, DateTime day, bool isOk)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return null;

                var expect = Path.Combine(dir, $"{day:yyyy-MM-dd}-{(isOk ? "产品记录表" : "抛料记录表")}.csv");
                if (File.Exists(expect)) return expect;
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 读取单表模式的产能 CSV，根据列名自适应 PASS/FAIL/HOUR 列并累加到 24 小时桶。
        /// </summary>
        private static bool TryLoadDay(string file, DayData target)
        {
            Array.Clear(target.HourPass, 0, target.HourPass.Length);
            Array.Clear(target.HourFail, 0, target.HourFail.Length);

            Encoding enc;
            try { enc = Encoding.GetEncoding("GB2312"); }
            catch { enc = new UTF8Encoding(false); }

            var lines = ReadAllLinesShared(file, enc);
            if (lines.Count == 0) return false;

            var header = SmartSplit(lines[0]);
            int idxPass = FindIndex(header, "PASS", "良品", "良率PASS", "OK");
            int idxFail = FindIndex(header, "FAIL", "不良", "NG");
            int idxHour = FindIndex(header, "HOUR", "小时", "时段", "时刻");
            bool hasHour = idxHour >= 0;

            for (int i = 1; i < lines.Count; i++)
            {
                var row = SmartSplit(lines[i]);
                if (row.Length == 0) continue;

                int hour;
                if (hasHour)
                {
                    if (idxHour >= row.Length) continue;
                    hour = ExtractHour(row[idxHour]);
                }
                else
                {
                    hour = i - 1;
                }
                if (hour < 0 || hour > 23) continue;

                int pass = (idxPass >= 0 && idxPass < row.Length) ? ToInt(row[idxPass]) : 0;
                int fail = (idxFail >= 0 && idxFail < row.Length) ? ToInt(row[idxFail]) : 0;

                target.HourPass[hour] += Math.Max(0, pass);
                target.HourFail[hour] += Math.Max(0, fail);
            }

            return true;
        }

        /// <summary>
        /// 读取拆分的 OK/NG 表，按记录时间聚合到 24 小时桶。
        /// </summary>
        private static bool TryLoadDayFromSplit(string? okFile, string? ngFile, DateTime day, DayData target)
        {
            Array.Clear(target.HourPass, 0, target.HourPass.Length);
            Array.Clear(target.HourFail, 0, target.HourFail.Length);

            bool loaded = false;

            if (!string.IsNullOrWhiteSpace(okFile) && File.Exists(okFile))
            {
                AggregateRecordFile(okFile, _splitOkHeaders, day, target.HourPass);
                loaded = true;
            }

            if (!string.IsNullOrWhiteSpace(ngFile) && File.Exists(ngFile))
            {
                AggregateRecordFile(ngFile, _splitNgHeaders, day, target.HourFail);
                loaded = true;
            }

            return loaded;
        }

        /// <summary>
        /// 读取单条 OK/NG 记录表，按时间列归并到指定小时桶，忽略日期不匹配或解析失败的行。
        /// </summary>
        private static void AggregateRecordFile(string file, string[] timeHeaders, DateTime day, int[] bucket)
        {
            Encoding enc;
            try { enc = Encoding.GetEncoding("GB2312"); }
            catch { enc = new UTF8Encoding(false); }

            var lines = ReadAllLinesShared(file, enc);
            if (lines.Count == 0) return;

            var header = SmartSplit(lines[0]);
            int idxTime = FindIndexContains(header, timeHeaders);
            if (idxTime < 0) return;

            for (int i = 1; i < lines.Count; i++)
            {
                var row = SmartSplit(lines[i]);
                if (row.Length == 0) continue;
                if (idxTime >= row.Length) continue;

                if (!TryParseHourWithOptionalDate(row[idxTime], day, out var hour)) continue;
                if (hour < 0 || hour > 23) continue;

                bucket[hour] += 1;
            }
        }

        private static int FindIndexContains(string[] arr, params string[] keys)
        {
            for (int i = 0; i < arr.Length; i++)
            {
                var t = arr[i].Trim().Trim('"');
                foreach (var k in keys)
                {
                    if (t.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)
                        return i;
                }
            }
            return -1;
        }

        private static bool TryParseHourWithOptionalDate(string? text, DateTime day, out int hour)
        {
            hour = -1;
            if (string.IsNullOrWhiteSpace(text)) return false;

            var raw = text.Trim().Trim('"');
            bool hasDate = HasDatePart(raw);

            if (DateTime.TryParse(raw, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var dt) ||
                DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dt))
            {
                if (hasDate && dt.Date != day.Date) return false;
                hour = dt.Hour;
                return hour >= 0 && hour <= 23;
            }

            hour = ExtractHour(raw);
            return hour >= 0 && hour <= 23;
        }

        private static bool HasDatePart(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            return s.Contains("-") || s.Contains("/") || s.IndexOf('T') >= 0 || s.IndexOf('t') >= 0;
        }

        private static List<string> ReadAllLinesShared(string path, Encoding enc)
        {
            var list = new List<string>();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs, enc, detectEncodingFromByteOrderMarks: true);
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    list.Add(line);
            }
            return list;
        }

        private static string[] SmartSplit(string line)
        {
            if (line.Contains(",")) return line.Split(',');
            if (line.Contains("\t")) return line.Split('\t');
            if (line.Contains(";")) return line.Split(';');
            return new[] { line };
        }

        private static int FindIndex(string[] arr, params string[] keys)
        {
            for (int i = 0; i < arr.Length; i++)
            {
                var t = arr[i].Trim().ToUpperInvariant();
                foreach (var k in keys)
                    if (t == k.Trim().ToUpperInvariant()) return i;
            }
            return -1;
        }

        private static int ExtractHour(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return -1;
            s = s.Trim();
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int h) && h >= 0 && h <= 23) return h;
            if (DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.None, out var dt)) return dt.Hour;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt)) return dt.Hour;
            var digits = new string(s.TakeWhile(char.IsDigit).ToArray());
            if (digits.Length > 0 && int.TryParse(digits, out h) && h >= 0 && h <= 23) return h;
            return -1;
        }

        private static int ToInt(string s)
        {
            if (int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return v;
            if (int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out v)) return v;
            return 0;
        }
    }
}

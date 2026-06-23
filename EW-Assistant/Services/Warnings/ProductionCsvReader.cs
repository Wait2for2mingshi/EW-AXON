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
    /// 读取产能/良率数据并按小时聚合，兼容单表与 OK/NG 分表。
    /// </summary>
    public class ProductionCsvReader
    {
        private readonly string _root;

        public ProductionCsvReader(string root = null)
        {
            _root = string.IsNullOrWhiteSpace(root) ? LocalDataConfig.ProductionCsvRoot : root;
        }

        /// <summary>取最近 24 小时产能，便于 Dashboard/看板快速刷新。</summary>
        public IList<ProductionHourRecord> GetLast24HoursProduction(DateTime now)
        {
            return GetProductionRange(now.AddHours(-24), now);
        }

        /// <summary>
        /// 指定时间窗内的小时聚合，自动判定是否使用分表模式。
        /// </summary>
        public IList<ProductionHourRecord> GetProductionRange(DateTime start, DateTime end)
        {
            if (end <= start) return new List<ProductionHourRecord>();

            var useSplit = LocalDataConfig.UseOkNgSplitTables;
            if (useSplit)
            {
                return GetProductionRangeFromSplitTables(start, end);
            }

            var result = new Dictionary<DateTime, ProductionHourRecord>();

            foreach (var file in EnumerateFiles(start, end))
            {
                var dateFromFile = GuessDateFromFile(file);
                foreach (var row in ReadRows(file))
                {
                    if (!row.Hour.HasValue) continue;

                    var hour = row.Hour.Value;
                    if (hour.Year < 1900)
                    {
                        hour = new DateTime(dateFromFile.Year, dateFromFile.Month, dateFromFile.Day, hour.Hour, 0, 0, hour.Kind);
                    }

                    if (hour < start || hour >= end) continue;

                    if (!result.TryGetValue(hour, out var agg))
                    {
                        agg = new ProductionHourRecord
                        {
                            Hour = hour,
                            PlannedOutput = row.PlannedOutput
                        };
                        result.Add(hour, agg);
                    }

                    agg.Pass += row.Pass;
                    agg.Fail += row.Fail;
                    if (row.PlannedOutput.HasValue)
                    {
                        agg.PlannedOutput = row.PlannedOutput;
                    }
                }
            }

            return result.Values.OrderBy(x => x.Hour).ToList();
        }

        /// <summary>实际使用的产能根目录（为空则取配置路径）。</summary>
        public string Root => _root;

        /// <summary>OK/NG 分表模式下的聚合逻辑，按文件名日期逐日扫描。</summary>
        private IList<ProductionHourRecord> GetProductionRangeFromSplitTables(DateTime start, DateTime end)
        {
            var result = new Dictionary<DateTime, ProductionHourRecord>();
            var day = start.Date;
            while (day <= end.Date)
            {
                var okFile = FindSplitFile(day, true);
                var ngFile = FindSplitFile(day, false);

                var passArr = new int[24];
                var failArr = new int[24];

                if (!string.IsNullOrWhiteSpace(okFile) && File.Exists(okFile))
                {
                    AggregateSplitFile(okFile, _splitOkHeaders, day, passArr);
                }
                if (!string.IsNullOrWhiteSpace(ngFile) && File.Exists(ngFile))
                {
                    AggregateSplitFile(ngFile, _splitNgHeaders, day, failArr);
                }

                for (int h = 0; h < 24; h++)
                {
                    var at = new DateTime(day.Year, day.Month, day.Day, h, 0, 0);
                    if (at < start || at >= end) continue;
                    if (passArr[h] <= 0 && failArr[h] <= 0) continue;

                    result[at] = new ProductionHourRecord
                    {
                        Hour = at,
                        Pass = passArr[h],
                        Fail = failArr[h]
                    };
                }

                day = day.AddDays(1);
            }

            return result.Values.OrderBy(x => x.Hour).ToList();
        }

        private IEnumerable<string> EnumerateFiles(DateTime start, DateTime end)
        {
            if (!Directory.Exists(_root)) yield break;

            var dir = new DirectoryInfo(_root);
            foreach (var file in dir.EnumerateFiles("*.csv", SearchOption.TopDirectoryOnly)
                                    .OrderByDescending(f => f.LastWriteTime))
            {
                // 优先用文件名中的日期判断是否落在查询窗口附近，避免一次性扫描过多历史文件。
                var fileDay = GuessDateFromFile(file.FullName).Date;
                if (fileDay < start.Date.AddDays(-1) || fileDay > end.Date.AddDays(1))
                {
                    continue;
                }

                yield return file.FullName;
            }
        }

        /// <summary>读取单个 CSV，按列名自适应 PASS/FAIL/PLAN 列并输出行记录。</summary>
        private IEnumerable<ProductionRow> ReadRows(string path)
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

                var hourIndex = FindIndex(headers, "hour", "小时", "时间", "时段");
                var passIndex = FindIndex(headers, "pass", "良品", "良率pass", "ok");
                var failIndex = FindIndex(headers, "fail", "不良", "报废", "抛料", "ng");
                var planIndex = FindIndex(headers, "plan", "计划", "目标");

                if (hourIndex < 0 && headers.Length > 0) hourIndex = 0;

                while (!reader.EndOfStream)
                {
                    var line = reader.ReadLine();
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var cells = line.Split(new[] { delimiter }, StringSplitOptions.None);
                    if (cells.Length == 0) continue;

                    var row = new ProductionRow
                    {
                        Hour = ParseHour(GetCell(cells, hourIndex)),
                        Pass = ParseInt(GetCell(cells, passIndex)),
                        Fail = ParseInt(GetCell(cells, failIndex)),
                        PlannedOutput = ParseNullableInt(GetCell(cells, planIndex))
                    };

                    yield return row;
                }
            }
        }

        private static string GetCell(string[] cells, int index)
        {
            if (index < 0 || index >= cells.Length) return null;
            return cells[index];
        }

        private static int ParseInt(string value)
        {
            if (int.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) return v;
            return 0;
        }

        private static int? ParseNullableInt(string value)
        {
            if (int.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) return v;
            return null;
        }

        private static DateTime? ParseHour(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var text = value.Trim();

            if (TimeSpan.TryParse(text, out var ts))
            {
                return new DateTime(1, 1, 1, ts.Hours, 0, 0);
            }

            if (text.Length >= 5 && TimeSpan.TryParseExact(text, "hh\\:mm", CultureInfo.InvariantCulture, out ts))
            {
                return new DateTime(1, 1, 1, ts.Hours, 0, 0);
            }

            if (int.TryParse(text, out var hourOnly) && hourOnly >= 0 && hourOnly <= 23)
            {
                return new DateTime(1, 1, 1, hourOnly, 0, 0);
            }

            if (text.Length >= 2 && int.TryParse(text.Substring(0, 2), out hourOnly) && hourOnly >= 0 && hourOnly <= 23)
            {
                return new DateTime(1, 1, 1, hourOnly, 0, 0);
            }

            if (text.Length == 10 && DateTime.TryParseExact(text, "yyyyMMddHH", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            {
                return TruncateToHour(dt);
            }

            if (text.Length == 12 && DateTime.TryParseExact(text, "yyyyMMddHHmm", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
            {
                return TruncateToHour(dt);
            }

            if (DateTime.TryParse(text, out dt))
            {
                return TruncateToHour(dt);
            }

            return null;
        }

        private static string NormalizeHeader(string header)
        {
            if (header == null) return string.Empty;
            return header.Trim().Replace(" ", string.Empty).Replace("\t", string.Empty).ToLowerInvariant();
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

        private static readonly string[] _splitOkHeaders = new[] { "产出时间", "產出時間" };
        private static readonly string[] _splitNgHeaders = new[] { "抛料开始时间", "抛料时间", "抛料開始時間" };

        private string FindSplitFile(DateTime day, bool isOk)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_root)) return null;
                var watchMode = LocalDataConfig.WatchMode;
                var name = $"{day:yyyy-MM-dd}-{(isOk ? "产品记录表" : "抛料记录表")}.csv";

                if (watchMode)
                {
                    var dayDir = Path.Combine(_root, day.ToString("yyyy-MM-dd"));
                    var path = Path.Combine(dayDir, name);
                    if (File.Exists(path)) return path;
                }

                var direct = Path.Combine(_root, name);
                if (File.Exists(direct)) return direct;
            }
            catch
            {
                return null;
            }

            return null;
        }

        private void AggregateSplitFile(string path, string[] timeAliases, DateTime day, int[] bucket)
        {
            try
            {
                if (!File.Exists(path)) return;
                var lines = File.ReadAllLines(path, new UTF8Encoding(false)).ToList();
                if (lines.Count == 0) return;

                var delim = DetectDelimiter(lines[0]);
                var header = lines[0].Split(new[] { delim }, StringSplitOptions.None)
                    .Select(s => (s ?? string.Empty).Trim().Trim('"'))
                    .ToArray();

                var idxTime = FindIndexContains(header, timeAliases);
                if (idxTime < 0) return;

                for (int i = 1; i < lines.Count; i++)
                {
                    if (string.IsNullOrWhiteSpace(lines[i])) continue;
                    var cells = lines[i].Split(new[] { delim }, StringSplitOptions.None);
                    if (idxTime >= cells.Length) continue;

                    if (!TryParseHourWithOptionalDate(cells[idxTime], day, out var hour)) continue;
                    if (hour < 0 || hour > 23) continue;
                    bucket[hour] += 1;
                }
            }
            catch
            {
                // ignore single file errors
            }
        }

        private static bool TryParseHourWithOptionalDate(string text, DateTime day, out int hour)
        {
            hour = -1;
            if (string.IsNullOrWhiteSpace(text)) return false;

            var raw = text.Trim().Trim('"');
            var hasDate = raw.Contains("-") || raw.Contains("/") || raw.IndexOf('T') >= 0 || raw.IndexOf('t') >= 0;

            if (DateTime.TryParse(raw, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var dt)
                || DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dt))
            {
                if (hasDate && dt.Date != day.Date) return false;
                hour = dt.Hour;
                return hour >= 0 && hour <= 23;
            }

            if (int.TryParse(raw, out var h) && h >= 0 && h <= 23)
            {
                hour = h;
                return true;
            }

            if (raw.Length >= 2 && int.TryParse(raw.Substring(0, 2), out h) && h >= 0 && h <= 23)
            {
                hour = h;
                return true;
            }

            return false;
        }

        private static int FindIndexContains(string[] headers, params string[] names)
        {
            for (var i = 0; i < headers.Length; i++)
            {
                var h = (headers[i] ?? string.Empty).Trim().Trim('"');
                foreach (var n in names)
                {
                    if (h.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)
                        return i;
                }
            }
            return -1;
        }

        private static DateTime TruncateToHour(DateTime dt)
        {
            return new DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, 0, 0, dt.Kind);
        }

        private static DateTime GuessDateFromFile(string path)
        {
            var fileName = Path.GetFileNameWithoutExtension(path);
            var match = Regex.Match(fileName ?? string.Empty, @"(20\d{6})");
            if (match.Success)
            {
                if (DateTime.TryParseExact(match.Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    return date;
                }
            }

            return File.GetLastWriteTime(path).Date;
        }

        private sealed class ProductionRow
        {
            public DateTime? Hour { get; set; }
            public int Pass { get; set; }
            public int Fail { get; set; }
            public int? PlannedOutput { get; set; }
        }
    }

    /// <summary>
    /// 每小时产能聚合结果。
    /// </summary>
    public class ProductionHourRecord
    {
        public DateTime Hour { get; set; }
        public int Pass { get; set; }
        public int Fail { get; set; }
        public int? PlannedOutput { get; set; }

        public int Total => Pass + Fail;
        public double Yield => Total <= 0 ? 0d : (double)Pass / Total;
    }
}

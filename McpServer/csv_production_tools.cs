using McpServer;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

// ======== CSV 模型 ========
public class HourStat
{
    public int Hour { get; set; }
    public int Pass { get; set; }
    public int Fail { get; set; }
    public int Total => Pass + Fail;
    public double Yield => Total == 0 ? 0 : Pass * 100.0 / Total;
}

public class DayStats
{
    public DateTime Date { get; set; }
    public int Pass { get; set; }
    public int Fail { get; set; }
    public int Total => Pass + Fail;
    public double Yield => Total == 0 ? 0 : Pass * 100.0 / Total;
    public List<HourStat> Hours { get; set; } = new List<HourStat>();
}

// ======== 读取与缓存 ========

static class CsvProductionRepository
{
    public static string ProductionLogPath { get; set; } = ".";
    public static string DailyFilePattern { get; set; } = "小时产量yyyyMMdd'.csv'";
    public static bool UseOkNgSplitTables { get; set; }
    // 允许逗号/分号/Tab
    public static string[] PossibleDelimiters { get; set; } = new[] { ",", ";", "\t" };

private static readonly ConcurrentDictionary<string, ProdCacheEntry> _cache =
    new ConcurrentDictionary<string, ProdCacheEntry>();
    private static readonly string[] _passAliases = new[] { "pass", "良品", "良率pass" };
    private static readonly string[] _failAliases = new[] { "fail", "不良", "报废", "抛料", "ng" };
    private static readonly string[] _splitOkHeaders = new[] { "产出时间", "產出時間" };
    private static readonly string[] _splitNgHeaders = new[] { "抛料开始时间", "抛料时间", "抛料開始時間" };

    private class ProdCacheEntry
    {
        public DateTime OkMtimeUtc { get; set; }
        public DateTime NgMtimeUtc { get; set; }
        public DayStats Stats { get; set; }
    }


    public static string ResolveDailyCsvPath(DateTime date)
    {
        var fileName = date.ToString(DailyFilePattern, CultureInfo.InvariantCulture);
        return Path.Combine(ProductionLogPath, fileName);
    }

    private static DayStats LoadDayFromSingleFile(DateTime date)
    {
        var csvPath = ResolveDailyCsvPath(date);
        if (!File.Exists(csvPath))
            throw new FileNotFoundException($"未找到当天 CSV：{csvPath}");

        var fi = new FileInfo(csvPath);
        var key = csvPath.ToLowerInvariant();

        ProdCacheEntry c;
        if (_cache.TryGetValue(key, out c) &&
            c.OkMtimeUtc == fi.LastWriteTimeUtc &&
            c.NgMtimeUtc == DateTime.MinValue)
            return c.Stats;

        var ds = ParseCsv(csvPath, date);
        _cache[key] = new ProdCacheEntry
        {
            OkMtimeUtc = fi.LastWriteTimeUtc,
            NgMtimeUtc = DateTime.MinValue,
            Stats = ds
        };
        return ds;
    }

    private static DayStats LoadDayFromSplitTables(DateTime date)
    {
        var okFile = ResolveSplitFile(date, true);
        var ngFile = ResolveSplitFile(date, false);

        if (string.IsNullOrWhiteSpace(okFile) && string.IsNullOrWhiteSpace(ngFile))
            throw new FileNotFoundException($"未找到当天 OK/NG CSV：{ProductionLogPath}");

        var key = $"split:{date:yyyy-MM-dd}";
        var okMtime = SafeGetLastWriteUtc(okFile);
        var ngMtime = SafeGetLastWriteUtc(ngFile);

        ProdCacheEntry c;
        if (_cache.TryGetValue(key, out c) &&
            c.OkMtimeUtc == okMtime &&
            c.NgMtimeUtc == ngMtime)
        {
            return c.Stats;
        }

        var ds = ParseSplitCsv(okFile, ngFile, date);
        _cache[key] = new ProdCacheEntry
        {
            OkMtimeUtc = okMtime,
            NgMtimeUtc = ngMtime,
            Stats = ds
        };
        return ds;
    }

    private static string? ResolveSplitFile(DateTime day, bool isOk)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ProductionLogPath)) return null;

            var expectName = $"{day:yyyy-MM-dd}-{(isOk ? "产品记录表" : "抛料记录表")}.csv";

            var dayDir = Path.Combine(ProductionLogPath, day.ToString("yyyy-MM-dd"));
            var exact = FindExactFile(dayDir, expectName);
            if (!string.IsNullOrWhiteSpace(exact)) return exact;

            exact = FindExactFile(ProductionLogPath, expectName);
            return exact;
        }
        catch
        {
            return null;
        }
    }

    private static string? FindExactFile(string dir, string fileName)
    {
        if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(fileName)) return null;
        try
        {
            var path = Path.Combine(dir, fileName);
            return File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    public static DayStats LoadDay(DateTime date)
    {
        if (UseOkNgSplitTables)
            return LoadDayFromSplitTables(date);

        return LoadDayFromSingleFile(date);
    }

    private static DayStats ParseCsv(string path, DateTime date)
    {
        var lines = ReadAllLinesShared(path);
        if (lines.Count == 0) throw new InvalidDataException("CSV 为空");

        // 自动识别分隔符
        string delim = DetectDelimiter(lines[0]);

        // 拿表头
        var header = SplitLine(lines[0], delim).Select(s => (s ?? "").Trim().Trim('"')).ToArray();

        int colPass = FindCol(header, _passAliases);
        int colFail = FindCol(header, _failAliases);

        if (colPass < 0 || colFail < 0)
            throw new InvalidDataException($"未在表头里识别出 PASS/FAIL 列，表头: {string.Join("|", header)}");

        var day = new DayStats { Date = date };

        // 逐行解析
        for (int i = 1; i < lines.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var cells = SplitLine(lines[i], delim);

            // 忽略“总数”行
            var c0 = cells.Length > 0 ? (cells[0] ?? "").Trim().Trim('"') : "";
            if (c0.Contains("总数", StringComparison.OrdinalIgnoreCase)) continue;

            // 解析小时
            if (!TryParseHour(c0, out int hour)) continue; // 跳过无效行

            int pass = ParseInt(cells, colPass);
            int fail = ParseInt(cells, colFail);

            day.Hours.Add(new HourStat { Hour = hour, Pass = pass, Fail = fail });
        }

        day.Pass = day.Hours.Sum(h => h.Pass);
        day.Fail = day.Hours.Sum(h => h.Fail);
        return day;
    }

    private static DayStats ParseSplitCsv(string okFile, string ngFile, DateTime date)
    {
        var pass = new int[24];
        var fail = new int[24];

        if (!string.IsNullOrWhiteSpace(okFile) && File.Exists(okFile))
            AggregateRecordFile(okFile, _splitOkHeaders, date, pass);

        if (!string.IsNullOrWhiteSpace(ngFile) && File.Exists(ngFile))
            AggregateRecordFile(ngFile, _splitNgHeaders, date, fail);

        var day = new DayStats { Date = date };
        for (int h = 0; h < 24; h++)
        {
            if (pass[h] > 0 || fail[h] > 0)
                day.Hours.Add(new HourStat { Hour = h, Pass = pass[h], Fail = fail[h] });
        }

        day.Pass = pass.Sum();
        day.Fail = fail.Sum();
        return day;
    }

    private static void AggregateRecordFile(string path, string[] timeHeaders, DateTime day, int[] bucket)
    {
        var lines = ReadAllLinesShared(path);
        if (lines.Count == 0) return;

        var delim = DetectDelimiter(lines[0]);
        var header = SplitLine(lines[0], delim).Select(s => (s ?? "").Trim().Trim('"')).ToArray();
        int idxTime = FindColContains(header, timeHeaders);
        if (idxTime < 0) return;

        for (int i = 1; i < lines.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var cells = SplitLine(lines[i], delim);
            if (idxTime >= cells.Length) continue;

            if (!TryParseHourWithOptionalDate(cells[idxTime], day, out int hour)) continue;
            if (hour < 0 || hour > 23) continue;

            bucket[hour] += 1;
        }
    }

    public static bool TryLoadDay(DateTime date, out DayStats stats, out string error)
    {
        stats = null; error = null;
        try { stats = LoadDay(date); return true; }
        catch (Exception ex) { error = ex.Message; return false; }
    }
    private static string DetectDelimiter(string head)
    {
        foreach (var d in PossibleDelimiters)
            if (head.Contains(d)) return d;
        return ","; // fallback
    }

    private static string[] SplitLine(string line, string delim)
    {
        // 简单 Split；Excel 导出的数值列无引号足够用
        return line.Split(new[] { delim }, StringSplitOptions.None);
    }

    private static int FindCol(string[] header, string[] aliases)
    {
        for (int i = 0; i < header.Length; i++)
        {
            var h = (header[i] ?? "").Trim().Trim('"').ToLowerInvariant();
            if (aliases.Any(a => h.Contains(a))) return i;
        }
        return -1;
    }

    private static bool TryParseHour(string cell, out int hour)
    {
        hour = -1;
        if (string.IsNullOrWhiteSpace(cell)) return false;
        cell = cell.Trim().Trim('"');

        // 允许 “0:00” / “00:00” / “0” / “7:00”
        if (TimeSpan.TryParse(cell, out var ts))
        {
            hour = Math.Clamp((int)ts.TotalHours, 0, 23);
            return true;
        }
        if (int.TryParse(cell, out var h))
        {
            hour = Math.Clamp(h, 0, 23);
            return true;
        }
        return false;
    }

    private static int ParseInt(string[] cells, int idx)
    {
        if (idx < 0 || idx >= cells.Length) return 0;
        var s = (cells[idx] ?? "").Trim().Trim('"');
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    private static int FindColContains(string[] header, string[] aliases)
    {
        for (int i = 0; i < header.Length; i++)
        {
            var h = (header[i] ?? "").Trim().Trim('"');
            foreach (var a in aliases)
            {
                if (h.IndexOf(a, StringComparison.OrdinalIgnoreCase) >= 0)
                    return i;
            }
        }
        return -1;
    }

    private static bool TryParseHourWithOptionalDate(string text, DateTime day, out int hour)
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

        return TryParseHour(raw, out hour) && hour >= 0 && hour <= 23;
    }

    private static bool HasDatePart(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        return s.Contains("-") || s.Contains("/") || s.IndexOf('T') >= 0 || s.IndexOf('t') >= 0;
    }

    private static List<string> ReadAllLinesShared(string path)
    {
        var list = new List<string>();
        Encoding enc;
        try { enc = Encoding.GetEncoding("GB2312"); }
        catch { enc = new UTF8Encoding(false); }

        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var sr = new StreamReader(fs, enc, detectEncodingFromByteOrderMarks: true))
        {
            string line;
            while ((line = sr.ReadLine()) != null)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    list.Add(line);
            }
        }
        return list;
    }

    private static DateTime SafeGetLastWriteUtc(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return DateTime.MinValue;
            return File.GetLastWriteTimeUtc(path);
        }
        catch { return DateTime.MinValue; }
    }

}

//1.查询某天的产出汇总（PASS/FAIL/总数/良率）（查询昨天产能）
//2.查询某天按小时明细（可选起止小时）（查询本月17号产能按小时）
//3.快速查询：某天某个时段的产出与良率（查询本月17号早上8点到晚上8点产能）
//4.最近N小时产能/良率汇总（查询最近6小时产能）
//5.跨多天的产能/良率汇总（查询最近2天产能。查询本月17号到19号产能）
// ======== 给 LLM 用的 MCP 工具 ========
[McpServerToolType]
public static class ProdCsvTools
{
    // 1) 查询某天汇总（默认今天）
    [McpServerTool, Description("查询某天的产出汇总（PASS/FAIL/总数/良率）。默认今天。返回 JSON，附带 Markdown 便于直接展示。")]
    public static Task<string> GetProductionSummary(
        [Description("日期，格式yyyy-MM-dd；留空=今天")] string date = null)
    {
        var theDay = ParseDateOrToday(date);
        DayStats ds;
        try { ds = CsvProductionRepository.LoadDay(theDay); }
        catch (Exception ex)
        {
            var err = JsonErr("GetProductionSummary", ex.Message);
            ToolCallLogger.Log(nameof(GetProductionSummary), new { date }, null, ex.ToString());
            return Task.FromResult(err);
        }

        var payload = new
        {
            type = "prod.summary",
            date = theDay.ToString("yyyy-MM-dd"),
            pass = ds.Pass,
            fail = ds.Fail,
            total = ds.Total,
            yield = Math.Round(ds.Yield, 2)
           // md = $"**{theDay:yyyy-MM-dd} 产出汇总**\n- PASS: {ds.Pass}\n- FAIL: {ds.Fail}\n- 总数: {ds.Total}\n- 良率: {ds.Yield:F2}%"
        };
        var res = JsonConvert.SerializeObject(payload);
        ToolCallLogger.Log(nameof(GetProductionSummary), new { date }, res);
        return Task.FromResult(res);
    }

    [McpServerTool, Description("获取最近 7 天的产能汇总，提供周度 KPI、日度明细与告警提示，方便一次性生成周报")]
    public static Task<string> GetWeeklyProductionSummary(
        [Description("周统计的结束日期（含当天），yyyy-MM-dd，默认今天")] string endDate = "")
    {
        var now = DateTime.Now;
        var endInput = string.IsNullOrWhiteSpace(endDate) ? now.ToString("yyyy-MM-dd") : endDate;
        if (!TryParseSmartDate(endInput, now, out var endDay))
        {
            var err = JsonConvert.SerializeObject(new { type = "error", where = "GetWeeklyProductionSummary", message = "endDate 格式应为 yyyy-MM-dd 或 MM-dd/本月dd" });
            ToolCallLogger.Log(nameof(GetWeeklyProductionSummary), new { endDate }, null, "invalid endDate");
            return Task.FromResult(err);
        }

        endDay = endDay.Date;
        var startDay = endDay.AddDays(-6);

        var dayRecords = new List<(DateTime date, int pass, int fail, int total, double yield, string warning)>();
        var warnings = new List<string>();
        int totalPass = 0, totalFail = 0;

        for (var d = startDay; d <= endDay; d = d.AddDays(1))
        {
            if (CsvProductionRepository.TryLoadDay(d, out var ds, out var err))
            {
                var total = ds.Pass + ds.Fail;
                var y = total == 0 ? 0 : Math.Round(ds.Pass * 100.0 / total, 2);
                dayRecords.Add((d, ds.Pass, ds.Fail, total, y, null));
                totalPass += ds.Pass;
                totalFail += ds.Fail;
            }
            else
            {
                var warn = $"{d:yyyy-MM-dd} 缺失CSV：{err}";
                warnings.Add(warn);
                dayRecords.Add((d, 0, 0, 0, 0.0, warn));
            }
        }

        var totals = dayRecords.Select(x => x.total).ToArray();
        var yields = dayRecords.Select(x => x.yield).ToArray();

        var grandTotal = totalPass + totalFail;
        var weekYield = grandTotal == 0 ? 0 : Math.Round(totalPass * 100.0 / grandTotal, 2);
        var avgYield = yields.Length == 0 ? 0 : Math.Round(yields.Average(), 2);
        var medianTotal = Math.Round(CalcMedian(totals), 2);
        var avgTotal = totals.Length == 0 ? 0 : totals.Average();
        var variance = totals.Length == 0 ? 0 : totals.Sum(t => Math.Pow(t - avgTotal, 2)) / (totals.Length == 0 ? 1 : totals.Length);
        var volatility = avgTotal == 0 ? 0 : Math.Round(Math.Sqrt(variance) / avgTotal * 100.0, 2);

        var lastDay = dayRecords.LastOrDefault();
        var lastDayInfo = new
        {
            date = lastDay.date.ToString("yyyy-MM-dd"),
            pass = lastDay.pass,
            fail = lastDay.fail,
            total = lastDay.total,
            yield = lastDay.yield
        };
        var lastDayDelta = new
        {
            total = avgTotal == 0 ? 0 : Math.Round((lastDay.total - avgTotal) * 100.0 / avgTotal, 1),
            yield = Math.Round(lastDay.yield - avgYield, 1)
        };

        var bestDays = dayRecords
            .OrderByDescending(x => x.total)
            .ThenBy(x => x.date)
            .Take(3)
            .Select(x => new { date = x.date.ToString("yyyy-MM-dd"), pass = x.pass, fail = x.fail, total = x.total, yield = x.yield })
            .ToList();

        var worstDays = dayRecords
            .OrderBy(x => x.total)
            .ThenBy(x => x.date)
            .Take(3)
            .Select(x => new { date = x.date.ToString("yyyy-MM-dd"), pass = x.pass, fail = x.fail, total = x.total, yield = x.yield })
            .ToList();

        var payload = new
        {
            type = "prod.weekly.summary",
            startDate = startDay.ToString("yyyy-MM-dd"),
            endDate = endDay.ToString("yyyy-MM-dd"),
            summary = new
            {
                pass = totalPass,
                fail = totalFail,
                total = grandTotal,
                yield = weekYield,
                avgYield,
                medianTotal,
                volatility,
                lastDay = lastDayInfo,
                lastDayDelta,
                bestDays,
                worstDays
            },
            days = dayRecords.Select(x => new
            {
                date = x.date.ToString("yyyy-MM-dd"),
                pass = x.pass,
                fail = x.fail,
                total = x.total,
                yield = x.yield,
                warning = x.warning ?? string.Empty
            }),
            warnings
        };

        var res = JsonConvert.SerializeObject(payload);
        ToolCallLogger.Log(nameof(GetWeeklyProductionSummary), new { endDate, startDate = startDay.ToString("yyyy-MM-dd") }, res, warnings.Any() ? string.Join(" | ", warnings) : null);
        return Task.FromResult(res);
    }

    // 2) 查询某天按小时明细（可选起止小时）
    [McpServerTool, Description("查询某天按小时的 PASS/FAIL 明细（可选 startHour/endHour，范围0-23）。默认今天全时段。")]
    public static Task<string> GetHourlyStats(
        [Description("日期，yyyy-MM-dd；留空=今天")] string date = null,
        [Description("起始小时（0-23，可空）")] int? startHour = null,
        [Description("结束小时（0-23，可空，含头不含尾；不填则到 24）")] int? endHour = null)
    {
        var theDay = ParseDateOrToday(date);
        DayStats ds;
        try { ds = CsvProductionRepository.LoadDay(theDay); }
        catch (Exception ex)
        {
            var err = JsonErr("GetHourlyStats", ex.Message);
            ToolCallLogger.Log(nameof(GetHourlyStats), new { date, startHour, endHour }, null, ex.ToString());
            return Task.FromResult(err);
        }

        int sh = Math.Clamp(startHour ?? 0, 0, 23);
        int eh = Math.Clamp(endHour ?? 24, sh + 1, 24);

        var hours = ds.Hours
            .Where(h => h.Hour >= sh && h.Hour < eh)
            .OrderBy(h => h.Hour)
            .ToList();

        int pass = hours.Sum(h => h.Pass);
        int fail = hours.Sum(h => h.Fail);
        int total = pass + fail;
        double yield = total == 0 ? 0 : pass * 100.0 / total;

        // Markdown 友好展示
        var sb = new StringBuilder();
        sb.AppendLine($"**{theDay:yyyy-MM-dd} {sh:00}:00–{eh:00}:00 小时段明细**");
        foreach (var h in hours)
            sb.AppendLine($"- {h.Hour:00}:00  PASS {h.Pass} | FAIL {h.Fail} | 良率 {h.Yield:F2}%");
        sb.AppendLine($"**小计**：PASS {pass} / FAIL {fail} / 总数 {total} / 良率 {yield:F2}%");

        var payload = new
        {
            type = "prod.hours",
            date = theDay.ToString("yyyy-MM-dd"),
            startHour = sh,
            endHour = eh,
            pass,
            fail,
            total,
            yield = Math.Round(yield, 2),
            hours = hours.Select(h => new { h.Hour, h.Pass, h.Fail, h.Total, yield = Math.Round(h.Yield, 2) })
          //  md = sb.ToString()
        };
        var json = JsonConvert.SerializeObject(payload);
        ToolCallLogger.Log(nameof(GetHourlyStats), new { date, startHour = sh, endHour = eh }, json);
        return Task.FromResult(json);
    }

    // 3) 快速查询：某天某个时段的产出与良率（自然语言最常问）
    [McpServerTool, Description("快捷查询：给定日期（可空=今天）与时间段（例如 10-14 或 10:00-14:00），返回该时段 PASS/FAIL/良率。")]
    public static Task<string> QuickQueryWindow(
        [Description("日期，yyyy-MM-dd；留空=今天")] string date = null,
        [Description("时间段，例如 '10-14' 或 '10:00-14:00'")] string window = null)
    {
        var theDay = ParseDateOrToday(date);
        if (!TryParseWindow(window, out var sh, out var eh))
        {
            var err = JsonErr("QuickQueryWindow", "时间段格式错误，示例：'10-14' 或 '10:00-14:00'");
            ToolCallLogger.Log(nameof(QuickQueryWindow), new { date, window }, null, "window parse error");
            return Task.FromResult(err);
        }

        // 复用明细逻辑
        return GetHourlyStats(theDay.ToString("yyyy-MM-dd"), sh, eh);
    }
    [McpServerTool, Description("最近N小时产能/良率汇总（跨天自动处理）。until默认为当前时间；返回JSON含md用于展示。")]
    public static Task<string> GetSummaryLastHours(
        [Description("最近多少小时，默认 4 小时")] int lastHours = 4,
        [Description("统计截止时间，格式 yyyy-MM-dd HH:mm；留空=现在")] string until = null)
    {
        if (lastHours <= 0) lastHours = 1;
        if (lastHours > 24 * 31) lastHours = 24 * 31; // 上限31天

        DateTime endLocal;
        if (string.IsNullOrWhiteSpace(until))
            endLocal = DateTime.Now;
        else if (!DateTime.TryParse(until, out endLocal))
        {
            var err = JsonConvert.SerializeObject(new { type = "error", where = "GetSummaryLastHours", message = "until 格式应为 yyyy-MM-dd HH:mm" });
            ToolCallLogger.Log(nameof(GetSummaryLastHours), new { lastHours, until }, null, "until parse error");
            return Task.FromResult(err);
        }

        // 对齐到整点：统计区间为 [startHour, endHour) ，不包含 endHour
        var endHour = new DateTime(endLocal.Year, endLocal.Month, endLocal.Day, endLocal.Hour, 0, 0);
        var startHour = endHour.AddHours(-lastHours);

        int sumPass = 0, sumFail = 0;
        var items = new List<object>();
        var warns = new List<string>();

        for (var t = startHour; t < endHour; t = t.AddHours(1))
        {
            if (CsvProductionRepository.TryLoadDay(t.Date, out var ds, out var err))
            {
                var hs = ds.Hours.FirstOrDefault(h => h.Hour == t.Hour);
                int p = hs?.Pass ?? 0, f = hs?.Fail ?? 0;
                items.Add(new
                {
                    date = t.ToString("yyyy-MM-dd"),
                    hour = t.Hour,
                    pass = p,
                    fail = f,
                    total = p + f,
                    yield = (p + f) == 0 ? 0 : Math.Round(p * 100.0 / (p + f), 2)
                });
                sumPass += p; sumFail += f;
            }
            else
            {
                warns.Add($"{t:yyyy-MM-dd} 缺少CSV：{err}");
                items.Add(new { date = t.ToString("yyyy-MM-dd"), hour = t.Hour, pass = 0, fail = 0, total = 0, yield = 0.0 });
            }
        }

        var total = sumPass + sumFail;
        var y = total == 0 ? 0 : Math.Round(sumPass * 100.0 / total, 2);

        var md = new StringBuilder()
            .AppendLine($"**最近 {lastHours} 小时（{startHour:yyyy-MM-dd HH}:00 → {endHour:yyyy-MM-dd HH}:00）汇总**")
            .AppendLine($"- PASS: {sumPass}")
            .AppendLine($"- FAIL: {sumFail}")
            .AppendLine($"- 总数: {total}")
            .AppendLine($"- 良率: {y:F2}%")
            .AppendLine()
            .AppendLine("**逐小时**")
            .ToString();

        foreach (dynamic it in items)
            md += $"- {it.date} {((int)it.hour).ToString("00")}:00  PASS {(int)it.pass} | FAIL {(int)it.fail} | 良率 {((double)it.yield).ToString("F2")}%\n";
        if (warns.Count > 0) md += "\n> 注意：\n> " + string.Join("\n> ", warns) + "\n";

        var payload = new
        {
            type = "prod.last_hours",
            start = startHour.ToString("yyyy-MM-dd HH:00"),
            end = endHour.ToString("yyyy-MM-dd HH:00"),
            lastHours,
            pass = sumPass,
            fail = sumFail,
            total,
            yield = y,
            hours = items,
            warnings = warns
          //  md
        };
        var res = JsonConvert.SerializeObject(payload);
        ToolCallLogger.Log(nameof(GetSummaryLastHours), new { lastHours, until = endLocal.ToString("yyyy-MM-dd HH:mm") }, res, warns.Any() ? string.Join(" | ", warns) : null);
        return Task.FromResult(res);
    }

    [McpServerTool, Description("跨多天的产能/良率汇总。起止日期包含端点；返回每日报表与整体汇总。")]
    public static Task<string> GetProductionRangeSummary(
        [Description("开始日期，yyyy-MM-dd（含）")] string startDate,
        [Description("结束日期，yyyy-MM-dd（含）")] string endDate)
    {
        var now = DateTime.Now;

        if (!TryParseSmartDate(startDate, now, out var d0))
        {
            var err = JsonConvert.SerializeObject(new { type = "error", where = "GetProductionRangeSummary", message = "startDate 格式应为 yyyy-MM-dd 或 MM-dd/本月dd" });
            ToolCallLogger.Log(nameof(GetProductionRangeSummary), new { startDate, endDate }, null, "startDate parse error");
            return Task.FromResult(err);
        }

        if (!TryParseSmartDate(endDate, now, out var d1))
        {
            var err = JsonConvert.SerializeObject(new { type = "error", where = "GetProductionRangeSummary", message = "endDate 格式应为 yyyy-MM-dd 或 MM-dd/本月dd" });
            ToolCallLogger.Log(nameof(GetProductionRangeSummary), new { startDate, endDate }, null, "endDate parse error");
            return Task.FromResult(err);
        }
        if (d1 < d0) { var tmp = d0; d0 = d1; d1 = tmp; }
        d0 = d0.Date; d1 = d1.Date;

        int totalPass = 0, totalFail = 0;
        var days = new List<object>();
        var warns = new List<string>();

        for (var d = d0; d <= d1; d = d.AddDays(1))
        {
            if (CsvProductionRepository.TryLoadDay(d, out var ds, out var err))
            {
                var t = ds.Pass + ds.Fail;
                var y = t == 0 ? 0 : Math.Round(ds.Pass * 100.0 / t, 2);
                days.Add(new { date = d.ToString("yyyy-MM-dd"), pass = ds.Pass, fail = ds.Fail, total = t, yield = y });
                totalPass += ds.Pass; totalFail += ds.Fail;
            }
            else
            {
                warns.Add($"{d:yyyy-MM-dd} 缺少CSV：{err}");
                days.Add(new { date = d.ToString("yyyy-MM-dd"), pass = 0, fail = 0, total = 0, yield = 0.0 });
            }
        }

        var grandTotal = totalPass + totalFail;
        var grandYield = grandTotal == 0 ? 0 : Math.Round(totalPass * 100.0 / grandTotal, 2);

        var sb = new StringBuilder();
        sb.AppendLine($"**{d0:yyyy-MM-dd} ~ {d1:yyyy-MM-dd} 跨天汇总**");
        foreach (dynamic x in days)
            sb.AppendLine($"- {x.date}  PASS {(int)x.pass} | FAIL {(int)x.fail} | 总 {(int)x.total} | 良率 {((double)x.yield).ToString("F2")}%");
        sb.AppendLine($"**总体**：PASS {totalPass} / FAIL {totalFail} / 总数 {grandTotal} / 良率 {grandYield:F2}%");
        if (warns.Count > 0) sb.AppendLine("\n> 注意：\n> " + string.Join("\n> ", warns));

        var payload = new
        {
            type = "prod.range.summary",
            startDate = d0.ToString("yyyy-MM-dd"),
            endDate = d1.ToString("yyyy-MM-dd"),
            pass = totalPass,
            fail = totalFail,
            total = grandTotal,
            yield = grandYield,
            days,
            warnings = warns,
            md = sb.ToString()
        };
        var res = JsonConvert.SerializeObject(payload);
        ToolCallLogger.Log(nameof(GetProductionRangeSummary), new { startDate = d0.ToString("yyyy-MM-dd"), endDate = d1.ToString("yyyy-MM-dd") }, res, warns.Any() ? string.Join(" | ", warns) : null);
        return Task.FromResult(res);
    }
    private static bool TryParseSmartDate(string text, DateTime now, out DateTime day)
    {
        day = default;
        if (string.IsNullOrWhiteSpace(text)) { day = now.Date; return true; }

        text = text.Trim();

        // 完整格式（优先）
        if (DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var dFull))
        { day = dFull.Date; return true; }

        // “MM-dd” / “M-d”
        var m1 = System.Text.RegularExpressions.Regex.Match(text, @"^(?<m>\d{1,2})[-/\.](?<d>\d{1,2})$");
        if (m1.Success)
        {
            int m = int.Parse(m1.Groups["m"].Value);
            int d = int.Parse(m1.Groups["d"].Value);
            try { day = new DateTime(now.Year, m, d); return true; } catch { return false; }
        }

        // 纯“dd”并且问法带“本月/这个月”
        var m2 = System.Text.RegularExpressions.Regex.Match(text, @"^(?<d>\d{1,2})$");
        if (m2.Success)
        {
            int d = int.Parse(m2.Groups["d"].Value);
            // 默认使用“当前月当前年”
            try { day = new DateTime(now.Year, now.Month, d); return true; } catch { return false; }
        }

        // 其它情况最后再交给 TryParse（但不会再改年）
        if (DateTime.TryParse(text, out var dAny)) { day = dAny.Date; return true; }

        return false;
    }


    // ===== 工具内部小函数 =====
    private static DateTime ParseDateOrToday(string date)
    {
        if (string.IsNullOrWhiteSpace(date)) return DateTime.Today;
        if (DateTime.TryParseExact(date.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var d)) return d.Date;
        // 容错：允许 "2025/10/21" 等
        if (DateTime.TryParse(date, out var d2)) return d2.Date;
        throw new ArgumentException("日期格式应为 yyyy-MM-dd");
    }

    private static bool TryParseWindow(string s, out int sh, out int eh)
    {
        sh = 0; eh = 24;
        if (string.IsNullOrWhiteSpace(s)) return true; // 不传=全天
        var raw = s.Replace("：", ":").Replace(" ", "");
        var parts = raw.Split('-', '—', '~');
        if (parts.Length != 2) return false;

        static bool H(string p, out int h)
        {
            h = 0;
            p = p.Trim();
            if (TimeSpan.TryParse(p, out var ts)) { h = Math.Clamp((int)ts.TotalHours, 0, 23); return true; }
            if (int.TryParse(p, out var hi)) { h = Math.Clamp(hi, 0, 23); return true; }
            return false;
        }
        if (!H(parts[0], out sh)) return false;
        if (!H(parts[1], out var end)) return false;
        eh = Math.Clamp(end, sh + 1, 24);
        return true;
    }

    private static string JsonErr(string where, string msg)
    {
        var o = new { type = "error", where, message = msg };
        return JsonConvert.SerializeObject(o);
    }

    private static double CalcMedian(IReadOnlyList<int> values)
    {
        if (values == null || values.Count == 0) return 0;
        var ordered = values.OrderBy(v => v).ToArray();
        int mid = ordered.Length / 2;
        if (ordered.Length % 2 == 1) return ordered[mid];
        return (ordered[mid - 1] + ordered[mid]) / 2.0;
    }
}

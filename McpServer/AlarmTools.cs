// === merged: AlarmCsvRepository + ProdAlarmTools + AlarmCsvTools (net48-friendly) ===
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
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using McpServer;

/* ===================== 公共模型（net48-friendly，避免 record） ===================== */

public class AlarmRecord
{
    public string Code { get; set; }        // 报警代码
    public string Content { get; set; }     // 报警内容
    public string Category { get; set; }    // 报警类别
    public DateTime Start { get; set; }     // 开始时间（结合文件日期）
    public DateTime End { get; set; }       // 结束时间（结合文件日期）
    public int DurationSec { get; set; }    // 报警时长（秒）
    public string SourceFile { get; set; }  // 来源文件
}

public class HourAlarm
{
    public int Hour { get; set; }
    public int Count { get; set; }
    public int DurationSec { get; set; }
    public Dictionary<string, int> CodeCount { get; private set; }
    public Dictionary<string, int> CodeDuration { get; private set; }

    public HourAlarm()
    {
        CodeCount = new Dictionary<string, int>();
        CodeDuration = new Dictionary<string, int>();
    }
}

/* ============================== AlarmCsvRepository =============================== */

public static class AlarmCsvRepository
{
    public static string CsvEncodingName { get; set; } = "GB2312";
    public static bool FlatFileLayout { get; set; }

    private static readonly ConcurrentDictionary<string, Tuple<DateTime, List<AlarmRecord>>> _cache
        = new ConcurrentDictionary<string, Tuple<DateTime, List<AlarmRecord>>>();

    static AlarmCsvRepository()
    {
        // 在 .NET Framework 下通常不需要，但为兼容 .NET Core/WPF 场景加 try 包裹
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { }
    }

    public static bool TryLoadDay(DateTime date, out List<AlarmRecord> recs, out string error)
    {
        recs = null; error = null;
        try { recs = LoadDay(date); return true; }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    public static List<AlarmRecord> LoadDay(DateTime date)
    {
        var file = ResolveFile(date);
        if (file == null)
            throw new FileNotFoundException(string.Format("未找到报警CSV（{0:yyyy-MM-dd}）：{1}", date, AlarmCsvTools.WarmLogPath));

        var fi = new FileInfo(file);
        var key = fi.FullName.ToLowerInvariant();
        Tuple<DateTime, List<AlarmRecord>> c;
        if (_cache.TryGetValue(key, out c) && c.Item1 == fi.LastWriteTimeUtc)
            return c.Item2;

        var recs = ParseCsv(file, date.Date);
        _cache[key] = Tuple.Create(fi.LastWriteTimeUtc, recs);
        return recs;
    }

    public static HourAlarm[] GetHourlyAggregation(DateTime date)
    {
        var recs = LoadDay(date);
        var hours = Enumerable.Range(0, 24).Select(h => new HourAlarm { Hour = h }).ToArray();

        // 每小时：全量区间列表（相对本小时起点的 [startSec, endSec)）
        var hourAll = new List<Tuple<int, int>>[24];
        var hourByCode = new Dictionary<string, List<Tuple<int, int>>>[24];
        for (int i = 0; i < 24; i++)
        {
            hourAll[i] = new List<Tuple<int, int>>();
            hourByCode[i] = new Dictionary<string, List<Tuple<int, int>>>(StringComparer.OrdinalIgnoreCase);
        }

        foreach (var r in recs)
        {
            // 裁剪到当天
            var dayStart = date.Date;
            var dayEnd = dayStart.AddDays(1);
            var s = r.Start < dayStart ? dayStart : r.Start;
            var e = r.End > dayEnd ? dayEnd : r.End;
            if (e <= s) continue;

            var codeKey = r.Code ?? "";

            // 拆到每个小时，并记录相对该小时起点的偏移
            var cur = s;
            while (cur < e)
            {
                var hourStart = new DateTime(cur.Year, cur.Month, cur.Day, cur.Hour, 0, 0);
                var hourEnd = hourStart.AddHours(1);
                var segEnd = e < hourEnd ? e : hourEnd;

                int a = (int)Math.Max(0, (cur - hourStart).TotalSeconds);   // [a, b)
                int b = (int)Math.Max(0, (segEnd - hourStart).TotalSeconds);
                if (b > a)
                {
                    int h = cur.Hour;

                    // 条数：在该小时内出现一次就+1（跨多小时会在各自小时+1）
                    hours[h].Count += 1;

                    // CodeCount：同上
                    int cc;
                    if (!hours[h].CodeCount.TryGetValue(codeKey, out cc)) cc = 0;
                    hours[h].CodeCount[codeKey] = cc + 1;

                    // 记录区间到“全量并集”和“按代码并集”
                    hourAll[h].Add(Tuple.Create(a, b));

                    List<Tuple<int, int>> list;
                    if (!hourByCode[h].TryGetValue(codeKey, out list))
                    {
                        list = new List<Tuple<int, int>>();
                        hourByCode[h][codeKey] = list;
                    }
                    list.Add(Tuple.Create(a, b));
                }

                cur = segEnd;
            }
        }

        // 计算并集长度（≤3600），写回 DurationSec / CodeDuration
        for (int h = 0; h < 24; h++)
        {
            hours[h].DurationSec = UnionSeconds(hourAll[h]);

            foreach (var kv in hourByCode[h])
            {
                var sec = UnionSeconds(kv.Value);
                hours[h].CodeDuration[kv.Key] = sec;
            }
        }

        return hours;
    }

    // 计算若干 [start,end) 区间的并集总秒数（0..3600）
    private static int UnionSeconds(List<Tuple<int, int>> intervals)
    {
        if (intervals == null || intervals.Count == 0) return 0;

        // 规范到 [0,3600]，并按起点排序
        var list = new List<Tuple<int, int>>(intervals.Count);
        for (int i = 0; i < intervals.Count; i++)
        {
            var a = Math.Max(0, Math.Min(3600, intervals[i].Item1));
            var b = Math.Max(0, Math.Min(3600, intervals[i].Item2));
            if (b > a) list.Add(Tuple.Create(a, b));
        }
        if (list.Count == 0) return 0;

        list.Sort((x, y) =>
        {
            int c = x.Item1.CompareTo(y.Item1);
            return c != 0 ? c : x.Item2.CompareTo(y.Item2);
        });

        int total = 0;
        int cs = list[0].Item1, ce = list[0].Item2;

        for (int i = 1; i < list.Count; i++)
        {
            int s = list[i].Item1, e = list[i].Item2;
            if (s <= ce)
            {
                if (e > ce) ce = e;
            }
            else
            {
                total += (ce - cs);
                cs = s; ce = e;
            }
        }
        total += (ce - cs);

        if (total < 0) total = 0;
        if (total > 3600) total = 3600;
        return total;
    }


    // ---------- 内部：读取与解析 ----------

    private static string ResolveFile(DateTime date)
    {
        if (!Directory.Exists(AlarmCsvTools.WarmLogPath)) return null;

        if (FlatFileLayout || AlarmCsvTools.FlatFileLayout)
        {
            var watchFile = ResolveWatchModeFile(AlarmCsvTools.WarmLogPath, date);
            if (!string.IsNullOrWhiteSpace(watchFile)) return watchFile;
        }

        Func<string, string> pickDate = path =>
        {
            var dt = ExtractDateFromFileName(path);
            if (!dt.HasValue) dt = File.GetLastWriteTime(path).Date;
            return dt.Value.ToString("yyyy-MM-dd");
        };

        var all = Directory.EnumerateFiles(AlarmCsvTools.WarmLogPath, "*.csv", SearchOption.TopDirectoryOnly).ToList();
        var exact = all.FirstOrDefault(p => pickDate(p) == date.ToString("yyyy-MM-dd"));
        if (exact != null) return exact;

        return all
            .OrderByDescending(p => File.GetLastWriteTime(p))
            .FirstOrDefault(p => ExtractDateFromFileName(p).HasValue && ExtractDateFromFileName(p).Value.Date == date.Date);
    }

    // flatFileLayout=true 时，报警文件按日期分目录存放，这里优先匹配当日目录下的目标文件。
    internal static string ResolveWatchModeFile(string root, DateTime date)
    {
        var dayDir = Path.Combine(root, date.ToString("yyyy-MM-dd"));
        if (!Directory.Exists(dayDir)) return null;

        var expected = new[]
        {
            Path.Combine(dayDir, $"{date:yyyy-MM-dd}-报警记录表.csv"),
            Path.Combine(dayDir, $"{date:yyyy-MM-dd}.csv")
        };
        foreach (var path in expected)
        {
            if (File.Exists(path)) return path;
        }

        var di = new DirectoryInfo(dayDir);
        var exact = di.EnumerateFiles("*.csv", SearchOption.TopDirectoryOnly)
                      .Where(f => MatchesDayFileName(f.Name, date))
                      .OrderByDescending(f => f.LastWriteTime)
                      .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                      .FirstOrDefault();
        if (exact != null) return exact.FullName;

        var fallback = di.EnumerateFiles("*.csv", SearchOption.TopDirectoryOnly)
                         .Where(f =>
                         {
                             var dt = ExtractDateFromFileName(f.FullName);
                             return dt.HasValue && dt.Value.Date == date.Date;
                         })
                         .OrderByDescending(f => f.LastWriteTime)
                         .FirstOrDefault();
        return fallback != null ? fallback.FullName : null;
    }

    private static List<AlarmRecord> ParseCsv(string path, DateTime fileDate)
    {
        var lines = ReadAllLinesWithEncoding(path, CsvEncodingName);
        if (lines.Count == 0) return new List<AlarmRecord>();

        var header = SplitCsvLine(lines[0]).Select(NormalizeCell).ToArray();
        int idxCode = IndexOf(header, "报警代码", "代码", "Code", "报警编号", "错误编码", "ErrorCode");
        int idxCont = IndexOf(header, "报警内容", "内容", "Desc", "描述", "Content", "错误信息");
        int idxCate = IndexOf(header, "报警类别", "类别", "Category", "错误类型");
        int idxStart = IndexOf(header, "开始时间", "Start", "开始", "StartTime", "起始时间");
        int idxEnd = IndexOf(header, "结束时间", "End", "结束", "EndTime");
        int idxDur = IndexOf(header, "报警时间(s)", "报警时长(s)", "持续时间(s)", "Seconds", "维修耗时");

        var recs = new List<AlarmRecord>();
        for (int i = 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;

            var cells = SplitCsvLine(line);
            Func<int, string> get = idx => NormalizeCell(idx >= 0 && idx < cells.Length ? cells[idx] : "");

            var code = get(idxCode);
            var cont = get(idxCont);
            var cate = get(idxCate);

            var start = ParseDateTimeCell(get(idxStart), fileDate);
            var end = ParseDateTimeCell(get(idxEnd), fileDate);
            var durSec = ParseDurationSeconds(get(idxDur));

            if (!start.HasValue && end.HasValue && durSec.HasValue)
                start = end.Value.AddSeconds(-durSec.Value);
            if (!start.HasValue) continue;

            if (!end.HasValue && durSec.HasValue)
                end = start.Value.AddSeconds(durSec.Value);

            if (end.HasValue && end.Value < start.Value)
            {
                if (durSec.HasValue && durSec.Value > 0)
                    end = start.Value.AddSeconds(durSec.Value);
                else
                    end = start.Value;
            }

            int useSec;
            if (durSec.HasValue)
                useSec = Math.Max(0, durSec.Value);
            else if (end.HasValue)
                useSec = (int)Math.Max(0, (end.Value - start.Value).TotalSeconds);
            else
                continue;

            if (!end.HasValue) end = start.Value.AddSeconds(useSec);

            var ar = new AlarmRecord
            {
                Code = code,
                Content = cont,
                Category = cate,
                Start = start.Value,
                End = end.HasValue ? end.Value : start.Value,
                DurationSec = useSec,
                SourceFile = Path.GetFileName(path)
            };
            recs.Add(ar);
        }
        return recs;
    }

    private static List<string> ReadAllLinesWithEncoding(string path, string encName)
    {
        var list = new List<string>();
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var sr = new StreamReader(fs, Encoding.GetEncoding(encName), true))
        {
            string ln;
            while ((ln = sr.ReadLine()) != null) list.Add(ln);
        }
        if (list.Count > 0 && list[0].IndexOf('�') >= 0)
        {
            list.Clear();
            using (var fs2 = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr2 = new StreamReader(fs2, Encoding.GetEncoding("GB18030"), true))
            {
                string ln2;
                while ((ln2 = sr2.ReadLine()) != null) list.Add(ln2);
            }
        }
        return list;
    }

    private static string[] SplitCsvLine(string line)
    {
        var result = new List<string>();
        var cur = new StringBuilder();
        bool inQ = false;
        for (int i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inQ && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                else inQ = !inQ;
            }
            else if ((ch == ',' || ch == ';' || ch == '\t') && !inQ)
            {
                result.Add(cur.ToString()); cur.Clear();
            }
            else cur.Append(ch);
        }
        result.Add(cur.ToString());
        return result.ToArray();
    }

    /// <summary>去除单双/中英文引号与多余空白，避免表头或单元格被引号包裹导致匹配失败。</summary>
    private static string NormalizeCell(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var t = s.Trim();
        if (t.Length >= 2)
        {
            var first = t[0];
            var last = t[t.Length - 1];
            if ((first == '"' && last == '"') || (first == '“' && last == '”') || (first == '\'' && last == '\''))
            {
                t = t.Substring(1, t.Length - 2);
                t = t.Replace("\"\"", "\"");
            }
        }
        return t.Trim();
    }

    private static int IndexOf(string[] header, params string[] keys)
    {
        for (int i = 0; i < header.Length; i++)
        {
            var h = NormalizeCell(header[i]);
            foreach (var k in keys)
                if (h.Equals(NormalizeCell(k), StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    private static bool TryParseTime(string s, out TimeSpan ts)
    {
        ts = default(TimeSpan);
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim().Replace("点", ":").Replace("分", ":").Replace("秒", "");
        s = s.Replace("-", ":").Replace("：", ":");

        var p = s.Split(new[] { ':' }, StringSplitOptions.RemoveEmptyEntries);
        int h, m;
        if (p.Length == 1 && int.TryParse(p[0], out h))
            return TimeSpan.TryParse(string.Format("{0:D2}:00:00", h), out ts);
        if (p.Length == 2 && int.TryParse(p[0], out h) && int.TryParse(p[1], out m))
            return TimeSpan.TryParse(string.Format("{0:D2}:{1:D2}:00", h, m), out ts);
        if (TimeSpan.TryParse(s, out ts)) return true;
        DateTime dt;
        if (DateTime.TryParse(s, out dt)) { ts = dt.TimeOfDay; return true; }
        return false;
    }

    private static DateTime? ParseDateTimeCell(string s, DateTime fileDate)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();

        DateTime dt;
        if (ContainsDateToken(s) && DateTime.TryParse(s, out dt)) return dt;

        TimeSpan ts;
        if (TryParseTime(s, out ts)) return fileDate + ts;

        if (DateTime.TryParse(s, out dt)) return dt;
        return null;
    }

    private static int? ParseDurationSeconds(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();

        int iv;
        if (int.TryParse(s, out iv) && iv >= 0) return iv;

        double dv;
        if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out dv) && dv >= 0)
            return (int)Math.Round(dv);

        TimeSpan ts;
        if (TimeSpan.TryParse(s, out ts)) return (int)Math.Max(0, ts.TotalSeconds);

        return null;
    }

    private static bool ContainsDateToken(string s)
    {
        return Regex.IsMatch(s, @"\d{4}[-/\.年]?\d{1,2}[-/\.月]?\d{1,2}");
    }

    private static DateTime? ExtractDateFromFileName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path) ?? "";
        var m = Regex.Match(name, @"(?<y>\d{4})[-_/\.]?(?<mo>\d{1,2})[-_/\.]?(?<d>\d{1,2})");
        if (m.Success)
        {
            int y, mo, d;
            if (int.TryParse(m.Groups["y"].Value, out y) &&
                int.TryParse(m.Groups["mo"].Value, out mo) &&
                int.TryParse(m.Groups["d"].Value, out d))
            {
                try { return new DateTime(y, mo, d); } catch { }
            }
        }
        return null;
    }

    private static bool MatchesDayFileName(string fileName, DateTime day)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        var withSuffix = $"{day:yyyy-MM-dd}-报警记录表.csv";
        var plain = $"{day:yyyy-MM-dd}.csv";
        return string.Equals(fileName, withSuffix, StringComparison.OrdinalIgnoreCase)
               || string.Equals(fileName, plain, StringComparison.OrdinalIgnoreCase);
    }

    /// 拆分一条报警在“天内各小时段”的占用（hour, seconds）
    private static IEnumerable<Tuple<int, int>> SplitIntoHours(AlarmRecord r, DateTime day)
    {
        var s = r.Start < day ? day : r.Start;
        var e = r.End > day.AddDays(1) ? day.AddDays(1) : r.End;
        if (e <= s) yield break;

        var cur = s;
        while (cur < e)
        {
            var bucketEnd = new DateTime(cur.Year, cur.Month, cur.Day, cur.Hour, 0, 0).AddHours(1);
            var segEnd = (e < bucketEnd) ? e : bucketEnd;
            int secs = (int)Math.Max(0, (segEnd - cur).TotalSeconds);
            if (secs > 0) yield return Tuple.Create(cur.Hour, secs);
            cur = segEnd;
        }
    }
}

/* ================================ ProdAlarmTools ================================ */

[McpServerToolType]
public static class ProdAlarmTools
{
    // 辅助：Clamp
    private static int ClampInt(int v, int lo, int hi) { if (v < lo) return lo; if (v > hi) return hi; return v; }

    // ---------- 1) 单日联表 ----------
    [McpServerTool, Description("单日联表：按小时输出 PASS/FAIL/良率 + 报警条数/报警秒数 + Top1报警代码（附内容）。")]
    public static Task<string> GetHourlyProdWithAlarms(
     [Description("日期，yyyy-MM-dd；也支持 MM-dd / dd（默认本月）；留空=今天")] string date = null,
     [Description("起始小时（0-23，可空）")] int? startHour = null,
     [Description("结束小时（1-24，可空，含头不含尾）")] int? endHour = null)
    {
        DateTime theDay;
        try { theDay = ParseSmartDateOrToday(date); }
        catch (Exception ex)
        {
            var err = JsonErr("GetHourlyProdWithAlarms", ex.Message);
            ToolCallLogger.Log(nameof(GetHourlyProdWithAlarms), new { date, startHour, endHour }, null, ex.ToString());
            return Task.FromResult(err);
        }

        List<AlarmRecord> alarms;
        string err2;
        if (!AlarmCsvRepository.TryLoadDay(theDay, out alarms, out err2))
            alarms = new List<AlarmRecord>(); // 没报警文件也能返回产能

        DayStats ds;
        string err1;
        if (!CsvProductionRepository.TryLoadDay(theDay, out ds, out err1))
        {
            var err = JsonErr("GetHourlyProdWithAlarms", err1);
            ToolCallLogger.Log(nameof(GetHourlyProdWithAlarms), new { date, startHour, endHour }, null, err1);
            return Task.FromResult(err);
        }

        var agg = AlarmCsvRepository.GetHourlyAggregation(theDay) ?? new HourAlarm[24];

        int sh = ClampInt(startHour.HasValue ? startHour.Value : 0, 0, 23);
        int eh = ClampInt(endHour.HasValue ? endHour.Value : 24, sh + 1, 24);

        // 仅做一个轻量映射：code -> 第一条非空 content（够用、快、输出小）
        var code2content = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < alarms.Count; i++)
        {
            var a = alarms[i];
            if (a == null) continue;

            var c = (a.Code ?? "").Trim();
            var t = (a.Content ?? "").Trim();
            if (c.Length == 0 || t.Length == 0) continue;

            if (!code2content.ContainsKey(c))
                code2content[c] = t;
        }

        var items = new List<object>();
        for (int h = sh; h < eh; h++)
        {
            var ph = ds.Hours.FirstOrDefault(x => x.Hour == h) ?? new HourStat { Hour = h };
            var ah = (h >= 0 && h < agg.Length && agg[h] != null) ? agg[h] : new HourAlarm { Hour = h };

            var top1 = (ah.CodeDuration != null && ah.CodeDuration.Count > 0)
                ? ah.CodeDuration.OrderByDescending(kv => kv.Value).First()
                : new KeyValuePair<string, int>("", 0);

            var topCode = (top1.Key ?? "").Trim();

            string topContent = "";
            if (topCode.Length > 0)
            {
                string hit;
                if (code2content.TryGetValue(topCode, out hit))
                    topContent = hit ?? "";
            }

            items.Add(new
            {
                hour = h,
                pass = ph.Pass,
                fail = ph.Fail,
                total = ph.Total,
                yield = Math.Round(ph.Yield, 2),

                alarmCount = ah.Count,
                alarmDurationSec = ah.DurationSec,

                topAlarmCode = topCode,
                topAlarmSeconds = top1.Value,
                topAlarmContent = string.IsNullOrEmpty(topContent) ? "" : topContent
            });
        }

        var payload = new
        {
            type = "prod.hourly.with.alarms",
            date = theDay.ToString("yyyy-MM-dd"),
            startHour = sh,
            endHour = eh,
            items = items
        };

        var res = JsonConvert.SerializeObject(payload);
        ToolCallLogger.Log(nameof(GetHourlyProdWithAlarms), new { date = theDay.ToString("yyyy-MM-dd"), startHour = sh, endHour = eh }, res);
        return Task.FromResult(res);
    }




    // ---------- 2) 跨天影响分析（含逐小时明细 rows） ----------
    [McpServerTool, Description("跨天影响分析：计算报警秒数与产量/良率的相关性，并输出低良率小时的Top报警（已去重并集，绝不钳位）。可加时段窗口，如 10-14。")]
    public static Task<string> GetAlarmImpactSummary(
        [Description("开始日期，yyyy-MM-dd / MM-dd / dd")] string startDate,
        [Description("结束日期，yyyy-MM-dd / MM-dd / dd")] string endDate,
        [Description("时段窗口，如 '10-14' 或 '10:00-14:00'；留空=全天")] string window = null,
        [Description("低良率阈值%，默认95")] double lowYieldThreshold = 95,
        [Description("兼容参数：已忽略，不再钳位")] bool capHourlySeconds = false)
    {
        DateTime d0, d1; int sh, eh;

        try
        {
            var range = ParseSmartRange(startDate, endDate);
            d0 = range.Item1; d1 = range.Item2;
        }
        catch (Exception ex)
        {
            var err = JsonErr("GetAlarmImpactSummary", ex.Message);
            ToolCallLogger.Log(nameof(GetAlarmImpactSummary), new { startDate, endDate, window, lowYieldThreshold }, null, ex.ToString());
            return Task.FromResult(err);
        }

        if (!TryParseWindow(window, out sh, out eh)) { sh = 0; eh = 24; }

        var warnings = new List<string>(); // 仅记录“缺文件”等必要提示

        var xs_alarm = new List<double>(); // 每小时报警秒数（已并集去重，≤3600）
        var ys_total = new List<double>(); // 每小时总产出
        var ys_yield = new List<double>(); // 每小时良率

        var lowYieldHours = new List<Tuple<DateTime, int>>();
        var lowRows = new List<object>();
        var codeDurOnLow = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var hourlyLoadSec = new int[24];
        var byDay = new List<object>();

        // ===== 新增：周度汇总累加器 =====
        int weekAlarmSeconds = 0;   // 周度去重后的报警总秒数（设备真实挂报警时间）

        for (var d = d0; d <= d1; d = d.AddDays(1))
        {
            DayStats ds;
            string e1;
            if (!CsvProductionRepository.TryLoadDay(d, out ds, out e1))
            {
                warnings.Add(string.Format("{0:yyyy-MM-dd} 产能缺失：{1}", d, e1));
                continue;
            }

            // 即使报警文件缺失，也依然返回产能侧数据
            List<AlarmRecord> _;
            string e2;
            if (!AlarmCsvRepository.TryLoadDay(d, out _, out e2))
                warnings.Add(string.Format("{0:yyyy-MM-dd} 报警缺失：{1}", d, e2));

            var agg = AlarmCsvRepository.GetHourlyAggregation(d) ?? new HourAlarm[24];

            // 逐小时采样
            for (int h = sh; h < eh; h++)
            {
                var ph = ds.Hours.FirstOrDefault(x => x.Hour == h) ?? new HourStat { Hour = h };
                var ah = (h >= 0 && h < agg.Length && agg[h] != null) ? agg[h] : new HourAlarm { Hour = h };

                // 这里的 sec 已是并集去重结果，天然 ≤3600，无需钳位
                int sec = ah.DurationSec;

                xs_alarm.Add(sec);
                ys_total.Add(ph.Total);
                ys_yield.Add(ph.Yield);

                if (h >= 0 && h < 24) hourlyLoadSec[h] = hourlyLoadSec[h] + sec;

                if (ph.Total > 0 && ph.Yield < lowYieldThreshold)
                {
                    lowYieldHours.Add(Tuple.Create(d, h));

                    var top1 = (ah.CodeDuration != null && ah.CodeDuration.Count > 0)
                        ? ah.CodeDuration.OrderByDescending(kv => kv.Value).First()
                        : new KeyValuePair<string, int>("", 0);

                    lowRows.Add(new
                    {
                        date = d.ToString("yyyy-MM-dd"),
                        hour = h,
                        total = ph.Total,
                        yield = Math.Round(ph.Yield, 2),
                        alarmSeconds = sec,
                        alarmCount = ah.Count,
                        topAlarmCode = top1.Key ?? "",
                        topAlarmSeconds = top1.Value
                    });

                    if (ah.CodeDuration != null)
                    {
                        foreach (var kv in ah.CodeDuration)
                        {
                            int cur;
                            codeDurOnLow[kv.Key] = (codeDurOnLow.TryGetValue(kv.Key, out cur) ? cur : 0) + kv.Value;
                        }
                    }
                }
            }

            // 日汇总：直接按“已去重的小时秒数”求和
            int sumSec = agg.Where(a => a != null).Sum(a => a.DurationSec);
            int sumCnt = agg.Where(a => a != null).Sum(a => a.Count);

            // 周度影响时长累加（去重后）
            weekAlarmSeconds += sumSec;

            byDay.Add(new
            {
                date = d.ToString("yyyy-MM-dd"),
                pass = ds.Pass,
                fail = ds.Fail,
                total = ds.Total,
                yield = Math.Round(ds.Yield, 2),
                alarmSeconds = sumSec,
                alarmCount = sumCnt
            });
        }

        var r_total = Pearson(xs_alarm, ys_total);
        var r_yield = Pearson(xs_alarm, ys_yield);

        var topOnLow = codeDurOnLow
            .OrderByDescending(kv => kv.Value)
            .Take(20)
            .Select(kv => new { code = kv.Key, seconds = kv.Value })
            .ToList();

        // 统计活跃小时数 & 低良率小时数
        int activeHours = 0;
        for (int i = 0; i < hourlyLoadSec.Length; i++)
        {
            if (hourlyLoadSec[i] > 0) activeHours++;
        }
        int lowYieldRowCount = lowRows.Count;

        var payload = new
        {
            type = "prod.alarm.impact",
            range = new { start = d0.ToString("yyyy-MM-dd"), end = d1.ToString("yyyy-MM-dd") },
            window = string.Format("{0:00}:00-{1:00}:00", sh, eh),

            // ===== 新增：周度汇总，专门给周报用 =====
            weeklyTotals = new
            {
                alarmSeconds = weekAlarmSeconds,   // 周度去重后的报警总秒数
                activeHours = activeHours,         // 有报警秒数的小时数
                lowYieldRowCount = lowYieldRowCount// 低良率小时条数（rows.Count）
            },

            correlation = new
            {
                alarmSeconds_vs_total = r_total,
                alarmSeconds_vs_yield = r_yield
            },
            hourlyLoadSec = hourlyLoadSec,
            lowYield = new
            {
                threshold = lowYieldThreshold,
                hours = lowYieldHours.Select(x => string.Format("{0:yyyy-MM-dd} {1:00}:00", x.Item1, x.Item2)).ToList(),
                rows = lowRows,
                topAlarmCodes = topOnLow
            },
            byDay = byDay,
            warnings = warnings // 仅保留缺数据等必要信息
        };

        var res = JsonConvert.SerializeObject(payload);
        ToolCallLogger.Log(nameof(GetAlarmImpactSummary), new { startDate = d0.ToString("yyyy-MM-dd"), endDate = d1.ToString("yyyy-MM-dd"), window, lowYieldThreshold }, res, warnings.Any() ? string.Join(" | ", warnings) : null);
        return Task.FromResult(res);
    }



    // ---------- 3) 低良率聚焦 ----------
    [McpServerTool, Description("在日期范围内筛选良率低于阈值%的小时，汇总对应报警Top。")]
    public static Task<string> GetTopAlarmsDuringLowYield(
        [Description("开始日期，yyyy-MM-dd / MM-dd / dd")] string startDate,
        [Description("结束日期，yyyy-MM-dd / MM-dd / dd")] string endDate,
        [Description("良率阈值%，默认95")] double threshold = 95,
        [Description("时段窗口，如 '10-14'；留空=全天")] string window = null)
    {
        DateTime d0, d1;
        try
        {
            var range = ParseSmartRange(startDate, endDate);
            d0 = range.Item1; d1 = range.Item2;
        }
        catch (Exception ex)
        {
            var err = JsonErr("GetTopAlarmsDuringLowYield", ex.Message);
            ToolCallLogger.Log(nameof(GetTopAlarmsDuringLowYield), new { startDate, endDate, threshold, window }, null, ex.ToString());
            return Task.FromResult(err);
        }

        int sh, eh;
        if (!TryParseWindow(window, out sh, out eh)) { sh = 0; eh = 24; }

        var codeDur = new Dictionary<string, int>();
        var codeCnt = new Dictionary<string, int>();
        var rows = new List<object>();
        var warns = new List<string>();

        for (var d = d0; d <= d1; d = d.AddDays(1))
        {
            DayStats ds;
            string e1;
            if (!CsvProductionRepository.TryLoadDay(d, out ds, out e1))
            { warns.Add(string.Format("{0:yyyy-MM-dd} 产能缺失：{1}", d, e1)); continue; }

            List<AlarmRecord> al;
            string e2;
            if (!AlarmCsvRepository.TryLoadDay(d, out al, out e2))
            { warns.Add(string.Format("{0:yyyy-MM-dd} 报警缺失：{1}", d, e2)); al = new List<AlarmRecord>(); }

            var agg = AlarmCsvRepository.GetHourlyAggregation(d);

            for (int h = sh; h < eh; h++)
            {
                var ph = ds.Hours.FirstOrDefault(x => x.Hour == h) ?? new HourStat { Hour = h };
                if (ph.Total == 0 || ph.Yield >= threshold) continue;

                var ah = agg[h];

                foreach (var kv in ah.CodeDuration)
                {
                    int v;
                    codeDur[kv.Key] = (codeDur.TryGetValue(kv.Key, out v) ? v : 0) + kv.Value;
                }
                foreach (var kv in ah.CodeCount)
                {
                    int v2;
                    codeCnt[kv.Key] = (codeCnt.TryGetValue(kv.Key, out v2) ? v2 : 0) + kv.Value;
                }

                rows.Add(new
                {
                    date = d.ToString("yyyy-MM-dd"),
                    hour = h,
                    pass = ph.Pass,
                    fail = ph.Fail,
                    total = ph.Total,
                    yield = Math.Round(ph.Yield, 2),
                    alarmSeconds = ah.DurationSec,
                    alarmCount = ah.Count
                });
            }
        }

        var top = codeDur
            .OrderByDescending(kv => kv.Value)
            .Take(20)
            .Select(kv => new
            {
                code = kv.Key,
                seconds = kv.Value,
                count = (codeCnt.ContainsKey(kv.Key) ? codeCnt[kv.Key] : 0)
            })
            .ToList();

        var payload = new
        {
            type = "prod.low_yield.top_alarms",
            range = new { start = d0.ToString("yyyy-MM-dd"), end = d1.ToString("yyyy-MM-dd") },
            window = string.Format("{0:00}:00-{1:00}:00", sh, eh),
            threshold = threshold,
            rows = rows,
            top = top,
            warnings = warns
        };
        var res = JsonConvert.SerializeObject(payload);
        ToolCallLogger.Log(nameof(GetTopAlarmsDuringLowYield), new { startDate = d0.ToString("yyyy-MM-dd"), endDate = d1.ToString("yyyy-MM-dd"), threshold, window }, res, warns.Any() ? string.Join(" | ", warns) : null);
        return Task.FromResult(res);
    }

    // ---------- 4) 自然语言入口 ----------
    [McpServerTool, Description("自然语言：如 本月17号到19号的10-14点 报警与产能分析")]
    public static Task<string> AnalyzeProdAndAlarmsNL(
        [Description("中文问句，例如：本月17号到19号的10-14点产能报警分析")] string text,
        [Description("低良率阈值%，默认95")] double lowYieldThreshold = 95)
    {
        var now = DateTime.Now;
        DateTime d0, d1; int? sh, eh;
        if (!TryParseThisMonthRange(text, now, out d0, out d1, out sh, out eh))
        {
            var err = JsonErr("AnalyzeProdAndAlarmsNL", "无法从文本解析日期/时段");
            ToolCallLogger.Log(nameof(AnalyzeProdAndAlarmsNL), new { text, lowYieldThreshold }, null, "parse text range failed");
            return Task.FromResult(err);
        }

        var win = (sh.HasValue && eh.HasValue) ? string.Format("{0:00}-{1:00}", sh.Value, eh.Value) : null;
        return GetAlarmImpactSummary(d0.ToString("yyyy-MM-dd"), d1.ToString("yyyy-MM-dd"), win, lowYieldThreshold);
    }

    // ---------- 工具函数 ----------

    private static DateTime ParseSmartDateOrToday(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return DateTime.Today;
        var now = DateTime.Now;
        DateTime d;
        if (TryParseSmartDate(text, now, out d)) return d;
        throw new ArgumentException("日期格式应为 yyyy-MM-dd / MM-dd / 本月dd / dd");
    }

    private static Tuple<DateTime, DateTime> ParseSmartRange(string s0, string s1)
    {
        var now = DateTime.Now;
        DateTime d0, d1;
        if (!TryParseSmartDate(s0, now, out d0)) throw new ArgumentException("startDate 无法解析");
        if (!TryParseSmartDate(s1, now, out d1)) throw new ArgumentException("endDate 无法解析");
        if (d1 < d0) { var t = d0; d0 = d1; d1 = t; }
        return Tuple.Create(d0.Date, d1.Date);
    }

    private static bool TryParseSmartDate(string text, DateTime now, out DateTime day)
    {
        day = default(DateTime);
        if (string.IsNullOrWhiteSpace(text)) { day = now.Date; return true; }

        text = text.Trim();
        DateTime dFull;
        if (DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out dFull))
        { day = dFull.Date; return true; }

        var m1 = Regex.Match(text, @"^(?<m>\d{1,2})[-/\.](?<d>\d{1,2})$");
        if (m1.Success)
        {
            int m = int.Parse(m1.Groups["m"].Value);
            int d = int.Parse(m1.Groups["d"].Value);
            try { day = new DateTime(now.Year, m, d); return true; } catch { return false; }
        }

        var m2 = Regex.Match(text, @"^(?<d>\d{1,2})$");
        if (m2.Success)
        {
            int d = int.Parse(m2.Groups["d"].Value);
            try { day = new DateTime(now.Year, now.Month, d); return true; } catch { return false; }
        }

        DateTime dAny;
        if (DateTime.TryParse(text, out dAny)) { day = dAny.Date; return true; }
        return false;
    }

    private static bool TryParseWindow(string s, out int sh, out int eh)
    {
        sh = 0; eh = 24;
        if (string.IsNullOrWhiteSpace(s)) return true;

        var raw = s.Replace("：", ":").Replace(" ", "");
        var parts = raw.Split(new char[] { '-', '—', '~' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return false;

        int h1, h2;
        if (!ParseHour(parts[0], out h1)) return false;
        if (!ParseHour(parts[1], out h2)) return false;

        sh = ClampInt(h1, 0, 23);
        eh = ClampInt(h2, 1, 24);
        if (eh <= sh) eh = ClampInt(sh + 1, 1, 24);
        return true;
    }

    private static bool ParseHour(string p, out int h)
    {
        h = 0;
        p = (p ?? "").Trim();
        TimeSpan ts;
        int hi;
        if (TimeSpan.TryParse(p, out ts)) { h = ClampInt((int)ts.TotalHours, 0, 23); return true; }
        if (int.TryParse(p, out hi)) { h = ClampInt(hi, 0, 23); return true; }
        return false;
    }

    private static bool TryParseThisMonthRange(string text, DateTime now,
        out DateTime d0, out DateTime d1, out int? sh, out int? eh)
    {
        d0 = default(DateTime); d1 = default(DateTime); sh = null; eh = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Replace("：", ":").Replace("到", "-").Replace("至", "-").Replace("—", "-").Replace("～", "-");

        var m = Regex.Match(text, @"本月\s*(?<d1>\d{1,2})[日号]?\s*-\s*(?<d2>\d{1,2})[日号]?");
        if (!m.Success) return false;

        int a = int.Parse(m.Groups["d1"].Value), b = int.Parse(m.Groups["d2"].Value);
        int lo = Math.Min(a, b), hi = Math.Max(a, b);
        try { d0 = new DateTime(now.Year, now.Month, lo); d1 = new DateTime(now.Year, now.Month, hi); }
        catch { return false; }

        var wm = Regex.Match(text, @"(?<h1>\d{1,2}(?::\d{1,2})?)\s*-\s*(?<h2>\d{1,2}(?::\d{1,2})?)");
        if (wm.Success)
        {
            int H1, H2;
            if (!ParseHour(wm.Groups["h1"].Value, out H1)) return true; // 仅日期也算成功
            if (!ParseHour(wm.Groups["h2"].Value, out H2)) return true;

            if (H1 > H2) { var t = H1; H1 = H2; H2 = t; }
            sh = ClampInt(H1, 0, 23);
            H2 = ClampInt(H2, 1, 24);
            if (H2 <= sh.Value) H2 = ClampInt(sh.Value + 1, 1, 24);
            eh = H2;
        }
        return true;
    }

    private static double Pearson(List<double> xs, List<double> ys)
    {
        int n = Math.Min(xs.Count, ys.Count);
        if (n <= 1) return 0.0;

        double mx = 0.0, my = 0.0;
        for (int i = 0; i < n; i++) { mx += xs[i]; my += ys[i]; }
        mx /= n; my /= n;

        double num = 0.0, dx2 = 0.0, dy2 = 0.0;
        for (int i = 0; i < n; i++)
        {
            double dx = xs[i] - mx, dy = ys[i] - my;
            num += dx * dy; dx2 += dx * dx; dy2 += dy * dy;
        }
        if (dx2 == 0.0 || dy2 == 0.0) return 0.0;
        double r = num / Math.Sqrt(dx2 * dy2);
        return Math.Round(r, 4);
    }

    private static string JsonErr(string where, string msg)
    {
        return JsonConvert.SerializeObject(new { type = "error", where = where, message = msg });
    }
}

/* ================================= AlarmCsvTools ================================ */

[McpServerToolType]
public static class AlarmCsvTools
{
    public static string WarmLogPath { get; set; } = @"D:\Data\Alarms";
    public static bool FlatFileLayout { get; set; }

    // 读取单日 CSV（自动识别文件日期；容忍 GB 系列编码）
    private static IEnumerable<AlarmRecord> ReadCsvByDay(string file)
    {
        var fileDate = ExtractDateFromFileName(file);
        if (!fileDate.HasValue) fileDate = File.GetLastWriteTime(file).Date;

        string text = File.ReadAllText(file, Encoding.GetEncoding("GB2312"));
        using (var sr = new StringReader(text))
        {
            var header = sr.ReadLine();
            if (header == null) yield break;

            var headers = SplitCsvLine(header).Select(NormalizeCell).ToArray();
            int idxCode = IndexOf(headers, "报警代码", "报警代碼", "Code", "报警编号");
            int idxContent = IndexOf(headers, "报警内容", "Content", "描述");
            int idxCategory = IndexOf(headers, "报警类别", "类别", "Category");
            int idxStart = IndexOf(headers, "开始时间", "Start", "StartTime");
            int idxEnd = IndexOf(headers, "结束时间", "End", "EndTime");
            int idxSeconds = IndexOf(headers, "报警时间(s)", "报警时长(s)", "持续时间(s)", "Seconds");

            string line;
            while ((line = sr.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var cells = SplitCsvLine(line).ToArray();
                if (cells.Length == 0) continue;

                Func<int, string> get = i => NormalizeCell(i >= 0 && i < cells.Length ? cells[i] : "");

                var code = get(idxCode);
                var content = get(idxContent);
                var category = get(idxCategory);

                TimeSpan startTs, endTs;
                var startOk = TryParseTime(get(idxStart), out startTs);
                var endOk = TryParseTime(get(idxEnd), out endTs);
                if (!startOk || !endOk) continue;

                var start = fileDate.Value + startTs;
                var end = fileDate.Value + endTs;
                if (end < start) end = start;

                int dur;
                int sec;
                if (int.TryParse(get(idxSeconds), out sec) && sec >= 0) dur = sec;
                else dur = (int)Math.Max(0, (end - start).TotalSeconds);

                var rec = new AlarmRecord
                {
                    Code = code,
                    Content = content,
                    Category = category,
                    Start = start,
                    End = end,
                    DurationSec = dur,
                    SourceFile = Path.GetFileName(file)
                };
                yield return rec;
            }
        }
    }

    // 指定起止日期（含端点），可选时段窗口过滤
    private static IEnumerable<AlarmRecord> ReadRange(DateTime d0, DateTime d1, TimeWindow? window)
    {
        var dir = WarmLogPath;
        if (!Directory.Exists(dir)) yield break;

        if (FlatFileLayout)
        {
            for (var day = d0.Date; day <= d1.Date; day = day.AddDays(1))
            {
                var file = AlarmCsvRepository.ResolveWatchModeFile(dir, day);
                if (string.IsNullOrWhiteSpace(file)) continue;

                foreach (var r in ReadCsvByDay(file))
                {
                    if (!window.HasValue || window.Value.Contains(r.Start.TimeOfDay))
                        yield return r;
                }
            }
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(dir, "*.csv", SearchOption.TopDirectoryOnly))
        {
            var day = ExtractDateFromFileName(file);
            if (!day.HasValue) day = File.GetLastWriteTime(file).Date;

            var d = day.Value.Date;
            if (d < d0.Date || d > d1.Date) continue;

            foreach (var r in ReadCsvByDay(file))
            {
                if (!window.HasValue || window.Value.Contains(r.Start.TimeOfDay))
                    yield return r;
            }
        }
    }

    /* ===== MCP 工具 ===== */

    [McpServerTool, Description("跨多天 + 指定时段（如 10-14 或 10:00-14:00）的报警统计；起止日期均含端点；window 留空=全天")]
    public static Task<string> GetAlarmRangeWindowSummary(
        [Description("开始日期 yyyy-MM-dd（含）")] string startDate,
        [Description("结束日期 yyyy-MM-dd（含）")] string endDate,
        [Description("时间段窗口，如 '10-14' 或 '10:00-14:00'；留空=全天")] string window = null,
        [Description("返回TopN报警（按持续秒或次数聚合），默认10")] int topN = 10,
        [Description("聚合依据：count/duration，默认duration")] string sortBy = "duration")
    {
        DateTime d0, d1;
        if (!DateTime.TryParseExact(startDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d0))
        {
            var err = Error("GetAlarmRangeWindowSummary", "startDate 格式应为 yyyy-MM-dd");
            ToolCallLogger.Log(nameof(GetAlarmRangeWindowSummary), new { startDate, endDate, window, topN, sortBy }, null, "startDate parse error");
            return Task.FromResult(err);
        }
        if (!DateTime.TryParseExact(endDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d1))
        {
            var err = Error("GetAlarmRangeWindowSummary", "endDate 格式应为 yyyy-MM-dd");
            ToolCallLogger.Log(nameof(GetAlarmRangeWindowSummary), new { startDate, endDate, window, topN, sortBy }, null, "endDate parse error");
            return Task.FromResult(err);
        }
        if (d1 < d0) { var t = d0; d0 = d1; d1 = t; }

        TimeWindow tw;
        TimeWindow? opt = null;
        if (!string.IsNullOrWhiteSpace(window))
        {
            if (!TimeWindow.TryParse(window, out tw))
            {
                var err = Error("GetAlarmRangeWindowSummary", "window 格式应为 'HH-mm' 或 'HH:mm-HH:mm'");
                ToolCallLogger.Log(nameof(GetAlarmRangeWindowSummary), new { startDate, endDate, window, topN, sortBy }, null, "window parse error");
                return Task.FromResult(err);
            }
            opt = tw;
        }

        var all = ReadRange(d0, d1, opt).ToList();

        int totalCount = all.Count;
        long totalDur = all.Sum(x => (long)x.DurationSec);

        var byCategory = all
            .GroupBy(x => x.Category ?? "")
            .Select(g => new { category = g.Key, count = g.Count(), duration = g.Sum(x => (long)x.DurationSec) })
            .OrderByDescending(x => x.duration)
            .ToList();

        var byCode = all
            .GroupBy(x => x.Code ?? "")
            .Select(g => new
            {
                code = g.Key,
                content = g.First().Content,
                count = g.Count(),
                duration = g.Sum(x => (long)x.DurationSec)
            });

        var top = ((sortBy ?? "duration").Equals("count", StringComparison.OrdinalIgnoreCase)
                    ? byCode.OrderByDescending(x => x.count)
                    : byCode.OrderByDescending(x => x.duration))
                  .Take(Math.Max(1, topN))
                  .ToList();

        var payload = new
        {
            type = "alarm.range.window.summary",
            range = new { start = d0.ToString("yyyy-MM-dd"), end = d1.ToString("yyyy-MM-dd") },
            window = opt.HasValue ? opt.Value.ToString() : "全天",
            totals = new { count = totalCount, durationSeconds = totalDur },
            byCategory = byCategory,
            top = top
        };
        var res = JsonConvert.SerializeObject(payload, Formatting.Indented);
        ToolCallLogger.Log(nameof(GetAlarmRangeWindowSummary), new { startDate = d0.ToString("yyyy-MM-dd"), endDate = d1.ToString("yyyy-MM-dd"), window, topN, sortBy }, res);
        return Task.FromResult(res);
    }

    [McpServerTool, Description("按关键字/代码检索报警明细（跨天 + 可选时段），用于AI回答时展示引用样本")]
    public static Task<string> QueryAlarms(
        [Description("开始日期 yyyy-MM-dd（含）")] string startDate,
        [Description("结束日期 yyyy-MM-dd（含）")] string endDate,
        [Description("报警代码（可空）")] string code = null,
        [Description("内容关键字（可空，多词用空格分隔，全包含匹配）")] string keyword = null,
        [Description("时间段窗口，如 '10-14' 或 '10:00-14:00'；留空=全天")] string window = null,
        [Description("最大返回条数，默认50")] int take = 50)
    {
        DateTime d0, d1;
        if (!DateTime.TryParseExact(startDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d0))
        {
            var err = Error("QueryAlarms", "startDate 格式应为 yyyy-MM-dd");
            ToolCallLogger.Log(nameof(QueryAlarms), new { startDate, endDate, code, keyword, window, take }, null, "startDate parse error");
            return Task.FromResult(err);
        }
        if (!DateTime.TryParseExact(endDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d1))
        {
            var err = Error("QueryAlarms", "endDate 格式应为 yyyy-MM-dd");
            ToolCallLogger.Log(nameof(QueryAlarms), new { startDate, endDate, code, keyword, window, take }, null, "endDate parse error");
            return Task.FromResult(err);
        }
        if (d1 < d0) { var t = d0; d0 = d1; d1 = t; }

        TimeWindow tw;
        TimeWindow? opt = null;
        if (!string.IsNullOrWhiteSpace(window))
        {
            if (!TimeWindow.TryParse(window, out tw))
            {
                var err = Error("QueryAlarms", "window 格式应为 'HH-mm' 或 'HH:mm-HH:mm'");
                ToolCallLogger.Log(nameof(QueryAlarms), new { startDate, endDate, code, keyword, window, take }, null, "window parse error");
                return Task.FromResult(err);
            }
            opt = tw;
        }

        IEnumerable<AlarmRecord> all = ReadRange(d0, d1, opt);

        if (!string.IsNullOrWhiteSpace(code))
            all = all.Where(x => string.Equals(x.Code ?? "", code, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var ks = keyword.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var k in ks)
            {
                var kk = k;
                all = all.Where(x =>
                    (!string.IsNullOrEmpty(x.Content) && x.Content.IndexOf(kk, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (!string.IsNullOrEmpty(x.Category) && x.Category.IndexOf(kk, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (!string.IsNullOrEmpty(x.Code) && x.Code.IndexOf(kk, StringComparison.OrdinalIgnoreCase) >= 0));
            }
        }

        var items = all
            .OrderByDescending(x => x.Start)
            .Take(Math.Max(1, take))
            .Select(x => new
            {
                Code = x.Code,
                Content = x.Content,
                Category = x.Category,
                start = x.Start.ToString("yyyy-MM-dd HH:mm:ss"),
                end = x.End.ToString("yyyy-MM-dd HH:mm:ss"),
                durationSeconds = x.DurationSec,
                file = x.SourceFile
            })
            .ToList();

        var payload = new
        {
            type = "alarm.query",
            range = new { start = d0.ToString("yyyy-MM-dd"), end = d1.ToString("yyyy-MM-dd") },
            window = opt.HasValue ? opt.Value.ToString() : "全天",
            count = items.Count,
            items = items
        };
        var res = JsonConvert.SerializeObject(payload, Formatting.Indented);
        ToolCallLogger.Log(nameof(QueryAlarms), new { startDate = d0.ToString("yyyy-MM-dd"), endDate = d1.ToString("yyyy-MM-dd"), code, keyword, window, take }, res);
        return Task.FromResult(res);
    }

    // ---------- 工具 & 解析 ----------

    private static string Error(string where, string message)
    {
        return JsonConvert.SerializeObject(new { type = "error", where = where, message = message });
    }

    private static IEnumerable<string> SplitCsvLine(string line)
    {
        var cur = new StringBuilder();
        bool inQ = false;
        for (int i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inQ && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                else inQ = !inQ;
            }
            else if (ch == ',' && !inQ)
            {
                yield return cur.ToString();
                cur.Clear();
            }
            else cur.Append(ch);
        }
        yield return cur.ToString();
    }

    /// <summary>去掉单双/中英文引号与空白，保障表头/单元格匹配。</summary>
    private static string NormalizeCell(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var t = s.Trim();
        if (t.Length >= 2)
        {
            var first = t[0];
            var last = t[t.Length - 1];
            if ((first == '"' && last == '"') || (first == '“' && last == '”') || (first == '\'' && last == '\''))
            {
                t = t.Substring(1, t.Length - 2);
                t = t.Replace("\"\"", "\"");
            }
        }
        return t.Trim();
    }

    private static int IndexOf(string[] headers, params string[] candidates)
    {
        for (int i = 0; i < headers.Length; i++)
        {
            var h = NormalizeCell(headers[i]);
            foreach (var c in candidates)
                if (h.Equals(NormalizeCell(c), StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    private static bool TryParseTime(string s, out TimeSpan ts)
    {
        ts = default(TimeSpan);
        if (string.IsNullOrWhiteSpace(s)) return false;

        s = s.Trim();
        s = s.Replace("点", ":").Replace("分", ":").Replace("秒", "");
        s = s.Replace("-", ":").Replace("：", ":");

        var parts = s.Split(new[] { ':' }, StringSplitOptions.RemoveEmptyEntries);
        int hh, mm;
        if (parts.Length == 1 && int.TryParse(parts[0], out hh))
            return TimeSpan.TryParse(string.Format("{0:D2}:00:00", hh), out ts);
        if (parts.Length == 2 && int.TryParse(parts[0], out hh) && int.TryParse(parts[1], out mm))
            return TimeSpan.TryParse(string.Format("{0:D2}:{1:D2}:00", hh, mm), out ts);
        if (TimeSpan.TryParse(s, out ts)) return true;
        DateTime dt;
        if (DateTime.TryParse(s, out dt)) { ts = dt.TimeOfDay; return true; }
        return false;
    }

    private static DateTime? ExtractDateFromFileName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path) ?? "";
        var m = Regex.Match(name, @"(?<y>\d{4})[-_/\.]?(?<m>\d{1,2})[-_/\.]?(?<d>\d{1,2})");
        if (m.Success)
        {
            int y, mo, d;
            if (int.TryParse(m.Groups["y"].Value, out y) &&
                int.TryParse(m.Groups["m"].Value, out mo) &&
                int.TryParse(m.Groups["d"].Value, out d))
            {
                try { return new DateTime(y, mo, d); } catch { }
            }
        }
        return null;
    }

    // 时段窗口（当天的 [From, To]）
    private struct TimeWindow
    {
        public TimeSpan From;
        public TimeSpan To;

        public TimeWindow(TimeSpan from, TimeSpan to)
        {
            From = from; To = to;
        }

        public bool Contains(TimeSpan t) { return t >= From && t <= To; }

        public override string ToString()
        {
            return string.Format("{0:hh\\:mm}-{1:hh\\:mm}", From, To);
        }

        public static bool TryParse(string s, out TimeWindow w)
        {
            w = default(TimeWindow);
            if (string.IsNullOrWhiteSpace(s)) return false;

            s = s.Trim().Replace("：", ":").Replace("—", "-").Replace("~", "-");
            var parts = s.Split(new[] { '-' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) return false;

            TimeSpan t0, t1;
            if (!TryAny(parts[0], out t0)) return false;
            if (!TryAny(parts[1], out t1)) return false;

            if (t1 < t0) { var tmp = t0; t0 = t1; t1 = tmp; }
            w = new TimeWindow(t0, t1);
            return true;
        }

        private static bool TryAny(string x, out TimeSpan ts)
        {
            ts = default(TimeSpan);
            if (x == null) return false;
            x = x.Replace("点", ":").Replace("分", ":").Replace("秒", "");
            var ps = x.Split(new[] { ':' }, StringSplitOptions.RemoveEmptyEntries);
            int h, m;
            if (ps.Length == 1 && int.TryParse(ps[0], out h))
                return TimeSpan.TryParse(string.Format("{0:D2}:00:00", h), out ts);
            if (ps.Length == 2 && int.TryParse(ps[0], out h) && int.TryParse(ps[1], out m))
                return TimeSpan.TryParse(string.Format("{0:D2}:{1:D2}:00", h, m), out ts);
            return TimeSpan.TryParse(x, out ts);
        }
    }
}

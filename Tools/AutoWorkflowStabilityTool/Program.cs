using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EW_Assistant.Diagnostics;

internal static partial class Program
{
    private const string DefaultConfigPath = @"D:\AppConfig.json";
    private const string DefaultWorkHttpServerPrefix = "http://127.0.0.1:8091/";
    private const string AutoClearAlarmLogRoot = @"D:\Data\AiLog\AutoClearAlarm";
    private const string StabilityLogRoot = @"D:\Data\AiLog\AutoStability";
    private const string SeparatorLine = "------------------------------------------------------------------------";

    public static async Task<int> Main(string[] args)
    {
        var options = Options.Parse(args);
        if (options.ShowHelp)
        {
            PrintUsage();
            return 0;
        }

        if (string.IsNullOrWhiteSpace(options.Prompt))
        {
            Console.Error.WriteLine("缺少 --prompt。");
            PrintUsage();
            return 2;
        }

        var serverUrl = string.IsNullOrWhiteSpace(options.ServerUrl)
            ? ResolveServerUrl(options.ConfigPath)
            : NormalizePrefix(options.ServerUrl);

        Directory.CreateDirectory(StabilityLogRoot);
        var logPath = Path.Combine(StabilityLogRoot, $"auto-stability-{DateTime.Now:yyyy-MM-dd-HHmmss}.log");
        LogRetentionPolicy.TryCleanupFiles(
            StabilityLogRoot,
            "*.log",
            SearchOption.TopDirectoryOnly,
            TimeSpan.FromDays(30));

        using var logger = new FileLogger(logPath);
        logger.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] AUTO 稳定性测试开始");
        logger.WriteLine($"服务地址：{serverUrl}");
        logger.WriteLine($"测试次数：{options.Count}");
        logger.WriteLine($"报警代码：{options.ErrorCode}");
        logger.WriteLine($"报警描述：{options.Prompt}");
        logger.WriteLine($"单次等待超时：{options.RunTimeoutSeconds} 秒");
        logger.WriteLine();

        Console.WriteLine($"日志输出：{logPath}");
        Console.WriteLine($"服务地址：{serverUrl}");

        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(5, options.RequestTimeoutSeconds))
        };

        var records = new List<RunRecord>();

        for (var i = 1; i <= options.Count; i++)
        {
            if (i > 1 && options.DelayBetweenRunsMs > 0)
            {
                await Task.Delay(options.DelayBetweenRunsMs);
            }

            var record = new RunRecord
            {
                Index = i,
                StartedAt = DateTime.Now
            };
            records.Add(record);

            logger.WriteLine($"===== 第 {i}/{options.Count} 次 =====");
            logger.WriteLine($"开始时间：{record.StartedAt:yyyy-MM-dd HH:mm:ss.fff}");

            var dailyLogPath = Path.Combine(AutoClearAlarmLogRoot, $"{record.StartedAt:yyyy-MM-dd}.log");
            var offset = GetFileLength(dailyLogPath);

            var body = new
            {
                errorCode = options.ErrorCode,
                prompt = options.Prompt,
                onlyMajorNodes = options.OnlyMajorNodes
            };

            try
            {
                using var response = await client.PostAsJsonAsync(serverUrl, body);
                record.HttpStatusCode = (int)response.StatusCode;
                record.AcceptedResponse = await response.Content.ReadAsStringAsync();

                logger.WriteLine($"HTTP状态：{record.HttpStatusCode}");
                logger.WriteLine($"受理响应：{record.AcceptedResponse}");

                if (!response.IsSuccessStatusCode)
                {
                    record.Status = "http_error";
                    logger.WriteLine("结果：请求未受理，跳过等待。");
                    logger.WriteLine();
                    continue;
                }

                record.Accepted = IsAccepted(record.AcceptedResponse);
                if (!record.Accepted)
                {
                    record.Status = "not_accepted";
                    logger.WriteLine("结果：服务返回未受理。");
                    logger.WriteLine();
                    continue;
                }

                var waitResult = await WaitForNextBlockAsync(
                    dailyLogPath,
                    offset,
                    TimeSpan.FromSeconds(Math.Max(10, options.RunTimeoutSeconds)),
                    TimeSpan.FromMilliseconds(Math.Max(200, options.PollIntervalMs)));

                record.FinishedAt = DateTime.Now;
                record.Elapsed = record.FinishedAt - record.StartedAt;

                if (!waitResult.Completed)
                {
                    record.Status = "timeout";
                    record.CapturedBlock = waitResult.Buffer;
                    logger.WriteLine($"结果：等待本地 AutoClearAlarm 日志超时（{options.RunTimeoutSeconds} 秒）。");
                    if (!string.IsNullOrWhiteSpace(waitResult.Buffer))
                    {
                        logger.WriteLine("已捕获的部分日志：");
                        logger.WriteLine(waitResult.Buffer.Trim());
                    }
                    logger.WriteLine();
                    continue;
                }

                record.Status = "completed";
                record.CapturedBlock = waitResult.Block;
                record.ToolSection = ExtractSection(waitResult.Block, "2. 调用的McpTool", "3. 最终回答");
                record.FinalReply = ExtractTailSection(waitResult.Block, "3. 最终回答");
                record.ToolSignature = ComputeSha256(NormalizeToolSection(record.ToolSection));
                record.FinalReplySignature = ComputeSha256(NormalizeText(record.FinalReply));

                logger.WriteLine($"完成时间：{record.FinishedAt:yyyy-MM-dd HH:mm:ss.fff}");
                logger.WriteLine($"耗时：{record.Elapsed.TotalSeconds:0.000} 秒");
                logger.WriteLine($"工具签名：{record.ToolSignature[..12]}");
                logger.WriteLine($"回答签名：{record.FinalReplySignature[..12]}");
                logger.WriteLine("完整日志块：");
                logger.WriteLine(record.CapturedBlock.Trim());
                logger.WriteLine();
            }
            catch (Exception ex)
            {
                record.Status = "exception";
                record.Error = ex.ToString();
                logger.WriteLine($"异常：{ex}");
                logger.WriteLine();
            }
        }

        WriteSummary(records, logger);
        Console.WriteLine("测试结束。");
        Console.WriteLine($"日志输出：{logPath}");
        return 0;
    }

    private static void WriteSummary(List<RunRecord> records, FileLogger logger)
    {
        var completed = records.Where(x => x.Status == "completed").ToList();
        var toolVariants = completed
            .GroupBy(x => x.ToolSignature)
            .OrderByDescending(g => g.Count())
            .ToList();
        var replyVariants = completed
            .GroupBy(x => x.FinalReplySignature)
            .OrderByDescending(g => g.Count())
            .ToList();

        logger.WriteLine("===== 汇总 =====");
        logger.WriteLine($"总次数：{records.Count}");
        logger.WriteLine($"完成次数：{completed.Count}");
        logger.WriteLine($"HTTP异常/未受理/超时/异常：{records.Count - completed.Count}");
        logger.WriteLine($"工具链路变体数：{toolVariants.Count}");
        foreach (var variant in toolVariants)
        {
            logger.WriteLine($"- 工具签名 {variant.Key[..12]}：{variant.Count()} 次；轮次={string.Join(",", variant.Select(x => x.Index))}");
        }

        logger.WriteLine($"最终回答变体数：{replyVariants.Count}");
        foreach (var variant in replyVariants)
        {
            logger.WriteLine($"- 回答签名 {variant.Key[..12]}：{variant.Count()} 次；轮次={string.Join(",", variant.Select(x => x.Index))}");
        }

        var stableTools = toolVariants.Count <= 1;
        var stableReply = replyVariants.Count <= 1;
        logger.WriteLine($"工具链路是否稳定：{(stableTools ? "是" : "否")}");
        logger.WriteLine($"最终回答是否稳定：{(stableReply ? "是" : "否")}");
        logger.WriteLine();

        Console.WriteLine($"完成次数：{completed.Count}/{records.Count}");
        Console.WriteLine($"工具链路变体数：{toolVariants.Count}");
        Console.WriteLine($"最终回答变体数：{replyVariants.Count}");
        Console.WriteLine($"工具链路是否稳定：{(stableTools ? "是" : "否")}");
        Console.WriteLine($"最终回答是否稳定：{(stableReply ? "是" : "否")}");
    }

    private static bool IsAccepted(string? responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(responseText);
            if (!doc.RootElement.TryGetProperty("ok", out var okProp))
            {
                return false;
            }

            return okProp.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => false
            };
        }
        catch
        {
            return false;
        }
    }

    private static long GetFileLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0L;
        }
        catch
        {
            return 0L;
        }
    }

    private static async Task<WaitBlockResult> WaitForNextBlockAsync(string path, long offset, TimeSpan timeout, TimeSpan pollInterval)
    {
        var started = DateTime.UtcNow;
        var buffer = string.Empty;

        while (DateTime.UtcNow - started < timeout)
        {
            buffer = ReadAppendedText(path, offset);
            if (!string.IsNullOrWhiteSpace(buffer))
            {
                var index = buffer.IndexOf(SeparatorLine, StringComparison.Ordinal);
                if (index >= 0)
                {
                    var end = index + SeparatorLine.Length;
                    return new WaitBlockResult
                    {
                        Completed = true,
                        Block = buffer[..end]
                    };
                }
            }

            await Task.Delay(pollInterval);
        }

        return new WaitBlockResult
        {
            Completed = false,
            Buffer = buffer
        };
    }

    private static string ReadAppendedText(string path, long offset)
    {
        try
        {
            if (!File.Exists(path))
            {
                return string.Empty;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length <= offset)
            {
                return string.Empty;
            }

            stream.Seek(offset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ExtractSection(string? block, string startMarker, string endMarker)
    {
        if (string.IsNullOrWhiteSpace(block))
        {
            return string.Empty;
        }

        var startIndex = block.IndexOf(startMarker, StringComparison.Ordinal);
        if (startIndex < 0)
        {
            return string.Empty;
        }

        startIndex += startMarker.Length;
        var endIndex = block.IndexOf(endMarker, startIndex, StringComparison.Ordinal);
        if (endIndex < 0)
        {
            endIndex = block.Length;
        }

        return block[startIndex..endIndex].Trim();
    }

    private static string ExtractTailSection(string? block, string startMarker)
    {
        if (string.IsNullOrWhiteSpace(block))
        {
            return string.Empty;
        }

        var startIndex = block.IndexOf(startMarker, StringComparison.Ordinal);
        if (startIndex < 0)
        {
            return string.Empty;
        }

        startIndex += startMarker.Length;
        var tail = block[startIndex..];
        var separatorIndex = tail.IndexOf(SeparatorLine, StringComparison.Ordinal);
        if (separatorIndex >= 0)
        {
            tail = tail[..separatorIndex];
        }

        return tail.Trim();
    }

    private static string NormalizeToolSection(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        normalized = TimestampRegex().Replace(normalized, "[TIMESTAMP]");
        return NormalizeText(normalized);
    }

    private static string NormalizeText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized
            .Split('\n')
            .Select(line => MultiSpaceRegex().Replace(line.Trim(), " "))
            .Where(line => line.Length > 0);
        return string.Join("\n", lines);
    }

    private static string ComputeSha256(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text ?? string.Empty));
        return Convert.ToHexString(bytes);
    }

    private static string ResolveServerUrl(string configPath)
    {
        try
        {
            if (!File.Exists(configPath))
            {
                return DefaultWorkHttpServerPrefix;
            }

            using var stream = File.OpenRead(configPath);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.TryGetProperty("WorkHttpServerPrefix", out var prefixProp))
            {
                var raw = prefixProp.GetString();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    return NormalizePrefix(raw);
                }
            }
        }
        catch
        {
            // ignore and use default
        }

        return DefaultWorkHttpServerPrefix;
    }

    private static string NormalizePrefix(string raw)
    {
        var text = string.IsNullOrWhiteSpace(raw) ? DefaultWorkHttpServerPrefix : raw.Trim();
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "http://" + text;
        }

        text = text.TrimEnd('/') + "/";
        return Uri.TryCreate(text, UriKind.Absolute, out var uri)
            ? uri.ToString()
            : DefaultWorkHttpServerPrefix;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("用法：");
        Console.WriteLine("  dotnet run --project Tools/AutoWorkflowStabilityTool -- --prompt \"C01_A未测分料气缸原位报警\"");
        Console.WriteLine();
        Console.WriteLine("可选参数：");
        Console.WriteLine("  --error-code <text>           默认 0");
        Console.WriteLine("  --count <int>                默认 20");
        Console.WriteLine("  --server <url>               默认读取 D:\\AppConfig.json 的 WorkHttpServerPrefix");
        Console.WriteLine("  --config <path>              默认 D:\\AppConfig.json");
        Console.WriteLine("  --run-timeout-seconds <int>  默认 120");
        Console.WriteLine("  --request-timeout-seconds <int> 默认 30");
        Console.WriteLine("  --delay-ms <int>             默认 500");
        Console.WriteLine("  --poll-ms <int>              默认 500");
        Console.WriteLine("  --all-nodes                  默认只发 onlyMajorNodes=true；加上该参数后会发 false");
    }

    [GeneratedRegex(@"\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\]")]
    private static partial Regex TimestampRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultiSpaceRegex();

    private sealed class WaitBlockResult
    {
        public bool Completed { get; set; }
        public string Block { get; set; } = string.Empty;
        public string Buffer { get; set; } = string.Empty;
    }

    private sealed class RunRecord
    {
        public int Index { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime FinishedAt { get; set; }
        public TimeSpan Elapsed { get; set; }
        public int HttpStatusCode { get; set; }
        public bool Accepted { get; set; }
        public string AcceptedResponse { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string Error { get; set; } = string.Empty;
        public string CapturedBlock { get; set; } = string.Empty;
        public string ToolSection { get; set; } = string.Empty;
        public string FinalReply { get; set; } = string.Empty;
        public string ToolSignature { get; set; } = string.Empty;
        public string FinalReplySignature { get; set; } = string.Empty;
    }

    private sealed class FileLogger : IDisposable
    {
        private readonly object _sync = new();
        private readonly StreamWriter _writer;

        public FileLogger(string path)
        {
            var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        }

        public void WriteLine(string text = "")
        {
            lock (_sync)
            {
                _writer.WriteLine(text);
            }
        }

        public void Dispose()
        {
            _writer.Dispose();
        }
    }

    private sealed class Options
    {
        public string Prompt { get; set; } = string.Empty;
        public string ErrorCode { get; set; } = "0";
        public int Count { get; set; } = 20;
        public string ServerUrl { get; set; } = string.Empty;
        public string ConfigPath { get; set; } = DefaultConfigPath;
        public int RunTimeoutSeconds { get; set; } = 120;
        public int RequestTimeoutSeconds { get; set; } = 30;
        public int DelayBetweenRunsMs { get; set; } = 500;
        public int PollIntervalMs { get; set; } = 500;
        public bool OnlyMajorNodes { get; set; } = true;
        public bool ShowHelp { get; set; }

        public static Options Parse(string[] args)
        {
            var options = new Options();
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                switch (arg)
                {
                    case "--prompt":
                        options.Prompt = ReadValue(args, ref i);
                        break;
                    case "--error-code":
                        options.ErrorCode = ReadValue(args, ref i);
                        break;
                    case "--count":
                        options.Count = ParseInt(ReadValue(args, ref i), 20);
                        break;
                    case "--server":
                        options.ServerUrl = ReadValue(args, ref i);
                        break;
                    case "--config":
                        options.ConfigPath = ReadValue(args, ref i);
                        break;
                    case "--run-timeout-seconds":
                        options.RunTimeoutSeconds = ParseInt(ReadValue(args, ref i), 120);
                        break;
                    case "--request-timeout-seconds":
                        options.RequestTimeoutSeconds = ParseInt(ReadValue(args, ref i), 30);
                        break;
                    case "--delay-ms":
                        options.DelayBetweenRunsMs = ParseInt(ReadValue(args, ref i), 500);
                        break;
                    case "--poll-ms":
                        options.PollIntervalMs = ParseInt(ReadValue(args, ref i), 500);
                        break;
                    case "--all-nodes":
                        options.OnlyMajorNodes = false;
                        break;
                    case "--help":
                    case "-h":
                    case "/?":
                        options.ShowHelp = true;
                        break;
                }
            }

            options.Count = Math.Max(1, options.Count);
            options.RunTimeoutSeconds = Math.Max(10, options.RunTimeoutSeconds);
            options.RequestTimeoutSeconds = Math.Max(5, options.RequestTimeoutSeconds);
            options.DelayBetweenRunsMs = Math.Max(0, options.DelayBetweenRunsMs);
            options.PollIntervalMs = Math.Max(100, options.PollIntervalMs);
            return options;
        }

        private static string ReadValue(string[] args, ref int index)
        {
            if (index + 1 >= args.Length)
            {
                return string.Empty;
            }

            index++;
            return args[index];
        }

        private static int ParseInt(string raw, int fallback)
        {
            return int.TryParse(raw, out var value) ? value : fallback;
        }
    }
}

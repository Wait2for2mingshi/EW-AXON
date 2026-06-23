using System.Globalization;
using System.Net;
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
    private const string BatchReplayLogRoot = @"D:\Data\AiLog\AutoVisionBatchReplay";
    private const string SeparatorLine = "------------------------------------------------------------------------";

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg",
        ".bmp",
        ".gif",
        ".webp",
        ".tif",
        ".tiff"
    };

    public static async Task<int> Main(string[] args)
    {
        var options = Options.Parse(args);
        if (options.ShowHelp)
        {
            PrintUsage();
            return 0;
        }

        var optionErrors = ValidateOptions(options);
        if (optionErrors.Count > 0)
        {
            foreach (var error in optionErrors)
            {
                Console.Error.WriteLine(error);
            }

            Console.Error.WriteLine();
            PrintUsage();
            return 2;
        }

        var config = ConfigSnapshot.Load(options.ConfigPath);
        if (!options.SkipConfigCheck)
        {
            var configErrors = ValidateConfig(config, options);
            if (configErrors.Count > 0)
            {
                foreach (var error in configErrors)
                {
                    Console.Error.WriteLine(error);
                }

                Console.Error.WriteLine();
                Console.Error.WriteLine("可在确认风险后加 --skip-config-check 跳过配置校验。");
                return 3;
            }
        }

        var configWarnings = BuildConfigWarnings(config, options);
        foreach (var warning in configWarnings)
        {
            Console.WriteLine("警告：" + warning);
        }

        var serverUrl = string.IsNullOrWhiteSpace(options.ServerUrl)
            ? NormalizePrefix(config.WorkHttpServerPrefix)
            : NormalizePrefix(options.ServerUrl);

        var images = EnumerateReplayImages(options);
        if (images.Count == 0)
        {
            Console.Error.WriteLine("未找到可回放图片。");
            return 4;
        }

        Directory.CreateDirectory(options.ReplaySlot);
        Directory.CreateDirectory(BatchReplayLogRoot);
        LogRetentionPolicy.TryCleanupFiles(
            BatchReplayLogRoot,
            "*.*",
            SearchOption.TopDirectoryOnly,
            TimeSpan.FromDays(30));

        var runStamp = DateTime.Now.ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture);
        var logPath = Path.Combine(BatchReplayLogRoot, $"auto-vision-batch-{runStamp}.log");
        var csvPath = Path.Combine(BatchReplayLogRoot, $"auto-vision-batch-{runStamp}.csv");

        using var logger = new FileLogger(logPath);
        using var csv = new CsvLogger(csvPath);
        csv.WriteHeader();

        logger.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] AUTO 视觉批量回放开始");
        logger.WriteLine($"服务地址：{serverUrl}");
        logger.WriteLine($"配置文件：{options.ConfigPath}");
        logger.WriteLine($"图片目录：{options.ImageDir}");
        logger.WriteLine($"回放槽目录：{options.ReplaySlot}");
        logger.WriteLine($"图片数量：{images.Count}");
        logger.WriteLine($"报警代码：{options.ErrorCode}");
        logger.WriteLine($"报警描述：{options.Prompt}");
        logger.WriteLine($"递归扫描：{(options.Recursive ? "是" : "否")}");
        logger.WriteLine($"排序：{options.Order}");
        logger.WriteLine($"首张触发前等待：{options.InitialDelayMs} ms");
        logger.WriteLine($"DryRun：{(options.DryRun ? "是" : "否")}");
        logger.WriteLine();

        Console.WriteLine($"日志输出：{logPath}");
        Console.WriteLine($"CSV输出：{csvPath}");
        Console.WriteLine($"服务地址：{serverUrl}");
        Console.WriteLine($"待回放图片：{images.Count}");

        if (options.DryRun)
        {
            WriteDryRun(images, logger);
            Console.WriteLine("DryRun 完成，未触发 AUTO。");
            return 0;
        }

        if (options.InitialDelayMs > 0)
        {
            Console.WriteLine($"首张触发前等待 {options.InitialDelayMs} ms...");
            await Task.Delay(options.InitialDelayMs).ConfigureAwait(false);
        }

        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(5, options.RequestTimeoutSeconds))
        };

        var records = new List<RunRecord>();
        for (var i = 0; i < images.Count; i++)
        {
            if (i > 0 && options.DelayBetweenRunsMs > 0)
            {
                await Task.Delay(options.DelayBetweenRunsMs);
            }

            var sourceImage = images[i];
            var record = new RunRecord
            {
                Index = i + 1,
                Total = images.Count,
                SourceImagePath = sourceImage,
                StartedAt = DateTime.Now
            };
            records.Add(record);

            logger.WriteLine($"===== 第 {record.Index}/{record.Total} 张 =====");
            logger.WriteLine($"开始时间：{record.StartedAt:yyyy-MM-dd HH:mm:ss.fff}");
            logger.WriteLine($"原图：{sourceImage}");

            Console.WriteLine($"[{record.Index}/{record.Total}] {Path.GetFileName(sourceImage)}");

            var dailyLogPath = Path.Combine(AutoClearAlarmLogRoot, $"{record.StartedAt:yyyy-MM-dd}.log");
            var offset = GetFileLength(dailyLogPath);

            try
            {
                record.ReplayImagePath = PrepareReplaySlot(options.ReplaySlot, sourceImage, logger);
                logger.WriteLine($"回放图：{record.ReplayImagePath}");

                var accepted = await TryPostWithRetryAsync(client, serverUrl, options, record, logger).ConfigureAwait(false);
                if (!accepted)
                {
                    FinishRecord(record, "http_error");
                    WriteRecord(record, logger, csv);
                    logger.WriteLine("批量回放已停止：当前图片未能受理，为避免图片与结果错位，不继续切换后续图片。");
                    break;
                }

                var waitResult = await WaitForNextBlockAsync(
                    dailyLogPath,
                    offset,
                    TimeSpan.FromSeconds(Math.Max(10, options.RunTimeoutSeconds)),
                    TimeSpan.FromMilliseconds(Math.Max(100, options.PollIntervalMs))).ConfigureAwait(false);

                record.FinishedAt = DateTime.Now;
                record.Elapsed = record.FinishedAt - record.StartedAt;

                if (!waitResult.Completed)
                {
                    record.CapturedBlock = waitResult.Buffer;
                    FinishRecord(record, "timeout");
                    logger.WriteLine($"结果：等待本地 AutoClearAlarm 日志超时（{options.RunTimeoutSeconds} 秒）。");
                    if (!string.IsNullOrWhiteSpace(waitResult.Buffer))
                    {
                        logger.WriteLine("已捕获的部分日志：");
                        logger.WriteLine(waitResult.Buffer.Trim());
                    }
                }
                else
                {
                    record.CapturedBlock = waitResult.Block;
                    record.SkippedRejectedBlocks = waitResult.SkippedRejectedBlocks;
                    record.ToolSection = ExtractSection(waitResult.Block, "2. 调用的McpTool", "3. 最终回答");
                    record.FinalReply = ExtractTailSection(waitResult.Block, "3. 最终回答");
                    record.ToolSignature = ComputeSha256(NormalizeToolSection(record.ToolSection));
                    record.FinalReplySignature = ComputeSha256(NormalizeText(record.FinalReply));
                    FinishRecord(record, ResolveCompletedStatus(record));
                }

                WriteRecord(record, logger, csv);
                if (record.Status != "completed")
                {
                    logger.WriteLine("批量回放已停止：当前图片未完成，为避免图片与结果错位，不继续切换后续图片。");
                    break;
                }
            }
            catch (Exception ex)
            {
                record.Error = ex.ToString();
                FinishRecord(record, "exception");
                logger.WriteLine($"异常：{ex}");
                WriteRecord(record, logger, csv);
                logger.WriteLine("批量回放已停止：当前图片发生异常，为避免图片与结果错位，不继续切换后续图片。");
                break;
            }
        }

        WriteSummary(records, logger);
        Console.WriteLine("批量回放结束。");
        Console.WriteLine($"日志输出：{logPath}");
        Console.WriteLine($"CSV输出：{csvPath}");

        return records.All(x => x.Status == "completed") ? 0 : 1;
    }

    private static List<string> ValidateOptions(Options options)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(options.ImageDir))
        {
            errors.Add("缺少 --image-dir。");
        }
        else if (!Directory.Exists(options.ImageDir))
        {
            errors.Add("图片目录不存在：" + options.ImageDir);
        }

        if (string.IsNullOrWhiteSpace(options.ReplaySlot))
        {
            errors.Add("缺少 --replay-slot。");
        }

        if (string.IsNullOrWhiteSpace(options.Prompt))
        {
            errors.Add("缺少 --prompt。");
        }

        if (!string.IsNullOrWhiteSpace(options.ImageDir)
            && !string.IsNullOrWhiteSpace(options.ReplaySlot)
            && PathsEqual(options.ImageDir, options.ReplaySlot))
        {
            errors.Add("--replay-slot 不能与 --image-dir 相同，避免清理回放槽时误删原图。");
        }

        return errors;
    }

    private static List<string> ValidateConfig(ConfigSnapshot config, Options options)
    {
        var errors = new List<string>();
        if (!config.Exists)
        {
            errors.Add("配置文件不存在：" + options.ConfigPath);
            return errors;
        }

        if (!config.IsReadable)
        {
            errors.Add("配置文件读取失败：" + config.Error);
            return errors;
        }

        if (string.IsNullOrWhiteSpace(config.AutoVisionKey))
        {
            errors.Add("配置 AutoVisionKey 为空，无法确认会触发视觉 AUTO workflow。");
        }

        if (config.AutoVisionImageTestMode != true)
        {
            errors.Add("配置 AutoVisionImageTestMode 不是 true。批量回放需要打开视觉测试模式，避免正式取图时间窗拦截历史图片。");
        }

        if (!PathsEqual(config.AutoVisionImagePathA, options.ReplaySlot))
        {
            errors.Add("配置 AutoVisionImagePathA 未指向回放槽。当前配置："
                       + (string.IsNullOrWhiteSpace(config.AutoVisionImagePathA) ? "空" : config.AutoVisionImagePathA)
                       + "；期望：" + options.ReplaySlot);
        }

        if (config.ClearMachineAlarmsShadowMode != true && !options.AllowLiveActions)
        {
            errors.Add("配置 ClearMachineAlarmsShadowMode 不是 true。历史图片批量回放默认禁止真实机台动作；确认要放行时加 --allow-live-actions。");
        }

        return errors;
    }

    private static List<string> BuildConfigWarnings(ConfigSnapshot config, Options options)
    {
        var warnings = new List<string>();
        if (options.SkipConfigCheck)
        {
            warnings.Add("已跳过配置校验，请自行确认 AutoVisionImageTestMode=true 且 AutoVisionImagePathA 指向回放槽。");
        }

        if (options.AllowLiveActions)
        {
            warnings.Add("已允许非影子模式运行，workflow 可能触发真实机台动作。");
        }

        if (config.Exists && config.IsReadable && config.ClearMachineAlarmsShadowMode != true)
        {
            warnings.Add("当前 ClearMachineAlarmsShadowMode 不为 true。");
        }

        return warnings;
    }

    private static List<string> EnumerateReplayImages(Options options)
    {
        var searchOption = options.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(options.ImageDir, "*", searchOption)
                .Where(IsSupportedImage)
                .Where(path => !IsUnderPath(path, options.ReplaySlot))
                .ToList();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("扫描图片目录失败：" + ex.Message, ex);
        }

        files = options.Order switch
        {
            "time" => files.OrderBy(GetImageSortTime).ThenBy(x => Path.GetFileName(x), StringComparer.OrdinalIgnoreCase).ToList(),
            "time-desc" => files.OrderByDescending(GetImageSortTime).ThenBy(x => Path.GetFileName(x), StringComparer.OrdinalIgnoreCase).ToList(),
            _ => files.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()
        };

        if (options.SkipCount > 0)
        {
            files = files.Skip(options.SkipCount).ToList();
        }

        if (options.Limit > 0)
        {
            files = files.Take(options.Limit).ToList();
        }

        return files;
    }

    private static void WriteDryRun(List<string> images, FileLogger logger)
    {
        logger.WriteLine("===== DryRun 图片清单 =====");
        for (var i = 0; i < images.Count; i++)
        {
            logger.WriteLine($"{i + 1}. {images[i]}");
        }
    }

    private static string PrepareReplaySlot(string replaySlot, string sourceImage, FileLogger logger)
    {
        Directory.CreateDirectory(replaySlot);
        var hasLockedOldImages = false;
        foreach (var file in Directory.EnumerateFiles(replaySlot, "*", SearchOption.TopDirectoryOnly).Where(IsSupportedImage))
        {
            if (!TryDeleteFileWithRetry(file, out var deleteError))
            {
                hasLockedOldImages = true;
                logger.WriteLine(
                    $"回放槽旧图片暂时无法删除，已保留并改用唯一新文件名：{file}；{deleteError.GetType().Name}: {deleteError.Message}");
            }
        }

        var targetPath = GetReplayTargetPath(replaySlot, sourceImage, hasLockedOldImages);
        File.Copy(sourceImage, targetPath, overwrite: false);
        try
        {
            if (hasLockedOldImages)
            {
                var now = DateTime.Now;
                File.SetCreationTime(targetPath, now);
                File.SetLastWriteTime(targetPath, now);
                logger.WriteLine("回放槽存在锁定旧图片，本次回放图时间戳已设为当前时间，确保测试模式优先选中新图。");
            }
            else
            {
                var sourceInfo = new FileInfo(sourceImage);
                File.SetCreationTime(targetPath, sourceInfo.CreationTime);
                File.SetLastWriteTime(targetPath, sourceInfo.LastWriteTime);
            }
        }
        catch
        {
            // 时间戳仅用于回放记录可读性，失败不阻断触发。
        }

        return targetPath;
    }

    private static bool TryDeleteFileWithRetry(string path, out Exception deleteError)
    {
        deleteError = new IOException("删除旧图片失败。");
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                File.Delete(path);
                return true;
            }
            catch (IOException ex)
            {
                deleteError = ex;
            }
            catch (UnauthorizedAccessException ex)
            {
                deleteError = ex;
            }

            if (attempt < 5)
            {
                Thread.Sleep(200);
            }
        }

        return false;
    }

    private static string GetReplayTargetPath(string replaySlot, string sourceImage, bool forceUniqueName)
    {
        var targetPath = Path.Combine(replaySlot, Path.GetFileName(sourceImage));
        if (!forceUniqueName && !File.Exists(targetPath))
        {
            return targetPath;
        }

        var name = Path.GetFileNameWithoutExtension(sourceImage);
        var extension = Path.GetExtension(sourceImage);
        for (var index = 0; ; index++)
        {
            var suffix = index == 0 ? string.Empty : $"-{index}";
            var candidate = Path.Combine(replaySlot, $"{DateTime.Now:yyyyMMdd-HHmmssfff}-{name}{suffix}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private static async Task<bool> TryPostWithRetryAsync(
        HttpClient client,
        string serverUrl,
        Options options,
        RunRecord record,
        FileLogger logger)
    {
        var started = DateTime.UtcNow;
        var attempt = 0;
        while (true)
        {
            attempt++;
            var body = new
            {
                errorCode = options.ErrorCode,
                prompt = options.Prompt,
                onlyMajorNodes = options.OnlyMajorNodes
            };

            using var response = await client.PostAsJsonAsync(serverUrl, body).ConfigureAwait(false);
            record.HttpStatusCode = (int)response.StatusCode;
            record.AcceptedResponse = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            ParseAcceptedResponse(record);

            logger.WriteLine($"HTTP状态：{record.HttpStatusCode}");
            logger.WriteLine($"受理响应：{record.AcceptedResponse}");

            if (response.IsSuccessStatusCode && record.Accepted)
            {
                return true;
            }

            var retryable = response.StatusCode == HttpStatusCode.TooManyRequests;
            if (!retryable)
            {
                record.Error = "请求未受理。HTTP=" + record.HttpStatusCode + "；response=" + record.AcceptedResponse;
                return false;
            }

            var elapsed = DateTime.UtcNow - started;
            var retryLimitReached = options.AcceptRetries >= 0 && attempt > options.AcceptRetries;
            var timeoutReached = elapsed >= TimeSpan.FromSeconds(options.AcceptTimeoutSeconds);
            if (retryLimitReached || timeoutReached)
            {
                record.Error = "等待 AUTO 空闲并受理当前图片超时。HTTP="
                               + record.HttpStatusCode
                               + "；attempt="
                               + attempt.ToString(CultureInfo.InvariantCulture)
                               + "；elapsedSeconds="
                               + elapsed.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture)
                               + "；response="
                               + record.AcceptedResponse;
                return false;
            }

            logger.WriteLine(
                "请求繁忙，等待上一张 AUTO 完成后重试；"
                + "delayMs=" + options.AcceptRetryDelayMs.ToString(CultureInfo.InvariantCulture)
                + "，attempt=" + attempt.ToString(CultureInfo.InvariantCulture)
                + "，elapsedSeconds=" + elapsed.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture));
            await Task.Delay(options.AcceptRetryDelayMs).ConfigureAwait(false);
        }
    }

    private static void ParseAcceptedResponse(RunRecord record)
    {
        record.Accepted = false;
        record.Workflow = string.Empty;
        record.WorkflowKey = string.Empty;
        record.IsVisionRelated = null;
        if (string.IsNullOrWhiteSpace(record.AcceptedResponse))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(record.AcceptedResponse);
            var root = doc.RootElement;
            if (root.TryGetProperty("ok", out var okProp) && okProp.ValueKind == JsonValueKind.True)
            {
                record.Accepted = true;
            }

            if (root.TryGetProperty("workflow", out var workflowProp))
            {
                record.Workflow = workflowProp.GetString() ?? string.Empty;
            }

            if (root.TryGetProperty("workflowKey", out var workflowKeyProp))
            {
                record.WorkflowKey = workflowKeyProp.GetString() ?? string.Empty;
            }

            if (root.TryGetProperty("isVisionRelated", out var visionProp))
            {
                record.IsVisionRelated = visionProp.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => null
                };
            }
        }
        catch
        {
            record.Accepted = false;
        }
    }

    private static void FinishRecord(RunRecord record, string status)
    {
        record.Status = status;
        if (record.FinishedAt == default)
        {
            record.FinishedAt = DateTime.Now;
        }

        if (record.StartedAt != default && record.Elapsed == default)
        {
            record.Elapsed = record.FinishedAt - record.StartedAt;
        }
    }

    private static string ResolveCompletedStatus(RunRecord record)
    {
        if (record.IsVisionRelated != true)
        {
            return "not_vision_workflow";
        }

        if (IsFailedWorkflowReply(record.FinalReply))
        {
            record.Error = "视觉 workflow 已受理但未成功产出最终报告，最终回答=" + NormalizeText(record.FinalReply);
            return "workflow_failed";
        }

        return "completed";
    }

    private static bool IsFailedWorkflowReply(string? finalReply)
    {
        var normalized = NormalizeText(finalReply);
        return string.IsNullOrWhiteSpace(normalized)
               || string.Equals(normalized, "{}", StringComparison.Ordinal)
               || normalized.Contains("AUTO 分析已取消", StringComparison.Ordinal);
    }

    private static void WriteRecord(RunRecord record, FileLogger logger, CsvLogger csv)
    {
        logger.WriteLine($"完成时间：{record.FinishedAt:yyyy-MM-dd HH:mm:ss.fff}");
        logger.WriteLine($"耗时：{record.Elapsed.TotalSeconds:0.000} 秒");
        logger.WriteLine($"结果：{record.Status}");
        logger.WriteLine($"是否视觉 workflow：{FormatNullableBool(record.IsVisionRelated)}");
        if (!string.IsNullOrWhiteSpace(record.Workflow))
        {
            logger.WriteLine($"workflow：{record.Workflow} ({record.WorkflowKey})");
        }

        if (record.SkippedRejectedBlocks > 0)
        {
            logger.WriteLine($"已跳过未受理日志块：{record.SkippedRejectedBlocks}");
        }

        if (!string.IsNullOrWhiteSpace(record.ToolSignature))
        {
            logger.WriteLine($"工具签名：{record.ToolSignature[..12]}");
            logger.WriteLine($"回答签名：{record.FinalReplySignature[..12]}");
        }

        if (!string.IsNullOrWhiteSpace(record.Error))
        {
            logger.WriteLine("错误：" + record.Error.Trim());
        }

        if (!string.IsNullOrWhiteSpace(record.CapturedBlock))
        {
            logger.WriteLine("完整日志块：");
            logger.WriteLine(record.CapturedBlock.Trim());
        }

        logger.WriteLine();
        csv.WriteRecord(record);
    }

    private static void WriteSummary(List<RunRecord> records, FileLogger logger)
    {
        var completed = records.Where(x => x.Status == "completed").ToList();
        var failed = records.Where(x => x.Status != "completed").ToList();
        var replyVariants = completed
            .Where(x => !string.IsNullOrWhiteSpace(x.FinalReplySignature))
            .GroupBy(x => x.FinalReplySignature)
            .OrderByDescending(g => g.Count())
            .ToList();

        logger.WriteLine("===== 汇总 =====");
        logger.WriteLine($"总图片数：{records.Count}");
        logger.WriteLine($"完成数：{completed.Count}");
        logger.WriteLine($"失败/未受理/超时/非视觉数：{failed.Count}");
        logger.WriteLine($"最终回答变体数：{replyVariants.Count}");
        foreach (var variant in replyVariants)
        {
            logger.WriteLine($"- 回答签名 {variant.Key[..12]}：{variant.Count()} 次；图片序号={string.Join(",", variant.Select(x => x.Index))}");
        }

        if (failed.Count > 0)
        {
            logger.WriteLine("失败明细：");
            foreach (var item in failed)
            {
                logger.WriteLine($"- #{item.Index} {item.Status} {item.SourceImagePath}");
            }
        }

        Console.WriteLine($"完成数：{completed.Count}/{records.Count}");
        Console.WriteLine($"失败/未受理/超时/非视觉数：{failed.Count}");
        Console.WriteLine($"最终回答变体数：{replyVariants.Count}");
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
        var scanIndex = 0;
        var skippedRejectedBlocks = 0;

        while (DateTime.UtcNow - started < timeout)
        {
            buffer = ReadAppendedText(path, offset);
            if (!string.IsNullOrWhiteSpace(buffer))
            {
                while (scanIndex < buffer.Length)
                {
                    var index = buffer.IndexOf(SeparatorLine, scanIndex, StringComparison.Ordinal);
                    if (index < 0)
                    {
                        break;
                    }

                    var end = index + SeparatorLine.Length;
                    var block = buffer[scanIndex..end];
                    scanIndex = end;

                    if (IsRejectedAuditBlock(block))
                    {
                        skippedRejectedBlocks++;
                        continue;
                    }

                    return new WaitBlockResult
                    {
                        Completed = true,
                        Block = block,
                        SkippedRejectedBlocks = skippedRejectedBlocks
                    };
                }
            }

            await Task.Delay(pollInterval).ConfigureAwait(false);
        }

        return new WaitBlockResult
        {
            Completed = false,
            Buffer = buffer,
            SkippedRejectedBlocks = skippedRejectedBlocks
        };
    }

    private static bool IsRejectedAuditBlock(string block)
    {
        if (string.IsNullOrWhiteSpace(block))
        {
            return false;
        }

        return block.Contains("本次 AUTO 请求未受理", StringComparison.Ordinal)
               || block.Contains("AUTO 请求已丢弃", StringComparison.Ordinal)
               || block.Contains("当前已有上一条任务正在执行", StringComparison.Ordinal);
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

    private static string NormalizePrefix(string? raw)
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

    private static bool IsSupportedImage(string path)
    {
        return !string.IsNullOrWhiteSpace(path)
               && ImageExtensions.Contains(Path.GetExtension(path));
    }

    private static DateTime GetImageSortTime(string path)
    {
        try
        {
            return File.GetLastWriteTime(path);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static bool PathsEqual(string? left, string? right)
    {
        var a = NormalizePathForCompare(left);
        var b = NormalizePathForCompare(right);
        return a.Length > 0 && b.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnderPath(string path, string parent)
    {
        var child = NormalizePathForCompare(path);
        var root = NormalizePathForCompare(parent);
        if (child.Length == 0 || root.Length == 0)
        {
            return false;
        }

        return child.Equals(root, StringComparison.OrdinalIgnoreCase)
               || child.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePathForCompare(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var text = path.Trim().Replace('/', '\\').TrimEnd('\\');
        if (DriveRootRegex().IsMatch(text) || text.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return text;
        }

        try
        {
            return Path.GetFullPath(text).Replace('/', '\\').TrimEnd('\\');
        }
        catch
        {
            return text;
        }
    }

    private static string FormatNullableBool(bool? value)
    {
        return value.HasValue ? (value.Value ? "true" : "false") : "unknown";
    }

    private static string TruncateForCsv(string text, int maxLength)
    {
        var normalized = NormalizeText(text);
        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        return normalized[..maxLength];
    }

    private static void PrintUsage()
    {
        Console.WriteLine("用法：");
        Console.WriteLine("  dotnet run --project Tools/AutoVisionBatchReplayTool -- --image-dir \"C:\\codex\\资料\\车间图片\\06_A相机拍照存图\\NG\" --replay-slot \"C:\\codex\\资料\\AUTO视觉回放_Current\" --prompt \"C01_A未测分料气缸原位报警\"");
        Console.WriteLine();
        Console.WriteLine("必填参数：");
        Console.WriteLine("  --image-dir <path>            原始图片目录");
        Console.WriteLine("  --replay-slot <path>          单图回放槽目录；D:\\AppConfig.json 的 AutoVisionImagePathA 需指向该目录");
        Console.WriteLine("  --prompt <text>               用于触发视觉 AUTO 的报警描述，需命中 报警知识库IO.xlsx 的 是否视觉相关=TRUE");
        Console.WriteLine();
        Console.WriteLine("可选参数：");
        Console.WriteLine("  --error-code <text>           默认 0");
        Console.WriteLine("  --server <url>                默认读取 D:\\AppConfig.json 的 WorkHttpServerPrefix");
        Console.WriteLine("  --config <path>               默认 D:\\AppConfig.json");
        Console.WriteLine("  --recursive                   递归扫描图片目录");
        Console.WriteLine("  --order <name|time|time-desc> 默认 name");
        Console.WriteLine("  --skip <int>                  跳过前 N 张");
        Console.WriteLine("  --limit <int>                 最多回放 N 张；默认不限制");
        Console.WriteLine("  --run-timeout-seconds <int>   默认 180");
        Console.WriteLine("  --request-timeout-seconds <int> 默认 30");
        Console.WriteLine("  --initial-delay-ms <int>      触发第一张前等待，默认 0");
        Console.WriteLine("  --delay-ms <int>              每张之间等待，默认 500");
        Console.WriteLine("  --poll-ms <int>               日志轮询间隔，默认 500");
        Console.WriteLine("  --accept-retries <int>        429 busy 受理重试次数，默认不限制；设置 0 表示不重试");
        Console.WriteLine("  --accept-timeout-seconds <int> 等待 AUTO 空闲并受理当前图片的最长时间，默认 600");
        Console.WriteLine("  --accept-retry-delay-ms <int> 受理重试间隔，默认 1000");
        Console.WriteLine("  --all-nodes                   默认只发 onlyMajorNodes=true；加上该参数后会发 false");
        Console.WriteLine("  --dry-run                     只列出图片与校验配置，不触发 AUTO");
        Console.WriteLine("  --stop-on-error               兼容保留；当前为保证一张一张跑，失败默认立即停止");
        Console.WriteLine("  --allow-live-actions          允许 ClearMachineAlarmsShadowMode=false 时运行");
        Console.WriteLine("  --skip-config-check           跳过配置校验");
    }

    [GeneratedRegex(@"^[a-zA-Z]:\\")]
    private static partial Regex DriveRootRegex();

    [GeneratedRegex(@"\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\]")]
    private static partial Regex TimestampRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultiSpaceRegex();

    private sealed class WaitBlockResult
    {
        public bool Completed { get; set; }
        public string Block { get; set; } = string.Empty;
        public string Buffer { get; set; } = string.Empty;
        public int SkippedRejectedBlocks { get; set; }
    }

    private sealed class RunRecord
    {
        public int Index { get; set; }
        public int Total { get; set; }
        public string SourceImagePath { get; set; } = string.Empty;
        public string ReplayImagePath { get; set; } = string.Empty;
        public DateTime StartedAt { get; set; }
        public DateTime FinishedAt { get; set; }
        public TimeSpan Elapsed { get; set; }
        public int HttpStatusCode { get; set; }
        public bool Accepted { get; set; }
        public bool? IsVisionRelated { get; set; }
        public int SkippedRejectedBlocks { get; set; }
        public string Workflow { get; set; } = string.Empty;
        public string WorkflowKey { get; set; } = string.Empty;
        public string AcceptedResponse { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string Error { get; set; } = string.Empty;
        public string CapturedBlock { get; set; } = string.Empty;
        public string ToolSection { get; set; } = string.Empty;
        public string FinalReply { get; set; } = string.Empty;
        public string ToolSignature { get; set; } = string.Empty;
        public string FinalReplySignature { get; set; } = string.Empty;
    }

    private sealed class ConfigSnapshot
    {
        public bool Exists { get; private set; }
        public bool IsReadable { get; private set; }
        public string Error { get; private set; } = string.Empty;
        public string WorkHttpServerPrefix { get; private set; } = DefaultWorkHttpServerPrefix;
        public string AutoVisionKey { get; private set; } = string.Empty;
        public string AutoVisionImagePathA { get; private set; } = string.Empty;
        public bool? AutoVisionImageTestMode { get; private set; }
        public bool? ClearMachineAlarmsShadowMode { get; private set; }

        public static ConfigSnapshot Load(string path)
        {
            var snapshot = new ConfigSnapshot
            {
                Exists = File.Exists(path)
            };
            if (!snapshot.Exists)
            {
                return snapshot;
            }

            try
            {
                using var stream = File.OpenRead(path);
                using var doc = JsonDocument.Parse(stream);
                var root = doc.RootElement;
                snapshot.WorkHttpServerPrefix = ReadString(root, "WorkHttpServerPrefix", DefaultWorkHttpServerPrefix);
                snapshot.AutoVisionKey = ReadString(root, "AutoVisionKey", string.Empty);
                snapshot.AutoVisionImagePathA = ReadString(root, "AutoVisionImagePathA", string.Empty);
                snapshot.AutoVisionImageTestMode = ReadBool(root, "AutoVisionImageTestMode");
                snapshot.ClearMachineAlarmsShadowMode = ReadBool(root, "ClearMachineAlarmsShadowMode");
                snapshot.IsReadable = true;
            }
            catch (Exception ex)
            {
                snapshot.Error = ex.Message;
            }

            return snapshot;
        }

        private static string ReadString(JsonElement root, string name, string fallback)
        {
            return root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
                ? prop.GetString() ?? fallback
                : fallback;
        }

        private static bool? ReadBool(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var prop))
            {
                return null;
            }

            return prop.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String when bool.TryParse(prop.GetString(), out var value) => value,
                _ => null
            };
        }
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

    private sealed class CsvLogger : IDisposable
    {
        private readonly StreamWriter _writer;

        public CsvLogger(string path)
        {
            var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(stream, new UTF8Encoding(true)) { AutoFlush = true };
        }

        public void WriteHeader()
        {
            WriteRow(
                "Index",
                "Total",
                "Status",
                "SourceImage",
                "ReplayImage",
                "StartedAt",
                "FinishedAt",
                "ElapsedSeconds",
                "HttpStatus",
                "Accepted",
                "IsVisionRelated",
                "SkippedRejectedBlocks",
                "Workflow",
                "WorkflowKey",
                "ToolSignature",
                "FinalReplySignature",
                "FinalReplyPreview",
                "Error");
        }

        public void WriteRecord(RunRecord record)
        {
            WriteRow(
                record.Index.ToString(CultureInfo.InvariantCulture),
                record.Total.ToString(CultureInfo.InvariantCulture),
                record.Status,
                record.SourceImagePath,
                record.ReplayImagePath,
                record.StartedAt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                record.FinishedAt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                record.Elapsed.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture),
                record.HttpStatusCode.ToString(CultureInfo.InvariantCulture),
                record.Accepted ? "true" : "false",
                FormatNullableBool(record.IsVisionRelated),
                record.SkippedRejectedBlocks.ToString(CultureInfo.InvariantCulture),
                record.Workflow,
                record.WorkflowKey,
                record.ToolSignature,
                record.FinalReplySignature,
                TruncateForCsv(record.FinalReply, 240),
                TruncateForCsv(record.Error, 500));
        }

        private void WriteRow(params string[] values)
        {
            _writer.WriteLine(string.Join(",", values.Select(Escape)));
        }

        private static string Escape(string? value)
        {
            var text = value ?? string.Empty;
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }

        public void Dispose()
        {
            _writer.Dispose();
        }
    }

    private sealed class Options
    {
        public string ImageDir { get; set; } = string.Empty;
        public string ReplaySlot { get; set; } = string.Empty;
        public string Prompt { get; set; } = string.Empty;
        public string ErrorCode { get; set; } = "0";
        public string ServerUrl { get; set; } = string.Empty;
        public string ConfigPath { get; set; } = DefaultConfigPath;
        public bool Recursive { get; set; }
        public string Order { get; set; } = "name";
        public int SkipCount { get; set; }
        public int Limit { get; set; }
        public int RunTimeoutSeconds { get; set; } = 180;
        public int RequestTimeoutSeconds { get; set; } = 30;
        public int InitialDelayMs { get; set; }
        public int DelayBetweenRunsMs { get; set; } = 500;
        public int PollIntervalMs { get; set; } = 500;
        public int AcceptRetries { get; set; } = -1;
        public int AcceptTimeoutSeconds { get; set; } = 600;
        public int AcceptRetryDelayMs { get; set; } = 1000;
        public bool OnlyMajorNodes { get; set; } = true;
        public bool DryRun { get; set; }
        public bool StopOnError { get; set; }
        public bool AllowLiveActions { get; set; }
        public bool SkipConfigCheck { get; set; }
        public bool ShowHelp { get; set; }

        public static Options Parse(string[] args)
        {
            var options = new Options();
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                switch (arg)
                {
                    case "--image-dir":
                        options.ImageDir = ReadValue(args, ref i);
                        break;
                    case "--replay-slot":
                        options.ReplaySlot = ReadValue(args, ref i);
                        break;
                    case "--prompt":
                        options.Prompt = ReadValue(args, ref i);
                        break;
                    case "--error-code":
                        options.ErrorCode = ReadValue(args, ref i);
                        break;
                    case "--server":
                        options.ServerUrl = ReadValue(args, ref i);
                        break;
                    case "--config":
                        options.ConfigPath = ReadValue(args, ref i);
                        break;
                    case "--recursive":
                        options.Recursive = true;
                        break;
                    case "--order":
                        options.Order = NormalizeOrder(ReadValue(args, ref i));
                        break;
                    case "--skip":
                        options.SkipCount = ParseInt(ReadValue(args, ref i), 0);
                        break;
                    case "--limit":
                        options.Limit = ParseInt(ReadValue(args, ref i), 0);
                        break;
                    case "--run-timeout-seconds":
                        options.RunTimeoutSeconds = ParseInt(ReadValue(args, ref i), 180);
                        break;
                    case "--request-timeout-seconds":
                        options.RequestTimeoutSeconds = ParseInt(ReadValue(args, ref i), 30);
                        break;
                    case "--initial-delay-ms":
                        options.InitialDelayMs = ParseInt(ReadValue(args, ref i), 0);
                        break;
                    case "--delay-ms":
                        options.DelayBetweenRunsMs = ParseInt(ReadValue(args, ref i), 500);
                        break;
                    case "--poll-ms":
                        options.PollIntervalMs = ParseInt(ReadValue(args, ref i), 500);
                        break;
                    case "--accept-retries":
                        options.AcceptRetries = ParseInt(ReadValue(args, ref i), -1);
                        break;
                    case "--accept-timeout-seconds":
                        options.AcceptTimeoutSeconds = ParseInt(ReadValue(args, ref i), 600);
                        break;
                    case "--accept-retry-delay-ms":
                        options.AcceptRetryDelayMs = ParseInt(ReadValue(args, ref i), 1000);
                        break;
                    case "--all-nodes":
                        options.OnlyMajorNodes = false;
                        break;
                    case "--dry-run":
                        options.DryRun = true;
                        break;
                    case "--stop-on-error":
                        options.StopOnError = true;
                        break;
                    case "--allow-live-actions":
                        options.AllowLiveActions = true;
                        break;
                    case "--skip-config-check":
                        options.SkipConfigCheck = true;
                        break;
                    case "--help":
                    case "-h":
                    case "/?":
                        options.ShowHelp = true;
                        break;
                }
            }

            options.SkipCount = Math.Max(0, options.SkipCount);
            options.Limit = Math.Max(0, options.Limit);
            options.RunTimeoutSeconds = Math.Max(10, options.RunTimeoutSeconds);
            options.RequestTimeoutSeconds = Math.Max(5, options.RequestTimeoutSeconds);
            options.InitialDelayMs = Math.Max(0, options.InitialDelayMs);
            options.DelayBetweenRunsMs = Math.Max(0, options.DelayBetweenRunsMs);
            options.PollIntervalMs = Math.Max(100, options.PollIntervalMs);
            options.AcceptRetries = Math.Max(-1, options.AcceptRetries);
            options.AcceptTimeoutSeconds = Math.Max(10, options.AcceptTimeoutSeconds);
            options.AcceptRetryDelayMs = Math.Max(100, options.AcceptRetryDelayMs);
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
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : fallback;
        }

        private static string NormalizeOrder(string raw)
        {
            var value = (raw ?? string.Empty).Trim().ToLowerInvariant();
            return value is "time" or "time-desc" ? value : "name";
        }
    }
}

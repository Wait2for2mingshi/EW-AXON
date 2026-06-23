using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EW_Assistant.Domain.Reports;
using EW_Assistant;
using EW_Assistant.Diagnostics;
using EW_Assistant.Services;

namespace EW_Assistant.Services.Reports
{
    /// <summary>
    /// 报表生成服务：负责调用 LLM 获取 Markdown，并保存到本地仓库。
    /// </summary>
    public class ReportGeneratorService
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> s_reportLocks = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
        private static readonly SemaphoreSlim s_generationLane = new SemaphoreSlim(1, 1);
        private readonly ReportStorageService _storage;
        private readonly LlmWorkflowClient _llm;
        private readonly DailyProdCalculator _dailyProdCalculator;
        private readonly DailyAlarmCalculator _dailyAlarmCalculator;
        private readonly WeeklyProdCalculator _weeklyProdCalculator;
        private readonly WeeklyAlarmCalculator _weeklyAlarmCalculator;

        public ReportGeneratorService(ReportStorageService storage = null, LlmWorkflowClient llm = null)
        {
            _storage = storage ?? new ReportStorageService();
            _llm = llm ?? new LlmWorkflowClient();
            _dailyProdCalculator = new DailyProdCalculator();
            _dailyAlarmCalculator = new DailyAlarmCalculator();
            _weeklyProdCalculator = new WeeklyProdCalculator();
            _weeklyAlarmCalculator = new WeeklyAlarmCalculator();
        }

        public Task<ReportInfo> GenerateDailyProdAsync(DateTime date, CancellationToken token = default(CancellationToken), bool force = false)
        {
            return GenerateDailyReportAsync(
                ReportType.DailyProd,
                date,
                token,
                force,
                _dailyProdCalculator.Calculate,
                GetDailyProdSkipReason,
                DailyProdReportPromptBuilder.BuildPayload,
                DailyProdReportMarkdownFormatter.Render,
                "生成日报失败");
        }

        public Task<ReportInfo> GenerateDailyAlarmAsync(DateTime date, CancellationToken token = default(CancellationToken), bool force = false)
        {
            return GenerateDailyReportAsync(
                ReportType.DailyAlarm,
                date,
                token,
                force,
                _dailyAlarmCalculator.Calculate,
                GetDailyAlarmSkipReason,
                DailyAlarmReportPromptBuilder.BuildPayload,
                DailyAlarmReportMarkdownFormatter.Render,
                "生成报警日报失败");
        }

        public Task<ReportInfo> GenerateWeeklyProdAsync(DateTime endDate, CancellationToken token = default(CancellationToken), bool force = false)
        {
            return GenerateWeeklyReportAsync(
                ReportType.WeeklyProd,
                endDate,
                token,
                force,
                _weeklyProdCalculator.Calculate,
                GetWeeklyProdSkipReason,
                WeeklyProdReportPromptBuilder.BuildPayload,
                WeeklyProdReportMarkdownFormatter.Render,
                "生成产能周报失败");
        }

        public Task<ReportInfo> GenerateWeeklyAlarmAsync(DateTime endDate, CancellationToken token = default(CancellationToken), bool force = false)
        {
            return GenerateWeeklyReportAsync(
                ReportType.WeeklyAlarm,
                endDate,
                token,
                force,
                _weeklyAlarmCalculator.Calculate,
                GetWeeklyAlarmSkipReason,
                WeeklyAlarmReportPromptBuilder.BuildPayload,
                WeeklyAlarmReportMarkdownFormatter.Render,
                "生成报警周报失败");
        }

        private Task<ReportInfo> GenerateDailyReportAsync<TData>(
            ReportType type,
            DateTime date,
            CancellationToken token,
            bool force,
            Func<DateTime, TData> calculate,
            Func<TData, string> getSkipReason,
            Func<TData, ReportPromptPayload> buildPayload,
            Func<TData, string, string> renderMarkdown,
            string failureMessage)
        {
            var reportDate = date.Date;
            var reportPath = _storage.GetDailyReportPath(type, reportDate);
            return GenerateReportAsync(
                type,
                reportDate,
                null,
                reportPath,
                token,
                force,
                () => calculate(reportDate),
                getSkipReason,
                buildPayload,
                renderMarkdown,
                (markdown, analysisMarkdown, dataJson) => _storage.SaveReportPackage(type, reportDate, markdown, analysisMarkdown, dataJson),
                failureMessage);
        }

        private Task<ReportInfo> GenerateWeeklyReportAsync<TData>(
            ReportType type,
            DateTime endDate,
            CancellationToken token,
            bool force,
            Func<DateTime, DateTime, TData> calculate,
            Func<TData, string> getSkipReason,
            Func<TData, ReportPromptPayload> buildPayload,
            Func<TData, string, string> renderMarkdown,
            string failureMessage)
        {
            var reportEnd = endDate.Date;
            var reportStart = reportEnd.AddDays(-6);
            var reportPath = _storage.GetWeeklyReportPath(type, reportStart, reportEnd);
            return GenerateReportAsync(
                type,
                reportStart,
                reportEnd,
                reportPath,
                token,
                force,
                () => calculate(reportStart, reportEnd),
                getSkipReason,
                buildPayload,
                renderMarkdown,
                (markdown, analysisMarkdown, dataJson) => _storage.SaveReportPackage(type, reportStart, reportEnd, markdown, analysisMarkdown, dataJson),
                failureMessage);
        }

        private async Task<ReportInfo> GenerateReportAsync<TData>(
            ReportType type,
            DateTime start,
            DateTime? end,
            string reportPath,
            CancellationToken token,
            bool force,
            Func<TData> calculate,
            Func<TData, string> getSkipReason,
            Func<TData, ReportPromptPayload> buildPayload,
            Func<TData, string, string> renderMarkdown,
            Func<string, string, string, string> savePackage,
            string failureMessage)
        {
            string promptText = null;
            await s_generationLane.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var reportLock = GetReportLock(reportPath);
                await reportLock.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var current = _storage.GetReportInfoByPath(type, reportPath);
                    var existing = _storage.IsReportPackageComplete(reportPath) ? current : null;
                    if (!force && existing != null)
                    {
                        return existing;
                    }

                    token.ThrowIfCancellationRequested();

                    var data = calculate();
                    var skipReason = getSkipReason != null ? getSkipReason(data) : null;
                    if (!string.IsNullOrWhiteSpace(skipReason))
                    {
                        var cleanedExisting = CleanupSkippedExistingReport(current, force);
                        LogSkippedGeneration(type, start, end, skipReason, cleanedExisting);
                        return null;
                    }

                    var prompt = buildPayload != null ? buildPayload(data) : null;
                    if (prompt == null)
                    {
                        throw new InvalidOperationException("未能构建报表 Prompt。");
                    }

                    LogGenerationStarted(type, start, end);
                    promptText = BuildPromptTraceText(prompt);
                    var analysisMd = await _llm.GenerateMarkdownAsync(prompt.ReportTask, prompt.ReportDataJson, token).ConfigureAwait(false);
                    analysisMd = NormalizeAnalysisMarkdown(type, analysisMd);
                    var fullMd = renderMarkdown(data, analysisMd);
                    var path = savePackage(fullMd, analysisMd, prompt.ReportDataJson);

                    var info = _storage.GetReportInfoByPath(type, path) ?? BuildFallbackInfo(type, start, end, path);
                    LogGeneration(type, start, end, path, true, null, fullMd, promptText);
                    return info;
                }
                finally
                {
                    reportLock.Release();
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogGeneration(type, start, end, null, false, ex.Message, null, promptText);
                throw new ReportGenerationException(failureMessage + "：" + ex.Message, ex);
            }
            finally
            {
                s_generationLane.Release();
            }
        }

        private ReportInfo BuildFallbackInfo(ReportType type, DateTime start, DateTime? end, string path)
        {
            var display = _storage.GetTypeDisplayName(type);
            var title = display;
            var dateLabel = string.Empty;
            if (type.IsDaily())
            {
                title = string.Format("{0}（{1:yyyy-MM-dd}）", display, start);
                dateLabel = start.ToString("yyyy-MM-dd");
            }
            else if (end.HasValue)
            {
                title = string.Format("{0}（{1:yyyy-MM-dd}~{2:yyyy-MM-dd}）", display, start, end.Value);
                dateLabel = string.Format("{0:yyyy-MM-dd} ~ {1:yyyy-MM-dd}", start, end.Value);
            }

            long size = 0;
            try
            {
                var fi = new FileInfo(path);
                if (fi.Exists) size = fi.Length;
            }
            catch { }

            return new ReportInfo
            {
                Type = type,
                TypeDisplayName = display,
                Id = Path.GetFileNameWithoutExtension(path),
                Title = title,
                DateLabel = dateLabel,
                Date = type.IsDaily() ? (DateTime?)start : null,
                StartDate = type.IsWeekly() ? (DateTime?)start : null,
                EndDate = type.IsWeekly() ? end : null,
                FilePath = path,
                FileName = Path.GetFileName(path),
                GeneratedAt = DateTime.Now,
                FileSize = size,
                FileSizeText = FormatFileSizeForFallback(size),
                IsToday = type.IsDaily() && start.Date == DateTime.Now.Date
            };
        }

        private static string FormatFileSizeForFallback(long size)
        {
            const double OneK = 1024d;
            const double OneM = OneK * 1024d;
            const double OneG = OneM * 1024d;

            if (size >= OneG) return string.Format("{0:0.##} GB", size / OneG);
            if (size >= OneM) return string.Format("{0:0.##} MB", size / OneM);
            if (size >= OneK) return string.Format("{0:0.##} KB", size / OneK);
            return size + " B";
        }

        private static string BuildPromptTraceText(ReportPromptPayload prompt)
        {
            if (prompt == null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(prompt.CombinedPrompt))
            {
                return prompt.CombinedPrompt;
            }

            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(prompt.ReportTask))
            {
                sb.AppendLine("【REPORT_TASK】");
                sb.AppendLine(prompt.ReportTask.Trim());
            }

            if (!string.IsNullOrWhiteSpace(prompt.ReportDataJson))
            {
                sb.AppendLine("【REPORT_DATA_JSON】");
                sb.AppendLine(prompt.ReportDataJson.Trim());
            }

            return sb.Length > 0 ? sb.ToString() : null;
        }

        private static string NormalizeAnalysisMarkdown(ReportType type, string analysisMarkdown)
        {
            analysisMarkdown = DifyOutputSanitizer.Clean(analysisMarkdown);
            if (string.IsNullOrWhiteSpace(analysisMarkdown))
            {
                return string.Empty;
            }

            var extracted = ReportAnalysisMarkdownExtractor.Extract(type, analysisMarkdown);
            return !string.IsNullOrWhiteSpace(extracted) ? extracted.Trim() : analysisMarkdown.Trim();
        }

        private static string GetDailyProdSkipReason(DailyProdData data)
        {
            return data != null && data.DayTotal > 0
                ? null
                : "总产量为 0，已跳过当日产能报表生成。";
        }

        private static string GetDailyAlarmSkipReason(DailyAlarmData data)
        {
            return data != null && (data.DayAlarmCount > 0 || data.DayAlarmSeconds > 0)
                ? null
                : "报警条数和报警时长均为 0，已跳过当日报警报表生成。";
        }

        private static string GetWeeklyProdSkipReason(WeeklyProdData data)
        {
            return data != null && data.Total > 0
                ? null
                : "周总产量为 0，已跳过产能周报生成。";
        }

        private static string GetWeeklyAlarmSkipReason(WeeklyAlarmData data)
        {
            return data != null && (data.TotalCount > 0 || data.TotalDurationSeconds > 0)
                ? null
                : "周报警条数和总时长均为 0，已跳过报警周报生成。";
        }

        private bool CleanupSkippedExistingReport(ReportInfo existing, bool force)
        {
            if (existing == null)
            {
                return false;
            }

            var isComplete = _storage.IsReportPackageComplete(existing.FilePath);
            if (!force && isComplete)
            {
                return false;
            }

            _storage.DeleteReport(existing);
            return true;
        }

        private static SemaphoreSlim GetReportLock(string reportPath)
        {
            var key = string.IsNullOrWhiteSpace(reportPath)
                ? Guid.NewGuid().ToString("N")
                : reportPath;
            return s_reportLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        }

        private void LogSkippedGeneration(ReportType type, DateTime start, DateTime? end, string reason, bool cleanedExisting)
        {
            try
            {
                var display = _storage.GetTypeDisplayName(type);
                var sb = new StringBuilder();
                sb.Append("[跳过生成] ").Append(display).Append(" ");
                if (type.IsDaily())
                {
                    sb.Append(start.ToString("yyyy-MM-dd"));
                }
                else if (end.HasValue)
                {
                    sb.Append(start.ToString("yyyy-MM-dd")).Append("~").Append(end.Value.ToString("yyyy-MM-dd"));
                }

                sb.Append(" | 原因: ").Append(reason ?? "无有效数据");
                if (cleanedExisting)
                {
                    sb.Append(" | 已清理旧残留报表");
                }

                AppendLocalReportLog(sb.ToString(), null, null);
            }
            catch
            {
                // 忽略跳过日志异常
            }
        }

        /// <summary>记录生成日志（含 LLM 请求与生成内容），失败不抛出。</summary>
        private void LogGenerationStarted(ReportType type, DateTime start, DateTime? end)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("开始生成");
                sb.Append(type.IsWeekly() ? "周报: " : "日报: ");
                sb.Append(type);
                sb.Append(" ");
                sb.Append(start.ToString("yyyy-MM-dd"));
                if (type.IsWeekly() && end.HasValue)
                {
                    sb.Append("~").Append(end.Value.ToString("yyyy-MM-dd"));
                }

                MainWindow.PostProgramInfo(sb.ToString(), "info");
            }
            catch
            {
                // 忽略开始日志异常，不影响主流程
            }
        }

        /// <summary>记录生成日志（含 LLM 请求与生成内容），失败不抛出。</summary>
        private void LogGeneration(ReportType type, DateTime start, DateTime? end, string path, bool success, string message, string content, string llmRequest)
        {
            try
            {
                var display = _storage.GetTypeDisplayName(type);
                var sb = new StringBuilder();
                sb.Append(success ? "[生成完成] " : "[生成失败] ");
                sb.Append(display);

                if (type.IsDaily())
                {
                    sb.Append(" ").Append(start.ToString("yyyy-MM-dd"));
                }
                else if (end.HasValue)
                {
                    sb.Append(" ").Append(start.ToString("yyyy-MM-dd")).Append("~").Append(end.Value.ToString("yyyy-MM-dd"));
                }

                if (!string.IsNullOrWhiteSpace(path))
                {
                    sb.Append(" | 文件: ").Append(path);
                }

                if (!string.IsNullOrWhiteSpace(message))
                {
                    sb.Append(" | 详情: ").Append(message);
                }

                MainWindow.PostProgramInfo(sb.ToString(), success ? "info" : "warn");
                AppendLocalReportLog(sb.ToString(), content, llmRequest);
            }
            catch
            {
                // 绝不让日志异常影响生成流程
            }
        }

        internal static string GetWorkflowTransportFailureMessage(Exception ex)
        {
            var current = ex;
            while (current != null)
            {
                if (IsWorkflowTransportFailureMessage(current.Message))
                {
                    return current.Message;
                }

                current = current.InnerException;
            }

            return null;
        }

        private static bool IsWorkflowTransportFailureMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            if (ContainsIgnoreCase(message, "发送请求时出错")
                || ContainsIgnoreCase(message, "An error occurred while sending the request"))
            {
                return true;
            }

            if (ContainsIgnoreCase(message, "由于目标计算机积极拒绝")
                || ContainsIgnoreCase(message, "actively refused")
                || ContainsIgnoreCase(message, "未能解析此远程名称")
                || ContainsIgnoreCase(message, "No such host is known")
                || ContainsIgnoreCase(message, "连接尝试失败")
                || ContainsIgnoreCase(message, "The SSL connection could not be established")
                || ContainsIgnoreCase(message, "远程主机强迫关闭了一个现有连接")
                || ContainsIgnoreCase(message, "forcibly closed by the remote host"))
            {
                return true;
            }

            return false;
        }

        private static bool ContainsIgnoreCase(string text, string keyword)
        {
            return !string.IsNullOrWhiteSpace(text)
                && !string.IsNullOrWhiteSpace(keyword)
                && text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void AppendLocalReportLog(string line, string content, string llmRequest)
        {
            try
            {
                var dir = Path.Combine(@"D:\Data", "AiLog", "Reports");
                Directory.CreateDirectory(dir);
                var fileName = "report-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log";
                var path = Path.Combine(dir, fileName);
                var sb = new StringBuilder();
                sb.AppendFormat("[{0:yyyy-MM-dd HH:mm:ss}] ", DateTime.Now);
                sb.Append(line ?? string.Empty);
                sb.AppendLine();
                if (!string.IsNullOrWhiteSpace(llmRequest))
                {
                    sb.AppendLine("==== LLM 请求 ====");
                    sb.AppendLine(llmRequest.Trim());
                    sb.AppendLine("==== 结束 ====");
                }
                if (!string.IsNullOrWhiteSpace(content))
                {
                    sb.AppendLine("==== 报表内容 ====");
                    sb.AppendLine(content.Trim());
                    sb.AppendLine("==== 结束 ====");
                }

                using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                using (var sw = new StreamWriter(fs, new UTF8Encoding(false)))
                {
                    sw.WriteLine(sb.ToString());
                    sw.Flush();
                }

                LogRetentionPolicy.TryCleanupFiles(
                    dir,
                    "*.log",
                    SearchOption.TopDirectoryOnly,
                    TimeSpan.FromDays(30));
            }
            catch
            {
                // 文件日志失败静默
            }
        }
    }

    /// <summary>
    /// 报表生成异常，便于上层捕获后提示用户。
    /// </summary>
    public class ReportGenerationException : Exception
    {
        public ReportGenerationException(string message, Exception inner = null) : base(message, inner) { }
    }
}

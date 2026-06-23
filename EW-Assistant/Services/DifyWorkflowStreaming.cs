// Net/DifyWorkflowClient.cs
using EW_Assistant.Diagnostics;
using EW_Assistant.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using EW_Assistant.Settings;
using EW_Assistant.Warnings;

namespace EW_Assistant.Net
{
    /// <summary>
    /// 面向本地 WPF 的 Dify Workflow 流式调用封装，内置并发闸门与日志记录，
    /// 支持“独占执行”与“尝试触发”两种入口，结果流向 AIAssistantView。
    /// </summary>
    public static class DifyWorkflowClient
    {
        // ====== 并发闸门（一次只跑一个问答）======
        private static readonly TimeSpan AutoWorkflowHardTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan AutoVisionImageAcquireTimeout = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan AutoVisionImagePollInterval = TimeSpan.FromMilliseconds(500);
        private static readonly TimeSpan AutoVisionImageStableInterval = TimeSpan.FromMilliseconds(300);
        private const int AutoVisionImageDirectorySearchMaxDepth = 6;
        private const string AutoVisionFixedLane = "A";
        private const string AutoVisionWorkflowImageVariableName = "picture";
        private const string AutoVisionEmptyReferenceImageVariableName = "empty_reference_picture";
        private const string AutoVisionNormalReferenceImageVariableName = "normal_reference_picture";
        private const string AutoVisionNormalReferenceImageRelativePath = @"Doc\正常有料基准图.jpg";
        private const string AutoVisionWorkflowFilesVariableName = "files";
        private static readonly string[] AutoVisionImageExtensions =
        {
            ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff"
        };
        private static readonly string[] AutoVisionImageFileNameTimeFormats =
        {
            "yyyy-MM-dd HH-mm-ss-fff",
            "yyyy-MM-dd HH-mm-ss"
        };
        private static readonly string[] AutoVisionImageDateFolderFormats =
        {
            "yyyy年MM月dd日",
            "yyyy-MM-dd",
            "yyyyMMdd"
        };
        private static readonly SemaphoreSlim _gate = new(1, 1);
        private static readonly object _currentRunSync = new object();
        private static readonly object _autoVisionCooldownSync = new object();
        private static volatile bool _isBusy = false;
        private static volatile bool _shutdownRequested = false;
        private static CancellationTokenSource _currentRunCts = null;
        private static string _currentTaskId = null;
        private static string _currentRunId = null;
        private static string _currentWorkflowApiKey = null;
        private static DateTime _lastAutoVisionAcceptedAt = DateTime.MinValue;

        public static bool IsBusy => _isBusy;
        public static string CurrentTaskId => _currentTaskId;
        public static string CurrentRunId => _currentRunId;
        public sealed class RunHandle
        {
            public string WorkflowRunId { get; set; }
            public string TaskId { get; set; }  
            public string FinalText { get; set; }
            public bool Succeeded { get; set; }
        }

        public sealed class AutoStartAttemptResult
        {
            public bool Accepted { get; set; }
            public string RejectReason { get; set; }
            public string Message { get; set; }
            public string WorkflowKeyName { get; set; }
            public string WorkflowDisplayName { get; set; }
            public bool IsVisionRelated { get; set; }
        }

        private sealed class AutoWorkflowRoute
        {
            public bool IsReady { get; set; }
            public string ApiBaseUrl { get; set; }
            public string BaseUrl { get; set; }
            public string ApiKey { get; set; }
            public string KeyName { get; set; } = "AutoKey";
            public string WorkflowDisplayName { get; set; } = "普通 AUTO 流程";
            public string RejectReason { get; set; } = "invalid_config";
            public string Message { get; set; } = "AutoURL / AutoKey 未配置。";
            public bool IsVisionRelated { get; set; }
        }

        private sealed class AutoVisionImageAcquireResult
        {
            public bool Succeeded { get; set; }
            public string SourceFilePath { get; set; }
            public string FilePath { get; set; }
            public DateTime FileTime { get; set; }
            public string UploadFileId { get; set; }
            public string VariableName { get; set; }
            public string RunFolderPath { get; set; }
            public string ProcessInfo { get; set; }
            public string ErrorMessage { get; set; }
        }

        private sealed class AutoVisionImageCandidate
        {
            public string FilePath { get; set; }
            public DateTime FileTime { get; set; }
            public string TimeSource { get; set; }
        }

        private sealed class AutoVisionImageProcessResult
        {
            public string FilePath { get; set; }
            public string RunFolderPath { get; set; }
            public string ProcessInfo { get; set; }
        }

        private sealed class AutoVisionImagePathSelection
        {
            public string Lane { get; set; } = string.Empty;
            public string ImagePath { get; set; } = string.Empty;
        }

        /// <summary>
        /// 对外入口（单通道）：按报警知识库标记选择 AutoKey / AutoVisionKey 调用 workflow（streaming）。
        /// 忙则直接返回 null（不排队），仅推送大节点到信息流，完整文本由 AIAssistantView 展示。
        /// </summary>
        public static async Task<RunHandle> RunAutoAnalysisExclusiveAsync(
            EW_Assistant.Views.AIAssistantView aiView,
            string errorCode,
            string prompt,
            string machineCode,
            bool onlyMajorNodes = true,
            string autoTraceId = null,
            CancellationToken ct = default)
        {
            var triggeredAt = DateTime.Now;
            if (_shutdownRequested)
            {
                return null;
            }

            var cfg = EW_Assistant.Services.ConfigService.Current;
            var route = ResolveAutoWorkflowRoute(cfg, prompt);
            if (!route.IsReady)
            {
                Post(route.Message, "warn");
                return null;
            }

            // 尝试立即占用闸门（不等待）
            if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
            {
              //  Post("当前有问答正在执行，请稍后再试。", "warn");
                return null;
            }

            if (route.IsVisionRelated && !TryReserveAutoVisionCooldown(cfg, out var cooldownRemaining))
            {
                _gate.Release();
                Post("视觉 AUTO 冷却中，剩余 " + FormatElapsed(cooldownRemaining) + "。", "warn");
                return null;
            }

            _isBusy = true;
            try
            {
                var runTimeout = ResolveAutoRunTimeout(route);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linkedCts.CancelAfter(runTimeout);
                RegisterCurrentRunCts(linkedCts);
                RegisterCurrentWorkflowApiKey(route.ApiKey);
                var handle = await RunWorkflowStreamingAsync(
                    baseUrl: route.BaseUrl,
                    apiKey: route.ApiKey,
                    errorCode: errorCode ?? "0",
                    prompt: prompt ?? string.Empty,
                    machineCode: machineCode ?? string.Empty,
                    aiView: aiView,
                    onlyMajorNodes: onlyMajorNodes,
                    autoTraceId: autoTraceId,
                    autoWorkflowDisplayName: route.WorkflowDisplayName,
                    autoWorkflowKeyName: route.KeyName,
                    autoApiBaseUrl: route.ApiBaseUrl,
                    autoIsVisionRelated: route.IsVisionRelated,
                    autoTriggeredAt: triggeredAt,
                    autoWorkflowTimeout: runTimeout,
                    ct: linkedCts.Token
                ).ConfigureAwait(false);

                return handle;
            }
            finally
            {
                ClearCurrentRunCts();
                ClearCurrentWorkflowApiKey();
                _currentTaskId = null;
                _currentRunId = null;
                _isBusy = false;
                _gate.Release();
            }
        }

        /// <summary>
        /// 立刻返回：尝试启动一次后台 AI 自动分析（独占闸门）。
        /// true=已受理并在后台执行；false=繁忙或配置不完整；AUTO 可不绑定聊天页接收器。
        /// </summary>
        public static AutoStartAttemptResult TryStartAutoAnalysisNow(
            EW_Assistant.Views.AIAssistantView aiView,
            string errorCode,
            string prompt,
            string machineCode,
            bool onlyMajorNodes = true,
            string autoTraceId = null,
            DateTime? autoTriggeredAt = null)
        {
            var triggeredAt = autoTriggeredAt ?? DateTime.Now;
            if (_shutdownRequested)
            {
                return new AutoStartAttemptResult
                {
                    Accepted = false,
                    RejectReason = "shutting_down",
                    Message = "程序正在退出，已拒绝新的 AUTO 分析。"
                };
            }

            var cfg = EW_Assistant.Services.ConfigService.Current;
            var route = ResolveAutoWorkflowRoute(cfg, prompt);
            if (!route.IsReady)
            {
                Post(route.Message, "warn");
                return new AutoStartAttemptResult
                {
                    Accepted = false,
                    RejectReason = route.RejectReason,
                    Message = route.Message,
                    WorkflowKeyName = route.KeyName,
                    WorkflowDisplayName = route.WorkflowDisplayName,
                    IsVisionRelated = route.IsVisionRelated
                };
            }

            // 无等待占闸门：一次只能跑一个
            if (!_gate.Wait(0))
            {
                return new AutoStartAttemptResult
                {
                    Accepted = false,
                    RejectReason = "busy_running",
                    Message = "当前已有任务执行中。"
                };
            }

            if (route.IsVisionRelated && !TryReserveAutoVisionCooldown(cfg, out var cooldownRemaining))
            {
                try { _gate.Release(); } catch { }
                var message = "视觉 AUTO 冷却中，剩余 " + FormatElapsed(cooldownRemaining) + "。";
                Post(message, "warn");
                return new AutoStartAttemptResult
                {
                    Accepted = false,
                    RejectReason = "auto_vision_cooldown",
                    Message = message,
                    WorkflowKeyName = route.KeyName,
                    WorkflowDisplayName = route.WorkflowDisplayName,
                    IsVisionRelated = route.IsVisionRelated
                };
            }

            _isBusy = true;
            RegisterCurrentWorkflowApiKey(route.ApiKey);
            var runTimeout = ResolveAutoRunTimeout(route);

            // 后台跑（不阻塞调用方）
            Task.Run(async () =>
            {
                using var timeoutCts = new CancellationTokenSource(runTimeout);
                try
                {
                    RegisterCurrentRunCts(timeoutCts);
                    RegisterCurrentWorkflowApiKey(route.ApiKey);
                    await RunWorkflowStreamingAsync(
                        baseUrl: route.BaseUrl,
                        apiKey: route.ApiKey,
                        errorCode: string.IsNullOrWhiteSpace(errorCode) ? "0" : errorCode,
                        prompt: prompt ?? string.Empty,
                        machineCode: machineCode ?? string.Empty,
                        aiView: aiView,                 // 可为 null，内部已判空
                        onlyMajorNodes: onlyMajorNodes,
                        autoTraceId: autoTraceId,
                        autoWorkflowDisplayName: route.WorkflowDisplayName,
                        autoWorkflowKeyName: route.KeyName,
                        autoApiBaseUrl: route.ApiBaseUrl,
                        autoIsVisionRelated: route.IsVisionRelated,
                        autoTriggeredAt: triggeredAt,
                        autoWorkflowTimeout: runTimeout,
                        ct: timeoutCts.Token
                    ).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    var timeoutMessage = _shutdownRequested
                        ? "AUTO 分析已因程序退出取消。"
                        : "AUTO 分析超时，已在 " + FormatElapsed(runTimeout) + " 后中止。";
                    AutoClearAlarmAudit.MarkWorkflowDispatchFailed(autoTraceId, timeoutMessage);
                    MainWindow.NotifyAutoDispatchFailed(autoTraceId, timeoutMessage);
                    Post(timeoutMessage, "warn");
                    WriteLog(timeoutMessage);
                }
                catch (Exception ex)
                {
                    AutoClearAlarmAudit.MarkWorkflowDispatchFailed(autoTraceId, "后台执行异常: " + ex.Message);
                    MainWindow.NotifyAutoDispatchFailed(autoTraceId, ex.Message);
                    Post("后台执行异常：" + ex.Message, "warn");
                }
                finally
                {
                    ClearCurrentRunCts();
                    ClearCurrentWorkflowApiKey();
                    _currentTaskId = null;
                    _currentRunId = null;
                    _isBusy = false;
                    try { _gate.Release(); } catch { }
                }
            });

            return new AutoStartAttemptResult
            {
                Accepted = true,
                RejectReason = string.Empty,
                Message = "accepted",
                WorkflowKeyName = route.KeyName,
                WorkflowDisplayName = route.WorkflowDisplayName,
                IsVisionRelated = route.IsVisionRelated
            };
        }

        public static void RequestShutdown()
        {
            _shutdownRequested = true;

            CancellationTokenSource cts;
            lock (_currentRunSync)
            {
                cts = _currentRunCts;
            }

            try { cts?.Cancel(); } catch { }

            if (!string.IsNullOrWhiteSpace(_currentTaskId))
            {
                _ = StopCurrentAsyncAuto();
            }
        }

        private static void RegisterCurrentRunCts(CancellationTokenSource cts)
        {
            if (cts == null)
                return;

            lock (_currentRunSync)
            {
                _currentRunCts = cts;
            }

            if (_shutdownRequested)
            {
                try { cts.Cancel(); } catch { }
            }
        }

        private static void ClearCurrentRunCts()
        {
            lock (_currentRunSync)
            {
                _currentRunCts = null;
            }
        }

        private static void RegisterCurrentWorkflowApiKey(string apiKey)
        {
            lock (_currentRunSync)
            {
                _currentWorkflowApiKey = apiKey;
            }
        }

        private static void ClearCurrentWorkflowApiKey()
        {
            lock (_currentRunSync)
            {
                _currentWorkflowApiKey = null;
            }
        }

        private static string GetCurrentWorkflowApiKey()
        {
            lock (_currentRunSync)
            {
                return _currentWorkflowApiKey;
            }
        }

        private static bool TryReserveAutoVisionCooldown(AppConfig cfg, out TimeSpan remaining)
        {
            remaining = TimeSpan.Zero;
            if (cfg?.AutoVisionImageTestMode == true)
            {
                return true;
            }

            var cooldownSeconds = Math.Max(0, Math.Min(3600, cfg?.AutoVisionCooldownSeconds ?? 180));
            if (cooldownSeconds <= 0)
            {
                return true;
            }

            var cooldown = TimeSpan.FromSeconds(cooldownSeconds);
            var now = DateTime.Now;
            lock (_autoVisionCooldownSync)
            {
                if (_lastAutoVisionAcceptedAt != DateTime.MinValue)
                {
                    var elapsed = now - _lastAutoVisionAcceptedAt;
                    if (elapsed < TimeSpan.Zero)
                    {
                        elapsed = TimeSpan.Zero;
                    }

                    if (elapsed < cooldown)
                    {
                        remaining = cooldown - elapsed;
                        return false;
                    }
                }

                _lastAutoVisionAcceptedAt = now;
                return true;
            }
        }

        /// <summary>
        /// 流式运行（SSE）。只把“大节点”推到信息流；最终文本推到 AIAssistantView。
        /// </summary>
        public static async Task<RunHandle> RunWorkflowStreamingAsync(
            string baseUrl,
            string apiKey,
            string errorCode,
            string prompt,           // ErrorDesc
            string machineCode,
            EW_Assistant.Views.AIAssistantView aiView,
            bool onlyMajorNodes = true,           // 只上报大节点
            string autoTraceId = null,
            string autoWorkflowDisplayName = null,
            string autoWorkflowKeyName = null,
            string autoApiBaseUrl = null,
            bool autoIsVisionRelated = false,
            DateTime? autoTriggeredAt = null,
            TimeSpan? autoWorkflowTimeout = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var effectiveMachineCode = ResolveMachineCode(machineCode);

            var handle = new RunHandle();
            var stopwatch = Stopwatch.StartNew();
            var runTimeout = autoWorkflowTimeout ?? AutoWorkflowHardTimeout;
            string autoVisionRunFolderPath = null;

            try
            {
                WriteLog(prompt);

                var ioInput = string.Empty;
                var alarmContext = string.Empty;
                BuildAlarmContext(prompt, out ioInput, out alarmContext, out var isVisionRelated);
                var workflowDisplayName = string.IsNullOrWhiteSpace(autoWorkflowDisplayName)
                    ? (isVisionRelated ? "视觉 AUTO 流程" : "普通 AUTO 流程")
                    : autoWorkflowDisplayName.Trim();
                var triggerLog = BuildAutoTriggerLog(effectiveMachineCode, prompt);
                if (!string.IsNullOrWhiteSpace(triggerLog))
                {
                    Post(triggerLog, "error");
                }

                var inputs = new JObject
                {
                    ["ErrorCode"] = errorCode ?? "0",
                    ["ErrorDesc"] = prompt ?? string.Empty,
                    ["machineCode"] = effectiveMachineCode ?? string.Empty,
                    ["IOInput"] = ioInput ?? string.Empty,
                    ["alarm_context"] = alarmContext ?? string.Empty
                };
                JArray autoVisionSysFiles = null;

                if (autoIsVisionRelated)
                {
                    WriteAutoVisionLog(autoTraceId, "workflow_prepare",
                        "视觉 AUTO 开始准备请求；workflow=" + workflowDisplayName
                        + "，keyName=" + (autoWorkflowKeyName ?? string.Empty)
                        + "，machineCode=" + (effectiveMachineCode ?? string.Empty)
                        + "，errorCode=" + (errorCode ?? string.Empty)
                        + "，triggeredAt=" + (autoTriggeredAt ?? DateTime.Now).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));

                    AutoVisionImageAcquireResult emptyReferenceImageResult = null;
                    AutoVisionImageAcquireResult normalReferenceImageResult = null;
                    AutoVisionImageAcquireResult imageResult;
                    try
                    {
                        emptyReferenceImageResult = await AcquireAndUploadAutoVisionEmptyReferenceImageAsync(
                            apiBaseUrl: autoApiBaseUrl,
                            apiKey: apiKey,
                            autoTraceId: autoTraceId,
                            ct: ct).ConfigureAwait(false);

                        if (emptyReferenceImageResult == null || !emptyReferenceImageResult.Succeeded)
                        {
                            handle.FinalText = emptyReferenceImageResult?.ErrorMessage ?? "视觉 AUTO 无料基准图获取失败。";
                            AutoClearAlarmAudit.MarkWorkflowDispatchFailed(autoTraceId, handle.FinalText);
                            MainWindow.NotifyAutoDispatchFailed(autoTraceId, handle.FinalText);
                            Post(handle.FinalText, "error");
                            WriteLog(handle.FinalText);
                            WriteAutoVisionLog(autoTraceId, "workflow_abort", handle.FinalText);
                            return handle;
                        }

                        normalReferenceImageResult = await AcquireAndUploadAutoVisionNormalReferenceImageAsync(
                            apiBaseUrl: autoApiBaseUrl,
                            apiKey: apiKey,
                            autoTraceId: autoTraceId,
                            ct: ct).ConfigureAwait(false);

                        if (normalReferenceImageResult == null || !normalReferenceImageResult.Succeeded)
                        {
                            handle.FinalText = normalReferenceImageResult?.ErrorMessage ?? "视觉 AUTO 正常有料基准图获取失败。";
                            AutoClearAlarmAudit.MarkWorkflowDispatchFailed(autoTraceId, handle.FinalText);
                            MainWindow.NotifyAutoDispatchFailed(autoTraceId, handle.FinalText);
                            Post(handle.FinalText, "error");
                            WriteLog(handle.FinalText);
                            WriteAutoVisionLog(autoTraceId, "workflow_abort", handle.FinalText);
                            return handle;
                        }

                        imageResult = await AcquireAndUploadAutoVisionImageAsync(
                            apiBaseUrl: autoApiBaseUrl,
                            apiKey: apiKey,
                            errorDesc: prompt,
                            alarmTriggeredAt: autoTriggeredAt ?? DateTime.Now,
                            autoTraceId: autoTraceId,
                            ct: ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        WriteAutoVisionLog(autoTraceId, "image_acquire_exception", "视觉 AUTO 图片获取或上传异常。", ex);
                        imageResult = new AutoVisionImageAcquireResult
                        {
                            Succeeded = false,
                            ErrorMessage = "视觉 AUTO 图片上传失败：" + ex.Message
                        };
                    }

                    if (imageResult == null || !imageResult.Succeeded)
                    {
                        handle.FinalText = imageResult?.ErrorMessage ?? "视觉 AUTO 图片获取失败。";
                        AutoClearAlarmAudit.MarkWorkflowDispatchFailed(autoTraceId, handle.FinalText);
                        MainWindow.NotifyAutoDispatchFailed(autoTraceId, handle.FinalText);
                        Post(handle.FinalText, "error");
                        WriteLog(handle.FinalText);
                        WriteAutoVisionLog(autoTraceId, "workflow_abort", handle.FinalText);
                        return handle;
                    }

                    autoVisionRunFolderPath = imageResult.RunFolderPath;
                    autoVisionSysFiles = BuildAutoVisionWorkflowFilesInput(
                        emptyReferenceImageResult.UploadFileId,
                        normalReferenceImageResult.UploadFileId,
                        imageResult.UploadFileId);
                    WriteAutoVisionLog(autoTraceId, "workflow_input_ready",
                        "图片变量已写入请求根级files，供Dify视觉节点sys.files使用"
                        + "；files顺序=无料基准图,正常有料基准图,实时采集图");
                    Post("视觉 AUTO 图片已准备好：无料基准图 + 正常有料基准图 + 实时采集图。", "ok");
                    WriteLog("视觉 AUTO 图片已上传：实时图上传图=" + imageResult.FilePath
                             + "，原图=" + imageResult.SourceFilePath
                             + "，图片时间=" + imageResult.FileTime.ToString("yyyy-MM-dd HH:mm:ss.fff")
                             + "，处理信息=" + imageResult.ProcessInfo
                             + "；无料基准图=" + emptyReferenceImageResult.SourceFilePath
                             + "，图片时间=" + emptyReferenceImageResult.FileTime.ToString("yyyy-MM-dd HH:mm:ss.fff")
                             + "，处理信息=" + emptyReferenceImageResult.ProcessInfo
                             + "；正常有料基准图=" + normalReferenceImageResult.SourceFilePath
                             + "，图片时间=" + normalReferenceImageResult.FileTime.ToString("yyyy-MM-dd HH:mm:ss.fff")
                             + "，处理信息=" + normalReferenceImageResult.ProcessInfo);
                }

                var body = new JObject
                {
                    ["inputs"] = inputs,
                    ["response_mode"] = "streaming",
                    ["user"] = ResolveUser()
                };
                if (autoVisionSysFiles != null)
                {
                    body[AutoVisionWorkflowFilesVariableName] = autoVisionSysFiles;
                }
                var json = body.ToString(Formatting.None);

                using var client = new HttpClient { Timeout = runTimeout };
                var req = new HttpRequestMessage(HttpMethod.Post, baseUrl);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");

                if (autoIsVisionRelated)
                {
                    WriteAutoVisionLog(autoTraceId, "workflow_request_send",
                        "开始请求 Dify workflow；url=" + baseUrl
                        + "，timeout=" + FormatElapsed(runTimeout)
                        + "，user=" + ResolveUser());
                }

                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    handle.FinalText = $"HTTP {(int)resp.StatusCode}: {err}";
                    if (autoIsVisionRelated)
                    {
                        WriteAutoVisionRunReply(autoVisionRunFolderPath, autoTraceId, false, handle.FinalText);
                        WriteAutoVisionLog(autoTraceId, "workflow_http_error", handle.FinalText);
                    }

                    AutoClearAlarmAudit.MarkWorkflowDispatchFailed(autoTraceId, handle.FinalText);
                    MainWindow.NotifyAutoDispatchFailed(autoTraceId, handle.FinalText);
                    Post(string.IsNullOrWhiteSpace(handle.FinalText) ? UiLanguageService.CurrentText("执行失败") : handle.FinalText, "error");
                    return handle;
                }

                string runId = null;
                string taskId = null;
                string finalText = null;

                using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var reader = new StreamReader(stream, Encoding.UTF8);

                var evtBuf = new StringBuilder();

                while (!reader.EndOfStream)
                {
                    ct.ThrowIfCancellationRequested();

                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null) break;

                    // 仅处理以 data: 开头的行，其他忽略
                    if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        evtBuf.Append(line.Substring(5).TrimStart());
                    }
                    else if (string.IsNullOrWhiteSpace(line))
                    {
                        // 一个事件结束
                        if (evtBuf.Length == 0) continue;

                        var one = evtBuf.ToString();
                        evtBuf.Clear();

                        JObject evt;
                        try { evt = JObject.Parse(one); }
                        catch { continue; }

                        var type = (string)evt["event"];
                        var d = evt["data"] as JObject;

                        switch (type)
                        {
                            case "workflow_started":
                            {
                                runId = d?["id"]?.ToString();
                                handle.WorkflowRunId = evt.Value<string>("workflow_run_id") ?? runId;
                                taskId = evt.Value<string>("task_id");
                                handle.TaskId = taskId;
                                _currentRunId = handle.WorkflowRunId;
                                _currentTaskId = handle.TaskId;
                                AutoClearAlarmAudit.MarkWorkflowStarted(autoTraceId, handle.WorkflowRunId, handle.TaskId);
                                MainWindow.NotifyAutoWorkflowStarted(autoTraceId, handle.WorkflowRunId, handle.TaskId);
                                if (autoIsVisionRelated)
                                {
                                    WriteAutoVisionLog(autoTraceId, "workflow_started",
                                        "Dify workflow 已启动；workflowRunId=" + (handle.WorkflowRunId ?? string.Empty)
                                        + "，taskId=" + (handle.TaskId ?? string.Empty));
                                }
                                break;
                            }

                            case "node_started":
                                {
                                    AutoClearAlarmAudit.RecordWorkflowNodeStarted(autoTraceId, d);
                                    TryMarkClearMachineAlarmsNode(autoTraceId, "node_started", d);
                                    if (!ShouldReportNode(d, onlyMajorNodes)) break;
                                    var title = d?["title"]?.ToString() ?? "未命名节点";
                                    var nodeType = d?["node_type"]?.ToString() ?? string.Empty;
                                    MainWindow.NotifyAutoNodeStarted(autoTraceId, title, nodeType);
                                    break;
                                }

                            case "text_chunk":
                            case "text_delta":
                                // 你不需要中途显示文本，所以这里不处理
                                break;

                            case "node_finished":
                                {
                                    TryMarkClearMachineAlarmsNode(autoTraceId, "node_finished", d);
                                    // 仅把明确的输出节点当作候选最终文本，避免中间 LLM 节点抢占最终结果。
                                    var nodeType = d?["node_type"]?.ToString()?.ToLowerInvariant() ?? "";
                                    var title = d?["title"]?.ToString() ?? "";
                                    double? elapsedSeconds = null;
                                    if (double.TryParse(d?["elapsed_time"]?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedElapsed))
                                    {
                                        elapsedSeconds = parsedElapsed;
                                    }

                                    AutoClearAlarmAudit.RecordWorkflowNodeFinished(autoTraceId, d, elapsedSeconds);

                                    var isTextNode = nodeType is "output" or "end"
                                                     || title.Contains("结束")
                                                     || title.Contains("输出");
                                    var previewText = isTextNode ? ExtractText(d?["outputs"], textOnly: true) : null;

                                    if (isTextNode && string.IsNullOrEmpty(finalText))
                                    {
                                        var outText = previewText;
                                        // 过滤掉过短/无意义内容（如 "0"）
                                        if (!string.IsNullOrWhiteSpace(outText) && outText.Trim().Length >= 2)
                                            finalText = outText;
                                    }

                                    if (ShouldReportNode(d, onlyMajorNodes))
                                    {
                                        MainWindow.NotifyAutoNodeFinished(autoTraceId, title, nodeType, elapsedSeconds, previewText);
                                    }
                                }
                                break;

                            case "workflow_finished":
                                {
                                    var status = d?["status"]?.ToString() ?? "succeeded";
                                    handle.Succeeded = string.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase);

                                    var outputs = d?["outputs"];
                                    // workflow_finished 的 outputs 才是最终事实来源；若存在有效输出，应覆盖前面缓存。
                                    var workflowFinalText = DifyOutputSanitizer.ExtractVisibleText(outputs, "text", "answer", "result", "output", "message");
                                    if (!string.IsNullOrWhiteSpace(workflowFinalText))
                                    {
                                        finalText = DifyOutputSanitizer.Clean(workflowFinalText);
                                    }

                                    handle.FinalText = DifyOutputSanitizer.Clean(finalText);

                                    if (aiView != null)
                                        Ui(() => aiView.AddBotMarkdownFinal(string.IsNullOrWhiteSpace(handle.FinalText)
                                            ? "*（无文本输出）*" : handle.FinalText));

                                    Post(handle.Succeeded
                                            ? UiLanguageService.CurrentText("结果已返回")
                                            : (string.IsNullOrWhiteSpace(handle.FinalText) ? UiLanguageService.CurrentText("执行失败") : handle.FinalText),
                                        handle.Succeeded ? "ok" : "warn");
                                    WriteLog(handle.FinalText);
                                    AutoClearAlarmAudit.MarkWorkflowFinished(autoTraceId, handle.Succeeded, handle.FinalText);
                                    MainWindow.NotifyAutoWorkflowFinished(autoTraceId, handle.Succeeded, handle.FinalText);
                                    if (autoIsVisionRelated)
                                    {
                                        WriteAutoVisionRunReply(autoVisionRunFolderPath, autoTraceId, handle.Succeeded, handle.FinalText);
                                        WriteAutoVisionLog(autoTraceId, "workflow_finished",
                                            "Dify workflow 已结束；succeeded=" + handle.Succeeded
                                            + "，workflowRunId=" + (handle.WorkflowRunId ?? string.Empty)
                                            + "，taskId=" + (handle.TaskId ?? string.Empty)
                                            + "，finalText=" + TrimForAutoVisionLog(handle.FinalText, 1000));
                                    }
                                }
                                break;

                            case "tts_message":
                            case "tts_message_end":
                            case "ping":
                            default:
                                // 忽略
                                break;
                        }
                    }
                }

                return handle;
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested || stopwatch.Elapsed >= runTimeout - TimeSpan.FromSeconds(1))
            {
                handle.FinalText = "AUTO 分析超时，已在 " + FormatElapsed(runTimeout) + " 后中止。";
                AutoClearAlarmAudit.MarkWorkflowDispatchFailed(autoTraceId, handle.FinalText);
                MainWindow.NotifyAutoDispatchFailed(autoTraceId, handle.FinalText);
                Post(handle.FinalText, "warn");
                WriteLog(handle.FinalText);
                if (autoIsVisionRelated)
                {
                    WriteAutoVisionRunReply(autoVisionRunFolderPath, autoTraceId, false, handle.FinalText);
                    WriteAutoVisionLog(autoTraceId, "workflow_timeout", handle.FinalText);
                }
                return handle;
            }
            catch (OperationCanceledException)
            {
                handle.FinalText = "AUTO 分析已取消。";
                AutoClearAlarmAudit.MarkWorkflowDispatchFailed(autoTraceId, handle.FinalText);
                MainWindow.NotifyAutoDispatchFailed(autoTraceId, handle.FinalText);
                Post(handle.FinalText, "warn");
                WriteLog(handle.FinalText);
                if (autoIsVisionRelated)
                {
                    WriteAutoVisionRunReply(autoVisionRunFolderPath, autoTraceId, false, handle.FinalText);
                    WriteAutoVisionLog(autoTraceId, "workflow_cancelled", handle.FinalText);
                }
                return handle;
            }
        }
        private static readonly object s_autoLogLock = new object();
        private static readonly object s_autoVisionLogLock = new object();
        public static void WriteLog(string str)
        {
            var folderPath = @"D:\Data\AiLog\Auto";
            Directory.CreateDirectory(folderPath);
            var path = Path.Combine(folderPath, DateTime.Now.ToString("yyyy-MM-dd") + ".txt");

            try
            {
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]     {str}"
                           .Replace("\r\n", "\n").Replace("\n", Environment.NewLine);

                lock (s_autoLogLock)
                {
                    using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
                    if (fs.Length == 0)
                    {
                        var bom = Encoding.UTF8.GetPreamble();
                        if (bom.Length > 0) fs.Write(bom, 0, bom.Length);
                    }

                    fs.Seek(0, SeekOrigin.End);
                    using var writer = new StreamWriter(fs, new UTF8Encoding(false)); // 追加时不再写 BOM
                    writer.WriteLine(line);
                    writer.Flush();
                }

                LogRetentionPolicy.TryCleanupFiles(
                    folderPath,
                    "*.txt",
                    SearchOption.TopDirectoryOnly,
                    TimeSpan.FromDays(30));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }

        private static void WriteAutoVisionLog(string autoTraceId, string stage, string message, Exception ex = null)
        {
            try
            {
                var folderPath = ResolveAutoVisionLogDirectory();
                var path = Path.Combine(folderPath, DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log");
                var trace = string.IsNullOrWhiteSpace(autoTraceId) ? "-" : autoTraceId.Trim();
                var line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "]"
                           + " traceId=" + trace
                           + " stage=" + (string.IsNullOrWhiteSpace(stage) ? "-" : stage.Trim())
                           + " " + (message ?? string.Empty);
                if (ex != null)
                {
                    line += " exception=" + ex.GetType().Name + ": " + ex.Message;
                }

                line = line.Replace("\r\n", "\n").Replace("\n", Environment.NewLine + "    ");

                lock (s_autoVisionLogLock)
                {
                    using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
                    if (fs.Length == 0)
                    {
                        var bom = Encoding.UTF8.GetPreamble();
                        if (bom.Length > 0) fs.Write(bom, 0, bom.Length);
                    }

                    fs.Seek(0, SeekOrigin.End);
                    using var writer = new StreamWriter(fs, new UTF8Encoding(false));
                    writer.WriteLine(line);
                    writer.Flush();
                }

                LogRetentionPolicy.TryCleanupFiles(
                    folderPath,
                    "*.log",
                    SearchOption.TopDirectoryOnly,
                    TimeSpan.FromDays(30));
            }
            catch (Exception logEx)
            {
                System.Diagnostics.Debug.WriteLine(logEx);
            }
        }

        private static string ResolveAutoVisionLogDirectory()
        {
            var primary = @"D:\Data\AiLog\AutoVision";
            try
            {
                Directory.CreateDirectory(primary);
                return primary;
            }
            catch
            {
                var fallback = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AiLog", "AutoVision");
                Directory.CreateDirectory(fallback);
                return fallback;
            }
        }

        private static string TrimForAutoVisionLog(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var normalized = value.Replace("\r\n", "\n").Replace("\r", "\n").Trim();
            if (maxLength <= 0 || normalized.Length <= maxLength)
            {
                return normalized;
            }

            return normalized.Substring(0, maxLength) + "...(truncated)";
        }

        /// <summary>
        /// （可选）停止当前流式任务：POST /workflows/tasks/:task_id/stop
        /// </summary>
        public static async Task<bool> StopCurrentAsyncAuto()
        {
            try
            {
                var taskId = _currentTaskId;
                if (string.IsNullOrEmpty(taskId)) return false;

                var cfg = ConfigService.Current;
                var apiKey = GetCurrentWorkflowApiKey();
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    apiKey = cfg?.AutoKey;
                }

                if (cfg == null || string.IsNullOrWhiteSpace(cfg.URL) || string.IsNullOrWhiteSpace(apiKey))
                    return false;

                var baseUrl = cfg.URL.Trim().TrimEnd('/');
                var url = $"{baseUrl}/workflows/tasks/{taskId}/stop";
                using var client = new HttpClient();
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                req.Content = new StringContent(JsonConvert.SerializeObject(new { user = ResolveUser() }), Encoding.UTF8, "application/json");

                using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var resp = await client.SendAsync(req, stopCts.Token).ConfigureAwait(false);
                var ok = resp.IsSuccessStatusCode;
                if (ok) Post("⏹ 已请求停止当前任务", "warn");
                return ok;
            }
            catch (Exception ex)
            {
                WriteLog("停止当前 AUTO 任务失败：" + ex.Message);
                return false;
            }
        }
        // 本地函数
        static bool ShouldReportNode(JObject data, bool onlyMajor)
        {
            if (data == null) return false;
            if (!onlyMajor) return true;

            var nodeType = data.Value<string>("node_type")?.ToLowerInvariant() ?? "";
            var title = data.Value<string>("title") ?? "";

            if (nodeType is "start" or "end" or "llm" or "if_else" or "agent" or "output")
                return true;

            if (title.Contains("LLM") ||
                title.Contains("条件") || title.Contains("Agent") ||
                title.Contains("开始") || title.Contains("结束") || title.Contains("输出"))
                return true;

            return false;
        }

        static string ExtractText(JToken token, bool textOnly = true)
        {
            if (token == null || token.Type == JTokenType.Null) return null;

            // 只要纯文本：数值/布尔一律忽略（避免 "0"）
            if (textOnly)
            {
                if (token.Type is JTokenType.Integer or JTokenType.Float or JTokenType.Boolean)
                    return null;
            }

            if (token.Type == JTokenType.String) return DifyOutputSanitizer.Clean(token.ToString());

            if (token is JObject obj)
            {
                // 常见键优先
                foreach (var k in new[] { "text", "answer", "result", "output", "message", "output_text" })
                    if (obj.TryGetValue(k, out var v))
                    {
                        var s = ExtractText(v, textOnly);
                        if (!string.IsNullOrWhiteSpace(s)) return s;
                    }

                // 任意字符串字段
                foreach (var p in obj.Properties())
                {
                    if (DifyOutputSanitizer.IsReasoningFieldName(p.Name))
                        continue;

                    if (p.Value.Type == JTokenType.String)
                        return DifyOutputSanitizer.Clean(p.Value.ToString());
                }

                // 兜底：允许返回紧凑 JSON（仅当 textOnly=false）
                return textOnly ? null : DifyOutputSanitizer.CleanToken(obj);
            }

            if (token is JArray arr)
            {
                foreach (var it in arr)
                {
                    var s = ExtractText(it, textOnly);
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
                return null;
            }

            // 允许在兜底阶段把数字也转成字符串
            return textOnly ? null : DifyOutputSanitizer.Clean(token.ToString());
        }


        static void Post(string text, string level) =>
            Ui(() => MainWindow.PostProgramInfo(text, level));

        static void Ui(Action action)
        {
            try
            {
                var disp = Application.Current?.Dispatcher;
                if (disp == null || disp.CheckAccess()) action();
                else disp.Invoke(action);
            }
            catch { }
        }

        private static AutoVisionImagePathSelection ResolveAutoVisionImagePath(AppConfig cfg)
        {
            var pathA = cfg?.AutoVisionImagePathA?.Trim() ?? string.Empty;

            return new AutoVisionImagePathSelection
            {
                Lane = AutoVisionFixedLane,
                ImagePath = pathA
            };
        }

        private static JObject BuildAutoVisionWorkflowImageInput(string uploadFileId)
        {
            return new JObject
            {
                ["transfer_method"] = "local_file",
                ["upload_file_id"] = uploadFileId ?? string.Empty,
                ["type"] = "image"
            };
        }

        private static JArray BuildAutoVisionWorkflowFilesInput(
            string emptyReferenceUploadFileId,
            string normalReferenceUploadFileId,
            string realImageUploadFileId)
        {
            return new JArray
            {
                BuildAutoVisionWorkflowImageInput(emptyReferenceUploadFileId),
                BuildAutoVisionWorkflowImageInput(normalReferenceUploadFileId),
                BuildAutoVisionWorkflowImageInput(realImageUploadFileId)
            };
        }

        private static async Task<AutoVisionImageAcquireResult> AcquireAndUploadAutoVisionEmptyReferenceImageAsync(
            string apiBaseUrl,
            string apiKey,
            string autoTraceId,
            CancellationToken ct)
        {
            var configuredPath = ConfigService.Current?.AutoVisionEmptyReferenceImagePath?.Trim() ?? string.Empty;
            return await AcquireAndUploadAutoVisionReferenceImageAsync(
                apiBaseUrl: apiBaseUrl,
                apiKey: apiKey,
                autoTraceId: autoTraceId,
                configuredPath: configuredPath,
                variableName: AutoVisionEmptyReferenceImageVariableName,
                displayName: "无料基准图",
                stagePrefix: "empty_reference",
                examplePath: @"Doc\无料基准图.jpg",
                ct: ct).ConfigureAwait(false);
        }

        private static async Task<AutoVisionImageAcquireResult> AcquireAndUploadAutoVisionNormalReferenceImageAsync(
            string apiBaseUrl,
            string apiKey,
            string autoTraceId,
            CancellationToken ct)
        {
            return await AcquireAndUploadAutoVisionReferenceImageAsync(
                apiBaseUrl: apiBaseUrl,
                apiKey: apiKey,
                autoTraceId: autoTraceId,
                configuredPath: AutoVisionNormalReferenceImageRelativePath,
                variableName: AutoVisionNormalReferenceImageVariableName,
                displayName: "正常有料基准图",
                stagePrefix: "normal_reference",
                examplePath: AutoVisionNormalReferenceImageRelativePath,
                ct: ct).ConfigureAwait(false);
        }

        private static async Task<AutoVisionImageAcquireResult> AcquireAndUploadAutoVisionReferenceImageAsync(
            string apiBaseUrl,
            string apiKey,
            string autoTraceId,
            string configuredPath,
            string variableName,
            string displayName,
            string stagePrefix,
            string examplePath,
            CancellationToken ct)
        {
            configuredPath = configuredPath?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                WriteAutoVisionLog(autoTraceId, stagePrefix + "_config_invalid", "视觉 AUTO " + displayName + "相对路径未配置。");
                return new AutoVisionImageAcquireResult
                {
                    Succeeded = false,
                    ErrorMessage = "视觉 AUTO " + displayName + "相对路径未配置。"
                };
            }

            if (Path.IsPathRooted(configuredPath))
            {
                WriteAutoVisionLog(autoTraceId, stagePrefix + "_config_invalid",
                    "视觉 AUTO " + displayName + "必须配置为执行程序相对路径；path=" + configuredPath);
                return new AutoVisionImageAcquireResult
                {
                    Succeeded = false,
                    ErrorMessage = "视觉 AUTO " + displayName + "必须配置为执行程序相对路径，例如 " + examplePath + "。当前配置=" + configuredPath
                };
            }

            var sourcePath = ResolveExecutableRelativePath(configuredPath);
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                WriteAutoVisionLog(autoTraceId, stagePrefix + "_not_found",
                    "视觉 AUTO " + displayName + "不存在；configuredPath=" + configuredPath
                    + "，resolvedPath=" + (sourcePath ?? string.Empty));
                return new AutoVisionImageAcquireResult
                {
                    Succeeded = false,
                    ErrorMessage = "视觉 AUTO " + displayName + "不存在。配置相对路径=" + configuredPath
                                   + "，解析路径=" + (sourcePath ?? string.Empty)
                };
            }

            if (!IsSupportedAutoVisionImage(sourcePath))
            {
                WriteAutoVisionLog(autoTraceId, stagePrefix + "_invalid_type",
                    "视觉 AUTO " + displayName + "不是支持的图片类型；path=" + sourcePath);
                return new AutoVisionImageAcquireResult
                {
                    Succeeded = false,
                    ErrorMessage = "视觉 AUTO " + displayName + "不是支持的图片类型。路径=" + sourcePath
                };
            }

            if (!await IsFileReadableAndStableAsync(sourcePath, ct).ConfigureAwait(false))
            {
                WriteAutoVisionLog(autoTraceId, stagePrefix + "_unstable",
                    "视觉 AUTO " + displayName + "暂不可读或仍在写入；path=" + sourcePath);
                return new AutoVisionImageAcquireResult
                {
                    Succeeded = false,
                    ErrorMessage = "视觉 AUTO " + displayName + "暂不可读或仍在写入。路径=" + sourcePath
                };
            }

            var fileTime = ResolveAutoVisionImageTime(sourcePath, out var timeSource);
            WriteAutoVisionLog(autoTraceId, stagePrefix + "_selected",
                displayName + "已选中；configuredPath=" + configuredPath
                + "，sourceImage=" + sourcePath
                + "，fileTime=" + fileTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                + "，timeSource=" + (timeSource ?? string.Empty));

            WriteAutoVisionLog(autoTraceId, stagePrefix + "_upload_start", "开始上传视觉 AUTO " + displayName + "；固定基准图不保存副本。");
            var uploadFileId = await UploadAutoVisionImageAsync(apiBaseUrl, apiKey, sourcePath, ResolveUser(), ct).ConfigureAwait(false);
            return new AutoVisionImageAcquireResult
            {
                Succeeded = true,
                SourceFilePath = sourcePath,
                FilePath = sourcePath,
                FileTime = fileTime,
                UploadFileId = uploadFileId,
                VariableName = variableName,
                ProcessInfo = "固定基准图直接上传，未保存副本。配置相对路径=" + configuredPath
            };
        }

        private static string ResolveExecutableRelativePath(string relativePath)
        {
            var trimmed = relativePath?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return string.Empty;
            }

            return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, trimmed));
        }

        private static async Task<AutoVisionImageAcquireResult> AcquireAndUploadAutoVisionImageAsync(
            string apiBaseUrl,
            string apiKey,
            string errorDesc,
            DateTime alarmTriggeredAt,
            string autoTraceId,
            CancellationToken ct)
        {
            var cfg = ConfigService.Current;
            var pathSelection = ResolveAutoVisionImagePath(cfg);
            var imagePath = pathSelection.ImagePath;
            if (string.IsNullOrWhiteSpace(imagePath))
            {
                WriteAutoVisionLog(autoTraceId, "image_config_invalid",
                    "视觉 AUTO 图片路径未配置；lane=" + (pathSelection.Lane ?? string.Empty)
                    + "，errorDesc=" + (errorDesc ?? string.Empty));
                return new AutoVisionImageAcquireResult
                {
                    Succeeded = false,
                    ErrorMessage = "视觉 AUTO 图片路径未配置，无法上传图片。工位=" + (pathSelection.Lane ?? string.Empty)
                };
            }

            var variableName = AutoVisionWorkflowImageVariableName;
            AutoVisionImageCandidate candidate;
            if (cfg?.AutoVisionImageTestMode == true)
            {
                Post("视觉 AUTO 图片测试模式：跳过时间校验，直接读取测试图片。", "info");
                WriteAutoVisionLog(autoTraceId, "image_test_mode",
                    "测试模式启用：跳过报警时间窗口校验；lane=" + pathSelection.Lane
                    + "，path=" + imagePath);
                candidate = await WaitForAutoVisionTestImageAsync(imagePath, pathSelection.Lane, autoTraceId, ct).ConfigureAwait(false);
                if (candidate == null)
                {
                    WriteAutoVisionLog(autoTraceId, "image_not_found", "测试模式未找到可用图片；path=" + imagePath);
                    return new AutoVisionImageAcquireResult
                    {
                        Succeeded = false,
                        ErrorMessage = "视觉 AUTO 测试模式未找到可用图片。路径=" + imagePath
                    };
                }

                WriteAutoVisionLog(autoTraceId, "image_candidate_selected",
                    "测试模式候选图片已选中；sourceImage=" + candidate.FilePath
                    + "，fileTime=" + candidate.FileTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                    + "，timeSource=" + (candidate.TimeSource ?? string.Empty));
                var testProcessedImage = ProcessAutoVisionImageForUpload(candidate.FilePath, autoTraceId, AutoVisionWorkflowImageVariableName);
                AutoClearAlarmAudit.MarkAutoVisionImage(autoTraceId, candidate.FilePath, testProcessedImage.FilePath);
                WriteAutoVisionLog(autoTraceId, "upload_start", "开始上传视觉 AUTO 图片。");
                var testUploadFileId = await UploadAutoVisionImageAsync(apiBaseUrl, apiKey, testProcessedImage.FilePath, ResolveUser(), ct).ConfigureAwait(false);
                return new AutoVisionImageAcquireResult
                {
                    Succeeded = true,
                    SourceFilePath = candidate.FilePath,
                    FilePath = testProcessedImage.FilePath,
                    FileTime = candidate.FileTime,
                    UploadFileId = testUploadFileId,
                    VariableName = variableName,
                    RunFolderPath = testProcessedImage.RunFolderPath,
                    ProcessInfo = testProcessedImage.ProcessInfo
                };
            }

            var lookbackSeconds = Math.Max(1, Math.Min(600, cfg?.AutoVisionImageLookbackSeconds ?? 10));
            var validAfter = alarmTriggeredAt.AddSeconds(-lookbackSeconds);
            var validBefore = alarmTriggeredAt;
            WriteAutoVisionLog(autoTraceId, "image_time_window",
                "正式模式取图窗口；validAfter=" + validAfter.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                + "，validBefore=" + validBefore.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                + "，lookbackSeconds=" + lookbackSeconds.ToString(CultureInfo.InvariantCulture)
                + "，lane=" + pathSelection.Lane
                + "，path=" + imagePath);

            candidate = await WaitForAutoVisionImageAsync(imagePath, validAfter, validBefore, pathSelection.Lane, autoTraceId, ct).ConfigureAwait(false);
            if (candidate == null)
            {
                WriteAutoVisionLog(autoTraceId, "image_not_found",
                    "未找到满足时间窗口的视觉图片；path=" + imagePath);
                return new AutoVisionImageAcquireResult
                {
                    Succeeded = false,
                    ErrorMessage = "未找到有效视觉图片。路径=" + imagePath
                                   + "，要求图片时间在 "
                                   + validAfter.ToString("yyyy-MM-dd HH:mm:ss.fff")
                                   + " 至 "
                                   + validBefore.ToString("yyyy-MM-dd HH:mm:ss.fff")
                                   + " 之间。"
                };
            }

            WriteAutoVisionLog(autoTraceId, "image_candidate_selected",
                "正式模式候选图片已选中；sourceImage=" + candidate.FilePath
                + "，fileTime=" + candidate.FileTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                + "，timeSource=" + (candidate.TimeSource ?? string.Empty));
            var processedImage = ProcessAutoVisionImageForUpload(candidate.FilePath, autoTraceId, AutoVisionWorkflowImageVariableName);
            AutoClearAlarmAudit.MarkAutoVisionImage(autoTraceId, candidate.FilePath, processedImage.FilePath);
            WriteAutoVisionLog(autoTraceId, "upload_start", "开始上传视觉 AUTO 图片。");
            var uploadFileId = await UploadAutoVisionImageAsync(apiBaseUrl, apiKey, processedImage.FilePath, ResolveUser(), ct).ConfigureAwait(false);
            return new AutoVisionImageAcquireResult
            {
                Succeeded = true,
                SourceFilePath = candidate.FilePath,
                FilePath = processedImage.FilePath,
                FileTime = candidate.FileTime,
                UploadFileId = uploadFileId,
                VariableName = variableName,
                RunFolderPath = processedImage.RunFolderPath,
                ProcessInfo = processedImage.ProcessInfo
            };
        }

        private static AutoVisionImageProcessResult ProcessAutoVisionImageForUpload(string sourcePath, string autoTraceId, string imageRole = null)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                throw new FileNotFoundException("视觉 AUTO 原图不存在。", sourcePath);
            }

            var safeTraceId = MakeSafeFileNamePart(string.IsNullOrWhiteSpace(autoTraceId) ? "no-trace" : autoTraceId);
            var outputDirectory = ResolveAutoVisionProcessedImageDirectory(safeTraceId);
            var safeSourceName = MakeSafeFileNamePart(Path.GetFileNameWithoutExtension(sourcePath));
            var safeImageRole = string.IsNullOrWhiteSpace(imageRole) ? string.Empty : "-" + MakeSafeFileNamePart(imageRole);

            WriteAutoVisionLog(autoTraceId, "image_process_start",
                "开始保存视觉 AUTO 上传副本。");

            var sourceExtension = Path.GetExtension(sourcePath);
            if (string.IsNullOrWhiteSpace(sourceExtension))
            {
                sourceExtension = ".img";
            }

            var copyPath = Path.Combine(
                outputDirectory,
                DateTime.Now.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture)
                + "-" + safeTraceId
                + "-" + safeSourceName
                + safeImageRole
                + "-copy" + sourceExtension);
            File.Copy(sourcePath, copyPath, overwrite: true);

            return new AutoVisionImageProcessResult
            {
                FilePath = copyPath,
                RunFolderPath = outputDirectory,
                ProcessInfo = "已保存上传副本。"
            };
        }

        private static string ResolveAutoVisionProcessedImageDirectory(string safeTraceId)
        {
            var day = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var folderName = string.IsNullOrWhiteSpace(safeTraceId) ? "no-trace" : safeTraceId;
            var primary = Path.Combine(@"D:\Data\AiLog\AutoVisionImages", day, folderName);
            try
            {
                Directory.CreateDirectory(primary);
                return primary;
            }
            catch (Exception ex)
            {
                WriteLog("创建视觉 AUTO 处理图目录失败，回落到程序目录：" + ex.Message);
                WriteAutoVisionLog(null, "image_storage_fallback", "创建视觉 AUTO 处理图目录失败，回落到程序目录；primary=" + primary, ex);
            }

            var fallback = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AiLog", "AutoVisionImages", day, folderName);
            Directory.CreateDirectory(fallback);
            return fallback;
        }

        private static void WriteAutoVisionRunReply(string runFolderPath, string autoTraceId, bool succeeded, string finalText)
        {
            if (string.IsNullOrWhiteSpace(runFolderPath))
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(runFolderPath);
                var replyPath = Path.Combine(runFolderPath, "ai-reply.txt");
                var builder = new StringBuilder();
                builder.AppendLine("traceId: " + (autoTraceId ?? string.Empty));
                builder.AppendLine("time: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
                builder.AppendLine("succeeded: " + (succeeded ? "true" : "false"));
                builder.AppendLine();
                builder.AppendLine(string.IsNullOrWhiteSpace(finalText) ? "（无文本输出）" : finalText.Trim());
                File.WriteAllText(replyPath, builder.ToString(), new UTF8Encoding(false));
                WriteAutoVisionLog(autoTraceId, "run_reply_saved", "视觉 AUTO 单次回复已保存；path=" + replyPath);
            }
            catch (Exception ex)
            {
                WriteAutoVisionLog(autoTraceId, "run_reply_save_failed", "保存视觉 AUTO 单次回复失败；folder=" + runFolderPath, ex);
            }
        }

        private static string MakeSafeFileNamePart(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "image";
            }

            var invalid = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(value.Length);
            foreach (var ch in value.Trim())
            {
                builder.Append(invalid.Contains(ch) ? '_' : ch);
            }

            var result = builder.ToString().Trim('.');
            return string.IsNullOrWhiteSpace(result) ? "image" : result;
        }

        private static TimeSpan ResolveAutoRunTimeout(AutoWorkflowRoute route)
        {
            var timeout = AutoWorkflowHardTimeout;
            if (route?.IsVisionRelated == true)
            {
                timeout = timeout.Add(AutoVisionImageAcquireTimeout);
            }

            return timeout;
        }

        private static async Task<AutoVisionImageCandidate> WaitForAutoVisionImageAsync(
            string configuredPath,
            DateTime validAfter,
            DateTime validBefore,
            string lane,
            string autoTraceId,
            CancellationToken ct)
        {
            var deadline = DateTime.Now.Add(AutoVisionImageAcquireTimeout);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var candidate = FindLatestAutoVisionImage(configuredPath, validAfter, validBefore, lane, autoTraceId);
                if (candidate != null && await IsFileReadableAndStableAsync(candidate.FilePath, ct).ConfigureAwait(false))
                {
                    return candidate;
                }

                if (DateTime.Now >= deadline)
                {
                    return null;
                }

                await Task.Delay(AutoVisionImagePollInterval, ct).ConfigureAwait(false);
            }
        }

        private static async Task<AutoVisionImageCandidate> WaitForAutoVisionTestImageAsync(
            string configuredPath,
            string lane,
            string autoTraceId,
            CancellationToken ct)
        {
            var deadline = DateTime.Now.Add(AutoVisionImageAcquireTimeout);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var candidate = FindAutoVisionTestImage(configuredPath, lane, autoTraceId);
                if (candidate != null && await IsFileReadableAndStableAsync(candidate.FilePath, ct).ConfigureAwait(false))
                {
                    return candidate;
                }

                if (DateTime.Now >= deadline)
                {
                    return null;
                }

                await Task.Delay(AutoVisionImagePollInterval, ct).ConfigureAwait(false);
            }
        }

        private static AutoVisionImageCandidate FindAutoVisionTestImage(string configuredPath, string lane, string autoTraceId)
        {
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                return null;
            }

            var path = configuredPath.Trim();
            try
            {
                AutoVisionImageCandidate best = null;
                foreach (var file in EnumerateAutoVisionImageFiles(path, null, lane, autoTraceId))
                {
                    var candidate = TryBuildImageCandidate(file, DateTime.MinValue);
                    if (candidate == null)
                    {
                        continue;
                    }

                    if (best == null || candidate.FileTime > best.FileTime)
                    {
                        best = candidate;
                    }
                }

                return best;
            }
            catch (Exception ex)
            {
                WriteLog("查找视觉 AUTO 测试图片失败：" + ex.Message);
                WriteAutoVisionLog(autoTraceId, "image_search_error", "查找视觉 AUTO 测试图片失败；path=" + configuredPath, ex);
                return null;
            }
        }

        private static AutoVisionImageCandidate FindLatestAutoVisionImage(string configuredPath, DateTime validAfter, DateTime validBefore, string lane, string autoTraceId)
        {
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                return null;
            }

            var path = configuredPath.Trim();
            try
            {
                AutoVisionImageCandidate best = null;
                foreach (var file in EnumerateAutoVisionImageFiles(path, validBefore, lane, autoTraceId))
                {
                    var candidate = TryBuildImageCandidate(file, validAfter, validBefore);
                    if (candidate == null)
                    {
                        continue;
                    }

                    if (best == null || candidate.FileTime > best.FileTime)
                    {
                        best = candidate;
                    }
                }

                return best;
            }
            catch (Exception ex)
            {
                WriteLog("查找视觉 AUTO 图片失败：" + ex.Message);
                WriteAutoVisionLog(autoTraceId, "image_search_error",
                    "查找视觉 AUTO 图片失败；path=" + configuredPath,
                    ex);
                return null;
            }
        }

        private static List<string> EnumerateAutoVisionImageFiles(
            string configuredPath,
            DateTime? targetTime,
            string lane,
            string autoTraceId)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                return result;
            }

            var path = configuredPath.Trim();
            try
            {
                if (File.Exists(path))
                {
                    if (IsSupportedAutoVisionImage(path))
                    {
                        result.Add(path);
                    }

                    return result;
                }

                if (targetTime.HasValue)
                {
                    var adjustedPath = ReplaceAutoVisionDateFolder(path, targetTime.Value);
                    if (!string.Equals(adjustedPath, path, StringComparison.OrdinalIgnoreCase)
                        && File.Exists(adjustedPath))
                    {
                        if (IsSupportedAutoVisionImage(adjustedPath))
                        {
                            result.Add(adjustedPath);
                        }

                        return result;
                    }
                }

                var directories = ResolveAutoVisionSearchDirectories(path, targetTime, lane);
                var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var directory in directories)
                {
                    try
                    {
                        foreach (var file in Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly))
                        {
                            if (!IsSupportedAutoVisionImage(file) || !seenFiles.Add(file))
                            {
                                continue;
                            }

                            result.Add(file);
                        }
                    }
                    catch (Exception ex)
                    {
                        WriteAutoVisionLog(autoTraceId, "image_directory_scan_error",
                            "扫描视觉 AUTO 图片目录失败；directory=" + directory,
                            ex);
                    }
                }
            }
            catch (Exception ex)
            {
                WriteAutoVisionLog(autoTraceId, "image_search_error",
                    "枚举视觉 AUTO 图片失败；path=" + configuredPath,
                    ex);
            }

            return result;
        }

        private static List<string> ResolveAutoVisionSearchDirectories(string configuredPath, DateTime? targetTime, string lane)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddDirectory(string directory)
            {
                if (string.IsNullOrWhiteSpace(directory))
                {
                    return;
                }

                var normalized = directory.Trim();
                if (Directory.Exists(normalized) && seen.Add(normalized))
                {
                    result.Add(normalized);
                }
            }

            var path = configuredPath.Trim();
            AddDirectory(path);

            if (targetTime.HasValue)
            {
                AddDirectory(ReplaceAutoVisionDateFolder(path, targetTime.Value));
            }

            var roots = result.ToArray();
            foreach (var root in roots)
            {
                if (targetTime.HasValue)
                {
                    if (PathContainsAutoVisionDateFolder(root) || IsAutoVisionLikelyImageDirectory(root, lane))
                    {
                        CollectAutoVisionImageLeafDirectories(root, lane, 0, AddDirectory);
                    }

                    foreach (var dateRoot in ResolveAutoVisionDateRootDirectories(root, targetTime.Value))
                    {
                        AddDirectory(dateRoot);
                        CollectAutoVisionImageLeafDirectories(dateRoot, lane, 0, AddDirectory);
                    }
                }
                else
                {
                    CollectAutoVisionImageLeafDirectories(root, lane, 0, AddDirectory);
                }
            }

            return result;
        }

        private static List<string> ResolveAutoVisionDateRootDirectories(string root, DateTime targetTime)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return result;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string directory)
            {
                if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory) && seen.Add(directory))
                {
                    result.Add(directory);
                }
            }

            var rootName = GetDirectoryName(root);
            if (TryParseAutoVisionDateFolder(rootName, out var parsedDate) && parsedDate.Date == targetTime.Date)
            {
                Add(root);
            }

            foreach (var dateFolderName in BuildAutoVisionDateFolderNames(targetTime))
            {
                Add(Path.Combine(root, dateFolderName));
            }

            return result;
        }

        private static void CollectAutoVisionImageLeafDirectories(
            string root,
            string lane,
            int depth,
            Action<string> addDirectory)
        {
            if (string.IsNullOrWhiteSpace(root)
                || depth >= AutoVisionImageDirectorySearchMaxDepth
                || !Directory.Exists(root))
            {
                return;
            }

            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(root).ToArray();
            }
            catch
            {
                return;
            }

            foreach (var child in children)
            {
                if (IsAutoVisionLikelyImageDirectory(child, lane))
                {
                    addDirectory(child);
                }

                CollectAutoVisionImageLeafDirectories(child, lane, depth + 1, addDirectory);
            }
        }

        private static bool IsAutoVisionLikelyImageDirectory(string directory, string lane)
        {
            var name = GetDirectoryName(directory);
            var fullPath = directory ?? string.Empty;
            var hasCaptureSegment = fullPath.IndexOf("拍照存图", StringComparison.OrdinalIgnoreCase) >= 0;
            var isNgDirectory = string.Equals(name, "NG", StringComparison.OrdinalIgnoreCase) && hasCaptureSegment;
            var isCaptureDirectory = name.IndexOf("拍照存图", StringComparison.OrdinalIgnoreCase) >= 0;
            return (isNgDirectory || isCaptureDirectory)
                   && IsAutoVisionPathMatchedLane(fullPath, lane);
        }

        private static bool IsAutoVisionPathMatchedLane(string path, string lane)
        {
            if (string.IsNullOrWhiteSpace(lane))
            {
                return true;
            }

            var normalizedLane = lane.Trim().ToUpperInvariant();
            if (normalizedLane != "A" && normalizedLane != "B")
            {
                return true;
            }

            var normalizedPath = (path ?? string.Empty)
                .Replace('\\', '/')
                .ToUpperInvariant();

            var hasA = normalizedPath.Contains("A侧")
                       || normalizedPath.Contains("A相机")
                       || normalizedPath.Contains("/A/")
                       || normalizedPath.Contains("_A");
            var hasB = normalizedPath.Contains("B侧")
                       || normalizedPath.Contains("B相机")
                       || normalizedPath.Contains("/B/")
                       || normalizedPath.Contains("_B");

            return normalizedLane == "A"
                ? hasA || !hasB
                : hasB || !hasA;
        }

        private static string ReplaceAutoVisionDateFolder(string path, DateTime targetTime)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return path;
            }

            var targetDateFolder = targetTime.ToString("yyyy年MM月dd日", CultureInfo.InvariantCulture);
            var segmentStart = 0;
            for (var i = 0; i <= path.Length; i++)
            {
                if (i < path.Length && !IsPathSeparator(path[i]))
                {
                    continue;
                }

                var length = i - segmentStart;
                if (length > 0)
                {
                    var segment = path.Substring(segmentStart, length);
                    if (TryParseAutoVisionDateFolder(segment, out _))
                    {
                        return path.Substring(0, segmentStart)
                               + targetDateFolder
                               + path.Substring(i);
                    }
                }

                segmentStart = i + 1;
            }

            return path;
        }

        private static bool PathContainsAutoVisionDateFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var segmentStart = 0;
            for (var i = 0; i <= path.Length; i++)
            {
                if (i < path.Length && !IsPathSeparator(path[i]))
                {
                    continue;
                }

                var length = i - segmentStart;
                if (length > 0)
                {
                    var segment = path.Substring(segmentStart, length);
                    if (TryParseAutoVisionDateFolder(segment, out _))
                    {
                        return true;
                    }
                }

                segmentStart = i + 1;
            }

            return false;
        }

        private static string[] BuildAutoVisionDateFolderNames(DateTime targetTime)
        {
            return new[]
            {
                targetTime.ToString("yyyy年MM月dd日", CultureInfo.InvariantCulture),
                targetTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                targetTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
            };
        }

        private static bool TryParseAutoVisionDateFolder(string value, out DateTime date)
        {
            return DateTime.TryParseExact(
                value ?? string.Empty,
                AutoVisionImageDateFolderFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out date);
        }

        private static bool IsPathSeparator(char ch)
        {
            return ch == Path.DirectorySeparatorChar
                   || ch == Path.AltDirectorySeparatorChar
                   || ch == '\\'
                   || ch == '/';
        }

        private static string GetDirectoryName(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return string.Empty;
            }

            return Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '\\', '/'));
        }

        private static AutoVisionImageCandidate TryBuildImageCandidate(string filePath, DateTime validAfter, DateTime? validBefore = null)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !IsSupportedAutoVisionImage(filePath))
            {
                return null;
            }

            try
            {
                var fileTime = ResolveAutoVisionImageTime(filePath, out var timeSource);
                if (fileTime < validAfter)
                {
                    return null;
                }

                if (validBefore.HasValue && fileTime > validBefore.Value)
                {
                    return null;
                }

                return new AutoVisionImageCandidate
                {
                    FilePath = filePath,
                    FileTime = fileTime,
                    TimeSource = timeSource
                };
            }
            catch
            {
                return null;
            }
        }

        private static DateTime ResolveAutoVisionImageTime(string filePath, out string timeSource)
        {
            if (TryParseAutoVisionImageTimeFromFileName(filePath, out var nameTime))
            {
                timeSource = "file_name";
                return nameTime;
            }

            var writeTime = File.GetLastWriteTime(filePath);
            var createTime = File.GetCreationTime(filePath);
            timeSource = "file_system";
            return writeTime >= createTime ? writeTime : createTime;
        }

        private static bool TryParseAutoVisionImageTimeFromFileName(string filePath, out DateTime fileTime)
        {
            fileTime = default;
            var name = Path.GetFileNameWithoutExtension(filePath);
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            var prefix = name.Trim();
            var splitIndex = prefix.IndexOf('_');
            if (splitIndex > 0)
            {
                prefix = prefix.Substring(0, splitIndex);
            }

            if (prefix.Length > AutoVisionImageFileNameTimeFormats[0].Length)
            {
                prefix = prefix.Substring(0, AutoVisionImageFileNameTimeFormats[0].Length);
            }

            return DateTime.TryParseExact(
                prefix,
                AutoVisionImageFileNameTimeFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out fileTime);
        }

        private static bool IsSupportedAutoVisionImage(string filePath)
        {
            var ext = Path.GetExtension(filePath)?.ToLowerInvariant();
            return !string.IsNullOrWhiteSpace(ext)
                   && AutoVisionImageExtensions.Any(x => string.Equals(x, ext, StringComparison.OrdinalIgnoreCase));
        }

        private static async Task<bool> IsFileReadableAndStableAsync(string filePath, CancellationToken ct)
        {
            var firstLength = TryGetReadableFileLength(filePath);
            if (firstLength < 0)
            {
                return false;
            }

            await Task.Delay(AutoVisionImageStableInterval, ct).ConfigureAwait(false);
            var secondLength = TryGetReadableFileLength(filePath);
            return secondLength >= 0 && secondLength == firstLength;
        }

        private static long TryGetReadableFileLength(string filePath)
        {
            try
            {
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return stream.Length;
            }
            catch
            {
                return -1;
            }
        }

        private static async Task<string> UploadAutoVisionImageAsync(
            string apiBaseUrl,
            string apiKey,
            string imagePath,
            string user,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(apiBaseUrl))
            {
                throw new InvalidOperationException("AutoVision 上传失败：API URL 为空。");
            }

            using var client = new HttpClient { Timeout = AutoWorkflowHardTimeout };
            using var content = new MultipartFormDataContent();
            using var fileStream = File.OpenRead(imagePath);
            var fileContent = new StreamContent(fileStream);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(GuessImageMimeType(imagePath));
            content.Add(fileContent, "file", Path.GetFileName(imagePath));
            content.Add(new StringContent(user ?? string.Empty, Encoding.UTF8), "user");

            using var req = new HttpRequestMessage(HttpMethod.Post, apiBaseUrl.TrimEnd('/') + "/files/upload");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            req.Content = content;

            using var response = await client.SendAsync(req, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Created && !response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException("上传视觉 AUTO 图片失败，HTTP " + (int)response.StatusCode + ": " + body);
            }

            var json = JObject.Parse(body);
            var id = json.Value<string>("id");
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new InvalidOperationException("上传视觉 AUTO 图片成功但未返回文件 id：" + body);
            }

            return id;
        }

        private static string GuessImageMimeType(string filePath)
        {
            var ext = Path.GetExtension(filePath)?.ToLowerInvariant();
            return ext switch
            {
                ".png" => "image/png",
                ".jpg" => "image/jpeg",
                ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                ".webp" => "image/webp",
                ".tif" => "image/tiff",
                ".tiff" => "image/tiff",
                _ => "application/octet-stream"
            };
        }

        private static AutoWorkflowRoute ResolveAutoWorkflowRoute(AppConfig cfg, string errorDesc)
        {
            var route = new AutoWorkflowRoute();
            if (cfg == null || string.IsNullOrWhiteSpace(cfg.URL))
            {
                route.Message = "AutoURL 未配置。";
                return route;
            }

            if (TryResolveAlarmKnowledgeItem(errorDesc, out var item) && item?.IsVisionRelated == true)
            {
                route.IsVisionRelated = true;
                route.KeyName = "AutoVisionKey";
                route.WorkflowDisplayName = "视觉 AUTO 流程";
                route.RejectReason = "invalid_vision_config";
            }

            var selectedKey = route.IsVisionRelated ? cfg.AutoVisionKey : cfg.AutoKey;
            if (string.IsNullOrWhiteSpace(selectedKey))
            {
                route.Message = route.IsVisionRelated
                    ? "AutoURL / AutoVisionKey 未配置。"
                    : "AutoURL / AutoKey 未配置。";
                return route;
            }

            if (route.IsVisionRelated && string.IsNullOrWhiteSpace(ResolveAutoVisionImagePath(cfg).ImagePath))
            {
                route.Message = "AutoVision " + AutoVisionFixedLane + "工位图片路径未配置。";
                return route;
            }

            if (route.IsVisionRelated)
            {
                var emptyReferencePath = cfg.AutoVisionEmptyReferenceImagePath?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(emptyReferencePath))
                {
                    route.Message = "AutoVision 无料基准图相对路径未配置。";
                    return route;
                }

                if (Path.IsPathRooted(emptyReferencePath))
                {
                    route.Message = "AutoVision 无料基准图必须配置为执行程序相对路径，例如 Doc\\无料基准图.jpg。";
                    return route;
                }

                var normalReferencePath = ResolveExecutableRelativePath(AutoVisionNormalReferenceImageRelativePath);
                if (string.IsNullOrWhiteSpace(normalReferencePath) || !File.Exists(normalReferencePath))
                {
                    route.Message = "AutoVision 正常有料基准图不存在。期望路径=" + normalReferencePath;
                    return route;
                }
            }

            route.ApiBaseUrl = cfg.URL.Trim().TrimEnd('/');
            route.BaseUrl = route.ApiBaseUrl + "/workflows/run";
            route.ApiKey = selectedKey.Trim();
            route.Message = "accepted";
            route.IsReady = true;
            return route;
        }

        private static void BuildAlarmContext(string errorDesc, out string ioInput, out string alarmContext, out bool isVisionRelated)
        {
            ioInput = string.Empty;
            alarmContext = string.Empty;
            isVisionRelated = false;

            if (string.IsNullOrWhiteSpace(errorDesc))
            {
                return;
            }

            if (!TryResolveAlarmKnowledgeItem(errorDesc, out var item) || item == null)
            {
                return;
            }

            isVisionRelated = item.IsVisionRelated;

            var doList = item.DoList ?? Array.Empty<string>();
            var cleanDoList = doList
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .ToArray();

            ioInput = cleanDoList.Length > 0 ? string.Join(",", cleanDoList) : string.Empty;

            var ctx = new
            {
                alarm_code = item.AlarmCode ?? string.Empty,
                alarm_name = item.AlarmName ?? string.Empty,
                goal = item.Goal ?? string.Empty,
                do_list = cleanDoList,
                di_list = item.DiList ?? Array.Empty<string>(),
                io_meaning = item.IoMeaning ?? string.Empty,
                expectation = item.Expectation ?? string.Empty,
                is_vision_related = item.IsVisionRelated
            };
            alarmContext = JsonConvert.SerializeObject(ctx, Formatting.None);
        }

        private static bool TryResolveAlarmKnowledgeItem(string errorDesc, out AlarmIoKnowledgeItem item)
        {
            item = null;
            if (string.IsNullOrWhiteSpace(errorDesc))
            {
                return false;
            }

            EnsureAlarmKnowledgeLoaded();
            return AlarmIoKnowledgeRepository.TryMatchByErrorDesc(errorDesc, out item);
        }

        private static void EnsureAlarmKnowledgeLoaded()
        {
            if (AlarmIoKnowledgeRepository.Count > 0)
            {
                return;
            }

            var ioMapPath = ConfigService.Current?.IoMapCsvPath;
            if (string.IsNullOrWhiteSpace(ioMapPath))
            {
                return;
            }

            AlarmIoKnowledgeRepository.TryLoadFromIoMapPath(ioMapPath, out _);
        }

        private static string FormatElapsed(TimeSpan elapsed)
        {
            var totalSeconds = Math.Max(0, elapsed.TotalSeconds);
            if (totalSeconds >= 60)
            {
                var minutes = Math.Floor(totalSeconds / 60d);
                var seconds = totalSeconds - (minutes * 60d);
                return string.Format(CultureInfo.InvariantCulture, "{0}分{1:0.000}秒", minutes, seconds);
            }

            return string.Format(CultureInfo.InvariantCulture, "{0:0.000}秒", totalSeconds);
        }

        private static string FormatDisplayId(string value)
        {
            var text = value?.Trim() ?? string.Empty;
            if (text.Length <= 12)
            {
                return string.IsNullOrWhiteSpace(text) ? "空" : text;
            }

            return text.Substring(0, 12) + "...";
        }

        private static string ResolveMachineCode(string incomingMachineCode)
        {
            _ = incomingMachineCode; // 保留参数仅为兼容既有调用方
            var configuredMachine = ConfigService.Current?.MachineCode;
            return string.IsNullOrWhiteSpace(configuredMachine) ? string.Empty : configuredMachine.Trim();
        }

        private static string ResolveUser()
        {
            var configuredUser = ConfigService.Current?.User;
            return string.IsNullOrWhiteSpace(configuredUser) ? Environment.MachineName : configuredUser.Trim();
        }

        private static string BuildAutoTriggerLog(string machineCode, string errorDesc)
        {
            var safeMachine = FormatLogValue(machineCode);
            var safeDesc = UiLanguageService.ApplyDisplayTextReplacements(FormatLogValue(errorDesc));
            var format = UiLanguageService.CurrentText("⚡ AUTO触发：machineCode={0}。捕获到机台报警：报警代码为：{1}，进入AI分析流程。");

            return string.Format(
                format,
                safeMachine,
                safeDesc
            );
        }

        private static string FormatLogValue(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "空" : value;
        }

        private static void TryMarkClearMachineAlarmsNode(string autoTraceId, string source, JObject data)
        {
            if (string.IsNullOrWhiteSpace(autoTraceId) || data == null)
            {
                return;
            }

            if (!ContainsClearMachineAlarmsSignature(data))
            {
                return;
            }

            var detail = new
            {
                source,
                nodeType = data.Value<string>("node_type"),
                title = data.Value<string>("title"),
                raw = DifyOutputSanitizer.CleanToken(data)
            };
            AutoClearAlarmAudit.MarkClearMachineAlarmsDetectedInWorkflow(
                autoTraceId,
                source,
                JsonConvert.SerializeObject(detail, Formatting.None));
        }

        private static bool ContainsClearMachineAlarmsSignature(JObject data)
        {
            if (data == null)
            {
                return false;
            }

            var nodeType = data.Value<string>("node_type") ?? string.Empty;
            var title = data.Value<string>("title") ?? string.Empty;
            var raw = data.ToString(Formatting.None);

            if (raw.IndexOf("ClearMachineAlarms", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            if (!string.Equals(nodeType, "tool", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return title.IndexOf("清除报警", StringComparison.OrdinalIgnoreCase) >= 0
                   || title.IndexOf("报警消除", StringComparison.OrdinalIgnoreCase) >= 0
                   || raw.IndexOf("清除报警", StringComparison.OrdinalIgnoreCase) >= 0
                   || raw.IndexOf("报警消除", StringComparison.OrdinalIgnoreCase) >= 0;
        }

    }
}

using EW_Assistant.Settings;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EW_Assistant.Diagnostics;

namespace EW_Assistant.Services
{
    /// <summary>
    /// Executor workflow 调用通道，负责请求组装、流式事件收集与执行日志落地。
    /// </summary>
    public sealed class UiCoarseVisionWorkflowService
    {
        private static readonly Lazy<UiCoarseVisionWorkflowService> s_lazy =
            new Lazy<UiCoarseVisionWorkflowService>(() => new UiCoarseVisionWorkflowService());
        private static readonly object s_logLock = new object();
        private const string InternalCaptureImageKind = "workflow_internal_capture";
        private const string DefaultCommandCatalogMode = "grounded_light_compact";
        private const int MaxLoggedStringLength = 4000;
        private const int MaxLoggedArrayItems = 32;
        private const int MaxStoredStreamEventCount = 256;

        public static UiCoarseVisionWorkflowService Instance => s_lazy.Value;

        private UiCoarseVisionWorkflowService() { }

        public async Task<UiCoarseVisionWorkflowResult> RunStreamingAsync(
            UiCoarseVisionWorkflowRequest request,
            Func<UiCoarseVisionWorkflowStreamEvent, Task> onEvent = null,
            CancellationToken ct = default)
        {
            if (!AgentAutomationService.ModuleEnabled)
                throw new InvalidOperationException("智能体控制模块已冻结。");
            if (request == null)
                throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.Goal))
                throw new InvalidOperationException("请先填写目标。");

            var cfg = ConfigService.Current ?? throw new InvalidOperationException("配置尚未加载。");
            if (string.IsNullOrWhiteSpace(cfg.URL))
                throw new InvalidOperationException("URL 未配置，无法调用多轮任务流 Workflow。");
            var apiKey = string.IsNullOrWhiteSpace(request.ApiKeyOverride)
                ? ConfigService.ResolveExecutorWorkflowApiKey(cfg)
                : request.ApiKeyOverride.Trim();
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("Executor Key 未配置，无法调用 Workflow。");

            var imagePath = SaveInputImageIfNeeded(request.ImageBase64);
            var userId = ResolveUser(cfg);
            var normalizedCatalogMode = NormalizeCommandCatalogMode(request.CommandCatalogMode);
            var commandCatalog = BuildCommandCatalog(normalizedCatalogMode);
            var extraInputs = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (request.ExtraInputs != null)
            {
                foreach (var pair in request.ExtraInputs)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key))
                        continue;

                    extraInputs[pair.Key] = pair.Value;
                }
            }

            extraInputs["goal"] = request.Goal.Trim();
            extraInputs["command_catalog"] = commandCatalog.ToString(Formatting.None);
            var requestInputsJson = BuildRequestInputsJson(extraInputs);
            var requestPayloadJson = BuildRequestPayloadJson(extraInputs, userId, "streaming");
            var streamEvents = new List<UiCoarseVisionWorkflowStreamEvent>();
            Func<FileWorkflowStreamEvent, Task> streamCallback = async evt =>
            {
                var converted = CreateStreamEvent(evt);
                if (converted != null)
                {
                    if (streamEvents.Count >= MaxStoredStreamEventCount)
                        streamEvents.RemoveAt(0);

                    streamEvents.Add(converted);
                }

                if (onEvent != null)
                    await onEvent(converted).ConfigureAwait(false);
            };

            FileWorkflowResult workflowResult;
            using (var client = new FileWorkflowClient(new FileWorkflowClientOptions
            {
                BaseUrl = cfg.URL,
                ApiKey = apiKey
            }))
            {
                workflowResult = await client.RunStreamingAsync(new FileWorkflowRequest
                {
                    FilePath = imagePath,
                    UserId = userId,
                    FileVariableName = string.IsNullOrWhiteSpace(imagePath) ? null : "picture",
                    PromptVariableName = null,
                    OutputFieldName = null,
                    DocumentType = "image",
                    ExtraInputs = extraInputs
                },
                streamCallback,
                ct).ConfigureAwait(false);
            }

            var result = BuildWorkflowResult(
                request,
                workflowResult,
                imagePath,
                userId,
                normalizedCatalogMode,
                commandCatalog,
                requestInputsJson,
                requestPayloadJson,
                streamEvents);
            WriteTrace(result);
            return result;
        }

        public async Task StopAsync(string taskId, string apiKeyOverride, CancellationToken ct = default)
        {
            if (!AgentAutomationService.ModuleEnabled)
                throw new InvalidOperationException("智能体控制模块已冻结。");
            if (string.IsNullOrWhiteSpace(taskId))
                throw new InvalidOperationException("当前任务尚未生成 taskId，无法调用停止接口。");

            var cfg = ConfigService.Current ?? throw new InvalidOperationException("配置尚未加载。");
            if (string.IsNullOrWhiteSpace(cfg.URL))
                throw new InvalidOperationException("URL 未配置，无法停止多轮任务流 Workflow。");
            var apiKey = string.IsNullOrWhiteSpace(apiKeyOverride)
                ? ConfigService.ResolveExecutorWorkflowApiKey(cfg)
                : apiKeyOverride.Trim();
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("Executor Key 未配置，无法停止 Workflow。");

            using var client = new FileWorkflowClient(new FileWorkflowClientOptions
            {
                BaseUrl = cfg.URL,
                ApiKey = apiKey
            });
            await client.StopTaskAsync(taskId.Trim(), ResolveUser(cfg), ct).ConfigureAwait(false);
        }

        private static UiCoarseVisionWorkflowResult BuildWorkflowResult(
            UiCoarseVisionWorkflowRequest request,
            FileWorkflowResult workflowResult,
            string imagePath,
            string userId,
            string normalizedCatalogMode,
            JObject commandCatalog,
            string requestInputsJson,
            string requestPayloadJson,
            IEnumerable<UiCoarseVisionWorkflowStreamEvent> streamEvents = null)
        {
            var normalizedStreamEvents = streamEvents == null
                ? new List<UiCoarseVisionWorkflowStreamEvent>()
                : streamEvents.Where(x => x != null).ToList();
            var workflowRunId = !string.IsNullOrWhiteSpace(workflowResult?.WorkflowRunId)
                ? workflowResult.WorkflowRunId
                : ResolveWorkflowIdentifier(normalizedStreamEvents, useTaskId: false);
            var taskId = !string.IsNullOrWhiteSpace(workflowResult?.TaskId)
                ? workflowResult.TaskId
                : ResolveWorkflowIdentifier(normalizedStreamEvents, useTaskId: true);

            return new UiCoarseVisionWorkflowResult
            {
                Succeeded = workflowResult.Succeeded,
                ImageFilePath = imagePath ?? string.Empty,
                ImageKind = ResolveImageKind(request),
                UserId = userId,
                Goal = request.Goal ?? string.Empty,
                CommandCatalogMode = normalizedCatalogMode,
                CommandCatalogJson = commandCatalog.ToString(Formatting.None),
                RequestInputsJson = requestInputsJson ?? string.Empty,
                RequestPayloadJson = requestPayloadJson ?? string.Empty,
                ResultJson = ExtractResultJson(workflowResult),
                Outputs = workflowResult.Outputs,
                RawResponse = workflowResult.RawResponse ?? string.Empty,
                WorkflowRunId = workflowRunId ?? string.Empty,
                TaskId = taskId ?? string.Empty,
                ErrorMessage = workflowResult.Succeeded ? string.Empty : ExtractErrorMessage(workflowResult),
                CompletedAtLocal = DateTime.Now,
                StreamEvents = normalizedStreamEvents
            };
        }

        private static string ResolveWorkflowIdentifier(
            IEnumerable<UiCoarseVisionWorkflowStreamEvent> streamEvents,
            bool useTaskId)
        {
            if (streamEvents == null)
                return string.Empty;

            foreach (var evt in streamEvents.Reverse())
            {
                if (evt == null)
                    continue;

                var value = useTaskId ? evt.TaskId : evt.WorkflowRunId;
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }

            return string.Empty;
        }

        private static string ExtractResultJson(FileWorkflowResult workflowResult)
        {
            if (workflowResult?.Outputs == null)
                return DifyOutputSanitizer.Clean(workflowResult?.OutputText);

            var token = workflowResult.Outputs["result_json"] ?? workflowResult.Outputs["text"];
            if (token == null || token.Type == JTokenType.Null)
                return DifyOutputSanitizer.Clean(workflowResult.OutputText ?? DifyOutputSanitizer.CleanToken(workflowResult.Outputs));

            if (token.Type == JTokenType.String)
                return DifyOutputSanitizer.Clean(token.Value<string>());

            return DifyOutputSanitizer.CleanToken(token);
        }

        private static string ExtractErrorMessage(FileWorkflowResult workflowResult)
        {
            if (workflowResult == null)
                return "Workflow 未返回结果。";

            if (!string.IsNullOrWhiteSpace(workflowResult.ErrorMessage))
                return workflowResult.ErrorMessage;

            var raw = workflowResult.RawResponse ?? string.Empty;
            if (string.IsNullOrWhiteSpace(raw))
                return "Workflow 调用失败。";

            var preview = raw
                .Replace("\r", " ")
                .Replace("\n", " / ")
                .Trim();
            return preview.Length <= 240 ? preview : preview.Substring(0, 237) + "...";
        }

        private static string ResolveUser(AppConfig cfg)
        {
            var configuredUser = cfg?.User;
            return string.IsNullOrWhiteSpace(configuredUser)
                ? Environment.MachineName
                : configuredUser.Trim();
        }

        private static string SaveInputImageIfNeeded(string imageBase64)
        {
            if (string.IsNullOrWhiteSpace(imageBase64))
                return string.Empty;

            Directory.CreateDirectory(AgentControlPaths.ExecutorLogRoot);

            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmssfff");
            var imagePath = Path.Combine(AgentControlPaths.ExecutorLogRoot, "coarse-" + timestamp + ".png");
            File.WriteAllBytes(imagePath, Convert.FromBase64String(imageBase64));
            LogRetentionPolicy.TryCleanupFiles(
                AgentControlPaths.ExecutorLogRoot,
                "coarse-*.png",
                SearchOption.TopDirectoryOnly,
                TimeSpan.FromDays(14));
            return imagePath;
        }

        private static string ResolveImageKind(UiCoarseVisionWorkflowRequest request)
        {
            var declaredKind = request?.ImageKind;
            if (!string.IsNullOrWhiteSpace(declaredKind))
                return declaredKind.Trim();

            return string.IsNullOrWhiteSpace(request?.ImageBase64)
                ? InternalCaptureImageKind
                : "local_upload";
        }

        private static void WriteTrace(UiCoarseVisionWorkflowResult result)
        {
            try
            {
                Directory.CreateDirectory(AgentControlPaths.ExecutorLogRoot);

                var now = result?.CompletedAtLocal ?? DateTime.Now;
                var summaryLogPath = Path.Combine(AgentControlPaths.ExecutorLogRoot, now.ToString("yyyy-MM-dd-HH") + ".log");
                var detailLogPath = BuildDetailLogPath(AgentControlPaths.ExecutorLogRoot, result);
                var summaryText = BuildTraceOutput(result, detailLogPath);
                var detailText = BuildDetailedTraceOutput(result, detailLogPath);

                lock (s_logLock)
                {
                    File.WriteAllText(detailLogPath, detailText, new UTF8Encoding(false));
                    File.AppendAllText(summaryLogPath, summaryText + Environment.NewLine + new string('-', 64) + Environment.NewLine, new UTF8Encoding(false));
                }

                LogRetentionPolicy.TryCleanupFiles(
                    AgentControlPaths.ExecutorLogRoot,
                    "*.log",
                    SearchOption.AllDirectories,
                    TimeSpan.FromDays(14),
                    deleteEmptyDirectories: true);
            }
            catch
            {
                // 日志失败不影响主流程
            }
        }

        private static UiCoarseVisionWorkflowStreamEvent CreateStreamEvent(FileWorkflowStreamEvent evt)
        {
            return new UiCoarseVisionWorkflowStreamEvent
            {
                EventName = evt?.EventName ?? string.Empty,
                WorkflowRunId = evt?.WorkflowRunId ?? string.Empty,
                TaskId = evt?.TaskId ?? string.Empty,
                NodeTitle = evt?.Data?.Value<string>("title") ?? string.Empty,
                NodeType = evt?.Data?.Value<string>("node_type") ?? string.Empty,
                Status = evt?.Data?.Value<string>("status") ?? string.Empty,
                Data = evt?.Data == null ? null : SanitizeTokenForLog(evt.Data) as JObject,
                RawJson = SanitizeStringForLog(evt?.RawJson ?? string.Empty, "raw_json"),
                LocalTimestamp = DateTime.Now
            };
        }

        private static string BuildTraceOutput(UiCoarseVisionWorkflowResult result, string detailLogPath)
        {
            var sb = new StringBuilder();
            var completedAt = result?.CompletedAtLocal ?? DateTime.Now;
            sb.AppendLine("时间：" + completedAt.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("结果：" + (result?.Succeeded == true ? "成功" : "失败"));
            sb.AppendLine("目标：" + SafeText(result?.Goal));
            sb.AppendLine("图片来源：" + SafeText(result?.ImageKind));
            AppendKeyValue(sb, "截图文件", result?.ImageFilePath);
            AppendKeyValue(sb, "WorkflowRunId", result?.WorkflowRunId);
            AppendKeyValue(sb, "TaskId", result?.TaskId);
            AppendKeyValue(sb, "CommandCatalogMode", result?.CommandCatalogMode, showIfEmpty: true);
            AppendKeyValue(sb, "CommandCatalogVersion", UiWorkflowTraceFormatter.TryExtractCommandCatalogVersion(result?.CommandCatalogJson), showIfEmpty: true);
            AppendKeyValue(sb, "任务结果", UiWorkflowTraceFormatter.TryExtractWorkflowSummary(result), showIfEmpty: true);
            AppendKeyValue(sb, "最后一步结果", UiWorkflowTraceFormatter.TryExtractLastResultSummary(result), showIfEmpty: true);
            AppendKeyValue(sb, "错误信息", result?.ErrorMessage);
            AppendKeyValue(sb, "详细日志", detailLogPath);

            var roundLines = UiWorkflowTraceFormatter.BuildWorkflowRoundLines(result?.StreamEvents);
            if (roundLines.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("每轮判定");
                sb.AppendLine("----------------------------------------");
                foreach (var line in roundLines)
                    sb.AppendLine(line);
            }

            return sb.ToString().TrimEnd();
        }

        private static string BuildDetailedTraceOutput(UiCoarseVisionWorkflowResult result, string detailLogPath)
        {
            var sb = new StringBuilder();
            var completedAt = result?.CompletedAtLocal ?? DateTime.Now;
            sb.AppendLine("时间：" + completedAt.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("结果：" + (result?.Succeeded == true ? "成功" : "失败"));
            sb.AppendLine("目标：" + SafeText(result?.Goal));
            AppendKeyValue(sb, "图片来源", result?.ImageKind, showIfEmpty: true);
            AppendKeyValue(sb, "截图文件", result?.ImageFilePath);
            AppendKeyValue(sb, "用户", result?.UserId);
            AppendKeyValue(sb, "WorkflowRunId", result?.WorkflowRunId);
            AppendKeyValue(sb, "TaskId", result?.TaskId);
            AppendKeyValue(sb, "CommandCatalogMode", result?.CommandCatalogMode, showIfEmpty: true);
            AppendKeyValue(sb, "CommandCatalogVersion", UiWorkflowTraceFormatter.TryExtractCommandCatalogVersion(result?.CommandCatalogJson), showIfEmpty: true);
            AppendKeyValue(sb, "任务结果", UiWorkflowTraceFormatter.TryExtractWorkflowSummary(result), showIfEmpty: true);
            AppendKeyValue(sb, "最后一步结果", UiWorkflowTraceFormatter.TryExtractLastResultSummary(result), showIfEmpty: true);
            AppendKeyValue(sb, "错误信息", result?.ErrorMessage);
            AppendKeyValue(sb, "当前日志文件", detailLogPath);

            AppendJsonSection(sb, "Workflow 触发输入", result?.RequestInputsJson);
            AppendJsonSection(sb, "Workflow 请求体", result?.RequestPayloadJson);
            AppendJsonSection(sb, "Workflow 最终输出", result?.Outputs);
            AppendJsonSection(sb, "Workflow 最终结果 JSON", result?.ResultJson);

            var events = result?.StreamEvents ?? new List<UiCoarseVisionWorkflowStreamEvent>();
            AppendKeyValue(sb, "节点事件数", events.Count.ToString(), showIfEmpty: true);
            sb.AppendLine();
            sb.AppendLine("节点明细");
            sb.AppendLine("----------------------------------------");

            if (events.Count == 0)
            {
                sb.AppendLine("本次未记录到 streaming 节点事件。");
            }
            else
            {
                foreach (var evt in events)
                    AppendEventDetail(sb, evt);
            }

            if (events.Count == 0)
                AppendJsonSection(sb, "Workflow 原始 SSE 事件", result?.RawResponse);

            return sb.ToString().TrimEnd();
        }

        private static void AppendEventDetail(StringBuilder sb, UiCoarseVisionWorkflowStreamEvent evt)
        {
            if (sb == null || evt == null)
                return;

            var time = FormatEventTime(evt);
            var title = NormalizeSingleLine(evt.NodeTitle);
            var eventName = NormalizeSingleLine(evt.EventName);
            var nodeType = NormalizeSingleLine(evt.NodeType);
            var status = NormalizeSingleLine(evt.Status);

            sb.AppendLine($"[{SafeText(time)}] {SafeText(eventName)} | title={SafeText(title)} | type={SafeText(nodeType)} | status={SafeText(status)}");

            var data = evt.Data;
            var appendedStructuredSection = false;
            if (data != null)
            {
                appendedStructuredSection |= AppendJsonSection(sb, "输入", data["inputs"]);
                appendedStructuredSection |= AppendJsonSection(sb, "数据处理", data["process_data"]);
                appendedStructuredSection |= AppendJsonSection(sb, "输出", data["outputs"]);
                appendedStructuredSection |= AppendJsonSection(sb, "执行元数据", data["execution_metadata"]);
                appendedStructuredSection |= AppendJsonSection(sb, "错误", data["error"]);
            }

            if (!appendedStructuredSection && !string.IsNullOrWhiteSpace(evt.RawJson))
                AppendJsonSection(sb, "原始事件 JSON", evt.RawJson);

            sb.AppendLine();
        }

        private static bool AppendJsonSection(StringBuilder sb, string title, object content)
        {
            if (sb == null || string.IsNullOrWhiteSpace(title) || content == null)
                return false;

            var token = ConvertToLogToken(content);
            if (token == null || token.Type == JTokenType.Null)
                return false;

            var printed = token.Type == JTokenType.String
                ? token.Value<string>()
                : token.ToString(Formatting.Indented);

            printed = NormalizeMultiline(printed);
            if (string.IsNullOrWhiteSpace(printed))
                return false;

            sb.AppendLine(title + "：");
            sb.AppendLine(printed);
            return true;
        }

        private static JToken ConvertToLogToken(object content)
        {
            if (content == null)
                return null;

            try
            {
                if (content is string text)
                {
                    if (string.IsNullOrWhiteSpace(text))
                        return null;

                    try
                    {
                        return SanitizeTokenForLog(JToken.Parse(text));
                    }
                    catch
                    {
                        return new JValue(SanitizeStringForLog(text, string.Empty));
                    }
                }

                if (content is JToken token)
                    return SanitizeTokenForLog(token);

                return SanitizeTokenForLog(JToken.FromObject(content));
            }
            catch
            {
                return new JValue(SanitizeStringForLog(content.ToString(), string.Empty));
            }
        }

        private static JToken SanitizeTokenForLog(JToken token, string propertyName = "")
        {
            if (token == null || token.Type == JTokenType.Null)
                return JValue.CreateNull();

            if (token.Type == JTokenType.Object)
            {
                var source = token as JObject;
                var result = new JObject();
                if (source != null)
                {
                    foreach (var property in source.Properties())
                    {
                        if (DifyOutputSanitizer.IsReasoningFieldName(property.Name))
                            continue;

                        result[property.Name] = SanitizeTokenForLog(property.Value, property.Name);
                    }
                }

                return result;
            }

            if (token.Type == JTokenType.Array)
            {
                var source = token as JArray;
                var result = new JArray();
                if (source != null)
                {
                    var index = 0;
                    foreach (var item in source)
                    {
                        if (index >= MaxLoggedArrayItems)
                        {
                            result.Add($"[数组已截断，原始长度 {source.Count}]");
                            break;
                        }

                        result.Add(SanitizeTokenForLog(item, propertyName));
                        index++;
                    }
                }

                return result;
            }

            if (token.Type == JTokenType.String)
            {
                var text = token.Value<string>();
                if (TryParseJsonLikeString(text, out var parsed))
                    return SanitizeTokenForLog(parsed, propertyName);

                return new JValue(SanitizeStringForLog(DifyOutputSanitizer.Clean(text), propertyName));
            }

            return token.DeepClone();
        }

        private static string SanitizeStringForLog(string value, string propertyName)
        {
            if (string.IsNullOrEmpty(value))
                return value ?? string.Empty;

            var normalizedProperty = (propertyName ?? string.Empty).Trim();
            if (normalizedProperty.IndexOf("base64", StringComparison.OrdinalIgnoreCase) >= 0)
                return $"[已省略 {normalizedProperty}，长度 {value.Length}]";

            if (normalizedProperty.IndexOf("image", StringComparison.OrdinalIgnoreCase) >= 0 &&
                value.Length > 256 &&
                IsProbablyBase64(value))
            {
                return $"[已省略 {normalizedProperty} 图像内容，长度 {value.Length}]";
            }

            if (value.Length <= MaxLoggedStringLength)
                return value;

            return value.Substring(0, MaxLoggedStringLength) + $"...(已截断，原始长度 {value.Length})";
        }

        private static bool IsProbablyBase64(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            var sample = value.Trim();
            if (sample.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return true;

            if (sample.Length < 128)
                return false;

            var maxLength = Math.Min(sample.Length, 256);
            for (var i = 0; i < maxLength; i++)
            {
                var ch = sample[i];
                if ((ch >= 'A' && ch <= 'Z') ||
                    (ch >= 'a' && ch <= 'z') ||
                    (ch >= '0' && ch <= '9') ||
                    ch == '+' || ch == '/' || ch == '=' ||
                    ch == '\r' || ch == '\n')
                {
                    continue;
                }

                return false;
            }

            return true;
        }

        private static bool TryParseJsonLikeString(string value, out JToken parsed)
        {
            parsed = null;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            var trimmed = value.Trim();
            if ((trimmed.StartsWith("{") && trimmed.EndsWith("}")) ||
                (trimmed.StartsWith("[") && trimmed.EndsWith("]")))
            {
                try
                {
                    parsed = JToken.Parse(trimmed);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        private static string NormalizeMultiline(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            return value.Replace("\r\n", "\n").Replace("\r", "\n").Trim();
        }

        private static string BuildRequestInputsJson(IDictionary<string, object> extraInputs)
        {
            if (extraInputs == null)
                return string.Empty;

            return JObject.FromObject(extraInputs).ToString(Formatting.Indented);
        }

        private static string BuildRequestPayloadJson(IDictionary<string, object> extraInputs, string userId, string responseMode)
        {
            var payload = new JObject
            {
                ["inputs"] = extraInputs == null ? new JObject() : JObject.FromObject(extraInputs),
                ["response_mode"] = string.IsNullOrWhiteSpace(responseMode) ? "blocking" : responseMode,
                ["user"] = userId ?? string.Empty
            };

            return payload.ToString(Formatting.Indented);
        }

        private static string BuildDetailLogPath(string rootDir, UiCoarseVisionWorkflowResult result)
        {
            var completedAt = result?.CompletedAtLocal ?? DateTime.Now;
            var detailDir = Path.Combine(rootDir, "Details", completedAt.ToString("yyyy-MM-dd"));
            Directory.CreateDirectory(detailDir);

            var runIdPart = MakeSafeFileNamePart(result?.WorkflowRunId, 48);
            var taskIdPart = MakeSafeFileNamePart(result?.TaskId, 32);
            var suffix = string.IsNullOrWhiteSpace(runIdPart)
                ? (string.IsNullOrWhiteSpace(taskIdPart) ? "no-run-id" : taskIdPart)
                : runIdPart;

            return Path.Combine(detailDir, completedAt.ToString("yyyyMMdd-HHmmssfff") + "-" + suffix + ".log");
        }

        private static string MakeSafeFileNamePart(string value, int maxLength)
        {
            var text = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            var invalidChars = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder();
            foreach (var ch in text)
            {
                if (invalidChars.Contains(ch))
                    continue;

                builder.Append(ch);
                if (builder.Length >= maxLength)
                    break;
            }

            return builder.ToString().Trim();
        }

        private static string NormalizeSingleLine(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            return value
                .Replace("\r", " ")
                .Replace("\n", " / ")
                .Trim();
        }

        private static string FormatEventTime(UiCoarseVisionWorkflowStreamEvent evt)
        {
            if (evt == null || evt.LocalTimestamp == default)
                return DateTime.Now.ToString("HH:mm:ss");

            return evt.LocalTimestamp.ToString("HH:mm:ss");
        }

        private static void AppendKeyValue(StringBuilder sb, string label, string value, bool showIfEmpty = false)
        {
            var normalized = NormalizeSingleLine(value);
            if (!showIfEmpty && string.IsNullOrWhiteSpace(normalized))
                return;

            sb.AppendLine(label + "：" + SafeText(normalized));
        }

        private static string SafeText(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "-" : value;
        }

        private static string NormalizeCommandCatalogMode(string mode)
        {
            var normalized = (mode ?? string.Empty).Trim().ToLowerInvariant();
            return normalized switch
            {
                "" => DefaultCommandCatalogMode,
                "mouse_only_compact" or "mouse_compact" or "mouse" => "mouse_only_compact",
                "general_compact" or "compact" or "general" => "general_compact",
                "grounded_light_compact" or "decision_compact" or "grounded_compact" or "execute_compact" or "grounded" => "grounded_light_compact",
                "mouse_only_full" or "mouse_full" => "mouse_only_full",
                "general_full" or "full" => "general_full",
                "grounded_light_full" or "grounded_full" or "execute_full" => "grounded_full",
                _ => DefaultCommandCatalogMode
            };
        }

        private static JObject BuildCommandCatalog(string mode)
        {
            return mode switch
            {
                "general_compact" => UiCommandCatalogService.BuildCompactCatalog(),
                "grounded_light_compact" => UiCommandCatalogService.BuildGroundedCompactCatalog(),
                "grounded_compact" => UiCommandCatalogService.BuildGroundedCompactCatalog(),
                "mouse_only_full" => UiCommandCatalogService.BuildMouseOnlyCatalog(),
                "general_full" => UiCommandCatalogService.BuildCatalog(),
                "grounded_full" => UiCommandCatalogService.BuildGroundedCatalog(),
                _ => UiCommandCatalogService.BuildMouseOnlyCompactCatalog()
            };
        }
    }

    public sealed class UiCoarseVisionWorkflowRequest
    {
        public string ImageBase64 { get; set; }
        public string ImageKind { get; set; }
        public string Goal { get; set; }
        public string CommandCatalogMode { get; set; }
        public string ApiKeyOverride { get; set; }
        public IDictionary<string, object> ExtraInputs { get; set; }
    }

    public sealed class UiCoarseVisionWorkflowResult
    {
        public bool Succeeded { get; set; }
        public string ImageFilePath { get; set; }
        public string ImageKind { get; set; }
        public string UserId { get; set; }
        public string Goal { get; set; }
        public string CommandCatalogMode { get; set; }
        public string CommandCatalogJson { get; set; }
        public string RequestInputsJson { get; set; }
        public string RequestPayloadJson { get; set; }
        public string ResultJson { get; set; }
        public JObject Outputs { get; set; }
        public string RawResponse { get; set; }
        public string WorkflowRunId { get; set; }
        public string TaskId { get; set; }
        public string ErrorMessage { get; set; }
        public DateTime CompletedAtLocal { get; set; }
        public List<UiCoarseVisionWorkflowStreamEvent> StreamEvents { get; set; } = new List<UiCoarseVisionWorkflowStreamEvent>();
    }

    public sealed class UiCoarseVisionWorkflowStreamEvent
    {
        public string EventName { get; set; }
        public string WorkflowRunId { get; set; }
        public string TaskId { get; set; }
        public string NodeTitle { get; set; }
        public string NodeType { get; set; }
        public string Status { get; set; }
        public JObject Data { get; set; }
        public string RawJson { get; set; }
        public DateTime LocalTimestamp { get; set; }
    }
}

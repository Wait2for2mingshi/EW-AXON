using McpServer;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace EW_Assistant.McpTools
{
    /// <summary>
    /// UI 运行时核心：截图、候选提取、执行与验收。
    /// </summary>
    internal static class UiRuntimeService
    {
        private const string ModuleDisabledMessage = "智能体控制模块已冻结。";
        private const string ObserveDetailDecisionLight = "decision_light";
        private const string ObserveDetailFull = "full";
        private const int DefaultLightCandidateLimit = 12;
        private const int DefaultFullCandidateLimit = 80;

        public static async Task<string> ObserveAsync(UiRuntimeHttpBridge.UiRuntimeObserveRequest? req)
        {
            if (!Base.IsAgentControlModuleEnabled())
                return BuildError("UiRuntimeObserve", ModuleDisabledMessage);

            var detailLevel = NormalizeObserveDetailLevel(req?.DetailLevel);
            var includeImages = req?.IncludeImages ?? string.Equals(detailLevel, ObserveDetailFull, StringComparison.Ordinal);
            var maxCandidates = req?.MaxCandidates ?? (string.Equals(detailLevel, ObserveDetailFull, StringComparison.Ordinal)
                ? DefaultFullCandidateLimit
                : DefaultLightCandidateLimit);

            var observe = await BuildObserveResultAsync(
                lane: req?.Lane ?? "default",
                scope: req?.Scope ?? "foreground_window",
                windowTitleContains: req?.WindowTitleContains,
                includeChildren: req?.IncludeChildren ?? true,
                maxChildren: req?.MaxChildren ?? 120,
                maxCandidates: maxCandidates,
                includeImages: includeImages,
                detailLevel: detailLevel,
                goal: req?.Goal,
                persistSession: true).ConfigureAwait(false);

            return observe.ToJson();
        }

        public static async Task<UiRuntimeCaptureResult> CaptureAsync(UiRuntimeHttpBridge.UiRuntimeCaptureRequest? req)
        {
            if (!Base.IsAgentControlModuleEnabled())
            {
                return new UiRuntimeCaptureResult
                {
                    Ok = false,
                    ErrorJson = BuildError("UiRuntimeCapture", ModuleDisabledMessage)
                };
            }

            var laneKey = UiLaneScheduler.NormalizeLane(req?.Lane ?? "default");
            var scopeKey = NormalizeScope(req?.Scope);
            var titleFilter = string.Equals(scopeKey, "foreground_window", StringComparison.Ordinal)
                ? null
                : req?.WindowTitleContains;
            var delayMs = Math.Max(0, Math.Min(req?.DelayMs ?? 0, 5000));

            if (delayMs > 0)
                await Task.Delay(delayMs).ConfigureAwait(false);

            var snapshotJson = await WindowsUiTools.UiSnapshot(
                lane: laneKey,
                windowTitleContains: titleFilter,
                includeChildren: false,
                maxChildren: 0).ConfigureAwait(false);

            var snapshot = ParseJsonOrFallback(snapshotJson, "UiRuntimeCapture");
            var ok = snapshot.Value<bool?>("ok") ?? false;
            if (!ok)
            {
                return new UiRuntimeCaptureResult
                {
                    Ok = false,
                    ErrorJson = BuildError(
                        "UiRuntimeCapture",
                        snapshot.Value<string>("message") ?? "UiSnapshot 失败。")
                };
            }

            var target = snapshot["target"] as JObject;
            var captureBounds = ResolveCaptureBounds(scopeKey, target);
            using var bitmap = CaptureBounds(captureBounds);
            var bytes = BitmapToBytes(bitmap);

            return new UiRuntimeCaptureResult
            {
                Ok = true,
                Lane = laneKey,
                Scope = scopeKey,
                TargetTitle = GetString(target, "Title"),
                CaptureLeft = captureBounds.Left,
                CaptureTop = captureBounds.Top,
                CaptureWidth = captureBounds.Width,
                CaptureHeight = captureBounds.Height,
                ImageBytes = bytes,
                FileName = $"capture-{DateTime.Now:yyyyMMdd-HHmmssfff}.png"
            };
        }

        public static async Task<string> ExecuteAsync(UiRuntimeHttpBridge.UiRuntimeExecuteRequest? req)
        {
            if (!Base.IsAgentControlModuleEnabled())
                return BuildError("UiRuntimeExecute", ModuleDisabledMessage);

            if (req == null)
                return BuildError("UiRuntimeExecute", "请求体为空。");

            if (string.IsNullOrWhiteSpace(req.RuntimeId))
                return BuildError("UiRuntimeExecute", "缺少 runtimeId。");

            if (string.IsNullOrWhiteSpace(req.TargetId))
                return BuildError("UiRuntimeExecute", "缺少 targetId。");

            if (!UiRuntimeSessionStore.TryGet(req.RuntimeId, out var session))
                return BuildError("UiRuntimeExecute", "runtimeId 不存在或已过期。");

            if (!session.Candidates.TryGetValue(req.TargetId.Trim(), out var candidate))
                return BuildError("UiRuntimeExecute", $"targetId 不存在：{req.TargetId}");

            var lane = string.IsNullOrWhiteSpace(req.Lane) ? session.Lane : req.Lane.Trim();
            var action = UiActionPolicy.NormalizeAction(req.Action);
            if (string.IsNullOrWhiteSpace(action))
                action = "click";

            string actionResult;
            if (!string.IsNullOrWhiteSpace(candidate.RefId))
            {
                actionResult = await WindowsUiTools.UiActByRef(
                    snapshotId: session.SnapshotId,
                    refId: candidate.RefId,
                    action: action,
                    text: req.Text,
                    confirmed: req.Confirmed,
                    lane: lane,
                    idempotencyKey: req.IdempotencyKey,
                    retries: req.Retries,
                    verifyTimeoutMs: req.VerifyTimeoutMs,
                    verifyTitleContains: req.VerifyTitleContains).ConfigureAwait(false);
            }
            else if (candidate.Selector != null)
            {
                actionResult = await WindowsUiTools.UiActBySelector(
                    titleContains: candidate.Selector.Value<string>("titleContains"),
                    className: candidate.Selector.Value<string>("className"),
                    processName: candidate.Selector.Value<string>("processName"),
                    childTitleContains: candidate.Selector.Value<string>("childTitleContains"),
                    childClassName: candidate.Selector.Value<string>("childClassName"),
                    windowIndex: candidate.Selector.Value<int?>("windowIndex") ?? 0,
                    action: action,
                    text: req.Text,
                    confirmed: req.Confirmed,
                    lane: lane,
                    idempotencyKey: req.IdempotencyKey,
                    retries: req.Retries,
                    verifyTimeoutMs: req.VerifyTimeoutMs,
                    verifyTitleContains: req.VerifyTitleContains).ConfigureAwait(false);
            }
            else
            {
                if (string.Equals(action, "set_text", StringComparison.OrdinalIgnoreCase))
                    return BuildError("UiRuntimeExecute", "当前候选元素缺少稳定引用，无法执行 set_text。");

                if (candidate.Hwnd == 0)
                    return BuildError("UiRuntimeExecute", "当前候选元素缺少 hwnd，无法执行坐标兜底点击。");

                actionResult = await WindowsUiTools.UiActByHwnd(
                    hwnd: candidate.Hwnd,
                    action: action,
                    text: req.Text,
                    confirmed: req.Confirmed,
                    lane: lane,
                    idempotencyKey: req.IdempotencyKey,
                    x: candidate.CenterX,
                    y: candidate.CenterY,
                    retries: req.Retries,
                    verifyTimeoutMs: req.VerifyTimeoutMs,
                    verifyTitleContains: req.VerifyTitleContains).ConfigureAwait(false);
            }

            var wrapper = ParseJsonOrFallback(actionResult, "UiRuntimeExecute");
            wrapper["type"] = "ui.runtime.execute";
            wrapper["runtimeId"] = session.RuntimeId;
            wrapper["snapshotId"] = session.SnapshotId;
            wrapper["targetId"] = candidate.TargetId;
            wrapper["targetType"] = candidate.Type;
            wrapper["targetText"] = candidate.Text;
            wrapper["source"] = candidate.Source;
            wrapper["refId"] = candidate.RefId ?? string.Empty;
            return wrapper.ToString(Formatting.None);
        }

        public static async Task<string> VerifyAsync(UiRuntimeHttpBridge.UiRuntimeVerifyRequest? req)
        {
            if (!Base.IsAgentControlModuleEnabled())
                return BuildError("UiRuntimeVerify", ModuleDisabledMessage);

            return await VerifyCoreAsync(
                lane: req?.Lane ?? "default",
                scope: req?.Scope ?? "foreground_window",
                windowTitleContains: req?.WindowTitleContains,
                expectedTitleContains: req?.ExpectedTitleContains,
                expectedTextContains: req?.ExpectedTextContains,
                requireTargetVisible: req?.RequireTargetVisible ?? true,
                includeChildren: req?.IncludeChildren ?? false,
                maxChildren: req?.MaxChildren ?? 60,
                verifyMode: "generic",
                excludeInputLikeText: false).ConfigureAwait(false);
        }

        public static async Task<string> CommandAsync(UiRuntimeHttpBridge.UiRuntimeCommandRequest? req)
        {
            if (!Base.IsAgentControlModuleEnabled())
                return BuildError("UiRuntimeCommand", ModuleDisabledMessage);

            if (req == null)
                return BuildError("UiRuntimeCommand", "请求体为空。");

            if (string.Equals(req.CommandType?.Trim(), "verify", StringComparison.OrdinalIgnoreCase))
            {
                var normalizedOperation = NormalizeVerifyOperation(req.Operation);
                if (string.IsNullOrWhiteSpace(normalizedOperation))
                    return BuildError("UiRuntimeCommand", "verify.operation 仅支持 window_ready、text_visible、chat_message_sent。");

                var verifyJson = await ExecuteVerifyCommandAsync(req, normalizedOperation).ConfigureAwait(false);
                var verifyWrapper = ParseJsonOrFallback(verifyJson, "UiRuntimeCommand");
                var originalType = verifyWrapper.Value<string>("type") ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(originalType))
                    verifyWrapper["innerType"] = originalType;
                verifyWrapper["type"] = "ui.runtime.command";
                verifyWrapper["commandType"] = "verify";
                verifyWrapper["operation"] = normalizedOperation;
                return verifyWrapper.ToString(Formatting.None);
            }

            if (string.Equals(req.CommandType?.Trim(), "execute", StringComparison.OrdinalIgnoreCase))
            {
                var executeJson = await ExecuteAsync(new UiRuntimeHttpBridge.UiRuntimeExecuteRequest
                {
                    RuntimeId = req.RuntimeId ?? string.Empty,
                    TargetId = req.TargetId ?? string.Empty,
                    Action = req.Operation ?? string.Empty,
                    Text = req.Text ?? string.Empty,
                    Confirmed = req.Confirmed,
                    Lane = req.Lane ?? "default",
                    IdempotencyKey = req.IdempotencyKey ?? string.Empty,
                    Retries = req.Retries,
                    VerifyTimeoutMs = req.VerifyTimeoutMs ?? 900,
                    VerifyTitleContains = req.VerifyTitleContains ?? string.Empty
                }).ConfigureAwait(false);

                var executeWrapper = ParseJsonOrFallback(executeJson, "UiRuntimeCommand");
                var originalType = executeWrapper.Value<string>("type") ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(originalType))
                    executeWrapper["innerType"] = originalType;
                executeWrapper["type"] = "ui.runtime.command";
                executeWrapper["commandType"] = "execute";
                executeWrapper["operation"] = UiActionPolicy.NormalizeAction(req.Operation);
                return executeWrapper.ToString(Formatting.None);
            }

            var pointArray = req.Point_2d is { Length: >= 2 }
                ? req.Point_2d
                : (req.Point2d is { Length: >= 2 } ? req.Point2d : null);

            var relativeX = req.RelativeX ?? pointArray?[0];
            var relativeY = req.RelativeY ?? pointArray?[1];

            var actionResult = await WindowsUiTools.UiCommand(
                commandType: req.CommandType,
                operation: req.Operation,
                x: req.X,
                y: req.Y,
                relativeX: relativeX,
                relativeY: relativeY,
                captureLeft: req.CaptureLeft,
                captureTop: req.CaptureTop,
                captureWidth: req.CaptureWidth,
                captureHeight: req.CaptureHeight,
                text: req.Text,
                keys: req.Keys ?? Array.Empty<string>(),
                modifiers: req.Modifiers ?? Array.Empty<string>(),
                commandLine: req.CommandLine,
                workingDirectory: req.WorkingDirectory,
                fileName: req.FileName,
                appName: req.AppName,
                browserName: req.BrowserName,
                url: req.Url,
                query: req.Query,
                site: req.Site,
                maxResults: req.MaxResults ?? 20,
                targetWindowTitleContains: req.TargetWindowTitleContains,
                confirmed: req.Confirmed,
                timeoutMs: req.TimeoutMs ?? 5000,
                pollMs: req.PollMs ?? 200,
                lane: req.Lane ?? "default").ConfigureAwait(false);

            var wrapper = ParseJsonOrFallback(actionResult, "UiRuntimeCommand");
            wrapper["type"] = "ui.runtime.command";
            return wrapper.ToString(Formatting.None);
        }

        private static string NormalizeVerifyOperation(string? operation)
        {
            var normalized = (operation ?? string.Empty).Trim().ToLowerInvariant();
            return normalized switch
            {
                "window_ready" or "window_visible" or "verify_window" or "验证窗口" => "window_ready",
                "text_visible" or "verify_text" or "验证文本" => "text_visible",
                "chat_message_sent" or "verify_chat_message" or "验证消息已发送" => "chat_message_sent",
                _ => string.Empty
            };
        }

        private static async Task<string> ExecuteVerifyCommandAsync(
            UiRuntimeHttpBridge.UiRuntimeCommandRequest req,
            string normalizedOperation)
        {
            var title = req.TargetWindowTitleContains?.Trim() ?? string.Empty;
            var expectedText = FirstNonEmpty(req.ExpectedTextContains, req.Text).Trim();
            var scope = string.IsNullOrWhiteSpace(req.Scope) ? "foreground_window" : req.Scope.Trim();

            return normalizedOperation switch
            {
                "window_ready" => string.IsNullOrWhiteSpace(title)
                    ? BuildError("UiRuntimeCommand", "verify.window_ready 需要 targetWindowTitleContains。")
                    : await VerifyCoreAsync(
                        lane: req.Lane ?? "default",
                        scope: scope,
                        windowTitleContains: title,
                        expectedTitleContains: title,
                        expectedTextContains: string.Empty,
                        requireTargetVisible: req.RequireTargetVisible ?? true,
                        includeChildren: req.IncludeChildren ?? false,
                        maxChildren: req.MaxChildren ?? 60,
                        verifyMode: normalizedOperation,
                        excludeInputLikeText: false).ConfigureAwait(false),
                "text_visible" => string.IsNullOrWhiteSpace(expectedText)
                    ? BuildError("UiRuntimeCommand", "verify.text_visible 需要 expectedTextContains。")
                    : await VerifyCoreAsync(
                        lane: req.Lane ?? "default",
                        scope: scope,
                        windowTitleContains: title,
                        expectedTitleContains: title,
                        expectedTextContains: expectedText,
                        requireTargetVisible: req.RequireTargetVisible ?? !string.IsNullOrWhiteSpace(title),
                        includeChildren: req.IncludeChildren ?? true,
                        maxChildren: req.MaxChildren ?? 120,
                        verifyMode: normalizedOperation,
                        excludeInputLikeText: false).ConfigureAwait(false),
                "chat_message_sent" => string.IsNullOrWhiteSpace(title)
                    ? BuildError("UiRuntimeCommand", "verify.chat_message_sent 需要 targetWindowTitleContains。")
                    : string.IsNullOrWhiteSpace(expectedText)
                    ? BuildError("UiRuntimeCommand", "verify.chat_message_sent 需要 expectedTextContains。")
                    : await VerifyCoreAsync(
                        lane: req.Lane ?? "default",
                        scope: scope,
                        windowTitleContains: title,
                        expectedTitleContains: title,
                        expectedTextContains: expectedText,
                        requireTargetVisible: req.RequireTargetVisible ?? true,
                        includeChildren: req.IncludeChildren ?? true,
                        maxChildren: req.MaxChildren ?? 160,
                        verifyMode: normalizedOperation,
                        excludeInputLikeText: true).ConfigureAwait(false),
                _ => BuildError("UiRuntimeCommand", "不支持的 verify.operation。")
            };
        }

        private static async Task<string> VerifyCoreAsync(
            string lane,
            string scope,
            string? windowTitleContains,
            string? expectedTitleContains,
            string? expectedTextContains,
            bool requireTargetVisible,
            bool includeChildren,
            int maxChildren,
            string verifyMode,
            bool excludeInputLikeText)
        {
            var observe = await BuildObserveResultAsync(
                lane: lane,
                scope: scope,
                windowTitleContains: windowTitleContains,
                includeChildren: includeChildren,
                maxChildren: maxChildren,
                maxCandidates: 30,
                includeImages: false,
                detailLevel: ObserveDetailDecisionLight,
                goal: null,
                persistSession: false).ConfigureAwait(false);

            var expectedTitle = expectedTitleContains?.Trim() ?? string.Empty;
            var expectedText = expectedTextContains?.Trim() ?? string.Empty;
            var titleMatched = string.IsNullOrWhiteSpace(expectedTitle)
                || ContainsIgnoreCase(observe.TargetTitle, expectedTitle)
                || observe.WindowTitles.Any(x => ContainsIgnoreCase(x, expectedTitle));
            var matchedCandidates = string.IsNullOrWhiteSpace(expectedText)
                ? new List<UiRuntimeCandidate>()
                : observe.Candidates
                    .Where(x => !string.IsNullOrWhiteSpace(x.Text))
                    .Where(x => !excludeInputLikeText || !IsInputLikeVerifyCandidate(x))
                    .Where(x => ContainsIgnoreCase(x.Text, expectedText))
                    .ToList();
            var textMatched = string.IsNullOrWhiteSpace(expectedText) || matchedCandidates.Count > 0;
            var targetVisible = !requireTargetVisible || observe.TargetVisible;

            var result = new JObject
            {
                ["type"] = "ui.runtime.verify",
                ["ok"] = observe.Ok && titleMatched && textMatched && targetVisible,
                ["lane"] = observe.Lane,
                ["scope"] = observe.Scope,
                ["verifyMode"] = verifyMode,
                ["targetVisible"] = observe.TargetVisible,
                ["titleMatched"] = titleMatched,
                ["textMatched"] = textMatched,
                ["currentTitle"] = observe.TargetTitle ?? string.Empty,
                ["candidateCount"] = observe.Candidates.Count,
                ["matchedCandidateCount"] = matchedCandidates.Count,
                ["matchedCandidateBriefs"] = new JArray(matchedCandidates.Take(5).Select(BuildVerifyCandidateBrief)),
                ["expectedTitleContains"] = expectedTitle,
                ["expectedTextContains"] = expectedText,
                ["excludeInputLikeText"] = excludeInputLikeText
            };

            if (!observe.Ok && !string.IsNullOrWhiteSpace(observe.Message))
                result["message"] = observe.Message;

            return result.ToString(Formatting.None);
        }

        private static bool IsInputLikeVerifyCandidate(UiRuntimeCandidate candidate)
        {
            if (candidate == null)
                return false;

            var type = (candidate.Type ?? string.Empty).Trim().ToLowerInvariant();
            return type == "input" || type == "combobox" || candidate.SupportsSetText;
        }

        private static string BuildVerifyCandidateBrief(UiRuntimeCandidate candidate)
        {
            if (candidate == null)
                return string.Empty;

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(candidate.TargetId))
                parts.Add(candidate.TargetId);
            if (!string.IsNullOrWhiteSpace(candidate.Type))
                parts.Add("type=" + candidate.Type);
            if (!string.IsNullOrWhiteSpace(candidate.Text))
                parts.Add("text=" + SafeToken(candidate.Text));
            if (!string.IsNullOrWhiteSpace(candidate.WindowTitle))
                parts.Add("window=" + SafeToken(candidate.WindowTitle));
            return string.Join(" | ", parts);
        }

        private static async Task<UiRuntimeObserveResult> BuildObserveResultAsync(
            string lane,
            string scope,
            string? windowTitleContains,
            bool includeChildren,
            int maxChildren,
            int maxCandidates,
            bool includeImages,
            string detailLevel,
            string? goal,
            bool persistSession)
        {
            var laneKey = UiLaneScheduler.NormalizeLane(lane);
            var scopeKey = NormalizeScope(scope);
            var titleFilter = string.Equals(scopeKey, "foreground_window", StringComparison.Ordinal)
                ? null
                : windowTitleContains;

            var snapshotJson = await WindowsUiTools.UiSnapshot(
                lane: laneKey,
                windowTitleContains: titleFilter,
                includeChildren: includeChildren,
                maxChildren: maxChildren).ConfigureAwait(false);

            var snapshot = ParseJsonOrFallback(snapshotJson, "UiRuntimeObserve");
            var ok = snapshot.Value<bool?>("ok") ?? false;
            if (!ok)
            {
                return new UiRuntimeObserveResult
                {
                    Ok = false,
                    Lane = laneKey,
                    Scope = scopeKey,
                    Message = snapshot.Value<string>("message") ?? "UiSnapshot 失败。"
                };
            }

            var target = snapshot["target"] as JObject;
            var captureBounds = ResolveCaptureBounds(scopeKey, target);
            using var bitmap = CaptureBounds(captureBounds);

            var structuredCandidates = BuildStructuredCandidates(snapshot, target);

            var allCandidates = structuredCandidates
                .Where(x => x.Width > 0 && x.Height > 0)
                .OrderByDescending(x => x.Source.StartsWith("uia", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(x => x.Confidence)
                .Take(200)
                .ToList();

            AssignTargetIds(allCandidates);

            var topWindows = ReadTopWindows(snapshot);
            var foregroundWindow = topWindows.FirstOrDefault(x => x.IsForeground);
            var decisionTopWindows = BuildDecisionTopWindows(topWindows);
            var decisionCandidates = BuildDecisionCandidates(
                allCandidates,
                captureBounds,
                maxCandidates,
                goal);

            var imageBytes = includeImages ? BitmapToBytes(bitmap) : Array.Empty<byte>();
            var annotatedSource = string.Equals(detailLevel, ObserveDetailFull, StringComparison.OrdinalIgnoreCase)
                ? allCandidates.Take(80).ToList()
                : decisionCandidates;
            var annotatedBytes = includeImages ? BuildAnnotatedImage(bitmap, captureBounds, annotatedSource) : Array.Empty<byte>();
            var result = new UiRuntimeObserveResult
            {
                Ok = true,
                Lane = laneKey,
                Scope = scopeKey,
                DetailLevel = detailLevel,
                Goal = goal?.Trim() ?? string.Empty,
                SnapshotId = snapshot.Value<string>("snapshotId") ?? string.Empty,
                TargetVisible = target != null,
                TargetTitle = GetString(target, "Title"),
                TargetHwnd = GetLong(target, "Hwnd"),
                CaptureLeft = captureBounds.Left,
                CaptureTop = captureBounds.Top,
                CaptureWidth = captureBounds.Width,
                CaptureHeight = captureBounds.Height,
                ImageBase64 = imageBytes.Length == 0 ? string.Empty : Convert.ToBase64String(imageBytes),
                AnnotatedImageBase64 = annotatedBytes.Length == 0 ? string.Empty : Convert.ToBase64String(annotatedBytes),
                WindowTitles = ReadWindowTitles(snapshot),
                TopWindows = topWindows,
                Candidates = allCandidates,
                DecisionTopWindows = decisionTopWindows,
                DecisionWindowTitles = decisionTopWindows
                    .Select(x => x.Title)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                DecisionCandidates = decisionCandidates
            };

            result.ForegroundWindow = foregroundWindow;
            result.TopWindowBriefs = BuildTopWindowBriefs(result.DecisionTopWindows);
            result.CandidateBriefs = BuildCandidateBriefs(result.DecisionCandidates);
            result.DecisionContext = BuildDecisionContext(result);

            if (persistSession)
            {
                result.RuntimeId = UiRuntimeSessionStore.Create(new UiRuntimeSession
                {
                    Lane = result.Lane,
                    Scope = result.Scope,
                    SnapshotId = result.SnapshotId,
                    Candidates = allCandidates.ToDictionary(x => x.TargetId, x => x, StringComparer.OrdinalIgnoreCase)
                });
            }

            return result;
        }

        private static List<UiRuntimeCandidate> BuildStructuredCandidates(JObject snapshot, JObject? target)
        {
            var candidates = new List<UiRuntimeCandidate>();
            if (target != null)
            {
                var windowCandidate = new UiRuntimeCandidate
                {
                    RefId = target.Value<string>("refId") ?? "TARGET",
                    Hwnd = GetLong(target, "Hwnd"),
                    Text = GetString(target, "Title"),
                    Type = "window",
                    ClassName = GetString(target, "ClassName"),
                    ProcessName = GetString(target, "ProcessName"),
                    WindowTitle = GetString(target, "Title"),
                    Left = GetInt(target, "Left"),
                    Top = GetInt(target, "Top"),
                    Width = GetInt(target, "Width"),
                    Height = GetInt(target, "Height"),
                    Visible = true,
                    Enabled = true,
                    Source = "uia",
                    Confidence = 0.92,
                    Selector = BuildWindowSelector(target)
                };
                windowCandidate.AvailableActions = BuildAvailableActions(windowCandidate);
                windowCandidate.SelectorSummary = BuildSelectorSummary(windowCandidate);
                candidates.Add(windowCandidate);
            }

            if (snapshot["children"] is not JArray children)
                return candidates;

            foreach (var token in children.OfType<JObject>())
            {
                var width = GetInt(token, "Width");
                var height = GetInt(token, "Height");
                if (width <= 0 || height <= 0)
                    continue;

                var className = GetString(token, "ClassName");
                var rawText = GetString(token, "Title");
                var type = InferElementType(className, rawText);

                var candidate = new UiRuntimeCandidate
                {
                    RefId = token.Value<string>("refId") ?? string.Empty,
                    Hwnd = GetLong(token, "Hwnd"),
                    Text = ShouldSuppressStructuredText(type, className) ? string.Empty : rawText,
                    Type = type,
                    ClassName = className,
                    ProcessName = GetString(token, "ProcessName"),
                    WindowTitle = GetString(target, "Title"),
                    Left = GetInt(token, "Left"),
                    Top = GetInt(token, "Top"),
                    Width = width,
                    Height = height,
                    Visible = GetBool(token, "Visible", true),
                    Enabled = GetBool(token, "Enabled", true),
                    Source = "uia",
                    Confidence = 0.85,
                    Selector = token["selector"] is JObject selector ? (JObject)selector.DeepClone() : null
                };
                candidate.AvailableActions = BuildAvailableActions(candidate);
                candidate.SelectorSummary = BuildSelectorSummary(candidate);
                candidates.Add(candidate);
            }

            return candidates;
        }

        private static void AssignTargetIds(List<UiRuntimeCandidate> candidates)
        {
            for (var i = 0; i < candidates.Count; i++)
            {
                candidates[i].TargetId = "E" + (i + 1).ToString("000");
            }
        }

        private static byte[] BuildAnnotatedImage(Bitmap bitmap, Rectangle captureBounds, List<UiRuntimeCandidate> candidates)
        {
            using var copy = new Bitmap(bitmap);
            using var graphics = Graphics.FromImage(copy);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;

            using var penUia = new Pen(Color.FromArgb(45, 127, 249), 2f);
            using var font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold, GraphicsUnit.Pixel);
            using var textBrush = new SolidBrush(Color.White);
            using var labelBack = new SolidBrush(Color.FromArgb(220, 17, 24, 39));

            foreach (var item in candidates.Take(80))
            {
                var rect = new Rectangle(
                    item.Left - captureBounds.Left,
                    item.Top - captureBounds.Top,
                    item.Width,
                    item.Height);
                if (rect.Width <= 0 || rect.Height <= 0)
                    continue;

                graphics.DrawRectangle(penUia, rect);

                var label = item.TargetId + " " + Truncate(item.Text, 10);
                var size = graphics.MeasureString(label, font);
                var labelRect = new RectangleF(
                    rect.Left,
                    Math.Max(0, rect.Top - size.Height - 2),
                    size.Width + 8,
                    size.Height + 4);
                graphics.FillRectangle(labelBack, labelRect);
                graphics.DrawString(label, font, textBrush, labelRect.Left + 4, labelRect.Top + 2);
            }

            return BitmapToBytes(copy);
        }

        private static Rectangle ResolveCaptureBounds(string scope, JObject? target)
        {
            if (string.Equals(scope, "full_screen", StringComparison.OrdinalIgnoreCase))
                return SystemInformation.VirtualScreen;

            var width = GetInt(target, "Width");
            var height = GetInt(target, "Height");
            if (width > 0 && height > 0)
            {
                return new Rectangle(
                    GetInt(target, "Left"),
                    GetInt(target, "Top"),
                    width,
                    height);
            }

            return SystemInformation.VirtualScreen;
        }

        private static Bitmap CaptureBounds(Rectangle bounds)
        {
            var width = Math.Max(1, bounds.Width);
            var height = Math.Max(1, bounds.Height);
            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
            return bitmap;
        }

        private static byte[] BitmapToBytes(Bitmap bitmap)
        {
            using var ms = new MemoryStream();
            bitmap.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }

        private static string NormalizeScope(string? scope)
        {
            var text = (scope ?? string.Empty).Trim().ToLowerInvariant();
            return text switch
            {
                "full_screen" => "full_screen",
                "window_title" => "window_title",
                _ => "foreground_window"
            };
        }

        private static string NormalizeObserveDetailLevel(string? detailLevel)
        {
            var text = (detailLevel ?? string.Empty).Trim().ToLowerInvariant();
            return text switch
            {
                "" => ObserveDetailDecisionLight,
                "light" => ObserveDetailDecisionLight,
                "compact" => ObserveDetailDecisionLight,
                "decision" => ObserveDetailDecisionLight,
                "decision_light" => ObserveDetailDecisionLight,
                "default" => ObserveDetailDecisionLight,
                "full" => ObserveDetailFull,
                "debug" => ObserveDetailFull,
                "verbose" => ObserveDetailFull,
                _ => ObserveDetailDecisionLight
            };
        }

        private static List<UiRuntimeTopWindow> BuildDecisionTopWindows(IEnumerable<UiRuntimeTopWindow> windows)
        {
            var preferred = windows?
                .Where(x => x != null)
                .Where(x => x.IsForeground
                    || (!x.IsShellWindow
                        && x.Visible
                        && x.Width >= 240
                        && x.Height >= 120
                        && !IsNoiseWindow(x.Title, x.ProcessName, x.ClassName)))
                .Take(6)
                .ToList() ?? new List<UiRuntimeTopWindow>();

            if (preferred.Count > 0)
                return preferred;

            return windows?
                .Where(x => x != null)
                .Take(4)
                .ToList() ?? new List<UiRuntimeTopWindow>();
        }

        private static List<UiRuntimeCandidate> BuildDecisionCandidates(
            IEnumerable<UiRuntimeCandidate> candidates,
            Rectangle captureBounds,
            int maxCandidates,
            string? goal)
        {
            var limit = Math.Clamp(maxCandidates, 6, 40);
            var goalHint = (goal ?? string.Empty).Trim();

            var ranked = candidates?
                .Where(IsDecisionCandidate)
                .Select(candidate => new
                {
                    Candidate = candidate,
                    Score = ComputeDecisionScore(candidate, captureBounds, goalHint)
                })
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Candidate.Confidence)
                .ThenBy(x => x.Candidate.TargetId, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Candidate)
                .Take(limit)
                .ToList() ?? new List<UiRuntimeCandidate>();

            if (ranked.Count > 0)
                return ranked;

            return candidates?
                .Where(x => x != null && x.Width > 0 && x.Height > 0)
                .Take(limit)
                .ToList() ?? new List<UiRuntimeCandidate>();
        }

        private static UiRuntimeDecisionContext BuildDecisionContext(UiRuntimeObserveResult observe)
        {
            var stableCount = observe.DecisionCandidates.Count(x => x.StableTarget);
            var actionableCount = observe.DecisionCandidates.Count(x => x.AvailableActions.Count > 0);
            var inputCount = observe.DecisionCandidates.Count(IsInputLikeCandidate);
            var focusWindowTitle = observe.ForegroundWindow?.Title ?? observe.TargetTitle ?? string.Empty;
            var focusProcessName = observe.ForegroundWindow?.ProcessName ?? string.Empty;
            var scene = InferDecisionScene(observe, inputCount);

            return new UiRuntimeDecisionContext
            {
                Scene = scene,
                FocusWindowTitle = focusWindowTitle,
                FocusProcessName = focusProcessName,
                PreferredStrategy = InferPreferredStrategy(observe, stableCount, inputCount),
                StableCandidateCount = stableCount,
                ActionableCandidateCount = actionableCount,
                InputCandidateCount = inputCount,
                RecommendedCommandTypes = BuildRecommendedCommandTypes(scene, focusWindowTitle, stableCount, inputCount),
                Notes = BuildDecisionNotes(observe, stableCount, inputCount),
                TopCandidateBriefs = observe.DecisionCandidates
                    .Take(5)
                    .Select(BuildCandidateBrief)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToList()
            };
        }

        private static string InferDecisionScene(UiRuntimeObserveResult observe, int inputCount)
        {
            if (observe.ForegroundWindow == null && !observe.TargetVisible)
                return "no_visible_window";

            if (IsNoiseWindow(
                observe.ForegroundWindow?.Title,
                observe.ForegroundWindow?.ProcessName,
                observe.ForegroundWindow?.ClassName))
            {
                return "launcher_surface";
            }

            if (inputCount > 0 || observe.DecisionCandidates.Any(x => x.AvailableActions.Count > 0 && !string.Equals(x.Type, "window", StringComparison.OrdinalIgnoreCase)))
                return "interactive_surface";

            if (observe.TargetVisible)
                return "window_visible";

            return "uncertain";
        }

        private static string InferPreferredStrategy(UiRuntimeObserveResult observe, int stableCount, int inputCount)
        {
            if (observe.DecisionCandidates.Any(x =>
                    x.StableTarget
                    && !string.Equals(x.Type, "window", StringComparison.OrdinalIgnoreCase)
                    && x.AvailableActions.Count > 0))
            {
                return "execute_first";
            }

            if (inputCount > 0)
                return "execute_or_keyboard";

            if (!string.IsNullOrWhiteSpace(observe.ForegroundWindow?.Title ?? observe.TargetTitle))
                return "window_then_keyboard";

            return stableCount > 0 ? "execute_or_window" : "shell_or_window";
        }

        private static List<string> BuildRecommendedCommandTypes(string scene, string focusWindowTitle, int stableCount, int inputCount)
        {
            var types = new List<string>();
            if (stableCount > 0)
                types.Add("execute");
            if (!string.IsNullOrWhiteSpace(focusWindowTitle))
                types.Add("window");
            if (inputCount > 0 || !string.IsNullOrWhiteSpace(focusWindowTitle))
                types.Add("keyboard");
            if (stableCount == 0 || string.Equals(scene, "launcher_surface", StringComparison.OrdinalIgnoreCase) || string.Equals(scene, "no_visible_window", StringComparison.OrdinalIgnoreCase))
                types.Add("shell");

            return types
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<string> BuildDecisionNotes(UiRuntimeObserveResult observe, int stableCount, int inputCount)
        {
            var notes = new List<string>();

            if (stableCount > 0)
                notes.Add($"当前已有 {stableCount} 个稳定候选，优先直接从 candidates 里选择 targetId。");
            else
                notes.Add("当前没有足够稳定的 grounded 候选，优先考虑 shell 或 window 级动作。");

            if (inputCount == 0 && !string.IsNullOrWhiteSpace(observe.ForegroundWindow?.Title ?? observe.TargetTitle))
            {
                notes.Add("未发现 supportsSetText=true 的稳定输入控件；若已知窗口标题，可退回 window.activate + keyboard.focus_type_text。");
            }

            return notes;
        }

        private static bool IsDecisionCandidate(UiRuntimeCandidate candidate)
        {
            if (candidate == null || !candidate.Visible || !candidate.Enabled || candidate.Width <= 0 || candidate.Height <= 0)
                return false;

            var type = (candidate.Type ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(type))
                return false;

            if ((type == "label" || type == "text" || type == "control")
                && !candidate.StableTarget
                && !LooksActionableText(candidate.Text))
            {
                return false;
            }

            if (LooksVerboseText(candidate.Text) && !candidate.SupportsSetText && type != "window")
                return false;

            return true;
        }

        private static double ComputeDecisionScore(UiRuntimeCandidate candidate, Rectangle captureBounds, string goalHint)
        {
            var type = (candidate.Type ?? string.Empty).Trim().ToLowerInvariant();
            var score = candidate.Confidence;

            if (candidate.StableTarget)
                score += 1.2;
            if (candidate.AvailableActions.Count > 0)
                score += 0.35;
            if (candidate.SupportsSetText)
                score += 0.85;

            score += type switch
            {
                "input" => 0.7,
                "combobox" => 0.7,
                "button" => 0.55,
                "menu" => 0.55,
                "checkbox" => 0.45,
                "radio" => 0.45,
                "list" => 0.4,
                "tree" => 0.4,
                "window" => 0.25,
                "label" => -0.15,
                "text" => -0.15,
                "control" => -0.2,
                _ => 0
            };

            if (LooksActionableText(candidate.Text))
                score += 0.18;
            if (LooksVerboseText(candidate.Text))
                score -= 0.45;

            var noisySurface = IsNoiseWindow(candidate.WindowTitle, candidate.ProcessName, candidate.ClassName);
            if (noisySurface)
                score -= 0.2;

            if ((type == "label" || type == "text" || type == "control")
                && candidate.Width >= Math.Max(900, (int)Math.Round(captureBounds.Width * 0.65)))
            {
                score -= 0.45;
            }

            if (!string.IsNullOrWhiteSpace(goalHint)
                && goalHint.Length <= 32
                && (ContainsIgnoreCase(candidate.Text, goalHint)
                    || ContainsIgnoreCase(candidate.WindowTitle, goalHint)))
            {
                score += 0.3;
            }

            return score;
        }

        private static bool IsInputLikeCandidate(UiRuntimeCandidate candidate)
        {
            if (candidate == null)
                return false;

            return candidate.SupportsSetText
                || string.Equals(candidate.Type, "input", StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.Type, "combobox", StringComparison.OrdinalIgnoreCase);
        }

        private static bool LooksActionableText(string? text)
        {
            var value = (text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value))
                return false;
            if (value.Length > 24)
                return false;
            if (LooksTimestampLike(value))
                return false;
            if (value.Contains("：", StringComparison.Ordinal) && value.Length > 12)
                return false;
            if (value.Contains("http://", StringComparison.OrdinalIgnoreCase) || value.Contains("https://", StringComparison.OrdinalIgnoreCase))
                return true;
            if (value.Count(char.IsWhiteSpace) > 2)
                return false;

            var punctuationCount = value.Count(ch => ch is '，' or '。' or '；' or ',' or ';' or ':' or '：');
            return punctuationCount <= 1;
        }

        private static bool LooksVerboseText(string? text)
        {
            var value = (text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value))
                return false;
            if (value.Length > 36)
                return true;
            if (value.Contains("workflow", StringComparison.OrdinalIgnoreCase) && value.Length > 20)
                return true;
            if ((value.Contains("http://", StringComparison.OrdinalIgnoreCase) || value.Contains("https://", StringComparison.OrdinalIgnoreCase))
                && value.Length > 24)
            {
                return true;
            }

            return value.Count(ch => ch is '，' or '。' or '；' or ',' or ';' or ':' or '：') >= 2;
        }

        private static bool LooksTimestampLike(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var digits = text.Count(char.IsDigit);
            if (digits < 4)
                return false;

            return text.Contains(':')
                || text.Contains('-')
                || text.Contains('/');
        }

        private static bool IsNoiseWindow(string? title, string? processName, string? className)
        {
            var normalizedTitle = (title ?? string.Empty).Trim();
            var normalizedProcess = (processName ?? string.Empty).Trim();
            var normalizedClass = (className ?? string.Empty).Trim();

            if (string.Equals(normalizedTitle, "Program Manager", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalizedTitle, "任务栏", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalizedTitle, "Windows 输入体验", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(normalizedProcess, "TextInputHost", StringComparison.OrdinalIgnoreCase))
                return true;

            return string.Equals(normalizedClass, "Progman", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalizedClass, "WorkerW", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalizedClass, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase);
        }

        private static string InferElementType(string? className, string? title)
        {
            var cls = (className ?? string.Empty).Trim().ToLowerInvariant();
            var text = (title ?? string.Empty).Trim();

            if (cls.Contains("button"))
                return "button";
            if (cls.Contains("edit") || cls.Contains("textbox"))
                return "input";
            if (cls.Contains("checkbox"))
                return "checkbox";
            if (cls.Contains("radiobutton"))
                return "radio";
            if (cls.Contains("combobox"))
                return "combobox";
            if (cls.Contains("listview") || cls.Contains("listbox"))
                return "list";
            if (cls.Contains("treeview"))
                return "tree";
            if (cls.Contains("menu"))
                return "menu";
            if (cls.Contains("window"))
                return "window";
            if (text.EndsWith("...", StringComparison.Ordinal) || text.Contains("浏览", StringComparison.Ordinal))
                return "button";
            return string.IsNullOrWhiteSpace(text) ? "control" : "label";
        }

        private static bool ShouldSuppressStructuredText(string? type, string? className)
        {
            var normalizedClass = (className ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(normalizedClass))
                return false;

            if (normalizedClass.Contains("notepadtextbox") ||
                normalizedClass.Contains("richedit") ||
                normalizedClass.Contains("scintilla"))
            {
                return true;
            }

            return false;
        }

        private static List<Point> ReadPoints(object? pointsObject)
        {
            var output = new List<Point>();
            if (pointsObject is not IEnumerable points)
                return output;

            foreach (var item in points)
            {
                if (item == null)
                    continue;

                var type = item.GetType();
                var x = type.GetProperty("X")?.GetValue(item);
                var y = type.GetProperty("Y")?.GetValue(item);
                if (x == null || y == null)
                    continue;

                output.Add(new Point(Convert.ToInt32(x), Convert.ToInt32(y)));
            }

            return output;
        }

        private static List<string> ReadWindowTitles(JObject snapshot)
        {
            var output = new List<string>();
            if (snapshot["windows"] is not JArray windows)
                return output;

            foreach (var item in windows.OfType<JObject>())
            {
                var title = GetString(item, "Title");
                if (!string.IsNullOrWhiteSpace(title))
                    output.Add(title);
            }

            return output;
        }

        private static List<UiRuntimeTopWindow> ReadTopWindows(JObject snapshot)
        {
            var output = new List<UiRuntimeTopWindow>();
            if (snapshot["windows"] is not JArray windows)
                return output;

            foreach (var item in windows.OfType<JObject>())
            {
                output.Add(new UiRuntimeTopWindow
                {
                    WindowId = item.Value<string>("refId") ?? string.Empty,
                    Hwnd = GetLong(item, "Hwnd"),
                    Title = GetString(item, "Title"),
                    ClassName = GetString(item, "ClassName"),
                    ProcessName = GetString(item, "ProcessName"),
                    Left = GetInt(item, "Left"),
                    Top = GetInt(item, "Top"),
                    Width = GetInt(item, "Width"),
                    Height = GetInt(item, "Height"),
                    Visible = GetBool(item, "Visible", true),
                    Enabled = GetBool(item, "Enabled", true),
                    IsForeground = GetBool(item, "isForeground", false),
                    IsShellWindow = IsShellWindow(GetString(item, "Title"), GetString(item, "ClassName"))
                });
            }

            return output;
        }

        private static bool IsShellWindow(string title, string className)
        {
            if (string.Equals(className, "Progman", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(className, "WorkerW", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(className, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return string.Equals(title, "Program Manager", StringComparison.OrdinalIgnoreCase);
        }

        private static List<string> BuildTopWindowBriefs(IEnumerable<UiRuntimeTopWindow> windows)
        {
            var output = new List<string>();
            if (windows == null)
                return output;

            foreach (var window in windows.Where(x => x != null).Take(12))
            {
                var parts = new List<string> { SafeToken(window.WindowId) };
                if (window.IsForeground)
                    parts.Add("fg");
                if (!string.IsNullOrWhiteSpace(window.ProcessName))
                    parts.Add("proc=" + SafeToken(window.ProcessName));
                if (!string.IsNullOrWhiteSpace(window.ClassName))
                    parts.Add("class=" + SafeToken(window.ClassName));
                if (!string.IsNullOrWhiteSpace(window.Title))
                    parts.Add("title=" + SafeToken(window.Title));

                output.Add(string.Join(" | ", parts));
            }

            return output;
        }

        private static List<string> BuildCandidateBriefs(IEnumerable<UiRuntimeCandidate> candidates)
        {
            var output = new List<string>();
            if (candidates == null)
                return output;

            foreach (var candidate in candidates.Where(x => x != null).Take(30))
            {
                output.Add(BuildCandidateBrief(candidate));
            }

            return output;
        }

        private static string BuildCandidateBrief(UiRuntimeCandidate candidate)
        {
            if (candidate == null)
                return string.Empty;

            var parts = new List<string> { SafeToken(candidate.TargetId) };
            if (!string.IsNullOrWhiteSpace(candidate.Type))
                parts.Add("type=" + SafeToken(candidate.Type));
            if (!string.IsNullOrWhiteSpace(candidate.Text))
                parts.Add("text=" + SafeToken(candidate.Text));
            if (!string.IsNullOrWhiteSpace(candidate.ProcessName))
                parts.Add("proc=" + SafeToken(candidate.ProcessName));
            if (!string.IsNullOrWhiteSpace(candidate.ClassName))
                parts.Add("class=" + SafeToken(candidate.ClassName));
            if (!string.IsNullOrWhiteSpace(candidate.RefId))
                parts.Add("ref=" + SafeToken(candidate.RefId));
            if (candidate.StableTarget)
                parts.Add("stable=true");
            if (candidate.AvailableActions.Count > 0)
                parts.Add("actions=" + string.Join("/", candidate.AvailableActions));
            if (!string.IsNullOrWhiteSpace(candidate.WindowTitle))
                parts.Add("window=" + SafeToken(candidate.WindowTitle));
            parts.Add("score=" + Math.Round(candidate.Confidence, 2).ToString("0.##"));
            return string.Join(" | ", parts);
        }

        private static List<string> BuildAvailableActions(UiRuntimeCandidate candidate)
        {
            var actions = new List<string>();
            if (candidate == null || !candidate.StableTarget || !candidate.Visible || !candidate.Enabled)
                return actions;

            var type = (candidate.Type ?? string.Empty).Trim().ToLowerInvariant();
            switch (type)
            {
                case "window":
                    actions.Add("activate");
                    actions.Add("click");
                    actions.Add("double_click");
                    actions.Add("right_click");
                    actions.Add("close");
                    break;
                case "input":
                case "combobox":
                    actions.Add("click");
                    if (candidate.SupportsSetText)
                        actions.Add("set_text");
                    actions.Add("right_click");
                    break;
                case "button":
                case "checkbox":
                case "radio":
                case "menu":
                    actions.Add("click");
                    actions.Add("right_click");
                    break;
                default:
                    actions.Add("click");
                    actions.Add("double_click");
                    actions.Add("right_click");
                    if (candidate.SupportsSetText)
                        actions.Add("set_text");
                    break;
            }

            return actions.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string BuildSelectorSummary(UiRuntimeCandidate candidate)
        {
            if (candidate == null)
                return string.Empty;

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(candidate.WindowTitle))
                parts.Add("window=" + SafeToken(candidate.WindowTitle));
            if (!string.IsNullOrWhiteSpace(candidate.ProcessName))
                parts.Add("proc=" + SafeToken(candidate.ProcessName));
            if (!string.IsNullOrWhiteSpace(candidate.ClassName))
                parts.Add("class=" + SafeToken(candidate.ClassName));
            if (!string.IsNullOrWhiteSpace(candidate.RefId))
                parts.Add("ref=" + SafeToken(candidate.RefId));
            return string.Join(" | ", parts);
        }

        private static JObject BuildWindowSelector(JObject target)
        {
            return new JObject
            {
                ["titleContains"] = GetString(target, "Title"),
                ["className"] = GetString(target, "ClassName"),
                ["processName"] = GetString(target, "ProcessName"),
                ["windowIndex"] = 0
            };
        }

        private static string SafeToken(string? value, int maxLength = 40)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var normalized = value
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim();

            return normalized.Length <= maxLength
                ? normalized
                : normalized[..Math.Max(0, maxLength - 3)] + "...";
        }

        private static JObject ParseJsonOrFallback(string text, string where)
        {
            try
            {
                return JObject.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
            }
            catch
            {
                return new JObject
                {
                    ["type"] = "error",
                    ["where"] = where,
                    ["ok"] = false,
                    ["message"] = "返回体不是有效 JSON。",
                    ["raw"] = text ?? string.Empty
                };
            }
        }

        private static string BuildError(string where, string message)
        {
            return new JObject
            {
                ["type"] = "error",
                ["where"] = where,
                ["ok"] = false,
                ["message"] = message
            }.ToString(Formatting.None);
        }

        private static int GetInt(JObject? jo, string name)
        {
            if (jo == null)
                return 0;
            return jo.Value<int?>(name)
                ?? jo.Value<int?>(ToCamel(name))
                ?? 0;
        }

        private static long GetLong(JObject? jo, string name)
        {
            if (jo == null)
                return 0;
            return jo.Value<long?>(name)
                ?? jo.Value<long?>(ToCamel(name))
                ?? 0;
        }

        private static bool GetBool(JObject? jo, string name, bool fallback)
        {
            if (jo == null)
                return fallback;
            return jo.Value<bool?>(name)
                ?? jo.Value<bool?>(ToCamel(name))
                ?? fallback;
        }

        private static string GetString(JObject? jo, string name)
        {
            if (jo == null)
                return string.Empty;
            return jo.Value<string>(name)
                ?? jo.Value<string>(ToCamel(name))
                ?? string.Empty;
        }

        private static string ToCamel(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return string.Empty;
            return char.ToLowerInvariant(name[0]) + name[1..];
        }

        private static bool ContainsIgnoreCase(string? text, string value)
        {
            return !string.IsNullOrWhiteSpace(text)
                && text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static double ComputeIou(int ax, int ay, int aw, int ah, int bx, int by, int bw, int bh)
        {
            var left = Math.Max(ax, bx);
            var top = Math.Max(ay, by);
            var right = Math.Min(ax + aw, bx + bw);
            var bottom = Math.Min(ay + ah, by + bh);
            var width = Math.Max(0, right - left);
            var height = Math.Max(0, bottom - top);
            if (width == 0 || height == 0)
                return 0;

            var inter = width * height;
            var union = aw * ah + bw * bh - inter;
            if (union <= 0)
                return 0;
            return (double)inter / union;
        }

        private static string Truncate(string? text, int length)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;
            return text.Length <= length ? text : text[..length];
        }

        private static string FirstNonEmpty(params string?[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return string.Empty;
        }
    }

    internal sealed class UiRuntimeObserveResult
    {
        public bool Ok { get; set; }
        public string RuntimeId { get; set; } = string.Empty;
        public string SnapshotId { get; set; } = string.Empty;
        public string Lane { get; set; } = "default";
        public string Scope { get; set; } = "foreground_window";
        public string DetailLevel { get; set; } = "decision_light";
        public string Goal { get; set; } = string.Empty;
        public bool TargetVisible { get; set; }
        public string TargetTitle { get; set; } = string.Empty;
        public long TargetHwnd { get; set; }
        public int CaptureLeft { get; set; }
        public int CaptureTop { get; set; }
        public int CaptureWidth { get; set; }
        public int CaptureHeight { get; set; }
        public string ImageBase64 { get; set; } = string.Empty;
        public string AnnotatedImageBase64 { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public List<string> WindowTitles { get; set; } = new();
        public List<UiRuntimeTopWindow> TopWindows { get; set; } = new();
        public List<UiRuntimeTopWindow> DecisionTopWindows { get; set; } = new();
        public List<string> DecisionWindowTitles { get; set; } = new();
        public UiRuntimeTopWindow? ForegroundWindow { get; set; }
        public List<string> TopWindowBriefs { get; set; } = new();
        public List<UiRuntimeCandidate> Candidates { get; set; } = new();
        public List<UiRuntimeCandidate> DecisionCandidates { get; set; } = new();
        public List<string> CandidateBriefs { get; set; } = new();
        public UiRuntimeDecisionContext? DecisionContext { get; set; }

        public string ToJson()
        {
            var isFull = string.Equals(DetailLevel, "full", StringComparison.OrdinalIgnoreCase);
            var visibleCandidates = isFull ? Candidates : DecisionCandidates;
            var visibleTopWindows = isFull ? TopWindows : DecisionTopWindows;
            var visibleWindowTitles = isFull ? WindowTitles : DecisionWindowTitles;

            var payload = new JObject
            {
                ["type"] = "ui.runtime.observe",
                ["ok"] = Ok,
                ["runtimeId"] = RuntimeId,
                ["snapshotId"] = SnapshotId,
                ["lane"] = Lane,
                ["scope"] = Scope,
                ["detailLevel"] = DetailLevel,
                ["targetVisible"] = TargetVisible,
                ["targetTitle"] = TargetTitle,
                ["targetHwnd"] = TargetHwnd,
                ["capture"] = new JObject
                {
                    ["left"] = CaptureLeft,
                    ["top"] = CaptureTop,
                    ["width"] = CaptureWidth,
                    ["height"] = CaptureHeight
                },
                ["candidateCount"] = visibleCandidates.Count,
                ["totalCandidateCount"] = Candidates.Count,
                ["windowTitles"] = new JArray(visibleWindowTitles),
                ["topWindows"] = new JArray(visibleTopWindows.Select(x => x.ToJObject())),
                ["topWindowBriefs"] = new JArray(TopWindowBriefs),
                ["candidates"] = new JArray(visibleCandidates.Select(x => x.ToJObject()))
            };

            if (ForegroundWindow != null)
                payload["foregroundWindow"] = ForegroundWindow.ToJObject();
            if (CandidateBriefs.Count > 0)
                payload["candidateBriefs"] = new JArray(CandidateBriefs);
            if (DecisionContext != null)
                payload["decisionContext"] = DecisionContext.ToJObject();
            if (!string.IsNullOrWhiteSpace(Goal))
                payload["goal"] = Goal;

            if (!string.IsNullOrWhiteSpace(ImageBase64))
                payload["imageBase64"] = ImageBase64;
            if (!string.IsNullOrWhiteSpace(AnnotatedImageBase64))
                payload["annotatedImageBase64"] = AnnotatedImageBase64;
            if (!string.IsNullOrWhiteSpace(Message))
                payload["message"] = Message;

            return payload.ToString(Formatting.None);
        }
    }

    internal sealed class UiRuntimeDecisionContext
    {
        public string Scene { get; set; } = string.Empty;
        public string FocusWindowTitle { get; set; } = string.Empty;
        public string FocusProcessName { get; set; } = string.Empty;
        public string PreferredStrategy { get; set; } = string.Empty;
        public int StableCandidateCount { get; set; }
        public int ActionableCandidateCount { get; set; }
        public int InputCandidateCount { get; set; }
        public List<string> RecommendedCommandTypes { get; set; } = new();
        public List<string> Notes { get; set; } = new();
        public List<string> TopCandidateBriefs { get; set; } = new();

        public JObject ToJObject()
        {
            return new JObject
            {
                ["scene"] = Scene,
                ["focusWindowTitle"] = FocusWindowTitle,
                ["focusProcessName"] = FocusProcessName,
                ["preferredStrategy"] = PreferredStrategy,
                ["stableCandidateCount"] = StableCandidateCount,
                ["actionableCandidateCount"] = ActionableCandidateCount,
                ["inputCandidateCount"] = InputCandidateCount,
                ["recommendedCommandTypes"] = new JArray(RecommendedCommandTypes),
                ["notes"] = new JArray(Notes),
                ["topCandidateBriefs"] = new JArray(TopCandidateBriefs)
            };
        }
    }

    internal sealed class UiRuntimeCaptureResult
    {
        public bool Ok { get; set; }
        public string ErrorJson { get; set; } = string.Empty;
        public string Lane { get; set; } = "default";
        public string Scope { get; set; } = "foreground_window";
        public string TargetTitle { get; set; } = string.Empty;
        public int CaptureLeft { get; set; }
        public int CaptureTop { get; set; }
        public int CaptureWidth { get; set; }
        public int CaptureHeight { get; set; }
        public byte[] ImageBytes { get; set; } = Array.Empty<byte>();
        public string FileName { get; set; } = "capture.png";
    }

    internal sealed class UiRuntimeCandidate
    {
        public string TargetId { get; set; } = string.Empty;
        public string RefId { get; set; } = string.Empty;
        public long Hwnd { get; set; }
        public string Type { get; set; } = "control";
        public string Text { get; set; } = string.Empty;
        public string ClassName { get; set; } = string.Empty;
        public string ProcessName { get; set; } = string.Empty;
        public string WindowTitle { get; set; } = string.Empty;
        public int Left { get; set; }
        public int Top { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool Enabled { get; set; } = true;
        public bool Visible { get; set; } = true;
        public string Source { get; set; } = "uia";
        public double Confidence { get; set; } = 0.5;
        public JObject? Selector { get; set; }
        public string SelectorSummary { get; set; } = string.Empty;
        public List<string> AvailableActions { get; set; } = new();
        public int CenterX => Left + Math.Max(1, Width) / 2;
        public int CenterY => Top + Math.Max(1, Height) / 2;
        public bool StableTarget => !string.IsNullOrWhiteSpace(RefId) || Selector != null || Hwnd != 0;
        public bool SupportsSetText => IsLikelyDirectSetTextTarget(Type, ClassName);

        public JObject ToJObject()
        {
            var payload = new JObject
            {
                ["targetId"] = TargetId,
                ["refId"] = RefId ?? string.Empty,
                ["hwnd"] = Hwnd,
                ["type"] = Type,
                ["text"] = Text ?? string.Empty,
                ["className"] = ClassName ?? string.Empty,
                ["processName"] = ProcessName ?? string.Empty,
                ["windowTitle"] = WindowTitle ?? string.Empty,
                ["bbox"] = new JArray(Left, Top, Left + Width, Top + Height),
                ["left"] = Left,
                ["top"] = Top,
                ["width"] = Width,
                ["height"] = Height,
                ["centerX"] = CenterX,
                ["centerY"] = CenterY,
                ["enabled"] = Enabled,
                ["visible"] = Visible,
                ["source"] = Source,
                ["confidence"] = Math.Round(Confidence, 4),
                ["stableTarget"] = StableTarget,
                ["supportsSetText"] = SupportsSetText,
                ["availableActions"] = new JArray(AvailableActions),
                ["selectorSummary"] = SelectorSummary ?? string.Empty
            };
            if (Selector != null)
                payload["selector"] = Selector.DeepClone();
            payload["summary"] = BuildSummary();
            return payload;
        }

        private string BuildSummary()
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(TargetId))
                parts.Add(TrimToken(TargetId, 12));
            if (!string.IsNullOrWhiteSpace(Type))
                parts.Add("type=" + TrimToken(Type, 20));
            if (!string.IsNullOrWhiteSpace(Text))
                parts.Add("text=" + TrimToken(Text, 40));
            if (!string.IsNullOrWhiteSpace(ProcessName))
                parts.Add("proc=" + TrimToken(ProcessName, 24));
            if (!string.IsNullOrWhiteSpace(ClassName))
                parts.Add("class=" + TrimToken(ClassName, 24));
            if (!string.IsNullOrWhiteSpace(RefId))
                parts.Add("ref=" + TrimToken(RefId, 20));
            if (StableTarget)
                parts.Add("stable=true");
            if (AvailableActions.Count > 0)
                parts.Add("actions=" + string.Join("/", AvailableActions));
            if (!string.IsNullOrWhiteSpace(WindowTitle))
                parts.Add("window=" + TrimToken(WindowTitle, 40));
            parts.Add("score=" + Math.Round(Confidence, 2).ToString("0.##"));
            return string.Join(" | ", parts);
        }

        private static string TrimToken(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var normalized = value
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim();

            return normalized.Length <= maxLength
                ? normalized
                : normalized[..Math.Max(0, maxLength - 3)] + "...";
        }

        private static bool IsLikelyDirectSetTextTarget(string? type, string? className)
        {
            var normalizedType = (type ?? string.Empty).Trim().ToLowerInvariant();
            if (normalizedType != "input" && normalizedType != "combobox")
                return false;

            var normalizedClass = (className ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(normalizedClass))
                return false;

            if (normalizedClass.Contains("notepadtextbox") ||
                normalizedClass.Contains("richedit") ||
                normalizedClass.Contains("scintilla"))
            {
                return false;
            }

            if (normalizedClass.Contains("edit"))
                return true;

            return normalizedType == "combobox" && normalizedClass.Contains("combobox");
        }
    }

    internal sealed class UiRuntimeTopWindow
    {
        public string WindowId { get; set; } = string.Empty;
        public long Hwnd { get; set; }
        public string Title { get; set; } = string.Empty;
        public string ClassName { get; set; } = string.Empty;
        public string ProcessName { get; set; } = string.Empty;
        public int Left { get; set; }
        public int Top { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool Visible { get; set; } = true;
        public bool Enabled { get; set; } = true;
        public bool IsForeground { get; set; }
        public bool IsShellWindow { get; set; }

        public JObject ToJObject()
        {
            return new JObject
            {
                ["windowId"] = WindowId,
                ["hwnd"] = Hwnd,
                ["title"] = Title,
                ["className"] = ClassName,
                ["processName"] = ProcessName,
                ["left"] = Left,
                ["top"] = Top,
                ["width"] = Width,
                ["height"] = Height,
                ["visible"] = Visible,
                ["enabled"] = Enabled,
                ["isForeground"] = IsForeground,
                ["isShellWindow"] = IsShellWindow
            };
        }
    }

    internal sealed class UiRuntimeSession
    {
        public string RuntimeId { get; set; } = string.Empty;
        public string SnapshotId { get; set; } = string.Empty;
        public string Lane { get; set; } = "default";
        public string Scope { get; set; } = "foreground_window";
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public Dictionary<string, UiRuntimeCandidate> Candidates { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    internal static class UiRuntimeSessionStore
    {
        private static readonly ConcurrentDictionary<string, UiRuntimeSession> s_sessions =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan SessionTtl = TimeSpan.FromMinutes(20);
        private static int s_sequence;

        public static string Create(UiRuntimeSession session)
        {
            CleanupExpired();
            var runtimeId = $"rt-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Interlocked.Increment(ref s_sequence)}";
            session.RuntimeId = runtimeId;
            session.CreatedAtUtc = DateTime.UtcNow;
            s_sessions[runtimeId] = session;
            return runtimeId;
        }

        public static bool TryGet(string runtimeId, out UiRuntimeSession session)
        {
            CleanupExpired();
            if (string.IsNullOrWhiteSpace(runtimeId))
            {
                session = null!;
                return false;
            }

            return s_sessions.TryGetValue(runtimeId.Trim(), out session!);
        }

        private static void CleanupExpired()
        {
            var now = DateTime.UtcNow;
            foreach (var pair in s_sessions)
            {
                if ((now - pair.Value.CreatedAtUtc) > SessionTtl)
                    s_sessions.TryRemove(pair.Key, out _);
            }
        }
    }
}

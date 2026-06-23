using McpServer;
using ModelContextProtocol.Server;
using Microsoft.Win32;
using Newtonsoft.Json;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace EW_Assistant.McpTools
{
    [McpServerToolType]
    public static class WindowsUiTools
    {
        private const uint WM_SETTEXT = 0x000C;
        private const uint WM_GETTEXT = 0x000D;
        private const uint WM_GETTEXTLENGTH = 0x000E;
        private const uint WM_CLOSE = 0x0010;
        private const uint EM_REPLACESEL = 0x00C2;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_UNICODE = 0x0004;
        private const uint INPUT_KEYBOARD = 1;
        private const int SW_SHOWMAXIMIZED = 3;
        private const int SW_RESTORE = 9;
        private const string ModuleDisabledMessage = "智能体控制模块已冻结。";
        private static readonly TimeSpan TopWindowCacheTtl = TimeSpan.FromMilliseconds(400);
        private static readonly TimeSpan InstalledAppCacheTtl = TimeSpan.FromSeconds(30);
        private static readonly Regex SearchTokenRegex = new(@"[A-Za-z0-9]+|[\u4e00-\u9fff]+", RegexOptions.Compiled);

        private static readonly object s_cacheLock = new();
        private static readonly object s_appCacheLock = new();
        private static List<WindowNode> s_cachedTopWindows = new();
        private static List<InstalledAppCandidate> s_cachedInstalledApps = new();
        private static DateTime s_cachedTopWindowsAtUtc = DateTime.MinValue;
        private static DateTime s_cachedInstalledAppsAtUtc = DateTime.MinValue;
        private static int s_cachedFingerprint;
        private static int s_monitorVersion;
        private static long s_cacheHitCount;
        private static long s_cacheMissCount;
        private static readonly Dictionary<string, string[]> BuiltinAppAliasGroups = new(StringComparer.OrdinalIgnoreCase)
        {
            ["微信"] = new[] { "微信", "wechat", "weixin" },
            ["谷歌浏览器"] = new[] { "谷歌浏览器", "google chrome", "chrome", "chrome.exe" },
            ["edge"] = new[] { "edge", "microsoft edge", "msedge", "msedge.exe" },
            ["360浏览器"] = new[] { "360浏览器", "360极速浏览器", "360极速浏览器x", "360chromex", "360browser", "360se" },
            ["资源管理器"] = new[] { "资源管理器", "文件资源管理器", "explorer", "explorer.exe" },
            ["记事本"] = new[] { "记事本", "notepad", "notepad.exe" },
            ["wps"] = new[] { "wps", "wps office", "wps文字", "wps表格", "wps演示" }
        };

        [McpServerTool, Description(
            "采集 Windows 结构化 UI 快照，返回 snapshotId 与 ref 列表（优先使用 ref 驱动动作）。\n" +
            "参数：lane 为会话通道；windowTitleContains 可限定目标窗口；includeChildren 控制是否采集子控件。")]
        public static async Task<string> UiSnapshot(
            [Description("会话通道标识，同一 lane 串行执行。")] string lane = "default",
            [Description("目标窗口标题包含关键字，可空。")] string windowTitleContains = null,
            [Description("是否采集目标窗口的子控件。")] bool includeChildren = true,
            [Description("子控件采集上限，范围建议 10~300。")] int maxChildren = 120)
        {
            if (!Base.IsAgentControlModuleEnabled())
                return BuildError(nameof(UiSnapshot), ModuleDisabledMessage);

            return await UiLaneScheduler.RunAsync(lane, () =>
            {
                var laneKey = UiLaneScheduler.NormalizeLane(lane);
                var childrenLimit = Math.Clamp(maxChildren, 10, 300);

                var windows = GetTopWindows(forceRefresh: false);
                if (windows.Count == 0)
                {
                    var empty = JsonConvert.SerializeObject(new
                    {
                        type = "ui.snapshot",
                        ok = false,
                        lane = laneKey,
                        message = "未发现可见窗口。"
                    });
                    ToolCallLogger.Log(nameof(UiSnapshot), new { lane, windowTitleContains, includeChildren, maxChildren }, empty);
                    return Task.FromResult(empty);
                }

                var foreground = ToHwndLong(GetForegroundWindow());
                var target = SelectTargetWindow(windows, foreground, windowTitleContains);
                var refMap = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

                var windowsWithRef = new List<object>();
                for (var i = 0; i < windows.Count; i++)
                {
                    var item = windows[i];
                    var refId = $"W{i + 1}";
                    refMap[refId] = item.Hwnd;
                    windowsWithRef.Add(new
                    {
                        refId,
                        item.Hwnd,
                        item.Title,
                        item.ClassName,
                        item.ProcessName,
                        item.Left,
                        item.Top,
                        item.Width,
                        item.Height,
                        item.Enabled,
                        item.Visible,
                        isForeground = item.Hwnd == foreground,
                        selector = new
                        {
                            titleContains = item.Title,
                            className = item.ClassName,
                            processName = item.ProcessName,
                            windowIndex = i
                        }
                    });
                }

                var childrenWithRef = new List<object>();
                if (includeChildren && target != null)
                {
                    var children = EnumerateChildWindows(new IntPtr(target.Hwnd), childrenLimit);
                    for (var i = 0; i < children.Count; i++)
                    {
                        var child = children[i];
                        var refId = $"C{i + 1}";
                        refMap[refId] = child.Hwnd;
                        childrenWithRef.Add(new
                        {
                            refId,
                            child.Hwnd,
                            child.Title,
                            child.ClassName,
                            child.ProcessName,
                            child.Left,
                            child.Top,
                            child.Width,
                            child.Height,
                            child.Enabled,
                            child.Visible,
                            selector = new
                            {
                                titleContains = target.Title,
                                className = target.ClassName,
                                processName = target.ProcessName,
                                childTitleContains = child.Title,
                                childClassName = child.ClassName,
                                windowIndex = 0
                            }
                        });
                    }
                }

                if (target != null)
                    refMap["TARGET"] = target.Hwnd;

                var snapshot = UiSnapshotStore.Create(laneKey, refMap);
                var result = JsonConvert.SerializeObject(new
                {
                    type = "ui.snapshot",
                    ok = true,
                    lane = laneKey,
                    snapshotId = snapshot.SnapshotId,
                    capturedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    monitorVersion = GetMonitorVersion(),
                    target = target == null
                        ? null
                        : new
                        {
                            refId = "TARGET",
                            target.Hwnd,
                            target.Title,
                            target.ClassName,
                            target.ProcessName,
                            target.Left,
                            target.Top,
                            target.Width,
                            target.Height
                        },
                    windows = windowsWithRef,
                    children = childrenWithRef,
                    tips = "推荐使用 selector+ref 双策略：先 snapshot，再 act；ref 失效时回退 selector。"
                });
                ToolCallLogger.Log(nameof(UiSnapshot), new { lane, windowTitleContains, includeChildren, maxChildren = childrenLimit }, result);
                return Task.FromResult(result);
            }).ConfigureAwait(false);
        }

        [McpServerTool, Description(
            "按 snapshot ref 执行 UI 动作。支持 action：activate/click/double_click/right_click/set_text/close。\n" +
            "支持 retries 与 verifyTimeoutMs，用于动作后验收与自动重试。")]
        public static async Task<string> UiActByRef(
            [Description("快照 ID，来自 UiSnapshot 返回。")] string snapshotId = null,
            [Description("元素引用 ID，例如 TARGET、W1、C3。")] string refId = null,
            [Description("动作名：activate/click/double_click/right_click/set_text/close。")] string action = null,
            [Description("set_text 时写入文本。")] string text = null,
            [Description("close 等高风险动作需显式确认。")] bool confirmed = false,
            [Description("会话通道标识，同一 lane 串行执行。")] string lane = "default",
            [Description("幂等键。相同 lane+key 在有效期内直接复用结果。")] string idempotencyKey = null,
            [Description("动作失败时重试次数（0~3）。")] int retries = 1,
            [Description("动作验收超时（毫秒，200~5000）。")] int verifyTimeoutMs = 900,
            [Description("验收时可选标题关键字。")] string verifyTitleContains = null)
        {
            if (!Base.IsAgentControlModuleEnabled())
                return BuildError(nameof(UiActByRef), ModuleDisabledMessage);

            return await UiLaneScheduler.RunAsync(lane, () =>
            {
                if (UiSnapshotStore.TryGetCachedAction(lane, idempotencyKey, out var cached))
                {
                    var dedup = BuildDeduplicatedResult(cached);
                    ToolCallLogger.Log(nameof(UiActByRef), new { snapshotId, refId, action, lane, idempotencyKey }, dedup);
                    return Task.FromResult(dedup);
                }

                if (!UiSnapshotStore.TryResolveRef(snapshotId, refId, out var hwnd, out var reason))
                {
                    var err = BuildError("UiActByRef", reason);
                    ToolCallLogger.Log(nameof(UiActByRef), new { snapshotId, refId, action, lane, idempotencyKey }, null, reason);
                    return Task.FromResult(err);
                }

                var result = ExecuteActionWithVerify(
                    hwnd: hwnd,
                    action: action,
                    text: text,
                    confirmed: confirmed,
                    lane: lane,
                    source: refId,
                    x: null,
                    y: null,
                    retries: retries,
                    verifyTimeoutMs: verifyTimeoutMs,
                    verifyTitleContains: verifyTitleContains);

                UiSnapshotStore.PutCachedAction(lane, idempotencyKey, result);
                ToolCallLogger.Log(nameof(UiActByRef), new { snapshotId, refId, action, text, confirmed, lane, idempotencyKey, retries, verifyTimeoutMs, verifyTitleContains }, result);
                return Task.FromResult(result);
            }).ConfigureAwait(false);
        }

        [McpServerTool, Description(
            "按窗口句柄执行 UI 动作。支持 action：activate/click/double_click/right_click/set_text/close。\n" +
            "支持 retries 与 verifyTimeoutMs，用于动作后验收与自动重试。")]
        public static async Task<string> UiActByHwnd(
            [Description("窗口句柄（十进制），例如 131072。")] long hwnd = 0,
            [Description("动作名：activate/click/double_click/right_click/set_text/close。")] string action = null,
            [Description("set_text 时写入文本。")] string text = null,
            [Description("close 等高风险动作需显式确认。")] bool confirmed = false,
            [Description("会话通道标识，同一 lane 串行执行。")] string lane = "default",
            [Description("幂等键。相同 lane+key 在有效期内直接复用结果。")] string idempotencyKey = null,
            [Description("click 时可选 X 坐标（屏幕坐标）。")] int? x = null,
            [Description("click 时可选 Y 坐标（屏幕坐标）。")] int? y = null,
            [Description("动作失败时重试次数（0~3）。")] int retries = 1,
            [Description("动作验收超时（毫秒，200~5000）。")] int verifyTimeoutMs = 900,
            [Description("验收时可选标题关键字。")] string verifyTitleContains = null)
        {
            if (!Base.IsAgentControlModuleEnabled())
                return BuildError(nameof(UiActByHwnd), ModuleDisabledMessage);

            return await UiLaneScheduler.RunAsync(lane, () =>
            {
                if (hwnd == 0)
                {
                    var err = BuildError("UiActByHwnd", "缺少 hwnd。");
                    ToolCallLogger.Log(nameof(UiActByHwnd), new { hwnd, action, text, confirmed, lane, idempotencyKey, x, y }, null, "缺少 hwnd");
                    return Task.FromResult(err);
                }

                if (UiSnapshotStore.TryGetCachedAction(lane, idempotencyKey, out var cached))
                {
                    var dedup = BuildDeduplicatedResult(cached);
                    ToolCallLogger.Log(nameof(UiActByHwnd), new { hwnd, action, lane, idempotencyKey }, dedup);
                    return Task.FromResult(dedup);
                }

                var result = ExecuteActionWithVerify(
                    hwnd: hwnd,
                    action: action,
                    text: text,
                    confirmed: confirmed,
                    lane: lane,
                    source: "hwnd",
                    x: x,
                    y: y,
                    retries: retries,
                    verifyTimeoutMs: verifyTimeoutMs,
                    verifyTitleContains: verifyTitleContains);

                UiSnapshotStore.PutCachedAction(lane, idempotencyKey, result);
                ToolCallLogger.Log(nameof(UiActByHwnd), new { hwnd, action, text, confirmed, lane, idempotencyKey, x, y, retries, verifyTimeoutMs, verifyTitleContains }, result);
                return Task.FromResult(result);
            }).ConfigureAwait(false);
        }

        [McpServerTool, Description(
            "按稳定 selector 执行动作。可用字段：titleContains/className/processName/childTitleContains/childClassName/windowIndex。")]
        public static async Task<string> UiActBySelector(
            [Description("窗口标题关键字，可空。")] string titleContains = null,
            [Description("窗口类名，可空。")] string className = null,
            [Description("进程名，可空。")] string processName = null,
            [Description("子控件标题关键字，可空。")] string childTitleContains = null,
            [Description("子控件类名，可空。")] string childClassName = null,
            [Description("命中多个窗口时取第几个（0-based）。")] int windowIndex = 0,
            [Description("动作名：activate/click/double_click/right_click/set_text/close。")] string action = null,
            [Description("set_text 时写入文本。")] string text = null,
            [Description("close 等高风险动作需显式确认。")] bool confirmed = false,
            [Description("会话通道标识，同一 lane 串行执行。")] string lane = "default",
            [Description("幂等键。相同 lane+key 在有效期内直接复用结果。")] string idempotencyKey = null,
            [Description("动作失败时重试次数（0~3）。")] int retries = 1,
            [Description("动作验收超时（毫秒，200~5000）。")] int verifyTimeoutMs = 900,
            [Description("验收时可选标题关键字。")] string verifyTitleContains = null)
        {
            if (!Base.IsAgentControlModuleEnabled())
                return BuildError(nameof(UiActBySelector), ModuleDisabledMessage);

            return await UiLaneScheduler.RunAsync(lane, () =>
            {
                if (UiSnapshotStore.TryGetCachedAction(lane, idempotencyKey, out var cached))
                {
                    var dedup = BuildDeduplicatedResult(cached);
                    ToolCallLogger.Log(nameof(UiActBySelector), new { titleContains, className, processName, childTitleContains, childClassName, windowIndex, action, lane, idempotencyKey }, dedup);
                    return Task.FromResult(dedup);
                }

                var windows = ResolveWindowsBySelector(titleContains, className, processName);
                if (windows.Count == 0)
                {
                    var err = BuildError("UiActBySelector", "未匹配到目标窗口。", new { titleContains, className, processName });
                    ToolCallLogger.Log(nameof(UiActBySelector), new { titleContains, className, processName, childTitleContains, childClassName, windowIndex, action, lane, idempotencyKey }, err, "selector no match");
                    return Task.FromResult(err);
                }

                var idx = Math.Clamp(windowIndex, 0, windows.Count - 1);
                var selectedWindow = windows[idx];
                var selectedHwnd = selectedWindow.Hwnd;
                var selectorSource = "selector-window";

                if (!string.IsNullOrWhiteSpace(childTitleContains) || !string.IsNullOrWhiteSpace(childClassName))
                {
                    var childCandidates = EnumerateChildWindows(new IntPtr(selectedWindow.Hwnd), 300)
                        .Where(c =>
                            (string.IsNullOrWhiteSpace(childTitleContains) || ContainsIgnoreCase(c.Title, childTitleContains)) &&
                            (string.IsNullOrWhiteSpace(childClassName) || ContainsIgnoreCase(c.ClassName, childClassName)))
                        .ToList();

                    if (childCandidates.Count == 0)
                    {
                        var err = BuildError("UiActBySelector", "窗口已命中，但未匹配到子控件。", new
                        {
                            titleContains,
                            className,
                            processName,
                            childTitleContains,
                            childClassName,
                            windowIndex = idx
                        });
                        ToolCallLogger.Log(nameof(UiActBySelector), new { titleContains, className, processName, childTitleContains, childClassName, windowIndex, action, lane, idempotencyKey }, err, "selector child no match");
                        return Task.FromResult(err);
                    }

                    selectedHwnd = childCandidates[0].Hwnd;
                    selectorSource = "selector-child";
                }

                var result = ExecuteActionWithVerify(
                    hwnd: selectedHwnd,
                    action: action,
                    text: text,
                    confirmed: confirmed,
                    lane: lane,
                    source: selectorSource,
                    x: null,
                    y: null,
                    retries: retries,
                    verifyTimeoutMs: verifyTimeoutMs,
                    verifyTitleContains: verifyTitleContains);

                UiSnapshotStore.PutCachedAction(lane, idempotencyKey, result);
                ToolCallLogger.Log(nameof(UiActBySelector), new { titleContains, className, processName, childTitleContains, childClassName, windowIndex = idx, selectedHwnd, action, text, confirmed, lane, idempotencyKey, retries, verifyTimeoutMs, verifyTitleContains }, result);
                return Task.FromResult(result);
            }).ConfigureAwait(false);
        }

        [McpServerTool, Description("等待指定标题关键字的窗口出现，常用于动作前置同步。")]
        public static async Task<string> UiWaitWindow(
            [Description("窗口标题关键字。")] string titleContains = null,
            [Description("超时时间（毫秒），默认 5000。")] int timeoutMs = 5000,
            [Description("轮询间隔（毫秒），默认 200。")] int pollMs = 200,
            [Description("会话通道标识，同一 lane 串行执行。")] string lane = "default")
        {
            if (!Base.IsAgentControlModuleEnabled())
                return BuildError(nameof(UiWaitWindow), ModuleDisabledMessage);

            return await UiLaneScheduler.RunAsync(lane, async () =>
            {
                if (string.IsNullOrWhiteSpace(titleContains))
                {
                    var err = BuildError("UiWaitWindow", "缺少 titleContains。");
                    ToolCallLogger.Log(nameof(UiWaitWindow), new { titleContains, timeoutMs, pollMs, lane }, null, "缺少 titleContains");
                    return err;
                }

                var timeout = Math.Clamp(timeoutMs, 200, 120000);
                var interval = Math.Clamp(pollMs, 50, 2000);
                var deadline = DateTime.UtcNow.AddMilliseconds(timeout);

                while (DateTime.UtcNow <= deadline)
                {
                    var windows = GetTopWindows(forceRefresh: true);
                    var found = windows.FirstOrDefault(w => ContainsIgnoreCase(w.Title, titleContains));
                    if (found != null)
                    {
                        var ok = JsonConvert.SerializeObject(new
                        {
                            type = "ui.wait",
                            ok = true,
                            lane = UiLaneScheduler.NormalizeLane(lane),
                            titleContains,
                            hwnd = found.Hwnd,
                            title = found.Title
                        });
                        ToolCallLogger.Log(nameof(UiWaitWindow), new { titleContains, timeoutMs = timeout, pollMs = interval, lane }, ok);
                        return ok;
                    }

                    await Task.Delay(interval).ConfigureAwait(false);
                }

                var timeoutResult = JsonConvert.SerializeObject(new
                {
                    type = "ui.wait",
                    ok = false,
                    lane = UiLaneScheduler.NormalizeLane(lane),
                    titleContains,
                    message = "超时未找到窗口。"
                });
                ToolCallLogger.Log(nameof(UiWaitWindow), new { titleContains, timeoutMs = timeout, pollMs = interval, lane }, timeoutResult, "timeout");
                return timeoutResult;
            }).ConfigureAwait(false);
        }

        [McpServerTool, Description("返回 UI 监控缓存状态（用于增量快照调优）。")]
        public static async Task<string> UiMonitorStatus(
            [Description("true 时强制刷新窗口缓存。")] bool forceRefresh = false,
            [Description("会话通道标识，同一 lane 串行执行。")] string lane = "default")
        {
            if (!Base.IsAgentControlModuleEnabled())
                return BuildError(nameof(UiMonitorStatus), ModuleDisabledMessage);

            return await UiLaneScheduler.RunAsync(lane, () =>
            {
                var windows = GetTopWindows(forceRefresh);
                var status = JsonConvert.SerializeObject(new
                {
                    type = "ui.monitor",
                    ok = true,
                    lane = UiLaneScheduler.NormalizeLane(lane),
                    monitorVersion = GetMonitorVersion(),
                    windowCount = windows.Count,
                    cache = new
                    {
                        hit = Interlocked.Read(ref s_cacheHitCount),
                        miss = Interlocked.Read(ref s_cacheMissCount),
                        lastRefreshUtc = s_cachedTopWindowsAtUtc.ToString("O"),
                        ttlMs = (int)TopWindowCacheTtl.TotalMilliseconds
                    }
                });
                ToolCallLogger.Log(nameof(UiMonitorStatus), new { forceRefresh, lane }, status);
                return Task.FromResult(status);
            }).ConfigureAwait(false);
        }

        [McpServerTool, Description(
            "执行本地通用命令。\n" +
            "commandType 当前支持 mouse、keyboard、window、shell、app、browser、clipboard、explorer、file。\n" +
            "mouse 支持 move、move_click、move_double_click、move_right_click；" +
            "keyboard 支持 type_text、focus_type_text、key_press、hotkey；" +
            "window 支持 activate、wait_appear、wait_disappear、close、list、find_best_match；" +
            "shell 支持 open_path、launch_app、open_url；" +
            "app 支持 list_installed、resolve、open_or_activate；" +
            "browser 支持 open_or_activate、open_url、open_url_in_tab、search_web、search_site；" +
            "clipboard 支持 set_text、paste_text；" +
            "explorer 支持 open_path_and_wait、open_path_and_select；" +
            "file 支持 create_directory、create_text_file、save_as、rename。")]
        public static async Task<string> UiCommand(
            [Description("命令类别。当前支持 mouse、keyboard、window、shell、app、browser、clipboard、explorer、file。")] string commandType = "mouse",
            [Description("命令操作名。不同 commandType 支持不同 operation。")] string operation = "move",
            [Description("屏幕绝对 X 坐标，可空。")] int? x = null,
            [Description("屏幕绝对 Y 坐标，可空。")] int? y = null,
            [Description("0-1000 相对 X 坐标，可空。")] int? relativeX = null,
            [Description("0-1000 相对 Y 坐标，可空。")] int? relativeY = null,
            [Description("截图范围左边界，可空；为空时默认虚拟屏幕。")] int? captureLeft = null,
            [Description("截图范围上边界，可空；为空时默认虚拟屏幕。")] int? captureTop = null,
            [Description("截图范围宽度，可空；为空时默认虚拟屏幕。")] int? captureWidth = null,
            [Description("截图范围高度，可空；为空时默认虚拟屏幕。")] int? captureHeight = null,
            [Description("键盘输入文本；供 keyboard 类命令后续扩展使用。")] string text = null,
            [Description("按键数组；供 keyboard 类命令后续扩展使用。")] string[] keys = null,
            [Description("修饰键数组；供 keyboard 类命令后续扩展使用。")] string[] modifiers = null,
            [Description("命令行内容；供 shell 类命令后续扩展使用。")] string commandLine = null,
            [Description("工作目录；供 shell 类命令后续扩展使用。")] string workingDirectory = null,
            [Description("文件名；供 explorer.open_path_and_select 与 file.rename 使用。")] string fileName = null,
            [Description("应用名；供 app 类命令使用。")] string appName = null,
            [Description("浏览器名；供 browser 类命令使用。")] string browserName = null,
            [Description("网址；供 browser.open_url 使用。")] string url = null,
            [Description("搜索关键词；供 browser.search_web 使用。")] string query = null,
            [Description("站点限定；供 browser.search_web 使用，可填域名或站点名。")] string site = null,
            [Description("最大返回数量；供 app.list_installed 使用。")] int maxResults = 20,
            [Description("目标窗口标题关键字；供 window/keyboard 类命令使用。")] string targetWindowTitleContains = null,
            [Description("高风险命令显式确认标记，例如 window.close。")] bool confirmed = false,
            [Description("等待/激活类命令超时（毫秒，200~120000）。")] int timeoutMs = 5000,
            [Description("等待类命令轮询间隔（毫秒，50~2000）。")] int pollMs = 200,
            [Description("会话通道标识，同一 lane 串行执行。")] string lane = "default")
        {
            if (!Base.IsAgentControlModuleEnabled())
                return BuildError(nameof(UiCommand), ModuleDisabledMessage);

            var normalizedCommandType = NormalizeCommandType(commandType);
            if (string.IsNullOrWhiteSpace(normalizedCommandType))
            {
                var err = BuildError("UiCommand", "commandType 当前仅支持 mouse、keyboard、window、shell、app、browser、clipboard、explorer、file。", new
                {
                    commandType
                });
                ToolCallLogger.Log(nameof(UiCommand), new
                {
                    commandType,
                    operation,
                    x,
                    y,
                    relativeX,
                    relativeY,
                    captureLeft,
                    captureTop,
                    captureWidth,
                    captureHeight,
                    text,
                    keys,
                    modifiers,
                    commandLine,
                    workingDirectory,
                    fileName,
                    appName,
                    browserName,
                    url,
                    query,
                    site,
                    maxResults,
                    targetWindowTitleContains,
                    confirmed,
                    timeoutMs,
                    pollMs,
                    lane
                }, err, "invalid commandType");
                return err;
            }

            if (string.Equals(normalizedCommandType, "mouse", StringComparison.Ordinal))
            {
                return await ExecuteMouseCommandAsync(
                    operation: operation,
                    x: x,
                    y: y,
                    relativeX: relativeX,
                    relativeY: relativeY,
                    captureLeft: captureLeft,
                    captureTop: captureTop,
                    captureWidth: captureWidth,
                    captureHeight: captureHeight,
                    lane: lane).ConfigureAwait(false);
            }

            if (string.Equals(normalizedCommandType, "keyboard", StringComparison.Ordinal))
            {
                return await ExecuteKeyboardCommandAsync(
                    operation: operation,
                    text: text,
                    keys: keys ?? Array.Empty<string>(),
                    modifiers: modifiers ?? Array.Empty<string>(),
                    targetWindowTitleContains: targetWindowTitleContains,
                    timeoutMs: timeoutMs,
                    pollMs: pollMs,
                    lane: lane).ConfigureAwait(false);
            }

            if (string.Equals(normalizedCommandType, "window", StringComparison.Ordinal))
            {
                return await ExecuteWindowCommandAsync(
                    operation: operation,
                    targetWindowTitleContains: targetWindowTitleContains,
                    confirmed: confirmed,
                    timeoutMs: timeoutMs,
                    pollMs: pollMs,
                    lane: lane).ConfigureAwait(false);
            }

            if (string.Equals(normalizedCommandType, "app", StringComparison.Ordinal))
            {
                return await ExecuteAppCommandAsync(
                    operation: operation,
                    appName: appName,
                    commandLine: commandLine,
                    workingDirectory: workingDirectory,
                    timeoutMs: timeoutMs,
                    pollMs: pollMs,
                    maxResults: maxResults,
                    lane: lane).ConfigureAwait(false);
            }

            if (string.Equals(normalizedCommandType, "browser", StringComparison.Ordinal))
            {
                return await ExecuteBrowserCommandAsync(
                    operation: operation,
                    browserName: browserName,
                    url: url,
                    query: query,
                    site: site,
                    timeoutMs: timeoutMs,
                    pollMs: pollMs,
                    lane: lane).ConfigureAwait(false);
            }

            if (string.Equals(normalizedCommandType, "clipboard", StringComparison.Ordinal))
            {
                return await ExecuteClipboardCommandAsync(
                    operation: operation,
                    text: text,
                    targetWindowTitleContains: targetWindowTitleContains,
                    timeoutMs: timeoutMs,
                    pollMs: pollMs,
                    lane: lane).ConfigureAwait(false);
            }

            if (string.Equals(normalizedCommandType, "explorer", StringComparison.Ordinal))
            {
                return await ExecuteExplorerCommandAsync(
                    operation: operation,
                    commandLine: commandLine,
                    fileName: fileName,
                    timeoutMs: timeoutMs,
                    pollMs: pollMs,
                    lane: lane).ConfigureAwait(false);
            }

            if (string.Equals(normalizedCommandType, "file", StringComparison.Ordinal))
            {
                return await ExecuteFileCommandAsync(
                    operation: operation,
                    commandLine: commandLine,
                    text: text,
                    fileName: fileName,
                    lane: lane).ConfigureAwait(false);
            }

            return await ExecuteShellCommandAsync(
                operation: operation,
                commandLine: commandLine,
                workingDirectory: workingDirectory,
                timeoutMs: timeoutMs,
                pollMs: pollMs,
                lane: lane).ConfigureAwait(false);
        }

        private static string NormalizeKeyboardOperation(string operation)
        {
            var normalized = (operation ?? string.Empty).Trim().ToLowerInvariant();
            return normalized switch
            {
                "type_text" or "input_text" or "text" or "输入文本" => "type_text",
                "focus_type_text" or "activate_type_text" or "focus_and_type_text" or "聚焦输入" or "聚焦后输入" => "focus_type_text",
                "key_press" or "press_key" or "按键" => "key_press",
                "hotkey" or "shortcut" or "快捷键" => "hotkey",
                _ => string.Empty
            };
        }

        private static string NormalizeWindowOperation(string operation)
        {
            var normalized = (operation ?? string.Empty).Trim().ToLowerInvariant();
            return normalized switch
            {
                "activate" or "focus" or "激活" or "聚焦" => "activate",
                "wait_appear" or "wait_window" or "等待出现" => "wait_appear",
                "wait_disappear" or "wait_close" or "等待消失" => "wait_disappear",
                "close" or "关闭" => "close",
                "list" or "枚举窗口" or "列出窗口" => "list",
                "find_best_match" or "find" or "查找最佳窗口" => "find_best_match",
                _ => string.Empty
            };
        }

        private static string NormalizeShellOperation(string operation)
        {
            var normalized = (operation ?? string.Empty).Trim().ToLowerInvariant();
            return normalized switch
            {
                "open_path" or "open_file" or "open_folder" or "打开路径" => "open_path",
                "launch_app" or "start_app" or "open_app" or "启动程序" => "launch_app",
                "open_url" or "browse_url" or "打开网址" => "open_url",
                _ => string.Empty
            };
        }

        private static string NormalizeAppOperation(string operation)
        {
            var normalized = (operation ?? string.Empty).Trim().ToLowerInvariant();
            return normalized switch
            {
                "list" or "list_installed" or "installed_list" or "枚举已安装应用" => "list_installed",
                "resolve" or "find" or "查找应用" => "resolve",
                "open_or_activate" or "launch_or_activate" or "打开或激活应用" => "open_or_activate",
                _ => string.Empty
            };
        }

        private static string NormalizeBrowserOperation(string operation)
        {
            var normalized = (operation ?? string.Empty).Trim().ToLowerInvariant();
            return normalized switch
            {
                "open_or_activate" or "activate" or "打开或激活浏览器" => "open_or_activate",
                "open_url" or "browse_url" or "打开网址" => "open_url",
                "open_url_in_tab" or "open_in_tab" or "标签页打开网址" => "open_url_in_tab",
                "search" or "search_web" or "web_search" or "搜索网页" => "search_web",
                "search_site" or "site_search" or "站内搜索" => "search_site",
                _ => string.Empty
            };
        }

        private static string NormalizeClipboardOperation(string operation)
        {
            var normalized = (operation ?? string.Empty).Trim().ToLowerInvariant();
            return normalized switch
            {
                "set_text" or "copy_text" or "复制文本" => "set_text",
                "paste_text" or "粘贴文本" => "paste_text",
                _ => string.Empty
            };
        }

        private static string NormalizeExplorerOperation(string operation)
        {
            var normalized = (operation ?? string.Empty).Trim().ToLowerInvariant();
            return normalized switch
            {
                "open_path_and_wait" or "打开路径并等待" => "open_path_and_wait",
                "open_path_and_select" or "打开路径并选中" => "open_path_and_select",
                _ => string.Empty
            };
        }

        private static string NormalizeFileOperation(string operation)
        {
            var normalized = (operation ?? string.Empty).Trim().ToLowerInvariant();
            return normalized switch
            {
                "create_directory" or "mkdir" or "创建目录" or "新建文件夹" => "create_directory",
                "create_text_file" or "创建文本文件" => "create_text_file",
                "save_as" or "保存为" => "save_as",
                "rename" or "重命名" => "rename",
                _ => string.Empty
            };
        }

        private static async Task<string> ExecuteAppCommandAsync(
            string operation,
            string appName,
            string commandLine,
            string workingDirectory,
            int timeoutMs,
            int pollMs,
            int maxResults,
            string lane)
        {
            return await UiLaneScheduler.RunAsync(lane, () =>
            {
                var normalizedOperation = NormalizeAppOperation(operation);
                if (string.IsNullOrWhiteSpace(normalizedOperation))
                {
                    var err = BuildError("UiCommand", "app.operation 仅支持 list_installed、resolve、open_or_activate。", new { operation });
                    ToolCallLogger.Log(nameof(UiCommand), new
                    {
                        commandType = "app",
                        operation,
                        appName,
                        commandLine,
                        workingDirectory,
                        maxResults,
                        lane
                    }, err, "invalid operation");
                    return Task.FromResult(err);
                }

                var query = FirstNonEmpty(appName, commandLine);
                var take = Math.Clamp(maxResults <= 0 ? 20 : maxResults, 1, 200);

                if (string.Equals(normalizedOperation, "list_installed", StringComparison.Ordinal))
                {
                    var matches = string.IsNullOrWhiteSpace(query)
                        ? GetInstalledApps(forceRefresh: false)
                        : ResolveInstalledApps(query, take);
                    var payload = JsonConvert.SerializeObject(new
                    {
                        type = "ui.command",
                        ok = true,
                        commandType = "app",
                        operation = normalizedOperation,
                        appName = query ?? string.Empty,
                        count = matches.Count,
                        apps = matches
                            .Take(take)
                            .Select(ToAppResult)
                            .ToList()
                    });
                    ToolCallLogger.Log(nameof(UiCommand), new
                    {
                        commandType = "app",
                        operation = normalizedOperation,
                        appName = query,
                        maxResults = take,
                        lane
                    }, payload);
                    return Task.FromResult(payload);
                }

                if (string.IsNullOrWhiteSpace(query))
                {
                    var err = BuildError("UiCommand", $"app.{normalizedOperation} 需要 appName。");
                    ToolCallLogger.Log(nameof(UiCommand), new
                    {
                        commandType = "app",
                        operation = normalizedOperation,
                        appName,
                        commandLine,
                        lane
                    }, err, "missing appName");
                    return Task.FromResult(err);
                }

                var candidates = ResolveInstalledApps(query, Math.Max(5, take));
                var bestCandidate = candidates.FirstOrDefault();

                if (string.Equals(normalizedOperation, "resolve", StringComparison.Ordinal))
                {
                    if (bestCandidate == null)
                    {
                        var err = BuildError("UiCommand", "未解析到匹配应用。", new
                        {
                            appName = query,
                            suggestions = GetInstalledApps(forceRefresh: false)
                                .Take(10)
                                .Select(x => x.DisplayName)
                                .Where(x => !string.IsNullOrWhiteSpace(x))
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToList()
                        });
                        ToolCallLogger.Log(nameof(UiCommand), new
                        {
                            commandType = "app",
                            operation = normalizedOperation,
                            appName = query,
                            lane
                        }, err, "resolve failed");
                        return Task.FromResult(err);
                    }

                    var payload = JsonConvert.SerializeObject(new
                    {
                        type = "ui.command",
                        ok = true,
                        commandType = "app",
                        operation = normalizedOperation,
                        appName = query,
                        resolved = ToAppResult(bestCandidate),
                        candidates = candidates.Take(Math.Max(3, take)).Select(ToAppResult).ToList()
                    });
                    ToolCallLogger.Log(nameof(UiCommand), new
                    {
                        commandType = "app",
                        operation = normalizedOperation,
                        appName = query,
                        lane
                    }, payload);
                    return Task.FromResult(payload);
                }

                if (TryFindWindowByTitle(query, out var titleMatchedWindow))
                {
                    var activated = ActivateWindow(titleMatchedWindow, timeoutMs, pollMs, out var activateMessage, maximizeWindow: true);
                    var payload = JsonConvert.SerializeObject(new
                    {
                        type = "ui.command",
                        ok = activated,
                        commandType = "app",
                        operation = normalizedOperation,
                        appName = query,
                        mode = "activate_existing_window",
                        hwnd = titleMatchedWindow.Hwnd,
                        title = titleMatchedWindow.Title,
                        processName = titleMatchedWindow.ProcessName,
                        message = activateMessage
                    });
                    ToolCallLogger.Log(nameof(UiCommand), new
                    {
                        commandType = "app",
                        operation = normalizedOperation,
                        appName = query,
                        lane
                    }, payload, activated ? null : activateMessage);
                    return Task.FromResult(payload);
                }

                // 托盘常驻应用的窗口标题可能不稳定，再按进程名/别名做一次激活尝试。
                if (TryFindWindowByProcessOrTitle(query, query, out var processOrTitleMatchedWindow))
                {
                    var activated = ActivateWindow(processOrTitleMatchedWindow, timeoutMs, pollMs, out var activateMessage, maximizeWindow: true);
                    var payload = JsonConvert.SerializeObject(new
                    {
                        type = "ui.command",
                        ok = activated,
                        commandType = "app",
                        operation = normalizedOperation,
                        appName = query,
                        mode = "activate_existing_process_or_title_window",
                        hwnd = processOrTitleMatchedWindow.Hwnd,
                        title = processOrTitleMatchedWindow.Title,
                        processName = processOrTitleMatchedWindow.ProcessName,
                        message = activateMessage
                    });
                    ToolCallLogger.Log(nameof(UiCommand), new
                    {
                        commandType = "app",
                        operation = normalizedOperation,
                        appName = query,
                        lane
                    }, payload, activated ? null : activateMessage);
                    return Task.FromResult(payload);
                }

                if (bestCandidate == null || string.IsNullOrWhiteSpace(bestCandidate.ExecutablePath))
                {
                    var err = BuildError("UiCommand", "未解析到可启动的应用路径。", new
                    {
                        appName = query,
                        candidates = candidates.Take(5).Select(ToAppResult).ToList()
                    });
                    ToolCallLogger.Log(nameof(UiCommand), new
                    {
                        commandType = "app",
                        operation = normalizedOperation,
                        appName = query,
                        lane
                    }, err, "no executable");
                    return Task.FromResult(err);
                }

                if (!File.Exists(bestCandidate.ExecutablePath))
                {
                    var err = BuildError("UiCommand", "解析到了应用记录，但可执行文件不存在。", new
                    {
                        appName = query,
                        executablePath = bestCandidate.ExecutablePath,
                        source = bestCandidate.Source
                    });
                    ToolCallLogger.Log(nameof(UiCommand), new
                    {
                        commandType = "app",
                        operation = normalizedOperation,
                        appName = query,
                        lane
                    }, err, "executable missing");
                    return Task.FromResult(err);
                }

                return Task.FromResult(StartResolvedApplication(
                    commandType: "app",
                    operation: normalizedOperation,
                    executablePath: bestCandidate.ExecutablePath,
                    arguments: string.Empty,
                    workingDirectory: FirstNonEmpty(workingDirectory, bestCandidate.WorkingDirectory),
                    titleQuery: FirstNonEmpty(bestCandidate.DisplayName, query),
                    processQuery: bestCandidate.ProcessName,
                    timeoutMs: timeoutMs,
                    pollMs: pollMs,
                    metadata: new
                    {
                        appName = query,
                        resolved = ToAppResult(bestCandidate)
                    },
                    lane: lane));
            }).ConfigureAwait(false);
        }

        private static async Task<string> ExecuteBrowserCommandAsync(
            string operation,
            string browserName,
            string url,
            string query,
            string site,
            int timeoutMs,
            int pollMs,
            string lane)
        {
            return await UiLaneScheduler.RunAsync(lane, () =>
            {
                var normalizedOperation = NormalizeBrowserOperation(operation);
                if (string.IsNullOrWhiteSpace(normalizedOperation))
                {
                    var err = BuildError("UiCommand", "browser.operation 仅支持 open_or_activate、open_url、open_url_in_tab、search_web、search_site。", new { operation });
                    ToolCallLogger.Log(nameof(UiCommand), new
                    {
                        commandType = "browser",
                        operation,
                        browserName,
                        url,
                        query,
                        site,
                        lane
                    }, err, "invalid operation");
                    return Task.FromResult(err);
                }

                var trimmedBrowserName = (browserName ?? string.Empty).Trim();
                if (string.Equals(normalizedOperation, "open_or_activate", StringComparison.Ordinal))
                {
                    if (string.IsNullOrWhiteSpace(trimmedBrowserName))
                    {
                        var err = BuildError("UiCommand", "browser.open_or_activate 需要 browserName。");
                        ToolCallLogger.Log(nameof(UiCommand), new
                        {
                            commandType = "browser",
                            operation = normalizedOperation,
                            browserName,
                            lane
                        }, err, "missing browserName");
                        return Task.FromResult(err);
                    }

                    if (TryFindWindowByProcessOrTitle(trimmedBrowserName, trimmedBrowserName, out var existingWindow))
                    {
                        var activated = ActivateWindow(existingWindow, timeoutMs, pollMs, out var activateMessage, maximizeWindow: true);
                        var payload = JsonConvert.SerializeObject(new
                        {
                            type = "ui.command",
                            ok = activated,
                            commandType = "browser",
                            operation = normalizedOperation,
                            browserName = trimmedBrowserName,
                            mode = "activate_existing_window",
                            hwnd = existingWindow.Hwnd,
                            title = existingWindow.Title,
                            processName = existingWindow.ProcessName,
                            message = activateMessage
                        });
                        ToolCallLogger.Log(nameof(UiCommand), new
                        {
                            commandType = "browser",
                            operation = normalizedOperation,
                            browserName = trimmedBrowserName,
                            lane
                        }, payload, activated ? null : activateMessage);
                        return Task.FromResult(payload);
                    }

                    var browserCandidateForActivate = ResolveInstalledApps(trimmedBrowserName, 5).FirstOrDefault();
                    if (browserCandidateForActivate == null || string.IsNullOrWhiteSpace(browserCandidateForActivate.ExecutablePath))
                    {
                        var err = BuildError("UiCommand", "未解析到可用浏览器路径。", new { browserName = trimmedBrowserName });
                        ToolCallLogger.Log(nameof(UiCommand), new
                        {
                            commandType = "browser",
                            operation = normalizedOperation,
                            browserName = trimmedBrowserName,
                            lane
                        }, err, "browser resolve failed");
                        return Task.FromResult(err);
                    }

                    if (!File.Exists(browserCandidateForActivate.ExecutablePath))
                    {
                        var err = BuildError("UiCommand", "浏览器记录已解析，但可执行文件不存在。", new
                        {
                            browserName = trimmedBrowserName,
                            executablePath = browserCandidateForActivate.ExecutablePath
                        });
                        ToolCallLogger.Log(nameof(UiCommand), new
                        {
                            commandType = "browser",
                            operation = normalizedOperation,
                            browserName = trimmedBrowserName,
                            lane
                        }, err, "browser executable missing");
                        return Task.FromResult(err);
                    }

                    return Task.FromResult(StartResolvedApplication(
                        commandType: "browser",
                        operation: normalizedOperation,
                        executablePath: browserCandidateForActivate.ExecutablePath,
                        arguments: string.Empty,
                        workingDirectory: browserCandidateForActivate.WorkingDirectory,
                        titleQuery: FirstNonEmpty(browserCandidateForActivate.DisplayName, trimmedBrowserName),
                        processQuery: browserCandidateForActivate.ProcessName,
                        timeoutMs: timeoutMs,
                        pollMs: pollMs,
                        metadata: new
                        {
                            browserName = trimmedBrowserName,
                            resolved = ToAppResult(browserCandidateForActivate)
                        },
                        lane: lane));
                }

                var targetUrl = string.Empty;
                if (string.Equals(normalizedOperation, "open_url", StringComparison.Ordinal) ||
                    string.Equals(normalizedOperation, "open_url_in_tab", StringComparison.Ordinal))
                {
                    targetUrl = (url ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(targetUrl))
                    {
                        var err = BuildError("UiCommand", $"browser.{normalizedOperation} 需要 url。");
                        ToolCallLogger.Log(nameof(UiCommand), new
                        {
                            commandType = "browser",
                            operation = normalizedOperation,
                            browserName,
                            url,
                            lane
                        }, err, "missing url");
                        return Task.FromResult(err);
                    }
                }
                else
                {
                    var trimmedQuery = (query ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(trimmedQuery))
                    {
                        var err = BuildError("UiCommand", "browser.search_web 需要 query。");
                        ToolCallLogger.Log(nameof(UiCommand), new
                        {
                            commandType = "browser",
                            operation = normalizedOperation,
                            browserName,
                            query,
                            site,
                            lane
                        }, err, "missing query");
                        return Task.FromResult(err);
                    }

                    if (string.Equals(normalizedOperation, "search_site", StringComparison.Ordinal) &&
                        string.IsNullOrWhiteSpace((site ?? string.Empty).Trim()))
                    {
                        var err = BuildError("UiCommand", "browser.search_site 需要 site。");
                        ToolCallLogger.Log(nameof(UiCommand), new
                        {
                            commandType = "browser",
                            operation = normalizedOperation,
                            browserName,
                            query,
                            site,
                            lane
                        }, err, "missing site");
                        return Task.FromResult(err);
                    }

                    targetUrl = BuildBrowserSearchUrl(trimmedQuery, site);
                }

                if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var uri) ||
                    !(string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
                {
                    var err = BuildError("UiCommand", "browser 类命令只支持 http/https 绝对网址。", new { url = targetUrl });
                    ToolCallLogger.Log(nameof(UiCommand), new
                    {
                        commandType = "browser",
                        operation = normalizedOperation,
                        browserName,
                        url = targetUrl,
                        query,
                        site,
                        lane
                    }, err, "invalid browser url");
                    return Task.FromResult(err);
                }

                if (string.IsNullOrWhiteSpace(trimmedBrowserName))
                {
                    return Task.FromResult(ExecuteShellCommandDirect(
                        operation: "open_url",
                        commandLine: targetUrl,
                        workingDirectory: string.Empty,
                        timeoutMs: timeoutMs,
                        pollMs: pollMs,
                        lane: lane));
                }

                var browserCandidate = ResolveInstalledApps(trimmedBrowserName, 5).FirstOrDefault();
                if (browserCandidate == null || string.IsNullOrWhiteSpace(browserCandidate.ExecutablePath))
                {
                    var err = BuildError("UiCommand", "未解析到可用浏览器路径。", new
                    {
                        browserName = trimmedBrowserName
                    });
                    ToolCallLogger.Log(nameof(UiCommand), new
                    {
                        commandType = "browser",
                        operation = normalizedOperation,
                        browserName = trimmedBrowserName,
                        url = targetUrl,
                        query,
                        site,
                        lane
                    }, err, "browser resolve failed");
                    return Task.FromResult(err);
                }

                if (!File.Exists(browserCandidate.ExecutablePath))
                {
                    var err = BuildError("UiCommand", "浏览器记录已解析，但可执行文件不存在。", new
                    {
                        browserName = trimmedBrowserName,
                        executablePath = browserCandidate.ExecutablePath
                    });
                    ToolCallLogger.Log(nameof(UiCommand), new
                    {
                        commandType = "browser",
                        operation = normalizedOperation,
                        browserName = trimmedBrowserName,
                        url = targetUrl,
                        query,
                        site,
                        lane
                    }, err, "browser executable missing");
                    return Task.FromResult(err);
                }

                return Task.FromResult(StartResolvedApplication(
                    commandType: "browser",
                    operation: normalizedOperation,
                    executablePath: browserCandidate.ExecutablePath,
                    arguments: targetUrl,
                    workingDirectory: browserCandidate.WorkingDirectory,
                    titleQuery: FirstNonEmpty(browserCandidate.DisplayName, trimmedBrowserName),
                    processQuery: browserCandidate.ProcessName,
                    timeoutMs: timeoutMs,
                    pollMs: pollMs,
                    metadata: new
                    {
                        browserName = trimmedBrowserName,
                        url = targetUrl,
                        query = query ?? string.Empty,
                        site = site ?? string.Empty,
                        resolved = ToAppResult(browserCandidate)
                    },
                    lane: lane));
            }).ConfigureAwait(false);
        }

        private static async Task<string> ExecuteClipboardCommandAsync(
            string operation,
            string text,
            string targetWindowTitleContains,
            int timeoutMs,
            int pollMs,
            string lane)
        {
            return await UiLaneScheduler.RunAsync(lane, () =>
            {
                var normalizedOperation = NormalizeClipboardOperation(operation);
                if (string.IsNullOrWhiteSpace(normalizedOperation))
                {
                    var err = BuildError("UiCommand", "clipboard.operation 仅支持 set_text、paste_text。", new { operation });
                    ToolCallLogger.Log(nameof(UiCommand), new
                    {
                        commandType = "clipboard",
                        operation,
                        text,
                        targetWindowTitleContains,
                        lane
                    }, err, "invalid operation");
                    return Task.FromResult(err);
                }

                var trimmedText = text ?? string.Empty;
                if (string.IsNullOrEmpty(trimmedText))
                {
                    var err = BuildError("UiCommand", $"clipboard.{normalizedOperation} 需要 text。");
                    ToolCallLogger.Log(nameof(UiCommand), new
                    {
                        commandType = "clipboard",
                        operation = normalizedOperation,
                        targetWindowTitleContains,
                        lane
                    }, err, "missing text");
                    return Task.FromResult(err);
                }

                if (!TrySetClipboardText(trimmedText, out var clipboardMessage))
                {
                    var err = BuildError("UiCommand", clipboardMessage);
                    ToolCallLogger.Log(nameof(UiCommand), new
                    {
                        commandType = "clipboard",
                        operation = normalizedOperation,
                        textLength = trimmedText.Length,
                        targetWindowTitleContains,
                        lane
                    }, err, "clipboard set failed");
                    return Task.FromResult(err);
                }

                if (string.Equals(normalizedOperation, "set_text", StringComparison.Ordinal))
                {
                    var payload = JsonConvert.SerializeObject(new
                    {
                        type = "ui.command",
                        ok = true,
                        commandType = "clipboard",
                        operation = normalizedOperation,
                        textLength = trimmedText.Length,
                        message = "剪贴板文本已设置。"
                    });
                    ToolCallLogger.Log(nameof(UiCommand), new
                    {
                        commandType = "clipboard",
                        operation = normalizedOperation,
                        textLength = trimmedText.Length,
                        lane
                    }, payload);
                    return Task.FromResult(payload);
                }

                if (!TryPasteClipboardText(targetWindowTitleContains, timeoutMs, pollMs, out var pasteMessage))
                {
                    var err = BuildError("UiCommand", pasteMessage, new { targetWindowTitleContains });
                    ToolCallLogger.Log(nameof(UiCommand), new
                    {
                        commandType = "clipboard",
                        operation = normalizedOperation,
                        textLength = trimmedText.Length,
                        targetWindowTitleContains,
                        lane
                    }, err, "clipboard paste failed");
                    return Task.FromResult(err);
                }

                var result = JsonConvert.SerializeObject(new
                {
                    type = "ui.command",
                    ok = true,
                    commandType = "clipboard",
                    operation = normalizedOperation,
                    textLength = trimmedText.Length,
                    targetWindowTitleContains = targetWindowTitleContains ?? string.Empty,
                    message = pasteMessage
                });
                ToolCallLogger.Log(nameof(UiCommand), new
                {
                    commandType = "clipboard",
                    operation = normalizedOperation,
                    textLength = trimmedText.Length,
                    targetWindowTitleContains,
                    lane
                }, result);
                return Task.FromResult(result);
            }).ConfigureAwait(false);
        }

        private static async Task<string> ExecuteExplorerCommandAsync(
            string operation,
            string commandLine,
            string fileName,
            int timeoutMs,
            int pollMs,
            string lane)
        {
            return await UiLaneScheduler.RunAsync(lane, () =>
            {
                var normalizedOperation = NormalizeExplorerOperation(operation);
                if (string.IsNullOrWhiteSpace(normalizedOperation))
                {
                    var err = BuildError("UiCommand", "explorer.operation 仅支持 open_path_and_wait、open_path_and_select。", new { operation });
                    ToolCallLogger.Log(nameof(UiCommand), new
                    {
                        commandType = "explorer",
                        operation,
                        commandLine,
                        fileName,
                        lane
                    }, err, "invalid operation");
                    return Task.FromResult(err);
                }

                var trimmedPath = (commandLine ?? string.Empty).Trim();
                var trimmedFileName = (fileName ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(trimmedPath))
                {
                    var err = BuildError("UiCommand", $"explorer.{normalizedOperation} 需要 commandLine。");
                    ToolCallLogger.Log(nameof(UiCommand), new
                    {
                        commandType = "explorer",
                        operation = normalizedOperation,
                        commandLine,
                        fileName,
                        lane
                    }, err, "missing commandLine");
                    return Task.FromResult(err);
                }

                if (string.Equals(normalizedOperation, "open_path_and_wait", StringComparison.Ordinal))
                {
                    if (!Directory.Exists(trimmedPath) &&
                        !File.Exists(trimmedPath) &&
                        !trimmedPath.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                    {
                        var err = BuildError("UiCommand", "open_path_and_wait 需要现有文件、目录或 shell: 路径。", new { commandLine = trimmedPath });
                        ToolCallLogger.Log(nameof(UiCommand), new
                        {
                            commandType = "explorer",
                            operation = normalizedOperation,
                            commandLine = trimmedPath,
                            lane
                        }, err, "path not found");
                        return Task.FromResult(err);
                    }

                    if (trimmedPath.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                    {
                        return Task.FromResult(ExecuteShellCommandDirect(
                            operation: "open_path",
                            commandLine: trimmedPath,
                            workingDirectory: string.Empty,
                            timeoutMs: timeoutMs,
                            pollMs: pollMs,
                            lane: lane));
                    }

                    var titleQuery = Path.GetFileName(trimmedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    if (string.IsNullOrWhiteSpace(titleQuery))
                        titleQuery = "资源管理器";

                    return Task.FromResult(StartResolvedApplication(
                        commandType: "explorer",
                        operation: normalizedOperation,
                        executablePath: "explorer.exe",
                        arguments: $"\"{trimmedPath}\"",
                        workingDirectory: ExtractExecutableDirectory(trimmedPath),
                        titleQuery: titleQuery,
                        processQuery: "explorer",
                        timeoutMs: timeoutMs,
                        pollMs: pollMs,
                        metadata: new
                        {
                            path = trimmedPath
                        },
                        lane: lane));
                }

                var selectPath = trimmedPath;
                if (Directory.Exists(trimmedPath))
                {
                    if (string.IsNullOrWhiteSpace(trimmedFileName))
                    {
                        var err = BuildError("UiCommand", "当 commandLine 为目录时，open_path_and_select 还需要 fileName。", new { commandLine = trimmedPath });
                        ToolCallLogger.Log(nameof(UiCommand), new
                        {
                            commandType = "explorer",
                            operation = normalizedOperation,
                            commandLine = trimmedPath,
                            fileName = trimmedFileName,
                            lane
                        }, err, "missing fileName");
                        return Task.FromResult(err);
                    }

                    selectPath = Path.Combine(trimmedPath, trimmedFileName);
                }

                if (!File.Exists(selectPath) && !Directory.Exists(selectPath))
                {
                    var err = BuildError("UiCommand", "open_path_and_select 需要可存在的文件或目录。", new { selectPath });
                    ToolCallLogger.Log(nameof(UiCommand), new
                    {
                        commandType = "explorer",
                        operation = normalizedOperation,
                        commandLine = trimmedPath,
                        fileName = trimmedFileName,
                        lane
                    }, err, "select path not found");
                    return Task.FromResult(err);
                }

                return Task.FromResult(StartResolvedApplication(
                    commandType: "explorer",
                    operation: normalizedOperation,
                    executablePath: "explorer.exe",
                    arguments: $"/select,\"{selectPath}\"",
                    workingDirectory: ExtractExecutableDirectory(selectPath),
                    titleQuery: Path.GetFileName(selectPath),
                    processQuery: "explorer",
                    timeoutMs: timeoutMs,
                    pollMs: pollMs,
                    metadata: new
                    {
                        path = trimmedPath,
                        fileName = trimmedFileName,
                        selectPath
                    },
                    lane: lane));
            }).ConfigureAwait(false);
        }

        private static async Task<string> ExecuteFileCommandAsync(
            string operation,
            string commandLine,
            string text,
            string fileName,
            string lane)
        {
            return await UiLaneScheduler.RunAsync(lane, () =>
            {
                var normalizedOperation = NormalizeFileOperation(operation);
                if (string.IsNullOrWhiteSpace(normalizedOperation))
                {
                    var err = BuildError("UiCommand", "file.operation 仅支持 create_directory、create_text_file、save_as、rename。", new { operation });
                    ToolCallLogger.Log(nameof(UiCommand), new
                    {
                        commandType = "file",
                        operation,
                        commandLine,
                        fileName,
                        lane
                    }, err, "invalid operation");
                    return Task.FromResult(err);
                }

                var targetPath = ExpandPath(commandLine);
                if (string.IsNullOrWhiteSpace(targetPath))
                {
                    var err = BuildError("UiCommand", $"file.{normalizedOperation} 需要 commandLine。");
                    ToolCallLogger.Log(nameof(UiCommand), new
                    {
                        commandType = "file",
                        operation = normalizedOperation,
                        commandLine,
                        fileName,
                        lane
                    }, err, "missing commandLine");
                    return Task.FromResult(err);
                }

                try
                {
                    if (string.Equals(normalizedOperation, "rename", StringComparison.Ordinal))
                    {
                        var newName = (fileName ?? string.Empty).Trim();
                        if (string.IsNullOrWhiteSpace(newName))
                        {
                            var err = BuildError("UiCommand", "file.rename 需要 fileName 作为新文件名。");
                            ToolCallLogger.Log(nameof(UiCommand), new
                            {
                                commandType = "file",
                                operation = normalizedOperation,
                                commandLine = targetPath,
                                fileName = newName,
                                lane
                            }, err, "missing new name");
                            return Task.FromResult(err);
                        }

                        if (!File.Exists(targetPath) && !Directory.Exists(targetPath))
                        {
                            var err = BuildError("UiCommand", "待重命名的文件或目录不存在。", new { commandLine = targetPath });
                            ToolCallLogger.Log(nameof(UiCommand), new
                            {
                                commandType = "file",
                                operation = normalizedOperation,
                                commandLine = targetPath,
                                fileName = newName,
                                lane
                            }, err, "source not found");
                            return Task.FromResult(err);
                        }

                        var parent = Path.GetDirectoryName(targetPath) ?? string.Empty;
                        var destinationPath = Path.Combine(parent, newName);
                        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
                        {
                            var err = BuildError("UiCommand", "目标名称已存在。", new { destinationPath });
                            ToolCallLogger.Log(nameof(UiCommand), new
                            {
                                commandType = "file",
                                operation = normalizedOperation,
                                commandLine = targetPath,
                                fileName = newName,
                                lane
                            }, err, "destination exists");
                            return Task.FromResult(err);
                        }

                        if (File.Exists(targetPath))
                            File.Move(targetPath, destinationPath);
                        else
                            Directory.Move(targetPath, destinationPath);

                        var result = JsonConvert.SerializeObject(new
                        {
                            type = "ui.command",
                            ok = true,
                            lane = UiLaneScheduler.NormalizeLane(lane),
                            commandType = "file",
                            operation = normalizedOperation,
                            sourcePath = targetPath,
                            destinationPath,
                            message = "文件或目录已重命名。"
                        });
                        ToolCallLogger.Log(nameof(UiCommand), new
                        {
                            commandType = "file",
                            operation = normalizedOperation,
                            commandLine = targetPath,
                            fileName = newName,
                            lane
                        }, result);
                        return Task.FromResult(result);
                    }

                    if (string.Equals(normalizedOperation, "create_directory", StringComparison.Ordinal))
                    {
                        if (File.Exists(targetPath))
                        {
                            var err = BuildError("UiCommand", "目标路径已存在同名文件，无法创建目录。", new { commandLine = targetPath });
                            ToolCallLogger.Log(nameof(UiCommand), new
                            {
                                commandType = "file",
                                operation = normalizedOperation,
                                commandLine = targetPath,
                                lane
                            }, err, "file exists at directory path");
                            return Task.FromResult(err);
                        }

                        var alreadyExists = Directory.Exists(targetPath);
                        Directory.CreateDirectory(targetPath);
                        var result = JsonConvert.SerializeObject(new
                        {
                            type = "ui.command",
                            ok = true,
                            lane = UiLaneScheduler.NormalizeLane(lane),
                            commandType = "file",
                            operation = normalizedOperation,
                            path = targetPath,
                            created = !alreadyExists,
                            message = alreadyExists ? "目录已存在，无需重复创建。" : "目录已创建。"
                        });
                        ToolCallLogger.Log(nameof(UiCommand), new
                        {
                            commandType = "file",
                            operation = normalizedOperation,
                            commandLine = targetPath,
                            lane
                        }, result);
                        return Task.FromResult(result);
                    }

                    var parentDirectory = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrWhiteSpace(parentDirectory))
                        Directory.CreateDirectory(parentDirectory);

                    if (string.Equals(normalizedOperation, "create_text_file", StringComparison.Ordinal))
                    {
                        if (File.Exists(targetPath))
                        {
                            var err = BuildError("UiCommand", "目标文件已存在，请改用 file.save_as。", new { commandLine = targetPath });
                            ToolCallLogger.Log(nameof(UiCommand), new
                            {
                                commandType = "file",
                                operation = normalizedOperation,
                                commandLine = targetPath,
                                lane
                            }, err, "file exists");
                            return Task.FromResult(err);
                        }

                        File.WriteAllText(targetPath, text ?? string.Empty, new UTF8Encoding(false));
                        var result = JsonConvert.SerializeObject(new
                        {
                            type = "ui.command",
                            ok = true,
                            lane = UiLaneScheduler.NormalizeLane(lane),
                            commandType = "file",
                            operation = normalizedOperation,
                            path = targetPath,
                            textLength = text?.Length ?? 0,
                            message = "文本文件已创建。"
                        });
                        ToolCallLogger.Log(nameof(UiCommand), new
                        {
                            commandType = "file",
                            operation = normalizedOperation,
                            commandLine = targetPath,
                            textLength = text?.Length ?? 0,
                            lane
                        }, result);
                        return Task.FromResult(result);
                    }

                    if (string.IsNullOrEmpty(text))
                    {
                        var err = BuildError("UiCommand", "file.save_as 需要 text。");
                        ToolCallLogger.Log(nameof(UiCommand), new
                        {
                            commandType = "file",
                            operation = normalizedOperation,
                            commandLine = targetPath,
                            lane
                        }, err, "missing text");
                        return Task.FromResult(err);
                    }

                    File.WriteAllText(targetPath, text, new UTF8Encoding(false));
                    var payload = JsonConvert.SerializeObject(new
                    {
                        type = "ui.command",
                        ok = true,
                        lane = UiLaneScheduler.NormalizeLane(lane),
                        commandType = "file",
                        operation = normalizedOperation,
                        path = targetPath,
                        textLength = text.Length,
                        message = "文件已保存。"
                    });
                    ToolCallLogger.Log(nameof(UiCommand), new
                    {
                        commandType = "file",
                        operation = normalizedOperation,
                        commandLine = targetPath,
                        textLength = text.Length,
                        lane
                    }, payload);
                    return Task.FromResult(payload);
                }
                catch (Exception ex)
                {
                    var err = BuildError("UiCommand", ex.Message, new
                    {
                        commandType = "file",
                        operation = normalizedOperation,
                        commandLine = targetPath,
                        fileName
                    });
                    ToolCallLogger.Log(nameof(UiCommand), new
                    {
                        commandType = "file",
                        operation = normalizedOperation,
                        commandLine = targetPath,
                        fileName,
                        lane
                    }, err, ex.Message);
                    return Task.FromResult(err);
                }
            }).ConfigureAwait(false);
        }

        private static async Task<string> ExecuteKeyboardCommandAsync(
            string operation,
            string text,
            string[] keys,
            string[] modifiers,
            string targetWindowTitleContains,
            int timeoutMs,
            int pollMs,
            string lane)
        {
            return await UiLaneScheduler.RunAsync(lane, () =>
            {
                var normalizedOperation = NormalizeKeyboardOperation(operation);
                if (string.IsNullOrWhiteSpace(normalizedOperation))
                {
                    var err = BuildError("UiCommand", "keyboard.operation 仅支持 type_text、focus_type_text、key_press、hotkey。", new { operation });
                    ToolCallLogger.Log(nameof(UiCommand), new
                    {
                        commandType = "keyboard",
                        operation,
                        text,
                        keys,
                        modifiers,
                        targetWindowTitleContains,
                        timeoutMs,
                        pollMs,
                        lane
                    }, err, "invalid operation");
                    return Task.FromResult(err);
                }

                var timeout = Math.Clamp(timeoutMs, 200, 120000);
                var poll = Math.Clamp(pollMs, 50, 2000);
                WindowNode targetWindow = null;
                WindowNode focusTarget = null;
                var focusStrategy = string.Empty;
                if (!string.IsNullOrWhiteSpace(targetWindowTitleContains))
                {
                    if (!TryFindWindowByTitle(targetWindowTitleContains, out targetWindow))
                    {
                        var err = BuildError("UiCommand", "未匹配到目标窗口。", new { targetWindowTitleContains });
                        ToolCallLogger.Log(nameof(UiCommand), new
                        {
                            commandType = "keyboard",
                            operation = normalizedOperation,
                            text,
                            keys,
                            modifiers,
                            targetWindowTitleContains,
                            timeoutMs = timeout,
                            pollMs = poll,
                            lane
                        }, err, "target window not found");
                        return Task.FromResult(err);
                    }
                }

                bool executed;
                string message;
                switch (normalizedOperation)
                {
                    case "type_text":
                    case "focus_type_text":
                        if (string.IsNullOrWhiteSpace(text))
                        {
                            var err = BuildError("UiCommand", "keyboard.type_text 需要 text。");
                            ToolCallLogger.Log(nameof(UiCommand), new
                            {
                                commandType = "keyboard",
                                operation = normalizedOperation,
                                text,
                                targetWindowTitleContains,
                                lane
                            }, err, "missing text");
                            return Task.FromResult(err);
                        }

                        if (string.Equals(normalizedOperation, "focus_type_text", StringComparison.Ordinal) &&
                            string.IsNullOrWhiteSpace(targetWindowTitleContains))
                        {
                            var err = BuildError("UiCommand", "keyboard.focus_type_text 需要 targetWindowTitleContains。");
                            ToolCallLogger.Log(nameof(UiCommand), new
                            {
                                commandType = "keyboard",
                                operation = normalizedOperation,
                                text,
                                targetWindowTitleContains,
                                lane
                            }, err, "missing targetWindowTitleContains");
                            return Task.FromResult(err);
                        }

                        if (targetWindow != null)
                        {
                            var requireInputFocus = string.Equals(normalizedOperation, "focus_type_text", StringComparison.Ordinal);
                            if (!PrepareWindowForKeyboardInput(
                                    targetWindow,
                                    timeout,
                                    poll,
                                    requireInputFocus,
                                    out focusTarget,
                                    out focusStrategy,
                                    out var prepareMessage))
                            {
                                var err = BuildError("UiCommand", prepareMessage, new
                                {
                                    targetWindowTitleContains,
                                    hwnd = targetWindow.Hwnd,
                                    focusStrategy
                                });
                                ToolCallLogger.Log(nameof(UiCommand), new
                                {
                                    commandType = "keyboard",
                                    operation = normalizedOperation,
                                    text,
                                    targetWindowTitleContains,
                                    timeoutMs = timeout,
                                    pollMs = poll,
                                    lane
                                }, err, "prepare input focus failed");
                                return Task.FromResult(err);
                            }

                            Thread.Sleep(requireInputFocus ? 220 : 80);
                        }

                        executed = TrySendText(text, targetWindow, focusTarget, out message);
                        break;
                    case "key_press":
                    {
                        if (targetWindow != null)
                        {
                            if (!ActivateWindow(targetWindow, timeout, poll, out var activateMessage))
                            {
                                var err = BuildError("UiCommand", activateMessage, new { targetWindowTitleContains, hwnd = targetWindow.Hwnd });
                                ToolCallLogger.Log(nameof(UiCommand), new
                                {
                                    commandType = "keyboard",
                                    operation = normalizedOperation,
                                    keys,
                                    modifiers,
                                    targetWindowTitleContains,
                                    timeoutMs = timeout,
                                    pollMs = poll,
                                    lane
                                }, err, "target window activate failed");
                                return Task.FromResult(err);
                            }

                            Thread.Sleep(60);
                        }

                        if (!TryResolveVirtualKeys(keys, out var keyVks, out var keyError))
                        {
                            var err = BuildError("UiCommand", keyError);
                            ToolCallLogger.Log(nameof(UiCommand), new
                            {
                                commandType = "keyboard",
                                operation = normalizedOperation,
                                keys,
                                modifiers,
                                targetWindowTitleContains,
                                lane
                            }, err, "invalid keys");
                            return Task.FromResult(err);
                        }

                        if (!TryResolveOptionalVirtualKeys(modifiers, out var modifierVks, out var modifierError))
                        {
                            var err = BuildError("UiCommand", modifierError);
                            ToolCallLogger.Log(nameof(UiCommand), new
                            {
                                commandType = "keyboard",
                                operation = normalizedOperation,
                                keys,
                                modifiers,
                                targetWindowTitleContains,
                                lane
                            }, err, "invalid modifiers");
                            return Task.FromResult(err);
                        }

                        executed = ExecuteKeySequence(modifierVks, keyVks, out message);
                        break;
                    }
                    case "hotkey":
                    {
                        if (targetWindow != null)
                        {
                            if (!ActivateWindow(targetWindow, timeout, poll, out var activateMessage))
                            {
                                var err = BuildError("UiCommand", activateMessage, new { targetWindowTitleContains, hwnd = targetWindow.Hwnd });
                                ToolCallLogger.Log(nameof(UiCommand), new
                                {
                                    commandType = "keyboard",
                                    operation = normalizedOperation,
                                    keys,
                                    modifiers,
                                    targetWindowTitleContains,
                                    timeoutMs = timeout,
                                    pollMs = poll,
                                    lane
                                }, err, "target window activate failed");
                                return Task.FromResult(err);
                            }

                            Thread.Sleep(60);
                        }

                        if (!TryResolveVirtualKeys(keys, out var keyVks, out var keyError))
                        {
                            var err = BuildError("UiCommand", keyError);
                            ToolCallLogger.Log(nameof(UiCommand), new
                            {
                                commandType = "keyboard",
                                operation = normalizedOperation,
                                keys,
                                modifiers,
                                targetWindowTitleContains,
                                lane
                            }, err, "invalid keys");
                            return Task.FromResult(err);
                        }

                        if (!TryResolveOptionalVirtualKeys(modifiers, out var modifierVks, out var modifierError))
                        {
                            var err = BuildError("UiCommand", modifierError);
                            ToolCallLogger.Log(nameof(UiCommand), new
                            {
                                commandType = "keyboard",
                                operation = normalizedOperation,
                                keys,
                                modifiers,
                                targetWindowTitleContains,
                                lane
                            }, err, "invalid modifiers");
                            return Task.FromResult(err);
                        }

                        executed = ExecuteHotkey(modifierVks, keyVks, out message);
                        break;
                    }
                    default:
                        executed = false;
                        message = "未实现的键盘动作。";
                        break;
                }

                var result = JsonConvert.SerializeObject(new
                {
                    type = "ui.command",
                    ok = executed,
                    lane = UiLaneScheduler.NormalizeLane(lane),
                    commandType = "keyboard",
                    operation = normalizedOperation,
                    targetWindowTitleContains = targetWindowTitleContains ?? string.Empty,
                    targetWindowTitle = targetWindow?.Title ?? string.Empty,
                    targetHwnd = targetWindow?.Hwnd ?? 0,
                    focusTargetHwnd = focusTarget?.Hwnd ?? targetWindow?.Hwnd ?? 0,
                    focusTargetClassName = focusTarget?.ClassName ?? string.Empty,
                    focusStrategy = focusStrategy ?? string.Empty,
                    textLength = text?.Length ?? 0,
                    keys = keys ?? Array.Empty<string>(),
                    modifiers = modifiers ?? Array.Empty<string>(),
                    message
                });
                ToolCallLogger.Log(nameof(UiCommand), new
                {
                    commandType = "keyboard",
                    operation = normalizedOperation,
                    text,
                    keys,
                    modifiers,
                    targetWindowTitleContains,
                    timeoutMs = timeout,
                    pollMs = poll,
                    lane
                }, result, executed ? null : message);
                return Task.FromResult(result);
            }).ConfigureAwait(false);
        }

        private static async Task<string> ExecuteWindowCommandAsync(
            string operation,
            string targetWindowTitleContains,
            bool confirmed,
            int timeoutMs,
            int pollMs,
            string lane)
        {
            return await UiLaneScheduler.RunAsync(lane, async () =>
            {
                var normalizedOperation = NormalizeWindowOperation(operation);
                if (string.IsNullOrWhiteSpace(normalizedOperation))
                {
                    var err = BuildError("UiCommand", "window.operation 仅支持 activate、wait_appear、wait_disappear、close、list、find_best_match。", new { operation });
                    ToolCallLogger.Log(nameof(UiCommand), new
                    {
                        commandType = "window",
                        operation,
                        targetWindowTitleContains,
                        confirmed,
                        timeoutMs,
                        pollMs,
                        lane
                    }, err, "invalid operation");
                    return err;
                }

                if (string.Equals(normalizedOperation, "list", StringComparison.Ordinal))
                {
                    var windows = GetTopWindows(forceRefresh: true);
                    var result = JsonConvert.SerializeObject(new
                    {
                        type = "ui.command",
                        ok = true,
                        lane = UiLaneScheduler.NormalizeLane(lane),
                        commandType = "window",
                        operation = normalizedOperation,
                        count = windows.Count,
                        windows = windows.Take(20).Select(ToWindowResult).ToList()
                    });
                    ToolCallLogger.Log(nameof(UiCommand), new
                    {
                        commandType = "window",
                        operation = normalizedOperation,
                        lane
                    }, result);
                    return result;
                }

                if (string.Equals(normalizedOperation, "find_best_match", StringComparison.Ordinal))
                {
                    if (string.IsNullOrWhiteSpace(targetWindowTitleContains))
                    {
                        var err = BuildError("UiCommand", "window.find_best_match 需要 targetWindowTitleContains。");
                        ToolCallLogger.Log(nameof(UiCommand), new
                        {
                            commandType = "window",
                            operation = normalizedOperation,
                            targetWindowTitleContains,
                            lane
                        }, err, "missing targetWindowTitleContains");
                        return err;
                    }

                    var windows = GetTopWindows(forceRefresh: true);
                    var titleVariants = ExpandAppQueryVariants(targetWindowTitleContains);
                    var bestMatches = windows
                        .Select(x => new
                        {
                            Window = x,
                            Score = ScoreWindowCandidate(x, Array.Empty<string>(), titleVariants)
                        })
                        .Where(x => x.Score > 0)
                        .OrderByDescending(x => x.Score)
                        .ThenByDescending(x => x.Window.Visible)
                        .Take(5)
                        .ToList();

                    if (bestMatches.Count == 0)
                    {
                        var err = BuildError("UiCommand", "未匹配到目标窗口。", new { targetWindowTitleContains });
                        ToolCallLogger.Log(nameof(UiCommand), new
                        {
                            commandType = "window",
                            operation = normalizedOperation,
                            targetWindowTitleContains,
                            lane
                        }, err, "window not found");
                        return err;
                    }

                    var payload = JsonConvert.SerializeObject(new
                    {
                        type = "ui.command",
                        ok = true,
                        lane = UiLaneScheduler.NormalizeLane(lane),
                        commandType = "window",
                        operation = normalizedOperation,
                        targetWindowTitleContains,
                        bestMatch = ToWindowResult(bestMatches[0].Window),
                        matches = bestMatches.Select(x => new
                        {
                            score = x.Score,
                            window = ToWindowResult(x.Window)
                        }).ToList()
                    });
                    ToolCallLogger.Log(nameof(UiCommand), new
                    {
                        commandType = "window",
                        operation = normalizedOperation,
                        targetWindowTitleContains,
                        lane
                    }, payload);
                    return payload;
                }

                if (string.IsNullOrWhiteSpace(targetWindowTitleContains))
                {
                    var err = BuildError("UiCommand", "window 类命令需要 targetWindowTitleContains。");
                    ToolCallLogger.Log(nameof(UiCommand), new
                    {
                        commandType = "window",
                        operation = normalizedOperation,
                        targetWindowTitleContains,
                        confirmed,
                        timeoutMs,
                        pollMs,
                        lane
                    }, err, "missing targetWindowTitleContains");
                    return err;
                }

                var timeout = Math.Clamp(timeoutMs, 200, 120000);
                var poll = Math.Clamp(pollMs, 50, 2000);

                if (normalizedOperation == "wait_appear")
                {
                    var result = await UiWaitWindow(
                        titleContains: targetWindowTitleContains,
                        timeoutMs: timeout,
                        pollMs: poll,
                        lane: lane).ConfigureAwait(false);
                    return WrapWindowCommandResult(result, normalizedOperation, targetWindowTitleContains);
                }

                if (normalizedOperation == "wait_disappear")
                {
                    var ok = WaitUntil(
                        () => !TryFindWindowByTitle(targetWindowTitleContains, out _),
                        timeout,
                        poll);
                    var result = JsonConvert.SerializeObject(new
                    {
                        type = "ui.command",
                        ok,
                        lane = UiLaneScheduler.NormalizeLane(lane),
                        commandType = "window",
                        operation = normalizedOperation,
                        targetWindowTitleContains,
                        message = ok ? "目标窗口已消失。" : "超时等待窗口消失。"
                    });
                    ToolCallLogger.Log(nameof(UiCommand), new
                    {
                        commandType = "window",
                        operation = normalizedOperation,
                        targetWindowTitleContains,
                        timeoutMs = timeout,
                        pollMs = poll,
                        lane
                    }, result, ok ? null : "timeout");
                    return result;
                }

                if (!TryFindWindowByTitle(targetWindowTitleContains, out var targetWindow))
                {
                    var err = BuildError("UiCommand", "未匹配到目标窗口。", new { targetWindowTitleContains });
                    ToolCallLogger.Log(nameof(UiCommand), new
                    {
                        commandType = "window",
                        operation = normalizedOperation,
                        targetWindowTitleContains,
                        confirmed,
                        timeoutMs = timeout,
                        pollMs = poll,
                        lane
                    }, err, "target window not found");
                    return err;
                }

                if (normalizedOperation == "activate")
                {
                    var ok = ActivateWindow(targetWindow, timeout, poll, out var message, maximizeWindow: true);
                    var result = JsonConvert.SerializeObject(new
                    {
                        type = "ui.command",
                        ok,
                        lane = UiLaneScheduler.NormalizeLane(lane),
                        commandType = "window",
                        operation = normalizedOperation,
                        targetWindowTitleContains,
                        hwnd = targetWindow.Hwnd,
                        title = targetWindow.Title,
                        message
                    });
                    ToolCallLogger.Log(nameof(UiCommand), new
                    {
                        commandType = "window",
                        operation = normalizedOperation,
                        targetWindowTitleContains,
                        timeoutMs = timeout,
                        pollMs = poll,
                        lane
                    }, result, ok ? null : message);
                    return result;
                }

                if (!confirmed)
                {
                    var err = BuildError("UiCommand", "window.close 为高风险动作，需 confirmed=true。", new { targetWindowTitleContains });
                    ToolCallLogger.Log(nameof(UiCommand), new
                    {
                        commandType = "window",
                        operation = normalizedOperation,
                        targetWindowTitleContains,
                        confirmed,
                        timeoutMs = timeout,
                        pollMs = poll,
                        lane
                    }, err, "missing confirmed");
                    return err;
                }

                var handle = new IntPtr(targetWindow.Hwnd);
                _ = SendMessage(handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                var closeOk = WaitUntil(() => !IsWindow(handle) || !IsWindowVisible(handle), timeout, poll);
                var closeResult = JsonConvert.SerializeObject(new
                {
                    type = "ui.command",
                    ok = closeOk,
                    lane = UiLaneScheduler.NormalizeLane(lane),
                    commandType = "window",
                    operation = normalizedOperation,
                    targetWindowTitleContains,
                    hwnd = targetWindow.Hwnd,
                    title = targetWindow.Title,
                    confirmed,
                    message = closeOk ? "目标窗口已关闭。" : "关闭后窗口仍存在。"
                });
                ToolCallLogger.Log(nameof(UiCommand), new
                {
                    commandType = "window",
                    operation = normalizedOperation,
                    targetWindowTitleContains,
                    confirmed,
                    timeoutMs = timeout,
                    pollMs = poll,
                    lane
                }, closeResult, closeOk ? null : "close verify failed");
                return closeResult;
            }).ConfigureAwait(false);
        }

        private static async Task<string> ExecuteShellCommandAsync(
            string operation,
            string commandLine,
            string workingDirectory,
            int timeoutMs,
            int pollMs,
            string lane)
        {
            return await UiLaneScheduler.RunAsync(lane, () =>
            {
                var normalizedOperation = NormalizeShellOperation(operation);
                if (string.IsNullOrWhiteSpace(normalizedOperation))
                {
                    var err = BuildError("UiCommand", "shell.operation 仅支持 open_path、launch_app、open_url。", new { operation });
                    ToolCallLogger.Log(nameof(UiCommand), new
                    {
                        commandType = "shell",
                        operation,
                        commandLine,
                        workingDirectory,
                        lane
                    }, err, "invalid operation");
                    return Task.FromResult(err);
                }

                var trimmedCommand = (commandLine ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(trimmedCommand))
                {
                    var err = BuildError("UiCommand", $"shell.{normalizedOperation} 需要 commandLine。");
                    ToolCallLogger.Log(nameof(UiCommand), new
                    {
                        commandType = "shell",
                        operation = normalizedOperation,
                        commandLine,
                        workingDirectory,
                        lane
                    }, err, "missing commandLine");
                    return Task.FromResult(err);
                }

                try
                {
                    Process process;
                    var timeout = Math.Clamp(timeoutMs, 200, 120000);
                    var poll = Math.Clamp(pollMs, 50, 2000);
                    switch (normalizedOperation)
                    {
                        case "open_path":
                            if (!Directory.Exists(trimmedCommand) &&
                                !File.Exists(trimmedCommand) &&
                                !trimmedCommand.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                            {
                                var err = BuildError("UiCommand", "open_path 需要现有文件、目录或 shell: 路径。", new { commandLine = trimmedCommand });
                                ToolCallLogger.Log(nameof(UiCommand), new
                                {
                                    commandType = "shell",
                                    operation = normalizedOperation,
                                    commandLine = trimmedCommand,
                                    workingDirectory,
                                    lane
                                }, err, "path not found");
                                return Task.FromResult(err);
                            }
                            process = Process.Start(new ProcessStartInfo
                            {
                                FileName = trimmedCommand,
                                WorkingDirectory = NormalizeWorkingDirectory(workingDirectory),
                                UseShellExecute = true,
                                WindowStyle = ProcessWindowStyle.Maximized
                            });
                            break;
                        case "open_url":
                            if (!Uri.TryCreate(trimmedCommand, UriKind.Absolute, out var uri) ||
                                !(string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
                            {
                                var err = BuildError("UiCommand", "open_url 仅支持 http/https 绝对网址。", new { commandLine = trimmedCommand });
                                ToolCallLogger.Log(nameof(UiCommand), new
                                {
                                    commandType = "shell",
                                    operation = normalizedOperation,
                                    commandLine = trimmedCommand,
                                    workingDirectory,
                                    lane
                                }, err, "invalid url");
                                return Task.FromResult(err);
                            }
                            process = Process.Start(new ProcessStartInfo
                            {
                                FileName = trimmedCommand,
                                UseShellExecute = true,
                                WindowStyle = ProcessWindowStyle.Maximized
                            });
                            break;
                        default:
                            process = Process.Start(new ProcessStartInfo
                            {
                                FileName = trimmedCommand,
                                WorkingDirectory = NormalizeWorkingDirectory(workingDirectory),
                                UseShellExecute = true,
                                WindowStyle = ProcessWindowStyle.Maximized
                            });
                            break;
                    }

                    var processReturned = process != null;
                    var activatedWindow = false;
                    var activatedHwnd = 0L;
                    var activatedTitle = string.Empty;
                    var activateMessage = string.Empty;
                    if (normalizedOperation == "launch_app" && processReturned)
                    {
                        activatedWindow = TryActivateLaunchedProcessWindow(
                            process,
                            timeout,
                            poll,
                            out activatedHwnd,
                            out activatedTitle,
                            out activateMessage,
                            maximizeWindow: true);
                    }

                    var ok = true;
                    var result = JsonConvert.SerializeObject(new
                    {
                        type = "ui.command",
                        ok,
                        lane = UiLaneScheduler.NormalizeLane(lane),
                        commandType = "shell",
                        operation = normalizedOperation,
                        commandLine = trimmedCommand,
                        workingDirectory = NormalizeWorkingDirectory(workingDirectory),
                        processReturned,
                        processId = process?.Id ?? 0,
                        activatedWindow,
                        activatedHwnd,
                        activatedTitle,
                        activateMessage,
                        message = processReturned
                            ? (activatedWindow ? $"命令已启动，{activateMessage}" : "命令已启动。")
                            : "系统已受理打开请求，未返回进程句柄。"
                    });
                    ToolCallLogger.Log(nameof(UiCommand), new
                    {
                        commandType = "shell",
                        operation = normalizedOperation,
                        commandLine = trimmedCommand,
                        workingDirectory,
                        timeoutMs = timeout,
                        pollMs = poll,
                        lane
                    }, result);
                    return Task.FromResult(result);
                }
                catch (Exception ex)
                {
                    var err = BuildError("UiCommand", ex.Message, new
                    {
                        commandType = "shell",
                        operation = normalizedOperation,
                        commandLine = trimmedCommand,
                        workingDirectory
                    });
                    ToolCallLogger.Log(nameof(UiCommand), new
                    {
                        commandType = "shell",
                        operation = normalizedOperation,
                        commandLine = trimmedCommand,
                        workingDirectory,
                        lane
                    }, err, ex.Message);
                    return Task.FromResult(err);
                }
            }).ConfigureAwait(false);
        }

        private static string ExecuteShellCommandDirect(
            string operation,
            string commandLine,
            string workingDirectory,
            int timeoutMs,
            int pollMs,
            string lane)
        {
            var normalizedOperation = NormalizeShellOperation(operation);
            if (string.IsNullOrWhiteSpace(normalizedOperation))
                return BuildError("UiCommand", "shell.operation 仅支持 open_path、launch_app、open_url。", new { operation });

            var trimmedCommand = (commandLine ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(trimmedCommand))
                return BuildError("UiCommand", $"shell.{normalizedOperation} 需要 commandLine。");

            try
            {
                Process process;
                var timeout = Math.Clamp(timeoutMs, 200, 120000);
                var poll = Math.Clamp(pollMs, 50, 2000);
                switch (normalizedOperation)
                {
                    case "open_path":
                        if (!Directory.Exists(trimmedCommand) &&
                            !File.Exists(trimmedCommand) &&
                            !trimmedCommand.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                        {
                            return BuildError("UiCommand", "open_path 需要现有文件、目录或 shell: 路径。", new { commandLine = trimmedCommand });
                        }

                        process = Process.Start(new ProcessStartInfo
                        {
                            FileName = trimmedCommand,
                            WorkingDirectory = NormalizeWorkingDirectory(workingDirectory),
                            UseShellExecute = true,
                            WindowStyle = ProcessWindowStyle.Maximized
                        });
                        break;
                    case "open_url":
                        if (!Uri.TryCreate(trimmedCommand, UriKind.Absolute, out var uri) ||
                            !(string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
                        {
                            return BuildError("UiCommand", "open_url 仅支持 http/https 绝对网址。", new { commandLine = trimmedCommand });
                        }

                        process = Process.Start(new ProcessStartInfo
                        {
                            FileName = trimmedCommand,
                            UseShellExecute = true,
                            WindowStyle = ProcessWindowStyle.Maximized
                        });
                        break;
                    default:
                        process = Process.Start(new ProcessStartInfo
                        {
                            FileName = trimmedCommand,
                            WorkingDirectory = NormalizeWorkingDirectory(workingDirectory),
                            UseShellExecute = true,
                            WindowStyle = ProcessWindowStyle.Maximized
                        });
                        break;
                }

                var processReturned = process != null;
                var activatedWindow = false;
                var activatedHwnd = 0L;
                var activatedTitle = string.Empty;
                var activateMessage = string.Empty;
                if (normalizedOperation == "launch_app" && processReturned)
                {
                    activatedWindow = TryActivateLaunchedProcessWindow(
                        process,
                        timeout,
                        poll,
                        out activatedHwnd,
                        out activatedTitle,
                        out activateMessage,
                        maximizeWindow: true);
                }

                return JsonConvert.SerializeObject(new
                {
                    type = "ui.command",
                    ok = true,
                    lane = UiLaneScheduler.NormalizeLane(lane),
                    commandType = "shell",
                    operation = normalizedOperation,
                    commandLine = trimmedCommand,
                    workingDirectory = NormalizeWorkingDirectory(workingDirectory),
                    processReturned,
                    processId = process?.Id ?? 0,
                    activatedWindow,
                    activatedHwnd,
                    activatedTitle,
                    activateMessage,
                    message = processReturned
                        ? (activatedWindow ? $"命令已启动，{activateMessage}" : "命令已启动。")
                        : "系统已受理打开请求，未返回进程句柄。"
                });
            }
            catch (Exception ex)
            {
                return BuildError("UiCommand", ex.Message, new
                {
                    commandType = "shell",
                    operation = normalizedOperation,
                    commandLine = trimmedCommand,
                    workingDirectory
                });
            }
        }

        private static string StartResolvedApplication(
            string commandType,
            string operation,
            string executablePath,
            string arguments,
            string workingDirectory,
            string titleQuery,
            string processQuery,
            int timeoutMs,
            int pollMs,
            object metadata,
            string lane)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = executablePath,
                    WorkingDirectory = NormalizeWorkingDirectory(workingDirectory),
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Maximized
                };
                if (!string.IsNullOrWhiteSpace(arguments))
                    psi.Arguments = arguments;

                var process = Process.Start(psi);
                var timeout = Math.Clamp(timeoutMs, 200, 120000);
                var poll = Math.Clamp(pollMs, 50, 2000);
                var processReturned = process != null;
                var activatedWindow = false;
                var activatedHwnd = 0L;
                var activatedTitle = string.Empty;
                var activateMessage = string.Empty;

                if (processReturned)
                {
                    activatedWindow = TryActivateLaunchedProcessWindow(
                        process,
                        timeout,
                        poll,
                        out activatedHwnd,
                        out activatedTitle,
                        out activateMessage,
                        maximizeWindow: true);
                }

                if (!activatedWindow &&
                    TryFindWindowByProcessOrTitle(processQuery, titleQuery, out var fallbackWindow))
                {
                    activatedWindow = ActivateWindow(fallbackWindow, timeout, poll, out activateMessage, maximizeWindow: true);
                    activatedHwnd = fallbackWindow.Hwnd;
                    activatedTitle = fallbackWindow.Title;
                }

                return JsonConvert.SerializeObject(new
                {
                    type = "ui.command",
                    ok = true,
                    lane = UiLaneScheduler.NormalizeLane(lane),
                    commandType,
                    operation,
                    executablePath,
                    arguments = arguments ?? string.Empty,
                    workingDirectory = NormalizeWorkingDirectory(workingDirectory),
                    processReturned,
                    processId = process?.Id ?? 0,
                    activatedWindow,
                    activatedHwnd,
                    activatedTitle,
                    activateMessage,
                    metadata,
                    message = processReturned
                        ? (activatedWindow ? $"命令已启动，{activateMessage}" : "命令已启动。")
                        : "系统已受理打开请求，未返回进程句柄。"
                });
            }
            catch (Exception ex)
            {
                return BuildError("UiCommand", ex.Message, new
                {
                    commandType,
                    operation,
                    executablePath,
                    arguments,
                    workingDirectory,
                    metadata
                });
            }
        }

        private static List<InstalledAppCandidate> GetInstalledApps(bool forceRefresh)
        {
            var now = DateTime.UtcNow;
            lock (s_appCacheLock)
            {
                if (!forceRefresh &&
                    s_cachedInstalledApps.Count > 0 &&
                    (now - s_cachedInstalledAppsAtUtc) <= InstalledAppCacheTtl)
                {
                    return s_cachedInstalledApps.Select(x => x.Clone()).ToList();
                }
            }

            var fresh = LoadInstalledApps();
            lock (s_appCacheLock)
            {
                s_cachedInstalledApps = fresh.Select(x => x.Clone()).ToList();
                s_cachedInstalledAppsAtUtc = now;
                return s_cachedInstalledApps.Select(x => x.Clone()).ToList();
            }
        }

        private static List<InstalledAppCandidate> LoadInstalledApps()
        {
            var candidates = new List<InstalledAppCandidate>();

            ReadAppPathCandidates(candidates, RegistryHive.CurrentUser, RegistryView.Registry64);
            ReadAppPathCandidates(candidates, RegistryHive.LocalMachine, RegistryView.Registry64);
            ReadAppPathCandidates(candidates, RegistryHive.CurrentUser, RegistryView.Registry32);
            ReadAppPathCandidates(candidates, RegistryHive.LocalMachine, RegistryView.Registry32);

            ReadUninstallCandidates(candidates, RegistryHive.CurrentUser, RegistryView.Registry64);
            ReadUninstallCandidates(candidates, RegistryHive.LocalMachine, RegistryView.Registry64);
            ReadUninstallCandidates(candidates, RegistryHive.CurrentUser, RegistryView.Registry32);
            ReadUninstallCandidates(candidates, RegistryHive.LocalMachine, RegistryView.Registry32);

            var unique = new Dictionary<string, InstalledAppCandidate>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in candidates)
            {
                if (candidate == null || string.IsNullOrWhiteSpace(candidate.DisplayName))
                    continue;

                var key = BuildInstalledAppKey(candidate);
                if (unique.TryGetValue(key, out var existing))
                {
                    MergeInstalledAppCandidate(existing, candidate);
                    continue;
                }

                unique[key] = candidate.Clone();
            }

            return unique.Values
                .OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void ReadAppPathCandidates(List<InstalledAppCandidate> buffer, RegistryHive hive, RegistryView view)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var appPaths = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
                if (appPaths == null)
                    return;

                foreach (var subKeyName in appPaths.GetSubKeyNames())
                {
                    using var subKey = appPaths.OpenSubKey(subKeyName);
                    if (subKey == null)
                        continue;

                    var executablePath = ExtractExecutablePathFromCommand(subKey.GetValue(string.Empty)?.ToString());
                    var displayName = Path.GetFileNameWithoutExtension(subKeyName) ?? subKeyName;
                    var processName = Path.GetFileNameWithoutExtension(executablePath);
                    var candidate = new InstalledAppCandidate
                    {
                        DisplayName = displayName,
                        ExecutablePath = executablePath,
                        ProcessName = processName ?? string.Empty,
                        WorkingDirectory = ExtractExecutableDirectory(executablePath),
                        Source = $"app_paths:{hive}:{view}"
                    };
                    candidate.Aliases.Add(subKeyName);
                    candidate.Aliases.Add(displayName);
                    AddAliasVariants(candidate, displayName);
                    buffer.Add(candidate);
                }
            }
            catch
            {
            }
        }

        private static void ReadUninstallCandidates(List<InstalledAppCandidate> buffer, RegistryHive hive, RegistryView view)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall == null)
                    return;

                foreach (var subKeyName in uninstall.GetSubKeyNames())
                {
                    using var subKey = uninstall.OpenSubKey(subKeyName);
                    if (subKey == null)
                        continue;

                    var displayName = (subKey.GetValue("DisplayName")?.ToString() ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(displayName))
                        continue;

                    var displayIcon = subKey.GetValue("DisplayIcon")?.ToString();
                    var installLocation = ExpandPath(subKey.GetValue("InstallLocation")?.ToString());
                    var executablePath = ExtractExecutablePathFromCommand(displayIcon);
                    if (string.IsNullOrWhiteSpace(executablePath))
                        executablePath = TryGuessExecutableFromInstallLocation(displayName, installLocation);

                    var processName = Path.GetFileNameWithoutExtension(executablePath);
                    var candidate = new InstalledAppCandidate
                    {
                        DisplayName = displayName,
                        ExecutablePath = executablePath,
                        ProcessName = processName ?? string.Empty,
                        WorkingDirectory = FirstNonEmpty(installLocation, ExtractExecutableDirectory(executablePath)),
                        InstallLocation = installLocation,
                        Source = $"uninstall:{hive}:{view}"
                    };
                    candidate.Aliases.Add(displayName);
                    AddAliasVariants(candidate, displayName);
                    buffer.Add(candidate);
                }
            }
            catch
            {
            }
        }

        private static List<InstalledAppCandidate> ResolveInstalledApps(string query, int take)
        {
            var variants = ExpandAppQueryVariants(query);
            return GetInstalledApps(forceRefresh: false)
                .Select(x => new { Candidate = x, Score = ScoreInstalledAppCandidate(x, variants) })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => File.Exists(x.Candidate.ExecutablePath))
                .ThenBy(x => x.Candidate.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Clamp(take, 1, 200))
                .Select(x => x.Candidate)
                .ToList();
        }

        private static int ScoreInstalledAppCandidate(InstalledAppCandidate candidate, IReadOnlyList<string> queryVariants)
        {
            if (candidate == null || queryVariants == null || queryVariants.Count == 0)
                return 0;

            var fields = new List<string>
            {
                candidate.DisplayName,
                candidate.ProcessName,
                Path.GetFileName(candidate.ExecutablePath) ?? string.Empty,
                candidate.ExecutablePath
            };
            fields.AddRange(candidate.Aliases ?? new List<string>());

            var best = 0;
            foreach (var variant in queryVariants)
            {
                var score = 0;
                var normalizedVariant = NormalizeSearchText(variant);
                var tokens = ExtractSearchTokens(variant);

                foreach (var field in fields.Where(x => !string.IsNullOrWhiteSpace(x)))
                {
                    var normalizedField = NormalizeSearchText(field);
                    if (string.Equals(field, variant, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(normalizedField, normalizedVariant, StringComparison.OrdinalIgnoreCase))
                    {
                        score += 1500;
                        continue;
                    }

                    if (ContainsIgnoreCase(field, variant) ||
                        (!string.IsNullOrWhiteSpace(normalizedVariant) &&
                         (normalizedField.Contains(normalizedVariant, StringComparison.OrdinalIgnoreCase) ||
                          normalizedVariant.Contains(normalizedField, StringComparison.OrdinalIgnoreCase))))
                    {
                        score += 900;
                    }

                    foreach (var token in tokens)
                    {
                        if (ContainsIgnoreCase(field, token))
                            score += 140;
                    }
                }

                if (File.Exists(candidate.ExecutablePath))
                    score += 40;
                if (candidate.Source.StartsWith("app_paths", StringComparison.OrdinalIgnoreCase))
                    score += 30;

                best = Math.Max(best, score);
            }

            return best;
        }

        private static IReadOnlyList<string> ExpandAppQueryVariants(string query)
        {
            var trimmed = (query ?? string.Empty).Trim();
            var variants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(trimmed))
                return variants.ToList();

            variants.Add(trimmed);
            variants.Add(NormalizeSearchText(trimmed));
            if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                variants.Add(trimmed[..^4]);

            var tokens = ExtractSearchTokens(trimmed);
            foreach (var pair in BuiltinAppAliasGroups)
            {
                if (pair.Value.Any(alias => QueryMatchesText(trimmed, tokens, alias)))
                {
                    variants.Add(pair.Key);
                    foreach (var alias in pair.Value)
                        variants.Add(alias);
                }
            }

            return variants.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        }

        private static bool QueryMatchesText(string query, IReadOnlyList<string> queryTokens, string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            if (ContainsIgnoreCase(text, query))
                return true;

            var normalizedQuery = NormalizeSearchText(query);
            var normalizedText = NormalizeSearchText(text);
            if (!string.IsNullOrWhiteSpace(normalizedQuery) &&
                (normalizedText.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
                 normalizedQuery.Contains(normalizedText, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            return queryTokens.Count > 0 && queryTokens.All(token => ContainsIgnoreCase(text, token));
        }

        private static List<string> ExtractSearchTokens(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return new List<string>();

            return SearchTokenRegex
                .Matches(value)
                .Select(x => x.Value.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string NormalizeSearchText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var normalized = value.Trim();
            if (normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                normalized = normalized[..^4];

            return new string(normalized
                .Where(ch => char.IsLetterOrDigit(ch) || (ch >= 0x4e00 && ch <= 0x9fff))
                .ToArray())
                .ToLowerInvariant();
        }

        private static void AddAliasVariants(InstalledAppCandidate candidate, string name)
        {
            if (candidate == null || string.IsNullOrWhiteSpace(name))
                return;

            foreach (var pair in BuiltinAppAliasGroups)
            {
                if (!QueryMatchesText(name, ExtractSearchTokens(name), pair.Key) &&
                    !pair.Value.Any(alias => QueryMatchesText(name, ExtractSearchTokens(name), alias)))
                {
                    continue;
                }

                candidate.Aliases.Add(pair.Key);
                foreach (var alias in pair.Value)
                    candidate.Aliases.Add(alias);
            }
        }

        private static object ToAppResult(InstalledAppCandidate candidate)
        {
            if (candidate == null)
                return new { };

            return new
            {
                displayName = candidate.DisplayName,
                executablePath = candidate.ExecutablePath,
                processName = candidate.ProcessName,
                workingDirectory = candidate.WorkingDirectory,
                installLocation = candidate.InstallLocation,
                source = candidate.Source,
                aliases = candidate.Aliases
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(8)
                    .ToList()
            };
        }

        private static string BuildInstalledAppKey(InstalledAppCandidate candidate)
        {
            var namePart = NormalizeSearchText(candidate?.DisplayName);
            var pathPart = (candidate?.ExecutablePath ?? string.Empty).Trim().ToLowerInvariant();
            var sourcePart = (candidate?.Source ?? string.Empty).Trim().ToLowerInvariant();
            return $"{namePart}|{pathPart}|{sourcePart}";
        }

        private static void MergeInstalledAppCandidate(InstalledAppCandidate target, InstalledAppCandidate source)
        {
            if (target == null || source == null)
                return;

            if (string.IsNullOrWhiteSpace(target.ExecutablePath) && !string.IsNullOrWhiteSpace(source.ExecutablePath))
                target.ExecutablePath = source.ExecutablePath;
            if (string.IsNullOrWhiteSpace(target.ProcessName) && !string.IsNullOrWhiteSpace(source.ProcessName))
                target.ProcessName = source.ProcessName;
            if (string.IsNullOrWhiteSpace(target.WorkingDirectory) && !string.IsNullOrWhiteSpace(source.WorkingDirectory))
                target.WorkingDirectory = source.WorkingDirectory;
            if (string.IsNullOrWhiteSpace(target.InstallLocation) && !string.IsNullOrWhiteSpace(source.InstallLocation))
                target.InstallLocation = source.InstallLocation;

            foreach (var alias in source.Aliases.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                if (!target.Aliases.Contains(alias, StringComparer.OrdinalIgnoreCase))
                    target.Aliases.Add(alias);
            }
        }

        private static string ExtractExecutablePathFromCommand(string raw)
        {
            var trimmed = ExpandPath(raw);
            if (string.IsNullOrWhiteSpace(trimmed))
                return string.Empty;

            if (trimmed.StartsWith("\"", StringComparison.Ordinal))
            {
                var endQuote = trimmed.IndexOf('"', 1);
                if (endQuote > 1)
                    trimmed = trimmed[1..endQuote];
            }
            else
            {
                var exeIndex = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (exeIndex > 0)
                    trimmed = trimmed[..(exeIndex + 4)];
            }

            var commaIndex = trimmed.IndexOf(',');
            if (commaIndex > 0)
                trimmed = trimmed[..commaIndex];

            trimmed = trimmed.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(trimmed))
                return string.Empty;

            return trimmed;
        }

        private static string TryGuessExecutableFromInstallLocation(string displayName, string installLocation)
        {
            var root = ExpandPath(installLocation);
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                return string.Empty;

            try
            {
                var exes = Directory.GetFiles(root, "*.exe", SearchOption.TopDirectoryOnly);
                if (exes.Length == 0)
                    return string.Empty;
                if (exes.Length == 1)
                    return exes[0];

                var variants = ExpandAppQueryVariants(displayName);
                return exes
                    .Select(path => new
                    {
                        Path = path,
                        Score = variants.Sum(variant =>
                            QueryMatchesText(variant, ExtractSearchTokens(variant), Path.GetFileNameWithoutExtension(path))
                                ? 100
                                : 0)
                    })
                    .OrderByDescending(x => x.Score)
                    .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault()?.Path ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string ExtractExecutableDirectory(string executablePath)
        {
            var path = (executablePath ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            try
            {
                return Path.GetDirectoryName(path) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string ExpandPath(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return string.Empty;

            try
            {
                return Environment.ExpandEnvironmentVariables(raw.Trim());
            }
            catch
            {
                return raw.Trim();
            }
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var item in values ?? Array.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(item))
                    return item.Trim();
            }

            return string.Empty;
        }

        private static bool TryFindWindowByProcessOrTitle(string processQuery, string titleQuery, out WindowNode window)
        {
            window = null;
            var windows = GetTopWindows(forceRefresh: true);
            var processVariants = ExpandAppQueryVariants(processQuery);
            var titleVariants = ExpandAppQueryVariants(titleQuery);

            window = windows
                .Select(x => new
                {
                    Window = x,
                    Score = ScoreWindowCandidate(x, processVariants, titleVariants)
                })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Window.Visible)
                .FirstOrDefault()?.Window;

            return window != null;
        }

        private static int ScoreWindowCandidate(WindowNode window, IReadOnlyList<string> processVariants, IReadOnlyList<string> titleVariants)
        {
            if (window == null)
                return 0;

            var score = 0;
            foreach (var variant in processVariants ?? Array.Empty<string>())
            {
                if (ContainsIgnoreCase(window.ProcessName, variant))
                    score += 500;
                if (ContainsIgnoreCase(window.Title, variant))
                    score += 180;
            }

            foreach (var variant in titleVariants ?? Array.Empty<string>())
            {
                if (ContainsIgnoreCase(window.Title, variant))
                    score += 420;
                if (ContainsIgnoreCase(window.ProcessName, variant))
                    score += 120;
            }

            return score;
        }

        private static object ToWindowResult(WindowNode window)
        {
            if (window == null)
                return new { };

            return new
            {
                hwnd = window.Hwnd,
                title = window.Title,
                className = window.ClassName,
                processName = window.ProcessName,
                left = window.Left,
                top = window.Top,
                width = window.Width,
                height = window.Height,
                visible = window.Visible,
                enabled = window.Enabled
            };
        }

        private static string BuildBrowserSearchUrl(string query, string site)
        {
            var effectiveQuery = (query ?? string.Empty).Trim();
            var trimmedSite = (site ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(trimmedSite))
            {
                effectiveQuery = trimmedSite.Contains('.')
                    ? $"site:{trimmedSite} {effectiveQuery}"
                    : $"{trimmedSite} {effectiveQuery}";
            }

            return "https://www.google.com/search?q=" + Uri.EscapeDataString(effectiveQuery);
        }

        private static bool TrySetClipboardText(string text, out string message)
        {
            return RunInSta(() =>
            {
                Clipboard.SetText(text ?? string.Empty);
                return true;
            }, out message);
        }

        private static bool TryPasteClipboardText(string targetWindowTitleContains, int timeoutMs, int pollMs, out string message)
        {
            message = string.Empty;
            if (!string.IsNullOrWhiteSpace(targetWindowTitleContains))
            {
                if (!TryFindWindowByTitle(targetWindowTitleContains, out var targetWindow))
                {
                    message = "未匹配到目标窗口。";
                    return false;
                }

                if (!ActivateWindow(targetWindow, timeoutMs, pollMs, out var activateMessage))
                {
                    message = activateMessage;
                    return false;
                }
            }

            Thread.Sleep(60);
            keybd_event((byte)Keys.ControlKey, 0, 0, UIntPtr.Zero);
            keybd_event((byte)Keys.V, 0, 0, UIntPtr.Zero);
            keybd_event((byte)Keys.V, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event((byte)Keys.ControlKey, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            message = "剪贴板文本已粘贴。";
            return true;
        }

        private static bool RunInSta(Func<bool> func, out string message)
        {
            message = string.Empty;
            Exception captured = null;
            var result = false;
            using var completed = new ManualResetEventSlim(false);

            var thread = new Thread(() =>
            {
                try
                {
                    result = func();
                }
                catch (Exception ex)
                {
                    captured = ex;
                }
                finally
                {
                    completed.Set();
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();

            if (!completed.Wait(TimeSpan.FromSeconds(3)))
            {
                message = "STA 剪贴板线程超时。";
                return false;
            }

            if (captured != null)
            {
                message = captured.Message;
                return false;
            }

            return result;
        }

        private static string ExecuteActionWithVerify(
            long hwnd,
            string action,
            string text,
            bool confirmed,
            string lane,
            string source,
            int? x,
            int? y,
            int retries,
            int verifyTimeoutMs,
            string verifyTitleContains)
        {
            var policy = UiActionPolicy.Evaluate(action, confirmed);
            if (!policy.allowed)
                return BuildError("UiAct", policy.reason, new { action, risk = policy.risk.ToString() });

            var normalized = policy.action;
            var retryCount = Math.Clamp(retries, 0, 3);
            var timeout = Math.Clamp(verifyTimeoutMs, 200, 5000);
            var attempts = retryCount + 1;

            string lastVerifyMessage = "未知验收失败。";
            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                var handle = new IntPtr(hwnd);
                if (handle == IntPtr.Zero || !IsWindow(handle))
                    return BuildError("UiAct", $"窗口句柄无效：{hwnd}");

                if (!TryExecuteActionOnce(handle, normalized, text, x, y, out var executeMessage, out var pointX, out var pointY))
                    return BuildError("UiAct", executeMessage, new { hwnd, action = normalized });

                if (VerifyAction(handle, normalized, text, timeout, verifyTitleContains, out var verifyMessage))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        type = "ui.act",
                        ok = true,
                        lane = UiLaneScheduler.NormalizeLane(lane),
                        source,
                        action = normalized,
                        risk = policy.risk.ToString(),
                        hwnd,
                        x = pointX,
                        y = pointY,
                        retries = retryCount,
                        attemptsUsed = attempt,
                        verified = true,
                        message = executeMessage,
                        verifyMessage
                    });
                }

                lastVerifyMessage = verifyMessage;
                if (attempt < attempts)
                    Thread.Sleep(120);
            }

            return BuildError("UiActVerify", lastVerifyMessage, new
            {
                action = normalized,
                hwnd,
                retries = retryCount,
                verifyTimeoutMs = timeout,
                verifyTitleContains
            });
        }

        private static bool TryExecuteActionOnce(IntPtr handle, string normalizedAction, string text, int? x, int? y, out string message, out int? pointX, out int? pointY)
        {
            pointX = null;
            pointY = null;
            message = string.Empty;

            if (normalizedAction == "activate")
            {
                TryRequestForeground(handle);
                message = "窗口激活请求已发送。";
                return true;
            }

            if (normalizedAction == "click")
            {
                if (!GetWindowRect(handle, out var rect))
                {
                    message = "读取窗口区域失败。";
                    return false;
                }

                var px = x ?? (rect.Left + Math.Max(1, rect.Right - rect.Left) / 2);
                var py = y ?? (rect.Top + Math.Max(1, rect.Bottom - rect.Top) / 2);

                TryRequestForeground(handle);
                Thread.Sleep(40);
                SetCursorPos(px, py);
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);

                pointX = px;
                pointY = py;
                message = "单击已发送。";
                return true;
            }

            if (normalizedAction == "double_click")
            {
                if (!GetWindowRect(handle, out var rect))
                {
                    message = "读取窗口区域失败。";
                    return false;
                }

                var px = x ?? (rect.Left + Math.Max(1, rect.Right - rect.Left) / 2);
                var py = y ?? (rect.Top + Math.Max(1, rect.Bottom - rect.Top) / 2);

                TryRequestForeground(handle);
                Thread.Sleep(40);
                SetCursorPos(px, py);
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(60);
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);

                pointX = px;
                pointY = py;
                message = "双击已发送。";
                return true;
            }

            if (normalizedAction == "right_click")
            {
                if (!GetWindowRect(handle, out var rect))
                {
                    message = "读取窗口区域失败。";
                    return false;
                }

                var px = x ?? (rect.Left + Math.Max(1, rect.Right - rect.Left) / 2);
                var py = y ?? (rect.Top + Math.Max(1, rect.Bottom - rect.Top) / 2);

                TryRequestForeground(handle);
                Thread.Sleep(40);
                SetCursorPos(px, py);
                mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, UIntPtr.Zero);
                mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, UIntPtr.Zero);

                pointX = px;
                pointY = py;
                message = "右击已发送。";
                return true;
            }

            if (normalizedAction == "set_text")
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    message = "set_text 需要 text 参数。";
                    return false;
                }

                TryRequestForeground(handle);
                SendMessage(handle, WM_SETTEXT, IntPtr.Zero, text);
                message = "文本写入请求已发送。";
                return true;
            }

            if (normalizedAction == "close")
            {
                SendMessage(handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                message = "窗口关闭消息已发送。";
                return true;
            }

            message = $"未实现的 action：{normalizedAction}";
            return false;
        }

        private static string NormalizeCommandType(string commandType)
        {
            var normalized = (commandType ?? string.Empty).Trim().ToLowerInvariant();
            return normalized switch
            {
                "" => "mouse",
                "mouse" => "mouse",
                "keyboard" => "keyboard",
                "shell" => "shell",
                "window" => "window",
                "app" => "app",
                "browser" => "browser",
                "clipboard" => "clipboard",
                "explorer" => "explorer",
                "file" => "file",
                _ => string.Empty
            };
        }

        private static async Task<string> ExecuteMouseCommandAsync(
            string operation,
            int? x,
            int? y,
            int? relativeX,
            int? relativeY,
            int? captureLeft,
            int? captureTop,
            int? captureWidth,
            int? captureHeight,
            string lane)
        {
            return await UiLaneScheduler.RunAsync(lane, () =>
            {
                var normalizedOperation = NormalizeMouseOperation(operation);
                if (string.IsNullOrWhiteSpace(normalizedOperation))
                {
                    var err = BuildError("UiCommand", "mouse.operation 仅支持 move、move_click、move_double_click、move_right_click。", new
                    {
                        operation
                    });
                    ToolCallLogger.Log(nameof(UiCommand), new
                    {
                        commandType = "mouse",
                        operation,
                        x,
                        y,
                        relativeX,
                        relativeY,
                        captureLeft,
                        captureTop,
                        captureWidth,
                        captureHeight,
                        lane
                    }, err, "invalid operation");
                    return Task.FromResult(err);
                }

                var screenBounds = SystemInformation.VirtualScreen;
                var resolvedLeft = captureLeft ?? screenBounds.Left;
                var resolvedTop = captureTop ?? screenBounds.Top;
                var resolvedWidth = captureWidth ?? screenBounds.Width;
                var resolvedHeight = captureHeight ?? screenBounds.Height;
                if (resolvedWidth <= 0 || resolvedHeight <= 0)
                {
                    var err = BuildError("UiCommand", "capture 范围无效。", new
                    {
                        captureLeft = resolvedLeft,
                        captureTop = resolvedTop,
                        captureWidth = resolvedWidth,
                        captureHeight = resolvedHeight
                    });
                    ToolCallLogger.Log(nameof(UiCommand), new
                    {
                        commandType = "mouse",
                        operation = normalizedOperation,
                        x,
                        y,
                        relativeX,
                        relativeY,
                        captureLeft = resolvedLeft,
                        captureTop = resolvedTop,
                        captureWidth = resolvedWidth,
                        captureHeight = resolvedHeight,
                        lane
                    }, err, "invalid capture");
                    return Task.FromResult(err);
                }

                int screenX;
                int screenY;
                var pointSource = "absolute";
                if (x.HasValue && y.HasValue)
                {
                    screenX = x.Value;
                    screenY = y.Value;
                }
                else
                {
                    if (!relativeX.HasValue || !relativeY.HasValue)
                    {
                        var err = BuildError("UiCommand", "缺少 x/y 或 relativeX/relativeY。");
                        ToolCallLogger.Log(nameof(UiCommand), new
                        {
                            commandType = "mouse",
                            operation = normalizedOperation,
                            x,
                            y,
                            relativeX,
                            relativeY,
                            captureLeft = resolvedLeft,
                            captureTop = resolvedTop,
                            captureWidth = resolvedWidth,
                            captureHeight = resolvedHeight,
                            lane
                        }, err, "missing coordinates");
                        return Task.FromResult(err);
                    }

                    if (relativeX.Value < 0 || relativeX.Value > 1000 || relativeY.Value < 0 || relativeY.Value > 1000)
                    {
                        var err = BuildError("UiCommand", "relativeX/relativeY 必须在 0 到 1000 之间。", new
                        {
                            relativeX,
                            relativeY
                        });
                        ToolCallLogger.Log(nameof(UiCommand), new
                        {
                            commandType = "mouse",
                            operation = normalizedOperation,
                            x,
                            y,
                            relativeX,
                            relativeY,
                            captureLeft = resolvedLeft,
                            captureTop = resolvedTop,
                            captureWidth = resolvedWidth,
                            captureHeight = resolvedHeight,
                            lane
                        }, err, "relative out of range");
                        return Task.FromResult(err);
                    }

                    var maxX = resolvedLeft + Math.Max(resolvedWidth - 1, 0);
                    var maxY = resolvedTop + Math.Max(resolvedHeight - 1, 0);
                    screenX = resolvedLeft + (int)Math.Round(relativeX.Value * resolvedWidth / 1000.0, MidpointRounding.AwayFromZero);
                    screenY = resolvedTop + (int)Math.Round(relativeY.Value * resolvedHeight / 1000.0, MidpointRounding.AwayFromZero);
                    screenX = Math.Max(resolvedLeft, Math.Min(maxX, screenX));
                    screenY = Math.Max(resolvedTop, Math.Min(maxY, screenY));
                    pointSource = "relative";
                }

                var executed = ExecuteMouseActionAtPoint(screenX, screenY, normalizedOperation, out var message);
                var result = JsonConvert.SerializeObject(new
                {
                    type = "ui.command",
                    ok = executed,
                    lane = UiLaneScheduler.NormalizeLane(lane),
                    commandType = "mouse",
                    operation = normalizedOperation,
                    source = pointSource,
                    x = screenX,
                    y = screenY,
                    relativeX,
                    relativeY,
                    capture = new
                    {
                        left = resolvedLeft,
                        top = resolvedTop,
                        width = resolvedWidth,
                        height = resolvedHeight
                    },
                    message
                });
                ToolCallLogger.Log(nameof(UiCommand), new
                {
                    commandType = "mouse",
                    operation = normalizedOperation,
                    x,
                    y,
                    relativeX,
                    relativeY,
                    captureLeft = resolvedLeft,
                    captureTop = resolvedTop,
                    captureWidth = resolvedWidth,
                    captureHeight = resolvedHeight,
                    lane
                }, result, executed ? null : message);
                return Task.FromResult(result);
            }).ConfigureAwait(false);
        }

        private static string NormalizeMouseOperation(string operation)
        {
            var normalized = (operation ?? string.Empty).Trim().ToLowerInvariant();
            return normalized switch
            {
                "" => "move",
                "move" => "move",
                "move_click" => "move_click",
                "click" => "move_click",
                "move_double_click" => "move_double_click",
                "double_click" => "move_double_click",
                "move_right_click" => "move_right_click",
                "right_click" => "move_right_click",
                _ => string.Empty
            };
        }

        private static bool ExecuteMouseActionAtPoint(int screenX, int screenY, string operation, out string message)
        {
            if (!SetCursorPos(screenX, screenY))
            {
                message = "鼠标移动失败。";
                return false;
            }

            switch (operation)
            {
                case "move":
                    message = "鼠标已移动到目标点。";
                    return true;
                case "move_click":
                    Thread.Sleep(30);
                    mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                    mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                    message = "鼠标已移动到目标点并完成单击。";
                    return true;
                case "move_double_click":
                    Thread.Sleep(30);
                    mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                    mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                    Thread.Sleep(80);
                    mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                    mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                    message = "鼠标已移动到目标点并完成双击。";
                    return true;
                case "move_right_click":
                    Thread.Sleep(30);
                    mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, UIntPtr.Zero);
                    mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, UIntPtr.Zero);
                    message = "鼠标已移动到目标点并完成右击。";
                    return true;
                default:
                    message = "未实现的鼠标动作。";
                    return false;
            }
        }

        private static string NormalizeWorkingDirectory(string workingDirectory)
        {
            var trimmed = (workingDirectory ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(trimmed) ? string.Empty : trimmed;
        }

        private static bool TryFindWindowByTitle(string titleContains, out WindowNode window)
        {
            window = null;
            var windows = GetTopWindows(forceRefresh: true);
            window = windows.FirstOrDefault(w => MatchesWindowQuery(w, titleContains));
            return window != null;
        }

        private static bool ActivateWindow(WindowNode targetWindow, int timeoutMs, int pollMs, out string message, bool maximizeWindow = false)
        {
            message = string.Empty;
            if (targetWindow == null)
            {
                message = "目标窗口为空。";
                return false;
            }

            var handle = new IntPtr(targetWindow.Hwnd);
            if (handle == IntPtr.Zero || !IsWindow(handle))
            {
                message = "目标窗口句柄无效。";
                return false;
            }

            var ok = ActivateWindowHandle(handle, targetWindow.Title, timeoutMs, pollMs, out var activateMessage, maximizeWindow);
            if (!ok)
            {
                Thread.Sleep(Math.Min(120, pollMs));
                ok = ActivateWindowHandle(handle, targetWindow.Title, timeoutMs, pollMs, out activateMessage, maximizeWindow);
            }

            message = activateMessage;
            return ok;
        }

        private static bool ActivateWindowHandle(IntPtr handle, string verifyTitleContains, int timeoutMs, int pollMs, out string message, bool maximizeWindow = false)
        {
            message = string.Empty;
            if (handle == IntPtr.Zero || !IsWindow(handle))
            {
                message = "目标窗口句柄无效。";
                return false;
            }

            TryRequestForeground(handle, maximizeWindow);
            var ok = WaitUntil(() =>
            {
                var fg = GetForegroundWindow();
                if (fg == handle)
                    return true;
                if (string.IsNullOrWhiteSpace(verifyTitleContains))
                    return false;
                var currentTitle = GetWindowTitle(fg);
                return ContainsIgnoreCase(currentTitle, verifyTitleContains);
            }, timeoutMs, pollMs);

            if (!ok)
            {
                message = "已发送窗口激活请求，但未通过验收。";
                return false;
            }

            if (maximizeWindow)
            {
                TryApplyWindowDisplayMode(handle, maximizeWindow: true);
                var maximized = WaitUntil(() => IsZoomed(handle), Math.Min(timeoutMs, 1200), Math.Max(50, Math.Min(pollMs, 200)));
                message = maximized
                    ? "目标窗口已激活，并已最大化。"
                    : "目标窗口已激活，并已请求最大化。";
                return true;
            }

            message = "目标窗口已激活。";
            return true;
        }

        private static void TryRequestForeground(IntPtr handle, bool maximizeWindow = false)
        {
            if (handle == IntPtr.Zero || !IsWindow(handle))
                return;

            var currentThreadId = GetCurrentThreadId();
            var attachedThreadIds = new List<uint>();
            try
            {
                var foreground = GetForegroundWindow();
                var foregroundThreadId = foreground == IntPtr.Zero
                    ? 0
                    : GetWindowThreadProcessId(foreground, out _);
                var targetThreadId = GetWindowThreadProcessId(handle, out _);

                if (foregroundThreadId != 0 &&
                    foregroundThreadId != currentThreadId &&
                    AttachThreadInput(currentThreadId, foregroundThreadId, true))
                {
                    attachedThreadIds.Add(foregroundThreadId);
                }

                if (targetThreadId != 0 &&
                    targetThreadId != currentThreadId &&
                    !attachedThreadIds.Contains(targetThreadId) &&
                    AttachThreadInput(currentThreadId, targetThreadId, true))
                {
                    attachedThreadIds.Add(targetThreadId);
                }

                TryApplyWindowDisplayMode(handle, maximizeWindow);
                _ = BringWindowToTop(handle);
                _ = SetActiveWindow(handle);
                _ = SetForegroundWindow(handle);
                _ = SetFocus(handle);
                if (maximizeWindow)
                    TryApplyWindowDisplayMode(handle, maximizeWindow);
            }
            catch
            {
            }
            finally
            {
                for (var i = attachedThreadIds.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        _ = AttachThreadInput(currentThreadId, attachedThreadIds[i], false);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static void TryApplyWindowDisplayMode(IntPtr handle, bool maximizeWindow)
        {
            if (handle == IntPtr.Zero || !IsWindow(handle))
                return;

            try
            {
                _ = ShowWindow(handle, maximizeWindow ? SW_SHOWMAXIMIZED : SW_RESTORE);
            }
            catch
            {
            }
        }

        private static bool PrepareWindowForKeyboardInput(
            WindowNode targetWindow,
            int timeoutMs,
            int pollMs,
            bool requireInputFocus,
            out WindowNode focusTarget,
            out string focusStrategy,
            out string message)
        {
            focusTarget = null;
            focusStrategy = "activate_window";
            message = string.Empty;

            if (!ActivateWindow(targetWindow, timeoutMs, pollMs, out var activateMessage))
            {
                message = activateMessage;
                return false;
            }

            if (!requireInputFocus)
            {
                message = activateMessage;
                return true;
            }

            var preferredTextEntry = FindPreferredTextEntryChild(targetWindow);
            if (preferredTextEntry == null)
            {
                focusStrategy = "activate_window_only";
                message = "已激活目标窗口，未识别到明显输入控件，改为直接向窗口发送键盘输入。";
                return true;
            }

            if (!TryClickChildForTextInput(targetWindow, preferredTextEntry, timeoutMs, pollMs, out var focusMessage))
            {
                message = focusMessage;
                return false;
            }

            focusTarget = preferredTextEntry;
            focusStrategy = "activate_window_click_input";
            message = focusMessage;
            return true;
        }

        private static WindowNode FindPreferredTextEntryChild(WindowNode targetWindow)
        {
            if (targetWindow == null || targetWindow.Hwnd == 0)
                return null;

            return EnumerateChildWindows(new IntPtr(targetWindow.Hwnd), 180)
                .Where(IsLikelyTextEntryChild)
                .OrderByDescending(ScoreTextEntryChild)
                .FirstOrDefault();
        }

        private static bool IsLikelyTextEntryChild(WindowNode child)
        {
            if (child == null || child.Hwnd == 0 || !child.Visible || !child.Enabled)
                return false;

            return IsLikelyTextEntryClass(child.ClassName);
        }

        private static bool IsLikelyTextEntryClass(string className)
        {
            var normalized = (className ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(normalized))
                return false;

            return normalized.Contains("edit") ||
                   normalized.Contains("textbox") ||
                   normalized.Contains("richedit") ||
                   normalized.Contains("scintilla");
        }

        private static int ScoreTextEntryChild(WindowNode child)
        {
            if (child == null)
                return 0;

            var className = (child.ClassName ?? string.Empty).Trim().ToLowerInvariant();
            var score = 0;
            if (className.Contains("richedit"))
                score += 1500;
            if (className.Contains("edit"))
                score += 1200;
            if (className.Contains("textbox"))
                score += 1000;
            if (className.Contains("scintilla"))
                score += 1000;
            if (child.Visible)
                score += 300;
            if (child.Enabled)
                score += 300;

            var area = Math.Max(1, child.Width) * Math.Max(1, child.Height);
            score += Math.Min(area / 400, 800);
            return score;
        }

        private static bool TryClickChildForTextInput(WindowNode targetWindow, WindowNode child, int timeoutMs, int pollMs, out string message)
        {
            message = string.Empty;
            if (targetWindow == null || child == null)
            {
                message = "缺少输入焦点目标。";
                return false;
            }

            var targetHandle = new IntPtr(targetWindow.Hwnd);
            var childHandle = new IntPtr(child.Hwnd);
            if (!IsWindow(targetHandle) || !IsWindow(childHandle))
            {
                message = "输入焦点目标已失效。";
                return false;
            }

            TryRequestForeground(targetHandle);
            if (!GetWindowRect(childHandle, out var rect))
            {
                message = "读取输入控件区域失败。";
                return false;
            }

            var width = Math.Max(1, rect.Right - rect.Left);
            var height = Math.Max(1, rect.Bottom - rect.Top);
            var clickX = Math.Clamp(rect.Left + Math.Max(12, Math.Min(32, width / 8)), rect.Left, rect.Right - 1);
            var clickY = Math.Clamp(rect.Top + Math.Max(8, Math.Min(24, height / 2)), rect.Top, rect.Bottom - 1);
            if (!SetCursorPos(clickX, clickY))
            {
                message = "移动到输入控件失败。";
                return false;
            }

            Thread.Sleep(40);
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);

            var ok = WaitUntil(() =>
            {
                var foreground = GetForegroundWindow();
                if (foreground == targetHandle)
                    return true;

                var title = GetWindowTitle(foreground);
                return ContainsIgnoreCase(title, targetWindow.Title);
            }, Math.Min(timeoutMs, 1500), pollMs);

            message = ok
                ? "目标窗口已激活，并已点击输入区域获取焦点。"
                : "已点击输入区域，但前台窗口验收未通过。";
            return ok;
        }

        private static bool TryActivateLaunchedProcessWindow(
            Process process,
            int timeoutMs,
            int pollMs,
            out long activatedHwnd,
            out string activatedTitle,
            out string message,
            bool maximizeWindow = false)
        {
            activatedHwnd = 0;
            activatedTitle = string.Empty;
            message = string.Empty;
            if (process == null)
            {
                message = "未返回进程对象。";
                return false;
            }

            try
            {
                if (!process.HasExited)
                    process.WaitForInputIdle(Math.Min(timeoutMs, 3000));
            }
            catch
            {
            }

            IntPtr handle = IntPtr.Zero;
            long foundHwnd = 0;
            string foundTitle = string.Empty;
            var found = WaitUntil(() =>
            {
                try
                {
                    process.Refresh();
                }
                catch
                {
                }

                handle = process.MainWindowHandle;
                if (handle == IntPtr.Zero || !IsWindow(handle))
                    return false;

                foundHwnd = ToHwndLong(handle);
                foundTitle = GetWindowTitle(handle);
                return true;
            }, Math.Min(timeoutMs, 5000), pollMs);

            if (!found)
            {
                message = "进程已启动，但暂未检测到主窗口。";
                return false;
            }

            activatedHwnd = foundHwnd;
            activatedTitle = foundTitle;
            var activated = ActivateWindowHandle(handle, activatedTitle, Math.Min(timeoutMs, 5000), pollMs, out var activateMessage, maximizeWindow);
            activatedHwnd = ToHwndLong(handle);
            activatedTitle = GetWindowTitle(handle);
            message = activateMessage;
            return activated;
        }

        private static bool TryResolveVirtualKeys(string[] rawKeys, out List<ushort> virtualKeys, out string error)
        {
            virtualKeys = new List<ushort>();
            error = string.Empty;

            var items = (rawKeys ?? Array.Empty<string>())
                .Select(x => (x ?? string.Empty).Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            if (items.Count == 0)
            {
                error = "缺少 keys。";
                return false;
            }

            foreach (var item in items)
            {
                if (!TryGetVirtualKeyCode(item, out var vk))
                {
                    error = $"不支持的按键：{item}";
                    return false;
                }

                virtualKeys.Add(vk);
            }

            return true;
        }

        private static bool TryResolveOptionalVirtualKeys(string[] rawKeys, out List<ushort> virtualKeys, out string error)
        {
            virtualKeys = new List<ushort>();
            error = string.Empty;

            var items = (rawKeys ?? Array.Empty<string>())
                .Select(x => (x ?? string.Empty).Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            if (items.Count == 0)
                return true;

            foreach (var item in items)
            {
                if (!TryGetVirtualKeyCode(item, out var vk))
                {
                    error = $"不支持的修饰键：{item}";
                    return false;
                }

                virtualKeys.Add(vk);
            }

            return true;
        }

        private static bool TryGetVirtualKeyCode(string key, out ushort virtualKey)
        {
            virtualKey = 0;
            var normalized = (key ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
                return false;

            switch (normalized.ToLowerInvariant())
            {
                case "enter":
                case "return":
                    virtualKey = (ushort)Keys.Enter;
                    return true;
                case "esc":
                case "escape":
                    virtualKey = (ushort)Keys.Escape;
                    return true;
                case "tab":
                    virtualKey = (ushort)Keys.Tab;
                    return true;
                case "space":
                    virtualKey = (ushort)Keys.Space;
                    return true;
                case "backspace":
                    virtualKey = (ushort)Keys.Back;
                    return true;
                case "delete":
                case "del":
                    virtualKey = (ushort)Keys.Delete;
                    return true;
                case "insert":
                case "ins":
                    virtualKey = (ushort)Keys.Insert;
                    return true;
                case "up":
                    virtualKey = (ushort)Keys.Up;
                    return true;
                case "down":
                    virtualKey = (ushort)Keys.Down;
                    return true;
                case "left":
                    virtualKey = (ushort)Keys.Left;
                    return true;
                case "right":
                    virtualKey = (ushort)Keys.Right;
                    return true;
                case "home":
                    virtualKey = (ushort)Keys.Home;
                    return true;
                case "end":
                    virtualKey = (ushort)Keys.End;
                    return true;
                case "pageup":
                case "pgup":
                    virtualKey = (ushort)Keys.PageUp;
                    return true;
                case "pagedown":
                case "pgdn":
                    virtualKey = (ushort)Keys.PageDown;
                    return true;
                case "ctrl":
                case "control":
                    virtualKey = (ushort)Keys.ControlKey;
                    return true;
                case "shift":
                    virtualKey = (ushort)Keys.ShiftKey;
                    return true;
                case "alt":
                    virtualKey = (ushort)Keys.Menu;
                    return true;
                case "win":
                case "windows":
                case "lwin":
                    virtualKey = (ushort)Keys.LWin;
                    return true;
                case "rwin":
                    virtualKey = (ushort)Keys.RWin;
                    return true;
            }

            if (normalized.Length == 1)
            {
                var ch = char.ToUpperInvariant(normalized[0]);
                if ((ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9'))
                {
                    virtualKey = ch;
                    return true;
                }
            }

            if (Enum.TryParse<Keys>(normalized, true, out var parsed))
            {
                virtualKey = (ushort)(parsed & Keys.KeyCode);
                return virtualKey != 0;
            }

            return false;
        }

        private static bool ExecuteKeySequence(IReadOnlyList<ushort> modifiers, IReadOnlyList<ushort> keys, out string message)
        {
            message = string.Empty;
            var pressedModifiers = new List<ushort>();

            try
            {
                foreach (var modifier in modifiers ?? Array.Empty<ushort>())
                {
                    if (!SendVirtualKey(modifier, keyUp: false))
                    {
                        message = "修饰键按下失败。";
                        return false;
                    }

                    pressedModifiers.Add(modifier);
                }

                foreach (var key in keys ?? Array.Empty<ushort>())
                {
                    if (!SendVirtualKey(key, keyUp: false) || !SendVirtualKey(key, keyUp: true))
                    {
                        message = "按键发送失败。";
                        return false;
                    }

                    Thread.Sleep(40);
                }

                message = "按键序列已发送。";
                return true;
            }
            finally
            {
                ReleasePressedKeys(pressedModifiers);
            }
        }

        private static bool ExecuteHotkey(IReadOnlyList<ushort> modifiers, IReadOnlyList<ushort> keys, out string message)
        {
            message = string.Empty;
            var modifierList = (modifiers ?? Array.Empty<ushort>()).ToList();
            var keyList = (keys ?? Array.Empty<ushort>()).ToList();
            if (keyList.Count == 0)
            {
                message = "缺少快捷键主键。";
                return false;
            }

            return ExecuteKeySequence(modifierList, keyList, out message);
        }

        private static void ReleasePressedKeys(IReadOnlyList<ushort> keys)
        {
            if (keys == null)
                return;

            for (var i = keys.Count - 1; i >= 0; i--)
            {
                _ = SendVirtualKey(keys[i], keyUp: true);
            }
        }

        private static bool SendVirtualKey(ushort virtualKey, bool keyUp)
        {
            var inputs = new[]
            {
                new INPUT
                {
                    Type = INPUT_KEYBOARD,
                    Data = new INPUTUNION
                    {
                        Keyboard = new KEYBDINPUT
                        {
                            Vk = virtualKey,
                            Scan = 0,
                            Flags = keyUp ? KEYEVENTF_KEYUP : 0,
                            Time = 0,
                            ExtraInfo = IntPtr.Zero
                        }
                    }
                }
            };

            return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length;
        }

        private static bool TrySendText(string text, WindowNode targetWindow, WindowNode focusTarget, out string message)
        {
            if (SendUnicodeText(text, out message))
                return true;

            var sendInputMessage = message;
            var textTarget = ResolveTextEntryTarget(targetWindow, focusTarget);
            if (textTarget == null)
            {
                message = sendInputMessage;
                return false;
            }

            if (!TryWriteTextViaControlMessage(textTarget, text, out var fallbackMessage))
            {
                message = string.IsNullOrWhiteSpace(fallbackMessage)
                    ? sendInputMessage
                    : sendInputMessage + " " + fallbackMessage;
                return false;
            }

            message = string.IsNullOrWhiteSpace(sendInputMessage)
                ? fallbackMessage
                : sendInputMessage + " 已自动回退到控件消息写入。";
            return true;
        }

        private static WindowNode ResolveTextEntryTarget(WindowNode targetWindow, WindowNode focusTarget)
        {
            if (focusTarget != null && focusTarget.Hwnd != 0 && IsWindow(new IntPtr(focusTarget.Hwnd)))
                return focusTarget;

            if (targetWindow == null || targetWindow.Hwnd == 0)
                return null;

            var preferred = FindPreferredTextEntryChild(targetWindow);
            if (preferred != null)
                return preferred;

            return IsLikelyTextEntryClass(targetWindow.ClassName) ? targetWindow : null;
        }

        private static bool TryWriteTextViaControlMessage(WindowNode textTarget, string text, out string message)
        {
            message = string.Empty;
            if (textTarget == null || textTarget.Hwnd == 0)
            {
                message = "未找到可回退写入的文本控件。";
                return false;
            }

            var handle = new IntPtr(textTarget.Hwnd);
            if (!IsWindow(handle))
            {
                message = "文本控件句柄已失效。";
                return false;
            }

            var className = GetWindowClass(handle);
            if (!IsLikelyTextEntryClass(className))
            {
                message = "目标控件不是已知文本输入控件。";
                return false;
            }

            var expected = NormalizeTextForCompare(text);
            if (TryWriteTextViaMessage(handle, text, expected, useReplaceSel: true, out var observed))
            {
                message = "控件消息写入成功。";
                return true;
            }

            if (TryWriteTextViaMessage(handle, text, expected, useReplaceSel: false, out observed))
            {
                message = "控件消息写入成功（WM_SETTEXT 回退）。";
                return true;
            }

            if (string.IsNullOrWhiteSpace(expected))
            {
                message = "控件消息写入已发送。";
                return true;
            }

            message = "控件消息写入后未读到目标文本，当前控件文本：" + TrimForLog(observed);
            return false;
        }

        private static bool TryWriteTextViaMessage(IntPtr handle, string text, string expected, bool useReplaceSel, out string observed)
        {
            observed = string.Empty;
            try
            {
                if (useReplaceSel)
                    SendMessage(handle, EM_REPLACESEL, (IntPtr)1, text ?? string.Empty);
                else
                    SendMessage(handle, WM_SETTEXT, IntPtr.Zero, text ?? string.Empty);
            }
            catch
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(expected))
                return true;

            var lastObserved = string.Empty;
            var ok = WaitUntil(() =>
            {
                lastObserved = NormalizeTextForCompare(ReadControlText(handle));
                if (string.IsNullOrWhiteSpace(lastObserved))
                    return false;

                return string.Equals(lastObserved, expected, StringComparison.Ordinal) ||
                       lastObserved.Contains(expected, StringComparison.Ordinal);
            }, 1200, 60);

            observed = lastObserved;
            return ok;
        }

        private static bool SendUnicodeText(string text, out string message)
        {
            message = string.Empty;
            if (string.IsNullOrEmpty(text))
            {
                message = "缺少文本。";
                return false;
            }

            foreach (var ch in text)
            {
                if (ch == '\r')
                    continue;

                if (ch == '\n')
                {
                    if (!SendVirtualKey((ushort)Keys.Enter, keyUp: false) ||
                        !SendVirtualKey((ushort)Keys.Enter, keyUp: true))
                    {
                        message = "换行发送失败。";
                        return false;
                    }

                    continue;
                }

                var inputs = new[]
                {
                    new INPUT
                    {
                        Type = INPUT_KEYBOARD,
                        Data = new INPUTUNION
                        {
                            Keyboard = new KEYBDINPUT
                            {
                                Vk = 0,
                                Scan = ch,
                                Flags = KEYEVENTF_UNICODE,
                                Time = 0,
                                ExtraInfo = IntPtr.Zero
                            }
                        }
                    },
                    new INPUT
                    {
                        Type = INPUT_KEYBOARD,
                        Data = new INPUTUNION
                        {
                            Keyboard = new KEYBDINPUT
                            {
                                Vk = 0,
                                Scan = ch,
                                Flags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP,
                                Time = 0,
                                ExtraInfo = IntPtr.Zero
                            }
                        }
                    }
                };

                var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
                if (sent != inputs.Length)
                {
                    var lastError = Marshal.GetLastWin32Error();
                    message = lastError > 0
                        ? $"文本输入失败（SendInput={sent}/{inputs.Length}, Win32={lastError}）。"
                        : $"文本输入失败（SendInput={sent}/{inputs.Length}）。";
                    return false;
                }
            }

            message = "文本已输入。";
            return true;
        }

        private static string WrapWindowCommandResult(string rawJson, string operation, string targetWindowTitleContains)
        {
            try
            {
                var wrapper = JsonConvert.DeserializeObject<Dictionary<string, object>>(rawJson)
                    ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                wrapper["type"] = "ui.command";
                wrapper["commandType"] = "window";
                wrapper["operation"] = operation;
                wrapper["targetWindowTitleContains"] = targetWindowTitleContains;
                return JsonConvert.SerializeObject(wrapper);
            }
            catch
            {
                return rawJson;
            }
        }

        private static bool VerifyAction(IntPtr handle, string normalizedAction, string text, int timeoutMs, string verifyTitleContains, out string message)
        {
            if (normalizedAction == "activate")
            {
                var ok = WaitUntil(() =>
                {
                    var fg = GetForegroundWindow();
                    if (fg == handle) return true;
                    if (!string.IsNullOrWhiteSpace(verifyTitleContains))
                    {
                        var title = GetWindowTitle(fg);
                        return ContainsIgnoreCase(title, verifyTitleContains);
                    }
                    return false;
                }, timeoutMs, 50);
                message = ok ? "激活验收通过。" : "激活验收失败。";
                return ok;
            }

            if (normalizedAction == "click")
            {
                var ok = WaitUntil(() => IsWindow(handle) && IsWindowVisible(handle), Math.Min(timeoutMs, 1200), 80);
                message = ok ? "单击验收通过（窗口仍可见）。" : "单击验收失败（窗口不可见或句柄失效）。";
                return ok;
            }

            if (normalizedAction == "double_click")
            {
                var ok = WaitUntil(() => IsWindow(handle) && IsWindowVisible(handle), Math.Min(timeoutMs, 1200), 80);
                message = ok ? "双击验收通过（窗口仍可见）。" : "双击验收失败（窗口不可见或句柄失效）。";
                return ok;
            }

            if (normalizedAction == "right_click")
            {
                var ok = WaitUntil(() => IsWindow(handle) && IsWindowVisible(handle), Math.Min(timeoutMs, 1200), 80);
                message = ok ? "右击验收通过（窗口仍可见）。" : "右击验收失败（窗口不可见或句柄失效）。";
                return ok;
            }

            if (normalizedAction == "set_text")
            {
                var expected = text ?? string.Empty;
                var lastObservedText = string.Empty;
                var ok = WaitUntil(() =>
                {
                    var current = ReadControlText(handle);
                    lastObservedText = current;
                    if (string.IsNullOrWhiteSpace(expected))
                        return true;
                    var normalizedCurrent = NormalizeTextForCompare(current);
                    var normalizedExpected = NormalizeTextForCompare(expected);
                    if (string.IsNullOrWhiteSpace(normalizedCurrent))
                        return false;
                    if (string.Equals(normalizedCurrent, normalizedExpected, StringComparison.Ordinal))
                        return true;
                    return normalizedCurrent.Contains(normalizedExpected, StringComparison.Ordinal);
                }, timeoutMs, 60);
                message = ok
                    ? "文本写入验收通过。"
                    : $"文本写入验收失败，当前控件文本：{TrimForLog(lastObservedText)}";
                return ok;
            }

            if (normalizedAction == "close")
            {
                var ok = WaitUntil(() => !IsWindow(handle) || !IsWindowVisible(handle), timeoutMs, 80);
                message = ok ? "关闭验收通过。" : "关闭验收失败（窗口仍存在）。";
                return ok;
            }

            message = "未知动作，无法验收。";
            return false;
        }

        private static bool WaitUntil(Func<bool> predicate, int timeoutMs, int pollMs)
        {
            if (predicate == null)
                return false;

            var timeout = Math.Max(100, timeoutMs);
            var poll = Math.Max(20, pollMs);
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds <= timeout)
            {
                try
                {
                    if (predicate())
                        return true;
                }
                catch
                {
                }

                Thread.Sleep(poll);
            }
            return false;
        }

        private static string BuildDeduplicatedResult(string cachedJson)
        {
            if (string.IsNullOrWhiteSpace(cachedJson))
                return cachedJson;

            try
            {
                var jo = JsonConvert.DeserializeObject<Dictionary<string, object>>(cachedJson) ?? new Dictionary<string, object>();
                jo["deduplicated"] = true;
                return JsonConvert.SerializeObject(jo);
            }
            catch
            {
                return cachedJson;
            }
        }

        private static string BuildError(string where, string message, object detail = null)
        {
            return JsonConvert.SerializeObject(new
            {
                type = "error",
                ok = false,
                where,
                message,
                detail
            });
        }

        private static WindowNode SelectTargetWindow(List<WindowNode> windows, long foreground, string titleContains)
        {
            if (windows == null || windows.Count == 0)
                return null;

            if (!string.IsNullOrWhiteSpace(titleContains))
            {
                var found = windows.FirstOrDefault(w => ContainsIgnoreCase(w.Title, titleContains));
                if (found != null) return found;
            }

            var fg = windows.FirstOrDefault(w => w.Hwnd == foreground);
            if (fg != null && !IsDesktopShellWindow(fg))
                return fg;

            var fallback = windows.FirstOrDefault(IsPreferredObserveTarget);
            return fallback ?? fg ?? windows[0];
        }

        private static List<WindowNode> ResolveWindowsBySelector(string titleContains, string className, string processName)
        {
            var windows = GetTopWindows(forceRefresh: true);
            var filtered = windows.Where(w =>
                (string.IsNullOrWhiteSpace(titleContains) || ContainsIgnoreCase(w.Title, titleContains)) &&
                (string.IsNullOrWhiteSpace(className) || ContainsIgnoreCase(w.ClassName, className)) &&
                (string.IsNullOrWhiteSpace(processName) || ContainsIgnoreCase(w.ProcessName, processName)))
                .ToList();

            return filtered;
        }

        private static List<WindowNode> GetTopWindows(bool forceRefresh)
        {
            var now = DateTime.UtcNow;
            lock (s_cacheLock)
            {
                if (!forceRefresh &&
                    s_cachedTopWindows.Count > 0 &&
                    (now - s_cachedTopWindowsAtUtc) <= TopWindowCacheTtl)
                {
                    Interlocked.Increment(ref s_cacheHitCount);
                    return CloneWindows(s_cachedTopWindows);
                }
            }

            var fresh = EnumerateTopWindowsRaw();
            var fingerprint = ComputeFingerprint(fresh);

            lock (s_cacheLock)
            {
                Interlocked.Increment(ref s_cacheMissCount);
                if (fingerprint != s_cachedFingerprint)
                {
                    s_monitorVersion++;
                    s_cachedFingerprint = fingerprint;
                }
                s_cachedTopWindows = CloneWindows(fresh);
                s_cachedTopWindowsAtUtc = now;
                return CloneWindows(s_cachedTopWindows);
            }
        }

        private static int GetMonitorVersion()
        {
            lock (s_cacheLock)
            {
                return s_monitorVersion;
            }
        }

        private static int ComputeFingerprint(List<WindowNode> windows)
        {
            unchecked
            {
                var hash = 17;
                hash = hash * 31 + (windows?.Count ?? 0);
                if (windows != null)
                {
                    for (var i = 0; i < windows.Count && i < 12; i++)
                    {
                        var w = windows[i];
                        hash = hash * 31 + w.Hwnd.GetHashCode();
                        hash = hash * 31 + (w.Title?.GetHashCode() ?? 0);
                        hash = hash * 31 + (w.ClassName?.GetHashCode() ?? 0);
                    }
                }
                return hash;
            }
        }

        private static List<WindowNode> CloneWindows(List<WindowNode> source)
        {
            return source?.Select(x => x.Clone()).ToList() ?? new List<WindowNode>();
        }

        private static List<WindowNode> EnumerateTopWindowsRaw()
        {
            var result = new List<WindowNode>();
            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd))
                    return true;

                if (!GetWindowRect(hWnd, out var rect))
                    return true;

                var width = Math.Max(0, rect.Right - rect.Left);
                var height = Math.Max(0, rect.Bottom - rect.Top);
                if (width <= 0 || height <= 0)
                    return true;

                var title = GetWindowTitle(hWnd);
                var className = GetWindowClass(hWnd);
                if (string.IsNullOrWhiteSpace(title) && !IsUsefulUntitledTopWindow(className))
                    return true;

                result.Add(BuildWindowNode(hWnd, title, className, rect));
                return true;
            }, IntPtr.Zero);

            var fg = ToHwndLong(GetForegroundWindow());
            var foregroundIndex = result.FindIndex(x => x.Hwnd == fg);
            if (foregroundIndex > 0)
            {
                var foregroundWindow = result[foregroundIndex];
                result.RemoveAt(foregroundIndex);
                result.Insert(0, foregroundWindow);
            }

            return result;
        }

        private static List<WindowNode> EnumerateChildWindows(IntPtr parent, int maxChildren)
        {
            var result = new List<WindowNode>();
            if (parent == IntPtr.Zero || maxChildren <= 0)
                return result;

            EnumChildWindows(parent, (hWnd, _) =>
            {
                if (result.Count >= maxChildren)
                    return false;

                if (!GetWindowRect(hWnd, out var rect))
                    return true;

                var title = GetWindowTitle(hWnd);
                var cls = GetWindowClass(hWnd);
                if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(cls))
                    return true;

                result.Add(BuildWindowNode(hWnd, title, rect));
                return true;
            }, IntPtr.Zero);

            return result;
        }

        private static WindowNode BuildWindowNode(IntPtr hWnd, string title, RECT rect)
        {
            return BuildWindowNode(hWnd, title, string.Empty, rect);
        }

        private static WindowNode BuildWindowNode(IntPtr hWnd, string title, string className, RECT rect)
        {
            var processName = string.Empty;
            try
            {
                GetWindowThreadProcessId(hWnd, out var pid);
                if (pid > 0)
                    processName = Process.GetProcessById((int)pid).ProcessName ?? string.Empty;
            }
            catch
            {
                processName = string.Empty;
            }

            var resolvedClassName = string.IsNullOrWhiteSpace(className)
                ? GetWindowClass(hWnd)
                : className;
            var resolvedTitle = title ?? string.Empty;
            if (string.IsNullOrWhiteSpace(resolvedTitle) &&
                string.Equals(resolvedClassName, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase))
            {
                resolvedTitle = "任务栏";
            }

            return new WindowNode
            {
                Hwnd = ToHwndLong(hWnd),
                Title = resolvedTitle,
                ClassName = resolvedClassName,
                ProcessName = processName,
                Left = rect.Left,
                Top = rect.Top,
                Width = Math.Max(0, rect.Right - rect.Left),
                Height = Math.Max(0, rect.Bottom - rect.Top),
                Enabled = IsWindowEnabled(hWnd),
                Visible = IsWindowVisible(hWnd)
            };
        }

        private static bool IsPreferredObserveTarget(WindowNode window)
        {
            return window != null
                && window.Visible
                && window.Width >= 160
                && window.Height >= 120
                && !IsDesktopShellWindow(window);
        }

        private static bool IsDesktopShellWindow(WindowNode window)
        {
            if (window == null)
                return false;

            var className = (window.ClassName ?? string.Empty).Trim();
            if (string.Equals(className, "Progman", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(className, "WorkerW", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(className, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase))
                return true;

            var title = (window.Title ?? string.Empty).Trim();
            return string.Equals(title, "Program Manager", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsUsefulUntitledTopWindow(string className)
        {
            return string.Equals(className, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsIgnoreCase(string value, string keyword)
        {
            if (string.IsNullOrWhiteSpace(keyword))
                return true;
            if (string.IsNullOrWhiteSpace(value))
                return false;
            return value.IndexOf(keyword.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool MatchesWindowQuery(WindowNode window, string query)
        {
            if (window == null)
                return false;

            foreach (var keyword in ExpandWindowQueryKeywords(query))
            {
                if (ContainsIgnoreCase(window.Title, keyword) ||
                    ContainsIgnoreCase(window.ProcessName, keyword) ||
                    ContainsIgnoreCase(window.ClassName, keyword))
                {
                    return true;
                }
            }

            return false;
        }

        private static IEnumerable<string> ExpandWindowQueryKeywords(string query)
        {
            var normalized = (query ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
                return Array.Empty<string>();

            var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                normalized
            };

            if (ContainsIgnoreCase(normalized, "记事本") || ContainsIgnoreCase(normalized, "notepad"))
            {
                values.Add("记事本");
                values.Add("notepad");
            }

            if (ContainsIgnoreCase(normalized, "资源管理器") ||
                ContainsIgnoreCase(normalized, "文件资源管理器") ||
                ContainsIgnoreCase(normalized, "explorer"))
            {
                values.Add("资源管理器");
                values.Add("文件资源管理器");
                values.Add("explorer");
            }

            if (normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                values.Add(normalized.Substring(0, normalized.Length - 4));

            return values;
        }

        private static string GetWindowTitle(IntPtr hWnd)
        {
            var length = GetWindowTextLength(hWnd);
            if (length <= 0)
                return string.Empty;

            var sb = new StringBuilder(length + 1);
            _ = GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private static string GetWindowClass(IntPtr hWnd)
        {
            var sb = new StringBuilder(256);
            _ = GetClassName(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private static string ReadControlText(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero || !IsWindow(hWnd))
                return string.Empty;

            try
            {
                var lengthResult = SendMessage(hWnd, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero);
                var length = Math.Clamp(lengthResult.ToInt32(), 0, 32767);
                if (length <= 0)
                    return string.Empty;

                var sb = new StringBuilder(length + 1);
                _ = SendMessage(hWnd, WM_GETTEXT, (IntPtr)sb.Capacity, sb);
                return sb.ToString();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string NormalizeTextForCompare(string value)
        {
            return (value ?? string.Empty)
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Trim();
        }

        private static string TrimForLog(string value, int maxLength = 80)
        {
            var normalized = NormalizeTextForCompare(value);
            if (string.IsNullOrWhiteSpace(normalized))
                return "<empty>";

            return normalized.Length <= maxLength
                ? normalized
                : normalized[..Math.Max(0, maxLength - 3)] + "...";
        }

        private static long ToHwndLong(IntPtr hWnd) => hWnd.ToInt64();

        private sealed class WindowNode
        {
            public long Hwnd { get; set; }
            public string Title { get; set; } = string.Empty;
            public string ClassName { get; set; } = string.Empty;
            public string ProcessName { get; set; } = string.Empty;
            public int Left { get; set; }
            public int Top { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public bool Enabled { get; set; }
            public bool Visible { get; set; }

            public WindowNode Clone()
            {
                return new WindowNode
                {
                    Hwnd = Hwnd,
                    Title = Title,
                    ClassName = ClassName,
                    ProcessName = ProcessName,
                    Left = Left,
                    Top = Top,
                    Width = Width,
                    Height = Height,
                    Enabled = Enabled,
                    Visible = Visible
                };
            }
        }

        private sealed class InstalledAppCandidate
        {
            public string DisplayName { get; set; } = string.Empty;
            public string ExecutablePath { get; set; } = string.Empty;
            public string ProcessName { get; set; } = string.Empty;
            public string WorkingDirectory { get; set; } = string.Empty;
            public string InstallLocation { get; set; } = string.Empty;
            public string Source { get; set; } = string.Empty;
            public List<string> Aliases { get; set; } = new();

            public InstalledAppCandidate Clone()
            {
                return new InstalledAppCandidate
                {
                    DisplayName = DisplayName,
                    ExecutablePath = ExecutablePath,
                    ProcessName = ProcessName,
                    WorkingDirectory = WorkingDirectory,
                    InstallLocation = InstallLocation,
                    Source = Source,
                    Aliases = Aliases?.ToList() ?? new List<string>()
                };
            }
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint Type;
            public INPUTUNION Data;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUTUNION
        {
            [FieldOffset(0)]
            public MOUSEINPUT Mouse;

            [FieldOffset(0)]
            public KEYBDINPUT Keyboard;

            [FieldOffset(0)]
            public HARDWAREINPUT Hardware;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort Vk;
            public ushort Scan;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int X;
            public int Y;
            public uint MouseData;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT
        {
            public uint Msg;
            public ushort ParamL;
            public ushort ParamH;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowEnabled(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsZoomed(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr SetActiveWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, string lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, StringBuilder lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
    }
}

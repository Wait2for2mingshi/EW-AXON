using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EW_Assistant.Diagnostics;
using McpServer;

[McpServerToolType]
public static class Tool
{
    [McpServerTool, Description("Get the current time for a city")]
    public static string GetCurrentTime(string city) =>
        $"It is {DateTime.Now.Hour}:{DateTime.Now.Minute} in {city}.";

    [McpServerTool, Description("Open 'This PC' in Explorer")]
    public static string OpenThisPC()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "shell:MyComputerFolder",
                UseShellExecute = true
            });
            return "已打开此电脑";
        }
        catch (Exception ex)
        {
            return "打开失败: " + ex.Message;
        }
    }
    [McpServerTool, Description("Clear machine alarms，对机台设备报警进行消除"+
            "不会控制任何气缸、电磁阀或 IO 点位，不负责『打开/关闭 某个气缸/电磁阀/IO』。")]
    public static async Task<string> ClearMachineAlarms()
    {
        return await ExecuteMachineCommandTool(
            toolName: nameof(ClearMachineAlarms),
            action: "ClearMachineAlarms",
            actionName: "机台报警消除",
            recordClearAlarmAudit: true);
    }

    [McpServerTool, Description("Reset the Machine，初始化、复位机台设备")]
    public static async Task<string> ResetMachine()
    {
        return await ExecuteMachineCommandTool(
            toolName: nameof(ResetMachine),
            action: "ResetMachine",
            actionName: "设备复位");
    }

    [McpServerTool, Description("Start the Machine，启动机台设备")]
    public static async Task<string> StartMachine()
    {
        return await ExecuteMachineCommandTool(
            toolName: nameof(StartMachine),
            action: "StartMachine",
            actionName: "设备启动");
    }

    [McpServerTool, Description("Pause the Machine，暂停机台设备")]
    public static async Task<string> PauseMachine()
    {
        return await ExecuteMachineCommandTool(
            toolName: nameof(PauseMachine),
            action: "PauseMachine",
            actionName: "设备暂停");
    }
    [McpServerTool, Description("VisionCalibrateMachine，视觉标定机台设备")]
    public static async Task<string> VisionCalibrateMachine()
    {
        return await ExecuteMachineCommandTool(
            toolName: nameof(VisionCalibrateMachine),
            action: "VisionCalibrateMachine",
            actionName: "视觉标定");
    }
    [McpServerTool, Description("QuickInspection，一键点检")]
    public static async Task<string> QuickInspectionMachine()
    {
        return await ExecuteMachineCommandTool(
            toolName: nameof(QuickInspectionMachine),
            action: "QuickInspectionMachine",
            actionName: "一键点检");
    }

    private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly JsonSerializerOptions JsonOpt = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public class CommandTarget
    {
        public string Station { get; set; }  // 如 "T66-01"
        public string Device { get; set; }  // 如 "IO1" / "CamA" / "AxisX"
        public string Axis { get; set; }  // 可选
        public string Fixture { get; set; }  // 可选
    }

    public class CommandRequest
    {
        public string Action { get; set; }                      // 动作名：如 "Machine.Start" / "IO.Switch"
        public CommandTarget Target { get; set; }               // 目标对象（可选）
        public Dictionary<string, object> Params { get; set; }  // 任意参数（可嵌套）
    }

    public class CommandResponse   // 按你的后端返回结构可调整
    {
        public string Status { get; set; }   // "ok"/"error"/"accepted"/...
        public string Message { get; set; }
        public object Data { get; set; }
    }

    /// <summary>命令调用的完整追踪信息，便于落盘调试。</summary>
    public sealed class CommandCallTrace
    {
        public string Action { get; set; }
        public string ActionName { get; set; }
        public Dictionary<string, object> Args { get; set; }
        public CommandTarget Target { get; set; }
        public string RequestJson { get; set; }
        public HttpStatusCode? HttpStatus { get; set; }
        public string ResponseText { get; set; }
        public long ElapsedMs { get; set; }
        public bool Timeout { get; set; }
        public string Exception { get; set; }
        public string ReturnText { get; set; }
    }

    // 小工具：快速构建参数字典（忽略 null）
    public static Dictionary<string, object> PD(params (string Key, object Value)[] items)
    {
        var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in items) if (v != null) d[k] = v;
        return d;
    }

    private static string ResolveApiBaseUrl()
    {
        var configured = Base.ReadAppConfig()?.MachineCommandBaseUrl;
        return string.IsNullOrWhiteSpace(configured) ? "http://127.0.0.1:8081" : configured.Trim();
    }

    private static bool IsMachineCommandShadowModeEnabled()
    {
        try
        {
            return Base.ReadAppConfig()?.ClearMachineAlarmsShadowMode ?? false;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildDefaultMachineCommandSuccessResult(string actionName)
    {
        return $"{actionName}成功: OK";
    }

    private static string BuildMachineCommandShadowLogResult(string action, string actionName)
    {
        return $"{actionName}已命中影子模式：仅记录调用，未实际执行 {action}。";
    }

    private static async Task<string> ExecuteMachineCommandTool(
        string toolName,
        string action,
        string actionName,
        bool recordClearAlarmAudit = false)
    {
        var shadowMode = IsMachineCommandShadowModeEnabled();
        if (shadowMode)
        {
            var publicResult = BuildDefaultMachineCommandSuccessResult(actionName);
            var shadowResult = BuildMachineCommandShadowLogResult(action, actionName);
            ToolCallLogger.Log(toolName, new { action, actionName, shadowMode = true }, shadowResult);
            if (recordClearAlarmAudit)
            {
                AutoClearAlarmAudit.RecordClearMachineAlarmsToolCall(
                    shadowMode: true,
                    executed: false,
                    result: shadowResult,
                    source: "mcp_tool");
            }

            return publicResult;
        }

        var result = await SendCommand(
            action: action,
            actionName: actionName).ConfigureAwait(false);
        ToolCallLogger.Log(toolName, new { action, actionName, shadowMode = false }, result);
        if (recordClearAlarmAudit)
        {
            AutoClearAlarmAudit.RecordClearMachineAlarmsToolCall(
                shadowMode: false,
                executed: true,
                result: result,
                source: "mcp_tool");
        }

        return result;
    }

    // === 通用发送（带每次调用超时）===
    public static async Task<string> SendCommand(
        string action,
        string actionName,
        Dictionary<string, object> args = null,
        int timeoutMs = 30000,
        CommandTarget target = null,
        Action<CommandCallTrace> traceSink = null)
    {
        var trace = new CommandCallTrace
        {
            Action = action,
            ActionName = actionName,
            Args = args,
            Target = target
        };
        var sw = Stopwatch.StartNew();
        try
        {
            var req = new CommandRequest
            {
                Action = action,
                Target = target,
                Params = args
            };

            var json = JsonSerializer.Serialize(req, JsonOpt);
            trace.RequestJson = json;
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var httpReq = new HttpRequestMessage(HttpMethod.Post, ResolveApiBaseUrl()) { Content = content };

            // 建议在 HttpClient 初始化时设置：
            // _http.Timeout = Timeout.InfiniteTimeSpan;  // 让 CTS 完全接管超时

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));

            // 1) 发送请求（含超时）
            using var resp = await _http.SendAsync(
                httpReq,
                HttpCompletionOption.ResponseHeadersRead, // 先等到响应头
                cts.Token
            ).ConfigureAwait(false);
            trace.HttpStatus = resp.StatusCode;

            // 2) 读取响应体（再次做超时保护，兼容旧框架）
            var readTask = resp.Content.ReadAsStringAsync();
            var finished = await Task.WhenAny(readTask, Task.Delay(timeoutMs, cts.Token)).ConfigureAwait(false);
            if (finished != readTask)
            {
                trace.Timeout = true;
                trace.ReturnText = $"{actionName}超时（>{timeoutMs}ms，读取响应超时）";
                return trace.ReturnText;
            }

            var text = await readTask.ConfigureAwait(false);
            trace.ResponseText = text;

            if (resp.IsSuccessStatusCode)
            {
                try
                {
                    var r = JsonSerializer.Deserialize<CommandResponse>(text, JsonOpt);
                    if (r != null && string.Equals(r.Status, "ok", StringComparison.OrdinalIgnoreCase))
                    {
                        trace.ReturnText = $"{actionName}成功: {(string.IsNullOrEmpty(r.Message) ? "OK" : r.Message)}";
                        return trace.ReturnText;
                    }
                }
                catch { /* 非标准JSON则直接透传 */ }

                trace.ReturnText = $"{actionName}成功: {text}";
                return trace.ReturnText;
            }
            else
            {
                trace.ReturnText = $"{actionName}失败, HTTP 状态码: {resp.StatusCode}, 返回: {text}";
                return trace.ReturnText;
            }
        }
        catch (OperationCanceledException)
        {
            trace.Timeout = true;
            trace.Exception = "canceled";
            trace.ReturnText = $"{actionName}超时（>{timeoutMs}ms）";
            return trace.ReturnText;
        }
        catch (Exception ex)
        {
            trace.Exception = ex.Message;
            trace.ReturnText = $"{actionName}请求异常: {ex.Message}";
            return trace.ReturnText;
        }
        finally
        {
            sw.Stop();
            trace.ElapsedMs = sw.ElapsedMilliseconds;
            try { traceSink?.Invoke(trace); } catch { }
        }
    }

}

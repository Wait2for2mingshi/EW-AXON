using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace EW_Assistant.McpTools
{
    /// <summary>
    /// 面向 Assistant 的本地 UI 运行时桥接接口，负责观察、执行、验收与通用本地命令。
    /// </summary>
    internal static class UiRuntimeHttpBridge
    {
        public static void Map(WebApplication app)
        {
            app.MapPost("/runtime/observe", async (UiRuntimeObserveRequest req) =>
            {
                var json = await UiRuntimeService.ObserveAsync(req).ConfigureAwait(false);
                return AsJsonContent(json);
            });

            app.MapPost("/runtime/capture", async (HttpContext http, UiRuntimeCaptureRequest req) =>
            {
                var result = await UiRuntimeService.CaptureAsync(req).ConfigureAwait(false);
                if (!result.Ok)
                    return AsJsonContent(result.ErrorJson);

                http.Response.Headers["X-Ui-Runtime-Lane"] = result.Lane;
                http.Response.Headers["X-Ui-Runtime-Scope"] = result.Scope;
                http.Response.Headers["X-Ui-Runtime-Target-Title"] = Uri.EscapeDataString(result.TargetTitle ?? string.Empty);
                http.Response.Headers["X-Ui-Runtime-Capture-Left"] = result.CaptureLeft.ToString();
                http.Response.Headers["X-Ui-Runtime-Capture-Top"] = result.CaptureTop.ToString();
                http.Response.Headers["X-Ui-Runtime-Capture-Width"] = result.CaptureWidth.ToString();
                http.Response.Headers["X-Ui-Runtime-Capture-Height"] = result.CaptureHeight.ToString();
                return Results.File(result.ImageBytes, "image/png", fileDownloadName: result.FileName);
            });

            app.MapPost("/runtime/execute", async (UiRuntimeExecuteRequest req) =>
            {
                var json = await UiRuntimeService.ExecuteAsync(req).ConfigureAwait(false);
                return AsJsonContent(json);
            });

            app.MapPost("/runtime/verify", async (UiRuntimeVerifyRequest req) =>
            {
                var json = await UiRuntimeService.VerifyAsync(req).ConfigureAwait(false);
                return AsJsonContent(json);
            });

            app.MapPost("/runtime/command", async (UiRuntimeCommandRequest req) =>
            {
                var json = await UiRuntimeService.CommandAsync(req).ConfigureAwait(false);
                return AsJsonContent(json);
            });

            app.MapPost("/io/query-status", async (HttpContext http) =>
            {
                await WriteIoQueryStatusAsync(http).ConfigureAwait(false);
            });

            app.MapPost("/runtime/io/query-status", async (HttpContext http) =>
            {
                await WriteIoQueryStatusAsync(http).ConfigureAwait(false);
            });

            app.MapPost("/io/command", async (HttpContext http) =>
            {
                await WriteIoCommandAsync(http).ConfigureAwait(false);
            });

            app.MapPost("/runtime/io/command", async (HttpContext http) =>
            {
                await WriteIoCommandAsync(http).ConfigureAwait(false);
            });

            app.MapPost("/machine/clear-alarms", async (HttpContext http) =>
            {
                await WriteClearMachineAlarmsAsync(http).ConfigureAwait(false);
            });

            app.MapPost("/runtime/machine/clear-alarms", async (HttpContext http) =>
            {
                await WriteClearMachineAlarmsAsync(http).ConfigureAwait(false);
            });

            app.MapPost("/auto-vision/skip-slot", async (HttpContext http) =>
            {
                await WriteAutoVisionCommandAsync(http, "AutoVisionSkipSlot", "跳过当前穴位").ConfigureAwait(false);
            });

            app.MapPost("/runtime/auto-vision/skip-slot", async (HttpContext http) =>
            {
                await WriteAutoVisionCommandAsync(http, "AutoVisionSkipSlot", "跳过当前穴位").ConfigureAwait(false);
            });

            app.MapPost("/auto-vision/retry-pick", async (HttpContext http) =>
            {
                await WriteAutoVisionCommandAsync(http, "AutoVisionRetryPick", "重取物料").ConfigureAwait(false);
            });

            app.MapPost("/runtime/auto-vision/retry-pick", async (HttpContext http) =>
            {
                await WriteAutoVisionCommandAsync(http, "AutoVisionRetryPick", "重取物料").ConfigureAwait(false);
            });

            app.MapPost("/auto-vision/retry", async (HttpContext http) =>
            {
                await WriteAutoVisionCommandAsync(http, "AutoVisionRetryPick", "重取物料").ConfigureAwait(false);
            });

            app.MapPost("/runtime/auto-vision/retry", async (HttpContext http) =>
            {
                await WriteAutoVisionCommandAsync(http, "AutoVisionRetryPick", "重取物料").ConfigureAwait(false);
            });
        }

        private static IResult AsJsonContent(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                json = new JObject
                {
                    ["type"] = "error",
                    ["where"] = "UiRuntimeHttpBridge",
                    ["message"] = "empty response"
                }.ToString();
            }

            return Results.Content(json, "application/json", Encoding.UTF8);
        }

        private static async System.Threading.Tasks.Task WriteIoQueryStatusAsync(HttpContext http)
        {
            var json = string.Empty;
            try
            {
                var body = await ReadJsonBodyAsync(http).ConfigureAwait(false);
                var ioNames = NormalizeIoNames(body);
                json = await IoMcpTools.IoQueryStatus(ioNames).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(json))
                {
                    json = new JObject
                    {
                        ["type"] = "error",
                        ["where"] = "IoQueryStatusHttp",
                        ["message"] = "empty response"
                    }.ToString();
                }

                http.Response.StatusCode = StatusCodes.Status200OK;
                http.Response.ContentType = "application/json; charset=utf-8";
                await http.Response.WriteAsync(json, Encoding.UTF8).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                json = new JObject
                {
                    ["type"] = "error",
                    ["where"] = "IoQueryStatusHttp",
                    ["message"] = ex.Message
                }.ToString();

                http.Response.StatusCode = StatusCodes.Status500InternalServerError;
                http.Response.ContentType = "application/json; charset=utf-8";
                await http.Response.WriteAsync(json, Encoding.UTF8).ConfigureAwait(false);
            }
        }

        private static async System.Threading.Tasks.Task WriteIoCommandAsync(HttpContext http)
        {
            var json = string.Empty;
            try
            {
                var body = await ReadJsonBodyAsync(http).ConfigureAwait(false);
                var command = NormalizeIoCommand(body);
                var ioName = command.ioName;
                var op = command.op;

                if (string.IsNullOrWhiteSpace(ioName) || string.IsNullOrWhiteSpace(op))
                {
                    var skipReason = string.IsNullOrWhiteSpace(ioName) && string.IsNullOrWhiteSpace(op)
                        ? "empty_plan"
                        : "invalid_plan";
                    json = BuildIoCommandSkippedResult(skipReason, ioName, op);
                    global::McpServer.ToolCallLogger.Log(
                        "IoCommandHttp",
                        new { ioName, op, skipReason },
                        json);

                    http.Response.StatusCode = StatusCodes.Status200OK;
                    http.Response.ContentType = "application/json; charset=utf-8";
                    await http.Response.WriteAsync(json, Encoding.UTF8).ConfigureAwait(false);
                    return;
                }

                json = await IoMcpTools.IoCommand(ioName, op).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(json))
                {
                    json = new JObject
                    {
                        ["type"] = "error",
                        ["where"] = "IoCommandHttp",
                        ["message"] = "empty response"
                    }.ToString();
                }

                http.Response.StatusCode = StatusCodes.Status200OK;
                http.Response.ContentType = "application/json; charset=utf-8";
                await http.Response.WriteAsync(json, Encoding.UTF8).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                json = new JObject
                {
                    ["type"] = "error",
                    ["where"] = "IoCommandHttp",
                    ["message"] = ex.Message
                }.ToString();

                http.Response.StatusCode = StatusCodes.Status500InternalServerError;
                http.Response.ContentType = "application/json; charset=utf-8";
                await http.Response.WriteAsync(json, Encoding.UTF8).ConfigureAwait(false);
            }
        }

        private static string BuildIoCommandSkippedResult(string skipReason, string ioName, string op)
        {
            var message = skipReason == "empty_plan"
                ? "修复计划为空，未执行 IO 写入。"
                : "修复计划缺少 io_name 或 op，未执行 IO 写入。";

            return new JObject
            {
                ["type"] = "io.command",
                ["success"] = false,
                ["ok"] = false,
                ["executed"] = false,
                ["skipped"] = true,
                ["skipReason"] = skipReason ?? string.Empty,
                ["ioName"] = ioName ?? string.Empty,
                ["actionCode"] = op ?? string.Empty,
                ["verificationStatus"] = "skipped",
                ["verificationText"] = message,
                ["resultText"] = message,
                ["message"] = message
            }.ToString();
        }

        private static async System.Threading.Tasks.Task WriteClearMachineAlarmsAsync(HttpContext http)
        {
            try
            {
                var result = await global::Tool.ClearMachineAlarms().ConfigureAwait(false);
                var success = IsSuccessfulMachineCommandResult(result);
                var json = new JObject
                {
                    ["ok"] = success,
                    ["success"] = success,
                    ["action"] = "ClearMachineAlarms",
                    ["executed"] = true,
                    ["result"] = result ?? string.Empty,
                    ["message"] = result ?? string.Empty
                }.ToString();

                http.Response.StatusCode = StatusCodes.Status200OK;
                http.Response.ContentType = "application/json; charset=utf-8";
                await http.Response.WriteAsync(json, Encoding.UTF8).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var json = new JObject
                {
                    ["ok"] = false,
                    ["success"] = false,
                    ["action"] = "ClearMachineAlarms",
                    ["executed"] = false,
                    ["where"] = "ClearMachineAlarmsHttp",
                    ["message"] = ex.Message
                }.ToString();

                http.Response.StatusCode = StatusCodes.Status500InternalServerError;
                http.Response.ContentType = "application/json; charset=utf-8";
                await http.Response.WriteAsync(json, Encoding.UTF8).ConfigureAwait(false);
            }
        }

        private static async System.Threading.Tasks.Task WriteAutoVisionCommandAsync(
            HttpContext http,
            string action,
            string actionName)
        {
            try
            {
                var execution = await ExecuteAutoVisionCommandAsync(action, actionName).ConfigureAwait(false);
                var text = new JObject { ["success"] = execution.Success }.ToString();
                global::McpServer.ToolCallLogger.Log(
                    action ?? "AutoVisionCommandHttp",
                    new
                    {
                        action = execution.Action,
                        actionName = execution.ActionName,
                        shadowMode = execution.ShadowMode,
                        executed = execution.Executed
                    },
                    BuildAutoVisionCommandLogJson(execution));

                http.Response.StatusCode = StatusCodes.Status200OK;
                http.Response.ContentType = "application/json; charset=utf-8";
                await http.Response.WriteAsync(text, Encoding.UTF8).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var text = new JObject { ["success"] = false }.ToString();
                global::McpServer.ToolCallLogger.Log(
                    action ?? "AutoVisionCommandHttp",
                    new { action, actionName },
                    text,
                    ex.Message);

                http.Response.StatusCode = StatusCodes.Status500InternalServerError;
                http.Response.ContentType = "application/json; charset=utf-8";
                await http.Response.WriteAsync(text, Encoding.UTF8).ConfigureAwait(false);
            }
        }

        private static System.Threading.Tasks.Task<AutoVisionCommandExecutionResult> ExecuteAutoVisionCommandAsync(
            string action,
            string actionName)
        {
            var shadowMode = IsAutoVisionCommandShadowModeEnabled();
            if (shadowMode)
            {
                return System.Threading.Tasks.Task.FromResult(new AutoVisionCommandExecutionResult
                {
                    Success = true,
                    Action = action ?? string.Empty,
                    ActionName = actionName ?? string.Empty,
                    ShadowMode = true,
                    Executed = false,
                    Message = "已命中影子模式：仅记录调用，未下发真实动作指令。"
                });
            }

            return ExecuteAutoVisionCommandCoreAsync(action, actionName);
        }

        private static System.Threading.Tasks.Task<AutoVisionCommandExecutionResult> ExecuteAutoVisionCommandCoreAsync(
            string action,
            string actionName)
        {
            // 真实 PLC/命令网关下发逻辑后续接在这里；当前保持联调占位受理。
            return System.Threading.Tasks.Task.FromResult(new AutoVisionCommandExecutionResult
            {
                Success = true,
                Action = action ?? string.Empty,
                ActionName = actionName ?? string.Empty,
                ShadowMode = false,
                Executed = false,
                Message = "动作指令下发入口已预留，当前按联调占位受理。"
            });
        }

        private static bool IsAutoVisionCommandShadowModeEnabled()
        {
            try
            {
                return global::McpServer.Base.ReadAppConfig()?.ClearMachineAlarmsShadowMode ?? false;
            }
            catch
            {
                return false;
            }
        }

        private static string BuildAutoVisionCommandLogJson(AutoVisionCommandExecutionResult execution)
        {
            if (execution == null)
            {
                return new JObject { ["success"] = false }.ToString();
            }

            return new JObject
            {
                ["type"] = "auto_vision.command",
                ["success"] = execution.Success,
                ["action"] = execution.Action ?? string.Empty,
                ["actionName"] = execution.ActionName ?? string.Empty,
                ["shadowMode"] = execution.ShadowMode,
                ["executed"] = execution.Executed,
                ["message"] = execution.Message ?? string.Empty
            }.ToString();
        }

        private static bool IsSuccessfulMachineCommandResult(string result)
        {
            if (string.IsNullOrWhiteSpace(result))
                return false;

            if (result.IndexOf("失败", StringComparison.OrdinalIgnoreCase) >= 0 ||
                result.IndexOf("超时", StringComparison.OrdinalIgnoreCase) >= 0 ||
                result.IndexOf("异常", StringComparison.OrdinalIgnoreCase) >= 0 ||
                result.IndexOf("未完成", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }

            return result.IndexOf("成功", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   result.IndexOf("OK", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static async System.Threading.Tasks.Task<JObject> ReadJsonBodyAsync(HttpContext http)
        {
            try
            {
                using (var reader = new StreamReader(http.Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false))
                {
                    var raw = await reader.ReadToEndAsync().ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(raw))
                        return new JObject();

                    var token = JToken.Parse(raw);
                    return token as JObject ?? new JObject();
                }
            }
            catch
            {
                return new JObject();
            }
        }

        private static string[] NormalizeIoNames(JObject req)
        {
            if (req == null)
                return Array.Empty<string>();

            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string value)
            {
                if (string.IsNullOrWhiteSpace(value))
                    return;

                var trimmed = value.Trim();
                if (seen.Add(trimmed))
                    result.Add(trimmed);
            }

            var ioNamesToken = req["ioNames"] ?? req["IoNames"] ?? req["IO_NAMES"] ?? req["io_names"];
            if (ioNamesToken is JArray ioNames)
            {
                foreach (var name in ioNames)
                    Add(name?.ToString());
            }

            var rawInput = FirstNotEmpty(
                req.Value<string>("ioInput"),
                req.Value<string>("IOInput"),
                req.Value<string>("input"));
            if (!string.IsNullOrWhiteSpace(rawInput))
            {
                foreach (var part in Regex.Split(rawInput, @"[,，;；|、\r\n\t]+"))
                    Add(part);
            }

            return result.ToArray();
        }

        private static (string ioName, string op) NormalizeIoCommand(JObject req)
        {
            if (req == null)
                return (string.Empty, string.Empty);

            var ioName = ReadIoCommandName(req);
            var op = ReadIoCommandOp(req);

            if ((string.IsNullOrWhiteSpace(ioName) || string.IsNullOrWhiteSpace(op)) &&
                TryReadEmbeddedJson(req, out var embedded))
            {
                if (string.IsNullOrWhiteSpace(ioName))
                    ioName = ReadIoCommandName(embedded);
                if (string.IsNullOrWhiteSpace(op))
                    op = ReadIoCommandOp(embedded);
            }

            return (ioName, op);
        }

        private static string ReadIoCommandName(JObject req)
        {
            if (req == null)
                return string.Empty;

            return FirstNotEmpty(
                req.Value<string>("ioName"),
                req.Value<string>("IoName"),
                req.Value<string>("IOName"),
                req.Value<string>("io_name"),
                req.Value<string>("name"),
                req.Value<string>("target"));
        }

        private static string ReadIoCommandOp(JObject req)
        {
            if (req == null)
                return string.Empty;

            return FirstNotEmpty(
                req.Value<string>("op"),
                req.Value<string>("Op"),
                req.Value<string>("operation"),
                req.Value<string>("Operation"),
                req.Value<string>("action"),
                req.Value<string>("Action"),
                req.Value<string>("intent"),
                req.Value<string>("Intent"));
        }

        private static bool TryReadEmbeddedJson(JObject req, out JObject embedded)
        {
            embedded = null;
            if (req == null)
                return false;

            var rawText = FirstNotEmpty(
                req.Value<string>("text"),
                req.Value<string>("Text"),
                req.Value<string>("output"),
                req.Value<string>("Output"),
                req.Value<string>("llm_response"),
                req.Value<string>("llmResponse"),
                req.Value<string>("llm_text"),
                req.Value<string>("llmText"));

            if (string.IsNullOrWhiteSpace(rawText))
                return false;

            rawText = StripThink(rawText.Trim());
            var fenceMatch = Regex.Match(rawText, @"^```(?:json)?\s*([\s\S]*?)\s*```$", RegexOptions.IgnoreCase);
            if (fenceMatch.Success)
                rawText = fenceMatch.Groups[1].Value.Trim();

            if (TryParseObject(rawText, out embedded))
                return true;

            var start = rawText.IndexOf('{');
            var end = rawText.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                var candidate = rawText.Substring(start, end - start + 1);
                return TryParseObject(candidate, out embedded);
            }

            return false;
        }

        private static string StripThink(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            text = Regex.Replace(
                text,
                @"<\s*think\b[^>]*>.*?</\s*think\s*>",
                string.Empty,
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            text = Regex.Replace(
                text,
                @"<\s*think\b[^>]*>.*$",
                string.Empty,
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            return text.Trim();
        }

        private static bool TryParseObject(string text, out JObject obj)
        {
            obj = null;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            try
            {
                obj = JObject.Parse(text);
                return true;
            }
            catch
            {
                obj = null;
                return false;
            }
        }

        private static string FirstNotEmpty(params string[] values)
        {
            return values?.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
        }

        private sealed class AutoVisionCommandExecutionResult
        {
            public bool Success { get; set; }
            public string Action { get; set; } = string.Empty;
            public string ActionName { get; set; } = string.Empty;
            public bool ShadowMode { get; set; }
            public bool Executed { get; set; }
            public string Message { get; set; } = string.Empty;
        }

        internal sealed class UiRuntimeObserveRequest
        {
            public string Lane { get; set; } = "default";
            public string Scope { get; set; } = "foreground_window";
            public string WindowTitleContains { get; set; } = string.Empty;
            public string DetailLevel { get; set; } = string.Empty;
            public string Goal { get; set; } = string.Empty;
            public bool? IncludeChildren { get; set; }
            public int? MaxChildren { get; set; }
            public int? MaxCandidates { get; set; }
            public bool? IncludeImages { get; set; }
        }

        internal sealed class UiRuntimeCaptureRequest
        {
            public string Lane { get; set; } = "default";
            public string Scope { get; set; } = "foreground_window";
            public string WindowTitleContains { get; set; } = string.Empty;
            public int DelayMs { get; set; }
        }

        internal sealed class UiRuntimeExecuteRequest
        {
            public string RuntimeId { get; set; } = string.Empty;
            public string TargetId { get; set; } = string.Empty;
            public string Action { get; set; } = "click";
            public string Text { get; set; } = string.Empty;
            public bool Confirmed { get; set; }
            public string Lane { get; set; } = "default";
            public string IdempotencyKey { get; set; } = string.Empty;
            public int Retries { get; set; } = 1;
            public int VerifyTimeoutMs { get; set; } = 900;
            public string VerifyTitleContains { get; set; } = string.Empty;
        }

        internal sealed class UiRuntimeVerifyRequest
        {
            public string Lane { get; set; } = "default";
            public string Scope { get; set; } = "foreground_window";
            public string WindowTitleContains { get; set; } = string.Empty;
            public string ExpectedTitleContains { get; set; } = string.Empty;
            public string ExpectedTextContains { get; set; } = string.Empty;
            public bool RequireTargetVisible { get; set; } = true;
            public bool IncludeChildren { get; set; }
            public int MaxChildren { get; set; } = 60;
        }

        internal sealed class UiRuntimeCommandRequest
        {
            public string Lane { get; set; } = "default";
            public string CommandType { get; set; } = "mouse";
            public string Operation { get; set; } = "move";
            public string Scope { get; set; } = "foreground_window";
            public string RuntimeId { get; set; } = string.Empty;
            public string TargetId { get; set; } = string.Empty;
            public int? X { get; set; }
            public int? Y { get; set; }
            public int? RelativeX { get; set; }
            public int? RelativeY { get; set; }
            public int[] Point_2d { get; set; } = Array.Empty<int>();
            public int[] Point2d { get; set; } = Array.Empty<int>();
            public int? CaptureLeft { get; set; }
            public int? CaptureTop { get; set; }
            public int? CaptureWidth { get; set; }
            public int? CaptureHeight { get; set; }
            public string Text { get; set; } = string.Empty;
            public string[] Keys { get; set; } = Array.Empty<string>();
            public string[] Modifiers { get; set; } = Array.Empty<string>();
            public string CommandLine { get; set; } = string.Empty;
            public string WorkingDirectory { get; set; } = string.Empty;
            public string FileName { get; set; } = string.Empty;
            public string AppName { get; set; } = string.Empty;
            public string BrowserName { get; set; } = string.Empty;
            public string Url { get; set; } = string.Empty;
            public string Query { get; set; } = string.Empty;
            public string Site { get; set; } = string.Empty;
            public int? MaxResults { get; set; }
            public string TargetWindowTitleContains { get; set; } = string.Empty;
            public string ExpectedTextContains { get; set; } = string.Empty;
            public bool? RequireTargetVisible { get; set; }
            public bool? IncludeChildren { get; set; }
            public int? MaxChildren { get; set; }
            public bool Confirmed { get; set; }
            public string IdempotencyKey { get; set; } = string.Empty;
            public int Retries { get; set; } = 1;
            public int? VerifyTimeoutMs { get; set; }
            public string VerifyTitleContains { get; set; } = string.Empty;
            public int? TimeoutMs { get; set; }
            public int? PollMs { get; set; }
        }
    }
}

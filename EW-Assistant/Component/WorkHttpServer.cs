using EW_Assistant.Diagnostics;
using EW_Assistant.Services;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EW_Assistant.Net
{
    /// <summary>
    /// 常驻的本地 POST 监听服务：接收外部触发的自动分析请求，转交 DifyWorkflowClient，
    /// 仅返回“是否受理”，实际结果在 UI 侧流式展示。
    /// </summary>
    public sealed class WorkHttpServer
    {
        private HttpListener _listener;
        private CancellationToken _token;
        private string _prefix;

        // 单例
        public static WorkHttpServer Instance { get; } = new WorkHttpServer();
        private WorkHttpServer() { }

        public bool IsRunning => _listener != null && _listener.IsListening;

        /// <summary>
        /// 启动 HTTP 监听（仅支持 POST），失败会写日志并抛出异常。
        /// </summary>
        public async Task StartAsync(string prefix, CancellationToken token)
        {
            if (IsRunning) return;

            _prefix = prefix;
            _token = token;

            _listener = new HttpListener();
            _listener.Prefixes.Add(prefix);
            try
            {
                _listener.Start();
                MainWindow.PostProgramInfo($"WorkHttpServer 已启动：{prefix}", "ok");
            }
            catch (HttpListenerException hlex)
            {
                MainWindow.PostProgramInfo($"监听失败：{hlex.Message}（可能需要 urlacl）", "error");
                throw;
            }

            _ = Task.Run(() => AcceptLoopAsync(), token);
        }

        /// <summary>停止监听并释放底层 HttpListener。</summary>
        public void Stop()
        {
            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }
            _listener = null;
        }

        /// <summary>
        /// 主循环：串行接受请求，具体处理交给子任务，异常写入信息流。
        /// </summary>
        private async Task AcceptLoopAsync()
        {
            while (!_token.IsCancellationRequested && IsRunning)
            {
                HttpListenerContext ctx = null;
                try
                {
                    ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                    _ = Task.Run(() => HandleContextAsync(ctx), _token);
                }
                catch (ObjectDisposedException) { break; }
                catch (HttpListenerException) { break; }
                catch (Exception ex)
                {
                    MainWindow.PostProgramInfo($"WorkHttpServer 异常：{ex.Message}", "warn");
                }
            }
        }

        /// <summary>
        /// 仅接受 POST，解析 JSON 体并尝试启动自动分析；繁忙时直接返回 429。
        /// </summary>
        private async Task HandleContextAsync(HttpListenerContext context)
        {
            string traceId = null;
            if (context.Request.HttpMethod != "POST")
            {
                context.Response.StatusCode = 405;
                await WriteJsonAsync(context, new { error = "method_not_allowed" });
                return;
            }

            string body = null;
            try
            {
                using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
                    body = await reader.ReadToEndAsync().ConfigureAwait(false);

                var jo = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);

                // 允许两种命名：lowerCamel 或 大写开头
                string errCode = (string)(jo["errorCode"] ?? jo["ErrorCode"] ?? "0");
                string prompt = (string)(jo["prompt"] ?? jo["ErrorDesc"] ?? "");
                var configuredMachine = EW_Assistant.Services.ConfigService.Current?.MachineCode;
                string machine = string.IsNullOrWhiteSpace(configuredMachine) ? string.Empty : configuredMachine.Trim();
                bool onlyMajor = (bool?)(jo["onlyMajorNodes"] ?? true) ?? true;
                var safeMachine = string.IsNullOrWhiteSpace(machine) ? "空" : machine;
                var safePrompt = string.IsNullOrWhiteSpace(prompt)
                    ? "空"
                    : prompt.Replace("\r", " ").Replace("\n", " ").Trim();
                var triggeredAt = DateTime.Now;
                traceId = AutoClearAlarmAudit.CreateTraceId();
                AutoClearAlarmAudit.StartAutoTrace(traceId, errCode, prompt, machine);
                MainWindow.NotifyAutoTriggered(traceId, machine, prompt);
                if (ConfigService.IsAutoWindowsNotificationEnabled())
                {
                    AutoWindowsNotificationService.ShowAutoTriggeredNotification(errCode, prompt);
                }

                // 关键改动：不等待 AI 结果，立刻触发，马上返回“是否受理”
                var startResult = DifyWorkflowClient.TryStartAutoAnalysisNow(
                    aiView: null,
                    errorCode: errCode,
                    prompt: prompt,
                    machineCode: machine,
                    onlyMajorNodes: onlyMajor,
                    autoTraceId: traceId,
                    autoTriggeredAt: triggeredAt
                );

                if (startResult == null || !startResult.Accepted)
                {
                    var rejectReason = string.IsNullOrWhiteSpace(startResult?.RejectReason)
                        ? (DifyWorkflowClient.IsBusy ? "busy_running" : "invalid_config")
                        : startResult.RejectReason.Trim();
                    var rejectMessage = string.IsNullOrWhiteSpace(startResult?.Message)
                        ? rejectReason
                        : startResult.Message.Trim();
                    var rejectLog = string.Equals(rejectReason, "busy_running", StringComparison.OrdinalIgnoreCase)
                        ? $"AUTO 请求已丢弃：当前已有任务执行中。machineCode={safeMachine}，报警内容：{safePrompt}"
                        : $"AUTO 请求未受理：{rejectMessage} machineCode={safeMachine}，报警内容：{safePrompt}";

                    DifyWorkflowClient.WriteLog(rejectLog);
                    AutoClearAlarmAudit.MarkAutoRejected(traceId, rejectReason);
                    MainWindow.NotifyAutoRejected(traceId, rejectReason);
                    if (string.Equals(rejectReason, "busy_running", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(rejectReason, "auto_vision_cooldown", StringComparison.OrdinalIgnoreCase))
                    {
                        context.Response.StatusCode = 429; // Too Many Requests / Busy
                        await WriteJsonAsync(context, new { ok = false, busy = true, reason = rejectReason, msg = "busy", detail = rejectMessage });
                    }
                    else
                    {
                        context.Response.StatusCode = 503; // Service Unavailable / Config not ready
                        await WriteJsonAsync(context, new { ok = false, busy = false, reason = rejectReason, msg = "service_not_ready", detail = rejectMessage });
                    }
                    return;
                }

                AutoClearAlarmAudit.MarkAutoAccepted(traceId);
                MainWindow.NotifyAutoAccepted(traceId);
                // 立即确认已受理；答案只在接收端 UI 呈现
                context.Response.StatusCode = 200;
                await WriteJsonAsync(context, new
                {
                    ok = true,
                    busy = false,
                    msg = "accepted",
                    workflow = startResult.WorkflowDisplayName,
                    workflowKey = startResult.WorkflowKeyName,
                    isVisionRelated = startResult.IsVisionRelated
                });
            }
            catch (Exception ex)
            {
                if (!string.IsNullOrWhiteSpace(traceId))
                {
                    AutoClearAlarmAudit.MarkWorkflowDispatchFailed(traceId, ex.Message);
                    MainWindow.NotifyAutoDispatchFailed(traceId, ex.Message);
                }
                context.Response.StatusCode = 500;
                await WriteJsonAsync(context, new { ok = false, error = ex.Message, body });
            }
        }

        /// <summary>统一 JSON 输出，异常也不抛出到调用方。</summary>
        private static async Task WriteJsonAsync(HttpListenerContext ctx, object obj)
        {
            var bytes = Encoding.UTF8.GetBytes(Newtonsoft.Json.JsonConvert.SerializeObject(obj));
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            try
            {
                await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            }
            finally
            {
                try { ctx.Response.OutputStream.Close(); } catch { }
            }
        }
    }
}

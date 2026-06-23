using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EW_Assistant.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EW_Assistant.Services.Reports
{
    /// <summary>
    /// 面向报表的 Workflow 客户端，传递 REPORT_TASK / REPORT_DATA_JSON 到 Dify Workflow。
    /// </summary>
    public class LlmWorkflowClient
    {
        private static readonly TimeSpan WorkflowTimeout = TimeSpan.FromMinutes(3);
        private readonly HttpClient _http;

        public LlmWorkflowClient(HttpClient http = null)
        {
            _http = http ?? new HttpClient { Timeout = WorkflowTimeout };
        }

        /// <summary>
        /// 调用 Workflow，返回 Markdown 文本（会去除 ``` 包裹）。
        /// </summary>
        public async Task<string> GenerateMarkdownAsync(string reportTask, string reportDataJson, CancellationToken token = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(reportTask) && string.IsNullOrWhiteSpace(reportDataJson))
                throw new ArgumentNullException("reportTask", "报告参数为空，无法调用 Workflow。");

            var cfg = ConfigService.Current;
            if (cfg == null || string.IsNullOrWhiteSpace(cfg.URL) || string.IsNullOrWhiteSpace(cfg.ReportKey))
            {
                throw new InvalidOperationException("LLM 地址或 ReportKey 未配置，请在设置中填写。");
            }

            var url = cfg.URL.TrimEnd('/') + "/workflows/run";

            var inputs = new Dictionary<string, object>();
            if (!string.IsNullOrWhiteSpace(reportTask)) inputs["REPORT_TASK"] = reportTask;
            if (!string.IsNullOrWhiteSpace(reportDataJson)) inputs["REPORT_DATA_JSON"] = reportDataJson;

            var payload = new Dictionary<string, object>
            {
                ["inputs"] = inputs,
                ["response_mode"] = "blocking",
                ["user"] = "report-bot"
            };

            using (var req = new HttpRequestMessage(HttpMethod.Post, url))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ReportKey);
                req.Content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

                try
                {
                    using (var resp = await _http.SendAsync(req, token).ConfigureAwait(false))
                    {
                        var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (!resp.IsSuccessStatusCode)
                        {
                            throw new InvalidOperationException("Workflow 调用失败：HTTP " + (int)resp.StatusCode + "，" + text);
                        }

                        var md = ExtractAnswer(text);
                        return NormalizeMarkdown(md);
                    }
                }
                catch (TaskCanceledException ex) when (!token.IsCancellationRequested)
                {
                    throw new InvalidOperationException("Workflow 调用超时。", ex);
                }
            }
        }

        private static string ExtractAnswer(string respText)
        {
            if (string.IsNullOrWhiteSpace(respText)) return string.Empty;
            var root = JObject.Parse(respText);
            var data = root["data"] as JObject ?? root;

            var outputs = data["outputs"] as JObject;
            if (outputs == null) return respText;

            return DifyOutputSanitizer.ExtractVisibleText(outputs, "text", "output", "answer", "result");
        }

        private static string NormalizeMarkdown(string md)
        {
            if (string.IsNullOrWhiteSpace(md)) return string.Empty;

            var text = md.Trim();
            if (text.StartsWith("```", StringComparison.Ordinal))
            {
                var newlineIdx = text.IndexOf('\n');
                if (newlineIdx >= 0)
                {
                    text = text.Substring(newlineIdx + 1);
                }

                var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
                if (lastFence >= 0)
                {
                    text = text.Substring(0, lastFence);
                }
            }

            return DifyOutputSanitizer.Clean(text);
        }
    }
}

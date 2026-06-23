using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using EW_Assistant.Diagnostics;

namespace EW_Assistant.Services
{
    /// <summary>
    /// AI 异常分析服务，仅返回文本结果，供 UI 展示与人工决策参考。
    /// </summary>
    public sealed class AiPerformanceAnalysisService
    {
        private const int MaxContextProcesses = 3;
        private const int MaxContextEvents = 5;
        private const int MaxEventDescriptionLength = 120;
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(2);
        private readonly HttpClient _http;
        private readonly string _baseUrl;
        private readonly string _apiKey;

        public AiPerformanceAnalysisService(HttpClient httpClient = null)
        {
            _http = httpClient ?? new HttpClient { Timeout = RequestTimeout };
            var cfg = ConfigService.Current;
            _baseUrl = cfg != null && !string.IsNullOrWhiteSpace(cfg.URL) ? cfg.URL.TrimEnd('/') : string.Empty;
            _apiKey = cfg != null ? cfg.PerformanceKey : string.Empty;
        }

        public async Task<string> AnalyzeAsync(AiAnalysisContext context, CancellationToken token = default(CancellationToken))
        {
            token.ThrowIfCancellationRequested();

            if (context == null) return string.Empty;

            if (string.IsNullOrWhiteSpace(_baseUrl) || string.IsNullOrWhiteSpace(_apiKey))
                return "未配置 AI 接口或密钥，无法生成分析。";

            var url = _baseUrl + "/workflows/run";
            var inputs = BuildInputs(context);
            var payload = new Dictionary<string, object>
            {
                ["inputs"] = inputs,
                ["response_mode"] = "blocking",
                ["user"] = "performance-analyzer"
            };

            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                    req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    var json = JsonConvert.SerializeObject(payload);
                    AppendLog("Request", url, BuildFriendlyRequestLog(context, inputs));

                    req.Content = new StringContent(json, Encoding.UTF8, "application/json");

                    using (var resp = await _http.SendAsync(req, token).ConfigureAwait(false))
                    {
                        token.ThrowIfCancellationRequested();
                        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        var preview = TryExtractAnswer(body);
                        AppendLog("Response", url, BuildFriendlyResponseLog(resp, preview, body));

                        if (!resp.IsSuccessStatusCode)
                            return "调用 AI 接口失败：" + resp.StatusCode;

                        var answer = TryExtractAnswer(body);
                        if (!string.IsNullOrWhiteSpace(answer))
                            return answer;

                        return string.IsNullOrWhiteSpace(body) ? string.Empty : DifyOutputSanitizer.Clean(body);
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                AppendLog("Exception", url, "性能 AI 请求已取消。");
                throw;
            }
            catch (TaskCanceledException)
            {
                AppendLog("Exception", url, "性能 AI 请求超时。");
                return "性能 AI 请求超时。";
            }
            catch (Exception ex)
            {
                AppendLog("Exception", url, ex.ToString());
                return "调用 AI 分析出错：" + ex.Message;
            }
        }

        private static Dictionary<string, object> BuildInputs(AiAnalysisContext context)
        {
            var json = BuildCompactContextJson(context);
            return new Dictionary<string, object>
            {
                ["context_json"] = json,
                ["timestamp"] = context.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                ["current_cpu_usage"] = context.CurrentCpuUsage,
                ["average_cpu_usage_5min"] = context.AverageCpuUsage5Min,
                ["top_process_summary"] = context.TopProcessSummary ?? string.Empty,
                ["event_summary"] = context.EventSummary ?? string.Empty,
                ["historical_comparison"] = context.HistoricalComparison ?? string.Empty
            };
        }

        private static string BuildCompactContextJson(AiAnalysisContext context)
        {
            if (context == null)
                return "{}";

            var compact = new
            {
                timestamp = context.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                current_cpu_usage = context.CurrentCpuUsage,
                average_cpu_usage_5min = context.AverageCpuUsage5Min,
                top_processes = BuildCompactProcesses(context.TopProcesses),
                events = BuildCompactEvents(context.RecentEvents)
            };

            return JsonConvert.SerializeObject(compact);
        }

        private static List<object> BuildCompactProcesses(IReadOnlyList<ProcessSnapshot> processes)
        {
            var result = new List<object>();
            if (processes == null || processes.Count == 0)
                return result;

            var count = Math.Min(MaxContextProcesses, processes.Count);
            for (int i = 0; i < count; i++)
            {
                var proc = processes[i];
                if (proc == null)
                    continue;

                result.Add(new
                {
                    name = proc.Name ?? string.Empty,
                    pid = proc.Pid,
                    cpu = proc.CpuUsage,
                    mem_mb = proc.MemoryMb
                });
            }

            return result;
        }

        private static List<object> BuildCompactEvents(IReadOnlyList<PerformanceEvent> eventsList)
        {
            var result = new List<object>();
            if (eventsList == null || eventsList.Count == 0)
                return result;

            var ordered = new List<PerformanceEvent>();
            for (int i = 0; i < eventsList.Count; i++)
            {
                if (eventsList[i] != null)
                    ordered.Add(eventsList[i]);
            }

            ordered.Sort((a, b) => b.StartTime.CompareTo(a.StartTime));
            var count = Math.Min(MaxContextEvents, ordered.Count);
            for (int i = 0; i < count; i++)
            {
                var evt = ordered[i];
                var description = Truncate(evt.Description ?? string.Empty, MaxEventDescriptionLength);
                result.Add(new
                {
                    type = evt.EventType ?? string.Empty,
                    time = evt.StartTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    related = evt.RelatedProcess ?? string.Empty,
                    desc = description
                });
            }

            return result;
        }

        private static string TryExtractAnswer(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return string.Empty;
            try
            {
                var obj = JObject.Parse(body);
                var answer = (string)obj["answer"];
                if (!string.IsNullOrWhiteSpace(answer)) return DifyOutputSanitizer.Clean(answer);

                var outputText = (string)obj["output_text"];
                if (!string.IsNullOrWhiteSpace(outputText)) return DifyOutputSanitizer.Clean(outputText);

                var data = obj["data"] as JObject;
                if (data != null)
                {
                    var outputs = data["outputs"] as JObject;
                    if (outputs != null)
                    {
                        var result = DifyOutputSanitizer.ExtractVisibleText(outputs, "result", "text", "answer", "output");
                        if (!string.IsNullOrWhiteSpace(result)) return DifyOutputSanitizer.Clean(result);
                    }
                }
            }
            catch
            {
                // 解析失败直接返回空
            }
            return string.Empty;
        }

        /// <summary>追加请求/响应/异常日志到 PerformanceAI 目录，失败静默。</summary>
        private static void AppendLog(string stage, string url, string content)
        {
            try
            {
                var dir = Path.Combine("D:\\", "Data", "AiLog", "PerformanceAI");
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var path = Path.Combine(dir, DateTime.Now.ToString("yyyy-MM-dd") + ".log");
                var sb = new StringBuilder();
                if (string.Equals(stage, "Request", StringComparison.OrdinalIgnoreCase))
                    sb.AppendLine("============================================================");

                sb.AppendFormat("{0:yyyy-MM-dd HH:mm:ss} [{1}] {2}", DateTime.Now, stage, url ?? string.Empty);
                sb.AppendLine();
                if (!string.IsNullOrEmpty(content))
                    sb.AppendLine(content);
                sb.AppendLine();

                File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));
                LogRetentionPolicy.TryCleanupFiles(
                    dir,
                    "*.log",
                    SearchOption.TopDirectoryOnly,
                    TimeSpan.FromDays(30));
            }
            catch
            {
                // 记录失败不影响主流程
            }
        }

        private static string BuildFriendlyRequestLog(AiAnalysisContext context, Dictionary<string, object> inputs)
        {
            if (context == null)
                return "无请求参数";

            var summary = context.EventSummary ?? string.Empty;
            var top = context.TopProcessSummary ?? string.Empty;
            return string.Format("Time={0:yyyy-MM-dd HH:mm:ss} | Cpu={1:F1}% | Avg5Min={2:F1}% | Events={3} | Top={4}",
                context.Timestamp, context.CurrentCpuUsage, context.AverageCpuUsage5Min,
                Truncate(summary.Replace(Environment.NewLine, " "), 200),
                Truncate(top.Replace(Environment.NewLine, " "), 200));
        }

        /// <summary>生成响应日志的概览文本，包含状态码与裁剪后的答案/原文。</summary>
        private static string BuildFriendlyResponseLog(HttpResponseMessage resp, string preview, string body)
        {
            var status = resp == null ? "Unknown" : string.Format("{0}({1})", (int)resp.StatusCode, resp.StatusCode);
            var head = string.Format("Status={0}", status);
            var decodedPreview = DecodeUnicode(preview);
            var answer = string.IsNullOrWhiteSpace(decodedPreview) ? "无解析结果" : Truncate(decodedPreview.Replace(Environment.NewLine, " "), 200);
            var decoded = DifyOutputSanitizer.Clean(DecodeUnicode(body));
            var raw = string.IsNullOrWhiteSpace(decoded) ? "无响应体" : Truncate(decoded, 500);
            return string.Format("{0} | AnswerPreview={1} | RawBody={2}", head, answer, raw);
        }

        private static string DecodeUnicode(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            try
            {
                return Regex.Unescape(input);
            }
            catch
            {
                return input;
            }
        }

        private static string Truncate(string input, int maxLen)
        {
            if (string.IsNullOrEmpty(input) || maxLen <= 0) return string.Empty;
            if (input.Length <= maxLen) return input;
            return input.Substring(0, maxLen) + "...";
        }
    }
}

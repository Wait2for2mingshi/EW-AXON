using EW_Assistant.Services;
using EW_Assistant.Warnings;
using EW_Assistant.Diagnostics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace EW_Assistant.Services.Warnings
{
    public sealed class WarningAnalysisResult
    {
        public bool IsSuccess { get; set; }
        public string Markdown { get; set; }
        public string ErrorMessage { get; set; }
        public bool IsTerminalFailure { get; set; }
    }

    /// <summary>
    /// 预警文本的 AI 分析服务（调用 LLM 返回 Markdown）。
    /// </summary>
    public class AiWarningAnalysisService
    {
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(2);
        private readonly HttpClient _http;
        private readonly string _baseUrl;
        private readonly string _apiKey;

        public AiWarningAnalysisService(HttpClient httpClient = null)
        {
            _http = httpClient ?? new HttpClient { Timeout = RequestTimeout };
            var cfg = ConfigService.Current;
            _baseUrl = cfg != null && !string.IsNullOrWhiteSpace(cfg.URL) ? cfg.URL.TrimEnd('/') : string.Empty;
            if (cfg != null)
            {
                _apiKey = cfg.EarlyWarningKey;
            }
            else
            {
                _apiKey = string.Empty;
            }
        }

        /// <summary>
        /// 调用 Dify Workflow 对单条预警进行分析，返回 Markdown 文本。
        /// </summary>
        public async Task<WarningAnalysisResult> AnalyzeAsync(WarningItem warning, CancellationToken token = default(CancellationToken))
        {
            token.ThrowIfCancellationRequested();

            if (warning == null)
            {
                return Failure("预警内容为空，无法生成分析。");
            }

            if (string.IsNullOrWhiteSpace(_baseUrl) || string.IsNullOrWhiteSpace(_apiKey))
            {
                return Failure("未配置 AI 接口或密钥，无法生成分析。", isTerminalFailure: true);
            }

            try
            {
                var inputs = new Dictionary<string, object>
                {
                    { "warning_key", warning.Key ?? string.Empty },
                    { "rule_id", warning.RuleId ?? string.Empty },
                    { "rule_name", warning.RuleName ?? string.Empty },
                    { "level", warning.Level ?? string.Empty },
                    { "type", warning.Type ?? string.Empty },
                    { "time_range", string.Format("{0:yyyy-MM-dd HH:mm} ~ {1:yyyy-MM-dd HH:mm}", warning.StartTime, warning.EndTime) },
                    { "metric_name", warning.MetricName ?? string.Empty },
                    { "current_value", ToStr(warning.CurrentValue)},
                    { "baseline_value", ToStr(warning.BaselineValue) },
                    { "threshold_value", ToStr(warning.ThresholdValue) },
                    { "summary", warning.Summary ?? string.Empty }
                };

                var payload = new Dictionary<string, object>
        {
            { "inputs", inputs },
            { "response_mode", "blocking" },
            { "user", "warning-analyzer" }
        };

                var url = _baseUrl.TrimEnd('/') + "/workflows/run";

                using (var req = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                    req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                    var json = JsonConvert.SerializeObject(payload);
                    AppendLog("Request", url, BuildFriendlyRequestLog(inputs));

                    req.Content = new StringContent(json, Encoding.UTF8, "application/json");

                    using (var resp = await _http.SendAsync(req, token).ConfigureAwait(false))
                    {
                        token.ThrowIfCancellationRequested();
                        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();

                        var preview = TryExtractAnswer(body);
                        AppendLog("Response", url, BuildFriendlyResponseLog(resp, preview, body));

                        if (!resp.IsSuccessStatusCode)
                        {
                            return Failure("调用 AI 接口失败：" + resp.StatusCode, IsTerminalHttpStatus(resp.StatusCode));
                        }

                        var answer = TryExtractAnswer(body);
                        if (!string.IsNullOrWhiteSpace(answer))
                        {
                            return Success(answer);
                        }

                        if (!string.IsNullOrWhiteSpace(body))
                        {
                            return Success(DifyOutputSanitizer.Clean(body));
                        }

                        return Failure("AI 未返回有效分析结果。");
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                AppendLog("Exception", _baseUrl.TrimEnd('/') + "/workflows/run", "预警 AI 请求已取消。");
                throw;
            }
            catch (TaskCanceledException)
            {
                AppendLog("Exception", _baseUrl.TrimEnd('/') + "/workflows/run", "预警 AI 请求超时。");
                return Failure("预警 AI 请求超时。");
            }
            catch (Exception ex)
            {
                AppendLog("Exception", _baseUrl.TrimEnd('/') + "/workflows/run", ex.ToString());
                return Failure("调用 AI 分析出错：" + ex.Message);
            }
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



        /// <summary>追加请求/响应/异常日志到 WarningAI 目录，失败静默。</summary>
        private static void AppendLog(string stage, string url, string content)
        {
            try
            {
                var dir = Path.Combine("D:\\", "Data", "AiLog", "WarningAI");
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var path = Path.Combine(dir, DateTime.Now.ToString("yyyy-MM-dd") + ".log");
                var sb = new StringBuilder();
                if (string.Equals(stage, "Request", StringComparison.OrdinalIgnoreCase))
                {
                
                    sb.AppendLine("============================================================");
                }
                sb.AppendFormat("{0:yyyy-MM-dd HH:mm:ss} [{1}] {2}", DateTime.Now, stage, url ?? string.Empty);
                sb.AppendLine();
                if (!string.IsNullOrEmpty(content))
                {
                    sb.AppendLine(content);
                }
         
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

        private static string BuildFriendlyRequestLog(Dictionary<string, object> inputs)
        {
            if (inputs == null) return "无请求参数";
            var rule = inputs.ContainsKey("rule_id") ? inputs["rule_id"] : string.Empty;
            var name = inputs.ContainsKey("rule_name") ? inputs["rule_name"] : string.Empty;
            var level = inputs.ContainsKey("level") ? inputs["level"] : string.Empty;
            var type = inputs.ContainsKey("type") ? inputs["type"] : string.Empty;
            var time = inputs.ContainsKey("time_range") ? inputs["time_range"] : string.Empty;
            var summary = inputs.ContainsKey("summary") ? inputs["summary"] : string.Empty;
            return string.Format("Rule={0} {1} | Level={2} | Type={3} | Time={4} | Summary={5}",
                rule, name, level, type, time, Truncate(summary?.ToString() ?? string.Empty, 200));
        }

        private static string ToStr(object value)
        {
            if (value == null) return string.Empty;
            if (value is string s) return s;
            if (value is IFormattable f)
            {
                return f.ToString(null, CultureInfo.InvariantCulture);
            }
            return value.ToString();
        }

        private static WarningAnalysisResult Success(string markdown)
        {
            var cleaned = DifyOutputSanitizer.Clean(markdown);
            return new WarningAnalysisResult
            {
                IsSuccess = !string.IsNullOrWhiteSpace(cleaned),
                Markdown = cleaned,
                ErrorMessage = string.Empty,
                IsTerminalFailure = false
            };
        }

        private static WarningAnalysisResult Failure(string errorMessage, bool isTerminalFailure = false)
        {
            return new WarningAnalysisResult
            {
                IsSuccess = false,
                Markdown = string.Empty,
                ErrorMessage = errorMessage ?? string.Empty,
                IsTerminalFailure = isTerminalFailure
            };
        }

        private static bool IsTerminalHttpStatus(System.Net.HttpStatusCode statusCode)
        {
            return statusCode == System.Net.HttpStatusCode.BadRequest
                   || statusCode == System.Net.HttpStatusCode.Unauthorized
                   || statusCode == System.Net.HttpStatusCode.Forbidden
                   || statusCode == System.Net.HttpStatusCode.NotFound;
        }
    }
}

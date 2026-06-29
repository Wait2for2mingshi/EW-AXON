using EW_Assistant.Services.Warnings;
using EW_Assistant.Diagnostics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace EW_Assistant.Services.PreventiveMaintenance
{
    public sealed class PreventiveMaintenanceAiService
    {
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(2);
        private readonly HttpClient _http;
        private readonly string _baseUrl;
        private readonly string _apiKey;

        public PreventiveMaintenanceAiService(HttpClient httpClient = null)
        {
            _http = httpClient ?? new HttpClient { Timeout = RequestTimeout };
            var cfg = ConfigService.Current;
            _baseUrl = cfg != null && !string.IsNullOrWhiteSpace(cfg.URL) ? cfg.URL.TrimEnd('/') : string.Empty;
            _apiKey = cfg != null ? cfg.EarlyWarningKey : string.Empty;
        }

        public async Task<PreventiveMaintenanceAiResult> AnalyzeAsync(PreventiveMaintenanceReport report, CancellationToken token = default(CancellationToken))
        {
            token.ThrowIfCancellationRequested();
            if (report == null)
            {
                return Failure("预防维护分析结果为空。");
            }

            if (string.IsNullOrWhiteSpace(_baseUrl) || string.IsNullOrWhiteSpace(_apiKey))
            {
                return Failure("未配置 AI 接口或预警 Key，已显示本地规则总结。");
            }

            var url = _baseUrl + "/workflows/run";
            var summaryPrompt = BuildSummaryPrompt(report);
            var inputs = new Dictionary<string, object>
            {
                ["warning_key"] = "preventive_maintenance",
                ["rule_id"] = "PM_RULE_SUMMARY",
                ["rule_name"] = "AI预防性维护",
                ["level"] = report.OverallRiskLevel,
                ["type"] = "PreventiveMaintenance",
                ["time_range"] = $"{report.CurrentStart:yyyy-MM-dd} ~ {report.CurrentEnd:yyyy-MM-dd}",
                ["metric_name"] = "设备健康风险",
                ["current_value"] = report.OverallRiskScore.ToString(),
                ["baseline_value"] = $"{report.BaselineStart:yyyy-MM-dd} ~ {report.BaselineEnd:yyyy-MM-dd}",
                ["threshold_value"] = "70",
                ["summary"] = summaryPrompt,
                ["context_json"] = BuildContextJson(report)
            };

            var payload = new Dictionary<string, object>
            {
                ["inputs"] = inputs,
                ["response_mode"] = "blocking",
                ["user"] = "preventive-maintenance"
            };

            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                    req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                    var json = JsonConvert.SerializeObject(payload);
                    AppendLog("Request", url, BuildFriendlyRequestLog(report));
                    req.Content = new StringContent(json, Encoding.UTF8, "application/json");

                    using (var resp = await _http.SendAsync(req, token).ConfigureAwait(false))
                    {
                        token.ThrowIfCancellationRequested();
                        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();

                        var answer = TryExtractAnswer(body);
                        AppendLog("Response", url, BuildFriendlyResponseLog(resp, answer, body));
                        if (!resp.IsSuccessStatusCode)
                        {
                            return Failure("调用 AI 接口失败：" + resp.StatusCode);
                        }

                        if (!string.IsNullOrWhiteSpace(answer))
                        {
                            return Success(answer);
                        }

                        return string.IsNullOrWhiteSpace(body)
                            ? Failure("AI 未返回有效分析结果。")
                            : Success(DifyOutputSanitizer.Clean(body));
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                AppendLog("Exception", url, "预防维护 AI 请求已取消。");
                throw;
            }
            catch (TaskCanceledException)
            {
                AppendLog("Exception", url, "预防维护 AI 请求超时。");
                return Failure("预防维护 AI 请求超时，已显示本地规则总结。");
            }
            catch (Exception ex)
            {
                AppendLog("Exception", url, ex.ToString());
                return Failure("调用 AI 分析出错：" + ex.Message);
            }
        }

        private static string BuildSummaryPrompt(PreventiveMaintenanceReport report)
        {
            var sb = new StringBuilder();
            sb.AppendLine("请基于以下设备报警与产能趋势，输出预防性维护建议。");
            sb.AppendLine("要求：不要虚构传感器数据或备件寿命；只基于给定事实判断风险。");
            sb.AppendLine("排版要求：不要使用项目符号、圆点列表或缩进列表；使用短段落，格式为“风险结论：”“风险项 1：”“原因：”“建议：”。");
            sb.AppendLine("输出结构：风险结论、重点部位、原因、建议点检项、建议维护动作、优先级。");
            sb.AppendLine();
            sb.AppendFormat("分析窗口：{0:yyyy-MM-dd} ~ {1:yyyy-MM-dd}，对比窗口：{2:yyyy-MM-dd} ~ {3:yyyy-MM-dd}",
                report.CurrentStart, report.CurrentEnd, report.BaselineStart, report.BaselineEnd);
            sb.AppendLine();
            sb.AppendFormat("总体风险：{0}，风险分：{1}", report.OverallRiskLevel, report.OverallRiskScore);
            sb.AppendLine();
            sb.AppendFormat("报警：当前 {0} 次 / {1:F1} 分钟，对比 {2} 次 / {3:F1} 分钟",
                report.CurrentAlarmCount, report.CurrentDowntimeMinutes, report.BaselineAlarmCount, report.BaselineDowntimeMinutes);
            sb.AppendLine();
            sb.AppendFormat("产能：当前 PASS={0}, FAIL={1}, 良率={2:P2}；对比 PASS={3}, FAIL={4}, 良率={5:P2}",
                report.CurrentPass, report.CurrentFail, report.CurrentYield, report.BaselinePass, report.BaselineFail, report.BaselineYield);
            sb.AppendLine();
            sb.AppendLine("高风险候选：");

            var index = 1;
            foreach (var item in report.RiskItems.Take(8))
            {
                sb.AppendFormat("风险项 {0}：{1} | {2} | {3} | 分数 {4} | 当前 {5} 次，对比 {6} 次 | 当前时长 {7:F1} 分钟，对比 {8:F1} 分钟 | 原因：{9} | 建议：{10}",
                    index,
                    item.Code,
                    item.Message,
                    item.Category,
                    item.RiskScore,
                    item.CurrentCount,
                    item.BaselineCount,
                    item.CurrentDowntimeMinutes,
                    item.BaselineDowntimeMinutes,
                    item.ReasonSummary,
                    item.SuggestedChecks);
                sb.AppendLine();
                index++;
            }

            return sb.ToString();
        }

        private static string BuildContextJson(PreventiveMaintenanceReport report)
        {
            var compact = new
            {
                generated_at = report.GeneratedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                window_days = report.WindowDays,
                current_range = new { start = report.CurrentStart.ToString("yyyy-MM-dd"), end = report.CurrentEnd.ToString("yyyy-MM-dd") },
                baseline_range = new { start = report.BaselineStart.ToString("yyyy-MM-dd"), end = report.BaselineEnd.ToString("yyyy-MM-dd") },
                risk = new { level = report.OverallRiskLevel, score = report.OverallRiskScore },
                alarms = new
                {
                    current_count = report.CurrentAlarmCount,
                    baseline_count = report.BaselineAlarmCount,
                    current_downtime_minutes = report.CurrentDowntimeMinutes,
                    baseline_downtime_minutes = report.BaselineDowntimeMinutes
                },
                production = new
                {
                    current_pass = report.CurrentPass,
                    current_fail = report.CurrentFail,
                    current_yield = report.CurrentYield,
                    baseline_pass = report.BaselinePass,
                    baseline_fail = report.BaselineFail,
                    baseline_yield = report.BaselineYield
                },
                top_risks = report.RiskItems.Take(8).Select(x => new
                {
                    x.Code,
                    x.Message,
                    x.Category,
                    x.RiskScore,
                    x.RiskLevel,
                    x.CurrentCount,
                    x.BaselineCount,
                    x.CurrentDowntimeMinutes,
                    x.BaselineDowntimeMinutes,
                    x.ReasonSummary,
                    x.SuggestedChecks
                }).ToList()
            };

            return JsonConvert.SerializeObject(compact);
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
                var outputs = data?["outputs"] as JObject;
                if (outputs != null)
                {
                    var result = DifyOutputSanitizer.ExtractVisibleText(outputs, "result", "text", "answer", "output");
                    if (!string.IsNullOrWhiteSpace(result)) return DifyOutputSanitizer.Clean(result);
                }
            }
            catch
            {
                // ignore parse error
            }
            return string.Empty;
        }

        private static string BuildFriendlyRequestLog(PreventiveMaintenanceReport report)
        {
            return string.Format("Range={0:yyyy-MM-dd}~{1:yyyy-MM-dd} | Risk={2}/{3} | Alarms={4} | Top={5}",
                report.CurrentStart,
                report.CurrentEnd,
                report.OverallRiskLevel,
                report.OverallRiskScore,
                report.CurrentAlarmCount,
                report.RiskItems == null ? string.Empty : string.Join(", ", report.RiskItems.Take(3).Select(x => x.Code)));
        }

        private static string BuildFriendlyResponseLog(HttpResponseMessage resp, string preview, string body)
        {
            var status = resp == null ? "Unknown" : string.Format("{0}({1})", (int)resp.StatusCode, resp.StatusCode);
            var decodedPreview = DecodeUnicode(preview);
            var answer = string.IsNullOrWhiteSpace(decodedPreview) ? "无解析结果" : Truncate(decodedPreview.Replace(Environment.NewLine, " "), 200);
            var decoded = DifyOutputSanitizer.Clean(DecodeUnicode(body));
            var raw = string.IsNullOrWhiteSpace(decoded) ? "无响应体" : Truncate(decoded, 500);
            return string.Format("Status={0} | AnswerPreview={1} | RawBody={2}", status, answer, raw);
        }

        private static void AppendLog(string stage, string url, string content)
        {
            try
            {
                var dir = Path.Combine("D:\\", "Data", "AiLog", "PreventiveMaintenanceAI");
                Directory.CreateDirectory(dir);
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
                LogRetentionPolicy.TryCleanupFiles(dir, "*.log", SearchOption.TopDirectoryOnly, TimeSpan.FromDays(30));
            }
            catch
            {
                // logging must not block the UI
            }
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
            return input.Length <= maxLen ? input : input.Substring(0, maxLen) + "...";
        }

        private static PreventiveMaintenanceAiResult Success(string markdown)
        {
            return new PreventiveMaintenanceAiResult
            {
                IsSuccess = true,
                Markdown = DifyOutputSanitizer.Clean(markdown),
                ErrorMessage = string.Empty
            };
        }

        private static PreventiveMaintenanceAiResult Failure(string errorMessage)
        {
            return new PreventiveMaintenanceAiResult
            {
                IsSuccess = false,
                Markdown = string.Empty,
                ErrorMessage = errorMessage ?? string.Empty
            };
        }
    }
}

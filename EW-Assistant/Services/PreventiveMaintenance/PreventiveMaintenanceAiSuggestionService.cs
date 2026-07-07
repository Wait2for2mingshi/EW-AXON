using EW_Assistant.Diagnostics;
using EW_Assistant.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EW_Assistant.Services.PreventiveMaintenance
{
    public sealed class PreventiveMaintenanceAiSuggestionService
    {
        private const int MaxCacheEntries = 500;
        private const int MaxTrendPoints = 8;
        private const int MaxSuggestionLength = 1800;
        private const string CacheFilePath = @"D:\DataAI\preventive_maintenance_ai_suggestion_cache.json";
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(18);
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
        private static readonly object s_cacheLock = new object();
        private static Dictionary<string, AiSuggestionCacheEntry> s_cache;

        private readonly HttpClient _http;

        public PreventiveMaintenanceAiSuggestionService(HttpClient http = null)
        {
            _http = http ?? new HttpClient { Timeout = RequestTimeout };
        }

        public async Task<IReadOnlyList<PartMaintenanceAiSuggestionResult>> GenerateVisibleSuggestionsAsync(
            PartMaintenanceReport report,
            string rangeText,
            int maxCylinderCount,
            int maxVacuumCount,
            CancellationToken token = default(CancellationToken))
        {
            token.ThrowIfCancellationRequested();

            var candidates = BuildCandidates(report, maxCylinderCount, maxVacuumCount);
            var results = new List<PartMaintenanceAiSuggestionResult>();
            var pending = new List<AiSuggestionCandidate>();

            foreach (var candidate in candidates)
            {
                string cached;
                if (TryReadCache(candidate, out cached))
                {
                    results.Add(new PartMaintenanceAiSuggestionResult
                    {
                        ComponentKey = candidate.ComponentKey,
                        Suggestion = cached,
                        FromCache = true
                    });
                }
                else
                {
                    pending.Add(candidate);
                }
            }

            if (pending.Count == 0)
                return results;

            string baseUrl;
            string apiKey;
            if (!TryGetDifyConfig(out baseUrl, out apiKey))
            {
                AppendLog("Skip", "MaintenanceKey 或 API URL 未配置，保留本地规则建议。");
                return results;
            }

            var generated = await RequestDifyAsync(baseUrl, apiKey, rangeText, pending, token).ConfigureAwait(false);
            var pendingByKey = pending
                .GroupBy(x => x.ComponentKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
            var hasNewCache = false;

            foreach (var item in generated)
            {
                AiSuggestionCandidate candidate;
                if (item == null ||
                    string.IsNullOrWhiteSpace(item.Suggestion) ||
                    !pendingByKey.TryGetValue(item.ComponentKey ?? string.Empty, out candidate))
                {
                    continue;
                }

                item.Suggestion = NormalizeSuggestion(item.Suggestion);
                StoreCache(candidate, item.Suggestion);
                results.Add(item);
                hasNewCache = true;
            }

            if (hasNewCache)
                SaveCache();

            return results;
        }

        public static string BuildComponentKey(PartMaintenanceComponentStatus status)
        {
            return status == null
                ? string.Empty
                : BuildComponentKey(status.PartType, status.ComponentName);
        }

        private static string BuildComponentKey(string partType, string componentName)
        {
            return (partType ?? string.Empty).Trim() + "|" + (componentName ?? string.Empty).Trim();
        }

        private static bool TryGetDifyConfig(out string baseUrl, out string apiKey)
        {
            var cfg = ConfigService.Current;
            baseUrl = cfg == null ? string.Empty : (cfg.URL ?? string.Empty).Trim().TrimEnd('/');
            apiKey = cfg == null ? string.Empty : (cfg.MaintenanceKey ?? string.Empty).Trim();
            return !string.IsNullOrWhiteSpace(baseUrl) && !string.IsNullOrWhiteSpace(apiKey);
        }

        private async Task<List<PartMaintenanceAiSuggestionResult>> RequestDifyAsync(
            string baseUrl,
            string apiKey,
            string rangeText,
            IList<AiSuggestionCandidate> candidates,
            CancellationToken token)
        {
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/workflows/run"))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    request.Content = new StringContent(JsonConvert.SerializeObject(BuildPayload(rangeText, candidates)), Encoding.UTF8, "application/json");
                    AppendLog("Request", "count=" + candidates.Count + " range=" + (rangeText ?? string.Empty));

                    using (var response = await _http.SendAsync(request, token).ConfigureAwait(false))
                    {
                        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();

                        if (!response.IsSuccessStatusCode)
                        {
                            AppendLog("Response", "HTTP " + (int)response.StatusCode + " " + Truncate(DifyOutputSanitizer.Clean(body), 800));
                            return new List<PartMaintenanceAiSuggestionResult>();
                        }

                        var parsed = ParseDifyResponse(body, candidates);
                        AppendLog("Response", "parsed=" + parsed.Count);
                        return parsed;
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                AppendLog("Cancel", "预防维护 AI 建议生成已取消。");
                throw;
            }
            catch (TaskCanceledException)
            {
                AppendLog("Timeout", "预防维护 AI 建议请求超过 " + RequestTimeout.TotalSeconds.ToString("0") + " 秒，保留本地建议。");
                return new List<PartMaintenanceAiSuggestionResult>();
            }
            catch (Exception ex)
            {
                AppendLog("Exception", ex.Message);
                return new List<PartMaintenanceAiSuggestionResult>();
            }
        }

        private static Dictionary<string, object> BuildPayload(string rangeText, IList<AiSuggestionCandidate> candidates)
        {
            var components = candidates.Select(x => x.Context).ToList();
            var contract = "{\"suggestions\":[{\"key\":\"组件key\",\"suggestion\":\"建议文本\"}]}";
            var task = "基于预防维护数据生成现场可执行建议。只能使用输入数据，不要编造指标，不要复述固定模板。每条建议控制在 120 字以内，必须返回 JSON：" + contract;
            var contextJson = JsonConvert.SerializeObject(new
            {
                task,
                range = rangeText ?? string.Empty,
                components,
                output_contract = contract
            }, Formatting.None);

            return new Dictionary<string, object>
            {
                ["inputs"] = new Dictionary<string, object>
                {
                    ["task"] = task,
                    ["prompt"] = task,
                    ["range"] = rangeText ?? string.Empty,
                    ["context_json"] = contextJson,
                    ["maintenance_context_json"] = contextJson,
                    ["components_json"] = JsonConvert.SerializeObject(components, Formatting.None),
                    ["output_contract"] = contract
                },
                ["response_mode"] = "blocking",
                ["user"] = "preventive-maintenance"
            };
        }

        private static List<PartMaintenanceAiSuggestionResult> ParseDifyResponse(
            string body,
            IEnumerable<AiSuggestionCandidate> candidates)
        {
            JToken root;
            if (!TryParseJsonFragment(body, out root))
                return new List<PartMaintenanceAiSuggestionResult>();

            var allowedKeys = new HashSet<string>(candidates.Select(x => x.ComponentKey), StringComparer.OrdinalIgnoreCase);
            var outputs = root["data"]?["outputs"] ?? root["outputs"] ?? root;
            var suggestionToken = outputs["suggestions"] ?? outputs["maintenance_suggestions"] ?? ParseSuggestionText(outputs);
            return ParseSuggestions(suggestionToken, allowedKeys);
        }

        private static JToken ParseSuggestionText(JToken outputs)
        {
            var text = DifyOutputSanitizer.ExtractVisibleText(
                outputs,
                "suggestions_json",
                "result",
                "answer",
                "text",
                "output");

            JToken token;
            return TryParseJsonFragment(text, out token) ? token : null;
        }

        private static List<PartMaintenanceAiSuggestionResult> ParseSuggestions(JToken token, HashSet<string> allowedKeys)
        {
            if (token == null)
                return new List<PartMaintenanceAiSuggestionResult>();

            if (token.Type == JTokenType.String)
            {
                JToken parsed;
                return TryParseJsonFragment(token.Value<string>(), out parsed)
                    ? ParseSuggestions(parsed, allowedKeys)
                    : new List<PartMaintenanceAiSuggestionResult>();
            }

            if (token.Type == JTokenType.Object)
            {
                var obj = (JObject)token;
                var nested = obj["suggestions"] ?? obj["items"];
                if (nested != null)
                    return ParseSuggestions(nested, allowedKeys);

                var single = BuildSuggestionResult(obj, allowedKeys);
                return single == null
                    ? new List<PartMaintenanceAiSuggestionResult>()
                    : new List<PartMaintenanceAiSuggestionResult> { single };
            }

            var results = new List<PartMaintenanceAiSuggestionResult>();
            if (token.Type != JTokenType.Array)
                return results;

            foreach (var item in token.Children<JObject>())
            {
                var result = BuildSuggestionResult(item, allowedKeys);
                if (result != null && !results.Any(x => string.Equals(x.ComponentKey, result.ComponentKey, StringComparison.OrdinalIgnoreCase)))
                    results.Add(result);
            }

            return results;
        }

        private static PartMaintenanceAiSuggestionResult BuildSuggestionResult(JObject obj, HashSet<string> allowedKeys)
        {
            var key = FirstText(obj, "key", "component_key", "componentKey");
            if (string.IsNullOrWhiteSpace(key))
                key = BuildComponentKey(FirstText(obj, "part_type", "partType"), FirstText(obj, "component_name", "componentName"));

            key = (key ?? string.Empty).Trim();
            if (!allowedKeys.Contains(key))
                return null;

            var suggestion = NormalizeSuggestion(FirstText(obj, "suggestion", "analysis", "text", "answer", "result"));
            if (string.IsNullOrWhiteSpace(suggestion))
                return null;

            return new PartMaintenanceAiSuggestionResult
            {
                ComponentKey = key,
                Suggestion = suggestion,
                FromCache = false
            };
        }

        private static string FirstText(JObject obj, params string[] names)
        {
            foreach (var name in names)
            {
                var value = obj.Value<string>(name);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return string.Empty;
        }

        private static List<AiSuggestionCandidate> BuildCandidates(
            PartMaintenanceReport report,
            int maxCylinderCount,
            int maxVacuumCount)
        {
            var candidates = new List<AiSuggestionCandidate>();
            if (report == null)
                return candidates;

            AddCandidates(candidates, report.CylinderStatuses, Math.Max(0, maxCylinderCount), report);
            AddCandidates(candidates, report.VacuumStatuses, Math.Max(0, maxVacuumCount), report);
            return candidates;
        }

        private static void AddCandidates(
            ICollection<AiSuggestionCandidate> target,
            IEnumerable<PartMaintenanceComponentStatus> statuses,
            int maxCount,
            PartMaintenanceReport report)
        {
            if (statuses == null || maxCount <= 0)
                return;

            foreach (var status in statuses.Take(maxCount))
            {
                if (status == null || string.IsNullOrWhiteSpace(status.ComponentName))
                    continue;

                var context = BuildComponentContext(report, status);
                target.Add(new AiSuggestionCandidate
                {
                    ComponentKey = BuildComponentKey(status),
                    ContextHash = ComputeSha256(JsonConvert.SerializeObject(context, Formatting.None)),
                    Context = context
                });
            }
        }

        private static object BuildComponentContext(PartMaintenanceReport report, PartMaintenanceComponentStatus status)
        {
            return new
            {
                key = BuildComponentKey(status),
                latest_report_date = report != null && report.LatestDate.HasValue ? report.LatestDate.Value.ToString("yyyy-MM-dd") : string.Empty,
                part_type = status.PartType ?? string.Empty,
                component_name = status.ComponentName ?? string.Empty,
                source_names = status.SourceNames ?? string.Empty,
                risk_level = status.RiskLevel ?? string.Empty,
                risk_score = status.RiskScore,
                sample_count = status.SampleCount,
                abnormal_count = status.AbnormalCount,
                average_value = status.AverageValue,
                max_value = status.MaxValue,
                latest_value = status.LatestValue,
                home = new
                {
                    risk_level = status.HomeRiskLevel ?? string.Empty,
                    risk_score = status.HomeRiskScore,
                    average_value = status.HomeAverageValue,
                    max_value = status.HomeMaxValue,
                    latest_value = status.HomeLatestValue
                },
                work = new
                {
                    risk_level = status.WorkRiskLevel ?? string.Empty,
                    risk_score = status.WorkRiskScore,
                    average_value = status.WorkAverageValue,
                    max_value = status.WorkMaxValue,
                    latest_value = status.WorkLatestValue
                },
                trend = status.Trend
                    .Skip(Math.Max(0, status.Trend.Count - MaxTrendPoints))
                    .Select(x => new
                    {
                        date = x.Date.ToString("yyyy-MM-dd"),
                        value = x.Value,
                        sample_count = x.SampleCount,
                        abnormal_count = x.AbnormalCount,
                        has_abnormal = x.HasAbnormal
                    })
                    .ToList()
            };
        }

        private static bool TryReadCache(AiSuggestionCandidate candidate, out string suggestion)
        {
            suggestion = string.Empty;
            lock (s_cacheLock)
            {
                AiSuggestionCacheEntry entry;
                if (!GetCacheUnsafe().TryGetValue(candidate.CacheKey, out entry))
                    return false;

                suggestion = entry.Suggestion ?? string.Empty;
                return !string.IsNullOrWhiteSpace(suggestion);
            }
        }

        private static void StoreCache(AiSuggestionCandidate candidate, string suggestion)
        {
            suggestion = NormalizeSuggestion(suggestion);
            if (candidate == null || string.IsNullOrWhiteSpace(suggestion))
                return;

            lock (s_cacheLock)
            {
                GetCacheUnsafe()[candidate.CacheKey] = new AiSuggestionCacheEntry
                {
                    Key = candidate.CacheKey,
                    Suggestion = suggestion,
                    UpdatedAt = DateTime.Now
                };
            }
        }

        private static Dictionary<string, AiSuggestionCacheEntry> GetCacheUnsafe()
        {
            if (s_cache != null)
                return s_cache;

            s_cache = new Dictionary<string, AiSuggestionCacheEntry>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(CacheFilePath))
                    return s_cache;

                var entries = JsonConvert.DeserializeObject<List<AiSuggestionCacheEntry>>(File.ReadAllText(CacheFilePath, Encoding.UTF8));
                foreach (var entry in entries ?? new List<AiSuggestionCacheEntry>())
                {
                    if (!string.IsNullOrWhiteSpace(entry.Key) && !string.IsNullOrWhiteSpace(entry.Suggestion))
                        s_cache[entry.Key] = entry;
                }
            }
            catch
            {
                // 缓存读取失败不影响页面展示。
            }

            return s_cache;
        }

        private static void SaveCache()
        {
            try
            {
                List<AiSuggestionCacheEntry> entries;
                lock (s_cacheLock)
                {
                    entries = GetCacheUnsafe()
                        .Values
                        .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Suggestion))
                        .OrderByDescending(x => x.UpdatedAt)
                        .Take(MaxCacheEntries)
                        .ToList();
                    s_cache = entries
                        .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
                }

                var dir = Path.GetDirectoryName(CacheFilePath);
                if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var temp = CacheFilePath + ".tmp";
                File.WriteAllText(temp, JsonConvert.SerializeObject(entries, Formatting.Indented), Utf8NoBom);
                File.Copy(temp, CacheFilePath, true);
                File.Delete(temp);
            }
            catch
            {
                // 缓存写入失败不影响主流程。
            }
        }

        private static bool TryParseJsonFragment(string text, out JToken token)
        {
            token = null;
            text = DifyOutputSanitizer.Clean(text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text))
                return false;

            if (text.StartsWith("```", StringComparison.Ordinal))
            {
                var firstLineEnd = text.IndexOf('\n');
                if (firstLineEnd >= 0)
                    text = text.Substring(firstLineEnd + 1);

                var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
                if (lastFence >= 0)
                    text = text.Substring(0, lastFence);
            }

            try
            {
                token = JToken.Parse(text);
                return true;
            }
            catch
            {
                var startObject = text.IndexOf('{');
                var startArray = text.IndexOf('[');
                var start = startObject < 0 ? startArray : (startArray < 0 ? startObject : Math.Min(startObject, startArray));
                if (start < 0)
                    return false;

                var endChar = text[start] == '{' ? '}' : ']';
                var end = text.LastIndexOf(endChar);
                if (end <= start)
                    return false;

                try
                {
                    token = JToken.Parse(text.Substring(start, end - start + 1));
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        private static string ComputeSha256(string text)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty));
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var item in bytes)
                    sb.Append(item.ToString("x2"));
                return sb.ToString();
            }
        }

        private static string NormalizeSuggestion(string suggestion)
        {
            var text = DifyOutputSanitizer.Clean(suggestion ?? string.Empty).Trim();
            return text.Length <= MaxSuggestionLength
                ? text
                : text.Substring(0, MaxSuggestionLength) + "...";
        }

        private static void AppendLog(string stage, string content)
        {
            try
            {
                var dir = Path.Combine("D:\\", "Data", "AiLog", "PreventiveMaintenanceAI");
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var path = Path.Combine(dir, DateTime.Now.ToString("yyyy-MM-dd") + ".log");
                File.AppendAllText(
                    path,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + (stage ?? string.Empty) + "]" + Environment.NewLine
                    + (content ?? string.Empty) + Environment.NewLine + Environment.NewLine,
                    Utf8NoBom);
                LogRetentionPolicy.TryCleanupFiles(dir, "*.log", SearchOption.TopDirectoryOnly, TimeSpan.FromDays(30));
            }
            catch
            {
                // 日志失败不影响主流程。
            }
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || maxLength <= 0)
                return string.Empty;

            return value.Length <= maxLength ? value : value.Substring(0, maxLength) + "...";
        }

        private sealed class AiSuggestionCandidate
        {
            public string ComponentKey { get; set; }
            public string ContextHash { get; set; }
            public string CacheKey => (ComponentKey ?? string.Empty) + "|" + (ContextHash ?? string.Empty);
            public object Context { get; set; }
        }

        private sealed class AiSuggestionCacheEntry
        {
            public string Key { get; set; }
            public string Suggestion { get; set; }
            public DateTime UpdatedAt { get; set; }
        }
    }

    public sealed class PartMaintenanceAiSuggestionResult
    {
        public string ComponentKey { get; set; }
        public string Suggestion { get; set; }
        public bool FromCache { get; set; }
    }
}

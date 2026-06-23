using EW_Assistant.Settings;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EW_Assistant.Services
{
    /// <summary>
    /// 面向本地 McpServer 的 HTTP 网关客户端，用于调用 Windows UI 自动化桥接接口。
    /// </summary>
    public sealed class AgentMcpBridgeClient
    {
        private static readonly Lazy<AgentMcpBridgeClient> s_lazy =
            new Lazy<AgentMcpBridgeClient>(() => new AgentMcpBridgeClient());
        private static readonly HttpClient s_http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        public static AgentMcpBridgeClient Instance => s_lazy.Value;

        private AgentMcpBridgeClient() { }

        public Task<JObject> UiRuntimeObserveAsync(
            string lane,
            string scope,
            string windowTitleContains,
            bool includeChildren,
            int maxChildren,
            int maxCandidates,
            bool includeImages = true,
            CancellationToken ct = default)
        {
            var body = new JObject
            {
                ["lane"] = lane ?? "default",
                ["scope"] = scope ?? "foreground_window",
                ["windowTitleContains"] = windowTitleContains ?? string.Empty,
                ["includeChildren"] = includeChildren,
                ["maxChildren"] = maxChildren,
                ["maxCandidates"] = maxCandidates,
                ["includeImages"] = includeImages
            };
            return PostJsonAsync("/runtime/observe", body, ct);
        }

        public Task<JObject> UiRuntimeCommandAsync(
            string commandType,
            string operation,
            string scope = null,
            int? relativeX = null,
            int? relativeY = null,
            int? captureLeft = null,
            int? captureTop = null,
            int? captureWidth = null,
            int? captureHeight = null,
            int? x = null,
            int? y = null,
            string text = null,
            IReadOnlyList<string> keys = null,
            IReadOnlyList<string> modifiers = null,
            string commandLine = null,
            string workingDirectory = null,
            string fileName = null,
            string appName = null,
            string browserName = null,
            string url = null,
            string query = null,
            string site = null,
            int? maxResults = null,
            string targetWindowTitleContains = null,
            string expectedTextContains = null,
            bool? requireTargetVisible = null,
            bool? includeChildren = null,
            int? maxChildren = null,
            bool? confirmed = null,
            int? timeoutMs = null,
            int? pollMs = null,
            string lane = "default",
            CancellationToken ct = default)
        {
            var body = new JObject
            {
                ["lane"] = lane ?? "default",
                ["commandType"] = commandType ?? "mouse",
                ["operation"] = operation ?? string.Empty
            };

            if (!string.IsNullOrWhiteSpace(scope)) body["scope"] = scope;
            if (x.HasValue) body["x"] = x.Value;
            if (y.HasValue) body["y"] = y.Value;
            if (relativeX.HasValue) body["relativeX"] = relativeX.Value;
            if (relativeY.HasValue) body["relativeY"] = relativeY.Value;
            if (relativeX.HasValue && relativeY.HasValue)
                body["point_2d"] = new JArray(relativeX.Value, relativeY.Value);
            if (captureLeft.HasValue) body["captureLeft"] = captureLeft.Value;
            if (captureTop.HasValue) body["captureTop"] = captureTop.Value;
            if (captureWidth.HasValue) body["captureWidth"] = captureWidth.Value;
            if (captureHeight.HasValue) body["captureHeight"] = captureHeight.Value;
            if (!string.IsNullOrWhiteSpace(text)) body["text"] = text;
            if (keys != null && keys.Count > 0) body["keys"] = new JArray(keys);
            if (modifiers != null && modifiers.Count > 0) body["modifiers"] = new JArray(modifiers);
            if (!string.IsNullOrWhiteSpace(commandLine)) body["commandLine"] = commandLine;
            if (!string.IsNullOrWhiteSpace(workingDirectory)) body["workingDirectory"] = workingDirectory;
            if (!string.IsNullOrWhiteSpace(fileName)) body["fileName"] = fileName;
            if (!string.IsNullOrWhiteSpace(appName)) body["appName"] = appName;
            if (!string.IsNullOrWhiteSpace(browserName)) body["browserName"] = browserName;
            if (!string.IsNullOrWhiteSpace(url)) body["url"] = url;
            if (!string.IsNullOrWhiteSpace(query)) body["query"] = query;
            if (!string.IsNullOrWhiteSpace(site)) body["site"] = site;
            if (maxResults.HasValue) body["maxResults"] = maxResults.Value;
            if (!string.IsNullOrWhiteSpace(targetWindowTitleContains)) body["targetWindowTitleContains"] = targetWindowTitleContains;
            if (!string.IsNullOrWhiteSpace(expectedTextContains)) body["expectedTextContains"] = expectedTextContains;
            if (requireTargetVisible.HasValue) body["requireTargetVisible"] = requireTargetVisible.Value;
            if (includeChildren.HasValue) body["includeChildren"] = includeChildren.Value;
            if (maxChildren.HasValue) body["maxChildren"] = maxChildren.Value;
            if (confirmed.HasValue) body["confirmed"] = confirmed.Value;
            if (timeoutMs.HasValue) body["timeoutMs"] = timeoutMs.Value;
            if (pollMs.HasValue) body["pollMs"] = pollMs.Value;

            return PostJsonAsync("/runtime/command", body, ct);
        }

        private async Task<JObject> PostJsonAsync(string path, JObject body, CancellationToken ct)
        {
            var baseUrl = BuildBaseUrl(ConfigService.Current);
            if (string.IsNullOrWhiteSpace(baseUrl))
                return BuildClientError("MCPServerIP 未配置或格式无效。");

            var url = baseUrl + path;
            try
            {
                using var content = new StringContent(
                    body?.ToString(Formatting.None) ?? "{}",
                    Encoding.UTF8,
                    "application/json");
                using var response = await s_http.PostAsync(url, content, ct).ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                    return BuildClientError($"HTTP {(int)response.StatusCode}", text);

                try
                {
                    return JObject.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
                }
                catch
                {
                    return BuildClientError("返回体不是有效 JSON。", text);
                }
            }
            catch (Exception ex)
            {
                return BuildClientError("请求异常：" + ex.Message);
            }
        }

        private static string BuildBaseUrl(AppConfig cfg)
        {
            var endpoint = cfg?.MCPServerIP;
            if (string.IsNullOrWhiteSpace(endpoint))
                return string.Empty;

            var text = endpoint.Trim();
            if (!text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                text = "http://" + text;
            }

            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
                return string.Empty;

            if (string.IsNullOrWhiteSpace(uri.Host) || uri.Port <= 0)
                return string.Empty;

            return $"{uri.Scheme}://{uri.Host}:{uri.Port}";
        }

        private static JObject BuildClientError(string message, string detail = null)
        {
            var jo = new JObject
            {
                ["type"] = "error",
                ["where"] = "AgentMcpBridgeClient",
                ["ok"] = false,
                ["message"] = message ?? "请求失败"
            };
            if (!string.IsNullOrWhiteSpace(detail))
                jo["detail"] = detail;
            return jo;
        }
    }
}

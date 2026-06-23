using EW_Assistant.Component.MindMap;
using EW_Assistant.Diagnostics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EW_Assistant.Services
{
    /// <summary>
    /// 基于 FileWorkflowClient 的文档→思维导图桥接器，负责调用 Workflow、解析 JSON 并构建 MindMapNode。
    /// </summary>
    public sealed class DocumentMindMapParser
    {
        private readonly MindmapService _mindmapService;
        private readonly string _prompt;
        private static readonly object s_mindmapLogLock = new object();

        private const string DefaultPrompt = "";
        public DocumentMindMapParser(string promptOverride = null)
        {
            _mindmapService = new MindmapService();
            _prompt = string.IsNullOrWhiteSpace(promptOverride) ? DefaultPrompt : promptOverride;
        }

        /// <summary>
        /// 调用 Workflow 生成思维导图 JSON，并解析为 MindMapNode 树；失败抛出异常。
        /// </summary>
        public async Task<MindMapNode> ParseAsync(string filePath, CancellationToken token = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("文件路径不能为空", "filePath");
            if (!File.Exists(filePath))
                throw new FileNotFoundException("未找到文档", filePath);

            var prompt = BuildPrompt(Path.GetFileName(filePath));
            var userId = BuildUserId();

            var extraInputs = BuildExtraInputs();
            var jsonText = await _mindmapService
                .BuildMindmapJsonAsync(filePath, prompt, userId, token, extraInputs)
                .ConfigureAwait(false);

            var normalized = NormalizeJson(jsonText);
            AppendMindmapLog(filePath, normalized);
            var rootToken = ParseJson(normalized);
            var rootNode = ConvertToken(rootToken);
            if (rootNode == null)
                throw new InvalidOperationException("Workflow 未返回有效的思维导图结构。");

            return rootNode;
        }

        private string BuildPrompt(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return _prompt;
            return string.Format("{0}文件名：{1}", _prompt, fileName);
        }

        private static string BuildUserId()
        {
            var user = Environment.UserName;
            if (string.IsNullOrWhiteSpace(user))
                user = "mindmap";
            return "mindmap-" + user.ToLowerInvariant();
        }

        private static IDictionary<string, object> BuildExtraInputs()
        {
            return new Dictionary<string, object>
            {
                ["mindmap"] = true,
                ["checklist"] = false
            };
        }

        private static string NormalizeJson(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                throw new InvalidOperationException("Workflow 未返回任何内容。");

            var trimmed = raw.Trim();
            if (trimmed.StartsWith("{", StringComparison.Ordinal) &&
                trimmed.EndsWith("}", StringComparison.Ordinal))
                return trimmed;

            var start = trimmed.IndexOf('{');
            var end = trimmed.LastIndexOf('}');
            if (start >= 0 && end >= start)
                return trimmed.Substring(start, end - start + 1);

            return trimmed;
        }

        private static JObject ParseJson(string json)
        {
            try
            {
                return JObject.Parse(json);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("无法解析 Workflow 返回的 JSON。", ex);
            }
        }

        private static MindMapNode ConvertToken(JToken token)
        {
            var obj = token as JObject;
            if (obj == null)
                return null;

            var title = obj.Value<string>("title");
            if (string.IsNullOrWhiteSpace(title))
                title = "未命名节点";
            var node = new MindMapNode(title)
            {
                IsExpanded = true
            };

            var body = obj.Value<string>("body");
            if (!string.IsNullOrWhiteSpace(body))
                node.AppendBodyText(body.Trim());

            var children = obj["children"] as JArray;
            if (children != null)
            {
                foreach (var childToken in children)
                {
                    var childNode = ConvertToken(childToken);
                    if (childNode != null)
                        node.Children.Add(childNode);
                }
            }

            return node;
        }

        /// <summary>将思维导图原始 JSON 结果落盘，便于排查。</summary>
        private static void AppendMindmapLog(string filePath, string json)
        {
            try
            {
                var dir = Path.Combine("D:\\", "Data", "AiLog", "DocumentAI");
                Directory.CreateDirectory(dir);
                var logPath = Path.Combine(dir, DateTime.Now.ToString("yyyy-MM-dd") + ".txt");

                var sb = new StringBuilder();
                sb.AppendFormat("[{0:yyyy-MM-dd HH:mm:ss}] 文件={1} 长度={2}", DateTime.Now, Path.GetFileName(filePath), json?.Length ?? 0);
                sb.AppendLine();
                if (!string.IsNullOrWhiteSpace(json))
                    sb.AppendLine(json.Trim());
                sb.AppendLine(new string('-', 80));

                lock (s_mindmapLogLock)
                {
                    File.AppendAllText(logPath, sb.ToString(), new UTF8Encoding(false));
                }

                LogRetentionPolicy.TryCleanupFiles(
                    dir,
                    "*.txt",
                    SearchOption.TopDirectoryOnly,
                    TimeSpan.FromDays(30));
            }
            catch
            {
                // 日志失败不影响主流程
            }
        }
    }
}

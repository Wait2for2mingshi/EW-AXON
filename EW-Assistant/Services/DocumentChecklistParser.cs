using EW_Assistant.Component.Checklist;
using EW_Assistant.Diagnostics;
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
    /// 调用 Workflow 生成 Checklist 文本并解析为模型。
    /// </summary>
    public sealed class DocumentChecklistParser
    {
        private readonly MindmapService _workflowService;
        private readonly string _prompt;
        private static readonly object s_checklistLogLock = new object();

        private const string DefaultPrompt = "";

        public DocumentChecklistParser(string promptOverride = null)
        {
            _workflowService = new MindmapService();
            _prompt = string.IsNullOrWhiteSpace(promptOverride) ? DefaultPrompt : promptOverride;
        }

        /// <summary>
        /// 调用 Workflow 生成 Checklist JSON，并解析为模型对象；日志落盘方便追踪。
        /// </summary>
        public async Task<DocumentChecklist> ParseAsync(string filePath, CancellationToken token = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("文件路径不能为空", "filePath");
            if (!File.Exists(filePath))
                throw new FileNotFoundException("未找到文档", filePath);

            var prompt = BuildPrompt(Path.GetFileName(filePath));
            var userId = BuildUserId();
            var extraInputs = BuildExtraInputs(requestChecklist: true);

            var jsonText = await _workflowService
                .BuildMindmapJsonAsync(filePath, prompt, userId, token, extraInputs)
                .ConfigureAwait(false);

            var normalized = NormalizeJson(jsonText);
            AppendChecklistLog(filePath, normalized);

            return ParseFromJson(normalized);
        }

        /// <summary>
        /// 直接解析 Checklist JSON 字符串为模型，适用于缓存/离线重放。
        /// </summary>
        public DocumentChecklist ParseFromJson(string jsonText)
        {
            var normalized = NormalizeJson(jsonText);
            var root = JObject.Parse(normalized);
            var result = new DocumentChecklist
            {
                Title = GetString(root, "title"),
                Description = GetString(root, "description") ?? GetString(root, "summary")
            };

            var groupsNode = root["groups"] ?? root["sections"] ?? root["checklist"];
            if (groupsNode == null && root.Type == JTokenType.Array)
                groupsNode = root;

            var array = groupsNode as JArray;
            if (array != null)
            {
                var order = 1;
                foreach (var token in array)
                {
                    var group = ParseGroup(token as JObject, order);
                    if (group != null)
                    {
                        result.Groups.Add(group);
                        order++;
                    }
                }
            }

            return result;
        }

        private static ChecklistGroup ParseGroup(JObject obj, int fallbackOrder)
        {
            if (obj == null)
                return null;

            var group = new ChecklistGroup
            {
                Order = obj.Value<int?>("order") ?? fallbackOrder,
                Title = NormalizeTitle(obj.Value<string>("title"), fallbackOrder),
                Description = GetString(obj, "description") ?? GetString(obj, "summary")
            };

            var itemsNode = obj["items"] ?? obj["steps"];
            var itemsArray = itemsNode as JArray;
            if (itemsArray != null)
            {
                var itemIndex = 1;
                foreach (var token in itemsArray)
                {
                    var item = ParseItem(token as JObject, itemIndex);
                    if (item != null)
                    {
                        group.Items.Add(item);
                        itemIndex++;
                    }
                }
            }

            return group;
        }

        private static ChecklistItem ParseItem(JObject obj, int fallbackOrder)
        {
            if (obj == null)
                return null;

            var item = new ChecklistItem
            {
                Order = obj.Value<int?>("order") ?? obj.Value<int?>("step") ?? fallbackOrder,
                Title = GetNonEmpty(obj.Value<string>("title"), "未命名步骤"),
                Detail = GetString(obj, "detail") ?? GetString(obj, "description") ?? string.Empty,
                Status = ChecklistItemStatusHelper.Parse(obj.Value<string>("status"), ChecklistItemStatus.Pending),
                Note = string.Empty
            };

            return item;
        }

        private static string NormalizeJson(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                throw new InvalidOperationException("Workflow 未返回任何内容");

            var trimmed = raw.Trim();
            if (trimmed.StartsWith("{", StringComparison.Ordinal) &&
                trimmed.EndsWith("}", StringComparison.Ordinal))
                return trimmed;

            if (trimmed.StartsWith("[", StringComparison.Ordinal) &&
                trimmed.EndsWith("]", StringComparison.Ordinal))
                return trimmed;

            var start = trimmed.IndexOf('{');
            var end = trimmed.LastIndexOf('}');
            if (start >= 0 && end >= start)
                return trimmed.Substring(start, end - start + 1);

            start = trimmed.IndexOf('[');
            end = trimmed.LastIndexOf(']');
            if (start >= 0 && end >= start)
                return trimmed.Substring(start, end - start + 1);

            return trimmed;
        }

        private static IDictionary<string, object> BuildExtraInputs(bool requestChecklist)
        {
            return new Dictionary<string, object>
            {
                ["mindmap"] = !requestChecklist,
                ["checklist"] = requestChecklist
            };
        }

        private static string BuildPrompt(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return DefaultPrompt;
            return string.Format("{0} 文件名：{1}", DefaultPrompt, fileName);
        }

        private static string BuildUserId()
        {
            var user = Environment.UserName;
            if (string.IsNullOrWhiteSpace(user))
                user = "checklist";
            return "checklist-" + user.ToLowerInvariant();
        }

        private static string GetString(JObject obj, string propertyName)
        {
            if (obj == null)
                return null;
            var token = obj[propertyName];
            return token == null ? null : token.ToString();
        }

        private static string NormalizeTitle(string title, int order)
        {
            var cleaned = GetNonEmpty(title, null);
            if (!string.IsNullOrWhiteSpace(cleaned))
                return cleaned;
            return string.Format("步骤组 {0}", order);
        }

        private static string GetNonEmpty(string text, string fallback)
        {
            if (string.IsNullOrWhiteSpace(text))
                return fallback;
            return text.Trim();
        }

        /// <summary>将 Checklist 的原始 JSON 结果落盘，便于排查。</summary>
        private static void AppendChecklistLog(string filePath, string json)
        {
            try
            {
                var dir = Path.Combine("D:\\", "Data", "AiLog", "DocumentAI");
                Directory.CreateDirectory(dir);
                var logPath = Path.Combine(dir, DateTime.Now.ToString("yyyy-MM-dd") + ".txt");

                var sb = new StringBuilder();
                sb.AppendFormat("[{0:yyyy-MM-dd HH:mm:ss}] [Checklist] 文件={1} 长度={2}", DateTime.Now, Path.GetFileName(filePath), json?.Length ?? 0);
                sb.AppendLine();
                if (!string.IsNullOrWhiteSpace(json))
                    sb.AppendLine(json.Trim());
                sb.AppendLine(new string('-', 80));

                lock (s_checklistLogLock)
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

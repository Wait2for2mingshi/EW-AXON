using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Linq;
using System.Text;
using EW_Assistant.Diagnostics;

namespace EW_Assistant.Services
{
    internal static class UiTaskRouteTraceWriter
    {
        private static readonly object s_logLock = new object();

        public static void Write(UiTaskRouteResult result)
        {
            try
            {
                Directory.CreateDirectory(AgentControlPaths.RouterLogRoot);

                var now = result?.CompletedAtLocal ?? DateTime.Now;
                var summaryLogPath = Path.Combine(AgentControlPaths.RouterLogRoot, now.ToString("yyyy-MM-dd-HH") + ".log");
                var detailDir = Path.Combine(AgentControlPaths.RouterLogRoot, "Details", now.ToString("yyyy-MM-dd"));
                Directory.CreateDirectory(detailDir);
                var traceIdPart = MakeSafeFileNamePart(result?.RouteTraceId, 64);
                var detailLogPath = Path.Combine(detailDir, now.ToString("yyyyMMdd-HHmmssfff") + "-" + traceIdPart + ".log");

                var summaryText = BuildSummaryTrace(result, detailLogPath);
                var detailText = BuildDetailTrace(result);

                lock (s_logLock)
                {
                    File.WriteAllText(detailLogPath, detailText, new UTF8Encoding(false));
                    File.AppendAllText(summaryLogPath, summaryText + Environment.NewLine + new string('-', 64) + Environment.NewLine, new UTF8Encoding(false));
                }

                LogRetentionPolicy.TryCleanupFiles(
                    AgentControlPaths.RouterLogRoot,
                    "*.log",
                    SearchOption.AllDirectories,
                    TimeSpan.FromDays(14),
                    deleteEmptyDirectories: true);
            }
            catch
            {
                // 路由日志失败不影响主流程
            }
        }

        private static string BuildSummaryTrace(UiTaskRouteResult result, string detailLogPath)
        {
            var sb = new StringBuilder();
            var completedAt = result?.CompletedAtLocal ?? DateTime.Now;
            sb.AppendLine("时间：" + completedAt.ToString("yyyy-MM-dd HH:mm:ss"));
            AppendKeyValue(sb, "RouteTraceId", result?.RouteTraceId);
            AppendKeyValue(sb, "结果", result?.Succeeded == true ? "成功" : "失败", showIfEmpty: true);
            AppendKeyValue(sb, "执行模式", result?.BrainDryRunEnabled == true ? "Brain Dry Run" : "正常联调", showIfEmpty: true);
            AppendKeyValue(sb, "主脑决策", result?.BrainResult?.Decision, showIfEmpty: true);
            AppendKeyValue(sb, "主脑回复", result?.FinalReply, showIfEmpty: true);
            AppendKeyValue(sb, "执行器", result?.ExecutorDescriptor?.Key, showIfEmpty: true);
            AppendKeyValue(sb, "CommandCatalogMode", result?.ResolvedCommandCatalogMode, showIfEmpty: true);
            AppendKeyValue(sb, "执行器跳过", result?.ExecutorSkipped == true ? "是" : "否", showIfEmpty: true);
            AppendKeyValue(sb, "跳过说明", result?.ExecutorSkipReason, showIfEmpty: true);
            AppendKeyValue(sb, "ExecutorWorkflowRunId", result?.ExecutorResult?.WorkflowRunId, showIfEmpty: true);
            AppendKeyValue(sb, "ExecutorTaskId", result?.ExecutorResult?.TaskId, showIfEmpty: true);
            AppendKeyValue(sb, "错误信息", result?.ErrorMessage, showIfEmpty: true);
            AppendKeyValue(sb, "详细日志", detailLogPath);
            return sb.ToString().TrimEnd();
        }

        private static string BuildDetailTrace(UiTaskRouteResult result)
        {
            var sb = new StringBuilder();
            var completedAt = result?.CompletedAtLocal ?? DateTime.Now;
            sb.AppendLine("时间：" + completedAt.ToString("yyyy-MM-dd HH:mm:ss"));
            AppendKeyValue(sb, "RouteTraceId", result?.RouteTraceId);
            AppendKeyValue(sb, "最终决策", result?.FinalDecision, showIfEmpty: true);
            AppendKeyValue(sb, "最终回复", result?.FinalReply, showIfEmpty: true);
            AppendKeyValue(sb, "结果", result?.Succeeded == true ? "成功" : "失败", showIfEmpty: true);
            AppendKeyValue(sb, "执行模式", result?.BrainDryRunEnabled == true ? "Brain Dry Run" : "正常联调", showIfEmpty: true);
            AppendKeyValue(sb, "执行器跳过", result?.ExecutorSkipped == true ? "是" : "否", showIfEmpty: true);
            AppendKeyValue(sb, "跳过说明", result?.ExecutorSkipReason, showIfEmpty: true);
            AppendKeyValue(sb, "错误信息", result?.ErrorMessage, showIfEmpty: true);

            sb.AppendLine();
            sb.AppendLine("Brain");
            sb.AppendLine("----------------------------------------");
            AppendKeyValue(sb, "WorkflowRunId", result?.BrainResult?.WorkflowRunId, showIfEmpty: true);
            AppendKeyValue(sb, "TaskId", result?.BrainResult?.TaskId, showIfEmpty: true);
            AppendKeyValue(sb, "Decision", result?.BrainResult?.Decision, showIfEmpty: true);
            AppendKeyValue(sb, "ExecutorKey", result?.BrainResult?.ExecutorKey, showIfEmpty: true);
            AppendKeyValue(sb, "ExecutorGoal", result?.BrainResult?.ExecutorGoal, showIfEmpty: true);
            AppendKeyValue(sb, "CommandCatalogMode", result?.BrainResult?.CommandCatalogMode, showIfEmpty: true);
            AppendKeyValue(sb, "ResolvedCommandCatalogMode", result?.ResolvedCommandCatalogMode, showIfEmpty: true);
            AppendKeyValue(sb, "UserReply", result?.BrainResult?.UserReply, showIfEmpty: true);
            AppendKeyValue(sb, "Reason", result?.BrainResult?.Reason, showIfEmpty: true);
            AppendJsonSection(sb, "Brain 请求输入", result?.BrainResult?.RequestInputsJson);
            AppendJsonSection(sb, "Brain 请求体", result?.BrainResult?.RequestPayloadJson);
            AppendJsonSection(sb, "Brain 输出", result?.BrainResult?.Outputs);
            AppendJsonSection(sb, "Brain 结构化结果", result?.BrainResult?.BrainResultJson);

            if (result?.ExecutorResult != null)
            {
                sb.AppendLine();
                sb.AppendLine("Executor");
                sb.AppendLine("----------------------------------------");
                AppendKeyValue(sb, "ExecutorKey", result?.ExecutorDescriptor?.Key, showIfEmpty: true);
                AppendKeyValue(sb, "WorkflowRunId", result?.ExecutorResult?.WorkflowRunId, showIfEmpty: true);
                AppendKeyValue(sb, "TaskId", result?.ExecutorResult?.TaskId, showIfEmpty: true);
                AppendKeyValue(sb, "Goal", result?.ExecutorResult?.Goal, showIfEmpty: true);
                AppendKeyValue(sb, "CommandCatalogMode", result?.ExecutorResult?.CommandCatalogMode, showIfEmpty: true);
                AppendKeyValue(sb, "错误信息", result?.ExecutorResult?.ErrorMessage, showIfEmpty: true);
                AppendJsonSection(sb, "Executor 请求输入", result?.ExecutorResult?.RequestInputsJson);
                AppendJsonSection(sb, "Executor 请求体", result?.ExecutorResult?.RequestPayloadJson);
                AppendJsonSection(sb, "Executor 输出", result?.ExecutorResult?.Outputs);
                AppendJsonSection(sb, "Executor 最终结果 JSON", result?.ExecutorResult?.ResultJson);
            }

            return sb.ToString().TrimEnd();
        }

        private static void AppendKeyValue(StringBuilder sb, string label, string value, bool showIfEmpty = false)
        {
            if (sb == null || string.IsNullOrWhiteSpace(label))
                return;

            var normalized = value == null ? string.Empty : value.Trim();
            if (!showIfEmpty && string.IsNullOrWhiteSpace(normalized))
                return;

            sb.AppendLine(label + "：" + (string.IsNullOrWhiteSpace(normalized) ? "-" : normalized));
        }

        private static void AppendJsonSection(StringBuilder sb, string title, object value)
        {
            if (sb == null || string.IsNullOrWhiteSpace(title) || value == null)
                return;

            string text = value switch
            {
                string raw => DifyOutputSanitizer.Clean(raw),
                JToken token => DifyOutputSanitizer.CleanToken(token, Formatting.Indented),
                _ => DifyOutputSanitizer.Clean(JsonConvert.SerializeObject(value, Formatting.Indented))
            };

            if (string.IsNullOrWhiteSpace(text))
                return;

            sb.AppendLine();
            sb.AppendLine(title);
            sb.AppendLine("----------------------------------------");
            sb.AppendLine(text.Trim());
        }

        private static string MakeSafeFileNamePart(string value, int maxLength)
        {
            var text = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text))
                return "no-trace-id";

            var invalidChars = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder();
            foreach (var ch in text)
            {
                if (invalidChars.Contains(ch))
                    continue;

                builder.Append(ch);
                if (builder.Length >= maxLength)
                    break;
            }

            return builder.Length == 0 ? "no-trace-id" : builder.ToString();
        }
    }
}

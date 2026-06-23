using EW_Assistant.Settings;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace EW_Assistant.Services
{
    internal sealed class BrainWorkflowService
    {
        private readonly AgentWorkspaceService _workspaceService = new AgentWorkspaceService();

        public async Task<BrainWorkflowResult> RunAsync(BrainWorkflowRequest request, CancellationToken ct = default)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.Goal))
                throw new InvalidOperationException("请先填写目标。");

            var cfg = ConfigService.Current ?? throw new InvalidOperationException("配置尚未加载。");
            if (string.IsNullOrWhiteSpace(cfg.URL))
                throw new InvalidOperationException("URL 未配置，无法调用 Brain workflow。");
            var brainApiKey = ConfigService.ResolveBrainWorkflowApiKey(cfg);
            if (string.IsNullOrWhiteSpace(brainApiKey))
                throw new InvalidOperationException("Brain Key 未配置，无法调用 Brain workflow。");

            var workspace = _workspaceService.LoadSnapshot(DateTime.Now);
            var userId = string.IsNullOrWhiteSpace(cfg.User) ? Environment.MachineName : cfg.User.Trim();
            var extraInputs = new Dictionary<string, object>
            {
                ["goal"] = request.Goal.Trim(),
                ["persona_md"] = workspace.PersonaMarkdown,
                ["memory_digest_md"] = workspace.MemoryDigestMarkdown,
                ["daily_memory_md"] = workspace.DailyMemoryMarkdown,
                ["available_executors_json"] = workspace.AvailableExecutorsJson,
                ["route_trace_id"] = request.RouteTraceId ?? string.Empty
            };
            var requestInputsJson = JObject.FromObject(extraInputs).ToString(Newtonsoft.Json.Formatting.Indented);
            var requestPayloadJson = new JObject
            {
                ["inputs"] = JObject.FromObject(extraInputs),
                ["response_mode"] = "blocking",
                ["user"] = userId
            }.ToString(Newtonsoft.Json.Formatting.Indented);

            FileWorkflowResult workflowResult;
            using (var client = new FileWorkflowClient(new FileWorkflowClientOptions
            {
                BaseUrl = cfg.URL,
                ApiKey = brainApiKey
            }))
            {
                workflowResult = await client.RunAsync(new FileWorkflowRequest
                {
                    UserId = userId,
                    PromptVariableName = null,
                    OutputFieldName = "brain_result_json",
                    ExtraInputs = extraInputs
                }, ct).ConfigureAwait(false);
            }

            var outputs = workflowResult.Outputs;
            var resultToken = AgentControlValueHelper.ParseJsonLike(
                outputs?["brain_result_json"]?.ToString()
                ?? workflowResult.OutputText
                ?? outputs?.ToString(Newtonsoft.Json.Formatting.None),
                new JObject()) as JObject ?? new JObject();
            var executorKeys = ExecutorRegistryService.ParseExecutorKeys(workspace.AvailableExecutorsJson);

            var decision = AgentControlValueHelper.NormalizeDecision(resultToken.Value<string>("decision"));
            var executorKey = (resultToken.Value<string>("executor_key") ?? string.Empty).Trim();
            var executorGoal = DifyOutputSanitizer.Clean(resultToken.Value<string>("executor_goal"));
            var commandCatalogMode = (resultToken.Value<string>("command_catalog_mode") ?? string.Empty).Trim();
            var completionContractJson = NormalizeCompletionContractJson(resultToken["completion_contract_json"]);
            var userReply = DifyOutputSanitizer.Clean(resultToken.Value<string>("user_reply"));
            var reason = DifyOutputSanitizer.Clean(resultToken.Value<string>("reason"));

            if (!string.Equals(decision, "run_executor", StringComparison.OrdinalIgnoreCase))
            {
                executorKey = string.Empty;
                executorGoal = string.Empty;
                commandCatalogMode = string.Empty;
            }
            else if (!executorKeys.Contains(executorKey, StringComparer.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(executorGoal))
            {
                decision = "need_human";
                executorKey = string.Empty;
                executorGoal = string.Empty;
                commandCatalogMode = string.Empty;
                if (string.IsNullOrWhiteSpace(userReply))
                    userReply = "主脑当前还无法稳定确定执行器或执行目标。";
                if (string.IsNullOrWhiteSpace(reason))
                    reason = "Brain 输出缺少有效的 executor_key 或 executor_goal。";
            }

            if (string.IsNullOrWhiteSpace(userReply))
            {
                userReply = decision switch
                {
                    "reply_direct" => "这是一个不需要执行器的请求。",
                    "reject" => "这个请求当前不适合直接执行。",
                    "run_executor" => "我会先交给执行器继续处理。",
                    _ => "当前信息还不够，我需要更明确的目标。"
                };
            }

            if (string.IsNullOrWhiteSpace(reason))
            {
                reason = decision switch
                {
                    "reply_direct" => "该请求属于直接回复场景。",
                    "reject" => "请求超出边界或不应执行。",
                    "run_executor" => "目标明确，适合交给执行器。",
                    _ => "目标不够清晰或执行条件不足。"
                };
            }

            var normalized = new JObject
            {
                ["decision"] = decision,
                ["executor_key"] = executorKey,
                ["executor_goal"] = executorGoal,
                ["command_catalog_mode"] = commandCatalogMode,
                ["completion_contract_json"] = AgentControlValueHelper.ParseJsonLike(completionContractJson, new JObject()) as JObject ?? new JObject(),
                ["user_reply"] = userReply,
                ["reason"] = reason
            };

            return new BrainWorkflowResult
            {
                Succeeded = workflowResult.Succeeded,
                WorkflowRunId = workflowResult.WorkflowRunId ?? string.Empty,
                TaskId = workflowResult.TaskId ?? string.Empty,
                Decision = decision,
                ExecutorKey = executorKey,
                ExecutorGoal = executorGoal,
                CommandCatalogMode = commandCatalogMode,
                CompletionContractJson = completionContractJson,
                UserReply = userReply,
                Reason = reason,
                ErrorMessage = workflowResult.Succeeded
                    ? string.Empty
                    : AgentControlValueHelper.FirstNonEmpty(workflowResult.ErrorMessage, workflowResult.RawResponse),
                RequestInputsJson = requestInputsJson,
                RequestPayloadJson = requestPayloadJson,
                BrainResultJson = normalized.ToString(Newtonsoft.Json.Formatting.None),
                Outputs = outputs,
                RawResponse = workflowResult.RawResponse ?? string.Empty
            };
        }

        private static string NormalizeCompletionContractJson(JToken token)
        {
            var jo = AgentControlValueHelper.ParseJsonLike(token, new JObject()) as JObject ?? new JObject();
            var normalized = new JObject
            {
                ["done_when"] = new JArray(NormalizeTextList(jo["done_when"])),
                ["not_enough"] = new JArray(NormalizeTextList(jo["not_enough"]))
            };

            if (!normalized["done_when"].Any())
                normalized["done_when"] = new JArray("屏幕上出现明确的目标完成证据");
            if (!normalized["not_enough"].Any())
                normalized["not_enough"] = new JArray("仅推测成功但没有明确完成证据");

            return normalized.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static IEnumerable<string> NormalizeTextList(JToken token)
        {
            if (token is not JArray arr)
                return Enumerable.Empty<string>();

            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in arr)
            {
                var text = item?.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(text))
                    continue;
                if (seen.Contains(text))
                    continue;
                seen.Add(text);
                result.Add(text);
                if (result.Count >= 8)
                    break;
            }
            return result;
        }
    }
}

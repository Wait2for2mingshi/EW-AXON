using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace EW_Assistant.Services
{
    /// <summary>
    /// 本地任务路由层：先调用 Brain workflow，再根据结果路由到具体 Executor workflow。
    /// </summary>
    public sealed class UiTaskRouterService
    {
        private static readonly Lazy<UiTaskRouterService> s_lazy =
            new Lazy<UiTaskRouterService>(() => new UiTaskRouterService());

        private readonly BrainWorkflowService _brainWorkflowService = new BrainWorkflowService();
        private readonly ExecutorRegistryService _executorRegistryService = new ExecutorRegistryService();

        public static UiTaskRouterService Instance => s_lazy.Value;

        private UiTaskRouterService() { }

        public async Task<UiTaskRouteResult> RunStreamingAsync(
            UiTaskRouteRequest request,
            Func<UiTaskRouteDispatchInfo, Task> onDispatch = null,
            Func<UiCoarseVisionWorkflowStreamEvent, Task> onExecutorEvent = null,
            CancellationToken ct = default)
        {
            if (!AgentAutomationService.ModuleEnabled)
                throw new InvalidOperationException("智能体控制模块已冻结。");
            if (request == null)
                throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.Goal))
                throw new InvalidOperationException("请先填写目标。");

            var routeTraceId = string.IsNullOrWhiteSpace(request.RouteTraceId)
                ? AgentControlValueHelper.CreateRouteTraceId()
                : request.RouteTraceId.Trim();
            MainWindow.PostProgramInfo("[UiTaskRoute] 开始调用 Brain workflow，traceId=" + routeTraceId, "info");

            var brainResult = await _brainWorkflowService.RunAsync(new BrainWorkflowRequest
            {
                Goal = request.Goal,
                RouteTraceId = routeTraceId
            }, ct).ConfigureAwait(false);

            UiTaskRouteResult routeResult;
            if (!brainResult.Succeeded)
            {
                routeResult = new UiTaskRouteResult
                {
                    RouteTraceId = routeTraceId,
                    BrainResult = brainResult,
                    FinalDecision = "need_human",
                    FinalReply = string.IsNullOrWhiteSpace(brainResult.UserReply)
                        ? "主脑调用失败，当前无法继续自动执行。"
                        : brainResult.UserReply,
                    Succeeded = false,
                    ErrorMessage = string.IsNullOrWhiteSpace(brainResult.ErrorMessage)
                        ? "Brain workflow 调用失败。"
                        : brainResult.ErrorMessage,
                    BrainDryRunEnabled = request.BrainDryRunEnabled,
                    CompletedAtLocal = DateTime.Now
                };
                UiTaskRouteTraceWriter.Write(routeResult);
                return routeResult;
            }

            if (!string.Equals(brainResult.Decision, "run_executor", StringComparison.OrdinalIgnoreCase))
            {
                routeResult = new UiTaskRouteResult
                {
                    RouteTraceId = routeTraceId,
                    BrainResult = brainResult,
                    FinalDecision = AgentControlValueHelper.NormalizeDecision(brainResult.Decision),
                    FinalReply = AgentControlValueHelper.FirstNonEmpty(
                        brainResult.UserReply,
                        string.Equals(brainResult.Decision, "reply_direct", StringComparison.OrdinalIgnoreCase)
                            ? "这是一个不需要执行器的请求。"
                            : "当前需要人工补充信息。"),
                    Succeeded = string.Equals(brainResult.Decision, "reply_direct", StringComparison.OrdinalIgnoreCase),
                    ErrorMessage = string.Empty,
                    BrainDryRunEnabled = request.BrainDryRunEnabled,
                    CompletedAtLocal = DateTime.Now
                };
                UiTaskRouteTraceWriter.Write(routeResult);
                return routeResult;
            }

            var descriptor = _executorRegistryService.Resolve(brainResult.ExecutorKey);
            if (descriptor == null)
            {
                routeResult = new UiTaskRouteResult
                {
                    RouteTraceId = routeTraceId,
                    BrainResult = brainResult,
                    FinalDecision = "need_human",
                    FinalReply = "主脑已选中执行器，但本地未找到可用的 executor 映射。",
                    Succeeded = false,
                    ErrorMessage = "未找到 executor_key=" + AgentControlValueHelper.SafeValue(brainResult.ExecutorKey) + " 对应的本地执行器映射。",
                    BrainDryRunEnabled = request.BrainDryRunEnabled,
                    CompletedAtLocal = DateTime.Now
                };
                UiTaskRouteTraceWriter.Write(routeResult);
                return routeResult;
            }

            var commandCatalogMode = string.IsNullOrWhiteSpace(brainResult.CommandCatalogMode)
                ? descriptor.DefaultCommandCatalogMode
                : brainResult.CommandCatalogMode.Trim();
            if (string.IsNullOrWhiteSpace(commandCatalogMode))
                commandCatalogMode = "grounded_light_compact";

            MainWindow.PostProgramInfo(
                "[UiTaskRoute] Brain 已选中 executor，key=" + descriptor.Key + "。",
                "info");

            if (request.BrainDryRunEnabled)
            {
                routeResult = new UiTaskRouteResult
                {
                    RouteTraceId = routeTraceId,
                    BrainResult = brainResult,
                    ExecutorDescriptor = descriptor,
                    ResolvedCommandCatalogMode = commandCatalogMode,
                    FinalDecision = "run_executor",
                    FinalReply = AgentControlValueHelper.FirstNonEmpty(brainResult.UserReply, "主脑已完成路由，本次未实际启动 executor。"),
                    Succeeded = true,
                    ErrorMessage = string.Empty,
                    BrainDryRunEnabled = true,
                    ExecutorSkipped = true,
                    ExecutorSkipReason = "Brain Dry Run 已开启：已验证主脑路由与本地 executor 映射，但未实际启动 executor。",
                    CompletedAtLocal = DateTime.Now
                };
                MainWindow.PostProgramInfo(
                    "[UiTaskRoute] Brain Dry Run 已开启，已验证 executor 路由但未实际启动 executor。",
                    "info");
                UiTaskRouteTraceWriter.Write(routeResult);
                return routeResult;
            }

            if (onDispatch != null)
            {
                await onDispatch(new UiTaskRouteDispatchInfo
                {
                    RouteTraceId = routeTraceId,
                    ExecutorKey = descriptor.Key,
                    ExecutorApiKey = descriptor.ApiKey,
                    ExecutorGoal = brainResult.ExecutorGoal,
                    CommandCatalogMode = commandCatalogMode
                }).ConfigureAwait(false);
            }

            var executorExtraInputs = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["route_trace_id"] = routeTraceId,
                ["brain_reason"] = brainResult.Reason ?? string.Empty,
                ["completion_contract_json"] = brainResult.CompletionContractJson ?? "{}",
                ["executor_key"] = descriptor.Key ?? string.Empty
            };

            var executorResult = await UiCoarseVisionWorkflowService.Instance.RunStreamingAsync(
                new UiCoarseVisionWorkflowRequest
                {
                    Goal = brainResult.ExecutorGoal,
                    CommandCatalogMode = commandCatalogMode,
                    ApiKeyOverride = descriptor.ApiKey,
                    ExtraInputs = executorExtraInputs
                },
                onExecutorEvent,
                ct).ConfigureAwait(false);

            routeResult = new UiTaskRouteResult
            {
                RouteTraceId = routeTraceId,
                BrainResult = brainResult,
                ExecutorDescriptor = descriptor,
                ExecutorResult = executorResult,
                ResolvedCommandCatalogMode = commandCatalogMode,
                FinalDecision = "run_executor",
                FinalReply = AgentControlValueHelper.FirstNonEmpty(brainResult.UserReply, "已交给执行器继续处理。"),
                Succeeded = executorResult?.Succeeded == true,
                ErrorMessage = executorResult?.Succeeded == true
                    ? string.Empty
                    : AgentControlValueHelper.FirstNonEmpty(executorResult?.ErrorMessage, "Executor workflow 调用失败。"),
                BrainDryRunEnabled = false,
                CompletedAtLocal = DateTime.Now
            };
            UiTaskRouteTraceWriter.Write(routeResult);
            return routeResult;
        }

        public Task StopExecutorAsync(string taskId, string apiKeyOverride = null, CancellationToken ct = default)
        {
            return UiCoarseVisionWorkflowService.Instance.StopAsync(taskId, apiKeyOverride, ct);
        }
    }
}

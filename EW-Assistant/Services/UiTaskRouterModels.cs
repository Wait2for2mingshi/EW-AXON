using Newtonsoft.Json.Linq;
using System;

namespace EW_Assistant.Services
{
    public sealed class UiTaskRouteRequest
    {
        public string Goal { get; set; }
        public string RouteTraceId { get; set; }
        public bool BrainDryRunEnabled { get; set; }
    }

    public sealed class UiTaskRouteResult
    {
        public string RouteTraceId { get; set; }
        public BrainWorkflowResult BrainResult { get; set; }
        public ExecutorDescriptor ExecutorDescriptor { get; set; }
        public UiCoarseVisionWorkflowResult ExecutorResult { get; set; }
        public string ResolvedCommandCatalogMode { get; set; }
        public string FinalDecision { get; set; }
        public string FinalReply { get; set; }
        public bool Succeeded { get; set; }
        public string ErrorMessage { get; set; }
        public bool BrainDryRunEnabled { get; set; }
        public bool ExecutorSkipped { get; set; }
        public string ExecutorSkipReason { get; set; }
        public DateTime CompletedAtLocal { get; set; }
    }

    public sealed class UiTaskRouteDispatchInfo
    {
        public string RouteTraceId { get; set; }
        public string ExecutorKey { get; set; }
        public string ExecutorApiKey { get; set; }
        public string ExecutorGoal { get; set; }
        public string CommandCatalogMode { get; set; }
    }

    public sealed class BrainWorkflowRequest
    {
        public string Goal { get; set; }
        public string RouteTraceId { get; set; }
    }

    public sealed class BrainWorkflowResult
    {
        public bool Succeeded { get; set; }
        public string WorkflowRunId { get; set; }
        public string TaskId { get; set; }
        public string Decision { get; set; }
        public string ExecutorKey { get; set; }
        public string ExecutorGoal { get; set; }
        public string CommandCatalogMode { get; set; }
        public string CompletionContractJson { get; set; }
        public string UserReply { get; set; }
        public string Reason { get; set; }
        public string ErrorMessage { get; set; }
        public string RequestInputsJson { get; set; }
        public string RequestPayloadJson { get; set; }
        public string BrainResultJson { get; set; }
        public JObject Outputs { get; set; }
        public string RawResponse { get; set; }
    }

    public sealed class ExecutorDescriptor
    {
        public string Key { get; set; }
        public string DisplayName { get; set; }
        public string ApiKey { get; set; }
        public string DefaultCommandCatalogMode { get; set; }
        public bool Enabled { get; set; }
    }
}

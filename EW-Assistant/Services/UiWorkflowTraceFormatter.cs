using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace EW_Assistant.Services
{
    internal static class UiWorkflowTraceFormatter
    {
        internal static UiCoarseVisionWorkflowStreamEvent CloneEvent(UiCoarseVisionWorkflowStreamEvent evt)
        {
            if (evt == null)
                return null;

            return new UiCoarseVisionWorkflowStreamEvent
            {
                EventName = evt.EventName ?? string.Empty,
                WorkflowRunId = evt.WorkflowRunId ?? string.Empty,
                TaskId = evt.TaskId ?? string.Empty,
                NodeTitle = evt.NodeTitle ?? string.Empty,
                NodeType = evt.NodeType ?? string.Empty,
                Status = evt.Status ?? string.Empty,
                Data = evt.Data == null ? null : (evt.Data.DeepClone() as JObject),
                RawJson = evt.RawJson ?? string.Empty,
                LocalTimestamp = evt.LocalTimestamp
            };
        }

        internal static string BuildWorkflowStatusLine(UiCoarseVisionWorkflowStreamEvent evt)
        {
            if (evt == null)
                return string.Empty;

            var eventName = NormalizeSingleLine(evt.EventName);
            var nodeTitle = NormalizeSingleLine(evt.NodeTitle);
            var time = FormatEventTime(evt);

            if (string.Equals(eventName, "node_finished", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(nodeTitle, "validate_output", StringComparison.OrdinalIgnoreCase))
                {
                    var round = ResolveRoundIndex(evt, 1);
                    var summary = TryExtractDecisionSummaryFromEvent(evt);
                    return string.IsNullOrWhiteSpace(summary)
                        ? $"[{time}] 第{round}轮判定已返回。"
                        : $"[{time}] 第{round}轮判定 | {summary}";
                }

                var executeSummary = TryExtractRuntimeCommandSummaryFromEvent(evt);
                if (!string.IsNullOrWhiteSpace(executeSummary))
                {
                    var round = ResolveRoundIndex(evt, 1);
                    return $"[{time}] 第{round}轮执行 | {executeSummary}";
                }
            }

            if (string.Equals(eventName, "workflow_finished", StringComparison.OrdinalIgnoreCase))
                return $"[{time}] workflow_finished | status={SafeText(NormalizeSingleLine(evt.Status))}";

            return string.Empty;
        }

        internal static IReadOnlyList<string> BuildWorkflowRoundLines(IEnumerable<UiCoarseVisionWorkflowStreamEvent> events)
        {
            return BuildWorkflowRounds(events)
                .OrderBy(x => x.RoundIndex)
                .SelectMany(round =>
                {
                    var lines = new List<string>();
                    if (!string.IsNullOrWhiteSpace(round.DecisionSummary))
                        lines.Add($"[{SafeText(round.DecisionTime)}] 第{round.RoundIndex}轮判定 | {round.DecisionSummary}");
                    if (!string.IsNullOrWhiteSpace(round.ExecuteSummary))
                        lines.Add($"[{SafeText(round.ExecuteTime)}] 第{round.RoundIndex}轮执行 | {round.ExecuteSummary}");
                    return lines;
                })
                .ToList();
        }

        internal static string TryExtractWorkflowSummary(UiCoarseVisionWorkflowResult result)
        {
            if (result == null)
                return string.Empty;

            var outputs = result.Outputs;
            var status = TryGetOutputValue(outputs, "status", "final_status", "verification");
            var reason = TryGetOutputValue(outputs, "reason", "final_reason", "verification_reason");
            var steps = TryGetOutputValue(outputs, "steps", "step_index");
            var lastTarget = TryGetOutputValue(outputs, "last_target_hint", "target_hint");

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(status))
                parts.Add("status=" + status);
            if (!string.IsNullOrWhiteSpace(steps))
                parts.Add("steps=" + steps);
            if (!string.IsNullOrWhiteSpace(lastTarget))
                parts.Add("last_target_hint=" + TruncateSingleLine(lastTarget, 40));
            if (!string.IsNullOrWhiteSpace(reason))
                parts.Add("reason=" + TruncateSingleLine(reason, 80));

            if (parts.Count > 0)
                return string.Join("，", parts);

            var commandSummary = TryExtractLastResultSummary(result);
            if (!string.IsNullOrWhiteSpace(commandSummary))
                return commandSummary;

            return result.Succeeded ? string.Empty : NormalizeSingleLine(result.ErrorMessage);
        }

        internal static string TryExtractLastResultSummary(UiCoarseVisionWorkflowResult result)
        {
            var lastResultJson = TryGetOutputValue(result?.Outputs, "last_result_json");
            if (!string.IsNullOrWhiteSpace(lastResultJson))
            {
                var summary = TryExtractDecisionSummary(lastResultJson);
                if (!string.IsNullOrWhiteSpace(summary))
                    return summary;

                summary = TryExtractRuntimeCommandSummary(lastResultJson);
                if (!string.IsNullOrWhiteSpace(summary))
                    return summary;
            }

            var fallbackJson = result?.ResultJson;
            if (string.IsNullOrWhiteSpace(fallbackJson))
                return string.Empty;

            var fallback = TryExtractDecisionSummary(fallbackJson);
            if (!string.IsNullOrWhiteSpace(fallback))
                return fallback;

            return TryExtractRuntimeCommandSummary(fallbackJson);
        }

        internal static string TryExtractCommandCatalogVersion(string commandCatalogJson)
        {
            if (string.IsNullOrWhiteSpace(commandCatalogJson))
                return string.Empty;

            try
            {
                var token = JToken.Parse(commandCatalogJson);
                return (token as JObject)?.Value<string>("version") ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static List<WorkflowRoundTrace> BuildWorkflowRounds(IEnumerable<UiCoarseVisionWorkflowStreamEvent> events)
        {
            var rounds = new List<WorkflowRoundTrace>();
            var currentRound = 0;

            if (events == null)
                return rounds;

            foreach (var evt in events)
            {
                if (evt == null || !string.Equals(evt.EventName, "node_finished", StringComparison.OrdinalIgnoreCase))
                    continue;

                var nodeTitle = NormalizeSingleLine(evt.NodeTitle);
                if (string.IsNullOrWhiteSpace(nodeTitle))
                    continue;

                if (string.Equals(nodeTitle, "validate_output", StringComparison.OrdinalIgnoreCase))
                {
                    currentRound = ResolveRoundIndex(evt, currentRound + 1);
                    var round = GetOrCreateRound(rounds, currentRound);
                    round.DecisionTime = FormatEventTime(evt);
                    round.DecisionSummary = TryExtractDecisionSummaryFromEvent(evt);
                    continue;
                }

                var executeSummary = TryExtractRuntimeCommandSummaryFromEvent(evt);
                if (!string.IsNullOrWhiteSpace(executeSummary))
                {
                    var roundIndex = ResolveRoundIndex(evt, currentRound);
                    if (roundIndex <= 0)
                        continue;

                    var round = GetOrCreateRound(rounds, roundIndex);
                    round.ExecuteTime = FormatEventTime(evt);
                    round.ExecuteSummary = executeSummary;
                }
            }

            return rounds;
        }

        private static WorkflowRoundTrace GetOrCreateRound(ICollection<WorkflowRoundTrace> rounds, int roundIndex)
        {
            foreach (var item in rounds)
            {
                if (item.RoundIndex == roundIndex)
                    return item;
            }

            var created = new WorkflowRoundTrace { RoundIndex = Math.Max(1, roundIndex) };
            rounds.Add(created);
            return created;
        }

        private static int ResolveRoundIndex(UiCoarseVisionWorkflowStreamEvent evt, int fallback)
        {
            var raw = TryGetNestedValue(evt?.Data, "execution_metadata", "loop_index");
            if (int.TryParse(raw, out var parsed) && parsed >= 0)
                return parsed + 1;

            raw = TryGetEventValue(evt?.Data, "iteration", "iteration_index", "current_iteration", "index");
            if (int.TryParse(raw, out parsed) && parsed > 0)
                return parsed;

            return Math.Max(1, fallback);
        }

        private static string TryExtractDecisionSummaryFromEvent(UiCoarseVisionWorkflowStreamEvent evt)
        {
            var outputs = evt?.Data?["outputs"] as JObject;
            var resultJson = outputs?["result_json"]?.Type == JTokenType.String
                ? outputs.Value<string>("result_json")
                : outputs?["result_json"]?.ToString(Formatting.None);

            if (string.IsNullOrWhiteSpace(resultJson))
                resultJson = outputs?["text"]?.Type == JTokenType.String
                    ? outputs.Value<string>("text")
                    : outputs?["text"]?.ToString(Formatting.None);

            return TryExtractDecisionSummary(DifyOutputSanitizer.Clean(resultJson));
        }

        private static string TryExtractRuntimeCommandSummaryFromEvent(UiCoarseVisionWorkflowStreamEvent evt)
        {
            var outputs = evt?.Data?["outputs"] as JObject;
            var body = outputs?["body"]?.Type == JTokenType.String
                ? outputs.Value<string>("body")
                : outputs?["body"]?.ToString(Formatting.None);
            var summary = TryExtractRuntimeCommandSummary(DifyOutputSanitizer.Clean(body));
            if (!string.IsNullOrWhiteSpace(summary))
                return summary;

            return TryExtractRuntimeCommandSummary(DifyOutputSanitizer.CleanToken(outputs));
        }

        private static string TryExtractDecisionSummary(string resultJson)
        {
            if (string.IsNullOrWhiteSpace(resultJson))
                return string.Empty;

            resultJson = DifyOutputSanitizer.Clean(resultJson);
            try
            {
                var token = JToken.Parse(resultJson);
                if (token is not JObject jo)
                    return string.Empty;

                var decision = jo.Value<string>("decision") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(decision))
                    return string.Empty;

                var commandType = jo.Value<string>("commandType") ?? jo.Value<string>("command_type") ?? string.Empty;
                var operation = jo.Value<string>("operation") ?? jo.Value<string>("action") ?? string.Empty;
                var targetHint = jo.Value<string>("target_hint") ?? string.Empty;
                var reason = jo.Value<string>("reason") ?? string.Empty;
                var argsSummary = BuildArgsSummary(jo["args"]);

                var parts = new List<string> { "decision=" + decision };
                if (!string.IsNullOrWhiteSpace(commandType) || !string.IsNullOrWhiteSpace(operation))
                    parts.Add("command=" + SafeText(commandType) + "." + SafeText(operation));
                if (!string.IsNullOrWhiteSpace(argsSummary))
                    parts.Add("args=" + argsSummary);
                if (!string.IsNullOrWhiteSpace(targetHint))
                    parts.Add("target_hint=" + TruncateSingleLine(targetHint, 50));
                if (!string.IsNullOrWhiteSpace(reason))
                    parts.Add("reason=" + TruncateSingleLine(reason, 80));

                return string.Join("，", parts);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string TryExtractRuntimeCommandSummary(string resultJson)
        {
            if (string.IsNullOrWhiteSpace(resultJson))
                return string.Empty;

            try
            {
                var token = JToken.Parse(resultJson);
                if (token is not JObject jo)
                    return string.Empty;

                var ok = jo.Value<bool?>("ok");
                var commandType = jo.Value<string>("commandType") ?? string.Empty;
                var operation = jo.Value<string>("operation") ?? jo.Value<string>("action") ?? string.Empty;
                var message = jo.Value<string>("message") ?? string.Empty;
                var argsSummary = BuildRuntimeCommandArgSummary(jo);

                if (string.IsNullOrWhiteSpace(commandType))
                {
                    var innerType = jo.Value<string>("type") ?? jo.Value<string>("innerType") ?? string.Empty;
                    if (innerType.IndexOf("execute", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        jo["targetId"] != null)
                    {
                        commandType = "execute";
                    }
                }

                var parts = new List<string>();
                if (ok.HasValue)
                    parts.Add("ok=" + ok.Value.ToString().ToLowerInvariant());
                if (!string.IsNullOrWhiteSpace(commandType) || !string.IsNullOrWhiteSpace(operation))
                    parts.Add("command=" + SafeText(commandType) + "." + SafeText(operation));
                if (!string.IsNullOrWhiteSpace(argsSummary))
                    parts.Add("args=" + argsSummary);
                if (!string.IsNullOrWhiteSpace(message))
                    parts.Add("message=" + TruncateSingleLine(message, 80));

                return string.Join("，", parts);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string BuildArgsSummary(JToken argsToken)
        {
            if (argsToken is not JObject jo)
                return string.Empty;

            var preferredKeys = new[]
            {
                "targetId",
                "runtimeId",
                "commandLine",
                "text",
                "targetWindowTitleContains",
                "verifyTitleContains",
                "keys",
                "modifiers",
                "point_2d",
                "workingDirectory",
                "confirmed"
            };

            var parts = new List<string>();
            foreach (var key in preferredKeys)
            {
                var token = jo[key];
                if (token == null || token.Type == JTokenType.Null)
                    continue;

                var value = CompactToken(token);
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                parts.Add(key + "=" + TruncateSingleLine(value, 40));
            }

            return string.Join(", ", parts);
        }

        private static string BuildRuntimeCommandArgSummary(JObject jo)
        {
            var parts = new List<string>();
            AppendTokenSummary(parts, jo, "runtimeId", 28);
            AppendTokenSummary(parts, jo, "targetId", 20);
            AppendTokenSummary(parts, jo, "targetType", 20);
            AppendTokenSummary(parts, jo, "targetText", 32);
            AppendTokenSummary(parts, jo, "refId", 20);
            AppendTokenSummary(parts, jo, "commandLine", 50);
            AppendTokenSummary(parts, jo, "targetWindowTitleContains", 40);
            AppendTokenSummary(parts, jo, "targetWindowTitle", 40);

            var textLength = jo.Value<int?>("textLength");
            if (textLength.HasValue && textLength.Value > 0)
                parts.Add("textLength=" + textLength.Value);

            var processId = jo.Value<int?>("processId");
            if (processId.HasValue && processId.Value > 0)
                parts.Add("processId=" + processId.Value);

            return string.Join(", ", parts);
        }

        private static void AppendTokenSummary(ICollection<string> parts, JObject source, string key, int maxLength)
        {
            if (parts == null || source == null || string.IsNullOrWhiteSpace(key))
                return;

            var token = source[key];
            if (token == null || token.Type == JTokenType.Null)
                return;

            var value = CompactToken(token);
            if (string.IsNullOrWhiteSpace(value))
                return;

            parts.Add(key + "=" + TruncateSingleLine(value, maxLength));
        }

        private static string TryGetOutputValue(JObject outputs, params string[] keys)
        {
            if (outputs == null || keys == null)
                return string.Empty;

            foreach (var key in keys)
            {
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                var token = outputs[key];
                if (token == null || token.Type == JTokenType.Null)
                    continue;

                var value = NormalizeSingleLine(token.Type == JTokenType.String ? DifyOutputSanitizer.Clean(token.Value<string>()) : DifyOutputSanitizer.CleanToken(token));
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return string.Empty;
        }

        private static string TryGetEventValue(JObject data, params string[] keys)
        {
            if (data == null || keys == null)
                return string.Empty;

            foreach (var key in keys)
            {
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                var token = data[key];
                if (token == null || token.Type == JTokenType.Null)
                    continue;

                var value = NormalizeSingleLine(token.Type == JTokenType.String ? token.Value<string>() : token.ToString(Formatting.None));
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return string.Empty;
        }

        private static string TryGetNestedValue(JObject data, params string[] path)
        {
            if (data == null || path == null || path.Length == 0)
                return string.Empty;

            JToken current = data;
            foreach (var key in path)
            {
                if (current is not JObject jo || string.IsNullOrWhiteSpace(key))
                    return string.Empty;

                current = jo[key];
                if (current == null || current.Type == JTokenType.Null)
                    return string.Empty;
            }

            return NormalizeSingleLine(current.Type == JTokenType.String ? current.Value<string>() : current.ToString(Formatting.None));
        }

        private static string CompactToken(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
                return string.Empty;

            if (token.Type == JTokenType.Array)
            {
                var values = token.Values()
                    .Select(CompactToken)
                    .Where(x => !string.IsNullOrWhiteSpace(x));
                return string.Join(", ", values);
            }

            if (token.Type == JTokenType.Object)
                return NormalizeSingleLine(token.ToString(Formatting.None));

            return NormalizeSingleLine(token.ToString());
        }

        private static string FormatEventTime(UiCoarseVisionWorkflowStreamEvent evt)
        {
            var timestamp = evt?.LocalTimestamp;
            if (timestamp.HasValue && timestamp.Value != default)
                return timestamp.Value.ToString("HH:mm:ss");

            return DateTime.Now.ToString("HH:mm:ss");
        }

        private static string TruncateSingleLine(string value, int maxLength)
        {
            var normalized = NormalizeSingleLine(value);
            if (normalized.Length <= maxLength)
                return normalized;

            return normalized.Substring(0, Math.Max(0, maxLength - 3)) + "...";
        }

        private static string NormalizeSingleLine(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            return value
                .Replace("\r", " ")
                .Replace("\n", " / ")
                .Trim();
        }

        private static string SafeText(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "-" : value;
        }

        private sealed class WorkflowRoundTrace
        {
            public int RoundIndex { get; set; }
            public string DecisionTime { get; set; }
            public string DecisionSummary { get; set; }
            public string ExecuteTime { get; set; }
            public string ExecuteSummary { get; set; }
        }
    }
}

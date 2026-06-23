using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EW_Assistant.Diagnostics
{
    /// <summary>
    /// 记录 AUTO 发给 AI 的内容、AI 最终回复，以及该次流程是否实际调用了 ClearMachineAlarms。
    /// 对外日志只保留最终摘要；状态文件仅用于 WPF 与 McpServer 跨进程串联。
    /// </summary>
    public static class AutoClearAlarmAudit
    {
        private const string LogRoot = @"D:\Data\AiLog\AutoClearAlarm";
        private const string CsvRoot = @"D:\Data\AiLog\AutoClearAlarmCsv";
        private const string StateRoot = @"D:\DataAI";
        private const string CsvPendingDirName = "_pending";
        private const string StateSnapshotFileName = "auto_clear_alarm_state.json";
        private const string StateTraceDirName = "auto_clear_alarm_states";
        private const string StateRecentIndexFileName = "auto_clear_alarm_recent_index.json";
        private const string AuditMutexName = @"Global\EWAssistant_AutoClearAlarmAudit";
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
        private static readonly Encoding Utf8Bom = new UTF8Encoding(true);
        private static readonly string[] SummaryCsvHeaders =
        {
            "触发时间",
            "报警内容",
            "现象",
            "AI回答",
            "MCP调用",
            "人工对策"
        };
        private static readonly ConcurrentDictionary<string, byte> PendingCsvFlushPaths =
            new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> ReasoningFieldNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "think",
                "thinking",
                "thought",
                "thoughts",
                "reasoning",
                "reasoning_content",
                "reasoningContent",
                "chain_of_thought",
                "chainOfThought"
            };
        private static readonly TimeSpan ActiveTraceWindow = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan CompletedTraceAttachWindow = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan StateRetentionWindow = TimeSpan.FromDays(7);
        private static readonly TimeSpan MaintenanceInitialDelay = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromMinutes(30);
        private static readonly System.Threading.Timer MaintenanceTimer = CreateMaintenanceTimer();
        private const int MaxToolCallEntries = 200;
        private const int MaxNodeExecutionEntries = 160;
        private const int MaxRecentTraceIndexEntries = 128;
        private const string TraceStatusPending = "pending";
        private const string TraceStatusAccepted = "accepted";
        private const string TraceStatusRunning = "running";
        private const string TraceStatusRejected = "rejected";
        private const string TraceStatusDispatchFailed = "dispatch_failed";
        private const string TraceStatusSucceeded = "succeeded";
        private const string TraceStatusFailed = "failed";

        public static string CreateTraceId()
        {
            return $"auto-{DateTime.Now:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}".Substring(0, 37);
        }

        public static void StartAutoTrace(string traceId, string errorCode, string prompt, string machineCode)
        {
            _ = machineCode;

            if (string.IsNullOrWhiteSpace(traceId))
            {
                return;
            }

            ExecuteLocked(() =>
            {
                var now = DateTime.Now;
                WriteStateUnsafe(new AutoClearAlarmState
                {
                    TraceId = traceId,
                    InputMessage = BuildInputText(errorCode, prompt),
                    StartedAt = now,
                    LastUpdatedAt = now,
                    Status = TraceStatusPending,
                    ClearMachineAlarmsCalled = false,
                    FinalReply = string.Empty
                });
                CleanupStateFilesUnsafe(now);
            });
        }

        public static void MarkAutoAccepted(string traceId)
        {
            TouchTrace(traceId);
        }

        public static void MarkAutoRejected(string traceId, string reason)
        {
            FinalizeTrace(traceId, FormatRejectedReason(reason), TraceStatusRejected);
        }

        public static void MarkWorkflowStarted(string traceId, string workflowRunId, string taskId)
        {
            UpdateTrace(traceId, (state, now) =>
            {
                state.WorkflowRunId = TrimPlainText(workflowRunId, 128);
                state.TaskId = TrimPlainText(taskId, 128);
                state.Status = TraceStatusRunning;
                state.LastUpdatedAt = now;
            });
        }

        public static void MarkWorkflowDispatchFailed(string traceId, string message)
        {
            FinalizeTrace(traceId, message, TraceStatusDispatchFailed);
        }

        public static void MarkAutoVisionImage(string traceId, string sourceImagePath, string processedImagePath)
        {
            if (string.IsNullOrWhiteSpace(traceId))
            {
                return;
            }

            UpdateTrace(traceId, (state, now) =>
            {
                state.IsVisionRelated = true;
                state.AutoVisionSourceImagePath = TrimPlainText(sourceImagePath, 1000);
                state.AutoVisionProcessedImagePath = TrimPlainText(processedImagePath, 1000);
                state.LastUpdatedAt = now;
            });
        }

        public static void RecordWorkflowNodeStarted(string traceId, JObject nodeData)
        {
            if (string.IsNullOrWhiteSpace(traceId) || nodeData == null)
            {
                return;
            }

            UpdateTrace(traceId, (state, now) =>
            {
                UpsertNodeExecutionUnsafe(state, nodeData, now, finished: false, elapsedSeconds: null);
                if (string.IsNullOrWhiteSpace(state.Status) || string.Equals(state.Status, TraceStatusAccepted, StringComparison.OrdinalIgnoreCase))
                {
                    state.Status = TraceStatusRunning;
                }

                state.LastUpdatedAt = now;
            });
        }

        public static void RecordWorkflowNodeFinished(string traceId, JObject nodeData, double? elapsedSeconds)
        {
            if (string.IsNullOrWhiteSpace(traceId) || nodeData == null)
            {
                return;
            }

            UpdateTrace(traceId, (state, now) =>
            {
                UpsertNodeExecutionUnsafe(state, nodeData, now, finished: true, elapsedSeconds: elapsedSeconds);
                if (string.IsNullOrWhiteSpace(state.Status) || string.Equals(state.Status, TraceStatusAccepted, StringComparison.OrdinalIgnoreCase))
                {
                    state.Status = TraceStatusRunning;
                }

                state.LastUpdatedAt = now;
            });
        }

        public static void MarkClearMachineAlarmsDetectedInWorkflow(string traceId, string source, string detail)
        {
            _ = traceId;
            _ = source;
            _ = detail;
            // 审计只记录实际工具调用，不记录 workflow 节点猜测结果。
        }

        public static void RecordClearMachineAlarmsToolCall(bool shadowMode, bool executed, string result, string error = null, string source = null)
        {
            _ = shadowMode;
            _ = executed;
            _ = result;
            _ = error;
            _ = source;

            ExecuteLocked(() =>
            {
                var now = DateTime.Now;
                var state = FindAttachableTraceUnsafe(now);
                if (!CanAttachToActiveTrace(state, now))
                {
                    return;
                }

                state.ClearMachineAlarmsCalled = true;
                state.ClearMachineAlarmsCalledAt = now;
                state.LastUpdatedAt = now;
                WriteStateUnsafe(state);
            });
        }

        public static void RecordMcpToolCall(string toolName, object args, string result, string error = null, string source = null)
        {
            if (string.IsNullOrWhiteSpace(toolName))
            {
                return;
            }

            ExecuteLocked(() =>
            {
                var now = DateTime.Now;
                var state = FindAttachableTraceUnsafe(now);
                if (!CanAttachToActiveTrace(state, now))
                {
                    return;
                }

                var added = AddToolCallUnsafe(
                    state,
                    new AutoMcpToolCallEntry
                    {
                        Timestamp = now,
                        ToolName = TrimPlainText(toolName, 128),
                        Source = TrimPlainText(source, 128),
                        Args = TrimMultilineText(SafeSerialize(args), 4000),
                        Result = TrimMultilineText(result, 4000),
                        Error = TrimMultilineText(error, 4000)
                    });

                state.LastUpdatedAt = now;
                WriteStateUnsafe(state);

                if (added && state.FinalizedAt != default)
                {
                    AppendLateToolCallLogUnsafe(state.TraceId, state.FinalizedAt, state.FinalReply, state.McpToolCalls.LastOrDefault());
                    TryUpsertSummaryCsvUnsafe(state);
                }
            });
        }

        public static void MarkWorkflowFinished(string traceId, bool succeeded, string finalText)
        {
            FinalizeTrace(traceId, finalText, succeeded ? TraceStatusSucceeded : TraceStatusFailed);
        }

        public static bool SaveManualReview(string traceId, string phenomenon, string manualCountermeasure)
        {
            if (string.IsNullOrWhiteSpace(traceId))
            {
                return false;
            }

            var saved = false;
            UpdateTrace(traceId, (state, now) =>
            {
                state.Phenomenon = TrimMultilineText(phenomenon, 2000);
                state.ManualCountermeasure = TrimMultilineText(manualCountermeasure, 2000);
                state.ManualReviewedAt = now;
                state.LastUpdatedAt = now;
                TryUpsertSummaryCsvUnsafe(state);
                saved = true;
            });
            return saved;
        }

        public static IReadOnlyList<string> ReadRecentTraceIds(int maxCount = 40)
        {
            var takeCount = Math.Max(1, maxCount);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    return ReadRecentTraceIndexEntriesUnsafe()
                        .Select(entry => (entry?.TraceId ?? string.Empty).Trim())
                        .Where(traceId => !string.IsNullOrWhiteSpace(traceId))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(takeCount)
                        .ToList();
                }
                catch
                {
                    if (attempt >= 1)
                    {
                        break;
                    }

                    Thread.Sleep(40);
                }
            }

            return Array.Empty<string>();
        }

        public static IReadOnlyList<SummaryCsvRecord> ReadSummaryCsvRecords(int maxCount = 200)
        {
            var records = new List<SummaryCsvRecord>();
            ExecuteLocked(() =>
            {
                if (!Directory.Exists(CsvRoot))
                {
                    return;
                }

                foreach (var path in Directory.GetFiles(CsvRoot, "*.csv", SearchOption.TopDirectoryOnly)
                             .OrderByDescending(File.GetLastWriteTime))
                {
                    records.AddRange(ReadSummaryCsvRecordsFromPathUnsafe(path, ParseSummaryCsvDate(path)));
                }
            });

            return OrderSummaryCsvRecords(records, maxCount);
        }

        public static IReadOnlyList<SummaryCsvRecord> ReadSummaryCsvRecordsByDate(DateTime date, int maxCount = 200)
        {
            var day = date == default ? DateTime.Today : date.Date;
            var path = GetSummaryCsvPath(day);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return new List<SummaryCsvRecord>();
            }

            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    return OrderSummaryCsvRecords(ReadSummaryCsvRecordsFromPathUnsafe(path, day), maxCount);
                }
                catch
                {
                    if (attempt >= 1)
                    {
                        break;
                    }

                    Thread.Sleep(40);
                }
            }

            return new List<SummaryCsvRecord>();
        }

        public static bool SaveSummaryCsvRecord(
            string csvPath,
            int rowIndex,
            string triggerTimeText,
            string alarmContent,
            string phenomenon,
            string manualCountermeasure)
        {
            if (string.IsNullOrWhiteSpace(csvPath))
            {
                return false;
            }

            var saved = false;
            ExecuteLocked(() =>
            {
                var rows = ReadCsvRowsUnsafe(csvPath);
                EnsureSummaryCsvHeader(rows);

                var targetRowIndex = ResolveSummaryCsvRowIndex(rows, rowIndex, triggerTimeText, alarmContent);
                if (targetRowIndex < 0)
                {
                    return;
                }

                var row = NormalizePendingCsvRow(rows[targetRowIndex]);
                ApplySummaryCsvManualReview(row, phenomenon, manualCountermeasure);
                rows[targetRowIndex] = row;
                WriteCsvRowsUnsafe(csvPath, rows);
                saved = true;
            });

            return saved;
        }

        private static IReadOnlyList<SummaryCsvRecord> OrderSummaryCsvRecords(IEnumerable<SummaryCsvRecord> records, int maxCount)
        {
            return (records ?? Enumerable.Empty<SummaryCsvRecord>())
                .Where(record => record != null)
                .OrderByDescending(record => record.TriggeredAt ?? record.TriggerDate)
                .Take(Math.Max(1, maxCount))
                .ToList();
        }

        private static List<SummaryCsvRecord> ReadSummaryCsvRecordsFromPathUnsafe(string path, DateTime csvDate)
        {
            var rows = ReadCsvRowsUnsafe(path);
            EnsureSummaryCsvHeader(rows);
            return BuildSummaryCsvRecords(path, csvDate, rows);
        }

        private static List<SummaryCsvRecord> BuildSummaryCsvRecords(string path, DateTime csvDate, IReadOnlyList<List<string>> rows)
        {
            var records = new List<SummaryCsvRecord>();
            if (rows == null)
            {
                return records;
            }

            for (var i = 1; i < rows.Count; i++)
            {
                var record = CreateSummaryCsvRecord(path, i, csvDate, rows[i]);
                if (record != null)
                {
                    records.Add(record);
                }
            }

            return records;
        }

        private static SummaryCsvRecord CreateSummaryCsvRecord(string path, int rowIndex, DateTime csvDate, IReadOnlyList<string> row)
        {
            var normalizedRow = NormalizePendingCsvRow(row);
            if (normalizedRow.Count < SummaryCsvHeaders.Length)
            {
                return null;
            }

            var triggerTimeText = GetCsvCell(normalizedRow, 0);
            var alarmContent = GetCsvCell(normalizedRow, 1);
            if (string.IsNullOrWhiteSpace(triggerTimeText) && string.IsNullOrWhiteSpace(alarmContent))
            {
                return null;
            }

            return new SummaryCsvRecord
            {
                CsvPath = path,
                RowIndex = rowIndex,
                TriggerDate = csvDate,
                TriggerTimeText = triggerTimeText,
                TriggeredAt = CombineSummaryCsvDateTime(csvDate, triggerTimeText),
                AlarmContent = alarmContent,
                Phenomenon = GetCsvCell(normalizedRow, 2),
                AiReply = GetCsvCell(normalizedRow, 3),
                McpCall = GetCsvCell(normalizedRow, 4),
                ManualCountermeasure = GetCsvCell(normalizedRow, 5)
            };
        }

        private static int ResolveSummaryCsvRowIndex(
            IReadOnlyList<List<string>> rows,
            int rowIndex,
            string triggerTimeText,
            string alarmContent)
        {
            if (rowIndex > 0 && rowIndex < (rows?.Count ?? 0)
                && IsSummaryCsvRowMatch(rows[rowIndex], triggerTimeText, alarmContent))
            {
                return rowIndex;
            }

            return FindSummaryCsvRowIndex(rows, BuildSummaryCsvLookupRow(triggerTimeText, alarmContent));
        }

        private static List<string> BuildSummaryCsvLookupRow(string triggerTimeText, string alarmContent)
        {
            return new List<string>
            {
                NormalizeCsvCell(triggerTimeText),
                NormalizeCsvCell(alarmContent),
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty
            };
        }

        private static bool IsSummaryCsvRowMatch(IReadOnlyList<string> row, string triggerTimeText, string alarmContent)
        {
            var normalizedRow = NormalizePendingCsvRow(row);
            return string.Equals(GetCsvCell(normalizedRow, 0), NormalizeCsvCell(triggerTimeText), StringComparison.Ordinal)
                   && string.Equals(GetCsvCell(normalizedRow, 1), NormalizeCsvCell(alarmContent), StringComparison.Ordinal);
        }

        private static void ApplySummaryCsvManualReview(List<string> row, string phenomenon, string manualCountermeasure)
        {
            if (row == null)
            {
                return;
            }

            row[2] = NormalizeCsvCell(phenomenon);
            row[5] = NormalizeCsvCell(manualCountermeasure);
        }

        private static void TouchTrace(string traceId)
        {
            UpdateTrace(traceId, (state, now) =>
            {
                if (string.IsNullOrWhiteSpace(state.Status) || string.Equals(state.Status, TraceStatusPending, StringComparison.OrdinalIgnoreCase))
                {
                    state.Status = TraceStatusAccepted;
                }

                state.LastUpdatedAt = now;
            });
        }

        private static void FinalizeTrace(string traceId, string finalReply, string finalStatus)
        {
            if (string.IsNullOrWhiteSpace(traceId))
            {
                return;
            }

            ExecuteLocked(() =>
            {
                var state = ReadTraceStateUnsafe(traceId);
                if (!IsSameTrace(state, traceId))
                {
                    return;
                }

                if (finalReply != null)
                {
                    state.FinalReply = TrimMultilineText(finalReply, 4000);
                }

                state.FinalizedAt = DateTime.Now;
                state.LastUpdatedAt = state.FinalizedAt;
                state.Status = string.IsNullOrWhiteSpace(finalStatus) ? state.Status : finalStatus;
                WriteStateUnsafe(state);
                AppendSummaryLogUnsafe(state);
            });
        }

        private static string FormatRejectedReason(string reason)
        {
            var normalized = (reason ?? string.Empty).Trim();
            if (string.Equals(normalized, "busy_running", StringComparison.OrdinalIgnoreCase))
            {
                return "本次 AUTO 请求未受理：当前已有上一条任务正在执行，本次触发已丢弃。";
            }

            if (string.Equals(normalized, "invalid_config", StringComparison.OrdinalIgnoreCase))
            {
                return "本次 AUTO 请求未受理：AutoURL / AutoKey 尚未配置。";
            }

            if (string.Equals(normalized, "busy_or_invalid_config", StringComparison.OrdinalIgnoreCase))
            {
                return "本次 AUTO 请求未受理：当前已有任务执行中，或 AutoURL / AutoKey 尚未配置。";
            }

            return string.IsNullOrWhiteSpace(normalized)
                ? "本次 AUTO 请求未受理。"
                : $"本次 AUTO 请求未受理：{normalized}";
        }

        private static void UpdateTrace(string traceId, Action<AutoClearAlarmState, DateTime> update)
        {
            if (string.IsNullOrWhiteSpace(traceId) || update == null)
            {
                return;
            }

            ExecuteLocked(() =>
            {
                var state = ReadTraceStateUnsafe(traceId);
                if (!IsSameTrace(state, traceId))
                {
                    return;
                }

                var now = DateTime.Now;
                update(state, now);
                WriteStateUnsafe(state);
            });
        }

        private static void ExecuteLocked(Action action)
        {
            try
            {
                using var mutex = new Mutex(false, AuditMutexName);
                var hasHandle = false;
                try
                {
                    hasHandle = mutex.WaitOne(TimeSpan.FromSeconds(5));
                }
                catch (AbandonedMutexException)
                {
                    hasHandle = true;
                }

                if (!hasHandle)
                {
                    return;
                }

                try
                {
                    action();
                }
                finally
                {
                    try { mutex.ReleaseMutex(); } catch { }
                }
            }
            catch
            {
                // 审计写入失败忽略，避免阻断主流程
            }
        }

        private static System.Threading.Timer CreateMaintenanceTimer()
        {
            return new System.Threading.Timer(_ => RunPeriodicMaintenance(), null, MaintenanceInitialDelay, MaintenanceInterval);
        }

        private static void RunPeriodicMaintenance()
        {
            try
            {
                ExecuteLocked(() => CleanupStateFilesUnsafe(DateTime.Now));
            }
            catch
            {
                // 周期性维护失败忽略，避免影响主流程。
            }
        }

        private static AutoClearAlarmState ReadTraceStateUnsafe(string traceId)
        {
            if (string.IsNullOrWhiteSpace(traceId))
            {
                return new AutoClearAlarmState();
            }

            return ReadStateFileUnsafe(GetTraceStatePath(traceId));
        }

        private static AutoClearAlarmState ReadStateFileUnsafe(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    return new AutoClearAlarmState();
                }

                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs, Utf8NoBom, true);
                var json = reader.ReadToEnd();
                if (string.IsNullOrWhiteSpace(json))
                {
                    return new AutoClearAlarmState();
                }

                return JsonConvert.DeserializeObject<AutoClearAlarmState>(json) ?? new AutoClearAlarmState();
            }
            catch
            {
                return new AutoClearAlarmState();
            }
        }

        private static void WriteStateUnsafe(AutoClearAlarmState state)
        {
            if (state == null || string.IsNullOrWhiteSpace(state.TraceId))
            {
                return;
            }

            EnsureDir(GetTraceStateRoot());
            var json = JsonConvert.SerializeObject(state, Formatting.Indented);
            WriteAllTextAtomic(GetTraceStatePath(state.TraceId), json, Utf8NoBom);

            try
            {
                WriteAllTextAtomic(GetStateSnapshotPath(), json, Utf8NoBom);
            }
            catch
            {
                // 最新快照写入失败忽略，不影响主流程
            }

            try
            {
                UpsertRecentTraceIndexUnsafe(state);
            }
            catch
            {
                // 最近 trace 索引写入失败忽略，不影响主流程
            }
        }

        private static void AppendSummaryLogUnsafe(AutoClearAlarmState state)
        {
            EnsureDir(LogRoot);
            var path = Path.Combine(LogRoot, $"{DateTime.Now:yyyy-MM-dd}.log");
            var sb = new StringBuilder();
            sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}]");
            sb.AppendLine($"追踪编号：{FormatLogValue(state?.TraceId)}");
            sb.AppendLine();
            AppendTriggerSection(sb, state);
            sb.AppendLine();
            AppendToolCalls(sb, state?.McpToolCalls);
            sb.AppendLine();
            AppendFinalReplySection(sb, state?.FinalReply);
            sb.AppendLine(new string('-', 72));
            File.AppendAllText(path, sb.ToString(), Utf8NoBom);
            TryUpsertSummaryCsvUnsafe(state);
            LogRetentionPolicy.TryCleanupFiles(LogRoot, "*.log", SearchOption.TopDirectoryOnly, TimeSpan.FromDays(30));
            LogRetentionPolicy.TryCleanupFiles(CsvRoot, "*.csv", SearchOption.TopDirectoryOnly, TimeSpan.FromDays(30));
        }

        private static void AppendLateToolCallLogUnsafe(string traceId, DateTime finalizedAt, string finalReply, AutoMcpToolCallEntry call)
        {
            if (call == null)
            {
                return;
            }

            EnsureDir(LogRoot);
            var path = Path.Combine(LogRoot, $"{DateTime.Now:yyyy-MM-dd}.log");
            var sb = new StringBuilder();
            sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}]");
            sb.AppendLine($"TraceId: {FormatLogValue(traceId)}");
            sb.AppendLine($"补记原因: MCP工具日志晚于 workflow_finished（{finalizedAt:yyyy-MM-dd HH:mm:ss.fff}）");
            AppendFinalReplySection(sb, finalReply);
            sb.AppendLine();
            sb.AppendLine("2. 调用的McpTool（补记）");
            AppendReadableToolCall(sb, call, 1);
            sb.AppendLine(new string('-', 72));
            File.AppendAllText(path, sb.ToString(), Utf8NoBom);
            LogRetentionPolicy.TryCleanupFiles(LogRoot, "*.log", SearchOption.TopDirectoryOnly, TimeSpan.FromDays(30));
        }

        private static void TryUpsertSummaryCsvUnsafe(AutoClearAlarmState state)
        {
            try
            {
                UpsertSummaryCsvUnsafe(state);
            }
            catch
            {
                // CSV 写入失败忽略，不影响主流程与原有日志
            }
        }

        private static void UpsertSummaryCsvUnsafe(AutoClearAlarmState state)
        {
            if (state == null || string.IsNullOrWhiteSpace(state.TraceId))
            {
                return;
            }

            if (!string.Equals(state.Status, TraceStatusSucceeded, StringComparison.OrdinalIgnoreCase))
            {
                DeleteSummaryCsvRowUnsafe(state);
                DeletePendingSummaryCsvUnsafe(state.TraceId);
                return;
            }

            EnsureDir(CsvRoot);
            var path = GetSummaryCsvPath(state);
            var newRow = BuildSummaryCsvRow(state);
            var fingerprint = BuildSummaryCsvFingerprint(path, newRow);

            TryFlushPendingSummaryCsvUnsafe(path);

            if (state.CsvSyncedAt != default
                && string.Equals(state.CsvFingerprint ?? string.Empty, fingerprint, StringComparison.Ordinal))
            {
                DeletePendingSummaryCsvUnsafe(state.TraceId);
                return;
            }

            if (TryUpsertSummaryCsvRowAtomicUnsafe(path, newRow))
            {
                MarkSummaryCsvSyncedUnsafe(state, fingerprint);
                TryFlushPendingSummaryCsvUnsafe(path);
                return;
            }

            QueuePendingSummaryCsvUnsafe(state.TraceId, path, fingerprint, newRow);
            TryFlushPendingSummaryCsvUnsafe(path);
        }

        private static string BuildSummaryCsvFingerprint(string path, IReadOnlyList<string> row)
        {
            var csvPath = path ?? string.Empty;
            var joined = row == null
                ? string.Empty
                : string.Join("\u001f", row.Select(value => NormalizeCsvCell(value)));
            return csvPath + "\u001e" + joined;
        }

        private static void TryFlushPendingSummaryCsvUnsafe(string path)
        {
            try
            {
                FlushPendingSummaryCsvUnsafe(path);
            }
            catch
            {
                // 待补写合并失败忽略，后续有新的 AUTO 记录时再继续尝试。
            }
        }

        private static string GetSummaryCsvPath(AutoClearAlarmState state)
        {
            var day = state?.FinalizedAt != default
                ? state.FinalizedAt.Date
                : (state?.StartedAt != default ? state.StartedAt.Date : DateTime.Now.Date);

            return GetSummaryCsvPath(day);
        }

        private static string GetSummaryCsvPath(DateTime day)
        {
            var normalized = day == default ? DateTime.Today : day.Date;
            return Path.Combine(CsvRoot, $"{normalized:yyyy-MM-dd}.csv");
        }

        private static void DeleteSummaryCsvRowUnsafe(AutoClearAlarmState state)
        {
            var path = GetSummaryCsvPath(state);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return;
            }

            var rows = ReadCsvRowsUnsafe(path);
            EnsureSummaryCsvHeader(rows);
            var rowIndex = FindSummaryCsvRowIndex(rows, BuildSummaryCsvRow(state));
            if (rowIndex < 0)
            {
                return;
            }

            rows.RemoveAt(rowIndex);
            WriteCsvRowsUnsafe(path, rows);
        }

        private static void EnsureSummaryCsvHeader(List<List<string>> rows)
        {
            if (rows == null)
            {
                return;
            }

            if (rows.Count == 0)
            {
                rows.Add(SummaryCsvHeaders.ToList());
                return;
            }

            if (!IsSummaryCsvHeader(rows[0]))
            {
                rows[0] = SummaryCsvHeaders.ToList();
            }
        }

        private static bool IsSummaryCsvHeader(IReadOnlyList<string> row)
        {
            return IsCsvHeader(row, SummaryCsvHeaders);
        }

        private static bool IsCsvHeader(IReadOnlyList<string> row, IReadOnlyList<string> headers)
        {
            if (row == null || headers == null || row.Count < headers.Count)
            {
                return false;
            }

            for (var i = 0; i < headers.Count; i++)
            {
                if (!string.Equals(row[i], headers[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static List<string> BuildSummaryCsvRow(AutoClearAlarmState state)
        {
            var alarmContent = ExtractAlarmContentForCsv(state?.InputMessage);
            return new List<string>
            {
                state?.StartedAt == default ? string.Empty : state.StartedAt.ToString("HH:mm:ss"),
                NormalizeCsvCell(FormatLogValue(alarmContent)),
                NormalizeCsvCell(state?.Phenomenon),
                NormalizeCsvCell(FormatLogValue(state?.FinalReply)),
                NormalizeCsvCell(BuildSummaryCsvToolCalls(state?.McpToolCalls)),
                NormalizeCsvCell(state?.ManualCountermeasure)
            };
        }

        private static string BuildSummaryCsvToolCalls(IReadOnlyList<AutoMcpToolCallEntry> calls)
        {
            if (calls == null || calls.Count == 0)
            {
                return "无";
            }

            var entries = new List<string>();
            var index = 1;
            foreach (var call in calls)
            {
                if (call == null)
                {
                    continue;
                }

                entries.Add(FormatReadableToolCall(call, index++));
            }

            return entries.Count == 0 ? "无" : string.Join(Environment.NewLine, entries);
        }

        private static int FindSummaryCsvRowIndex(IReadOnlyList<List<string>> rows, IReadOnlyList<string> newRow)
        {
            if (rows == null || newRow == null || newRow.Count < 2)
            {
                return -1;
            }

            var triggerTime = NormalizeCsvKey(newRow[0]);
            var alarmContent = NormalizeCsvKey(ExtractAlarmContentForCsv(newRow[1]));
            for (var i = 1; i < rows.Count; i++)
            {
                var row = rows[i];
                if (NormalizeCsvKey(GetCsvCell(row, 0)) == triggerTime
                    && NormalizeCsvKey(ExtractAlarmContentForCsv(GetCsvCell(row, 1))) == alarmContent)
                {
                    return i;
                }
            }

            return -1;
        }

        private static string GetCsvCell(IReadOnlyList<string> row, int index)
        {
            return row != null && index >= 0 && row.Count > index
                ? row[index] ?? string.Empty
                : string.Empty;
        }

        private static string GetLastCsvCell(IReadOnlyList<string> row)
        {
            return row == null || row.Count == 0
                ? string.Empty
                : GetCsvCell(row, row.Count - 1);
        }

        private static string NormalizeCsvCell(string value)
        {
            return (value ?? string.Empty)
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Trim();
        }

        private static string NormalizeCsvKey(string value)
        {
            return NormalizeCsvCell(value);
        }

        private static DateTime ParseSummaryCsvDate(string path)
        {
            try
            {
                var fileName = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
                return DateTime.TryParseExact(fileName, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var day)
                    ? day.Date
                    : File.GetLastWriteTime(path).Date;
            }
            catch
            {
                return DateTime.Now.Date;
            }
        }

        private static DateTime? CombineSummaryCsvDateTime(DateTime day, string triggerTimeText)
        {
            if (day == default)
            {
                return null;
            }

            if (TimeSpan.TryParse(triggerTimeText, out var time))
            {
                return day.Date + time;
            }

            return day.Date;
        }

        private static List<List<string>> ReadCsvRowsUnsafe(string path)
        {
            var rows = new List<List<string>>();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return rows;
            }

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs, Encoding.UTF8, true);
            var text = reader.ReadToEnd();
            if (string.IsNullOrEmpty(text))
            {
                return rows;
            }

            var row = new List<string>();
            var cell = new StringBuilder();
            var inQuotes = false;

            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                if (inQuotes)
                {
                    if (ch == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            cell.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        cell.Append(ch);
                    }

                    continue;
                }

                switch (ch)
                {
                    case '"':
                        inQuotes = true;
                        break;
                    case ',':
                        row.Add(cell.ToString());
                        cell.Clear();
                        break;
                    case '\r':
                        row.Add(cell.ToString());
                        cell.Clear();
                        rows.Add(row);
                        row = new List<string>();
                        if (i + 1 < text.Length && text[i + 1] == '\n')
                        {
                            i++;
                        }
                        break;
                    case '\n':
                        row.Add(cell.ToString());
                        cell.Clear();
                        rows.Add(row);
                        row = new List<string>();
                        break;
                    default:
                        cell.Append(ch);
                        break;
                }
            }

            if (cell.Length > 0 || row.Count > 0)
            {
                row.Add(cell.ToString());
                rows.Add(row);
            }

            if (rows.Count > 0 && rows[0].Count > 0)
            {
                rows[0][0] = rows[0][0].TrimStart('\ufeff');
            }

            return rows
                .Where(currentRow => currentRow != null && currentRow.Any(cellValue => !string.IsNullOrEmpty(cellValue)))
                .ToList();
        }

        private static void WriteCsvRowsUnsafe(string path, IEnumerable<IReadOnlyList<string>> rows)
        {
            var sb = new StringBuilder();
            foreach (var row in rows ?? Array.Empty<IReadOnlyList<string>>())
            {
                if (row == null)
                {
                    continue;
                }

                for (var i = 0; i < row.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(',');
                    }

                    sb.Append(EscapeCsvCell(row[i]));
                }

                sb.AppendLine();
            }

            WriteAllTextAtomic(path, sb.ToString(), Utf8Bom);
        }

        private static bool TryUpsertSummaryCsvRowAtomicUnsafe(string path, IReadOnlyList<string> row)
        {
            try
            {
                var rows = ReadCsvRowsUnsafe(path);
                EnsureSummaryCsvHeader(rows);

                var newRow = NormalizePendingCsvRow(row);
                var existingIndex = FindSummaryCsvRowIndex(rows, newRow);
                if (existingIndex >= 0)
                {
                    if (string.IsNullOrWhiteSpace(GetCsvCell(newRow, 2)))
                    {
                        newRow[2] = GetCsvCell(rows[existingIndex], 2);
                    }

                    if (string.IsNullOrWhiteSpace(GetCsvCell(newRow, 5)))
                    {
                        newRow[5] = GetLastCsvCell(rows[existingIndex]);
                    }

                    rows[existingIndex] = newRow;
                }
                else
                {
                    rows.Add(newRow);
                }

                WriteCsvRowsUnsafe(path, rows);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void FlushPendingSummaryCsvUnsafe(string path)
        {
            var pendingEntries = ReadPendingSummaryEntriesUnsafe(path);
            if (pendingEntries.Count == 0)
            {
                return;
            }

            var rows = ReadCsvRowsUnsafe(path);
            EnsureSummaryCsvHeader(rows);

            foreach (var entry in pendingEntries.OrderBy(item => item.QueuedAt).ThenBy(item => item.TraceId, StringComparer.OrdinalIgnoreCase))
            {
                var newRow = NormalizePendingCsvRow(entry?.Row);
                if (newRow.Count == 0)
                {
                    continue;
                }

                var existingIndex = FindSummaryCsvRowIndex(rows, newRow);
                if (existingIndex >= 0)
                {
                    if (string.IsNullOrWhiteSpace(GetCsvCell(newRow, 2)))
                    {
                        newRow[2] = GetCsvCell(rows[existingIndex], 2);
                    }

                    if (string.IsNullOrWhiteSpace(GetCsvCell(newRow, 5)))
                    {
                        newRow[5] = GetLastCsvCell(rows[existingIndex]);
                    }

                    rows[existingIndex] = newRow;
                }
                else
                {
                    rows.Add(newRow);
                }
            }

            WriteCsvRowsUnsafe(path, rows);

            foreach (var entry in pendingEntries)
            {
                MarkSummaryCsvSyncedByPendingEntryUnsafe(entry);
                DeletePendingSummaryCsvUnsafe(entry?.TraceId);
            }
        }

        private static List<string> NormalizePendingCsvRow(IReadOnlyList<string> row)
        {
            var normalized = new List<string>();
            if (row != null)
            {
                foreach (var value in row)
                {
                    normalized.Add(NormalizeCsvCell(value));
                }
            }

            while (normalized.Count < SummaryCsvHeaders.Length)
            {
                normalized.Add(string.Empty);
            }

            return normalized;
        }

        private static List<SummaryCsvPendingEntry> ReadPendingSummaryEntriesUnsafe(string path)
        {
            var root = GetSummaryCsvPendingRoot();
            if (!Directory.Exists(root))
            {
                return new List<SummaryCsvPendingEntry>();
            }

            var normalizedPath = path ?? string.Empty;
            var list = new List<SummaryCsvPendingEntry>();
            foreach (var pendingPath in Directory.GetFiles(root, "*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var json = File.ReadAllText(pendingPath, Utf8NoBom);
                    var entry = JsonConvert.DeserializeObject<SummaryCsvPendingEntry>(json);
                    if (entry == null)
                    {
                        continue;
                    }

                    if (!string.Equals(entry.CsvPath ?? string.Empty, normalizedPath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    list.Add(entry);
                }
                catch
                {
                    // 单个待补写条目损坏时忽略，避免阻断后续有效条目。
                }
            }

            return list;
        }

        private static void QueuePendingSummaryCsvUnsafe(string traceId, string path, string fingerprint, IReadOnlyList<string> row)
        {
            if (string.IsNullOrWhiteSpace(traceId))
            {
                return;
            }

            EnsureDir(GetSummaryCsvPendingRoot());
            var entry = new SummaryCsvPendingEntry
            {
                TraceId = traceId,
                CsvPath = path,
                Fingerprint = fingerprint,
                QueuedAt = DateTime.Now,
                Row = NormalizePendingCsvRow(row)
            };

            var json = JsonConvert.SerializeObject(entry, Formatting.Indented);
            WriteAllTextAtomic(GetPendingSummaryCsvPath(traceId), json, Utf8NoBom);
            SchedulePendingSummaryCsvFlush(path);
        }

        private static void WriteAllTextAtomic(string path, string content, Encoding encoding)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                EnsureDir(dir);
            }

            var tempPath = path + ".tmp." + Guid.NewGuid().ToString("N");
            File.WriteAllText(tempPath, content ?? string.Empty, encoding ?? Utf8NoBom);

            try
            {
                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(tempPath, path, null, true);
                        tempPath = null;
                        return;
                    }
                    catch
                    {
                        File.Copy(tempPath, path, true);
                    }
                }
                else
                {
                    File.Move(tempPath, path);
                    tempPath = null;
                    return;
                }

                File.Delete(tempPath);
                tempPath = null;
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(tempPath))
                {
                    try
                    {
                        if (File.Exists(tempPath))
                        {
                            File.Delete(tempPath);
                        }
                    }
                    catch
                    {
                        // 清理临时文件失败忽略。
                    }
                }
            }
        }

        private static void MarkSummaryCsvSyncedUnsafe(AutoClearAlarmState state, string fingerprint)
        {
            if (state == null || string.IsNullOrWhiteSpace(state.TraceId))
            {
                return;
            }

            state.CsvSyncedAt = DateTime.Now;
            state.CsvFingerprint = fingerprint ?? string.Empty;
            WriteStateUnsafe(state);
            DeletePendingSummaryCsvUnsafe(state.TraceId);
        }

        private static void MarkSummaryCsvSyncedByPendingEntryUnsafe(SummaryCsvPendingEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.TraceId))
            {
                return;
            }

            var state = ReadTraceStateUnsafe(entry.TraceId);
            if (!IsSameTrace(state, entry.TraceId))
            {
                return;
            }

            state.CsvSyncedAt = DateTime.Now;
            state.CsvFingerprint = entry.Fingerprint ?? string.Empty;
            WriteStateUnsafe(state);
        }

        private static void DeletePendingSummaryCsvUnsafe(string traceId)
        {
            if (string.IsNullOrWhiteSpace(traceId))
            {
                return;
            }

            try
            {
                var path = GetPendingSummaryCsvPath(traceId);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // 删除失败忽略，下次再尝试。
            }
        }

        private static string GetSummaryCsvPendingRoot()
        {
            return Path.Combine(CsvRoot, CsvPendingDirName);
        }

        private static string GetPendingSummaryCsvPath(string traceId)
        {
            return Path.Combine(GetSummaryCsvPendingRoot(), traceId + ".json");
        }

        private static void SchedulePendingSummaryCsvFlush(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            if (!PendingCsvFlushPaths.TryAdd(path, 0))
            {
                return;
            }

            Task.Run(async () =>
            {
                try
                {
                    for (var attempt = 0; attempt < 30; attempt++)
                    {
                        await Task.Delay(attempt == 0 ? 1000 : 2000).ConfigureAwait(false);

                        var pendingCleared = false;
                        ExecuteLocked(() =>
                        {
                            try
                            {
                                FlushPendingSummaryCsvUnsafe(path);
                            }
                            catch
                            {
                                // 文件仍被占用时继续等待下一轮。
                            }

                            pendingCleared = ReadPendingSummaryEntriesUnsafe(path).Count == 0;
                        });

                        if (pendingCleared)
                        {
                            break;
                        }
                    }
                }
                catch
                {
                    // 后台补写失败忽略，下次有新的写入时继续尝试。
                }
                finally
                {
                    PendingCsvFlushPaths.TryRemove(path, out _);
                }
            });
        }

        private static string EscapeCsvCell(string value)
        {
            var text = value ?? string.Empty;
            if (text.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0)
            {
                return text;
            }

            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }

        private static void AppendTriggerSection(StringBuilder sb, AutoClearAlarmState state)
        {
            sb.AppendLine("1. 异常触发");
            if (state?.StartedAt != default)
            {
                sb.AppendLine($"触发时间：{state.StartedAt:yyyy-MM-dd HH:mm:ss.fff}");
            }

            sb.AppendLine("触发内容：");
            sb.AppendLine(IndentMultiline(FormatLogValue(state?.InputMessage), "  "));
        }

        private static void AppendToolCalls(StringBuilder sb, IReadOnlyList<AutoMcpToolCallEntry> calls)
        {
            sb.AppendLine("2. 调用的McpTool");

            if (calls == null || calls.Count == 0)
            {
                sb.AppendLine("无");
                return;
            }

            var index = 1;
            foreach (var call in calls)
            {
                if (call == null)
                {
                    continue;
                }

                AppendReadableToolCall(sb, call, index++);
            }
        }

        private static void AppendFinalReplySection(StringBuilder sb, string finalReply)
        {
            sb.AppendLine("3. 最终回答");
            sb.AppendLine(FormatLogValue(finalReply));
        }

        private static void AppendReadableToolCall(StringBuilder sb, AutoMcpToolCallEntry call, int index)
        {
            sb.AppendLine(FormatReadableToolCall(call, index));
        }

        private static string FormatReadableToolCall(AutoMcpToolCallEntry call, int index)
        {
            var sb = new StringBuilder();
            var title = BuildToolCallTitle(call);
            sb.Append($"{index}. [{call.Timestamp:yyyy-MM-dd HH:mm:ss.fff}] {title}");

            foreach (var line in BuildToolCallDetails(call))
            {
                sb.AppendLine();
                sb.Append($"   {line}");
            }

            return sb.ToString();
        }

        private static List<string> BuildToolCallDetails(AutoMcpToolCallEntry call)
        {
            var toolName = call?.ToolName?.Trim();
            if (string.Equals(toolName, "IoCommand", StringComparison.OrdinalIgnoreCase))
            {
                return BuildIoCommandDetails(call);
            }

            if (string.Equals(toolName, "IoQueryStatus", StringComparison.OrdinalIgnoreCase))
            {
                return BuildIoQueryDetails(call);
            }

            if (string.Equals(toolName, "ClearMachineAlarms", StringComparison.OrdinalIgnoreCase))
            {
                return BuildClearAlarmDetails(call);
            }

            if (IsMachineCommandTool(toolName))
            {
                return BuildMachineCommandDetails(call);
            }

            return BuildGenericToolDetails(call);
        }

        private static string BuildToolCallTitle(AutoMcpToolCallEntry call)
        {
            var toolName = call?.ToolName?.Trim();
            if (string.Equals(toolName, "IoCommand", StringComparison.OrdinalIgnoreCase))
            {
                return "执行 IO 操作";
            }

            if (string.Equals(toolName, "IoQueryStatus", StringComparison.OrdinalIgnoreCase))
            {
                return "读取 IO 状态";
            }

            if (string.Equals(toolName, "ClearMachineAlarms", StringComparison.OrdinalIgnoreCase))
            {
                return "执行机台报警消除";
            }

            if (string.Equals(toolName, "ResetMachine", StringComparison.OrdinalIgnoreCase))
            {
                return "执行设备复位";
            }

            if (string.Equals(toolName, "StartMachine", StringComparison.OrdinalIgnoreCase))
            {
                return "执行设备启动";
            }

            if (string.Equals(toolName, "PauseMachine", StringComparison.OrdinalIgnoreCase))
            {
                return "执行设备暂停";
            }

            if (string.Equals(toolName, "VisionCalibrateMachine", StringComparison.OrdinalIgnoreCase))
            {
                return "执行视觉标定";
            }

            if (string.Equals(toolName, "QuickInspectionMachine", StringComparison.OrdinalIgnoreCase))
            {
                return "执行一键点检";
            }

            return $"调用 {FormatLogValue(toolName)}";
        }

        private static bool IsMachineCommandTool(string toolName)
        {
            return string.Equals(toolName, "ResetMachine", StringComparison.OrdinalIgnoreCase)
                || string.Equals(toolName, "StartMachine", StringComparison.OrdinalIgnoreCase)
                || string.Equals(toolName, "PauseMachine", StringComparison.OrdinalIgnoreCase)
                || string.Equals(toolName, "VisionCalibrateMachine", StringComparison.OrdinalIgnoreCase)
                || string.Equals(toolName, "QuickInspectionMachine", StringComparison.OrdinalIgnoreCase);
        }

        private static List<string> BuildIoCommandDetails(AutoMcpToolCallEntry call)
        {
            var lines = new List<string>();
            var args = ParseJsonToken(call?.Args) as JObject;
            var result = ParseJsonToken(call?.Result) as JObject;
            var shadowMode = GetNullableBool(args?["shadowMode"]) ?? GetNullableBool(result?["shadowMode"]);

            var ioName = GetTokenText(args?["ioName"]) ?? GetTokenText(result?["ioName"]) ?? GetTokenText(result?["io"]);
            var op = GetTokenText(args?["op"]) ?? GetTokenText(result?["actionCode"]) ?? GetTokenText(result?["intent"]);
            var actionText = GetTokenText(result?["actionText"]);
            var address = GetTokenText(args?["address"]) ?? GetTokenText(result?["address"]);
            var verificationStatus = GetTokenText(result?["verificationStatus"]) ?? GetTokenText(result?["status"]);
            var verificationText = GetTokenText(result?["verificationText"]);
            var success = GetTokenText(result?["success"]) ?? GetTokenText(result?["ok"]);
            var resultText = GetTokenText(result?["resultText"]);

            if (!string.IsNullOrWhiteSpace(ioName))
            {
                lines.Add($"对象：{ioName}");
            }

            if (!string.IsNullOrWhiteSpace(actionText))
            {
                lines.Add($"动作：{actionText}");
            }
            else if (!string.IsNullOrWhiteSpace(op))
            {
                lines.Add($"动作：{FormatIoIntent(op)}");
            }

            if (!string.IsNullOrWhiteSpace(address))
            {
                lines.Add($"地址：{address}");
            }

            if (shadowMode.HasValue)
            {
                lines.Add($"执行方式：{(shadowMode.Value ? "影子模式（默认返回 OK，未真实执行）" : "真实执行")}");
            }

            if (!string.IsNullOrWhiteSpace(call?.Error))
            {
                lines.Add($"执行结果：失败（{call.Error}）");
            }
            else
            {
                lines.Add($"执行结果：{FormatIoCommandResult(success, verificationStatus, resultText)}");
            }

            var verificationSummary = BuildIoCommandVerificationSummary(op, verificationStatus, verificationText);
            if (!string.IsNullOrWhiteSpace(verificationSummary))
            {
                lines.Add($"状态校验：{verificationSummary}");
            }

            if (lines.Count == 0)
            {
                lines.Add($"结果：{FormatCompactText(call?.Result)}");
            }

            return lines;
        }

        private static List<string> BuildIoQueryDetails(AutoMcpToolCallEntry call)
        {
            var lines = new List<string>();
            var args = ParseJsonToken(call?.Args) as JObject;
            var result = ParseJsonToken(call?.Result) as JObject;

            var ioNames = GetTokenText(args?["ioNames"]);
            if (!string.IsNullOrWhiteSpace(ioNames))
            {
                lines.Add($"读取对象：{ioNames}");
            }

            if (!string.IsNullOrWhiteSpace(call?.Error))
            {
                lines.Add($"执行结果：失败（{call.Error}）");
            }

            var results = result?["results"] as JObject;
            if (results != null && results.Properties().Any())
            {
                var items = new List<string>();
                var errors = new List<string>();
                foreach (var property in results.Properties())
                {
                    var value = GetTokenText(property.Value);
                    items.Add($"{property.Name}={FormatIoQueryValue(value)}");
                    if (string.Equals(value, "error", StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add(property.Name);
                    }
                }

                lines.Add("读取结果：" + string.Join("；", items));
                if (errors.Count > 0)
                {
                    lines.Add("读取失败项：" + string.Join("、", errors));
                }
            }
            else if (string.IsNullOrWhiteSpace(call?.Error))
            {
                lines.Add($"结果：{FormatCompactText(call?.Result)}");
            }

            return lines;
        }

        private static List<string> BuildClearAlarmDetails(AutoMcpToolCallEntry call)
        {
            var lines = new List<string>();
            var args = ParseJsonToken(call?.Args) as JObject;
            var shadowMode = GetNullableBool(args?["shadowMode"]);
            if (shadowMode.HasValue)
            {
                lines.Add($"执行方式：{(shadowMode.Value ? "影子模式（仅记录，未真实执行）" : "真实执行")}");
            }

            if (!string.IsNullOrWhiteSpace(call?.Error))
            {
                lines.Add($"执行结果：失败（{call.Error}）");
            }
            else
            {
                lines.Add($"执行结果：{FormatClearAlarmResult(call?.Result, shadowMode)}");
            }

            return lines;
        }

        private static List<string> BuildMachineCommandDetails(AutoMcpToolCallEntry call)
        {
            var lines = new List<string>();
            var args = ParseJsonToken(call?.Args) as JObject;
            var shadowMode = GetNullableBool(args?["shadowMode"]);
            var actionName = GetTokenText(args?["actionName"]);

            if (!string.IsNullOrWhiteSpace(actionName))
            {
                lines.Add($"动作：{actionName}");
            }

            if (shadowMode.HasValue)
            {
                lines.Add($"执行方式：{(shadowMode.Value ? "影子模式（默认返回 OK，未真实执行）" : "真实执行")}");
            }

            if (!string.IsNullOrWhiteSpace(call?.Error))
            {
                lines.Add($"执行结果：失败（{FormatCompactText(call.Error)}）");
            }
            else
            {
                lines.Add($"执行结果：{FormatCompactText(call?.Result)}");
            }

            return lines;
        }

        private static List<string> BuildGenericToolDetails(AutoMcpToolCallEntry call)
        {
            var lines = new List<string>();

            if (!string.IsNullOrWhiteSpace(call?.Args))
            {
                lines.Add($"输入：{FormatCompactText(call.Args)}");
            }

            if (!string.IsNullOrWhiteSpace(call?.Error))
            {
                lines.Add($"执行结果：失败（{FormatCompactText(call.Error)}）");
            }
            else
            {
                lines.Add($"执行结果：{FormatCompactText(call?.Result)}");
            }

            return lines;
        }

        private static bool AddToolCallUnsafe(AutoClearAlarmState state, AutoMcpToolCallEntry entry)
        {
            if (state == null || entry == null || string.IsNullOrWhiteSpace(entry.ToolName))
            {
                return false;
            }

            state.McpToolCalls ??= new List<AutoMcpToolCallEntry>();
            var last = state.McpToolCalls.LastOrDefault();
            if (IsSameToolCall(last, entry))
            {
                return false;
            }

            state.McpToolCalls.Add(entry);
            if (state.McpToolCalls.Count > MaxToolCallEntries)
            {
                state.McpToolCalls = state.McpToolCalls
                    .Skip(Math.Max(0, state.McpToolCalls.Count - MaxToolCallEntries))
                    .ToList();
            }

            return true;
        }

        private static void UpsertNodeExecutionUnsafe(
            AutoClearAlarmState state,
            JObject nodeData,
            DateTime now,
            bool finished,
            double? elapsedSeconds)
        {
            if (state == null || nodeData == null)
            {
                return;
            }

            state.NodeExecutions ??= new List<AutoWorkflowNodeExecutionEntry>();

            var nodeId = TrimPlainText(nodeData.Value<string>("node_id") ?? nodeData.Value<string>("id"), 128);
            var title = TrimPlainText(nodeData.Value<string>("title"), 256);
            var nodeType = TrimPlainText(nodeData.Value<string>("node_type"), 64);
            var entry = FindNodeExecutionUnsafe(state.NodeExecutions, nodeId, title, nodeType, preferOpenEntry: true)
                        ?? new AutoWorkflowNodeExecutionEntry();

            if (!state.NodeExecutions.Contains(entry))
            {
                state.NodeExecutions.Add(entry);
            }

            entry.NodeId = string.IsNullOrWhiteSpace(nodeId) ? entry.NodeId : nodeId;
            entry.Title = string.IsNullOrWhiteSpace(title) ? entry.Title : title;
            entry.NodeType = string.IsNullOrWhiteSpace(nodeType) ? entry.NodeType : nodeType;
            entry.Inputs = TrimMultilineText(SerializeNodeToken(nodeData["inputs"]), 12000);
            entry.ProcessData = TrimMultilineText(SerializeNodeToken(nodeData["process_data"]), 12000);
            entry.Outputs = TrimMultilineText(SerializeNodeToken(nodeData["outputs"]), 12000);
            entry.ExecutionMetadata = TrimMultilineText(SerializeNodeToken(nodeData["execution_metadata"]), 12000);
            entry.Error = TrimMultilineText(SerializeNodeToken(nodeData["error"]), 6000);
            entry.ElapsedSeconds = elapsedSeconds ?? entry.ElapsedSeconds;

            if (entry.StartedAt == default)
            {
                if (finished && elapsedSeconds.HasValue && elapsedSeconds.Value > 0)
                {
                    entry.StartedAt = now - TimeSpan.FromSeconds(elapsedSeconds.Value);
                }
                else
                {
                    entry.StartedAt = now;
                }
            }

            entry.LastUpdatedAt = now;
            entry.Status = finished
                ? (string.IsNullOrWhiteSpace(entry.Error) ? "succeeded" : "failed")
                : "running";

            if (finished)
            {
                entry.FinishedAt = now;
            }

            if (state.NodeExecutions.Count > MaxNodeExecutionEntries)
            {
                state.NodeExecutions = state.NodeExecutions
                    .OrderBy(item => item.StartedAt == default ? item.LastUpdatedAt : item.StartedAt)
                    .Skip(Math.Max(0, state.NodeExecutions.Count - MaxNodeExecutionEntries))
                    .ToList();
            }
        }

        private static AutoWorkflowNodeExecutionEntry FindNodeExecutionUnsafe(
            IReadOnlyList<AutoWorkflowNodeExecutionEntry> entries,
            string nodeId,
            string title,
            string nodeType,
            bool preferOpenEntry)
        {
            if (entries == null || entries.Count == 0)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(nodeId))
            {
                var byId = entries
                    .Reverse()
                    .FirstOrDefault(item =>
                        string.Equals(item?.NodeId, nodeId, StringComparison.OrdinalIgnoreCase)
                        && (!preferOpenEntry || item.FinishedAt == default));
                if (byId != null)
                {
                    return byId;
                }

                byId = entries
                    .Reverse()
                    .FirstOrDefault(item => string.Equals(item?.NodeId, nodeId, StringComparison.OrdinalIgnoreCase));
                if (byId != null)
                {
                    return byId;
                }
            }

            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(nodeType))
            {
                return null;
            }

            return entries
                .Reverse()
                .FirstOrDefault(item =>
                    string.Equals(item?.Title ?? string.Empty, title ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item?.NodeType ?? string.Empty, nodeType ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                    && (!preferOpenEntry || item.FinishedAt == default));
        }

        private static bool IsSameToolCall(AutoMcpToolCallEntry left, AutoMcpToolCallEntry right)
        {
            if (left == null || right == null)
            {
                return false;
            }

            if (!string.Equals(left.ToolName, right.ToolName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.Equals(left.Source ?? string.Empty, right.Source ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.Equals(left.Args ?? string.Empty, right.Args ?? string.Empty, StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.Equals(left.Result ?? string.Empty, right.Result ?? string.Empty, StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.Equals(left.Error ?? string.Empty, right.Error ?? string.Empty, StringComparison.Ordinal))
            {
                return false;
            }

            return Math.Abs((left.Timestamp - right.Timestamp).TotalSeconds) <= 2;
        }

        private static AutoClearAlarmState FindAttachableTraceUnsafe(DateTime now)
        {
            var states = ReadAllStatesUnsafe();
            if (states.Count == 0)
            {
                return new AutoClearAlarmState();
            }

            var active = states
                .Where(state => IsActiveStatus(state?.Status) && CanAttachToActiveTrace(state, now))
                .OrderByDescending(GetAttachPriorityTimestamp)
                .FirstOrDefault();

            if (active != null)
            {
                return active;
            }

            return states
                .Where(state => state != null
                                && state.FinalizedAt != default
                                && CanAttachToActiveTrace(state, now))
                .OrderByDescending(state => state.FinalizedAt)
                .ThenByDescending(GetAttachPriorityTimestamp)
                .FirstOrDefault()
                   ?? new AutoClearAlarmState();
        }

        private static List<AutoClearAlarmState> ReadAllStatesUnsafe()
        {
            var list = new List<AutoClearAlarmState>();
            foreach (var traceId in ReadRecentTraceIndexEntriesUnsafe()
                         .Select(entry => entry?.TraceId)
                         .Where(traceId => !string.IsNullOrWhiteSpace(traceId))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var state = ReadTraceStateUnsafe(traceId);
                if (!string.IsNullOrWhiteSpace(state?.TraceId))
                {
                    list.Add(state);
                }
            }

            if (list.Count > 0)
            {
                return list;
            }

            var root = GetTraceStateRoot();
            if (!Directory.Exists(root))
            {
                return list;
            }

            foreach (var path in Directory.GetFiles(root, "*.json", SearchOption.TopDirectoryOnly)
                         .OrderByDescending(path =>
                         {
                             try
                             {
                                 return File.GetLastWriteTime(path);
                             }
                             catch
                             {
                                 return DateTime.MinValue;
                             }
                         })
                         .Take(MaxRecentTraceIndexEntries))
            {
                var state = ReadStateFileUnsafe(path);
                if (!string.IsNullOrWhiteSpace(state?.TraceId))
                {
                    list.Add(state);
                }
            }

            return list;
        }

        private static string GetStateSnapshotPath()
        {
            return Path.Combine(StateRoot, StateSnapshotFileName);
        }

        private static string GetRecentTraceIndexPath()
        {
            return Path.Combine(StateRoot, StateRecentIndexFileName);
        }

        private static string GetTraceStateRoot()
        {
            return Path.Combine(StateRoot, StateTraceDirName);
        }

        private static string GetTraceStatePath(string traceId)
        {
            return Path.Combine(GetTraceStateRoot(), traceId + ".json");
        }

        private static List<RecentTraceIndexEntry> ReadRecentTraceIndexEntriesUnsafe()
        {
            try
            {
                var path = GetRecentTraceIndexPath();
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    return new List<RecentTraceIndexEntry>();
                }

                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs, Utf8NoBom, true);
                var json = reader.ReadToEnd();
                if (string.IsNullOrWhiteSpace(json))
                {
                    return new List<RecentTraceIndexEntry>();
                }

                var entries = JsonConvert.DeserializeObject<List<RecentTraceIndexEntry>>(json)
                              ?? new List<RecentTraceIndexEntry>();
                return OrderRecentTraceIndexEntries(entries);
            }
            catch
            {
                return new List<RecentTraceIndexEntry>();
            }
        }

        private static void UpsertRecentTraceIndexUnsafe(AutoClearAlarmState state)
        {
            if (state == null || string.IsNullOrWhiteSpace(state.TraceId))
            {
                return;
            }

            var entries = ReadRecentTraceIndexEntriesUnsafe();
            entries.RemoveAll(entry =>
                string.Equals(entry?.TraceId, state.TraceId, StringComparison.OrdinalIgnoreCase));
            entries.Add(new RecentTraceIndexEntry
            {
                TraceId = state.TraceId,
                StartedAt = state.StartedAt,
                LastUpdatedAt = state.LastUpdatedAt,
                FinalizedAt = state.FinalizedAt
            });

            WriteRecentTraceIndexEntriesUnsafe(OrderRecentTraceIndexEntries(entries));
        }

        private static List<RecentTraceIndexEntry> OrderRecentTraceIndexEntries(IEnumerable<RecentTraceIndexEntry> entries)
        {
            return (entries ?? Enumerable.Empty<RecentTraceIndexEntry>())
                .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.TraceId))
                .GroupBy(entry => entry.TraceId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(GetRecentTraceIndexReferenceTime)
                    .First())
                .OrderByDescending(GetRecentTraceIndexReferenceTime)
                .Take(MaxRecentTraceIndexEntries)
                .ToList();
        }

        private static DateTime GetRecentTraceIndexReferenceTime(RecentTraceIndexEntry entry)
        {
            if (entry == null)
            {
                return DateTime.MinValue;
            }

            if (entry.FinalizedAt != default)
            {
                return entry.FinalizedAt;
            }

            if (entry.LastUpdatedAt != default)
            {
                return entry.LastUpdatedAt;
            }

            return entry.StartedAt;
        }

        private static void WriteRecentTraceIndexEntriesUnsafe(IReadOnlyList<RecentTraceIndexEntry> entries)
        {
            var path = GetRecentTraceIndexPath();
            var normalizedEntries = OrderRecentTraceIndexEntries(entries);
            if (normalizedEntries.Count == 0)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch
                {
                    // 删除失败忽略，下次维护再尝试。
                }

                return;
            }

            var json = JsonConvert.SerializeObject(normalizedEntries, Formatting.Indented);
            WriteAllTextAtomic(path, json, Utf8NoBom);
        }

        private static void CleanupRecentTraceIndexUnsafe(DateTime now)
        {
            var entries = ReadRecentTraceIndexEntriesUnsafe();
            if (entries.Count == 0)
            {
                WriteRecentTraceIndexEntriesUnsafe(entries);
                return;
            }

            var keptEntries = entries
                .Where(entry =>
                {
                    if (entry == null || string.IsNullOrWhiteSpace(entry.TraceId))
                    {
                        return false;
                    }

                    var tracePath = GetTraceStatePath(entry.TraceId);
                    if (!File.Exists(tracePath))
                    {
                        DeletePendingSummaryCsvUnsafe(entry.TraceId);
                        return false;
                    }

                    var referenceTime = GetRecentTraceIndexReferenceTime(entry);
                    return referenceTime == default || now - referenceTime <= StateRetentionWindow;
                })
                .ToList();

            WriteRecentTraceIndexEntriesUnsafe(keptEntries);
        }

        private static void RefreshStateSnapshotUnsafe()
        {
            foreach (var traceId in ReadRecentTraceIndexEntriesUnsafe()
                         .Select(entry => entry?.TraceId)
                         .Where(traceId => !string.IsNullOrWhiteSpace(traceId))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var path = GetTraceStatePath(traceId);
                try
                {
                    if (!File.Exists(path))
                    {
                        continue;
                    }

                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(fs, Utf8NoBom, true);
                    var json = reader.ReadToEnd();
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        continue;
                    }

                    WriteAllTextAtomic(GetStateSnapshotPath(), json, Utf8NoBom);
                    return;
                }
                catch
                {
                    // 单个 trace 状态文件读取失败时尝试下一个。
                }
            }

            try
            {
                var snapshotPath = GetStateSnapshotPath();
                if (File.Exists(snapshotPath))
                {
                    File.Delete(snapshotPath);
                }
            }
            catch
            {
                // 删除失败忽略，下次维护再尝试。
            }
        }

        private static bool IsActiveStatus(string status)
        {
            var value = (status ?? string.Empty).Trim();
            return string.Equals(value, TraceStatusAccepted, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(value, TraceStatusRunning, StringComparison.OrdinalIgnoreCase);
        }

        private static DateTime GetAttachPriorityTimestamp(AutoClearAlarmState state)
        {
            if (state == null)
            {
                return DateTime.MinValue;
            }

            if (state.LastUpdatedAt != default)
            {
                return state.LastUpdatedAt;
            }

            if (state.FinalizedAt != default)
            {
                return state.FinalizedAt;
            }

            return state.StartedAt;
        }

        private static void CleanupStateFilesUnsafe(DateTime now)
        {
            var root = GetTraceStateRoot();
            if (Directory.Exists(root))
            {
                foreach (var path in Directory.GetFiles(root, "*.json", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        var state = ReadStateFileUnsafe(path);
                        var referenceTime = state?.FinalizedAt != default
                            ? state.FinalizedAt
                            : (state?.LastUpdatedAt != default ? state.LastUpdatedAt : state?.StartedAt ?? default);

                        if (referenceTime == default)
                        {
                            referenceTime = File.GetLastWriteTime(path);
                        }

                        if (referenceTime != default && now - referenceTime > StateRetentionWindow)
                        {
                            DeletePendingSummaryCsvUnsafe(state?.TraceId);
                            File.Delete(path);
                        }
                    }
                    catch
                    {
                        // 清理失败忽略
                    }
                }
            }

            CleanupRecentTraceIndexUnsafe(now);
            RefreshStateSnapshotUnsafe();
        }

        private static bool IsSameTrace(AutoClearAlarmState state, string traceId)
        {
            return state != null
                   && !string.IsNullOrWhiteSpace(state.TraceId)
                   && string.Equals(state.TraceId, traceId, StringComparison.OrdinalIgnoreCase);
        }

        private static bool CanAttachToActiveTrace(AutoClearAlarmState state, DateTime now)
        {
            if (state == null || string.IsNullOrWhiteSpace(state.TraceId))
            {
                return false;
            }

            var activeAt = state.LastUpdatedAt == default ? state.StartedAt : state.LastUpdatedAt;
            if (activeAt == default)
            {
                return false;
            }

            if (state.FinalizedAt != default)
            {
                return now - state.FinalizedAt <= CompletedTraceAttachWindow;
            }

            return now - activeAt <= ActiveTraceWindow;
        }

        private static string BuildInputText(string errorCode, string prompt)
        {
            var code = TrimPlainText(errorCode, 256);
            var desc = TrimMultilineText(prompt, 4000);

            if (!string.IsNullOrWhiteSpace(code) && !string.IsNullOrWhiteSpace(desc))
            {
                return $"报警代码={code}{Environment.NewLine}内容={desc}";
            }

            if (!string.IsNullOrWhiteSpace(desc))
            {
                return desc;
            }

            return string.IsNullOrWhiteSpace(code) ? "空" : code;
        }

        private static string ExtractAlarmContentForCsv(string inputMessage)
        {
            var text = (inputMessage ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var lines = text
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (line.StartsWith("内容=", StringComparison.Ordinal))
                {
                    var value = line.Substring("内容=".Length).Trim();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }

            return text;
        }

        private static JToken ParseJsonToken(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            try
            {
                return JToken.Parse(text);
            }
            catch
            {
                return null;
            }
        }

        private static string GetTokenText(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
            {
                return null;
            }

            if (token.Type == JTokenType.Array)
            {
                var values = token
                    .Children()
                    .Select(GetTokenText)
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .ToList();
                return values.Count == 0 ? null : string.Join("、", values);
            }

            if (token.Type == JTokenType.Boolean)
            {
                return token.Value<bool>() ? "true" : "false";
            }

            return token.Type == JTokenType.String
                ? token.Value<string>()?.Trim()
                : token.ToString(Formatting.None);
        }

        private static bool? GetNullableBool(JToken token)
        {
            var text = GetTokenText(token);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            if (bool.TryParse(text, out var boolValue))
            {
                return boolValue;
            }

            return text switch
            {
                "1" => true,
                "0" => false,
                _ => null
            };
        }

        private static string FormatIoIntent(string op)
        {
            return (op ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "open" => "打开",
                "close" => "关闭",
                _ => FormatCompactText(op)
            };
        }

        private static string FormatIoCommandResult(string success, string verificationStatus, string resultText)
        {
            if (!string.IsNullOrWhiteSpace(resultText))
            {
                var normalizedStatus = (verificationStatus ?? string.Empty).Trim().ToLowerInvariant();
                if (normalizedStatus == "passed" || normalizedStatus == "failed" || normalizedStatus == "unavailable")
                {
                    return string.Equals((success ?? string.Empty).Trim(), "true", StringComparison.OrdinalIgnoreCase)
                        ? "成功"
                        : "已执行";
                }
            }

            return (verificationStatus ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "passed" => "成功",
                "failed" => "已执行，但状态与预期不一致",
                "unavailable" => "已执行，但无法完成状态校验",
                "timeout" => "失败（超时/无响应）",
                "ok" => "成功",
                "readback_unexpected" => "已执行，但状态与预期不一致",
                "readback_unavailable" => "已执行，但无法完成状态校验",
                _ => string.IsNullOrWhiteSpace(verificationStatus) ? "已执行" : FormatCompactText(verificationStatus)
            };
        }

        private static string BuildIoCommandVerificationSummary(string op, string verificationStatus, string verificationText)
        {
            if (!string.IsNullOrWhiteSpace(verificationText))
            {
                return FormatCompactText(verificationText).TrimEnd('。');
            }

            var action = FormatIoIntent(op);
            return (verificationStatus ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "passed" => string.IsNullOrWhiteSpace(action) ? "动作已到位" : $"{action}动作已到位",
                "failed" => string.IsNullOrWhiteSpace(action) ? "执行后状态与预期不一致" : $"{action}后状态与预期不一致",
                "unavailable" => "未获取到可用读回结果",
                "timeout" => "执行超时，未完成状态校验",
                "ok" => string.IsNullOrWhiteSpace(action) ? "动作已到位" : $"{action}动作已到位",
                "readback_unexpected" => string.IsNullOrWhiteSpace(action) ? "执行后状态与预期不一致" : $"{action}后状态与预期不一致",
                "readback_unavailable" => "未获取到可用读回结果",
                _ => null
            };
        }

        private static string FormatIoQueryValue(string value)
        {
            return (value ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "true" => "true（原始IO值）",
                "false" => "false（原始IO值）",
                "error" => "读取失败",
                "" => "空",
                _ => FormatCompactText(value)
            };
        }

        private static string FormatClearAlarmResult(string result, bool? shadowMode)
        {
            if (shadowMode == true)
            {
                return "已记录调用，未真实执行";
            }

            var text = (result ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return "空";
            }

            return FormatCompactText(text);
        }

        private static string FormatCompactText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return "空";
            }

            return TrimPlainText(text, 500);
        }

        private static string TrimPlainText(string text, int max)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return text;
            }

            var normalized = text.Replace("\r\n", " ").Replace('\n', ' ').Trim();
            if (normalized.Length <= max)
            {
                return normalized;
            }

            return normalized.Substring(0, max) + $"...（后续截断 {normalized.Length - max} 字符）";
        }

        private static string TrimMultilineText(string text, int max)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return text;
            }

            var normalized = text
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Trim()
                .Replace("\n", Environment.NewLine);

            if (normalized.Length <= max)
            {
                return normalized;
            }

            return normalized.Substring(0, max) + $"...（后续截断 {normalized.Length - max} 字符）";
        }

        private static string FormatLogValue(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "空" : value;
        }

        private static string IndentMultiline(string text, string indent)
        {
            var normalized = FormatLogValue(text)
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Replace("\n", Environment.NewLine);
            return indent + normalized.Replace(Environment.NewLine, Environment.NewLine + indent);
        }

        private static string SafeSerialize(object value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            try
            {
                return JsonConvert.SerializeObject(value);
            }
            catch
            {
                return value.ToString() ?? string.Empty;
            }
        }

        private static string SerializeNodeToken(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return string.Empty;
            }

            try
            {
                if (token.Type == JTokenType.String)
                {
                    var text = token.ToString();
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        return string.Empty;
                    }

                    try
                    {
                        var reparsed = SanitizeReasoningTokenForLog(JToken.Parse(text));
                        return reparsed.ToString(Formatting.Indented);
                    }
                    catch
                    {
                        return StripReasoningBlocksForLog(text);
                    }
                }

                return SanitizeReasoningTokenForLog(token).ToString(Formatting.Indented);
            }
            catch
            {
                return token.ToString();
            }
        }

        private static JToken SanitizeReasoningTokenForLog(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return JValue.CreateNull();
            }

            if (token.Type == JTokenType.Object)
            {
                var result = new JObject();
                foreach (var property in ((JObject)token).Properties())
                {
                    if (IsReasoningFieldName(property.Name))
                    {
                        continue;
                    }

                    result[property.Name] = SanitizeReasoningTokenForLog(property.Value);
                }

                return result;
            }

            if (token.Type == JTokenType.Array)
            {
                var result = new JArray();
                foreach (var item in (JArray)token)
                {
                    result.Add(SanitizeReasoningTokenForLog(item));
                }

                return result;
            }

            if (token.Type == JTokenType.String)
            {
                var text = token.Value<string>();
                if (TryParseJsonLikeForReasoningSanitizer(text, out var parsed))
                {
                    return SanitizeReasoningTokenForLog(parsed);
                }

                return new JValue(StripReasoningBlocksForLog(text));
            }

            return token.DeepClone();
        }

        private static bool IsReasoningFieldName(string propertyName)
        {
            return !string.IsNullOrWhiteSpace(propertyName) &&
                   ReasoningFieldNames.Contains(propertyName.Trim());
        }

        private static bool TryParseJsonLikeForReasoningSanitizer(string text, out JToken token)
        {
            token = null;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var trimmed = text.Trim();
            if (!((trimmed.StartsWith("{", StringComparison.Ordinal) && trimmed.EndsWith("}", StringComparison.Ordinal)) ||
                  (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))))
            {
                return false;
            }

            try
            {
                token = JToken.Parse(trimmed);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string StripReasoningBlocksForLog(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text ?? string.Empty;
            }

            var cleaned = Regex.Replace(
                text,
                "<\\s*(think|thinking|reasoning)\\b[^>]*>.*?</\\s*(think|thinking|reasoning)\\s*>",
                string.Empty,
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            cleaned = Regex.Replace(
                cleaned,
                "<\\s*(think|thinking|reasoning)\\b[^>]*>.*$",
                string.Empty,
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            return Regex.Replace(
                cleaned,
                "</\\s*(think|thinking|reasoning)\\s*>",
                string.Empty,
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
        }

        private static void EnsureDir(string path)
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
        }

        private sealed class AutoClearAlarmState
        {
            public string TraceId { get; set; }
            public string Status { get; set; }
            public string WorkflowRunId { get; set; }
            public string TaskId { get; set; }
            public string InputMessage { get; set; }
            public string FinalReply { get; set; }
            public string Phenomenon { get; set; }
            public string ManualCountermeasure { get; set; }
            public DateTime StartedAt { get; set; }
            public DateTime LastUpdatedAt { get; set; }
            public DateTime FinalizedAt { get; set; }
            public DateTime ManualReviewedAt { get; set; }
            public DateTime CsvSyncedAt { get; set; }
            public bool ClearMachineAlarmsCalled { get; set; }
            public bool IsVisionRelated { get; set; }
            public DateTime ClearMachineAlarmsCalledAt { get; set; }
            public string CsvFingerprint { get; set; }
            public string AutoVisionSourceImagePath { get; set; }
            public string AutoVisionProcessedImagePath { get; set; }
            public List<AutoMcpToolCallEntry> McpToolCalls { get; set; } = new List<AutoMcpToolCallEntry>();
            public List<AutoWorkflowNodeExecutionEntry> NodeExecutions { get; set; } = new List<AutoWorkflowNodeExecutionEntry>();
        }

        private sealed class AutoMcpToolCallEntry
        {
            public DateTime Timestamp { get; set; }
            public string ToolName { get; set; }
            public string Source { get; set; }
            public string Args { get; set; }
            public string Result { get; set; }
            public string Error { get; set; }
        }

        private sealed class AutoWorkflowNodeExecutionEntry
        {
            public string NodeId { get; set; }
            public string Title { get; set; }
            public string NodeType { get; set; }
            public string Status { get; set; }
            public DateTime StartedAt { get; set; }
            public DateTime LastUpdatedAt { get; set; }
            public DateTime FinishedAt { get; set; }
            public double? ElapsedSeconds { get; set; }
            public string Inputs { get; set; }
            public string ProcessData { get; set; }
            public string Outputs { get; set; }
            public string ExecutionMetadata { get; set; }
            public string Error { get; set; }
        }

        private sealed class SummaryCsvPendingEntry
        {
            public string TraceId { get; set; }
            public string CsvPath { get; set; }
            public string Fingerprint { get; set; }
            public DateTime QueuedAt { get; set; }
            public List<string> Row { get; set; } = new List<string>();
        }

        private sealed class RecentTraceIndexEntry
        {
            public string TraceId { get; set; }
            public DateTime StartedAt { get; set; }
            public DateTime LastUpdatedAt { get; set; }
            public DateTime FinalizedAt { get; set; }
        }

        public sealed class SummaryCsvRecord
        {
            public string CsvPath { get; set; }
            public int RowIndex { get; set; }
            public DateTime TriggerDate { get; set; }
            public DateTime? TriggeredAt { get; set; }
            public string TriggerTimeText { get; set; }
            public string AlarmContent { get; set; }
            public string Phenomenon { get; set; }
            public string AiReply { get; set; }
            public string McpCall { get; set; }
            public string ManualCountermeasure { get; set; }
        }
    }
}

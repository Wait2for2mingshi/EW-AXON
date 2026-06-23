using EW_Assistant.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace EW_Assistant.Views
{
    /// <summary>
    /// 本地路由入口页：先调用 Brain workflow，再由本地路由层选择 executor workflow。
    /// </summary>
    public partial class UiRuntimeView : UserControl, INotifyPropertyChanged
    {
        private const string DefaultTaskGoal =
            "进入D盘";
        private const int StopRequestGracePeriodMs = 2000;
        private const int MaxWorkflowEventLogEntries = 256;
        private string _taskGoal = DefaultTaskGoal;
        private string _startDelaySecondsText = "2";
        private bool _brainDryRunEnabled;
        private string _taskStatus = "等待任务";
        private string _activityStatus = "等待日志";
        private string _latestResponseJson =
            "暂无日志";
        private bool _taskRunning;
        private bool _stopRequested;
        private CancellationTokenSource _taskRunCts;
        private string _currentRouteTraceId = string.Empty;
        private string _currentExecutorApiKey = string.Empty;
        private string _currentWorkflowRunId = string.Empty;
        private string _currentWorkflowTaskId = string.Empty;
        private readonly List<UiCoarseVisionWorkflowStreamEvent> _workflowEvents = new List<UiCoarseVisionWorkflowStreamEvent>();

        public UiRuntimeView()
        {
            InitializeComponent();
            DataContext = this;
            Unloaded += UiRuntimeView_Unloaded;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public string TaskGoal
        {
            get => _taskGoal;
            set => SetField(ref _taskGoal, value);
        }

        public string StartDelaySecondsText
        {
            get => _startDelaySecondsText;
            set => SetField(ref _startDelaySecondsText, value);
        }

        public bool BrainDryRunEnabled
        {
            get => _brainDryRunEnabled;
            set => SetField(ref _brainDryRunEnabled, value);
        }

        public string TaskStatus
        {
            get => _taskStatus;
            set => SetField(ref _taskStatus, value);
        }

        public string ActivityStatus
        {
            get => _activityStatus;
            set => SetField(ref _activityStatus, value);
        }

        public string LatestResponseJson
        {
            get => _latestResponseJson;
            set => SetField(ref _latestResponseJson, value);
        }

        public string CurrentRouteTraceId => _currentRouteTraceId;

        public string CurrentWorkflowRunId => _currentWorkflowRunId;

        public string CurrentWorkflowTaskId => _currentWorkflowTaskId;

        public bool CanStartTask => !_taskRunning && !_stopRequested;

        public bool CanStopTask => _taskRunning && !_stopRequested;

        private void BtnCopyCurrentRouteTraceId_Click(object sender, RoutedEventArgs e)
        {
            CopyToClipboard(_currentRouteTraceId, "RouteTraceId");
        }

        private void BtnCopyCurrentWorkflowRunId_Click(object sender, RoutedEventArgs e)
        {
            CopyToClipboard(_currentWorkflowRunId, "WorkflowRunId");
        }

        private void BtnCopyCurrentWorkflowTaskId_Click(object sender, RoutedEventArgs e)
        {
            CopyToClipboard(_currentWorkflowTaskId, "TaskId");
        }

        private void BtnCopyLatestResponseJson_Click(object sender, RoutedEventArgs e)
        {
            CopyToClipboard(_latestResponseJson, "日志内容");
        }

        private void BtnClearLatestResponseJson_Click(object sender, RoutedEventArgs e)
        {
            LatestResponseJson = "暂无日志";
            if (!_taskRunning)
                ActivityStatus = "等待日志";
        }

        private async void BtnStartTask_Click(object sender, RoutedEventArgs e)
        {
            if (_taskRunning)
            {
                MainWindow.PostProgramInfo("[UiTaskFlow] 本地路由任务正在执行，请稍候。", "warn");
                return;
            }

            if (string.IsNullOrWhiteSpace(TaskGoal))
            {
                MainWindow.PostProgramInfo("[UiTaskFlow] 请先填写目标。", "warn");
                return;
            }

            try
            {
                if (!TryParseDelaySeconds(StartDelaySecondsText, 120, out var delaySeconds, out var delayError))
                {
                    TaskStatus = delayError;
                    MainWindow.PostProgramInfo("[UiTaskFlow] " + delayError, "warn");
                    return;
                }

                ResetCurrentTracking();
                SetStopRequested(false);
                _taskRunCts?.Dispose();
                _taskRunCts = new CancellationTokenSource();
                var runToken = _taskRunCts.Token;
                SetTaskRunning(true);
                _currentRouteTraceId = AgentControlValueHelper.CreateRouteTraceId();
                RaiseTrackingChanged();

                if (delaySeconds > 0)
                {
                    for (var remaining = delaySeconds; remaining > 0; remaining--)
                    {
                        TaskStatus = $"{remaining} 秒后启动本地路由任务，请切到目标界面。";
                        await Task.Delay(1000, runToken).ConfigureAwait(true);
                    }
                }

                TaskStatus = BrainDryRunEnabled
                    ? "正在调用 Brain workflow（Dry Run），本次不会实际启动 executor..."
                    : "正在调用 Brain workflow，并由本地路由层决定是否启动 executor...";
                ResetWorkflowEventLog();
                var result = await UiTaskRouterService.Instance.RunStreamingAsync(
                    new UiTaskRouteRequest
                    {
                        Goal = TaskGoal,
                        RouteTraceId = _currentRouteTraceId,
                        BrainDryRunEnabled = BrainDryRunEnabled
                    },
                    HandleRouteDispatchAsync,
                    HandleWorkflowStreamEventAsync,
                    runToken).ConfigureAwait(true);

                UpdateCurrentTracking(result);
                if (_stopRequested)
                {
                    LatestResponseJson = BuildRouteWorkflowOutput(result, _workflowEvents, "已停止");
                    TaskStatus = "已请求停止当前本地路由任务。";
                    ActivityStatus = "当前任务已停止。";
                    MainWindow.PostProgramInfo("[UiTaskFlow] 本地路由任务已停止。", "warn");
                }
                else
                {
                    LatestResponseJson = BuildRouteWorkflowOutput(result, _workflowEvents);
                    ApplyRouteResult(result);
                    if (string.Equals(result.FinalDecision, "run_executor", StringComparison.OrdinalIgnoreCase))
                    {
                        if (result.ExecutorSkipped)
                        {
                            var skipReason = AgentControlValueHelper.SafeValue(result.ExecutorSkipReason);
                            TaskStatus = "Brain Dry Run 已完成：" + skipReason;
                            MainWindow.PostProgramInfo("[UiTaskFlow] Brain Dry Run 已完成，本次未实际启动 executor。", "ok");
                        }
                        else if (result.ExecutorResult?.Succeeded == true)
                        {
                            var workflowSummary = UiWorkflowTraceFormatter.TryExtractWorkflowSummary(result.ExecutorResult);
                            TaskStatus = string.IsNullOrWhiteSpace(workflowSummary)
                                ? "本地路由已完成：executor 已返回结果。"
                                : "本地路由已完成：" + workflowSummary;
                            MainWindow.PostProgramInfo("[UiTaskFlow] 本地路由已完成，executor 返回成功。", "ok");
                        }
                        else
                        {
                            var error = AgentControlValueHelper.SafeValue(result.ErrorMessage ?? result.ExecutorResult?.ErrorMessage);
                            TaskStatus = "本地路由调用 executor 失败：" + error;
                            MainWindow.PostProgramInfo("[UiTaskFlow] 本地路由调用 executor 失败：" + error, "warn");
                        }
                    }
                    else if (string.Equals(result.FinalDecision, "reply_direct", StringComparison.OrdinalIgnoreCase))
                    {
                        TaskStatus = "Brain 已直接回复：" + AgentControlValueHelper.SafeValue(result.FinalReply);
                        MainWindow.PostProgramInfo("[UiTaskFlow] Brain 已直接回复，无需进入 executor。", "ok");
                    }
                    else if (string.Equals(result.FinalDecision, "reject", StringComparison.OrdinalIgnoreCase))
                    {
                        TaskStatus = "Brain 拒绝继续执行：" + AgentControlValueHelper.SafeValue(result.FinalReply);
                        MainWindow.PostProgramInfo("[UiTaskFlow] Brain 判定该请求不适合直接执行。", "warn");
                    }
                    else
                    {
                        TaskStatus = "Brain 需要人工补充信息：" + AgentControlValueHelper.SafeValue(result.FinalReply);
                        MainWindow.PostProgramInfo("[UiTaskFlow] Brain 需要人工补充信息。", "warn");
                    }
                }
            }
            catch (OperationCanceledException) when (_stopRequested)
            {
                LatestResponseJson = BuildStoppedRouteOutput();
                TaskStatus = "已停止当前本地路由任务。";
                ActivityStatus = "当前任务已停止。";
                MainWindow.PostProgramInfo("[UiTaskFlow] 已停止当前本地路由任务。", "warn");
            }
            catch (OperationCanceledException)
            {
                TaskStatus = "本地路由监听已取消。";
                MainWindow.PostProgramInfo("[UiTaskFlow] 本地路由监听已取消。", "warn");
            }
            catch (Exception ex)
            {
                TaskStatus = "本地路由任务异常：" + ex.Message;
                LatestResponseJson = BuildExceptionOutput("本地路由任务", ex.Message);
                MainWindow.PostProgramInfo("[UiTaskFlow] 本地路由任务异常：" + ex.Message, "error");
            }
            finally
            {
                _taskRunCts?.Dispose();
                _taskRunCts = null;
                ResetCurrentTracking();
                SetTaskRunning(false);
                SetStopRequested(false);
            }
        }

        private async void BtnStopTask_Click(object sender, RoutedEventArgs e)
        {
            if (!_taskRunning || _stopRequested)
                return;

            SetStopRequested(true);
            ActivityStatus = "正在停止当前任务...";
            MainWindow.PostProgramInfo("[UiTaskFlow] 正在请求停止本地路由任务。", "warn");
            var cancelImmediately = true;

            try
            {
                if (!string.IsNullOrWhiteSpace(_currentWorkflowTaskId))
                {
                    using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await UiTaskRouterService.Instance
                        .StopExecutorAsync(_currentWorkflowTaskId, _currentExecutorApiKey, stopCts.Token)
                        .ConfigureAwait(true);

                    cancelImmediately = false;
                    TaskStatus = $"已向 executor 提交停止请求，等待平台结束当前任务，taskId={_currentWorkflowTaskId}。";
                    MainWindow.PostProgramInfo("[UiTaskFlow] 已向 executor 提交停止请求，等待平台结束，taskId=" + _currentWorkflowTaskId + "。", "warn");
                    ScheduleForcedStreamingCancellation();
                }
                else
                {
                    TaskStatus = "当前尚未进入 executor，先停止本地等待。";
                    MainWindow.PostProgramInfo("[UiTaskFlow] 当前尚未进入 executor，先停止本地等待。", "warn");
                }
            }
            catch (Exception ex)
            {
                TaskStatus = "停止请求失败，将中断本地流式连接：" + ex.Message;
                MainWindow.PostProgramInfo("[UiTaskFlow] 停止请求失败，将中断本地流式连接：" + ex.Message, "warn");
            }
            finally
            {
                if (cancelImmediately)
                {
                    try
                    {
                        _taskRunCts?.Cancel();
                    }
                    catch
                    {
                        // 取消失败不阻断 UI 收尾
                    }
                }
            }
        }

        private void ApplyRouteResult(UiTaskRouteResult result)
        {
            if (result?.ExecutorSkipped == true)
            {
                ActivityStatus = string.IsNullOrWhiteSpace(result.ExecutorSkipReason)
                    ? "Brain Dry Run 已完成，本次未实际启动 executor。"
                    : result.ExecutorSkipReason;
                return;
            }

            if (result?.ExecutorResult != null)
            {
                var summary = UiWorkflowTraceFormatter.TryExtractWorkflowSummary(result.ExecutorResult);
                ActivityStatus = string.IsNullOrWhiteSpace(summary)
                    ? (result.ExecutorResult.Succeeded
                        ? "executor 已返回结果，详见右侧日志。"
                        : "executor 未返回可识别摘要，请查看右侧日志。")
                    : summary;
                return;
            }

            ActivityStatus = string.IsNullOrWhiteSpace(result?.FinalReply)
                ? "Brain 已返回路由结果。"
                : result.FinalReply;
        }

        private void ResetWorkflowEventLog()
        {
            _workflowEvents.Clear();
            LatestResponseJson = BuildWorkflowLiveTraceOutput(_workflowEvents);
            ActivityStatus = "等待 Brain 路由";
        }

        private async Task HandleWorkflowStreamEventAsync(UiCoarseVisionWorkflowStreamEvent evt)
        {
            await RunOnUiAsync(() =>
            {
                var snapshot = UiWorkflowTraceFormatter.CloneEvent(evt);
                if (snapshot != null)
                {
                    UpdateCurrentWorkflowTracking(snapshot);
                    if (_workflowEvents.Count >= MaxWorkflowEventLogEntries)
                        _workflowEvents.RemoveAt(0);
                    _workflowEvents.Add(snapshot);
                    LatestResponseJson = BuildWorkflowLiveTraceOutput(_workflowEvents);
                    var statusLine = UiWorkflowTraceFormatter.BuildWorkflowStatusLine(snapshot);
                    if (!string.IsNullOrWhiteSpace(statusLine))
                        ActivityStatus = statusLine;
                }
            }).ConfigureAwait(false);
        }

        private async Task HandleRouteDispatchAsync(UiTaskRouteDispatchInfo info)
        {
            await RunOnUiAsync(() =>
            {
                if (info == null)
                    return;

                if (!string.IsNullOrWhiteSpace(info.RouteTraceId))
                    _currentRouteTraceId = info.RouteTraceId.Trim();
                _currentExecutorApiKey = info.ExecutorApiKey ?? string.Empty;
                RaiseTrackingChanged();

                var executorSummary = string.Join(" | ", new[]
                {
                    string.IsNullOrWhiteSpace(info.ExecutorKey) ? string.Empty : "executor=" + info.ExecutorKey.Trim(),
                    string.IsNullOrWhiteSpace(info.CommandCatalogMode) ? string.Empty : "catalog=" + info.CommandCatalogMode.Trim()
                }.Where(x => !string.IsNullOrWhiteSpace(x)));

                if (!string.IsNullOrWhiteSpace(executorSummary))
                    TaskStatus = "Brain 已完成路由，正在启动 executor：" + executorSummary;
            }).ConfigureAwait(false);
        }

        private static string BuildRouteWorkflowOutput(
            UiTaskRouteResult result,
            IEnumerable<UiCoarseVisionWorkflowStreamEvent> events,
            string resultLabel = null)
        {
            var eventList = events == null
                ? new List<UiCoarseVisionWorkflowStreamEvent>()
                : events.Where(x => x != null).ToList();
            var roundLines = UiWorkflowTraceFormatter.BuildWorkflowRoundLines(eventList);
            var executorResult = result?.ExecutorResult;
            var sb = new StringBuilder();
            sb.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine("阶段：" + ResolveRouteStageTitle(result?.BrainDryRunEnabled == true));
            sb.AppendLine("结果：" + (string.IsNullOrWhiteSpace(resultLabel)
                ? (result?.Succeeded == true ? "成功" : "失败")
                : resultLabel));
            sb.AppendLine();
            sb.AppendLine("摘要");
            sb.AppendLine("----------------------------------------");
            AppendKeyValue(sb, "RouteTraceId", result?.RouteTraceId, showIfEmpty: true);
            AppendKeyValue(sb, "执行模式", result?.BrainDryRunEnabled == true ? "Brain Dry Run" : "正常联调", showIfEmpty: true);
            AppendKeyValue(sb, "Brain 决策", result?.BrainResult?.Decision, showIfEmpty: true);
            AppendKeyValue(sb, "Brain 回复", result?.FinalReply, showIfEmpty: true);
            AppendKeyValue(sb, "Brain 原因", result?.BrainResult?.Reason, showIfEmpty: true);
            AppendKeyValue(sb, "Brain WorkflowRunId", result?.BrainResult?.WorkflowRunId, showIfEmpty: true);
            AppendKeyValue(sb, "Brain TaskId", result?.BrainResult?.TaskId, showIfEmpty: true);
            AppendKeyValue(sb, "ExecutorKey", result?.ExecutorDescriptor?.Key, showIfEmpty: true);
            AppendKeyValue(sb, "ExecutorGoal", executorResult?.Goal ?? result?.BrainResult?.ExecutorGoal, showIfEmpty: true);
            AppendKeyValue(sb, "CommandCatalogMode", executorResult?.CommandCatalogMode ?? result?.ResolvedCommandCatalogMode ?? result?.BrainResult?.CommandCatalogMode, showIfEmpty: true);
            AppendKeyValue(sb, "CommandCatalogVersion", UiWorkflowTraceFormatter.TryExtractCommandCatalogVersion(executorResult?.CommandCatalogJson), showIfEmpty: true);
            AppendKeyValue(sb, "执行器跳过", result?.ExecutorSkipped == true ? "是" : "否", showIfEmpty: true);
            AppendKeyValue(sb, "跳过说明", result?.ExecutorSkipReason, showIfEmpty: true);
            AppendKeyValue(sb, "ExecutorWorkflowRunId", executorResult?.WorkflowRunId, showIfEmpty: true);
            AppendKeyValue(sb, "ExecutorTaskId", executorResult?.TaskId, showIfEmpty: true);
            AppendKeyValue(sb, "任务结果", UiWorkflowTraceFormatter.TryExtractWorkflowSummary(executorResult), showIfEmpty: true);
            AppendKeyValue(sb, "最后一步结果", UiWorkflowTraceFormatter.TryExtractLastResultSummary(executorResult), showIfEmpty: true);
            AppendKeyValue(sb, "错误信息", result?.ErrorMessage ?? executorResult?.ErrorMessage, showIfEmpty: true);

            if (roundLines.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("每轮判定");
                sb.AppendLine("----------------------------------------");
                foreach (var line in roundLines)
                    sb.AppendLine(line);
            }

            return sb.ToString().TrimEnd();
        }

        private static string BuildWorkflowLiveTraceOutput(IEnumerable<UiCoarseVisionWorkflowStreamEvent> events)
        {
            var eventList = events == null
                ? new List<UiCoarseVisionWorkflowStreamEvent>()
                : events.Where(x => x != null).ToList();
            var roundLines = UiWorkflowTraceFormatter.BuildWorkflowRoundLines(eventList);
            var sb = new StringBuilder();
            sb.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine("阶段：本地路由层（实时）");
            sb.AppendLine("结果：执行中");
            sb.AppendLine();
            sb.AppendLine("每轮判定");
            sb.AppendLine("----------------------------------------");

            if (roundLines.Count > 0)
            {
                foreach (var line in roundLines)
                    sb.AppendLine(line);
            }
            else
            {
                sb.AppendLine("等待 Brain 路由...");
            }

            return sb.ToString().TrimEnd();
        }

        private string BuildStoppedRouteOutput()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine("阶段：" + ResolveRouteStageTitle(BrainDryRunEnabled));
            sb.AppendLine("结果：已停止");
            sb.AppendLine();
            sb.AppendLine("摘要");
            sb.AppendLine("----------------------------------------");
            AppendKeyValue(sb, "RouteTraceId", _currentRouteTraceId, showIfEmpty: true);
            AppendKeyValue(sb, "执行模式", BrainDryRunEnabled ? "Brain Dry Run" : "正常联调", showIfEmpty: true);
            AppendKeyValue(sb, "Goal", TaskGoal, showIfEmpty: true);
            AppendKeyValue(sb, "ExecutorWorkflowRunId", _currentWorkflowRunId, showIfEmpty: true);
            AppendKeyValue(sb, "ExecutorTaskId", _currentWorkflowTaskId, showIfEmpty: true);
            AppendKeyValue(sb, "说明", "用户手动停止了当前任务。", showIfEmpty: true);
            return sb.ToString().TrimEnd();
        }

        private static string ResolveRouteStageTitle(bool brainDryRunEnabled)
        {
            return brainDryRunEnabled
                ? "本地路由层（Brain Dry Run）"
                : "本地路由层（Brain -> Executor）";
        }

        private static Task RunOnUiAsync(Action action)
        {
            if (action == null)
                return Task.CompletedTask;

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                action();
                return Task.CompletedTask;
            }

            return dispatcher.InvokeAsync(action).Task;
        }

        private static string BuildExceptionOutput(string stage, string message)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"阶段：{stage}");
            sb.AppendLine("结果：发生异常");
            sb.AppendLine();
            sb.AppendLine("摘要");
            sb.AppendLine("----------------------------------------");
            sb.AppendLine("异常信息：" + NormalizeSingleLine(message));
            return sb.ToString().TrimEnd();
        }

        private static void AppendKeyValue(StringBuilder sb, string label, string value, bool showIfEmpty = false)
        {
            var normalized = NormalizeSingleLine(value);
            if (!showIfEmpty && string.IsNullOrWhiteSpace(normalized))
                return;

            sb.AppendLine(label + "：" + SafeText(normalized));
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

        private static bool TryParseDelaySeconds(string raw, int maxSeconds, out int seconds, out string error)
        {
            seconds = 0;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(raw))
                return true;

            if (!int.TryParse(raw.Trim(), out var parsed))
            {
                error = "延迟触发秒数必须是整数。";
                return false;
            }

            if (parsed < 0 || parsed > maxSeconds)
            {
                error = $"延迟触发秒数需在 0 到 {maxSeconds} 之间。";
                return false;
            }

            seconds = parsed;
            return true;
        }

        private void UpdateCurrentWorkflowTracking(UiCoarseVisionWorkflowStreamEvent evt)
        {
            if (evt == null)
                return;

            if (!string.IsNullOrWhiteSpace(evt.WorkflowRunId))
                _currentWorkflowRunId = evt.WorkflowRunId.Trim();
            if (!string.IsNullOrWhiteSpace(evt.TaskId))
                _currentWorkflowTaskId = evt.TaskId.Trim();
            RaiseTrackingChanged();
        }

        private void UpdateCurrentWorkflowTracking(UiCoarseVisionWorkflowResult result)
        {
            if (result == null)
                return;

            if (!string.IsNullOrWhiteSpace(result.WorkflowRunId))
                _currentWorkflowRunId = result.WorkflowRunId.Trim();
            if (!string.IsNullOrWhiteSpace(result.TaskId))
                _currentWorkflowTaskId = result.TaskId.Trim();
            RaiseTrackingChanged();
        }

        private void UpdateCurrentWorkflowTracking(BrainWorkflowResult result)
        {
            if (result == null)
                return;

            if (!string.IsNullOrWhiteSpace(result.WorkflowRunId))
                _currentWorkflowRunId = result.WorkflowRunId.Trim();
            if (!string.IsNullOrWhiteSpace(result.TaskId))
                _currentWorkflowTaskId = result.TaskId.Trim();
            RaiseTrackingChanged();
        }

        private void UpdateCurrentTracking(UiTaskRouteResult result)
        {
            if (result == null)
                return;

            if (!string.IsNullOrWhiteSpace(result.RouteTraceId))
                _currentRouteTraceId = result.RouteTraceId.Trim();
            _currentExecutorApiKey = result.ExecutorDescriptor?.ApiKey ?? string.Empty;

            UpdateCurrentWorkflowTracking(result.BrainResult);
            UpdateCurrentWorkflowTracking(result.ExecutorResult);
            RaiseTrackingChanged();
        }

        private void ResetCurrentTracking()
        {
            _currentRouteTraceId = string.Empty;
            _currentExecutorApiKey = string.Empty;
            _currentWorkflowRunId = string.Empty;
            _currentWorkflowTaskId = string.Empty;
            RaiseTrackingChanged();
        }

        private void RaiseTrackingChanged()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentRouteTraceId)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentWorkflowRunId)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentWorkflowTaskId)));
        }

        private void SetTaskRunning(bool value)
        {
            if (_taskRunning == value)
                return;

            _taskRunning = value;
            RaiseTaskCommandStateChanged();
        }

        private void SetStopRequested(bool value)
        {
            if (_stopRequested == value)
                return;

            _stopRequested = value;
            RaiseTaskCommandStateChanged();
        }

        private void RaiseTaskCommandStateChanged()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanStartTask)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanStopTask)));
        }

        private void ScheduleForcedStreamingCancellation()
        {
            var cts = _taskRunCts;
            if (cts == null)
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(StopRequestGracePeriodMs).ConfigureAwait(false);
                }
                catch
                {
                    return;
                }

                if (!_taskRunning || !_stopRequested || !ReferenceEquals(_taskRunCts, cts))
                    return;

                await RunOnUiAsync(() =>
                {
                    if (!_taskRunning || !_stopRequested || !ReferenceEquals(_taskRunCts, cts))
                        return;

                    TaskStatus = "停止请求已提交，但平台未在 2 秒内结束任务，正在中断本地流式连接。";
                    MainWindow.PostProgramInfo("[UiTaskFlow] 停止请求已提交，但平台 2 秒内未结束任务，改为中断本地流式连接。", "warn");
                    try
                    {
                        cts.Cancel();
                    }
                    catch
                    {
                        // 取消失败不阻断 UI 收尾
                    }
                }).ConfigureAwait(false);
            });
        }

        private void UiRuntimeView_Unloaded(object sender, RoutedEventArgs e)
        {
            try
            {
                _taskRunCts?.Cancel();
            }
            catch
            {
                // 卸载时仅做本地收尾
            }

            _taskRunCts?.Dispose();
            _taskRunCts = null;
        }

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }

        private static void CopyToClipboard(string value, string label)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), "暂无日志", StringComparison.Ordinal))
                {
                    MainWindow.PostProgramInfo($"[UiTaskFlow] 当前没有可复制的{label}。", "warn");
                    return;
                }

                Clipboard.SetText(value);
                MainWindow.PostProgramInfo($"[UiTaskFlow] 已复制{label}。", "info");
            }
            catch (Exception ex)
            {
                MainWindow.PostProgramInfo($"[UiTaskFlow] 复制{label}失败：{ex.Message}", "error");
            }
        }

    }
}

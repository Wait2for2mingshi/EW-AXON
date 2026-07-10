using EW_Assistant.Views;
using EW_Assistant.Views.Inventory;
using EW_Assistant.Views.Reports;
using EW_Assistant.Settings;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Path = System.IO.Path;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using EW_Assistant.Services.Reports;
using EW_Assistant.Warnings;
using System.Windows.Threading;
using EW_Assistant.Diagnostics;
using EW_Assistant.Services;
using EW_Assistant.Services.Warnings;

namespace EW_Assistant
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        /// <summary>全局唯一的主窗体实例，便于跨线程日志调用。</summary>
        public static MainWindow Instance { get; private set; }

        /// <summary>信息流列表的数据源，绑定到 UI。</summary>
        public ObservableCollection<ProgramInfoItem> InfoItems { get; } = new ObservableCollection<ProgramInfoItem>();
        /// <summary>AUTO 演示卡阶段列表。</summary>
        public ObservableCollection<AutoStageViewItem> AutoStageItems { get; } = new ObservableCollection<AutoStageViewItem>();
        /// <summary>AUTO 演示卡最近进展列表。</summary>
        public ObservableCollection<AutoFeedViewItem> AutoFeedItems { get; } = new ObservableCollection<AutoFeedViewItem>();

        public bool IsAutoDemoVisible
        {
            get => _isAutoDemoVisible;
            set
            {
                if (_isAutoDemoVisible == value)
                    return;

                _isAutoDemoVisible = value;
                SafeNotifyPropertyChanged(nameof(IsAutoDemoVisible));
            }
        }
        private bool _isAutoDemoVisible = true;

        public string AutoDemoStatusText
        {
            get => _autoDemoStatusText;
            set
            {
                var next = value ?? string.Empty;
                if (string.Equals(_autoDemoStatusText, next, StringComparison.Ordinal))
                    return;

                _autoDemoStatusText = next;
                SafeNotifyPropertyChanged(nameof(AutoDemoStatusText));
            }
        }
        private string _autoDemoStatusText = L("待命中");

        public string AutoDemoStatusLevel
        {
            get => _autoDemoStatusLevel;
            set
            {
                var next = value ?? string.Empty;
                if (string.Equals(_autoDemoStatusLevel, next, StringComparison.Ordinal))
                    return;

                _autoDemoStatusLevel = next;
                SafeNotifyPropertyChanged(nameof(AutoDemoStatusLevel));
            }
        }
        private string _autoDemoStatusLevel = "pending";

        public string AutoDemoHeadline
        {
            get => _autoDemoHeadline;
            set
            {
                var next = value ?? string.Empty;
                if (string.Equals(_autoDemoHeadline, next, StringComparison.Ordinal))
                    return;

                _autoDemoHeadline = next;
                SafeNotifyPropertyChanged(nameof(AutoDemoHeadline));
            }
        }
        private string _autoDemoHeadline = L("等待报警触发");

        public string AutoDemoSecondaryText
        {
            get => _autoDemoSecondaryText;
            set
            {
                var next = value ?? string.Empty;
                if (string.Equals(_autoDemoSecondaryText, next, StringComparison.Ordinal))
                    return;

                _autoDemoSecondaryText = next;
                SafeNotifyPropertyChanged(nameof(AutoDemoSecondaryText));
            }
        }
        private string _autoDemoSecondaryText = string.Empty;

        public string AutoDemoMachineText
        {
            get => _autoDemoMachineText;
            set
            {
                var next = value ?? string.Empty;
                if (string.Equals(_autoDemoMachineText, next, StringComparison.Ordinal))
                    return;

                _autoDemoMachineText = next;
                SafeNotifyPropertyChanged(nameof(AutoDemoMachineText));
            }
        }
        private string _autoDemoMachineText = "-";

        public string AutoDemoTraceText
        {
            get => _autoDemoTraceText;
            set
            {
                var next = value ?? string.Empty;
                if (string.Equals(_autoDemoTraceText, next, StringComparison.Ordinal))
                    return;

                _autoDemoTraceText = next;
                SafeNotifyPropertyChanged(nameof(AutoDemoTraceText));
            }
        }
        private string _autoDemoTraceText = "-";

        public string AutoDemoElapsedText
        {
            get => _autoDemoElapsedText;
            set
            {
                var next = value ?? string.Empty;
                if (string.Equals(_autoDemoElapsedText, next, StringComparison.Ordinal))
                    return;

                _autoDemoElapsedText = next;
                SafeNotifyPropertyChanged(nameof(AutoDemoElapsedText));
            }
        }
        private string _autoDemoElapsedText = L("0.0 秒");

        public string AutoDemoToolCountText
        {
            get => _autoDemoToolCountText;
            set
            {
                var next = value ?? string.Empty;
                if (string.Equals(_autoDemoToolCountText, next, StringComparison.Ordinal))
                    return;

                _autoDemoToolCountText = next;
                SafeNotifyPropertyChanged(nameof(AutoDemoToolCountText));
            }
        }
        private string _autoDemoToolCountText = string.Format(L("{0} 次"), 0);

        public string AutoDemoRunText
        {
            get => _autoDemoRunText;
            set
            {
                var next = value ?? string.Empty;
                if (string.Equals(_autoDemoRunText, next, StringComparison.Ordinal))
                    return;

                _autoDemoRunText = next;
                SafeNotifyPropertyChanged(nameof(AutoDemoRunText));
            }
        }
        private string _autoDemoRunText = L("等待分配");

        public string AutoDemoTaskText
        {
            get => _autoDemoTaskText;
            set
            {
                var next = value ?? string.Empty;
                if (string.Equals(_autoDemoTaskText, next, StringComparison.Ordinal))
                    return;

                _autoDemoTaskText = next;
                SafeNotifyPropertyChanged(nameof(AutoDemoTaskText));
            }
        }
        private string _autoDemoTaskText = L("等待分配");

        /// <summary>底部状态栏文本。</summary>
        public string StatusText
        {
            get => _statusText;
            set
            {
                _statusText = value;
                SafeNotifyPropertyChanged(nameof(StatusText));
            }
        }
        private string _statusText = L("就绪");

        /// <summary>顶部标题栏中间文案。</summary>
        public string HeaderTitleText
        {
            get => _headerTitleText;
            set
            {
                var next = value ?? string.Empty;
                if (string.Equals(_headerTitleText, next, StringComparison.Ordinal))
                    return;
                _headerTitleText = next;
                SafeNotifyPropertyChanged(nameof(HeaderTitleText));
            }
        }
        private string _headerTitleText = "T66-TCT Program Powered By EW AI";

        /// <summary>当前导航标题。</summary>
        public string CurrentPageTitle
        {
            get => _currentPageTitle;
            set
            {
                if (string.Equals(_currentPageTitle, value, StringComparison.Ordinal))
                    return;
                _currentPageTitle = value ?? string.Empty;
                SafeNotifyPropertyChanged(nameof(CurrentPageTitle));
            }
        }
        private string _currentPageTitle = L("总览");
        private const string ExceptionDiagnosisPageLabel = "异常诊断";
        private const string AgentControlPageLabel = "智能体控制";
        private const string AutoAuditStateRoot = @"D:\DataAI";
        private const int AutoFeedMaxItems = 3;
        private const int SwRestore = 9;
        private const int SwShowMaximized = 3;
        private readonly DispatcherTimer _autoDemoTimer = new DispatcherTimer();
        private readonly List<AutoStageState> _autoStageStates = new List<AutoStageState>();
        private readonly HashSet<string> _autoToolCallKeys = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, AutoPendingTrace> _autoPendingTraces = new Dictionary<string, AutoPendingTrace>(StringComparer.OrdinalIgnoreCase);
        private string _currentAutoTraceId = string.Empty;
        private DateTime? _currentAutoStartedAt;
        private DateTime? _currentAutoFinishedAt;

        public double CurrentPageTitleFontSize => UiLanguageService.IsEnglish(_activeUiLanguage) ? 24d : 28d;

        public bool IsAgentControlEntryEnabled
        {
            get => _isAgentControlEntryEnabled;
            private set
            {
                if (_isAgentControlEntryEnabled == value)
                    return;

                _isAgentControlEntryEnabled = value;
                SafeNotifyPropertyChanged(nameof(IsAgentControlEntryEnabled));
            }
        }
        private bool _isAgentControlEntryEnabled = true;

        // 主导航映射：标签 -> 对应视图的工厂方法
        private readonly Dictionary<string, Func<UIElement>> _routes = new Dictionary<string, Func<UIElement>>()
        {
            ["总览"] = () => new DashboardView() ,
            ["AI助手"] = () => new AIAssistantView(),
            ["AI文档"] = () => new DocumentAiView(),
            ["产能看板"] = () => new ProductionBoardView(),
            ["报警看板"] = () => new AlarmView(),
            [ExceptionDiagnosisPageLabel] = () => new ExceptionDiagnosisView(),
            ["性能监控"] = () => new PerformanceMonitorView(),
            ["报表中心"] = () => new ReportsCenterView(),
            ["预警中心"] = () => new WarningCenterView(),
            ["预防维护"] = () => new PreventiveMaintenanceView(),
            ["机台控制"] = () => new MachineControl(),
            [AgentControlPageLabel] = () => new AgentControlView(),
            ["库存管理"] = () => new InventoryView(),
            ["设置"] = () => new ConfigView(),
        };
        // 弱引用缓存页面：可复用最近页面，但不再把打开过的页面永久钉在进程里。
        private readonly Dictionary<string, WeakReference<UIElement>> _viewCache =
            new Dictionary<string, WeakReference<UIElement>>();
        private CancellationTokenSource _serverCts;
        private readonly Services.McpServerProcessHost _mcpHost = Services.McpServerProcessHost.Instance;
        private readonly ReportStorageService _reportStorage = new ReportStorageService();
        private readonly ReportGeneratorService _reportGenerator;
        private readonly ReportScheduler _reportScheduler;
        private readonly CancellationTokenSource _reportSchedulerCts = new CancellationTokenSource();
        private string _activeUiLanguage = UiLanguageService.Chinese;
        private const string DefaultHeaderTitle = "T66-TCT Program Powered By EW AI";
        private const int ShutdownWatchdogDelayMs = 8000;
        private int _shutdownStarted;
        public MainWindow()
        {
            InitializeComponent();
            Instance = this;
            DataContext = this;
            InitializeAutoDemoCard();
            var currentConfig = Services.ConfigService.Current ?? Services.ConfigService.Load();
            _activeUiLanguage = UiLanguageService.Normalize(currentConfig?.UiLanguage);
            SafeNotifyPropertyChanged(nameof(CurrentPageTitleFontSize));
            ApplyHeaderTitleFromConfig(currentConfig);
            ApplyAgentControlEntryState(currentConfig, emitInfoLog: false);
            Services.ConfigService.ConfigChanged += ConfigService_ConfigChanged;
            _reportGenerator = new ReportGeneratorService(_reportStorage, new LlmWorkflowClient());
            _reportScheduler = new ReportScheduler(_reportStorage, _reportGenerator);

            // 启动常驻 HTTP 服务与 MCP 辅助进程
            _serverCts = new CancellationTokenSource();
            var prefix = currentConfig?.WorkHttpServerPrefix;
            if (string.IsNullOrWhiteSpace(prefix))
            {
                prefix = Services.ConfigService.NormalizeWorkHttpServerPrefix(null);
            }
            _ = Net.WorkHttpServer.Instance.StartAsync(prefix, _serverCts.Token);
            _mcpHost.StartIfNeeded(LogMcpMessage);

            // 应用退出统一停止
            Application.Current.Exit += (_, __) =>
            {
                BeginApplicationShutdown();
            };

            // 预创建 AI 助手页以提前初始化全局实例，避免首次打开时延迟
            if (!TryGetCachedView("AI助手", out _))
            {
                var ai = new EW_Assistant.Views.AIAssistantView();  // 这一步会设置 GlobalInstance
                CacheView("AI助手", ai);
            }
            NavigateByContent("总览");
            Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var ioMapPath = Services.ConfigService.Current?.IoMapCsvPath;
                if (AlarmIoKnowledgeRepository.TryLoadFromIoMapPath(ioMapPath, out var message))
                {
                    PostProgramInfo(message, "ok");
                }
                else if (!string.IsNullOrWhiteSpace(message))
                {
                    PostProgramInfo(message, "warn");
                }
            }
            catch (Exception ex)
            {
                PostProgramInfo("读取报警知识库IO失败：" + ex.Message, "warn");
            }

            await InitializeReportsAsync();
            _ = StartReportSchedulerLoopAsync();
        }

        private void ConfigService_ConfigChanged(object sender, AppConfig cfg)
        {
            if (Dispatcher.CheckAccess())
            {
                ApplyHeaderTitleFromConfig(cfg);
                ApplyAgentControlEntryState(cfg, emitInfoLog: true);
                ApplyAutoNotificationState(cfg);
                if (_reportScheduler != null)
                {
                    _reportScheduler.ResetAutoGenerationSuspension();
                }
            }
            else
            {
                Dispatcher.Invoke(() =>
                {
                    ApplyHeaderTitleFromConfig(cfg);
                    ApplyAgentControlEntryState(cfg, emitInfoLog: true);
                    ApplyAutoNotificationState(cfg);
                    if (_reportScheduler != null)
                    {
                        _reportScheduler.ResetAutoGenerationSuspension();
                    }
                });
            }
        }

        private void ApplyHeaderTitleFromConfig(AppConfig cfg)
        {
            var title = cfg?.TitleBarText;
            HeaderTitleText = title == null ? DefaultHeaderTitle : title;
        }

        private void ApplyAgentControlEntryState(AppConfig cfg, bool emitInfoLog)
        {
            var enabled = Services.ConfigService.IsAgentControlModuleEnabled(cfg);
            var changed = IsAgentControlEntryEnabled != enabled;
            IsAgentControlEntryEnabled = enabled;

            if (!enabled)
            {
                if (NavAgentControlButton != null && NavAgentControlButton.IsChecked == true)
                {
                    if (NavDashboardButton != null)
                        NavDashboardButton.IsChecked = true;
                    else
                        NavigateByContent("总览");
                }
                else if (string.Equals(CurrentPageTitle, AgentControlPageLabel, StringComparison.Ordinal))
                {
                    NavigateByContent("总览");
                }
            }

            if (emitInfoLog && changed)
            {
                PostProgramInfo(
                    enabled ? "智能体控制模块已解冻，入口与相关能力恢复可用。" : "智能体控制模块已冻结，入口与相关能力已停用。",
                    enabled ? "info" : "warn");
            }
        }

        private void ApplyAutoNotificationState(AppConfig cfg)
        {
            if (!Services.ConfigService.IsAutoWindowsNotificationEnabled(cfg))
            {
                AutoWindowsNotificationService.Dispose();
            }
        }

        private async Task InitializeReportsAsync()
        {
            try
            {
                PostProgramInfo("正在初始化报表...", "info");
                await _reportScheduler.EnsureBasicReportsAsync();
                if (_reportScheduler.IsAutoGenerationSuspended)
                {
                    PostProgramInfo("报表初始化已停止，请查看前面的具体原因。", "warn");
                }
                else
                {
                    PostProgramInfo("报表初始化完成。", "ok");
                }
            }
            catch (OperationCanceledException)
            {
                PostProgramInfo("报表初始化已取消。", "warn");
            }
            catch (Exception ex)
            {
                PostProgramInfo("报表初始化失败：" + ex.Message, "warn");
            }
        }

        private async Task StartReportSchedulerLoopAsync()
        {
            var token = _reportSchedulerCts.Token;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await _reportScheduler.EnsureBasicReportsAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    PostProgramInfo("自动检查报表失败：" + ex.Message, "warn");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(30), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private void InitializeAutoDemoCard()
        {
            InitializeAutoStageStates();
            RefreshAutoStageItems();
            _autoDemoTimer.Interval = TimeSpan.FromMilliseconds(500);
            _autoDemoTimer.Tick += AutoDemoTimer_Tick;
            _autoDemoTimer.Start();
        }

        public static event Action<string, string, string> AutoTriggeredNotified;
        public static event Action<string> AutoAcceptedNotified;
        public static event Action<string, string> AutoRejectedNotified;
        public static event Action<string, string> AutoDispatchFailedNotified;
        public static event Action<string, string, string> AutoWorkflowStartedNotified;
        public static event Action<string, string, string> AutoNodeStartedNotified;
        public static event Action<string, string, string, double?, string> AutoNodeFinishedNotified;
        public static event Action<string, bool, string> AutoWorkflowFinishedNotified;

        public static void NotifyAutoTriggered(string traceId, string machineCode, string prompt)
        {
            DispatchToMainWindow(window => window.HandleAutoTriggered(traceId, machineCode, prompt));
            SafeRaiseAutoEvent(() => AutoTriggeredNotified?.Invoke(traceId, machineCode, prompt));
        }

        public static void NotifyAutoAccepted(string traceId)
        {
            DispatchToMainWindow(window => window.HandleAutoAccepted(traceId));
            SafeRaiseAutoEvent(() => AutoAcceptedNotified?.Invoke(traceId));
        }

        public static void NotifyAutoRejected(string traceId, string reason)
        {
            DispatchToMainWindow(window => window.HandleAutoRejected(traceId, reason));
            SafeRaiseAutoEvent(() => AutoRejectedNotified?.Invoke(traceId, reason));
        }

        public static void NotifyAutoDispatchFailed(string traceId, string message)
        {
            DispatchToMainWindow(window => window.HandleAutoDispatchFailed(traceId, message));
            SafeRaiseAutoEvent(() => AutoDispatchFailedNotified?.Invoke(traceId, message));
        }

        public static void NotifyAutoWorkflowStarted(string traceId, string workflowRunId, string taskId)
        {
            DispatchToMainWindow(window => window.HandleAutoWorkflowStarted(traceId, workflowRunId, taskId));
            SafeRaiseAutoEvent(() => AutoWorkflowStartedNotified?.Invoke(traceId, workflowRunId, taskId));
        }

        public static void NotifyAutoNodeStarted(string traceId, string title, string nodeType)
        {
            DispatchToMainWindow(window => window.HandleAutoNodeStarted(traceId, title, nodeType));
            SafeRaiseAutoEvent(() => AutoNodeStartedNotified?.Invoke(traceId, title, nodeType));
        }

        public static void NotifyAutoNodeFinished(string traceId, string title, string nodeType, double? elapsedSeconds = null, string previewText = null)
        {
            DispatchToMainWindow(window => window.HandleAutoNodeFinished(traceId, title, nodeType, elapsedSeconds, previewText));
            SafeRaiseAutoEvent(() => AutoNodeFinishedNotified?.Invoke(traceId, title, nodeType, elapsedSeconds, previewText));
        }

        public static void NotifyAutoWorkflowFinished(string traceId, bool succeeded, string finalText)
        {
            DispatchToMainWindow(window => window.HandleAutoWorkflowFinished(traceId, succeeded, finalText));
            SafeRaiseAutoEvent(() => AutoWorkflowFinishedNotified?.Invoke(traceId, succeeded, finalText));
        }

        private static void SafeRaiseAutoEvent(Action action)
        {
            try
            {
                action?.Invoke();
            }
            catch
            {
                // AUTO 通知扩散失败不影响主流程。
            }
        }

        private static void DispatchToMainWindow(Action<MainWindow> action)
        {
            var window = Instance;
            if (window == null || action == null)
                return;

            try
            {
                if (window.Dispatcher.HasShutdownStarted || window.Dispatcher.HasShutdownFinished)
                    return;

                if (window.Dispatcher.CheckAccess())
                    action(window);
                else
                    window.Dispatcher.BeginInvoke(new Action(() => action(window)));
            }
            catch
            {
                // 演示卡更新失败不影响主流程
            }
        }

        private void HandleAutoTriggered(string traceId, string machineCode, string prompt)
        {
            if (string.IsNullOrWhiteSpace(traceId))
                return;

            var safeTraceId = traceId.Trim();
            var pending = new AutoPendingTrace
            {
                TraceId = safeTraceId,
                MachineCode = machineCode ?? string.Empty,
                Prompt = prompt ?? string.Empty,
                TriggeredAt = DateTime.Now
            };
            _autoPendingTraces[safeTraceId] = pending;

            if (!CanReplaceCurrentAutoCard(safeTraceId))
                return;

            ActivateAutoCard(pending);
        }

        private void HandleAutoAccepted(string traceId)
        {
            if (!IsCurrentAutoTrace(traceId))
            {
                if (!TryActivatePendingAutoTrace(traceId))
                    return;
            }

            AutoDemoStatusText = L("流程启动中");
            AutoDemoStatusLevel = "running";
            if (string.IsNullOrWhiteSpace(AutoDemoHeadline))
                AutoDemoHeadline = L("已受理");
            AutoDemoSecondaryText = string.Empty;
            SetAutoStageStatus("accepted", "done", L("本地 WorkHttpServer 已受理请求。"));
            AddAutoFeed(L("本地已受理"), L("本地服务 accepted，等待 workflow_started。"), "ok");
            RemovePendingAutoTrace(traceId);
        }

        private void HandleAutoRejected(string traceId, string reason)
        {
            RemovePendingAutoTrace(traceId);
            if (!IsCurrentAutoTrace(traceId))
                return;

            var displayReason = FormatAutoFailureReason(reason);
            _currentAutoFinishedAt = DateTime.Now;
            AutoDemoStatusText = L("未受理");
            AutoDemoStatusLevel = "warn";
            AutoDemoHeadline = ShortenSingleLine(displayReason, 80);
            AutoDemoSecondaryText = string.Empty;
            SetAutoStageStatus("accepted", "error", displayReason);
            SetAutoStageStatus("analysis", "pending", L("workflow 未启动。"));
            SetAutoStageStatus("result", "error", L("本次请求未被执行。"));
            UpdateAutoElapsedText();
            AddAutoFeed(L("请求未受理"), displayReason, "warn");
        }

        private void HandleAutoDispatchFailed(string traceId, string message)
        {
            RemovePendingAutoTrace(traceId);
            if (!IsCurrentAutoTrace(traceId))
                return;

            var preview = ShortenSingleLine(message, 120);
            _currentAutoFinishedAt = DateTime.Now;
            AutoDemoStatusText = L("失败");
            AutoDemoStatusLevel = "error";
            AutoDemoHeadline = string.IsNullOrWhiteSpace(preview) ? L("启动失败") : preview;
            AutoDemoSecondaryText = string.Empty;
            SetAutoStageStatus("analysis", "error", preview);
            SetAutoStageStatus("result", "error", L("workflow 未返回有效结果。"));
            FinalizeAutoMcpStageIfNeeded();
            UpdateAutoElapsedText();
            AddAutoFeed(L("启动失败"), preview, "error");
        }

        private void HandleAutoWorkflowStarted(string traceId, string workflowRunId, string taskId)
        {
            if (!IsCurrentAutoTrace(traceId))
                return;

            AutoDemoRunText = SafeAutoMetaValue(workflowRunId, L("等待分配"));
            AutoDemoTaskText = SafeAutoMetaValue(taskId, L("等待分配"));
            AutoDemoStatusText = L("AI分析中");
            AutoDemoStatusLevel = "running";
            if (string.IsNullOrWhiteSpace(AutoDemoHeadline))
                AutoDemoHeadline = L("开始分析");
            AutoDemoSecondaryText = string.Empty;
            SetAutoStageStatus("accepted", "done", L("请求已受理并完成 workflow 调度。"));
            SetAutoStageStatus("analysis", "running", L("workflow_started，正在分析报警上下文。"));
            AddAutoFeed(L("workflow 已启动"), BuildAutoRunSummary(workflowRunId, taskId), "info");
        }

        private void HandleAutoNodeStarted(string traceId, string title, string nodeType)
        {
            if (!IsCurrentAutoTrace(traceId))
                return;

            var friendlyTitle = FormatAutoNodeTitle(title, nodeType);
            AutoDemoHeadline = friendlyTitle;
            AutoDemoSecondaryText = string.Empty;

            if (IsToolNode(nodeType, title))
            {
                AutoDemoStatusText = L("动作执行中");
                SetAutoStageStatus("mcp", "running", L("正在准备执行：") + friendlyTitle);
            }
            else if (IsOutputNode(nodeType, title))
            {
                AutoDemoStatusText = L("结果整理中");
                SetAutoStageStatus("result", "running", L("正在汇总最终回答。"));
            }
            else
            {
                AutoDemoStatusText = L("AI分析中");
                SetAutoStageStatus("analysis", "running", L("当前节点：") + friendlyTitle);
            }

            AddAutoFeed(L("进入 ") + friendlyTitle, IsToolNode(nodeType, title) ? L("动作阶段") : L("分析阶段"), "info");
        }

        private void HandleAutoNodeFinished(string traceId, string title, string nodeType, double? elapsedSeconds, string previewText)
        {
            if (!IsCurrentAutoTrace(traceId))
                return;

            var friendlyTitle = FormatAutoNodeTitle(title, nodeType);
            var elapsedText = elapsedSeconds.HasValue ? string.Format(L("（{0:0.##} 秒）"), elapsedSeconds.Value) : string.Empty;
            var preview = ShortenSingleLine(previewText, 80);

            if (IsToolNode(nodeType, title))
            {
                AutoDemoStatusText = L("动作执行中");
                SetAutoStageStatus("mcp", "running", L("工具节点已返回：") + friendlyTitle);
                AutoDemoHeadline = friendlyTitle;
            }
            else if (IsOutputNode(nodeType, title))
            {
                AutoDemoStatusText = L("结果整理中");
                SetAutoStageStatus("result", "running", string.IsNullOrWhiteSpace(preview) ? L("正在整理最终回答。") : preview);
                AutoDemoHeadline = string.IsNullOrWhiteSpace(preview) ? friendlyTitle : preview;
            }
            else
            {
                AutoDemoStatusText = L("AI分析中");
                SetAutoStageStatus("analysis", "running", L("最近完成：") + friendlyTitle);
                AutoDemoHeadline = friendlyTitle;
            }

            AddAutoFeed(L("完成 ") + friendlyTitle + elapsedText, string.IsNullOrWhiteSpace(preview) ? L("节点已完成") : preview, "ok");
        }

        private void HandleAutoWorkflowFinished(string traceId, bool succeeded, string finalText)
        {
            RemovePendingAutoTrace(traceId);
            if (!IsCurrentAutoTrace(traceId))
                return;

            var preview = ShortenSingleLine(finalText, 120);
            _currentAutoFinishedAt = DateTime.Now;
            AutoDemoStatusText = succeeded ? L("完成") : L("失败");
            AutoDemoStatusLevel = succeeded ? "ok" : "error";
            AutoDemoHeadline = succeeded
                ? L("结果已返回")
                : (string.IsNullOrWhiteSpace(preview) ? L("执行失败") : preview);
            AutoDemoSecondaryText = string.Empty;
            SetAutoStageStatus("analysis", succeeded ? "done" : "error", succeeded ? L("AI 分析阶段已结束。") : L("分析阶段异常结束。"));
            SetAutoStageStatus("result", succeeded ? "done" : "error", string.IsNullOrWhiteSpace(preview)
                ? (succeeded ? L("已生成最终回答。") : L("未生成有效回答。"))
                : preview);
            FinalizeAutoMcpStageIfNeeded();
            UpdateAutoElapsedText();
            AddAutoFeed(succeeded ? L("结果已返回") : L("流程失败"), string.IsNullOrWhiteSpace(preview) ? L("请查看 AI 最终回答。") : preview, succeeded ? "ok" : "error");
        }

        private void AutoDemoTimer_Tick(object sender, EventArgs e)
        {
            UpdateAutoElapsedText();
            SyncAutoDemoWithAuditState();
        }

        private void SyncAutoDemoWithAuditState()
        {
            if (string.IsNullOrWhiteSpace(_currentAutoTraceId))
                return;

            var state = ReadAutoAuditState(_currentAutoTraceId);
            if (state == null)
                return;

            var traceId = state.Value<string>("TraceId");
            if (!IsCurrentAutoTrace(traceId))
                return;

            var startedAt = state.Value<DateTime?>("StartedAt");
            if (startedAt.HasValue)
            {
                if (!_currentAutoStartedAt.HasValue || startedAt.Value < _currentAutoStartedAt.Value)
                    _currentAutoStartedAt = startedAt.Value;
            }

            var finalizedAt = state.Value<DateTime?>("FinalizedAt");
            if (finalizedAt.HasValue && finalizedAt.Value != default)
            {
                _currentAutoFinishedAt = finalizedAt.Value;
            }

            AutoDemoRunText = SafeAutoMetaValue(state.Value<string>("WorkflowRunId"), AutoDemoRunText);
            AutoDemoTaskText = SafeAutoMetaValue(state.Value<string>("TaskId"), AutoDemoTaskText);

            var calls = state["McpToolCalls"] as JArray;
            var clearAlarmCalled = state.Value<bool?>("ClearMachineAlarmsCalled") ?? false;
            ApplyAutoAuditToolCalls(calls, clearAlarmCalled);

            var status = (state.Value<string>("Status") ?? string.Empty).Trim().ToLowerInvariant();
            var finalReply = state.Value<string>("FinalReply");
            if (!_currentAutoFinishedAt.HasValue && IsFinalAutoAuditStatus(status))
            {
                if (string.Equals(status, "rejected", StringComparison.OrdinalIgnoreCase))
                {
                    HandleAutoRejected(traceId, finalReply);
                    return;
                }

                if (string.Equals(status, "dispatch_failed", StringComparison.OrdinalIgnoreCase))
                {
                    HandleAutoDispatchFailed(traceId, finalReply);
                    return;
                }

                HandleAutoWorkflowFinished(traceId, string.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase), finalReply);
                return;
            }

            if ((_currentAutoFinishedAt.HasValue || IsFinalAutoAuditStatus(status)) && !string.IsNullOrWhiteSpace(finalReply))
            {
                SetAutoStageStatus("result",
                    string.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase) ? "done" : "error",
                    ShortenSingleLine(finalReply, 120));
            }
        }

        private void ApplyAutoAuditToolCalls(JArray calls, bool clearAlarmCalled)
        {
            var count = calls?.Count ?? 0;
            AutoDemoToolCountText = string.Format(L("{0} 次"), count);

            if (count == 0)
            {
                if (clearAlarmCalled)
                {
                    SetAutoStageStatus("mcp", _currentAutoFinishedAt.HasValue ? "done" : "running", L("已触发 ClearMachineAlarms。"));
                }
                else if (_currentAutoFinishedAt.HasValue && string.Equals(GetAutoStageStatus("mcp"), "pending", StringComparison.OrdinalIgnoreCase))
                {
                    SetAutoStageStatus("mcp", "done", L("本次未触发任何 MCP 动作。"));
                }

                return;
            }

            var lastTitle = string.Empty;
            foreach (var token in calls)
            {
                var call = token as JObject;
                if (call == null)
                    continue;

                var key = BuildAutoToolCallKey(call);
                if (!_autoToolCallKeys.Add(key))
                    continue;

                var title = BuildAutoToolFeedTitle(call);
                var detail = BuildAutoToolFeedDetail(call);
                lastTitle = title;
                AddAutoFeed(title, detail, HasAutoToolError(call) ? "error" : "ok");
            }

            var lastCall = calls[calls.Count - 1] as JObject;
            if (string.IsNullOrWhiteSpace(lastTitle) && lastCall != null)
            {
                lastTitle = BuildAutoToolFeedTitle(lastCall);
            }

            var summary = count == 1
                ? L("已执行 1 个 MCP 动作。")
                : string.Format(L("已执行 {0} 个 MCP 动作。"), count);
            if (!string.IsNullOrWhiteSpace(lastTitle))
                summary += L(" 最近：") + lastTitle;

            SetAutoStageStatus("mcp", _currentAutoFinishedAt.HasValue ? "done" : "running", summary);
        }

        private static JObject ReadAutoAuditState(string traceId)
        {
            try
            {
                var safeTraceId = (traceId ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(safeTraceId))
                    return null;

                var path = Path.Combine(AutoAuditStateRoot, "auto_clear_alarm_states", safeTraceId + ".json");
                if (!File.Exists(path))
                {
                    path = Path.Combine(AutoAuditStateRoot, "auto_clear_alarm_state.json");
                    if (!File.Exists(path))
                        return null;
                }

                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs, Encoding.UTF8, true);
                var json = reader.ReadToEnd();
                return string.IsNullOrWhiteSpace(json) ? null : JObject.Parse(json);
            }
            catch
            {
                return null;
            }
        }

        private void InitializeAutoStageStates()
        {
            _autoStageStates.Clear();
            _autoStageStates.Add(new AutoStageState("trigger", "1", L("触发")));
            _autoStageStates.Add(new AutoStageState("accepted", "2", L("受理")));
            _autoStageStates.Add(new AutoStageState("analysis", "3", L("分析")));
            _autoStageStates.Add(new AutoStageState("mcp", "4", L("执行")));
            _autoStageStates.Add(new AutoStageState("result", "5", L("完成")));
            RefreshAutoStageItems();
        }

        private void SetAutoStageStatus(string key, string status, string summary)
        {
            var stage = _autoStageStates.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
            if (stage == null)
                return;

            stage.Status = string.IsNullOrWhiteSpace(status) ? "pending" : status.Trim().ToLowerInvariant();
            stage.Summary = summary ?? string.Empty;
            RefreshAutoStageItems();
        }

        private string GetAutoStageStatus(string key)
        {
            var stage = _autoStageStates.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
            return stage?.Status ?? "pending";
        }

        private void FinalizeAutoMcpStageIfNeeded()
        {
            var currentStatus = GetAutoStageStatus("mcp");
            if (string.Equals(currentStatus, "pending", StringComparison.OrdinalIgnoreCase))
            {
                SetAutoStageStatus("mcp", "done", L("本次未触发任何 MCP 动作。"));
                return;
            }

            if (string.Equals(currentStatus, "running", StringComparison.OrdinalIgnoreCase))
            {
                var stage = _autoStageStates.FirstOrDefault(x => string.Equals(x.Key, "mcp", StringComparison.OrdinalIgnoreCase));
                SetAutoStageStatus("mcp", "done", stage?.Summary ?? L("MCP 动作执行结束。"));
            }
        }

        private void ActivateAutoCard(AutoPendingTrace pending)
        {
            if (pending == null || string.IsNullOrWhiteSpace(pending.TraceId))
                return;

            _currentAutoTraceId = pending.TraceId;
            _currentAutoStartedAt = pending.TriggeredAt == default(DateTime) ? DateTime.Now : pending.TriggeredAt;
            _currentAutoFinishedAt = null;
            _autoToolCallKeys.Clear();
            AutoFeedItems.Clear();
            InitializeAutoStageStates();
            IsAutoDemoVisible = true;
            AutoDemoStatusText = L("报警处理中");
            AutoDemoStatusLevel = "running";
            AutoDemoMachineText = SafeAutoMetaValue(pending.MachineCode);
            AutoDemoTraceText = _currentAutoTraceId;
            AutoDemoRunText = L("等待分配");
            AutoDemoTaskText = L("等待分配");
            AutoDemoToolCountText = string.Format(L("{0} 次"), 0);
            AutoDemoHeadline = BuildAutoPromptPreview(pending.Prompt);
            AutoDemoSecondaryText = string.Empty;
            UpdateAutoElapsedText();

            SetAutoStageStatus("trigger", "done", L("已捕获报警并生成 trace。"));
            SetAutoStageStatus("accepted", "running", L("等待本地服务受理。"));
            SetAutoStageStatus("analysis", "pending", L("尚未进入 Dify workflow。"));
            SetAutoStageStatus("mcp", "pending", L("等待 AI 判断是否需要执行动作。"));
            SetAutoStageStatus("result", "pending", L("等待最终回答。"));

            AddAutoFeed(L("报警触发"), BuildAutoPromptPreview(pending.Prompt), "info");
        }

        private bool TryActivatePendingAutoTrace(string traceId)
        {
            var safeTraceId = (traceId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(safeTraceId))
                return false;

            AutoPendingTrace pending;
            if (!_autoPendingTraces.TryGetValue(safeTraceId, out pending))
                return false;

            ActivateAutoCard(pending);
            return true;
        }

        private bool CanReplaceCurrentAutoCard(string nextTraceId)
        {
            var safeTraceId = (nextTraceId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(_currentAutoTraceId))
                return true;

            if (string.Equals(_currentAutoTraceId, safeTraceId, StringComparison.OrdinalIgnoreCase))
                return true;

            if (_currentAutoFinishedAt.HasValue)
                return true;

            return !string.Equals(AutoDemoStatusLevel, "running", StringComparison.OrdinalIgnoreCase);
        }

        private void RemovePendingAutoTrace(string traceId)
        {
            var safeTraceId = (traceId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(safeTraceId))
                return;

            _autoPendingTraces.Remove(safeTraceId);
        }

        private void RefreshAutoStageItems()
        {
            AutoStageItems.Clear();
            foreach (var stage in _autoStageStates)
            {
                AutoStageItems.Add(new AutoStageViewItem
                {
                    IndexText = stage.IndexText,
                    Title = stage.Title,
                    Summary = stage.Summary,
                    Status = stage.Status
                });
            }
        }

        private void AddAutoFeed(string title, string detail, string level, string extraDetail = null)
        {
            title = UiLanguageService.ApplyDisplayTextReplacements(title);
            detail = UiLanguageService.ApplyDisplayTextReplacements(detail);
            extraDetail = UiLanguageService.ApplyDisplayTextReplacements(extraDetail);
            var normalizedTitle = ShortenSingleLine(title, 60);
            var normalizedDetail = ShortenSingleLine(detail, 100);
            var normalizedExtra = ShortenSingleLine(extraDetail, 100);
            var mergedDetail = string.IsNullOrWhiteSpace(normalizedExtra)
                ? normalizedDetail
                : (string.IsNullOrWhiteSpace(normalizedDetail) ? normalizedExtra : normalizedDetail + " | " + normalizedExtra);

            AutoFeedItems.Insert(0, new AutoFeedViewItem
            {
                TimeText = DateTime.Now.ToString("HH:mm:ss"),
                Title = string.IsNullOrWhiteSpace(normalizedTitle) ? L("AUTO 事件") : normalizedTitle,
                Detail = string.IsNullOrWhiteSpace(mergedDetail) ? "-" : mergedDetail,
                Level = string.IsNullOrWhiteSpace(level) ? "info" : level.Trim().ToLowerInvariant()
            });

            while (AutoFeedItems.Count > AutoFeedMaxItems)
                AutoFeedItems.RemoveAt(AutoFeedItems.Count - 1);
        }

        private void UpdateAutoElapsedText()
        {
            if (!_currentAutoStartedAt.HasValue)
            {
                AutoDemoElapsedText = L("0.0 秒");
                return;
            }

            var end = _currentAutoFinishedAt ?? DateTime.Now;
            var elapsed = end - _currentAutoStartedAt.Value;
            if (elapsed < TimeSpan.Zero)
                elapsed = TimeSpan.Zero;

            AutoDemoElapsedText = FormatAutoElapsed(elapsed);
        }

        private bool IsCurrentAutoTrace(string traceId)
        {
            return !string.IsNullOrWhiteSpace(_currentAutoTraceId)
                   && !string.IsNullOrWhiteSpace(traceId)
                   && string.Equals(_currentAutoTraceId, traceId.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsFinalAutoAuditStatus(string status)
        {
            return string.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(status, "rejected", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(status, "dispatch_failed", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildAutoRunSummary(string workflowRunId, string taskId)
        {
            if (!string.IsNullOrWhiteSpace(taskId))
                return "TaskId=" + taskId.Trim();

            return "workflow_started";
        }

        private static string BuildAutoPromptPreview(string prompt)
        {
            var preview = ShortenSingleLine(prompt, 64);
            return string.IsNullOrWhiteSpace(preview) ? L("报警触发") : preview;
        }

        private static string FormatAutoFailureReason(string reason)
        {
            var normalized = (reason ?? string.Empty).Trim();
            if (string.Equals(normalized, "busy_running", StringComparison.OrdinalIgnoreCase))
            {
                return L("当前已有 AUTO 任务执行中，本次请求已丢弃。");
            }

            if (string.Equals(normalized, "invalid_config", StringComparison.OrdinalIgnoreCase))
            {
                return L("Auto URL / AutoKey 尚未配置，本次请求未被受理。");
            }

            if (string.Equals(normalized, "invalid_vision_config", StringComparison.OrdinalIgnoreCase))
            {
                return L("Auto URL / AutoVisionKey 尚未配置，本次视觉相关 AUTO 请求未被受理。");
            }

            if (string.Equals(normalized, "busy_or_invalid_config", StringComparison.OrdinalIgnoreCase))
            {
                return L("当前已有 AUTO 任务执行中，或 Auto URL / AutoKey / AutoVisionKey 尚未配置。");
            }

            return string.IsNullOrWhiteSpace(normalized) ? L("请求未被系统受理。") : normalized;
        }

        private static string FormatAutoNodeTitle(string title, string nodeType)
        {
            var normalizedTitle = ShortenSingleLine(title, 60);
            if (!string.IsNullOrWhiteSpace(normalizedTitle))
                return UiLanguageService.ApplyDisplayTextReplacements(normalizedTitle);

            var normalizedType = (nodeType ?? string.Empty).Trim().ToLowerInvariant();
            return normalizedType switch
            {
                "start" => L("开始节点"),
                "llm" => L("AI 判断节点"),
                "agent" => L("Agent 节点"),
                "if_else" => L("条件判断节点"),
                "tool" => L("工具执行节点"),
                "output" => L("输出节点"),
                "end" => L("结束节点"),
                _ => L("未命名节点")
            };
        }

        private static bool IsToolNode(string nodeType, string title)
        {
            var type = (nodeType ?? string.Empty).Trim();
            if (string.Equals(type, "tool", StringComparison.OrdinalIgnoreCase))
                return true;

            var normalizedTitle = (title ?? string.Empty).Trim();
            return normalizedTitle.IndexOf("tool", StringComparison.OrdinalIgnoreCase) >= 0
                   || normalizedTitle.IndexOf("执行", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsOutputNode(string nodeType, string title)
        {
            var type = (nodeType ?? string.Empty).Trim();
            if (string.Equals(type, "output", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "end", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var normalizedTitle = (title ?? string.Empty).Trim();
            return normalizedTitle.IndexOf("输出", StringComparison.OrdinalIgnoreCase) >= 0
                   || normalizedTitle.IndexOf("结束", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string BuildAutoToolCallKey(JObject call)
        {
            if (call == null)
                return string.Empty;

            return string.Join("|", new[]
            {
                call.Value<string>("Timestamp") ?? string.Empty,
                call.Value<string>("ToolName") ?? string.Empty,
                call.Value<string>("Args") ?? string.Empty,
                call.Value<string>("Result") ?? string.Empty,
                call.Value<string>("Error") ?? string.Empty
            });
        }

        private static bool HasAutoToolError(JObject call)
        {
            return call != null && !string.IsNullOrWhiteSpace(call.Value<string>("Error"));
        }

        private static string BuildAutoToolFeedTitle(JObject call)
        {
            var toolName = (call?.Value<string>("ToolName") ?? string.Empty).Trim();
            return toolName switch
            {
                "ClearMachineAlarms" => L("MCP 已触发机台清报警"),
                "IoCommand" => L("MCP 已执行 IO 动作"),
                "IoQueryStatus" => L("MCP 已读取 IO 状态"),
                "ResetMachine" => L("MCP 已执行设备复位"),
                "StartMachine" => L("MCP 已执行设备启动"),
                "PauseMachine" => L("MCP 已执行设备暂停"),
                "VisionCalibrateMachine" => L("MCP 已执行视觉标定"),
                "QuickInspectionMachine" => L("MCP 已执行一键点检"),
                _ => string.IsNullOrWhiteSpace(toolName) ? L("MCP 已执行动作") : L("MCP 已调用 ") + toolName
            };
        }

        private static string BuildAutoToolFeedDetail(JObject call)
        {
            if (call == null)
                return "-";

            var error = ShortenSingleLine(call.Value<string>("Error"), 90);
            if (!string.IsNullOrWhiteSpace(error))
                return L("失败：") + error;

            var args = TryParseJsonObject(call.Value<string>("Args"));
            var result = TryParseJsonObject(call.Value<string>("Result"));
            var toolName = (call.Value<string>("ToolName") ?? string.Empty).Trim();

            if (string.Equals(toolName, "IoCommand", StringComparison.OrdinalIgnoreCase))
            {
                var ioName = args?.Value<string>("ioName") ?? result?.Value<string>("ioName") ?? result?.Value<string>("io");
                var action = args?.Value<string>("op") ?? result?.Value<string>("actionText") ?? result?.Value<string>("actionCode");
                var resultText = result?.Value<string>("resultText") ?? result?.Value<string>("verificationText");
                return JoinAutoToolParts(ioName, action, resultText);
            }

            if (string.Equals(toolName, "ClearMachineAlarms", StringComparison.OrdinalIgnoreCase))
            {
                var resultText = ShortenSingleLine(call.Value<string>("Result"), 90);
                return string.IsNullOrWhiteSpace(resultText) ? L("已下发机台清报警动作。") : resultText;
            }

            if (string.Equals(toolName, "ResetMachine", StringComparison.OrdinalIgnoreCase)
                || string.Equals(toolName, "StartMachine", StringComparison.OrdinalIgnoreCase)
                || string.Equals(toolName, "PauseMachine", StringComparison.OrdinalIgnoreCase)
                || string.Equals(toolName, "VisionCalibrateMachine", StringComparison.OrdinalIgnoreCase)
                || string.Equals(toolName, "QuickInspectionMachine", StringComparison.OrdinalIgnoreCase))
            {
                var actionName = args?.Value<string>("actionName");
                var resultText = ShortenSingleLine(call.Value<string>("Result"), 90);
                return JoinAutoToolParts(actionName, resultText);
            }

            var generic = ShortenSingleLine(call.Value<string>("Result"), 90);
            if (!string.IsNullOrWhiteSpace(generic))
                return generic;

            generic = ShortenSingleLine(call.Value<string>("Args"), 90);
            return string.IsNullOrWhiteSpace(generic) ? "-" : generic;
        }

        private static JObject TryParseJsonObject(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            try
            {
                return JObject.Parse(text);
            }
            catch
            {
                return null;
            }
        }

        private static string JoinAutoToolParts(params string[] values)
        {
            return string.Join(" | ", values
                .Select(value => ShortenSingleLine(value, 60))
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        }

        private static string ShortenSingleLine(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var normalized = value
                .Replace("\r", " ")
                .Replace("\n", " / ")
                .Trim();
            if (normalized.Length <= maxLength)
                return normalized;

            if (maxLength <= 3)
                return normalized.Substring(0, maxLength);

            return normalized.Substring(0, maxLength - 3) + "...";
        }

        private static string FormatAutoElapsed(TimeSpan elapsed)
        {
            var totalSeconds = Math.Max(0, (int)Math.Floor(elapsed.TotalSeconds));
            return string.Format(L("{0} 秒"), totalSeconds);
        }

        private static string SafeAutoMetaValue(string value, string fallback = "-")
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }
        // 修正无边框窗口最大化时的可用区域，保证尊重工作区（去掉任务栏）
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            var source = (HwndSource)PresentationSource.FromVisual(this);
            if (source != null)
            {
                source.AddHook(WndProc);
            }
        }

        private const int WM_GETMINMAXINFO = 0x0024;
        private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_GETMINMAXINFO)
            {
                WmGetMinMaxInfo(hwnd, lParam);
                handled = false; // 这里让 WPF 接着处理其他部分
            }

            return IntPtr.Zero;
        }

        private void WmGetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
        {
            var mmi = (MINMAXINFO)Marshal.PtrToStructure(lParam, typeof(MINMAXINFO));

            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
            {
                var monitorInfo = new MONITORINFO();
                monitorInfo.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
                if (GetMonitorInfo(monitor, ref monitorInfo))
                {
                    RECT rcWorkArea = monitorInfo.rcWork;     // 工作区（去掉任务栏）
                    RECT rcMonitorArea = monitorInfo.rcMonitor; // 整个显示器

                    // 最大化时窗口左上角相对于显示器左上角的偏移
                    mmi.ptMaxPosition.X = rcWorkArea.Left - rcMonitorArea.Left;
                    mmi.ptMaxPosition.Y = rcWorkArea.Top - rcMonitorArea.Top;

                    // 最大化时窗口的宽高 = 工作区大小
                    mmi.ptMaxSize.X = rcWorkArea.Right - rcWorkArea.Left;
                    mmi.ptMaxSize.Y = rcWorkArea.Bottom - rcWorkArea.Top;

                    // 最大跟踪尺寸也同步一下，防止拖拉超过工作区
                    mmi.ptMaxTrackSize = mmi.ptMaxSize;
                }
            }

            Marshal.StructureToPtr(mmi, lParam, true);
        }
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                // 双击标题栏：最大化/还原
                if (WindowState == WindowState.Maximized)
                {
                    WindowState = WindowState.Normal;
                }
                else
                {
                    WindowState = WindowState.Maximized;
                }
            }
            else
            {
                // 拖动窗口
                try
                {
                    DragMove();
                }
                catch
                {
                    // 忽略偶发异常
                }
            }
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            if (WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;
            }
            else
            {
                WindowState = WindowState.Maximized;
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            BeginApplicationShutdown();
            Application.Current?.Shutdown();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            BeginApplicationShutdown();
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            try
            {
                BeginApplicationShutdown();
                if (ReferenceEquals(Instance, this))
                {
                    Instance = null;
                }

                Application.Current?.Shutdown();
            }
            catch
            {
                // 主窗体关闭时，无论是否存在隐藏附属窗体，都强制结束整个 WPF 应用。
            }
        }

        private void BeginApplicationShutdown()
        {
            if (Interlocked.Exchange(ref _shutdownStarted, 1) == 1)
            {
                return;
            }

            try { PostProgramInfo("正在退出，停止后台任务...", "info"); } catch { }
            PerformApplicationShutdownCleanup();
            StartBackgroundShutdownCleanup();
            StartShutdownWatchdog();
        }

        private void PerformApplicationShutdownCleanup()
        {
            try { _serverCts?.Cancel(); } catch { }
            try { _reportSchedulerCts.Cancel(); } catch { }
            try { _autoDemoTimer.Stop(); } catch { }
            try { Net.DifyWorkflowClient.RequestShutdown(); } catch { }
            try { AIAssistantView.GlobalInstance?.StopAllBackgroundWork(); } catch { }
            try { WarningMonitorService.Instance.Stop(); } catch { }
            try { PerformanceAutoAnalysisService.Instance.Stop(); } catch { }
            try { PerformanceMonitorService.Instance.Stop(); } catch { }
            try { AutoWindowsNotificationService.Dispose(); } catch { }
            try { Services.ConfigService.ConfigChanged -= ConfigService_ConfigChanged; } catch { }
            try { Net.WorkHttpServer.Instance.Stop(); } catch { }
        }

        private void StartBackgroundShutdownCleanup()
        {
            var cleanupThread = new Thread(() =>
            {
                try
                {
                    // MCP 子进程停止可能等待数秒，放到独立线程收尾，避免关闭窗口时卡住 UI 线程。
                    _mcpHost.Stop();
                }
                catch
                {
                    // 退出阶段忽略清理异常。
                }
            })
            {
                IsBackground = false,
                Name = "AssistantShutdownCleanup"
            };

            try
            {
                cleanupThread.Start();
            }
            catch
            {
                // 启动清理线程失败时交给退出兜底处理。
            }
        }

        private static void StartShutdownWatchdog()
        {
            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(ShutdownWatchdogDelayMs).ConfigureAwait(false);
                    Environment.Exit(0);
                }
                catch
                {
                    // 兜底退出失败时不再抛出，避免退出阶段产生新异常。
                }
            });
        }

        public static void OpenExceptionDiagnosisFromNotification()
        {
            var window = Instance;
            if (window == null)
            {
                return;
            }

            if (!window.Dispatcher.CheckAccess())
            {
                _ = window.Dispatcher.BeginInvoke(new Action(OpenExceptionDiagnosisFromNotification));
                return;
            }

            window.ActivateAndNavigateToExceptionDiagnosis();
        }

        private void NavItem_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb)
            {
                var key = rb.CommandParameter?.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(key))
                    key = rb.Content?.ToString()?.Trim();
                if (!string.IsNullOrEmpty(key))
                    NavigateByContent(key);
            }
        }

        private void NavigateByContent(string label)
        {
            if (string.Equals(label, AgentControlPageLabel, StringComparison.Ordinal) && !IsAgentControlEntryEnabled)
            {
                PostProgramInfo("智能体控制模块已冻结，已阻止打开对应页面。", "warn");
                return;
            }

            PruneDeadViews();

            if (!TryGetCachedView(label, out var view))
            {
                view = CreateView(label);
                CacheView(label, view);
            }
            RightHost.Children.Clear();
            RightHost.Children.Add(view);

            UpdateNavTitle(label);
        }

        private void ActivateAndNavigateToExceptionDiagnosis()
        {
            var keepMaximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            if (!IsVisible)
            {
                Show();
            }

            if (NavExceptionDiagnosisButton != null && NavExceptionDiagnosisButton.IsChecked != true)
            {
                NavExceptionDiagnosisButton.IsChecked = true;
            }
            else
            {
                NavigateByContent(ExceptionDiagnosisPageLabel);
            }

            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    ShowWindow(hwnd, keepMaximized ? SwShowMaximized : SwRestore);
                    SetForegroundWindow(hwnd);
                }
            }
            catch
            {
                // 激活失败时继续走 WPF 兜底。
            }

            Activate();
            if (keepMaximized && WindowState != WindowState.Maximized)
            {
                WindowState = WindowState.Maximized;
            }

            Topmost = true;
            Topmost = false;
            Focus();
        }

        private UIElement CreateView(string label)
        {
            if (string.Equals(label, "AI助手", StringComparison.Ordinal))
            {
                return AIAssistantView.GlobalInstance ?? new AIAssistantView();
            }

            if (_routes.TryGetValue(label, out var factory))
                return factory();

            return new TextBlock { Text = string.Format(L("未实现视图：{0}"), label), Margin = new Thickness(24), FontSize = 18 };
        }

        private bool TryGetCachedView(string label, out UIElement view)
        {
            view = null;
            if (string.IsNullOrWhiteSpace(label))
                return false;

            if (!_viewCache.TryGetValue(label, out var weakRef) || weakRef == null)
                return false;

            if (weakRef.TryGetTarget(out view) && view != null)
                return true;

            _viewCache.Remove(label);
            return false;
        }

        private void CacheView(string label, UIElement view)
        {
            if (string.IsNullOrWhiteSpace(label) || view == null)
                return;

            UiLanguageService.ApplyStaticText(view, new AppConfig { UiLanguage = _activeUiLanguage });
            _viewCache[label] = new WeakReference<UIElement>(view);
        }

        private void PruneDeadViews()
        {
            if (_viewCache.Count == 0)
                return;

            var staleKeys = new List<string>();
            foreach (var pair in _viewCache)
            {
                if (pair.Value == null)
                {
                    staleKeys.Add(pair.Key);
                    continue;
                }

                if (!pair.Value.TryGetTarget(out var view) || view == null)
                {
                    staleKeys.Add(pair.Key);
                }
            }

            for (int i = 0; i < staleKeys.Count; i++)
            {
                _viewCache.Remove(staleKeys[i]);
            }
        }

        private void UpdateNavTitle(string label)
        {
            CurrentPageTitle = LocalizeActiveUi(label);

            if (NavTitlePanel == null || NavTitleTransform == null)
                return;

            NavTitlePanel.Opacity = 0;
            NavTitleTransform.Y = 10;

            var fade = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(280),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var slide = new DoubleAnimation
            {
                From = 10,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(260),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            NavTitlePanel.BeginAnimation(UIElement.OpacityProperty, fade);
            NavTitleTransform.BeginAnimation(TranslateTransform.YProperty, slide);
        }

        private string LocalizeActiveUi(string chineseText)
        {
            return UiLanguageService.IsEnglish(_activeUiLanguage)
                ? UiLanguageService.TranslateToEnglish(chineseText)
                : (chineseText ?? string.Empty);
        }

        private static string L(string chineseText)
        {
            return UiLanguageService.CurrentText(chineseText);
        }

        // ===== 日志相关静态字段 =====
        /// <summary>程序信息日志根目录。</summary>
        private const string ProgramLogRoot = @"D:\Data\AiLog\UI\";

        /// <summary>写日志的锁，防止多线程同时写同一个文件。</summary>
        private static readonly object _programLogLock = new object();

        /// <summary>
        /// 安全写入一行程序信息日志，不抛异常。
        /// </summary>
        private static void SafeWriteProgramLog(string message, string level)
        {
            try
            {
                // 空值兜底，保证日志格式完整
                if (message == null) message = string.Empty;
                if (string.IsNullOrWhiteSpace(level)) level = "info";

                // 准备目录与按天切分的文件名
                Directory.CreateDirectory(ProgramLogRoot);
                string fileName = DateTime.Now.ToString("yyyy-MM-dd") + ".log";
                string filePath = Path.Combine(ProgramLogRoot, fileName);

                // 时间 + 级别 + 文本
                string line = string.Format(
                    "[{0:yyyy-MM-dd HH:mm:ss}] [{1}] {2}",
                    DateTime.Now,
                    level.ToUpperInvariant(),
                    message.Replace(Environment.NewLine, " ")  // 简单处理多行
                );

                lock (_programLogLock)
                {
                    File.AppendAllText(filePath, line + Environment.NewLine, Encoding.UTF8);
                }

                LogRetentionPolicy.TryCleanupFiles(
                    ProgramLogRoot,
                    "*.log",
                    SearchOption.TopDirectoryOnly,
                    TimeSpan.FromDays(30));
            }
            catch (Exception ex)
            {
                // 日志失败不应影响主流程，这里仅落到调试输出
                Debug.WriteLine("[ProgramLog] 写日志失败：" + ex.Message);
            }
        }

        // ===== 面向全程序开放的静态接口 =====
        /// <summary>
        /// 跨线程安全地写入程序信息日志，同时投递到 UI 信息流。
        /// level 参数可选："info" | "ok" | "warn" | "error"
        /// </summary>
        public static void PostProgramInfo(string message, string level = "info")
        {
            // 1) 先写文件日志（与 UI 无关）
            SafeWriteProgramLog(message, level);

            // 2) 再投递到主窗口信息卡（原有逻辑）
            var w = Instance;
            if (w == null) return;

            // 应用退出时可能存在后台线程写日志，需吞掉 Dispatcher 已关闭带来的异常
            try
            {
                if (w.Dispatcher.HasShutdownStarted || w.Dispatcher.HasShutdownFinished)
                    return;

                if (w.Dispatcher.CheckAccess())
                    w.AppendInfo(message, level);
                else
                    w.Dispatcher.Invoke(() => w.AppendInfo(message, level));
            }
            catch (TaskCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException ex)
            {
                Debug.WriteLine($"PostProgramInfo 调用失败：{ex.Message}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PostProgramInfo 调用异常：{ex}");
            }
        }

        private void LogMcpMessage(string message, string level)
        {
            PostProgramInfo(message ?? string.Empty, level ?? "info");
        }

        // ===== 本地实现 =====
        private void AppendInfo(string text, string level)
        {
            if (ShouldSuppressProgramInfoInUi(text))
                return;

            InfoItems.Add(new ProgramInfoItem
            {
                Time = DateTime.Now,
                Text = UiLanguageService.ApplyDisplayTextReplacements(text),
                Level = (level ?? "info").ToLowerInvariant()
            });

            // 自动滚动到底
            if (InfoItems.Count > 0)
                InfoList.ScrollIntoView(InfoItems.Last());

            // 控制上限，避免越积越多
            const int MaxItems = 200;
            while (InfoItems.Count > MaxItems)
                InfoItems.RemoveAt(0);
        }

        private static bool ShouldSuppressProgramInfoInUi(string text)
        {
            var normalized = (text ?? string.Empty).Trim();
            return string.Equals(normalized, "视觉 AUTO 图片测试模式：跳过时间校验，直接读取测试图片。", StringComparison.Ordinal)
                   || string.Equals(normalized, "视觉 AUTO 图片已准备好：无料基准图 + 正常有料基准图 + 实时采集图。", StringComparison.Ordinal);
        }

        private void BtnClearInfo_Click(object sender, RoutedEventArgs e)
        {
            InfoItems.Clear();
            StatusText = L("已清空");
        }

        private void BtnCopyInfo_Click(object sender, RoutedEventArgs e)
        {
            CopySelectedInfoItems();
        }

        private void BtnCopyAllInfo_Click(object sender, RoutedEventArgs e)
        {
            CopyInfoItems(InfoItems, string.Format(L("已复制 {0} 条信息"), InfoItems.Count));
        }

        private void InfoList_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                CopySelectedInfoItems();
                e.Handled = true;
            }
        }

        private void CopySelectedInfoItems()
        {
            var selected = InfoList.SelectedItems.Cast<ProgramInfoItem>().ToList();
            if (selected.Count == 0 && InfoList.SelectedItem is ProgramInfoItem one)
            {
                selected.Add(one);
            }

            if (selected.Count == 0)
            {
                StatusText = L("请先选择要复制的信息");
                return;
            }

            CopyInfoItems(selected, string.Format(L("已复制 {0} 条信息"), selected.Count));
        }

        private void CopyInfoItems(IEnumerable<ProgramInfoItem> items, string successStatus)
        {
            var text = BuildInfoClipboardText(items);
            if (string.IsNullOrWhiteSpace(text))
            {
                StatusText = L("无可复制内容");
                return;
            }

            try
            {
                Clipboard.SetText(text);
                StatusText = successStatus;
            }
            catch (Exception ex)
            {
                StatusText = L("复制失败");
                Debug.WriteLine("复制信息流失败：" + ex.Message);
            }
        }

        private static string BuildInfoClipboardText(IEnumerable<ProgramInfoItem> items)
        {
            if (items == null)
                return string.Empty;

            var rows = items.Where(x => x != null).ToList();
            if (rows.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();
            foreach (var item in rows)
            {
                var level = string.IsNullOrWhiteSpace(item.Level) ? "INFO" : item.Level.ToUpperInvariant();
                sb.Append('[')
                  .Append(item.Time.ToString("HH:mm:ss"))
                  .Append("] [")
                  .Append(level)
                  .Append("] ")
                  .Append(item.Text ?? string.Empty)
                  .AppendLine();
            }

            return sb.ToString().TrimEnd();
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private void SafeNotifyPropertyChanged(string propertyName)
        {
            if (Dispatcher.CheckAccess())
            {
                OnPropertyChanged(propertyName);
            }
            else
            {
                Dispatcher.Invoke(() => OnPropertyChanged(propertyName));
            }
        }
    }
    public class ProgramInfoItem
    {
        public DateTime Time { get; set; }
        public string Text { get; set; } = "";
        /// <summary>等级："info" | "ok" | "warn" | "error"</summary>
        public string Level { get; set; } = "info";
    }
    public sealed class AutoStageViewItem
    {
        public string IndexText { get; set; } = "";
        public string Title { get; set; } = "";
        public string Summary { get; set; } = "";
        public string Status { get; set; } = "pending";
    }
    public sealed class AutoFeedViewItem
    {
        public string TimeText { get; set; } = "";
        public string Title { get; set; } = "";
        public string Detail { get; set; } = "";
        public string Level { get; set; } = "info";
    }
    internal sealed class AutoPendingTrace
    {
        public string TraceId { get; set; } = "";
        public string MachineCode { get; set; } = "";
        public string Prompt { get; set; } = "";
        public DateTime TriggeredAt { get; set; }
    }
    internal sealed class AutoStageState
    {
        public AutoStageState(string key, string indexText, string title)
        {
            Key = key ?? string.Empty;
            IndexText = indexText ?? string.Empty;
            Title = title ?? string.Empty;
            Summary = string.Empty;
            Status = "pending";
        }

        public string Key { get; }
        public string IndexText { get; }
        public string Title { get; }
        public string Summary { get; set; }
        public string Status { get; set; }
    }
    public class DeviceBrief
    {
        /// <summary>设备名称（展示用）。</summary>
        public string Name { get; set; }

        /// <summary>设备型号或机种。</summary>
        public string Model { get; set; }

        /// <summary>序列号或资产编号。</summary>
        public string Serial { get; set; }

        /// <summary>安装日期。</summary>
        public DateTime? InstallDate { get; set; }

        /// <summary>累积运行小时数。</summary>
        public double? RuntimeHours { get; set; }

        public DateTime? LastMaintenance { get; set; }
        public DateTime? NextMaintenance { get; set; }

        /// <summary>当前责任人。</summary>
        public string Owner { get; set; }

        /// <summary>责任人联系方式。</summary>
        public string OwnerPhone { get; set; }

        /// <summary>质保状态描述。</summary>
        public string WarrantyStatus { get; set; }

        /// <summary>供应商信息。</summary>
        public string Supplier { get; set; }
    }
}

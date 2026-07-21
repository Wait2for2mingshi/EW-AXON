using EW_Assistant.Services;
using EW_Assistant.Services.PreventiveMaintenance;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace EW_Assistant.Views
{
    public partial class PreventiveMaintenanceView : UserControl, INotifyPropertyChanged
    {
        private readonly PartMaintenanceAnalyzer _analyzer = new PartMaintenanceAnalyzer();
        private readonly PreventiveMaintenanceAiSuggestionService _aiSuggestionService = new PreventiveMaintenanceAiSuggestionService();
        private const int MinimumRefreshIndicatorMs = 600;
        private const int MinFreshAiSuggestionLength = 180;
        private const string RefreshButtonText = "重新分析";
        private const string RefreshInProgressText = "正在分析中，再点击一次可以取消重新分析";
        private const string RefreshCancelingText = "正在取消重新分析...";
        private const string RefreshCanceledText = "已取消重新分析";
        private const string RefreshCompletedText = "重新分析完成";
        private const string AiSuggestionTitle = "AI分析建议";
        private const string AiSuggestionLoadingText = "AI分析中";
        private const string LocalRuleTitle = "本地分析规则";
        private const string AiSuggestionFailureText = "ai分析失败";
        private PartMaintenanceReport _report;
        private string _fileCountText = "-";
        private string _latestDateText = "-";
        private string _cylinderSummary = "暂无气缸数据。";
        private string _vacuumSummary = "暂无真空吸数据。";
        private string _selectedDateRangeMode = "全部";
        private DateTime? _customStartDate = DateTime.Today.AddDays(-6);
        private DateTime? _customEndDate = DateTime.Today;
        private PartMaintenanceComponentStatus _selectedCylinder;
        private PartMaintenanceComponentStatus _selectedVacuum;
        private int? _hoverCylinderPointIndex;
        private int? _hoverCylinderWorkPointIndex;
        private int? _hoverVacuumPointIndex;
        private bool _isRefreshing;
        private CancellationTokenSource _refreshCancellationTokenSource;
        private CancellationTokenSource _aiSuggestionCancellationTokenSource;
        private string _refreshStatusText = string.Empty;
        private DateTime? _latestDataAnchorDate;
        private DateRangeSelection _currentAiRange;
        private int _aiSuggestionGenerationId;
        private bool _isApplyingReport;
        private DispatcherTimer _aiSuggestionProgressTimer;
        private int _aiSuggestionDotPhase;

        public PreventiveMaintenanceView()
        {
            InitializeComponent();
            DataContext = this;
            Loaded += PreventiveMaintenanceView_Loaded;
            Unloaded += PreventiveMaintenanceView_Unloaded;
        }

        public ObservableCollection<PartMaintenanceComponentStatus> CylinderStatuses { get; } = new ObservableCollection<PartMaintenanceComponentStatus>();
        public ObservableCollection<PartMaintenanceComponentStatus> VacuumStatuses { get; } = new ObservableCollection<PartMaintenanceComponentStatus>();
        public ObservableCollection<PartMaintenanceComponentStatus> CylinderOptions { get; } = new ObservableCollection<PartMaintenanceComponentStatus>();
        public ObservableCollection<PartMaintenanceComponentStatus> VacuumOptions { get; } = new ObservableCollection<PartMaintenanceComponentStatus>();
        public ObservableCollection<string> DateRangeOptions { get; } = new ObservableCollection<string> { "全部", "近3天", "近7天", "近一个月", "自定义" };

        public string FileCountText
        {
            get => _fileCountText;
            private set { if (_fileCountText != value) { _fileCountText = value; OnPropertyChanged(); } }
        }

        public string LatestDateText
        {
            get => _latestDateText;
            private set { if (_latestDateText != value) { _latestDateText = value; OnPropertyChanged(); } }
        }

        public string CylinderSummary
        {
            get => _cylinderSummary;
            private set { if (_cylinderSummary != value) { _cylinderSummary = value; OnPropertyChanged(); } }
        }

        public string VacuumSummary
        {
            get => _vacuumSummary;
            private set { if (_vacuumSummary != value) { _vacuumSummary = value; OnPropertyChanged(); } }
        }

        public string RefreshStatusText
        {
            get => _refreshStatusText;
            private set { if (_refreshStatusText != value) { _refreshStatusText = value; OnPropertyChanged(); } }
        }

        public bool IsRefreshing
        {
            get => _isRefreshing;
            private set { if (_isRefreshing != value) { _isRefreshing = value; OnPropertyChanged(); } }
        }

        public string SelectedDateRangeMode
        {
            get => _selectedDateRangeMode;
            set
            {
                if (_selectedDateRangeMode != value)
                {
                    _selectedDateRangeMode = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CustomRangeVisibility));
                }
            }
        }

        public DateTime? CustomStartDate
        {
            get => _customStartDate;
            set { if (_customStartDate != value) { _customStartDate = value; OnPropertyChanged(); } }
        }

        public DateTime? CustomEndDate
        {
            get => _customEndDate;
            set { if (_customEndDate != value) { _customEndDate = value; OnPropertyChanged(); } }
        }

        public Visibility CustomRangeVisibility
        {
            get => string.Equals(SelectedDateRangeMode, "自定义", StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        public PartMaintenanceComponentStatus SelectedCylinder
        {
            get => _selectedCylinder;
            set
            {
                if (!ReferenceEquals(_selectedCylinder, value))
                {
                    _selectedCylinder = value;
                    _hoverCylinderPointIndex = null;
                    _hoverCylinderWorkPointIndex = null;
                    OnPropertyChanged();
                    CylinderSummary = BuildCylinderTrendSummary(_selectedCylinder);
                    CylinderChart?.InvalidateVisual();
                    if (!_isApplyingReport)
                        StartAiSuggestionRefresh(_report, _currentAiRange, _selectedCylinder);
                }
            }
        }

        public PartMaintenanceComponentStatus SelectedVacuum
        {
            get => _selectedVacuum;
            set
            {
                if (!ReferenceEquals(_selectedVacuum, value))
                {
                    _selectedVacuum = value;
                    _hoverVacuumPointIndex = null;
                    OnPropertyChanged();
                    VacuumSummary = BuildComponentTrendSummary("吸嘴", _selectedVacuum);
                    VacuumChart?.InvalidateVisual();
                    if (!_isApplyingReport)
                        StartAiSuggestionRefresh(_report, _currentAiRange, _selectedVacuum);
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private async void PreventiveMaintenanceView_Loaded(object sender, RoutedEventArgs e)
        {
            await RefreshAnalysisAsync();
        }

        private void PreventiveMaintenanceView_Unloaded(object sender, RoutedEventArgs e)
        {
            CancelAiSuggestionRefresh();
            _aiSuggestionProgressTimer?.Stop();
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            if (IsRefreshing)
            {
                _refreshCancellationTokenSource?.Cancel();
                RefreshStatusText = RefreshCancelingText;
                return;
            }

            await RefreshAnalysisAsync();
        }

        private async Task RefreshAnalysisAsync()
        {
            if (IsRefreshing)
                return;

            var range = ResolveSelectedDateRange();
            var cancellationSource = new CancellationTokenSource();
            _refreshCancellationTokenSource = cancellationSource;
            IsRefreshing = true;
            RefreshStatusText = RefreshInProgressText;
            SetRefreshButtonLoading(true);
            var refreshStartedAt = DateTime.UtcNow;
            if (_report == null)
            {
                ApplyLoadingState();
            }

            try
            {
                var path = ConfigService.Current?.PartCsvPath ?? string.Empty;
                var token = cancellationSource.Token;
                var report = await Task.Run(() => _analyzer.Analyze(path, range.StartDate, range.EndDate, token), token);
                ApplyReport(report, range);
                RefreshStatusText = string.IsNullOrWhiteSpace(report.StatusMessage)
                    ? RefreshCompletedText
                    : RefreshCompletedText + "：" + report.StatusMessage;
            }
            catch (OperationCanceledException)
            {
                RefreshStatusText = RefreshCanceledText;
            }
            catch (Exception ex)
            {
                MainWindow.PostProgramInfo("预防维护分析失败：" + ex.Message, "error");
            }
            finally
            {
                var remainingMs = MinimumRefreshIndicatorMs - (int)(DateTime.UtcNow - refreshStartedAt).TotalMilliseconds;
                if (remainingMs > 0)
                    await Task.Delay(remainingMs);

                if (ReferenceEquals(_refreshCancellationTokenSource, cancellationSource))
                    _refreshCancellationTokenSource = null;

                cancellationSource.Dispose();
                IsRefreshing = false;
                SetRefreshButtonLoading(false);
                if (RefreshStatusText == RefreshInProgressText)
                    RefreshStatusText = RefreshCompletedText;
            }
        }

        private void SetRefreshButtonLoading(bool isLoading)
        {
            if (BtnRefresh == null)
                return;

            BtnRefresh.Content = isLoading ? CreateLoadingCircle() : RefreshButtonText;
        }

        private static FrameworkElement CreateLoadingCircle()
        {
            var root = new Grid { Width = 22, Height = 22 };
            var track = new System.Windows.Shapes.Ellipse
            {
                Width = 18,
                Height = 18,
                Stroke = Brushes.White,
                StrokeThickness = 2,
                Opacity = 0.35,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var arc = new System.Windows.Shapes.Path
            {
                Stroke = Brushes.White,
                StrokeThickness = 2.2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Data = Geometry.Parse("M 11,2 A 9,9 0 0 1 20,11")
            };
            var rotate = new RotateTransform(0, 11, 11);
            arc.RenderTransform = rotate;
            rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, new Duration(TimeSpan.FromMilliseconds(850)))
            {
                RepeatBehavior = RepeatBehavior.Forever
            });
            root.Children.Add(track);
            root.Children.Add(arc);
            return root;
        }

        private void ApplyReport(PartMaintenanceReport report, DateRangeSelection range)
        {
            _report = report;
            _currentAiRange = range;
            FileCountText = _report.FileCount.ToString();
            LatestDateText = _report.LatestDate.HasValue ? _report.LatestDate.Value.ToString("MM-dd") : "-";
            if (_report.LatestDate.HasValue)
                _latestDataAnchorDate = _report.LatestDate.Value.Date;

            _isApplyingReport = true;
            try
            {
                CylinderOptions.Clear();
                foreach (var status in _report.CylinderStatuses)
                {
                    CylinderOptions.Add(status);
                }

                VacuumOptions.Clear();
                foreach (var status in _report.VacuumStatuses)
                {
                    VacuumOptions.Add(status);
                }

                CylinderStatuses.Clear();
                foreach (var status in _report.CylinderStatuses.Take(5))
                {
                    CylinderStatuses.Add(status);
                }

                VacuumStatuses.Clear();
                foreach (var status in _report.VacuumStatuses.Take(3))
                {
                    VacuumStatuses.Add(status);
                }

                SelectedCylinder = CylinderOptions.FirstOrDefault();
                SelectedVacuum = VacuumOptions.FirstOrDefault();
            }
            finally
            {
                _isApplyingReport = false;
            }

            CylinderChart?.InvalidateVisual();
            VacuumChart?.InvalidateVisual();
            StartAiSuggestionRefresh(_report, range, GetHighestRiskVisibleStatus());
        }

        private void ApplyLoadingState()
        {
            CancelAiSuggestionRefresh();
            FileCountText = "-";
            LatestDateText = "-";
            _currentAiRange = null;

            var cylinder = new PartMaintenanceComponentStatus
            {
                PartType = "气缸",
                ComponentName = "正在分析...",
                Suggestion = "正在读取零件 CSV，并生成气缸预防维护建议，请稍候。",
                AiSuggestionDisplayText = "正在读取零件 CSV，并生成气缸预防维护建议，请稍候。",
                IsAiSuggestionRunning = true
            };

            var vacuum = new PartMaintenanceComponentStatus
            {
                PartType = "吸嘴",
                ComponentName = "正在分析...",
                Suggestion = "正在读取零件 CSV，并生成吸嘴预防维护建议，请稍候。",
                AiSuggestionDisplayText = "正在读取零件 CSV，并生成吸嘴预防维护建议，请稍候。",
                IsAiSuggestionRunning = true
            };

            CylinderOptions.Clear();
            CylinderOptions.Add(cylinder);
            VacuumOptions.Clear();
            VacuumOptions.Add(vacuum);

            CylinderStatuses.Clear();
            VacuumStatuses.Clear();

            SelectedCylinder = cylinder;
            SelectedVacuum = vacuum;
            CylinderSummary = "正在分析气缸数据...";
            VacuumSummary = "正在分析吸嘴数据...";
            CylinderChart?.InvalidateVisual();
            VacuumChart?.InvalidateVisual();
            EnsureAiSuggestionProgressTimer();
        }

        private void StartAiSuggestionRefresh(
            PartMaintenanceReport report,
            DateRangeSelection range,
            PartMaintenanceComponentStatus preferredStatus)
        {
            CancelAiSuggestionRefresh();
            if (report == null || range == null)
                return;

            var queue = BuildAiSuggestionQueue(preferredStatus);
            if (queue.Count == 0)
                return;

            var generation = Interlocked.Increment(ref _aiSuggestionGenerationId);
            var aiSource = new CancellationTokenSource();
            _aiSuggestionCancellationTokenSource = aiSource;
            var rangeText = BuildRangeText(range);

            _aiSuggestionDotPhase = 1;
            foreach (var status in queue)
            {
                string cached;
                if (_aiSuggestionService.TryGetCachedSuggestion(report, status, out cached) &&
                    !IsLikelyLegacyShortSuggestion(cached))
                {
                    SetAiSuggestionSuccess(status, cached);
                }
                else
                {
                    SetAiSuggestionLoading(status);
                }
            }

            NotifyAiSuggestionChanged();

            _ = Task.Run(async () =>
            {
                try
                {
                    foreach (var status in queue)
                    {
                        aiSource.Token.ThrowIfCancellationRequested();

                        string cached;
                        var hasCached = _aiSuggestionService.TryGetCachedSuggestion(report, status, out cached);
                        var refreshCache = hasCached && IsLikelyLegacyShortSuggestion(cached);
                        if (hasCached && !refreshCache)
                            continue;

                        var results = await _aiSuggestionService.GenerateSuggestionsAsync(
                            report,
                            rangeText,
                            new[] { status },
                            aiSource.Token,
                            refreshCache).ConfigureAwait(false);

                        if (aiSource.Token.IsCancellationRequested)
                            return;

                        var key = PreventiveMaintenanceAiSuggestionService.BuildComponentKey(status);
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (generation != _aiSuggestionGenerationId || !ReferenceEquals(_report, report))
                                return;

                            var current = FindComponentStatusByKey(report, key);
                            var result = results == null
                                ? null
                                : results.FirstOrDefault(x => x != null
                                                              && !string.IsNullOrWhiteSpace(x.Suggestion)
                                                              && string.Equals(x.ComponentKey, key, StringComparison.OrdinalIgnoreCase));

                            if (result == null)
                                SetAiSuggestionFailure(current);
                            else
                                SetAiSuggestionSuccess(current, result.Suggestion);

                            NotifyAiSuggestionChanged();
                        }));
                    }
                }
                catch (OperationCanceledException)
                {
                    // 切换范围或重新分析时取消后台 AI 建议生成。
                }
                finally
                {
                    if (ReferenceEquals(_aiSuggestionCancellationTokenSource, aiSource))
                        _aiSuggestionCancellationTokenSource = null;
                }
            });
        }

        private void CancelAiSuggestionRefresh()
        {
            Interlocked.Increment(ref _aiSuggestionGenerationId);
            var current = _aiSuggestionCancellationTokenSource;
            _aiSuggestionCancellationTokenSource = null;
            if (current != null)
                current.Cancel();
        }

        private void SetAiSuggestionFailure(PartMaintenanceComponentStatus status)
        {
            if (status == null)
                return;
            status.AiSuggestionTitle = LocalRuleTitle;
            status.AiSuggestionFailureMessage = AiSuggestionFailureText;
            status.AiSuggestionDisplayText = string.IsNullOrWhiteSpace(status.Suggestion)
                ? "暂无本地规则建议。"
                : status.Suggestion;
            status.AiSuggestionProgress = 100;
            status.IsAiSuggestionRunning = false;
        }

        private void SetAiSuggestionLoading(PartMaintenanceComponentStatus status)
        {
            if (status == null)
                return;

            status.AiSuggestionTitle = AiSuggestionTitle;
            status.AiSuggestionFailureMessage = string.Empty;
            status.AiSuggestionDisplayText = BuildAiSuggestionLoadingText();
            status.AiSuggestionProgress = 0;
            status.IsAiSuggestionRunning = true;
            EnsureAiSuggestionProgressTimer();
        }

        private void SetAiSuggestionSuccess(PartMaintenanceComponentStatus status, string suggestion)
        {
            if (status == null)
                return;

            status.AiSuggestionTitle = AiSuggestionTitle;
            status.AiSuggestionFailureMessage = string.Empty;
            status.AiSuggestionDisplayText = suggestion ?? string.Empty;
            status.AiSuggestionProgress = 100;
            status.IsAiSuggestionRunning = false;
        }

        private void NotifyAiSuggestionChanged()
        {
            OnPropertyChanged(nameof(SelectedCylinder));
            OnPropertyChanged(nameof(SelectedVacuum));
        }

        private void EnsureAiSuggestionProgressTimer()
        {
            if (_aiSuggestionProgressTimer == null)
            {
                _aiSuggestionProgressTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(320)
                };
                _aiSuggestionProgressTimer.Tick += AiSuggestionProgressTimer_Tick;
            }

            if (!_aiSuggestionProgressTimer.IsEnabled)
                _aiSuggestionProgressTimer.Start();
        }

        private void AiSuggestionProgressTimer_Tick(object sender, EventArgs e)
        {
            var anyRunning = false;
            _aiSuggestionDotPhase = (_aiSuggestionDotPhase % 3) + 1;
            foreach (var status in EnumerateKnownStatuses())
            {
                if (status == null || !status.IsAiSuggestionRunning)
                    continue;

                anyRunning = true;
                status.AiSuggestionDisplayText = BuildAiSuggestionLoadingText();
                var progress = status.AiSuggestionProgress;
                var step = progress < 55 ? 7 : progress < 82 ? 3 : 1;
                status.AiSuggestionProgress = Math.Min(96, progress + step);
            }

            if (anyRunning)
                NotifyAiSuggestionChanged();

            if (!anyRunning)
                _aiSuggestionProgressTimer?.Stop();
        }

        private string BuildAiSuggestionLoadingText()
        {
            return AiSuggestionLoadingText + new string('.', _aiSuggestionDotPhase == 0 ? 1 : _aiSuggestionDotPhase);
        }

        private IEnumerable<PartMaintenanceComponentStatus> EnumerateKnownStatuses()
        {
            return _report == null
                ? new[] { SelectedCylinder, SelectedVacuum }.Where(x => x != null)
                : _report.CylinderStatuses.Concat(_report.VacuumStatuses);
        }

        private static bool IsLikelyLegacyShortSuggestion(string suggestion)
        {
            return !string.IsNullOrWhiteSpace(suggestion)
                   && suggestion.Trim().Length < MinFreshAiSuggestionLength;
        }

        private List<PartMaintenanceComponentStatus> BuildAiSuggestionQueue(PartMaintenanceComponentStatus preferredStatus)
        {
            var queue = new List<PartMaintenanceComponentStatus>();
            AddAiSuggestionQueueItem(queue, preferredStatus);
            AddAiSuggestionQueueItem(queue, GetHighestRiskVisibleStatus());
            AddAiSuggestionQueueItem(queue, SelectedCylinder);
            AddAiSuggestionQueueItem(queue, SelectedVacuum);

            foreach (var status in CylinderStatuses)
                AddAiSuggestionQueueItem(queue, status);

            foreach (var status in VacuumStatuses)
                AddAiSuggestionQueueItem(queue, status);

            return queue;
        }

        private static void AddAiSuggestionQueueItem(
            ICollection<PartMaintenanceComponentStatus> queue,
            PartMaintenanceComponentStatus status)
        {
            if (queue == null || status == null || string.IsNullOrWhiteSpace(status.ComponentName))
                return;

            var key = PreventiveMaintenanceAiSuggestionService.BuildComponentKey(status);
            if (queue.Any(x => string.Equals(
                PreventiveMaintenanceAiSuggestionService.BuildComponentKey(x),
                key,
                StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            queue.Add(status);
        }

        private PartMaintenanceComponentStatus GetHighestRiskVisibleStatus()
        {
            return CylinderStatuses
                .Concat(VacuumStatuses)
                .OrderByDescending(x => x?.RiskScore ?? 0)
                .FirstOrDefault();
        }

        private static PartMaintenanceComponentStatus FindComponentStatusByKey(PartMaintenanceReport report, string componentKey)
        {
            if (report == null || string.IsNullOrWhiteSpace(componentKey))
                return null;

            foreach (var status in report.CylinderStatuses.Concat(report.VacuumStatuses))
            {
                if (string.Equals(
                    PreventiveMaintenanceAiSuggestionService.BuildComponentKey(status),
                    componentKey,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return status;
                }
            }

            return null;
        }

        private static string BuildRangeText(DateRangeSelection range)
        {
            if (range == null)
                return "全部";

            if (range.StartDate.HasValue || range.EndDate.HasValue)
            {
                var start = range.StartDate.HasValue ? range.StartDate.Value.ToString("yyyy-MM-dd") : "最早";
                var end = range.EndDate.HasValue ? range.EndDate.Value.ToString("yyyy-MM-dd") : "最新";
                return (range.Mode ?? "自定义") + "：" + start + " ~ " + end;
            }

            return range.Mode ?? "全部";
        }

        private DateRangeSelection ResolveSelectedDateRange()
        {
            var anchorDate = ResolveRecentRangeAnchorDate();
            var mode = string.IsNullOrWhiteSpace(SelectedDateRangeMode) ? "全部" : SelectedDateRangeMode.Trim();

            if (mode == "近3天")
                return CreateRecentDayRange(mode, 3, anchorDate);

            if (mode == "近7天")
                return CreateRecentDayRange(mode, 7, anchorDate);

            if (mode == "近一个月")
                return new DateRangeSelection(mode, anchorDate.AddMonths(-1), anchorDate);

            if (mode == "自定义")
            {
                var start = CustomStartDate?.Date;
                var end = CustomEndDate?.Date;
                if (start.HasValue && end.HasValue && start.Value > end.Value)
                {
                    var temp = start;
                    start = end;
                    end = temp;
                }

                return new DateRangeSelection(mode, start, end);
            }

            return CreateAllRange();
        }

        private static DateRangeSelection CreateAllRange()
        {
            return new DateRangeSelection("全部", null, null);
        }

        private DateTime ResolveRecentRangeAnchorDate()
        {
            if (_latestDataAnchorDate.HasValue)
                return _latestDataAnchorDate.Value.Date;

            var path = ConfigService.Current?.PartCsvPath ?? string.Empty;
            var latest = _analyzer.GetLatestDataDate(path);
            if (latest.HasValue)
            {
                _latestDataAnchorDate = latest.Value.Date;
                return latest.Value.Date;
            }

            return DateTime.Today;
        }

        private static DateRangeSelection CreateRecentDayRange(string mode, int days, DateTime anchorDate)
        {
            var safeDays = Math.Max(1, days);
            var end = anchorDate.Date;
            return new DateRangeSelection(mode, end.AddDays(1 - safeDays), end);
        }

        private sealed class DateRangeSelection
        {
            public DateRangeSelection(string mode, DateTime? startDate, DateTime? endDate)
            {
                Mode = mode;
                StartDate = startDate;
                EndDate = endDate;
            }

            public string Mode { get; }
            public DateTime? StartDate { get; }
            public DateTime? EndDate { get; }
        }

        private static string BuildComponentTrendSummary(string name, PartMaintenanceComponentStatus status)
        {
            if (status == null || status.Trend.Count == 0)
                return "请选择" + name + "查看趋势。";

            var latest = status.Trend[status.Trend.Count - 1];
            return status.ComponentName
                   + " 最近日期 " + latest.Date.ToString("yyyy-MM-dd")
                   + "，趋势值 " + latest.Value.ToString("0.###") + "。";
        }

        private static string BuildCylinderTrendSummary(PartMaintenanceComponentStatus status)
        {
            if (status == null)
                return "请选择气缸查看趋势。";

            var homeLatest = GetLatestTrendPoint(status.HomeTrend);
            var workLatest = GetLatestTrendPoint(status.WorkTrend);
            if (homeLatest == null && workLatest == null)
                return BuildComponentTrendSummary("气缸", status);

            if (homeLatest != null && workLatest != null && homeLatest.Date.Date == workLatest.Date.Date)
            {
                return status.ComponentName
                       + " 最近日期 " + homeLatest.Date.ToString("yyyy-MM-dd")
                       + "，原位日均值 " + homeLatest.Value.ToString("0.###")
                       + "，动位日均值 " + workLatest.Value.ToString("0.###") + "。";
            }

            var parts = new List<string>();
            if (homeLatest != null)
                parts.Add("原位最近 " + homeLatest.Date.ToString("yyyy-MM-dd") + " 日均值 " + homeLatest.Value.ToString("0.###"));
            if (workLatest != null)
                parts.Add("动位最近 " + workLatest.Date.ToString("yyyy-MM-dd") + " 日均值 " + workLatest.Value.ToString("0.###"));

            return status.ComponentName + " " + string.Join("，", parts) + "。";
        }

        private static PartMaintenanceTrendPoint GetLatestTrendPoint(IList<PartMaintenanceTrendPoint> points)
        {
            return points == null || points.Count == 0 ? null : points[points.Count - 1];
        }

        private void CylinderChart_PaintSurface(object sender, SKPaintSurfaceEventArgs e)
        {
            DrawTrendChart(e.Surface.Canvas, e.Info, SelectedCylinder?.HomeTrend, "气缸", _hoverCylinderPointIndex, SelectedCylinder?.WorkTrend, _hoverCylinderWorkPointIndex, "原位", "动位");
        }

        private void VacuumChart_PaintSurface(object sender, SKPaintSurfaceEventArgs e)
        {
            DrawTrendChart(e.Surface.Canvas, e.Info, SelectedVacuum?.Trend, "吸嘴", _hoverVacuumPointIndex);
        }

        private void CylinderChart_MouseMove(object sender, MouseEventArgs e)
        {
            UpdateChartHover(CylinderChart, SelectedCylinder?.HomeTrend, SelectedCylinder?.WorkTrend, e.GetPosition(CylinderChart), ref _hoverCylinderPointIndex, ref _hoverCylinderWorkPointIndex);
        }

        private void VacuumChart_MouseMove(object sender, MouseEventArgs e)
        {
            UpdateChartHover(VacuumChart, SelectedVacuum?.Trend, e.GetPosition(VacuumChart), ref _hoverVacuumPointIndex);
        }

        private void CylinderChart_MouseLeave(object sender, MouseEventArgs e)
        {
            ClearChartHover(CylinderChart, ref _hoverCylinderPointIndex, ref _hoverCylinderWorkPointIndex);
        }

        private void VacuumChart_MouseLeave(object sender, MouseEventArgs e)
        {
            ClearChartHover(VacuumChart, ref _hoverVacuumPointIndex);
        }

        private void CylinderStatusGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var status = ResolveDoubleClickedStatus(sender, e);
            var option = FindMatchingComponent(CylinderOptions, status);
            if (option == null)
                return;

            SelectedCylinder = option;
            e.Handled = true;
        }

        private void VacuumStatusGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var status = ResolveDoubleClickedStatus(sender, e);
            var option = FindMatchingComponent(VacuumOptions, status);
            if (option == null)
                return;

            SelectedVacuum = option;
            e.Handled = true;
        }

        private static PartMaintenanceComponentStatus ResolveDoubleClickedStatus(object sender, MouseButtonEventArgs e)
        {
            var row = FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject);
            if (row?.Item is PartMaintenanceComponentStatus rowStatus)
                return rowStatus;

            return (sender as DataGrid)?.SelectedItem as PartMaintenanceComponentStatus;
        }

        private static PartMaintenanceComponentStatus FindMatchingComponent(
            ObservableCollection<PartMaintenanceComponentStatus> options,
            PartMaintenanceComponentStatus status)
        {
            if (options == null || status == null)
                return null;

            var referenceMatch = options.FirstOrDefault(x => ReferenceEquals(x, status));
            if (referenceMatch != null)
                return referenceMatch;

            return options.FirstOrDefault(x =>
                string.Equals(x.ComponentName, status.ComponentName, StringComparison.OrdinalIgnoreCase));
        }

        private static T FindVisualParent<T>(DependencyObject current)
            where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T typed)
                    return typed;

                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }

        private static void UpdateChartHover(SKElement chart, System.Collections.Generic.IList<PartMaintenanceTrendPoint> points, Point position, ref int? hoverIndex)
        {
            var nextIndex = FindNearestTrendPoint(points, position, chart.ActualWidth, chart.ActualHeight);
            if (hoverIndex != nextIndex)
            {
                hoverIndex = nextIndex;
                chart.InvalidateVisual();
            }
        }

        private static void UpdateChartHover(
            SKElement chart,
            System.Collections.Generic.IList<PartMaintenanceTrendPoint> points,
            System.Collections.Generic.IList<PartMaintenanceTrendPoint> overlayPoints,
            Point position,
            ref int? hoverIndex,
            ref int? overlayHoverIndex)
        {
            var range = GetTrendRange(points, overlayPoints);
            var nextIndex = FindNearestTrendPoint(points, position, chart.ActualWidth, chart.ActualHeight, range, out var distance);
            var nextOverlayIndex = FindNearestTrendPoint(overlayPoints, position, chart.ActualWidth, chart.ActualHeight, range, out var overlayDistance);

            if (nextIndex.HasValue && nextOverlayIndex.HasValue)
            {
                if (overlayDistance < distance)
                    nextIndex = null;
                else
                    nextOverlayIndex = null;
            }

            if (hoverIndex != nextIndex || overlayHoverIndex != nextOverlayIndex)
            {
                hoverIndex = nextIndex;
                overlayHoverIndex = nextOverlayIndex;
                chart.InvalidateVisual();
            }
        }

        private static void ClearChartHover(SKElement chart, ref int? hoverIndex)
        {
            if (hoverIndex.HasValue)
            {
                hoverIndex = null;
                chart.InvalidateVisual();
            }
        }

        private static void ClearChartHover(SKElement chart, ref int? hoverIndex, ref int? overlayHoverIndex)
        {
            if (hoverIndex.HasValue || overlayHoverIndex.HasValue)
            {
                hoverIndex = null;
                overlayHoverIndex = null;
                chart.InvalidateVisual();
            }
        }

        private static int? FindNearestTrendPoint(System.Collections.Generic.IList<PartMaintenanceTrendPoint> points, Point position, double width, double height)
        {
            return FindNearestTrendPoint(points, position, width, height, GetTrendRange(points), out _);
        }

        private static int? FindNearestTrendPoint(
            System.Collections.Generic.IList<PartMaintenanceTrendPoint> points,
            Point position,
            double width,
            double height,
            (double min, double max) range,
            out double bestDistance)
        {
            bestDistance = double.MaxValue;
            if (points == null || points.Count == 0 || width <= 0d || height <= 0d)
                return null;

            var rect = GetChartRect((float)width, (float)height);
            if (position.X < rect.Left - 14 || position.X > rect.Right + 14 || position.Y < rect.Top - 14 || position.Y > rect.Bottom + 14)
                return null;

            var bestIndex = -1;
            for (var i = 0; i < points.Count; i++)
            {
                var chartPoint = GetChartPoint(rect, points, i, range.min, range.max);
                var dx = position.X - chartPoint.X;
                var dy = position.Y - chartPoint.Y;
                var distance = Math.Sqrt(dx * dx + dy * dy);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestIndex = i;
                }
            }

            return bestDistance <= 18d && bestIndex >= 0 ? (int?)bestIndex : null;
        }

        private static void DrawTrendChart(
            SKCanvas canvas,
            SKImageInfo info,
            System.Collections.Generic.IList<PartMaintenanceTrendPoint> points,
            string title,
            int? hoverIndex,
            System.Collections.Generic.IList<PartMaintenanceTrendPoint> overlayPoints = null,
            int? overlayHoverIndex = null,
            string primaryLegendText = "趋势点",
            string overlayLegendText = null)
        {
            canvas.Clear(SKColors.White);
            var rect = GetChartRect(info.Width, info.Height);
            var typeface = ResolveChartTypeface();
            var hasPoints = points != null && points.Count > 0;
            var hasOverlay = overlayPoints != null && overlayPoints.Count > 0;

            using (var axisPaint = new SKPaint { Color = new SKColor(148, 163, 184), StrokeWidth = 1, IsAntialias = true })
            using (var gridPaint = new SKPaint { Color = new SKColor(226, 232, 240), StrokeWidth = 1, IsAntialias = true })
            using (var textPaint = new SKPaint { Color = new SKColor(71, 85, 105), TextSize = 12, IsAntialias = true, Typeface = typeface })
            using (var legendPaint = new SKPaint { Color = new SKColor(51, 65, 85), TextSize = 12, IsAntialias = true, Typeface = typeface })
            using (var linePaint = new SKPaint { Color = new SKColor(37, 99, 235), StrokeWidth = 3, IsAntialias = true, Style = SKPaintStyle.Stroke })
            using (var fillPaint = new SKPaint { Color = new SKColor(37, 99, 235), IsAntialias = true })
            using (var overlayLinePaint = new SKPaint { Color = new SKColor(22, 163, 74), StrokeWidth = 3, IsAntialias = true, Style = SKPaintStyle.Stroke })
            using (var overlayFillPaint = new SKPaint { Color = new SKColor(22, 163, 74), IsAntialias = true })
            using (var abnormalPaint = new SKPaint { Color = new SKColor(220, 38, 38), IsAntialias = true })
            using (var hoverFillPaint = new SKPaint { Color = new SKColor(245, 158, 11), IsAntialias = true })
            using (var hoverLinePaint = new SKPaint { Color = new SKColor(245, 158, 11, 120), StrokeWidth = 1.5f, IsAntialias = true })
            {
                DrawLegend(canvas, rect, fillPaint, abnormalPaint, legendPaint, primaryLegendText, overlayFillPaint, overlayLegendText);
                canvas.DrawLine(rect.Left, rect.Top, rect.Left, rect.Bottom, axisPaint);
                canvas.DrawLine(rect.Left, rect.Bottom, rect.Right, rect.Bottom, axisPaint);

                if (!hasPoints && !hasOverlay)
                {
                    DrawCenteredText(canvas, title + "暂无数据", info, textPaint);
                    return;
                }

                var range = GetTrendRange(points, overlayPoints);
                var min = range.min;
                var max = range.max;

                for (var i = 0; i <= 4; i++)
                {
                    var y = rect.Bottom - (rect.Height * i / 4f);
                    canvas.DrawLine(rect.Left, y, rect.Right, y, gridPaint);
                    var value = min + (max - min) * i / 4d;
                    canvas.DrawText(value.ToString("0.##"), 8, y + 4, textPaint);
                }

                DrawTrendSeries(canvas, rect, points, min, max, linePaint, fillPaint, abnormalPaint, textPaint, hoverIndex, hoverFillPaint, hoverLinePaint, drawLabels: hasPoints, seriesName: primaryLegendText);
                DrawTrendSeries(canvas, rect, overlayPoints, min, max, overlayLinePaint, overlayFillPaint, abnormalPaint, textPaint, overlayHoverIndex, hoverFillPaint, hoverLinePaint, drawLabels: !hasPoints, seriesName: overlayLegendText);
            }
        }

        private static void DrawTrendSeries(
            SKCanvas canvas,
            SKRect rect,
            System.Collections.Generic.IList<PartMaintenanceTrendPoint> points,
            double min,
            double max,
            SKPaint linePaint,
            SKPaint fillPaint,
            SKPaint abnormalPaint,
            SKPaint textPaint,
            int? hoverIndex,
            SKPaint hoverFillPaint,
            SKPaint hoverLinePaint,
            bool drawLabels,
            string seriesName = null)
        {
            if (points == null || points.Count == 0)
                return;

            var path = new SKPath();
            for (var i = 0; i < points.Count; i++)
            {
                var point = points[i];
                var chartPoint = GetChartPoint(rect, points, i, min, max);
                var x = chartPoint.X;
                var y = chartPoint.Y;

                if (i == 0) path.MoveTo(x, y);
                else path.LineTo(x, y);

                var radius = points.Count == 1 ? 5.5f : (point.HasAbnormal ? 5.5f : 4f);
                canvas.DrawCircle(x, y, radius, point.HasAbnormal ? abnormalPaint : fillPaint);
                if (points.Count == 1)
                {
                    canvas.DrawText(point.Value.ToString("0.###"), x + 10f, y - 8f, textPaint);
                }

                if (drawLabels && ShouldDrawLabel(i, points.Count))
                {
                    canvas.DrawText(point.Label, x - 16, rect.Bottom + 20, textPaint);
                }
            }

            canvas.DrawPath(path, linePaint);

            if (hoverIndex.HasValue && hoverIndex.Value >= 0 && hoverIndex.Value < points.Count)
            {
                var chartPoint = GetChartPoint(rect, points, hoverIndex.Value, min, max);
                canvas.DrawLine(chartPoint.X, rect.Top, chartPoint.X, rect.Bottom, hoverLinePaint);
                canvas.DrawCircle(chartPoint.X, chartPoint.Y, 7f, hoverFillPaint);
                canvas.DrawCircle(chartPoint.X, chartPoint.Y, 3.5f, fillPaint);
                DrawHoverTooltip(canvas, rect, chartPoint, points[hoverIndex.Value], textPaint, seriesName);
            }
        }

        private static SKRect GetChartRect(float width, float height)
        {
            return new SKRect(58, 42, Math.Max(100, width - 20), Math.Max(80, height - 44));
        }

        private static (double min, double max) GetTrendRange(System.Collections.Generic.IList<PartMaintenanceTrendPoint> points)
        {
            return GetTrendRange(points, null);
        }

        private static (double min, double max) GetTrendRange(
            System.Collections.Generic.IList<PartMaintenanceTrendPoint> points,
            System.Collections.Generic.IList<PartMaintenanceTrendPoint> overlayPoints)
        {
            var values = Enumerable.Empty<PartMaintenanceTrendPoint>();
            if (points != null)
                values = values.Concat(points);
            if (overlayPoints != null)
                values = values.Concat(overlayPoints);

            var list = values.ToList();
            if (list.Count == 0)
                return (0d, 1d);

            var dataMin = list.Min(x => x.Value);
            var dataMax = list.Max(x => x.Value);
            var center = (dataMin + dataMax) / 2d;
            var dataSpan = Math.Max(0.0001d, dataMax - dataMin);
            var visibleSpan = Math.Max(dataSpan * 1.6d, Math.Max(Math.Abs(center) * 0.35d, 0.05d));
            var min = center - visibleSpan / 2d;
            var max = center + visibleSpan / 2d;

            if (dataMin >= 0d && min < 0d)
            {
                max -= min;
                min = 0d;
            }

            return (min, max);
        }

        private static SKPoint GetChartPoint(SKRect rect, System.Collections.Generic.IList<PartMaintenanceTrendPoint> points, int index, double min, double max)
        {
            var point = points[index];
            var x = points.Count == 1
                ? rect.Left + rect.Width / 2f
                : rect.Left + rect.Width * index / (points.Count - 1);
            var y = rect.Bottom - (float)((point.Value - min) / (max - min) * rect.Height);
            return new SKPoint(x, y);
        }

        private static void DrawHoverTooltip(SKCanvas canvas, SKRect chartRect, SKPoint pointLocation, PartMaintenanceTrendPoint point, SKPaint textPaint, string seriesName = null)
        {
            if (point == null)
                return;

            var lines = new List<string>();
            if (!string.IsNullOrWhiteSpace(seriesName))
                lines.Add("侧别：" + seriesName);
            lines.Add("日期：" + point.Date.ToString("yyyy-MM-dd"));
            lines.Add("趋势：" + point.Value.ToString("0.###"));

            var paddingX = 10f;
            var paddingY = 8f;
            var lineHeight = 19f;
            var maxTextWidth = lines.Max(x => textPaint.MeasureText(x));
            var boxWidth = maxTextWidth + paddingX * 2f;
            var boxHeight = paddingY * 2f + lineHeight * lines.Count;
            var left = pointLocation.X + 12f;
            var top = pointLocation.Y - boxHeight - 12f;

            if (left + boxWidth > chartRect.Right)
                left = pointLocation.X - boxWidth - 12f;
            if (left < chartRect.Left)
                left = chartRect.Left;
            if (top < chartRect.Top)
                top = pointLocation.Y + 12f;
            if (top + boxHeight > chartRect.Bottom)
                top = chartRect.Bottom - boxHeight;

            var box = new SKRect(left, top, left + boxWidth, top + boxHeight);
            using (var shadowPaint = new SKPaint { Color = new SKColor(15, 23, 42, 38), IsAntialias = true })
            using (var backgroundPaint = new SKPaint { Color = SKColors.White, IsAntialias = true })
            using (var borderPaint = new SKPaint { Color = new SKColor(245, 158, 11), StrokeWidth = 1.2f, IsAntialias = true, Style = SKPaintStyle.Stroke })
            {
                var shadow = new SKRect(box.Left + 2f, box.Top + 2f, box.Right + 2f, box.Bottom + 2f);
                canvas.DrawRoundRect(shadow, 6f, 6f, shadowPaint);
                canvas.DrawRoundRect(box, 6f, 6f, backgroundPaint);
                canvas.DrawRoundRect(box, 6f, 6f, borderPaint);
            }

            for (var i = 0; i < lines.Count; i++)
            {
                canvas.DrawText(lines[i], box.Left + paddingX, box.Top + paddingY + 14f + lineHeight * i, textPaint);
            }
        }

        private static SKTypeface ResolveChartTypeface()
        {
            return SKTypeface.FromFamilyName("Microsoft YaHei")
                   ?? SKTypeface.FromFamilyName("SimSun")
                   ?? SKTypeface.Default;
        }

        private static bool ShouldDrawLabel(int index, int count)
        {
            if (count <= 8)
                return true;

            return index == 0 || index == count - 1 || index % Math.Max(1, count / 6) == 0;
        }

        private static void DrawLegend(
            SKCanvas canvas,
            SKRect rect,
            SKPaint trendPaint,
            SKPaint abnormalPaint,
            SKPaint textPaint,
            string trendText = "趋势点",
            SKPaint overlayPaint = null,
            string overlayText = null)
        {
            const string abnormalText = "异常点";
            var trendWidth = textPaint.MeasureText(trendText);
            var overlayWidth = string.IsNullOrWhiteSpace(overlayText) ? 0f : textPaint.MeasureText(overlayText);
            var abnormalWidth = textPaint.MeasureText(abnormalText);
            var totalWidth = 8f + 6f + trendWidth + 22f + 8f + 6f + abnormalWidth;
            if (!string.IsNullOrWhiteSpace(overlayText) && overlayPaint != null)
                totalWidth += 22f + 8f + 6f + overlayWidth;
            var x = Math.Max(rect.Left, rect.Right - totalWidth);
            var y = rect.Top - 22f;

            canvas.DrawCircle(x + 4f, y + 6f, 4f, trendPaint);
            canvas.DrawText(trendText, x + 14f, y + 10f, textPaint);

            var abnormalX = x + 14f + trendWidth + 22f;
            if (!string.IsNullOrWhiteSpace(overlayText) && overlayPaint != null)
            {
                canvas.DrawCircle(abnormalX + 4f, y + 6f, 4f, overlayPaint);
                canvas.DrawText(overlayText, abnormalX + 14f, y + 10f, textPaint);
                abnormalX += 14f + overlayWidth + 22f;
            }

            canvas.DrawCircle(abnormalX + 4f, y + 6f, 4f, abnormalPaint);
            canvas.DrawText(abnormalText, abnormalX + 14f, y + 10f, textPaint);
        }

        private static void DrawCenteredText(SKCanvas canvas, string text, SKImageInfo info, SKPaint paint)
        {
            var width = paint.MeasureText(text);
            canvas.DrawText(text, (info.Width - width) / 2f, info.Height / 2f, paint);
        }

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}

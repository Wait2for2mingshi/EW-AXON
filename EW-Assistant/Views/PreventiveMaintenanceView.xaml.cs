using EW_Assistant.Services;
using EW_Assistant.Services.PreventiveMaintenance;
using Newtonsoft.Json;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace EW_Assistant.Views
{
    public partial class PreventiveMaintenanceView : UserControl, INotifyPropertyChanged
    {
        private readonly PartMaintenanceAnalyzer _analyzer = new PartMaintenanceAnalyzer();
        private const string ReportCacheFilePath = @"D:\DataAI\preventive_maintenance_report_cache.json";
        private const int ReportCacheVersion = 2;
        private const int StartupPreloadDelayMs = 5000;
        private const int MinimumRefreshIndicatorMs = 600;
        private const string RefreshButtonText = "重新分析";
        private const string RefreshInProgressText = "正在分析中，再点击一次可以取消重新分析";
        private const string RefreshCancelingText = "正在取消重新分析...";
        private const string RefreshCanceledText = "已取消重新分析";
        private const string RefreshCompletedText = "重新分析完成";
        private static readonly object s_reportCacheLock = new object();
        private static PreventiveMaintenanceReportCache s_cachedReportCache;
        private static Task s_startupPreloadTask;
        private static bool s_startupPreloadIsAnalyzing;
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
        private int? _hoverVacuumPointIndex;
        private bool _isRefreshing;
        private CancellationTokenSource _refreshCancellationTokenSource;
        private string _refreshStatusText = string.Empty;
        private DateTime? _latestDataAnchorDate;

        public PreventiveMaintenanceView()
        {
            InitializeComponent();
            DataContext = this;
            Loaded += PreventiveMaintenanceView_Loaded;
        }

        public ObservableCollection<PartMaintenanceRisk> Risks { get; } = new ObservableCollection<PartMaintenanceRisk>();
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
                    OnPropertyChanged();
                    CylinderSummary = BuildComponentTrendSummary("气缸", _selectedCylinder);
                    CylinderChart?.InvalidateVisual();
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
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public static void PreloadAllData()
        {
            var range = CreateAllRange();
            lock (s_reportCacheLock)
            {
                if (s_startupPreloadTask != null && !s_startupPreloadTask.IsCompleted)
                    return;

                s_startupPreloadTask = Task.Run(async () =>
                {
                    await Task.Delay(StartupPreloadDelayMs);

                    PartMaintenanceReport cached;
                    if (TryGetCachedReport(range, out cached))
                        return;

                    if (!TryBeginStartupAnalysis())
                        return;

                    try
                    {
                        PreloadAllDataCore(range);
                    }
                    finally
                    {
                        EndStartupAnalysis();
                    }
                });
            }
        }

        private static void PreloadAllDataCore(DateRangeSelection range)
        {
            try
            {
                var path = ConfigService.Current?.PartCsvPath ?? string.Empty;
                var report = new PartMaintenanceAnalyzer().Analyze(path, range.StartDate, range.EndDate);
                StoreCachedReport(report, range);
                SaveCachedReport(report, range);
                MainWindow.PostProgramInfo("预防维护全部数据已预热。", "ok");
            }
            catch (Exception ex)
            {
                MainWindow.PostProgramInfo("预防维护全部数据预热失败：" + ex.Message, "warn");
            }
        }

        private async void PreventiveMaintenanceView_Loaded(object sender, RoutedEventArgs e)
        {
            if (TryShowCachedReport())
                return;

            await RefreshAnalysisAsync(usePendingPreload: true);
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

        private async Task RefreshAnalysisAsync(bool usePendingPreload = false)
        {
            if (IsRefreshing)
                return;

            var range = ResolveSelectedDateRange();
            var ownsStartupAnalysis = false;
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
                if (usePendingPreload)
                {
                    var preloadTask = GetStartupPreloadTask(range);
                    if (preloadTask != null)
                    {
                        await preloadTask;
                        cancellationSource.Token.ThrowIfCancellationRequested();

                        PartMaintenanceReport preloadedReport;
                        if (TryGetCachedReport(range, out preloadedReport))
                        {
                            ApplyReport(preloadedReport);
                            return;
                        }
                    }
                }

                var path = ConfigService.Current?.PartCsvPath ?? string.Empty;
                if (IsStartupPreloadRange(range))
                    ownsStartupAnalysis = TryBeginStartupAnalysis();

                var token = cancellationSource.Token;
                var report = await Task.Run(() => _analyzer.Analyze(path, range.StartDate, range.EndDate, token), token);
                ApplyReport(report);
                RefreshStatusText = string.IsNullOrWhiteSpace(report.StatusMessage)
                    ? RefreshCompletedText
                    : RefreshCompletedText + "：" + report.StatusMessage;
                StoreCachedReport(report, range);
                _ = Task.Run(() => SaveCachedReport(report, range));
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
                if (ownsStartupAnalysis)
                    EndStartupAnalysis();

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

        private void ApplyReport(PartMaintenanceReport report)
        {
            _report = report;
            FileCountText = _report.FileCount.ToString();
            LatestDateText = _report.LatestDate.HasValue ? _report.LatestDate.Value.ToString("MM-dd") : "-";
            if (_report.LatestDate.HasValue)
                _latestDataAnchorDate = _report.LatestDate.Value.Date;

            Risks.Clear();
            foreach (var risk in _report.Risks)
            {
                Risks.Add(risk);
            }

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
            CylinderChart?.InvalidateVisual();
            VacuumChart?.InvalidateVisual();
        }

        private void ApplyLoadingState()
        {
            FileCountText = "-";
            LatestDateText = "-";

            var cylinder = new PartMaintenanceComponentStatus
            {
                PartType = "气缸",
                ComponentName = "正在分析...",
                Summary = "正在读取气缸数据。",
                Suggestion = "正在读取零件 CSV，并生成气缸预防维护建议，请稍候。"
            };

            var vacuum = new PartMaintenanceComponentStatus
            {
                PartType = "吸嘴",
                ComponentName = "正在分析...",
                Summary = "正在读取吸嘴数据。",
                Suggestion = "正在读取零件 CSV，并生成吸嘴预防维护建议，请稍候。"
            };

            CylinderOptions.Clear();
            CylinderOptions.Add(cylinder);
            VacuumOptions.Clear();
            VacuumOptions.Add(vacuum);

            CylinderStatuses.Clear();
            VacuumStatuses.Clear();
            Risks.Clear();

            SelectedCylinder = cylinder;
            SelectedVacuum = vacuum;
            CylinderSummary = "正在分析气缸数据...";
            VacuumSummary = "正在分析吸嘴数据...";
            CylinderChart?.InvalidateVisual();
            VacuumChart?.InvalidateVisual();
        }

        private bool TryShowCachedReport()
        {
            if (_report != null)
                return true;

            var range = ResolveSelectedDateRange();
            PartMaintenanceReport cached;
            if (!TryGetCachedReport(range, out cached))
                return false;

            ApplyReport(cached);
            return true;
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

        private static Task GetStartupPreloadTask(DateRangeSelection range)
        {
            if (!IsStartupPreloadRange(range))
                return null;

            lock (s_reportCacheLock)
            {
                return s_startupPreloadIsAnalyzing ? s_startupPreloadTask : null;
            }
        }

        private static bool TryBeginStartupAnalysis()
        {
            lock (s_reportCacheLock)
            {
                if (s_startupPreloadIsAnalyzing)
                    return false;

                s_startupPreloadIsAnalyzing = true;
                return true;
            }
        }

        private static void EndStartupAnalysis()
        {
            lock (s_reportCacheLock)
            {
                s_startupPreloadIsAnalyzing = false;
            }
        }

        private static bool IsStartupPreloadRange(DateRangeSelection range)
        {
            return IsSameRange("全部", null, null, range);
        }

        private static bool TryGetCachedReport(DateRangeSelection range, out PartMaintenanceReport report)
        {
            lock (s_reportCacheLock)
            {
                if (s_cachedReportCache != null
                    && IsSameRange(s_cachedReportCache.RangeMode, s_cachedReportCache.StartDate, s_cachedReportCache.EndDate, range))
                {
                    report = s_cachedReportCache.Report;
                    return report != null;
                }
            }

            report = LoadCachedReport(range);
            if (report == null)
                return false;

            StoreCachedReport(report, range);
            return true;
        }

        private static void StoreCachedReport(PartMaintenanceReport report, DateRangeSelection range)
        {
            if (report == null)
                return;

            lock (s_reportCacheLock)
            {
                s_cachedReportCache = new PreventiveMaintenanceReportCache
                {
                    Version = ReportCacheVersion,
                    RangeMode = range?.Mode ?? "全部",
                    StartDate = range?.StartDate,
                    EndDate = range?.EndDate,
                    Report = report
                };
            }
        }

        private static PartMaintenanceReport LoadCachedReport(DateRangeSelection range)
        {
            try
            {
                if (!File.Exists(ReportCacheFilePath))
                    return null;

                var json = File.ReadAllText(ReportCacheFilePath);
                if (string.IsNullOrWhiteSpace(json))
                    return null;

                var cache = JsonConvert.DeserializeObject<PreventiveMaintenanceReportCache>(json);
                if (cache != null
                    && cache.Version == ReportCacheVersion
                    && cache.Report != null
                    && IsSameRange(cache.RangeMode, cache.StartDate, cache.EndDate, range))
                {
                    return cache.Report;
                }

                if (cache != null && (cache.Report != null || !string.IsNullOrWhiteSpace(cache.RangeMode)))
                    return null;

                if (range != null && range.IsAll)
                    return JsonConvert.DeserializeObject<PartMaintenanceReport>(json);

                return null;
            }
            catch
            {
                return null;
            }
        }

        private static void SaveCachedReport(PartMaintenanceReport report, DateRangeSelection range)
        {
            if (report == null)
                return;

            try
            {
                var dir = Path.GetDirectoryName(ReportCacheFilePath);
                if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var cache = new PreventiveMaintenanceReportCache
                {
                    Version = ReportCacheVersion,
                    RangeMode = range?.Mode ?? "全部",
                    StartDate = range?.StartDate,
                    EndDate = range?.EndDate,
                    Report = report
                };
                var json = JsonConvert.SerializeObject(cache, Formatting.Indented);
                var temp = ReportCacheFilePath + ".tmp";
                File.WriteAllText(temp, json);
                File.Copy(temp, ReportCacheFilePath, true);
                File.Delete(temp);
            }
            catch
            {
                // 缓存失败不影响页面主流程。
            }
        }

        private static bool IsSameRange(string mode, DateTime? startDate, DateTime? endDate, DateRangeSelection range)
        {
            if (range == null)
                return false;

            return string.Equals(mode ?? "全部", range.Mode ?? "全部", StringComparison.OrdinalIgnoreCase)
                   && NullableDateEquals(startDate, range.StartDate)
                   && NullableDateEquals(endDate, range.EndDate);
        }

        private static bool NullableDateEquals(DateTime? left, DateTime? right)
        {
            if (!left.HasValue && !right.HasValue)
                return true;
            if (!left.HasValue || !right.HasValue)
                return false;

            return left.Value.Date == right.Value.Date;
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
            public bool IsAll => !StartDate.HasValue && !EndDate.HasValue;
        }

        private sealed class PreventiveMaintenanceReportCache
        {
            public int Version { get; set; }
            public string RangeMode { get; set; }
            public DateTime? StartDate { get; set; }
            public DateTime? EndDate { get; set; }
            public PartMaintenanceReport Report { get; set; }
        }

        private static string BuildComponentTrendSummary(string name, PartMaintenanceComponentStatus status)
        {
            if (status == null || status.Trend.Count == 0)
                return "请选择" + name + "查看趋势。";

            var latest = status.Trend[status.Trend.Count - 1];
            return status.ComponentName
                   + " 最近日期 " + latest.Date.ToString("yyyy-MM-dd")
                   + "，趋势值 " + latest.Value.ToString("0.###")
                   + "，风险 " + status.RiskLevel
                   + "，分数 " + status.RiskScore + "。";
        }

        private void CylinderChart_PaintSurface(object sender, SKPaintSurfaceEventArgs e)
        {
            DrawTrendChart(e.Surface.Canvas, e.Info, SelectedCylinder?.Trend, "气缸", _hoverCylinderPointIndex);
        }

        private void VacuumChart_PaintSurface(object sender, SKPaintSurfaceEventArgs e)
        {
            DrawTrendChart(e.Surface.Canvas, e.Info, SelectedVacuum?.Trend, "吸嘴", _hoverVacuumPointIndex);
        }

        private void CylinderChart_MouseMove(object sender, MouseEventArgs e)
        {
            UpdateChartHover(CylinderChart, SelectedCylinder?.Trend, e.GetPosition(CylinderChart), ref _hoverCylinderPointIndex);
        }

        private void VacuumChart_MouseMove(object sender, MouseEventArgs e)
        {
            UpdateChartHover(VacuumChart, SelectedVacuum?.Trend, e.GetPosition(VacuumChart), ref _hoverVacuumPointIndex);
        }

        private void CylinderChart_MouseLeave(object sender, MouseEventArgs e)
        {
            ClearChartHover(CylinderChart, ref _hoverCylinderPointIndex);
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

        private static void ClearChartHover(SKElement chart, ref int? hoverIndex)
        {
            if (hoverIndex.HasValue)
            {
                hoverIndex = null;
                chart.InvalidateVisual();
            }
        }

        private static int? FindNearestTrendPoint(System.Collections.Generic.IList<PartMaintenanceTrendPoint> points, Point position, double width, double height)
        {
            if (points == null || points.Count == 0 || width <= 0d || height <= 0d)
                return null;

            var rect = GetChartRect((float)width, (float)height);
            if (position.X < rect.Left - 14 || position.X > rect.Right + 14 || position.Y < rect.Top - 14 || position.Y > rect.Bottom + 14)
                return null;

            var range = GetTrendRange(points);
            var bestIndex = -1;
            var bestDistance = double.MaxValue;
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

        private static string GetPointRiskLevel(PartMaintenanceTrendPoint point)
        {
            if (point == null)
                return "未知";

            if (point.HasAbnormal)
                return "高风险";

            if (point.Kind == PartMaintenanceKind.Vacuum)
            {
                if (point.Value >= 45d) return "高风险";
                if (point.Value >= 15d) return "中风险";
                if (point.Value > 0d) return "低风险";
                return "正常";
            }

            if (point.Value >= 0.7d) return "高风险";
            if (point.Value >= 0.35d) return "中风险";
            if (point.Value > 0d) return "低风险";
            return "正常";
        }

        private static void DrawTrendChart(SKCanvas canvas, SKImageInfo info, System.Collections.Generic.IList<PartMaintenanceTrendPoint> points, string title, int? hoverIndex)
        {
            canvas.Clear(SKColors.White);
            var rect = GetChartRect(info.Width, info.Height);
            var typeface = ResolveChartTypeface();

            using (var axisPaint = new SKPaint { Color = new SKColor(148, 163, 184), StrokeWidth = 1, IsAntialias = true })
            using (var gridPaint = new SKPaint { Color = new SKColor(226, 232, 240), StrokeWidth = 1, IsAntialias = true })
            using (var textPaint = new SKPaint { Color = new SKColor(71, 85, 105), TextSize = 12, IsAntialias = true, Typeface = typeface })
            using (var legendPaint = new SKPaint { Color = new SKColor(51, 65, 85), TextSize = 12, IsAntialias = true, Typeface = typeface })
            using (var linePaint = new SKPaint { Color = new SKColor(37, 99, 235), StrokeWidth = 3, IsAntialias = true, Style = SKPaintStyle.Stroke })
            using (var fillPaint = new SKPaint { Color = new SKColor(37, 99, 235), IsAntialias = true })
            using (var abnormalPaint = new SKPaint { Color = new SKColor(220, 38, 38), IsAntialias = true })
            using (var hoverFillPaint = new SKPaint { Color = new SKColor(245, 158, 11), IsAntialias = true })
            using (var hoverLinePaint = new SKPaint { Color = new SKColor(245, 158, 11, 120), StrokeWidth = 1.5f, IsAntialias = true })
            {
                DrawLegend(canvas, rect, fillPaint, abnormalPaint, legendPaint);
                canvas.DrawLine(rect.Left, rect.Top, rect.Left, rect.Bottom, axisPaint);
                canvas.DrawLine(rect.Left, rect.Bottom, rect.Right, rect.Bottom, axisPaint);

                if (points == null || points.Count == 0)
                {
                    DrawCenteredText(canvas, title + "暂无数据", info, textPaint);
                    return;
                }

                var range = GetTrendRange(points);
                var min = range.min;
                var max = range.max;

                for (var i = 0; i <= 4; i++)
                {
                    var y = rect.Bottom - (rect.Height * i / 4f);
                    canvas.DrawLine(rect.Left, y, rect.Right, y, gridPaint);
                    var value = min + (max - min) * i / 4d;
                    canvas.DrawText(value.ToString("0.##"), 8, y + 4, textPaint);
                }

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

                    if (ShouldDrawLabel(i, points.Count))
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
                    DrawHoverTooltip(canvas, rect, chartPoint, points[hoverIndex.Value], textPaint);
                }
            }
        }

        private static SKRect GetChartRect(float width, float height)
        {
            return new SKRect(58, 42, Math.Max(100, width - 20), Math.Max(80, height - 44));
        }

        private static (double min, double max) GetTrendRange(System.Collections.Generic.IList<PartMaintenanceTrendPoint> points)
        {
            var max = Math.Max(1d, points.Max(x => x.Value));
            var min = Math.Min(0d, points.Min(x => x.Value));
            if (Math.Abs(max - min) < 0.0001d)
            {
                max += 1d;
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

        private static void DrawHoverTooltip(SKCanvas canvas, SKRect chartRect, SKPoint pointLocation, PartMaintenanceTrendPoint point, SKPaint textPaint)
        {
            if (point == null)
                return;

            var lines = new[]
            {
                "日期：" + point.Date.ToString("yyyy-MM-dd"),
                "趋势：" + point.Value.ToString("0.###"),
                "风险：" + GetPointRiskLevel(point)
            };

            var paddingX = 10f;
            var paddingY = 8f;
            var lineHeight = 19f;
            var maxTextWidth = lines.Max(x => textPaint.MeasureText(x));
            var boxWidth = maxTextWidth + paddingX * 2f;
            var boxHeight = paddingY * 2f + lineHeight * lines.Length;
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

            for (var i = 0; i < lines.Length; i++)
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

        private static void DrawLegend(SKCanvas canvas, SKRect rect, SKPaint trendPaint, SKPaint abnormalPaint, SKPaint textPaint)
        {
            const string trendText = "趋势点";
            const string abnormalText = "异常点";
            var trendWidth = textPaint.MeasureText(trendText);
            var abnormalWidth = textPaint.MeasureText(abnormalText);
            var totalWidth = 8f + 6f + trendWidth + 22f + 8f + 6f + abnormalWidth;
            var x = Math.Max(rect.Left, rect.Right - totalWidth);
            var y = rect.Top - 22f;

            canvas.DrawCircle(x + 4f, y + 6f, 4f, trendPaint);
            canvas.DrawText(trendText, x + 14f, y + 10f, textPaint);

            var abnormalX = x + 14f + trendWidth + 22f;
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

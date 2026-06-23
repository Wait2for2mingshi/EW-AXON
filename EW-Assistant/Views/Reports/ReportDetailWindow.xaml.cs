using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using EW_Assistant.Domain.Reports;
using EW_Assistant.Services;
using EW_Assistant.Services.Reports;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace EW_Assistant.Views.Reports
{
    public partial class ReportDetailWindow : Window
    {
        private readonly ReportInfo _reportInfo;
        private readonly ReportExportService _exportService;
        private SKImageInfo _primaryChartInfo;
        private SKImageInfo _secondaryChartInfo;
        private SKImageInfo _compactTertiaryChartInfo;
        private SKImageInfo _standaloneTertiaryChartInfo;
        private ReportChartHitInfo _primaryHoverHit;
        private ReportChartHitInfo _secondaryHoverHit;
        private ReportChartHitInfo _tertiaryHoverHit;
        private ReportChartHitInfo _primarySelectedHit;
        private ReportChartHitInfo _secondarySelectedHit;
        private ReportChartHitInfo _tertiarySelectedHit;

        public ReportDetailWindow(ReportInfo info, StructuredReportPreview preview)
        {
            InitializeComponent();
            _reportInfo = info;
            _exportService = new ReportExportService();
            ViewModel = new ReportDetailWindowViewModel(info, preview);
            DataContext = ViewModel;
            Title = string.IsNullOrWhiteSpace(ViewModel.ReportTitle) ? L("报表详情") : ViewModel.ReportTitle;
            Loaded += ReportDetailWindow_Loaded;
        }

        public ReportDetailWindowViewModel ViewModel { get; }

        private static string L(string chineseText)
        {
            return UiLanguageService.CurrentText(chineseText);
        }

        private void ReportDetailWindow_Loaded(object sender, RoutedEventArgs e)
        {
            InvalidateCharts();
        }

        private void PrimaryChart_PaintSurface(object sender, SKPaintSurfaceEventArgs e)
        {
            _primaryChartInfo = e.Info;
            ReportChartDrawingHelper.DrawChart(ViewModel.PrimaryChart, e.Surface.Canvas, e.Info, _primaryHoverHit, _primarySelectedHit);
        }

        private void SecondaryChart_PaintSurface(object sender, SKPaintSurfaceEventArgs e)
        {
            _secondaryChartInfo = e.Info;
            ReportChartDrawingHelper.DrawChart(ViewModel.SecondaryChart, e.Surface.Canvas, e.Info, _secondaryHoverHit, _secondarySelectedHit);
        }

        private void TertiaryChart_PaintSurface(object sender, SKPaintSurfaceEventArgs e)
        {
            if (ReferenceEquals(sender, CompactTertiaryChartCanvas))
            {
                _compactTertiaryChartInfo = e.Info;
            }
            else
            {
                _standaloneTertiaryChartInfo = e.Info;
            }

            ReportChartDrawingHelper.DrawChart(ViewModel.TertiaryChart, e.Surface.Canvas, e.Info, _tertiaryHoverHit, _tertiarySelectedHit);
        }

        private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
        {
            string errorMessage;
            if (!ReportWindowHelper.TryRevealInExplorer(ViewModel.FilePath, out errorMessage))
            {
                MessageBox.Show(this, errorMessage, L("报表详情"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (_reportInfo == null)
            {
                MessageBox.Show(this, L("当前报表元数据缺失，无法导出。"), L("报表详情"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = ReportWindowHelper.CreateExportDialog(_reportInfo, L("导出报表"));

            var result = dialog.ShowDialog(this);
            if (result != true || string.IsNullOrWhiteSpace(dialog.FileName))
            {
                return;
            }

            try
            {
                var format = ReportWindowHelper.ResolveExportFormat(dialog.FilterIndex, dialog.FileName);
                var targetPath = ReportWindowHelper.NormalizeExportFilePath(format, dialog.FileName);
                _exportService.Export(_reportInfo, format, targetPath);
                MainWindow.PostProgramInfo(L("报表已导出：") + targetPath, "ok");
            }
            catch (Exception ex)
            {
                MainWindow.PostProgramInfo(L("导出失败：") + ex.Message, "error");
                MessageBox.Show(this, L("导出失败：") + ex.Message, L("报表详情"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void InvalidateCharts()
        {
            try
            {
                PrimaryChartCanvas?.InvalidateVisual();
                SecondaryChartCanvas?.InvalidateVisual();
                CompactTertiaryChartCanvas?.InvalidateVisual();
                StandaloneTertiaryChartCanvas?.InvalidateVisual();
            }
            catch
            {
                // 忽略图表刷新异常
            }
        }

        private void NestedScrollRegion_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (ContentScrollViewer == null)
            {
                return;
            }

            ContentScrollViewer.ScrollToVerticalOffset(ContentScrollViewer.VerticalOffset - e.Delta);
            e.Handled = true;
        }

        private void DetailTableGrid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var dataGrid = sender as DataGrid;
            if (dataGrid == null)
            {
                return;
            }

            var innerScrollViewer = FindDescendant<ScrollViewer>(dataGrid);
            if (innerScrollViewer == null)
            {
                NestedScrollRegion_PreviewMouseWheel(sender, e);
                return;
            }

            var scrollingUp = e.Delta > 0;
            var atTop = innerScrollViewer.VerticalOffset <= 0;
            var atBottom = innerScrollViewer.VerticalOffset >= innerScrollViewer.ScrollableHeight;
            var cannotScrollInternally = innerScrollViewer.ScrollableHeight <= 0;
            if (cannotScrollInternally || (scrollingUp && atTop) || (!scrollingUp && atBottom))
            {
                NestedScrollRegion_PreviewMouseWheel(sender, e);
            }
        }

        private void ChartCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            var element = sender as SKElement;
            if (element == null)
            {
                return;
            }

            var hit = ResolveChartHit(element, e.GetPosition(element));
            UpdateHoverState(element, hit);
            if (hit?.DataPoint == null)
            {
                element.Cursor = Cursors.Arrow;
                HideChartTooltip();
                return;
            }

            element.Cursor = Cursors.Hand;
            ShowChartTooltip(element, e.GetPosition(element), BuildTooltipText(hit.DataPoint));
        }

        private void ChartCanvas_MouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is SKElement element)
            {
                UpdateHoverState(element, null);
                element.Cursor = Cursors.Arrow;
            }

            HideChartTooltip();
        }

        private void ChartCanvas_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            var element = sender as SKElement;
            if (element == null)
            {
                return;
            }

            var hit = ResolveChartHit(element, e.GetPosition(element));
            if (hit?.DataPoint == null)
            {
                return;
            }

            HideChartTooltip();
            UpdateSelectedState(element, hit);
            var preservedOuterOffset = ContentScrollViewer != null ? ContentScrollViewer.VerticalOffset : 0d;
            SyncDetailTableSelection(hit?.DataPoint);
            RestoreOuterScrollOffset(preservedOuterOffset);
            InvalidateCharts();
            var window = new ReportChartDrilldownWindow(ViewModel.ReportTitle, hit.DataPoint)
            {
                Owner = this
            };
            window.Loaded += (loadedSender, loadedArgs) => RestoreOuterScrollOffset(preservedOuterOffset);
            window.Closed += (closedSender, closedArgs) => RestoreOuterScrollOffset(preservedOuterOffset);
            window.ShowDialog();
            RestoreOuterScrollOffset(preservedOuterOffset);
            e.Handled = true;
        }

        private static T FindDescendant<T>(DependencyObject source) where T : DependencyObject
        {
            if (source == null)
            {
                return null;
            }

            var count = VisualTreeHelper.GetChildrenCount(source);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(source, i);
                if (child is T typed)
                {
                    return typed;
                }

                var nested = FindDescendant<T>(child);
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }

        private ReportChartHitInfo ResolveChartHit(SKElement element, Point point)
        {
            var chart = ResolveChartDefinition(element);
            var info = ResolveChartInfo(element);
            if (chart == null || info.Width <= 0 || info.Height <= 0 || element.ActualWidth <= 0 || element.ActualHeight <= 0)
            {
                return null;
            }

            var skPoint = new SKPoint(
                (float)(point.X * info.Width / element.ActualWidth),
                (float)(point.Y * info.Height / element.ActualHeight));

            return ReportChartDrawingHelper.HitTest(chart, info, skPoint);
        }

        private void UpdateHoverState(SKElement element, ReportChartHitInfo hit)
        {
            if (ReferenceEquals(element, PrimaryChartCanvas))
            {
                if (SameHit(_primaryHoverHit, hit)) return;
                _primaryHoverHit = hit;
                PrimaryChartCanvas?.InvalidateVisual();
                return;
            }

            if (ReferenceEquals(element, SecondaryChartCanvas))
            {
                if (SameHit(_secondaryHoverHit, hit)) return;
                _secondaryHoverHit = hit;
                SecondaryChartCanvas?.InvalidateVisual();
                return;
            }

            if (ReferenceEquals(element, CompactTertiaryChartCanvas))
            {
                if (SameHit(_tertiaryHoverHit, hit)) return;
                _tertiaryHoverHit = hit;
                CompactTertiaryChartCanvas?.InvalidateVisual();
                return;
            }

            if (ReferenceEquals(element, StandaloneTertiaryChartCanvas))
            {
                if (SameHit(_tertiaryHoverHit, hit)) return;
                _tertiaryHoverHit = hit;
                StandaloneTertiaryChartCanvas?.InvalidateVisual();
            }
        }

        private void UpdateSelectedState(SKElement element, ReportChartHitInfo hit)
        {
            _primarySelectedHit = null;
            _secondarySelectedHit = null;
            _tertiarySelectedHit = null;

            if (ReferenceEquals(element, PrimaryChartCanvas))
            {
                _primarySelectedHit = hit;
                return;
            }

            if (ReferenceEquals(element, SecondaryChartCanvas))
            {
                _secondarySelectedHit = hit;
                return;
            }

            if (ReferenceEquals(element, CompactTertiaryChartCanvas) || ReferenceEquals(element, StandaloneTertiaryChartCanvas))
            {
                _tertiarySelectedHit = hit;
            }
        }

        private ReportChartDefinition ResolveChartDefinition(SKElement element)
        {
            if (ReferenceEquals(element, PrimaryChartCanvas))
            {
                return ViewModel.PrimaryChart;
            }

            if (ReferenceEquals(element, SecondaryChartCanvas))
            {
                return ViewModel.SecondaryChart;
            }

            if (ReferenceEquals(element, CompactTertiaryChartCanvas) || ReferenceEquals(element, StandaloneTertiaryChartCanvas))
            {
                return ViewModel.TertiaryChart;
            }

            return null;
        }

        private SKImageInfo ResolveChartInfo(SKElement element)
        {
            if (ReferenceEquals(element, PrimaryChartCanvas))
            {
                return _primaryChartInfo;
            }

            if (ReferenceEquals(element, SecondaryChartCanvas))
            {
                return _secondaryChartInfo;
            }

            if (ReferenceEquals(element, CompactTertiaryChartCanvas))
            {
                return _compactTertiaryChartInfo;
            }

            if (ReferenceEquals(element, StandaloneTertiaryChartCanvas))
            {
                return _standaloneTertiaryChartInfo;
            }

            return new SKImageInfo();
        }

        private void ShowChartTooltip(SKElement element, Point point, string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                HideChartTooltip();
                return;
            }

            ChartTooltipTextBlock.Text = text;
            ChartTooltipPopup.PlacementTarget = element;
            ChartTooltipPopup.HorizontalOffset = point.X + 16;
            ChartTooltipPopup.VerticalOffset = point.Y + 12;
            ChartTooltipPopup.IsOpen = true;
        }

        private void HideChartTooltip()
        {
            ChartTooltipPopup.IsOpen = false;
        }

        private void SyncDetailTableSelection(ReportChartDataPoint dataPoint)
        {
            if (dataPoint?.DetailTable == null || dataPoint.DetailTable.Rows.Count == 0 || DetailTableGrid == null || ViewModel.DetailTableView == null || ViewModel.DetailTableView.Count == 0)
            {
                return;
            }

            var lookupValue = Convert.ToString(dataPoint.DetailTable.Rows[0][0]) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(lookupValue))
            {
                return;
            }

            foreach (var item in ViewModel.DetailTableView.Cast<DataRowView>())
            {
                var rowValue = item.Row.ItemArray.Length > 0 ? Convert.ToString(item.Row[0]) : string.Empty;
                if (!string.Equals(rowValue, lookupValue, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var outerOffset = ContentScrollViewer != null ? ContentScrollViewer.VerticalOffset : 0d;
                var gridVisible = IsElementVisibleWithinScrollViewer(DetailTableGrid, ContentScrollViewer);
                DetailTableGrid.SelectedItem = item;
                RestoreOuterScrollOffset(outerOffset);
                if (gridVisible)
                {
                    DetailTableGrid.ScrollIntoView(item);
                    RestoreOuterScrollOffset(outerOffset);
                }
                break;
            }
        }

        private bool IsElementVisibleWithinScrollViewer(FrameworkElement element, ScrollViewer scrollViewer)
        {
            if (element == null || scrollViewer == null || !element.IsLoaded || element.ActualHeight <= 0)
            {
                return false;
            }

            try
            {
                var transform = element.TransformToAncestor(scrollViewer);
                var top = transform.Transform(new Point(0, 0)).Y;
                var bottom = top + element.ActualHeight;
                return bottom >= 0 && top <= scrollViewer.ViewportHeight;
            }
            catch
            {
                return false;
            }
        }

        private static bool SameHit(ReportChartHitInfo left, ReportChartHitInfo right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left == null || right == null)
            {
                return false;
            }

            return left.PointIndex == right.PointIndex && left.SeriesIndex == right.SeriesIndex;
        }

        private void RestoreOuterScrollOffset(double offset)
        {
            if (ContentScrollViewer == null)
            {
                return;
            }

            void Restore()
            {
                try
                {
                    ContentScrollViewer.ScrollToVerticalOffset(offset);
                }
                catch
                {
                    // 忽略滚动复位异常
                }
            }

            Dispatcher.BeginInvoke((Action)Restore, DispatcherPriority.Background);
            Dispatcher.BeginInvoke((Action)Restore, DispatcherPriority.ContextIdle);
        }

        private static string BuildTooltipText(ReportChartDataPoint point)
        {
            if (point == null)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(point.Label))
            {
                sb.Append(point.Label);
            }

            if (point.Metrics != null && point.Metrics.Count > 0)
            {
                foreach (var metric in point.Metrics.Where(x => x != null && !string.IsNullOrWhiteSpace(x.Name)))
                {
                    if (sb.Length > 0) sb.AppendLine();
                    sb.Append(metric.Name + "：");
                    sb.Append(metric.Value ?? string.Empty);
                }
            }
            else if (!string.IsNullOrWhiteSpace(point.Summary))
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.Append(point.Summary);
            }

            if (sb.Length > 0) sb.AppendLine();
            sb.Append(L("单击查看详情"));
            return sb.ToString();
        }

        public sealed class ReportDetailWindowViewModel
        {
            public ReportDetailWindowViewModel(ReportInfo info, StructuredReportPreview preview)
            {
                var safePreview = preview ?? new StructuredReportPreview();

                ReportTitle = string.IsNullOrWhiteSpace(info?.Title) ? L("报表详情") : info.Title;
                ReportMetaText = BuildMetaText(info);
                FilePath = info?.FilePath ?? string.Empty;

                PreviewKpis = (safePreview.Kpis ?? new List<ReportPreviewKpi>()).ToList();
                PrimaryChart = safePreview.PrimaryChart;
                SecondaryChart = safePreview.SecondaryChart;
                TertiaryChart = safePreview.TertiaryChart;
                PreviewMarkdown = !string.IsNullOrWhiteSpace(safePreview.AnalysisMarkdown)
                    ? safePreview.AnalysisMarkdown
                    : "> " + L("当前报表暂无 AI 分析正文。");
                DetailTitle = !string.IsNullOrWhiteSpace(safePreview.DetailTitle) ? safePreview.DetailTitle : L("明细数据");
                DetailTableView = safePreview.DetailTable != null ? safePreview.DetailTable.DefaultView : null;
                PreviewNotes = (safePreview.Notes ?? new List<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToList();
            }

            public string ReportTitle { get; }
            public string ReportMetaText { get; }
            public string FilePath { get; }
            public IList<ReportPreviewKpi> PreviewKpis { get; }
            public ReportChartDefinition PrimaryChart { get; }
            public ReportChartDefinition SecondaryChart { get; }
            public ReportChartDefinition TertiaryChart { get; }
            public string PreviewMarkdown { get; }
            public string DetailTitle { get; }
            public DataView DetailTableView { get; }
            public IList<string> PreviewNotes { get; }

            public bool HasPrimaryChart => PrimaryChart != null;
            public bool HasSecondaryChart => SecondaryChart != null;
            public bool HasTertiaryChart => TertiaryChart != null;
            public bool HasCompactTertiaryChart => TertiaryChart != null && TertiaryChart.Kind == ReportChartKind.Donut;
            public bool HasStandaloneTertiaryChart => TertiaryChart != null && TertiaryChart.Kind != ReportChartKind.Donut;
            public bool HasDetailTable => DetailTableView != null && DetailTableView.Count > 0;
            public bool HasPreviewNotes => PreviewNotes != null && PreviewNotes.Count > 0;

            private static string BuildMetaText(ReportInfo info)
            {
                if (info == null)
                {
                    return L("未找到报表元数据");
                }

                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(info.DateLabel))
                {
                    parts.Add(info.DateLabel);
                }

                if (info.GeneratedAt != default)
                {
                    parts.Add(L("更新：") + info.GeneratedAt.ToString("yyyy-MM-dd HH:mm"));
                }

                if (!string.IsNullOrWhiteSpace(info.FileName))
                {
                    parts.Add(L("文件：") + info.FileName);
                }

                return parts.Count > 0 ? string.Join("  ·  ", parts) : L("未解析日期");
            }
        }
    }
}

using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EW_Assistant.Services.Reports;
using EW_Assistant.Services;
using EW_Assistant.Domain.Reports;
using EW_Assistant.ViewModels;
using SkiaSharp.Views.Desktop;

namespace EW_Assistant.Views.Reports
{
    /// <summary>
    /// 报表中心视图，支持本地报表生成、预览与导出。
    /// </summary>
    public partial class ReportsCenterView : UserControl
    {
        private readonly ReportsCenterViewModel _viewModel;

        public ReportsCenterView()
        {
            InitializeComponent();
            _viewModel = new ReportsCenterViewModel();
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            DataContext = _viewModel;
        }

        private static string L(string chineseText)
        {
            return UiLanguageService.CurrentText(chineseText);
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            if (e.PropertyName == nameof(ReportsCenterViewModel.PrimaryChart) ||
                e.PropertyName == nameof(ReportsCenterViewModel.SecondaryChart) ||
                e.PropertyName == nameof(ReportsCenterViewModel.TertiaryChart) ||
                e.PropertyName == nameof(ReportsCenterViewModel.SelectedReport))
            {
                InvalidateCharts();
            }
        }

        private async void ReportTypeButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is ReportType reportType)
            {
                await _viewModel.SwitchReportTypeAsync(reportType);
            }
        }

        private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var info = _viewModel.SelectedReport;
            string errorMessage;
            if (!ReportWindowHelper.TryRevealInExplorer(info?.FilePath, out errorMessage))
            {
                MessageBox.Show(errorMessage, L("提示"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            var info = _viewModel.SelectedReport;
            if (info == null)
            {
                MessageBox.Show(L("请选择要导出的报表。"), L("提示"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = ReportWindowHelper.CreateExportDialog(info, L("导出报表"));

            var result = dialog.ShowDialog();
            if (result != true || string.IsNullOrWhiteSpace(dialog.FileName))
            {
                return;
            }

            var format = ReportWindowHelper.ResolveExportFormat(dialog.FilterIndex, dialog.FileName);
            var targetPath = ReportWindowHelper.NormalizeExportFilePath(format, dialog.FileName);
            string error;
            if (_viewModel.TryExportReport(info, format, targetPath, out error))
            {
                MainWindow.PostProgramInfo(L("报表已导出：") + targetPath, "ok");
            }
            else
            {
                MainWindow.PostProgramInfo(L("导出失败：") + error, "error");
                MessageBox.Show(L("导出失败：") + error, L("提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void RegenerateButton_Click(object sender, RoutedEventArgs e)
        {
            var info = _viewModel.SelectedReport;
            if (info == null)
            {
                return;
            }

            try
            {
                MainWindow.PostProgramInfo(L("开始重新生成报表：") + info.Title, "info");
                var outcome = await _viewModel.RegenerateAsync(info);
                if (outcome == ReportRegenerateOutcome.Generated)
                {
                    MainWindow.PostProgramInfo(L("报表重新生成完成：") + info.Title, "ok");
                }
            }
            catch (Exception ex)
            {
                MainWindow.PostProgramInfo(L("重新生成失败：") + ex.Message, "error");
            }
        }

        private void ReportList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var sv = FindScrollViewer(sender as DependencyObject);
            if (sv == null) return;

            double factor = 1.5;
            double delta = -e.Delta * factor;
            sv.ScrollToVerticalOffset(sv.VerticalOffset + delta);
            e.Handled = true;
        }

        private async void ReportList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var container = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
            if (container == null || _viewModel.SelectedReport == null)
            {
                return;
            }

            await OpenDetailWindowAsync(_viewModel.SelectedReport);
            e.Handled = true;
        }

        private async void SummaryPreviewCard_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount < 2 || _viewModel.SelectedReport == null)
            {
                return;
            }

            await OpenDetailWindowAsync(_viewModel.SelectedReport);
            e.Handled = true;
        }

        private void PrimaryChart_PaintSurface(object sender, SKPaintSurfaceEventArgs e)
        {
            ReportChartDrawingHelper.DrawChart(_viewModel.PrimaryChart, e.Surface.Canvas, e.Info);
        }

        private void InvalidateCharts()
        {
            try
            {
                PrimaryChartCanvas?.InvalidateVisual();
            }
            catch
            {
                // 忽略 UI 刷新异常
            }
        }

        private async System.Threading.Tasks.Task OpenDetailWindowAsync(ReportInfo info)
        {
            if (info == null)
            {
                return;
            }

            try
            {
                var preview = await _viewModel.BuildPreviewSnapshotAsync(info);
                var window = new ReportDetailWindow(info, preview)
                {
                    Owner = Window.GetWindow(this)
                };
                window.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show(L("打开报表详情失败：") + ex.Message, L("提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static T FindAncestor<T>(DependencyObject source) where T : DependencyObject
        {
            while (source != null)
            {
                if (source is T typed)
                {
                    return typed;
                }

                source = VisualTreeHelper.GetParent(source);
            }

            return null;
        }

        private static ScrollViewer FindScrollViewer(DependencyObject source)
        {
            if (source == null) return null;
            if (source is ScrollViewer sv) return sv;
            int count = VisualTreeHelper.GetChildrenCount(source);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(source, i);
                var result = FindScrollViewer(child);
                if (result != null) return result;
            }
            return null;
        }

    }
}

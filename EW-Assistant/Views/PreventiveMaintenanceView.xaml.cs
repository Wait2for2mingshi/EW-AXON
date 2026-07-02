using EW_Assistant.Services;
using EW_Assistant.Services.PreventiveMaintenance;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace EW_Assistant.Views
{
    public partial class PreventiveMaintenanceView : UserControl, INotifyPropertyChanged
    {
        private readonly PartMaintenanceAnalyzer _analyzer = new PartMaintenanceAnalyzer();
        private PartMaintenanceReport _report;
        private string _statusText = "等待分析";
        private string _rootPathText = string.Empty;
        private string _fileCountText = "-";
        private string _latestDateText = "-";
        private string _cylinderSummary = "暂无气缸数据。";
        private string _vacuumSummary = "暂无真空吸数据。";

        public PreventiveMaintenanceView()
        {
            InitializeComponent();
            DataContext = this;
            Loaded += PreventiveMaintenanceView_Loaded;
        }

        public ObservableCollection<PartMaintenanceRisk> Risks { get; } = new ObservableCollection<PartMaintenanceRisk>();
        public ObservableCollection<PartMaintenanceComponentStatus> CylinderStatuses { get; } = new ObservableCollection<PartMaintenanceComponentStatus>();
        public ObservableCollection<PartMaintenanceComponentStatus> VacuumStatuses { get; } = new ObservableCollection<PartMaintenanceComponentStatus>();

        public string StatusText
        {
            get => _statusText;
            private set { if (_statusText != value) { _statusText = value; OnPropertyChanged(); } }
        }

        public string RootPathText
        {
            get => _rootPathText;
            private set { if (_rootPathText != value) { _rootPathText = value; OnPropertyChanged(); } }
        }

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

        public event PropertyChangedEventHandler PropertyChanged;

        private void PreventiveMaintenanceView_Loaded(object sender, RoutedEventArgs e)
        {
            RefreshAnalysis();
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            RefreshAnalysis();
        }

        private void RefreshAnalysis()
        {
            try
            {
                var path = ConfigService.Current?.PartCsvPath ?? string.Empty;
                _report = _analyzer.Analyze(path);
                RootPathText = "零件 CSV 地址：" + (_report.RootPath ?? string.Empty);
                StatusText = _report.StatusMessage;
                FileCountText = _report.FileCount.ToString();
                LatestDateText = _report.LatestDate.HasValue ? _report.LatestDate.Value.ToString("MM-dd") : "-";
                CylinderSummary = BuildTrendSummary("气缸", _report.CylinderTrend);
                VacuumSummary = BuildTrendSummary("真空吸", _report.VacuumTrend);

                Risks.Clear();
                foreach (var risk in _report.Risks)
                {
                    Risks.Add(risk);
                }

                CylinderStatuses.Clear();
                foreach (var status in _report.CylinderStatuses)
                {
                    CylinderStatuses.Add(status);
                }

                VacuumStatuses.Clear();
                foreach (var status in _report.VacuumStatuses)
                {
                    VacuumStatuses.Add(status);
                }

                CylinderChart?.InvalidateVisual();
                VacuumChart?.InvalidateVisual();
            }
            catch (Exception ex)
            {
                StatusText = "预防维护分析失败：" + ex.Message;
                MainWindow.PostProgramInfo(StatusText, "error");
            }
        }

        private static string BuildTrendSummary(string name, System.Collections.Generic.IList<PartMaintenanceTrendPoint> points)
        {
            if (points == null || points.Count == 0)
                return "未读取到" + name + "趋势数据。";

            var latest = points[points.Count - 1];
            var abnormalDays = points.Count(x => x.HasAbnormal);
            var valueLabel = latest.HasNumericValue ? "趋势值" : "异常率";
            return "最近日期 " + latest.Date.ToString("yyyy-MM-dd")
                   + "，" + valueLabel + " " + latest.Value.ToString("0.###")
                   + "，异常天数 " + abnormalDays + " 天，异常记录 " + points.Sum(x => x.AbnormalCount) + " 条。";
        }

        private void CylinderChart_PaintSurface(object sender, SKPaintSurfaceEventArgs e)
        {
            DrawTrendChart(e.Surface.Canvas, e.Info, _report?.CylinderTrend, "气缸");
        }

        private void VacuumChart_PaintSurface(object sender, SKPaintSurfaceEventArgs e)
        {
            DrawTrendChart(e.Surface.Canvas, e.Info, _report?.VacuumTrend, "真空吸");
        }

        private static void DrawTrendChart(SKCanvas canvas, SKImageInfo info, System.Collections.Generic.IList<PartMaintenanceTrendPoint> points, string title)
        {
            canvas.Clear(SKColors.White);
            var rect = new SKRect(58, 42, Math.Max(80, info.Width - 20), Math.Max(80, info.Height - 44));
            var typeface = ResolveChartTypeface();

            using (var axisPaint = new SKPaint { Color = new SKColor(148, 163, 184), StrokeWidth = 1, IsAntialias = true })
            using (var gridPaint = new SKPaint { Color = new SKColor(226, 232, 240), StrokeWidth = 1, IsAntialias = true })
            using (var textPaint = new SKPaint { Color = new SKColor(71, 85, 105), TextSize = 12, IsAntialias = true, Typeface = typeface })
            using (var legendPaint = new SKPaint { Color = new SKColor(51, 65, 85), TextSize = 12, IsAntialias = true, Typeface = typeface })
            using (var linePaint = new SKPaint { Color = new SKColor(37, 99, 235), StrokeWidth = 3, IsAntialias = true, Style = SKPaintStyle.Stroke })
            using (var fillPaint = new SKPaint { Color = new SKColor(37, 99, 235), IsAntialias = true })
            using (var abnormalPaint = new SKPaint { Color = new SKColor(220, 38, 38), IsAntialias = true })
            {
                DrawLegend(canvas, rect, fillPaint, abnormalPaint, legendPaint);
                canvas.DrawLine(rect.Left, rect.Top, rect.Left, rect.Bottom, axisPaint);
                canvas.DrawLine(rect.Left, rect.Bottom, rect.Right, rect.Bottom, axisPaint);

                if (points == null || points.Count == 0)
                {
                    DrawCenteredText(canvas, title + "暂无数据", info, textPaint);
                    return;
                }

                var max = Math.Max(1d, points.Max(x => x.Value));
                var min = Math.Min(0d, points.Min(x => x.Value));
                if (Math.Abs(max - min) < 0.0001d)
                {
                    max += 1d;
                    min = 0d;
                }

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
                    var x = points.Count == 1
                        ? rect.Left + rect.Width / 2f
                        : rect.Left + rect.Width * i / (points.Count - 1);
                    var y = rect.Bottom - (float)((point.Value - min) / (max - min) * rect.Height);

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

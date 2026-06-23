using EW_Assistant.Services;
using EW_Assistant.Warnings;
using SkiaSharp;
using SkiaSharp.Views.WPF;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace EW_Assistant
{
    public partial class DashboardView : UserControl
    {
        // ===== 依赖属性：CSV 根目录 & 文件前缀 =====
        public static readonly DependencyProperty FilePrefixProperty =
            DependencyProperty.Register(nameof(FilePrefix), typeof(string), typeof(DashboardView),
                new PropertyMetadata("小时产量", OnProductionPathChanged));

        // ===== 图表尺寸（外部可绑） =====
        public static readonly DependencyProperty WeekChartWidthProperty =
            DependencyProperty.Register(nameof(WeekChartWidth), typeof(double), typeof(DashboardView),
                new PropertyMetadata(590d));
        public static readonly DependencyProperty WeekChartHeightProperty =
            DependencyProperty.Register(nameof(WeekChartHeight), typeof(double), typeof(DashboardView),
                new PropertyMetadata(220d));
        public static readonly DependencyProperty HourChartWidthProperty =
            DependencyProperty.Register(nameof(HourChartWidth), typeof(double), typeof(DashboardView),
                new PropertyMetadata(590d));
        public static readonly DependencyProperty HourChartHeightProperty =
            DependencyProperty.Register(nameof(HourChartHeight), typeof(double), typeof(DashboardView),
                new PropertyMetadata(220d));

        // ===== 良率卡片尺寸 =====
        public static readonly DependencyProperty YieldCardWidthProperty =
            DependencyProperty.Register(nameof(YieldCardWidth), typeof(double), typeof(DashboardView),
                new PropertyMetadata(220d));
        public static readonly DependencyProperty YieldCardHeightProperty =
            DependencyProperty.Register(nameof(YieldCardHeight), typeof(double), typeof(DashboardView),
                new PropertyMetadata(220d));

        // 良率圆环的相对尺寸（相对于卡片边长的比例，0~1），默认 0.72 = 72%
        // 把它调小（如 0.62）即可让圆环“小一点”
        public static readonly DependencyProperty YieldRingScaleProperty =
            DependencyProperty.Register(nameof(YieldRingScale), typeof(double), typeof(DashboardView),
                new PropertyMetadata(0.92d, OnYieldVisualPropertyChanged));

        // 良率圆环的相对粗细（相对于圆环外径，0~1），默认 0.10 = 10%
        public static readonly DependencyProperty YieldRingThicknessProperty =
            DependencyProperty.Register(nameof(YieldRingThickness), typeof(double), typeof(DashboardView),
                new PropertyMetadata(0.10d, OnYieldVisualPropertyChanged));

        public double YieldRingScale
        {
            get => (double)GetValue(YieldRingScaleProperty);
            set => SetValue(YieldRingScaleProperty, value);
        }
        public double YieldRingThickness
        {
            get => (double)GetValue(YieldRingThicknessProperty);
            set => SetValue(YieldRingThicknessProperty, value);
        }

        private static void OnYieldVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is DashboardView v) v.SkYield?.InvalidateVisual();
        }


        public double WeekChartWidth { get => (double)GetValue(WeekChartWidthProperty); set => SetValue(WeekChartWidthProperty, value); }
        public double WeekChartHeight { get => (double)GetValue(WeekChartHeightProperty); set => SetValue(WeekChartHeightProperty, value); }
        public double HourChartWidth { get => (double)GetValue(HourChartWidthProperty); set => SetValue(HourChartWidthProperty, value); }
        public double HourChartHeight { get => (double)GetValue(HourChartHeightProperty); set => SetValue(HourChartHeightProperty, value); }

        private static string L(string chineseText)
        {
            return UiLanguageService.CurrentText(chineseText);
        }
        public double YieldCardWidth { get => (double)GetValue(YieldCardWidthProperty); set => SetValue(YieldCardWidthProperty, value); }
        public double YieldCardHeight { get => (double)GetValue(YieldCardHeightProperty); set => SetValue(YieldCardHeightProperty, value); }
        public string FilePrefix { get => (string)GetValue(FilePrefixProperty); set => SetValue(FilePrefixProperty, value); }

        // ===== 周数据 =====
        private class DayData
        {
            public DateTime Date;
            public int Pass;
            public int Fail;
            public bool Missing;
            public int Total => Math.Max(0, Pass) + Math.Max(0, Fail);
        }
        private readonly List<DayData> _week = new();

        // ===== 日产能（24小时，堆叠） =====
        private readonly int[] _hourPass = new int[24];
        private readonly int[] _hourFail = new int[24];
        private bool _todayMissing = false;

        // 今日良率
        private int _todayPassSum = 0;
        private int _todayFailSum = 0;
        private double _todayYield = 0.0; // 0..1

        // ===== 动画 =====
        private double _anim = 1.0;
        private readonly Stopwatch _sw = new Stopwatch();
        private bool _isAnimating = false;
        private TimeSpan _duration = TimeSpan.FromMilliseconds(550);
        private const double Stagger = 0.08;
        private static double EaseOutCubic(double x) => 1 - Math.Pow(1 - x, 3);
        private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);

        public DashboardView()
        {
            InitializeComponent();

            Loaded += (_, __) =>
            {
                ReloadWeek(forceCacheReload: true);
                ReloadTodayHours();
                AlarmLogCache.LoadRecent(ConfigService.Current.AlarmLogPath, days: 7);
                RefreshAlarmFromCache();
                LoadAlarmHourlyFromCacheOrFile();
                LoadAlarmDurationShareFromCacheOrFile();
                RefreshTrackedWriteTimes();
                StartAnimation();

                if (AutoRefreshEnabled) StartAutoRefresh();
            };

            Unloaded += (_, __) =>
            {
                StopAutoRefresh();
                CompositionTarget.Rendering -= OnRendering;
                _isAnimating = false;
            };
        }

        //自动刷新
        // === 新增：自动刷新设置 ===
        public static readonly DependencyProperty RefreshIntervalProperty =
            DependencyProperty.Register(nameof(RefreshInterval), typeof(TimeSpan), typeof(DashboardView),
                new PropertyMetadata(TimeSpan.FromSeconds(10), OnRefreshIntervalChanged)); // 默认 10s

        public TimeSpan RefreshInterval
        {
            get => (TimeSpan)GetValue(RefreshIntervalProperty);
            set => SetValue(RefreshIntervalProperty, value);
        }

        public static readonly DependencyProperty AutoRefreshEnabledProperty =
            DependencyProperty.Register(nameof(AutoRefreshEnabled), typeof(bool), typeof(DashboardView),
                new PropertyMetadata(true, OnAutoRefreshEnabledChanged));

        public bool AutoRefreshEnabled
        {
            get => (bool)GetValue(AutoRefreshEnabledProperty);
            set => SetValue(AutoRefreshEnabledProperty, value);
        }

        private static void OnRefreshIntervalChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is DashboardView v)
            {
                v._refreshTimer.Interval = (TimeSpan)e.NewValue;
                // 如果正在跑，立刻生效
                if (v._refreshTimer.IsEnabled)
                {
                    v._refreshTimer.Stop();
                    v._refreshTimer.Start();
                }
            }
        }

        private static void OnAutoRefreshEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is DashboardView v)
            {
                if ((bool)e.NewValue) v.StartAutoRefresh();
                else v.StopAutoRefresh();
            }
        }

        private readonly DispatcherTimer _refreshTimer = new DispatcherTimer();
        private bool _isRefreshing = false; // 防抖
        private DateTime _lastAlarmWriteTime = DateTime.MinValue;
        private DateTime _lastProductionWriteTime = DateTime.MinValue;


        private void StartAutoRefresh()
        {
            _refreshTimer.Interval = RefreshInterval;
            if (!_refreshTimer.IsEnabled)
            {
                _refreshTimer.Tick -= RefreshTimer_Tick;
                _refreshTimer.Tick += RefreshTimer_Tick;
                _refreshTimer.Start();
            }
        }

        private void StopAutoRefresh()
        {
            _refreshTimer.Tick -= RefreshTimer_Tick;
            _refreshTimer.Stop();
        }
        private async void RefreshTimer_Tick(object? sender, EventArgs e)
        {
            await RefreshDashboardAsync(forceRefresh: false);
        }

        // 可选：手动暴露一个强制刷新方法给外部调用
        public void ForceRefresh()
        {
            _ = RefreshDashboardAsync(forceRefresh: true);
        }

        private static void OnProductionPathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is DashboardView v)
            {
                v.ReloadWeek(forceCacheReload: true);
                v.ReloadTodayHours();
                v.RefreshAlarmFromCache();
                v.LoadAlarmHourlyFromCacheOrFile();
                v.LoadAlarmDurationShareFromCacheOrFile();
                v.RefreshTrackedWriteTimes();
                v.StartAnimation();

            }
        }

        private async Task RefreshDashboardAsync(bool forceRefresh)
        {
            if (_isRefreshing) return;
            _isRefreshing = true;
            try
            {
                var latestAlarm = GetLatestAlarmWriteTime();
                var latestProduction = GetLatestProductionWriteTime();

                if (!forceRefresh
                    && latestAlarm <= _lastAlarmWriteTime
                    && latestProduction <= _lastProductionWriteTime)
                {
                    return;
                }

                await Task.Run(() =>
                {
                    AlarmLogCache.LoadRecent(ConfigService.Current.AlarmLogPath, days: 7);
                    return true;
                });

                ReloadWeek(forceCacheReload: true);
                ReloadTodayHours();
                RefreshAlarmFromCache();
                LoadAlarmHourlyFromCacheOrFile();
                LoadAlarmDurationShareFromCacheOrFile();
                SkChart?.InvalidateVisual();
                SkHour?.InvalidateVisual();
                SkYield?.InvalidateVisual();
                SkAlarm?.InvalidateVisual();
                SkAlarmHour?.InvalidateVisual();
                SkAlarmPie?.InvalidateVisual();
                _lastAlarmWriteTime = latestAlarm;
                _lastProductionWriteTime = latestProduction;
            }
            catch (Exception ex)
            {
                MainWindow.PostProgramInfo(L("自动刷新失败：") + ex.Message, "warn");
            }
            finally
            {
                _isRefreshing = false;
            }
        }

        private void RefreshTrackedWriteTimes()
        {
            _lastAlarmWriteTime = GetLatestAlarmWriteTime();
            _lastProductionWriteTime = GetLatestProductionWriteTime();
        }

        private DateTime GetLatestAlarmWriteTime()
        {
            try
            {
                var root = LocalDataConfig.AlarmCsvRoot;
                var watchMode = LocalDataConfig.WatchMode;
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                    return _lastAlarmWriteTime;

                return watchMode
                    ? GetLatestWatchModeWriteTime(root)
                    : GetLatestFlatWriteTime(root);
            }
            catch
            {
                return _lastAlarmWriteTime;
            }
        }

        private DateTime GetLatestProductionWriteTime()
        {
            try
            {
                var root = LocalDataConfig.ProductionCsvRoot;
                var watchMode = LocalDataConfig.WatchMode;
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                    return _lastProductionWriteTime;

                return watchMode
                    ? GetLatestWatchModeWriteTime(root)
                    : GetLatestFlatWriteTime(root);
            }
            catch
            {
                return _lastProductionWriteTime;
            }
        }

        private static DateTime GetLatestFlatWriteTime(string root)
        {
            var max = DateTime.MinValue;
            var files = Directory.GetFiles(root, "*.csv", SearchOption.TopDirectoryOnly);
            foreach (var path in files)
            {
                var t = File.GetLastWriteTime(path);
                if (t > max) max = t;
            }

            return max;
        }

        private static DateTime GetLatestWatchModeWriteTime(string root)
        {
            var max = DateTime.MinValue;
            var dirs = Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly);
            foreach (var dir in dirs)
            {
                var name = Path.GetFileName(dir);
                if (!DateTime.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    continue;

                var files = Directory.GetFiles(dir, "*.csv", SearchOption.TopDirectoryOnly);
                foreach (var path in files)
                {
                    var t = File.GetLastWriteTime(path);
                    if (t > max) max = t;
                }
            }

            return max;
        }

        private void StartAnimation()
        {
            _sw.Restart();
            _isAnimating = true;
            _anim = 0.0;
            CompositionTarget.Rendering -= OnRendering;
            CompositionTarget.Rendering += OnRendering;
        }
        private void OnRendering(object sender, EventArgs e)
        {
            if (!_isAnimating) return;

            double t = _sw.Elapsed.TotalMilliseconds / _duration.TotalMilliseconds;
            _anim = Clamp01(t);

            SkChart?.InvalidateVisual();
            SkHour?.InvalidateVisual();
            SkYield?.InvalidateVisual();
            SkAlarm?.InvalidateVisual();
            SkTop5?.InvalidateVisual();
            SkAlarmHour?.InvalidateVisual();
            SkAlarmPie?.InvalidateVisual();

            if (_anim >= 1.0)
            {
                _isAnimating = false;
                _sw.Stop();
                CompositionTarget.Rendering -= OnRendering;
                _anim = 1.0;
            }
        }
        // 共享读取：允许对方正在写入时我们只读打开
        private static List<string> ReadAllLinesShared(string path, Encoding enc)
        {
            var list = new List<string>();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs, enc, detectEncodingFromByteOrderMarks: true);
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    list.Add(line);
            }
            return list;
        }
        // ===== 数据读取 =====
        // ===== 数据读取（周） =====
        private void ReloadWeek(bool forceCacheReload = false)
        {
            ProductionLogCache.LoadRecent(ConfigService.Current.ProductionLogPath, FilePrefix, days: 7, force: forceCacheReload);

            _week.Clear();
            var snapshot = ProductionLogCache.SnapshotRecent(7);
            foreach (var d in snapshot)
            {
                _week.Add(new DayData
                {
                    Date = d.Date,
                    Pass = d.SumPass,
                    Fail = d.SumFail,
                    Missing = d.Missing
                });
            }

            if (Subtitle != null)
                Subtitle.Text = L("统计：PASS(蓝)+FAIL(红)");
            SkChart?.InvalidateVisual();
        }

        // ===== 数据读取（今日24小时 & 良率） =====
        private void ReloadTodayHours(bool forceCacheReload = false)
        {
            ProductionLogCache.LoadRecent(ConfigService.Current.ProductionLogPath, FilePrefix, days: 7, force: forceCacheReload);

            Array.Clear(_hourPass, 0, _hourPass.Length);
            Array.Clear(_hourFail, 0, _hourFail.Length);
            _todayMissing = false;
            _todayPassSum = 0;
            _todayFailSum = 0;
            _todayYield = 0;

            var data = ProductionLogCache.GetDay(DateTime.Today);
            if (data == null || data.Missing)
            {
                _todayMissing = true;
                return;
            }

            for (int h = 0; h < 24; h++)
            {
                _hourPass[h] = data.HourPass[h];
                _hourFail[h] = data.HourFail[h];
            }

            _todayPassSum = data.SumPass;
            _todayFailSum = data.SumFail;
            int total = _todayPassSum + _todayFailSum;
            _todayYield = total > 0 ? (double)_todayPassSum / total : 0.0;
        }

        // ===== 绘图：周图（PASS/FAIL 双柱） =====
        private void SkChart_PaintSurface(object sender, SkiaSharp.Views.Desktop.SKPaintSurfaceEventArgs e)
        {
            var canvas = e.Surface.Canvas;
            var info = e.Info;
            canvas.Clear(SKColors.White);

            float chartPadLeft = 56, chartPadRight = 24, chartPadTop = 8, chartPadBottom = 46;
            var chart = new SKRect(chartPadLeft, chartPadTop, info.Width - chartPadRight, info.Height - chartPadBottom);

            using var gridPaint = new SKPaint { Color = new SKColor(240, 243, 248), StrokeWidth = 1, IsStroke = true };
            using var axisPaint = new SKPaint { Color = new SKColor(228, 232, 240), StrokeWidth = 1, IsStroke = true };
            using var textPaint = new SKPaint { Color = new SKColor(107, 114, 128), TextSize = 12, IsAntialias = true };
            using var valuePaint = new SKPaint { IsAntialias = true, Color = new SKColor(55, 65, 81), TextSize = 12, FakeBoldText = true };
            using var dayPaint = new SKPaint { IsAntialias = true, Color = new SKColor(107, 114, 128), TextSize = 12 };
            using var barPaint = new SKPaint { IsAntialias = true };

            int max = Math.Max(1, _week.Any() ? _week.Max(d => Math.Max(d.Pass, d.Fail)) : 1);
            int gridCount = 4;
            // 上限 = 最高值 * 1.1（向上取整）
            int yMax = (int)Math.Ceiling(max * 1.10);


            for (int i = 0; i <= gridCount; i++)
            {
                float y = chart.Top + (chart.Height * i / gridCount);
                canvas.DrawLine(chart.Left, y, chart.Right, y, gridPaint);
            }
            canvas.DrawLine(chart.Left, chart.Bottom, chart.Right, chart.Bottom, axisPaint);
            canvas.DrawLine(chart.Left, chart.Top, chart.Left, chart.Bottom, axisPaint);

            for (int i = 0; i <= gridCount; i++)
            {
                int val = yMax * (gridCount - i) / gridCount;
                var y = chart.Top + (chart.Height * i / gridCount);
                var label = val.ToString(CultureInfo.InvariantCulture);
                var bounds = new SKRect();
                textPaint.MeasureText(label, ref bounds);
                canvas.DrawText(label, chart.Left - 8 - bounds.Width, y + bounds.Height / 2, textPaint);
            }

            if (_week.Count == 0) return;

            float slot = chart.Width / _week.Count;
            float barWidth = slot * 0.28f;
            float gap = 0f;
            float radius = 6f;

            var passDark = new SKColor(59, 130, 246);
            var passLight = new SKColor(147, 197, 253);
            var failDark = new SKColor(249, 115, 22);
            var failLight = new SKColor(253, 186, 116);
            using var failValuePaint = new SKPaint { IsAntialias = true, Color = failDark, TextSize = 10, FakeBoldText = true };

            for (int i = 0; i < _week.Count; i++)
            {
                var d = _week[i];
                float cx = chart.Left + slot * i + slot / 2;

                // X 轴日期
                string dayLabel = d.Date.ToString("MM-dd");
                var db = new SKRect(); dayPaint.MeasureText(dayLabel, ref db);
                canvas.DrawText(dayLabel, cx - db.MidX, chart.Bottom + 18, dayPaint);

                // 动画
                double local = Clamp01((_anim - i * Stagger) / (1.0 - Math.Min(1.0, i * Stagger)));
                double eased = EaseOutCubic(local);

                if (d.Missing)
                {
                    var tag = "0";
                    var tb = new SKRect(); valuePaint.MeasureText(tag, ref tb);
                    canvas.DrawText(tag, cx - tb.MidX, chart.Bottom - 6, valuePaint);
                    continue;
                }

                float pH = chart.Height * (float)(d.Pass / Math.Max(1.0, (double)yMax)) * (float)eased;
                float fH = chart.Height * (float)(d.Fail / Math.Max(1.0, (double)yMax)) * (float)eased;

                float passLeft = cx - gap - barWidth;
                float failLeft = cx + gap;
                float bottom = chart.Bottom;

                // PASS
                if (pH > 0.1f)
                {
                    var r = new SKRect(passLeft, bottom - pH, passLeft + barWidth, bottom);
                    using var rr = MakeRRect(r, radius, radius, radius, radius);
                    using var shader = SKShader.CreateLinearGradient(new SKPoint(0, r.Top), new SKPoint(0, r.Bottom),
                        new[] { passLight, passDark }, null, SKShaderTileMode.Clamp);
                    barPaint.Shader = shader; canvas.DrawRoundRect(rr, barPaint); barPaint.Shader = null;
                }

                // FAIL
                if (fH > 0.1f)
                {
                    var r = new SKRect(failLeft, bottom - fH, failLeft + barWidth, bottom);
                    using var rr = MakeRRect(r, radius, radius, radius, radius);
                    using var shader = SKShader.CreateLinearGradient(new SKPoint(0, r.Top), new SKPoint(0, r.Bottom),
                        new[] { failLight, failDark }, null, SKShaderTileMode.Clamp);
                    barPaint.Shader = shader; canvas.DrawRoundRect(rr, barPaint); barPaint.Shader = null;

                    var failTag = d.Fail.ToString();
                    var fb = new SKRect(); failValuePaint.MeasureText(failTag, ref fb);
                    float fx = r.MidX - fb.MidX;
                    float fy = r.Top - 6 - fb.MidY;
                    if (fy < chart.Top + 4 - fb.Top) fy = chart.Top + 4 - fb.Top;
                    canvas.DrawText(failTag, fx, fy, failValuePaint);
                }

                // 顶部总数
                var tagTotal = (d.Pass + d.Fail).ToString();
                var tb2 = new SKRect(); valuePaint.MeasureText(tagTotal, ref tb2);
                float desired = chart.Bottom - Math.Max(pH, fH) - 6;
                float baseline = SafeBaseline(valuePaint, tagTotal, desired, chart.Top, chart.Bottom);
                canvas.DrawText(tagTotal, cx - tb2.MidX, baseline, valuePaint);
            }
        }

        // ===== 绘图：日图（24 小时堆叠） =====
        private void SkHour_PaintSurface(object sender, SkiaSharp.Views.Desktop.SKPaintSurfaceEventArgs e)
        {
            var canvas = e.Surface.Canvas;
            var info = e.Info;
            canvas.Clear(SKColors.White);

            float chartPadLeft = 56, chartPadRight = 24, chartPadTop = 8, chartPadBottom = 46;
            var chart = new SKRect(chartPadLeft, chartPadTop, info.Width - chartPadRight, info.Height - chartPadBottom);

            using var gridPaint = new SKPaint { Color = new SKColor(240, 243, 248), StrokeWidth = 1, IsStroke = true };
            using var axisPaint = new SKPaint { Color = new SKColor(228, 232, 240), StrokeWidth = 1, IsStroke = true };
            using var textPaint = new SKPaint { Color = new SKColor(107, 114, 128), TextSize = 12, IsAntialias = true };
            using var valuePaint = new SKPaint { IsAntialias = true, Color = new SKColor(55, 65, 81), TextSize = 11, FakeBoldText = true };
            using var barPaint = new SKPaint { IsAntialias = true };

            if (_todayMissing)
            {
                canvas.DrawLine(chart.Left, chart.Bottom, chart.Right, chart.Bottom, axisPaint);
                canvas.DrawLine(chart.Left, chart.Top, chart.Left, chart.Bottom, axisPaint);
                for (int i = 0; i <= 4; i++)
                {
                    float y = chart.Top + chart.Height * i / 4f;
                    canvas.DrawLine(chart.Left, y, chart.Right, y, gridPaint);
                }
                var msg = "0";
                var b = new SKRect(); textPaint.MeasureText(msg, ref b);
                canvas.DrawText(msg, chart.MidX - b.MidX, chart.MidY - b.MidY, textPaint);
                return;
            }

            int maxTotal = 1;
            for (int i = 0; i < 24; i++) maxTotal = Math.Max(maxTotal, _hourPass[i] + _hourFail[i]);
            int yMax = (int)Math.Ceiling(maxTotal * 1.10);

            for (int i = 0; i <= 4; i++)
            {
                float y = chart.Top + (chart.Height * i / 4f);
                canvas.DrawLine(chart.Left, y, chart.Right, y, gridPaint);
            }
            canvas.DrawLine(chart.Left, chart.Bottom, chart.Right, chart.Bottom, axisPaint);
            canvas.DrawLine(chart.Left, chart.Top, chart.Left, chart.Bottom, axisPaint);

            for (int i = 0; i <= 4; i++)
            {
                int val = yMax * (4 - i) / 4;
                var y = chart.Top + (chart.Height * i / 4f);
                var label = val.ToString(CultureInfo.InvariantCulture);
                var bounds = new SKRect(); textPaint.MeasureText(label, ref bounds);
                canvas.DrawText(label, chart.Left - 8 - bounds.Width, y + bounds.Height / 2, textPaint);
            }

            float slot = chart.Width / 24f;
            float barWidth = slot * 0.62f;
            float radius = 5f;

            var passDark = new SKColor(59, 130, 246);
            var passLight = new SKColor(147, 197, 253);
            var failDark = new SKColor(239, 68, 68);
            var failLight = new SKColor(254, 202, 202);

            for (int i = 0; i < 24; i++)
            {
                int p = _hourPass[i];
                int f = _hourFail[i];
                int t = p + f;

                double local = Clamp01((_anim - i * 0.02) / (1.0 - Math.Min(1.0, i * 0.02)));
                double eased = EaseOutCubic(local);

                float pH = chart.Height * (float)(p / Math.Max(1.0, (double)yMax)) * (float)eased;
                float fH = chart.Height * (float)(f / Math.Max(1.0, (double)yMax)) * (float)eased;
                float totalH = pH + fH;

                float cx = chart.Left + slot * i + slot / 2f;
                float left = cx - barWidth / 2f, right = cx + barWidth / 2f;
                float bottom = chart.Bottom;

                if (pH > 0.1f)
                {
                    var r = new SKRect(left, bottom - pH, right, bottom);
                    using var rr = MakeRRect(r, 0, 0, radius, radius);
                    using var shader = SKShader.CreateLinearGradient(new SKPoint(0, r.Top), new SKPoint(0, r.Bottom),
                        new[] { passLight, passDark }, null, SKShaderTileMode.Clamp);
                    barPaint.Shader = shader; canvas.DrawRoundRect(rr, barPaint); barPaint.Shader = null;
                }
                if (fH > 0.1f)
                {
                    var r = new SKRect(left, bottom - pH - fH, right, bottom - pH);
                    using var rr = MakeRRect(r, radius, radius, 0, 0);
                    using var shader = SKShader.CreateLinearGradient(new SKPoint(0, r.Top), new SKPoint(0, r.Bottom),
                        new[] { failLight, failDark }, null, SKShaderTileMode.Clamp);
                    barPaint.Shader = shader; canvas.DrawRoundRect(rr, barPaint); barPaint.Shader = null;
                }

                if (i % 3 == 0)
                {
                    var label = i.ToString("00");
                    var tbx = new SKRect(); textPaint.MeasureText(label, ref tbx);
                    canvas.DrawText(label, cx - tbx.MidX, chart.Bottom + 18, textPaint);
                }

                if (t <= 0) continue;

                var tag = t.ToString();
                var tb = new SKRect(); valuePaint.MeasureText(tag, ref tb);
                float desired = chart.Bottom - totalH - 6;
                float baseline = SafeBaseline(valuePaint, tag, desired, chart.Top, chart.Bottom);
                canvas.DrawText(tag, cx - tb.MidX, baseline, valuePaint);
            }
        }

        // ===== 绘图：良率圆环 =====
        private void SkYield_PaintSurface(object sender, SkiaSharp.Views.Desktop.SKPaintSurfaceEventArgs e)
        {
            var canvas = e.Surface.Canvas;
            var info = e.Info;
            canvas.Clear(SKColors.White);

            // —— 尺寸与位置（圆环更小，但卡片尺寸不变）——
            float pad = 8f;
            float cx = info.Width / 2f, cy = info.Height / 2f;

            // 外径按比例缩放（YieldRingScale: 0~1，建议 0.60~0.80）
            float ringScale = (float)Math.Max(0.3, Math.Min(1.0, YieldRingScale));
            float outerSize = Math.Min(info.Width, info.Height) * ringScale;

            // 给一点内边距，避免贴边
            float size = Math.Max(outerSize - pad * 2f, 16f);

            // 圆环粗细（相对外径），最少 6 像素防止太细
            float thickness = Math.Max(6f, (float)(YieldRingThickness * size));

            float startAngle = -90f; // 从 12 点方向开始
            double eased = 1 - Math.Pow(1 - _anim, 3);
            double ratio = _todayYield * eased;                 // 今日良率（ReloadTodayHours 已按 PASS/(PASS+FAIL) 计算）
            float sweep = (float)(360.0 * Clamp01(ratio));   // 0..1 之间

            // 圆环外接正方形
            var box = new SKRect(cx - size / 2f, cy - size / 2f, cx + size / 2f, cy + size / 2f);

            // 颜色
            var ringBg = new SKColor(231, 238, 247);
            var g1 = new SKColor(167, 243, 208); // #A7F3D0
            var g2 = new SKColor(16, 185, 129);  // #10B981
            var textColor = new SKColor(11, 18, 32);

            // 背景环
            using var bg = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = thickness,
                Color = ringBg,
                StrokeCap = SKStrokeCap.Round
            };
            canvas.DrawArc(box, 0, 360, false, bg);

            // ===== 前景进度环（把渐变接缝挪到弧线外）=====
            using var fg = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = thickness,
                StrokeCap = SKStrokeCap.Round
            };

            // 渐变首尾同色，保证环路连续；中间 g2 为高亮色
            var colors = new[] { g1, g2, g1 };     // 首尾相同色
            var offsets = new[] { 0f, 0.99f, 1f };  // g2 出现在环上大约 70% 位置（可调）

            // 把接缝放到弧线起点之前（比如起点往回 20°），这样弧线不经过接缝
            float seamAngle = startAngle - 5;
            using var shader = SKShader.CreateSweepGradient(new SKPoint(cx, cy), colors, offsets);
            fg.Shader = shader.WithLocalMatrix(SKMatrix.CreateRotationDegrees(seamAngle, cx, cy));


            if (!_todayMissing)
            {
                if (sweep >= 359.4f)
                {
                    // 接近整圈：直接画整圈，彻底没有端帽/接缝
                    canvas.DrawCircle(cx, cy, size / 2f, fg);
                }
                else if (sweep > 0.5f)
                {
                    canvas.DrawArc(box, startAngle, sweep, false, fg);
                }
            }



            // 百分比文字
            string pct = (_todayYield * 100.0).ToString("0.#", CultureInfo.CurrentCulture) + "%";
            using var tp = new SKPaint { IsAntialias = true, Color = textColor, TextSize = size * 0.32f, FakeBoldText = true };
            var tb = new SKRect(); tp.MeasureText(pct, ref tb);
            canvas.DrawText(pct, cx - tb.MidX, cy - tb.MidY, tp);
        }


        // ===== 工具 =====
        private static SKRoundRect MakeRRect(SKRect rect, float tl, float tr, float br, float bl)
        {
            var rr = new SKRoundRect();
            rr.SetRectRadii(rect, new[]
            {
                new SKPoint(tl, tl), new SKPoint(tr, tr),
                new SKPoint(br, br), new SKPoint(bl, bl),
            });
            return rr;
        }

        private static int NiceCeil(int v)
        {
            if (v <= 5) return 5;
            double base10 = Math.Pow(10, Math.Floor(Math.Log10(v)));
            int[] steps = { 1, 2, 5, 10 };
            foreach (var s in steps)
            {
                int cand = (int)(s * base10);
                if (v <= cand) return cand;
            }
            return (int)(10 * base10);
        }

        private static float SafeBaseline(SKPaint paint, string text,
                                          float desiredBaseline,
                                          float chartTop, float chartBottom,
                                          float padTop = 10f, float padBottom = 4f)
        {
            var bounds = new SKRect();
            paint.MeasureText(text, ref bounds);
            float minBaseline = chartTop + padTop - bounds.Top;
            float maxBaseline = chartBottom - padBottom;
            if (desiredBaseline < minBaseline) return minBaseline;
            if (desiredBaseline > maxBaseline) return maxBaseline;
            return desiredBaseline;
        }


        //==========================================报警相关==============================//
        // 统一的中文字体（支持 CJK）
        private static readonly SKTypeface FONT_CJK = ResolveCjkTypeface();

        private static SKTypeface ResolveCjkTypeface()
        {
            return UiFontService.CreateSkiaTypeface("测试中文123");
        }


        // ======== 今日报警 TOP5 ========
        private class TopAlarmItem
        {
            public int Rank;
            public string Name = "";
            public int Count;
        }
        private readonly List<TopAlarmItem> _top5 = new();
        private void RefreshAlarmFromCache()
        {

            // B. 用缓存做内存计算 -> 各自更新UI
            // 1) 最近一周报警次数卡
            _alarmWeek.Clear();
            foreach (var (date, count, missing) in AlarmLogCompute.GetDailyCountsFromCache(7))
                _alarmWeek.Add(new AlarmDay { Date = date, Count = count, Missing = missing });

            // 2) 今日报警类型 TOP5（改为 Skia 数据源）
            var top = AlarmLogCompute.GetTopCategoriesFromCache(DateTime.Today, 5);
            _top5.Clear();
            int idx = 0;
            foreach (var t in top)
            {
                _top5.Add(new TopAlarmItem
                {
                    Rank = ++idx,
                    Name = t.Category ?? "",
                    Count = t.Count
                });
            }

            // 不再使用 ItemsControl 绑定
            // Top5AlarmList.ItemsSource = items;

            // 这里不强制重开动画（与现在策略一致）：
            // 若你想每次刷新都重放动画，改为 StartAnimation();
            // 当前策略：只重绘
            SkTop5?.InvalidateVisual();
        }
        /// <summary>
        /// 优先从缓存拉取今日每小时报警次数（24 个），失败则回落到 CSV 文件扫描。
        /// </summary>
        private void LoadAlarmHourlyFromCacheOrFile()
        {
            try
            {
                Array.Clear(_alarmHour, 0, _alarmHour.Length);
                _alarmTodayMissing = false;

                // 1) 优先：尝试缓存（若你的 AlarmLogCompute 没有此 API，可直接走 2)）
                try
                {
                    // 约定：若你的工具已有该方法，请保留；没有就让它抛异常走 2)
                    var fromCache = AlarmLogCompute.GetHourlyCountsFromCache(DateTime.Today);
                    if (fromCache != null && fromCache.Length == 24)
                    {
                        for (int i = 0; i < 24; i++) _alarmHour[i] = Math.Max(0, fromCache[i]);
                        return;
                    }
                }
                catch { /* 忽略，回落到 CSV */ }

                // 2) 回落：扫描今日报警 CSV 并按小时聚合
                string dir = ConfigService.Current.AlarmLogPath ?? "";
                if (!Directory.Exists(dir)) { _alarmTodayMissing = true; return; }

                // 尽量匹配包含 yyyyMMdd 的文件名；找不到则用全部 csv 中最新的那个
                string stamp = DateTime.Today.ToString("yyyyMMdd");
                var candidates = Directory.GetFiles(dir, "*.csv", SearchOption.TopDirectoryOnly)
                    .Where(f => Path.GetFileName(f).Contains(stamp))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                string? file = candidates.FirstOrDefault();
                if (file == null)
                {
                    _alarmTodayMissing = true;
                    return;
                }

                Encoding enc;
                try { enc = Encoding.GetEncoding("GB2312"); }
                catch { enc = new UTF8Encoding(false); }

                var lines = ReadAllLinesShared(file, enc);
                if (lines.Count == 0) { _alarmTodayMissing = true; return; }

                // 解析表头，寻找“小时/时间/发生时间/时刻”等字段
                var header = SmartSplitAny(lines[0]).Select(NormalizeCell).ToArray();
                int idxHour = IndexOf(header, "小时", "Hour", "HOUR", "时间", "发生时间", "时刻", "时段");

                for (int i = 1; i < lines.Count; i++)
                {
                    var row = SmartSplitAny(lines[i]).Select(NormalizeCell).ToArray();
                    if (row.Length == 0) continue;

                    int hour = -1;
                    if (idxHour >= 0 && idxHour < row.Length) hour = ExtractHourLoose(row[idxHour]);
                    if (hour < 0 || hour > 23)
                    {
                        // 没有小时列时，粗略用行号推断（不推荐，但尽量填充以避免空图）
                        if (idxHour < 0) hour = (i - 1) % 24;
                    }
                    if (hour >= 0 && hour <= 23) _alarmHour[hour]++;
                }
            }
            catch
            {
                _alarmTodayMissing = true;
            }
        }
        private void LoadAlarmDurationShareFromCacheOrFile()
        {
            _pieSlices.Clear();
            _pieTotalSeconds = 0;

            try
            {
                // 1) 优先：缓存
                var fromCache = AlarmLogCompute.GetCategoryDurationsFromCache(DateTime.Today);
                if (fromCache != null && fromCache.Count > 0)
                {
                    foreach (var (cat, sec) in fromCache)
                        if (sec > 0) _pieSlices.Add((cat ?? "Unknown", sec));
                }
                else
                {
                    // 2) 回落：直接扫描今日 CSV（用你给的表头）
                    string dir = ConfigService.Current.AlarmLogPath ?? "";
                    if (Directory.Exists(dir))
                    {
                        string stamp = DateTime.Today.ToString("yyyyMMdd");
                        var candidates = Directory.GetFiles(dir, "*.csv", SearchOption.TopDirectoryOnly)
                                                  .Where(f => Path.GetFileName(f).Contains(stamp))
                                                  .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                                                  .ToArray();

                                string? file = candidates.FirstOrDefault();

                                if (!string.IsNullOrEmpty(file))
                                {
                            Encoding enc;
                            try { enc = Encoding.GetEncoding("GB2312"); } catch { enc = new UTF8Encoding(false); }
                            var lines = ReadAllLinesShared(file, enc);
                            if (lines.Count > 0)
                            {
                                var header = SmartSplitAny(lines[0]).Select(NormalizeCell).ToArray();
                                int idxCategory = IndexOf(header, "报警类别", "类别", "Category", "Type");
                                int idxSeconds = IndexOf(header, "报警时间(s)", "报警时长(s)", "持续时间(s)", "Seconds", "Duration");

                                var dict = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

                                for (int i = 1; i < lines.Count; i++)
                                {
                                    var row = SmartSplitAny(lines[i]).Select(NormalizeCell).ToArray();
                                    if (row.Length == 0) continue;

                                    string cat = (idxCategory >= 0 && idxCategory < row.Length) ? (row[idxCategory] ?? "") : "Unknown";
                                    if (string.IsNullOrEmpty(cat)) cat = "Unknown";

                                    double sec = 0;
                                    if (idxSeconds >= 0 && idxSeconds < row.Length)
                                    {
                                        var s = (row[idxSeconds] ?? "").Trim().TrimEnd('s', 'S');
                                        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out sec))
                                            double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out sec);
                                        if (sec < 0) sec = 0;
                                    }

                                    if (sec > 0)
                                        dict[cat] = dict.TryGetValue(cat, out var acc) ? acc + sec : sec;
                                }

                                foreach (var kv in dict.OrderByDescending(k => k.Value))
                                    _pieSlices.Add((kv.Key, kv.Value));
                            }
                        }
                    }
                }

                // Top-N + 其他 聚合（让图例不拥挤）
                const int TOP_N = 6;
                if (_pieSlices.Count > TOP_N)
                {
                    var top = _pieSlices.Take(TOP_N).ToList();
                    var otherSec = _pieSlices.Skip(TOP_N).Sum(x => x.Seconds);
                    _pieSlices.Clear();
                    _pieSlices.AddRange(top);
                    if (otherSec > 0) _pieSlices.Add((L("其他"), otherSec));
                }

                _pieTotalSeconds = _pieSlices.Sum(x => x.Seconds);
            }
            catch
            {
                _pieSlices.Clear();
                _pieTotalSeconds = 0;
            }
        }

        private static string[] SmartSplitAny(string line)
        {
            if (string.IsNullOrEmpty(line)) return Array.Empty<string>();
            if (line.Contains(",")) return line.Split(',');
            if (line.Contains("\t")) return line.Split('\t');
            if (line.Contains(";")) return line.Split(';');
            if (line.Contains("|")) return line.Split('|');
            return new[] { line };
        }

        /// <summary>去掉单双/中英文引号与多余空白，提升表头匹配和数值解析的容错。</summary>
        private static string NormalizeCell(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            var t = s.Trim();
            if (t.Length >= 2)
            {
                var first = t[0];
                var last = t[t.Length - 1];
                if ((first == '"' && last == '"') || (first == '“' && last == '”') || (first == '\'' && last == '\''))
                {
                    t = t.Substring(1, t.Length - 2);
                    t = t.Replace("\"\"", "\"");
                }
            }
            return t.Trim();
        }

        private static int IndexOf(string[] arr, params string[] keys)
        {
            for (int i = 0; i < arr.Length; i++)
            {
                var t = NormalizeCell(arr[i]).ToUpperInvariant();
                foreach (var k in keys)
                    if (t == NormalizeCell(k).ToUpperInvariant()) return i;
            }
            return -1;
        }

        private static int ExtractHourLoose(string s)
        {
            s = NormalizeCell(s);
            if (string.IsNullOrWhiteSpace(s)) return -1;

            // 直接整数（0-23）
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int h) && h >= 0 && h <= 23) return h;

            // 标准时间
            if (DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.None, out var dt)) return dt.Hour;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt)) return dt.Hour;

            // “08:00-09:00”、“08点”、“8时30分” 之类
            var digits = new string(s.TakeWhile(ch => char.IsDigit(ch)).ToArray());
            if (digits.Length > 0 && int.TryParse(digits, out h) && h >= 0 && h <= 23) return h;

            return -1;
        }


        private void SkTop5_PaintSurface(object sender, SkiaSharp.Views.Desktop.SKPaintSurfaceEventArgs e)
        {
            var canvas = e.Surface.Canvas;
            var info = e.Info;
            canvas.Clear(SKColors.White);

            // 画布区域 & 内边距
            float padX = 10f, padY = 8f;
            var area = new SKRect(padX, padY, info.Width - padX, info.Height - padY);

            // 文字画笔（用你统一的中文字体；若没有 FONT_UI，就去掉 Typeface）
            // SkTop5_PaintSurface
            using var namePaint = new SKPaint { Typeface = FONT_CJK, IsAntialias = true, Color = new SKColor(107, 114, 128), TextSize = 13 };
            using var countPaint = new SKPaint { Typeface = FONT_CJK, IsAntialias = true, Color = new SKColor(55, 65, 81), TextSize = 16, FakeBoldText = true };

            using var trackPaint = new SKPaint { IsAntialias = true, Color = new SKColor(0xEE, 0xF2, 0xF7) }; // #EEF2F7
            using var barPaint = new SKPaint { IsAntialias = true };

            if (_top5.Count == 0)
            {
                var tip = L("今日暂无报警");
                var b = new SKRect(); namePaint.MeasureText(tip, ref b);
                canvas.DrawText(tip, area.MidX - b.MidX, area.MidY + b.Height / 2, namePaint);
                return;
            }

            int max = Math.Max(1, _top5.Max(x => x.Count));

            // 布局：一行 = 名称(上) + 胶囊条(下) + 右侧数字
            int rows = _top5.Count;
            float rowGap = 20f;
            float rowH = (area.Height - rowGap * (rows - 1)) / rows;

            float valueColW = 48f;                      // 右侧数字列宽
            float barLeft = area.Left;                // 胶囊起点（与名称左对齐）
            float barRight = area.Right - valueColW;   // 预留右侧数字
            float fullW = Math.Max(0, barRight - barLeft);

            // 胶囊高度 & 圆角
            float barH = 16f;                      // 视觉更接近你的图
            float radius = barH / 2f;

            // 颜色：沿用报警周图的橙色渐变
            var barDark = new SKColor(234, 88, 12);    // 橙深
            var barLight = new SKColor(253, 186, 116);  // 橙浅

            for (int i = 0; i < rows; i++)
            {
                var it = _top5[i];

                // 每行起点
                float yTop = area.Top + i * (rowH + rowGap);

                // —— 名称（灰，左上）——
                string name = EllipsisFit(it.Name ?? "", namePaint, fullW - 6); // 给右侧胶囊留点空
                var nb = new SKRect(); namePaint.MeasureText(name, ref nb);
                float nameBase = yTop + nb.Height; // 简单基线
                canvas.DrawText(name, barLeft, nameBase, namePaint);

                // —— 胶囊条基线（名称下方 6px）——
                float gapAfterName = 6f;
                float yBarMid = nameBase + gapAfterName + barH / 2f;

                // —— 动画：与其它卡片一致（_anim + Stagger + EaseOutCubic）——
                double local = Clamp01((_anim - i * Stagger) / (1.0 - Math.Min(1.0, i * Stagger)));
                double eased = EaseOutCubic(local);

                // —— 轨道（浅灰整条）——
                var trackRect = new SKRect(barLeft, yBarMid - barH / 2, barRight, yBarMid + barH / 2);
                using (var rrTrack = new SKRoundRect(trackRect, radius, radius))
                    canvas.DrawRoundRect(rrTrack, trackPaint);

                // —— 进度（蓝色渐变，圆角；极小值显示成“圆点”）——
                float ratio = (float)it.Count / max;
                float w = fullW * ratio * (float)eased;

                if (it.Count > 0 && w < barH * 0.45f)
                {
                    // 很小：用小圆点表现
                    using var shaderDot = SKShader.CreateLinearGradient(
                        new SKPoint(0, yBarMid - barH / 2),
                        new SKPoint(0, yBarMid + barH / 2),
                        new[] { barLight, barDark }, null, SKShaderTileMode.Clamp);
                    barPaint.Shader = shaderDot;
                    canvas.DrawCircle(barLeft + radius, yBarMid, radius, barPaint);
                    barPaint.Shader = null;
                }
                else if (w > 0.5f)
                {
                    var fillRect = new SKRect(barLeft, yBarMid - barH / 2, barLeft + w, yBarMid + barH / 2);
                    using var shader = SKShader.CreateLinearGradient(
                        new SKPoint(0, fillRect.Top),
                        new SKPoint(0, fillRect.Bottom),
                        new[] { barLight, barDark }, null, SKShaderTileMode.Clamp);
                    barPaint.Shader = shader;
                    using (var rrFill = new SKRoundRect(fillRect, radius, radius))
                        canvas.DrawRoundRect(rrFill, barPaint);
                    barPaint.Shader = null;
                }

                // —— 右侧数量（深色、粗体，垂直对齐到胶囊中线）——
                var valText = it.Count.ToString();
                var vb = new SKRect(); countPaint.MeasureText(valText, ref vb);
                float vx = area.Right - valueColW / 2f - vb.MidX;
                float vy = yBarMid + vb.Height / 2 - 1;
                canvas.DrawText(valText, vx, vy, countPaint);
            }
        }

        private static string EllipsisFit(string s, SKPaint paint, float maxWidth)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var bounds = new SKRect();
            paint.MeasureText(s, ref bounds);
            if (bounds.Width <= maxWidth) return s;

            const string ell = "…";
            paint.MeasureText(ell, ref bounds);
            float ellW = bounds.Width;

            int left = 0, right = s.Length;
            string best = "";
            while (left <= right)
            {
                int mid = (left + right) / 2;
                string sub = s.Substring(0, mid) + ell;
                paint.MeasureText(sub, ref bounds);
                if (bounds.Width <= maxWidth)
                {
                    best = sub;
                    left = mid + 1;
                }
                else right = mid - 1;
            }
            return string.IsNullOrEmpty(best) ? ell : best;
        }


        // ======== 报警周数据 ========
        private class AlarmDay
        {
            public DateTime Date { get; set; }
            public int Count { get; set; }    // 当天报警条数（这里仅用于周统计）
            public bool Missing { get; set; } // 文件缺失或读取失败
        }

        private readonly List<AlarmDay> _alarmWeek = new();


        // ======== 绘制报警周图 ========
        private void SkAlarm_PaintSurface(object sender, SkiaSharp.Views.Desktop.SKPaintSurfaceEventArgs e)
        {
            var canvas = e.Surface.Canvas;
            var info = e.Info;
            canvas.Clear(SKColors.White);

            float chartPadLeft = 56, chartPadRight = 24, chartPadTop = 8, chartPadBottom = 46;
            var chart = new SKRect(chartPadLeft, chartPadTop, info.Width - chartPadRight, info.Height - chartPadBottom);

            using var gridPaint = new SKPaint { Color = new SKColor(240, 243, 248), StrokeWidth = 1, IsStroke = true };
            using var axisPaint = new SKPaint { Color = new SKColor(228, 232, 240), StrokeWidth = 1, IsStroke = true };
            using var textPaint = new SKPaint { Color = new SKColor(107, 114, 128), TextSize = 12, IsAntialias = true };
            using var valuePaint = new SKPaint { IsAntialias = true, Color = new SKColor(55, 65, 81), TextSize = 12, FakeBoldText = true };
            using var dayPaint = new SKPaint { IsAntialias = true, Color = new SKColor(107, 114, 128), TextSize = 12 };
            using var barPaint = new SKPaint { IsAntialias = true };

            // 坐标轴与网格
            int max = Math.Max(1, _alarmWeek.Any() ? _alarmWeek.Max(d => d.Count) : 1);
            int yMax = NiceCeil((int)Math.Ceiling(max * 1.10));

            for (int i = 0; i <= 4; i++)
            {
                float y = chart.Top + (chart.Height * i / 4f);
                canvas.DrawLine(chart.Left, y, chart.Right, y, gridPaint);
            }
            canvas.DrawLine(chart.Left, chart.Bottom, chart.Right, chart.Bottom, axisPaint);
            canvas.DrawLine(chart.Left, chart.Top, chart.Left, chart.Bottom, axisPaint);

            for (int i = 0; i <= 4; i++)
            {
                int val = yMax * (4 - i) / 4;
                var y = chart.Top + (chart.Height * i / 4f);
                var label = val.ToString(CultureInfo.InvariantCulture);
                var bounds = new SKRect();
                textPaint.MeasureText(label, ref bounds);
                canvas.DrawText(label, chart.Left - 8 - bounds.Width, y + bounds.Height / 2, textPaint);
            }

            if (_alarmWeek.Count == 0) return;

            float slot = chart.Width / _alarmWeek.Count;
            float barWidth = slot * 0.52f;
            float radius = 6f;

            var barDark = new SKColor(234, 88, 12);   // 橙色深
            var barLight = new SKColor(253, 186, 116);// 橙色浅

            for (int i = 0; i < _alarmWeek.Count; i++)
            {
                var d = _alarmWeek[i];
                float cx = chart.Left + slot * i + slot / 2f;

                // X轴日期
                string dayLabel = d.Date.ToString("MM-dd");
                var db = new SKRect(); dayPaint.MeasureText(dayLabel, ref db);
                canvas.DrawText(dayLabel, cx - db.MidX, chart.Bottom + 18, dayPaint);

                // 动画
                double local = Clamp01((_anim - i * 0.08) / (1.0 - Math.Min(1.0, i * 0.08)));
                double eased = EaseOutCubic(local);

                if (d.Missing)
                {
                    var tag = "0";
                    var tb = new SKRect(); valuePaint.MeasureText(tag, ref tb);
                    canvas.DrawText(tag, cx - tb.MidX, chart.Bottom - 6, valuePaint);
                    continue;
                }

                float h = chart.Height * (float)((d.Count / Math.Max(1.0, (double)yMax)) * eased);
                float left = cx - barWidth / 2f, right = cx + barWidth / 2f;
                float bottom = chart.Bottom;

                var r = new SKRect(left, bottom - h, right, bottom);
                using var rr = MakeRRect(r, radius, radius, radius, radius);
                using var shader = SKShader.CreateLinearGradient(new SKPoint(0, r.Top), new SKPoint(0, r.Bottom),
                    new[] { barLight, barDark }, null, SKShaderTileMode.Clamp);
                barPaint.Shader = shader; canvas.DrawRoundRect(rr, barPaint); barPaint.Shader = null;

                // 顶部数字
                var tag2 = d.Count.ToString();
                var tb2 = new SKRect(); valuePaint.MeasureText(tag2, ref tb2);
                float desired = chart.Bottom - h - 6;
                float baseline = SafeBaseline(valuePaint, tag2, desired, chart.Top, chart.Bottom);
                canvas.DrawText(tag2, cx - tb2.MidX, baseline, valuePaint);
            }
        }

        // === 新增：今日 24 小时报警次数（折线图数据） ===
        private readonly int[] _alarmHour = new int[24];
        private bool _alarmTodayMissing = false;
        private void SkAlarmHour_PaintSurface(object? sender, SkiaSharp.Views.Desktop.SKPaintSurfaceEventArgs e)
        {
            var canvas = e.Surface.Canvas;
            var info = e.Info;
            canvas.Clear(SKColors.White);

            float chartPadLeft = 56, chartPadRight = 24, chartPadTop = 8, chartPadBottom = 46;
            var chart = new SKRect(chartPadLeft, chartPadTop, info.Width - chartPadRight, info.Height - chartPadBottom);

            using var gridPaint = new SKPaint { Color = new SKColor(240, 243, 248), StrokeWidth = 1, IsStroke = true, IsAntialias = true };
            using var axisPaint = new SKPaint { Color = new SKColor(228, 232, 240), StrokeWidth = 1, IsStroke = true, IsAntialias = true };
            using var textPaint = new SKPaint { Color = new SKColor(107, 114, 128), TextSize = 12, IsAntialias = true };
            using var dotOutline = new SKPaint { Color = SKColors.White, StrokeWidth = 2, Style = SKPaintStyle.Stroke, IsAntialias = true };

            if (_alarmTodayMissing)
            {
                for (int i = 0; i <= 4; i++)
                {
                    float y = chart.Top + chart.Height * i / 4f;
                    canvas.DrawLine(chart.Left, y, chart.Right, y, gridPaint);
                }
                canvas.DrawLine(chart.Left, chart.Bottom, chart.Right, chart.Bottom, axisPaint);
                canvas.DrawLine(chart.Left, chart.Top, chart.Left, chart.Bottom, axisPaint);

                string msg = "0";
                var b = new SKRect(); textPaint.MeasureText(msg, ref b);
                canvas.DrawText(msg, chart.MidX - b.MidX, chart.MidY - b.MidY, textPaint);
                return;
            }

            int max = 1;
            for (int i = 0; i < 24; i++) max = Math.Max(max, _alarmHour[i]);
            int yMax = NiceCeil((int)Math.Ceiling(max * 1.10));

            for (int i = 0; i <= 4; i++)
            {
                float y = chart.Top + (chart.Height * i / 4f);
                canvas.DrawLine(chart.Left, y, chart.Right, y, gridPaint);
            }
            canvas.DrawLine(chart.Left, chart.Bottom, chart.Right, chart.Bottom, axisPaint);
            canvas.DrawLine(chart.Left, chart.Top, chart.Left, chart.Bottom, axisPaint);

            for (int i = 0; i <= 4; i++)
            {
                int val = yMax * (4 - i) / 4;
                var y = chart.Top + (chart.Height * i / 4f);
                var label = val.ToString(CultureInfo.InvariantCulture);
                var bounds = new SKRect(); textPaint.MeasureText(label, ref bounds);
                canvas.DrawText(label, chart.Left - 8 - bounds.Width, y + bounds.Height / 2, textPaint);
            }

            for (int h = 0; h < 24; h += 3)
            {
                float x = chart.Left + (chart.Width) * h / 23f;
                string lab = h.ToString("00");
                var tbx = new SKRect(); textPaint.MeasureText(lab, ref tbx);
                canvas.DrawText(lab, x - tbx.MidX, chart.Bottom + 18, textPaint);
            }

            var lineColor = new SKColor(239, 83, 80);
            using var linePaint = new SKPaint { Color = lineColor, StrokeWidth = 2.5f, IsAntialias = true, Style = SKPaintStyle.Stroke };
            using var fillPaint = new SKPaint { Color = new SKColor(lineColor.Red, lineColor.Green, lineColor.Blue, 38), IsAntialias = true, Style = SKPaintStyle.Fill };
            using var dotPaint = new SKPaint { Color = lineColor, IsAntialias = true, Style = SKPaintStyle.Fill };

            double eased = EaseOutCubic(_anim);

            var pts = new SKPoint[24];
            for (int i = 0; i < 24; i++)
            {
                float x = chart.Left + (chart.Width) * i / 23f;
                float y = chart.Bottom - (float)((_alarmHour[i] / Math.Max(1.0, (double)yMax)) * chart.Height * eased);
                pts[i] = new SKPoint(x, y);
            }

            using (var area = new SKPath())
            {
                area.MoveTo(pts[0]);
                for (int i = 1; i < pts.Length; i++) area.LineTo(pts[i]);
                area.LineTo(chart.Right, chart.Bottom);
                area.LineTo(chart.Left, chart.Bottom);
                area.Close();
                canvas.DrawPath(area, fillPaint);
            }

            using (var path = new SKPath())
            {
                path.MoveTo(pts[0]);
                for (int i = 1; i < pts.Length; i++) path.LineTo(pts[i]);
                canvas.DrawPath(path, linePaint);
            }

            int nowHour = DateTime.Now.Hour;
            for (int i = 0; i < 24; i++)
            {
                float r = (i == nowHour) ? 4.5f : 3f;
                canvas.DrawCircle(pts[i], r + 1.5f, dotOutline);
                canvas.DrawCircle(pts[i], r, dotPaint);
            }

            // ======= 标注端点数值：仅非 0，且无方框 =======
            using var labelPaint = new SKPaint
            {
                IsAntialias = true,
                Color = new SKColor(31, 41, 55),
                TextSize = 13,              // 想更显眼可调到 14~15
                FakeBoldText = true,
                Typeface = FONT_CJK
            };

            void DrawPointLabel(int idx)
            {
                int v = _alarmHour[idx];
                if (v <= 0) return; // 0 不显示

                string txt = v.ToString(CultureInfo.InvariantCulture);
                var tb = new SKRect(); labelPaint.MeasureText(txt, ref tb);

                // 放在点的上方，自动限位到图内
                float desired = pts[idx].Y - 8f;
                float baseline = SafeBaseline(labelPaint, txt, desired, chart.Top, chart.Bottom, padTop: 8f, padBottom: 6f);

                // 左半区文字放右侧，右半区放左侧，避免顶到边缘
                bool rightHalf = idx >= 12;
                float tx = rightHalf ? (pts[idx].X - tb.Width - 6f) : (pts[idx].X + 6f);
                tx = Math.Max(chart.Left + 2f, Math.Min(tx, chart.Right - tb.Width - 2f));

                canvas.DrawText(txt, tx, baseline, labelPaint);
            }

            // 逐点绘制（仅非 0 会画）
            for (int i = 0; i < 24; i++)
                DrawPointLabel(i);

            if (_alarmHour.All(v => v == 0))
            {
                var tip = L("无报警数据");
                var tb = new SKRect(); textPaint.MeasureText(tip, ref tb);
                canvas.DrawText(tip, chart.MidX - tb.MidX, chart.MidY, textPaint);
            }
        }



        // === 报警时长占比（环形饼） ===
        private readonly List<(string Name, double Seconds)> _pieSlices = new();
        private double _pieTotalSeconds = 0;

        private static string FormatDuration(double seconds)
        {
            if (seconds <= 0) return "0s";
            var ts = TimeSpan.FromSeconds(seconds);
            if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h{ts.Minutes:D2}m";
            if (ts.TotalMinutes >= 1) return $"{(int)ts.TotalMinutes}m{ts.Seconds:D2}s";
            return $"{ts.Seconds}s";
        }
        private void SkAlarmPie_PaintSurface(object? sender, SkiaSharp.Views.Desktop.SKPaintSurfaceEventArgs e)
        {
            var canvas = e.Surface.Canvas;
            var info = e.Info;
            canvas.Clear(SKColors.White);

            float pad = 8f;
            float w = info.Width, h = info.Height;

            // 饼图占宽度约 60%，高度优先（不变）
            float pieSize = Math.Min(h - pad * 2f, w * 0.60f - pad * 2f);
            pieSize = Math.Max(80f, pieSize);

            // 饼图靠左，居中
            float cx = pad + pieSize / 2f;
            float cy = h / 2f;
            var box = new SKRect(cx - pieSize / 2f, cy - pieSize / 2f, cx + pieSize / 2f, cy + pieSize / 2f);

            using var textPaint = new SKPaint { IsAntialias = true, Color = new SKColor(11, 18, 32), TextSize = 14, Typeface = FONT_CJK };
            using var borderPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = new SKColor(255, 255, 255, 220), StrokeWidth = 1.5f };

            if (_pieTotalSeconds <= 0 || _pieSlices.Count == 0)
            {
                using var bgFill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(238, 242, 247) };
                canvas.DrawOval(box, bgFill);
                var tip = L("暂无时长数据");
                var tb = new SKRect(); textPaint.MeasureText(tip, ref tb);
                canvas.DrawText(tip, (w - tb.Width) / 2f, cy - tb.MidY, textPaint);
                return;
            }

            SKColor[] palette = new[]
            {
        new SKColor(59,130,246), new SKColor(234,88,12), new SKColor(16,185,129),
        new SKColor(139,92,246), new SKColor(236,72,153), new SKColor(20,184,166),
        new SKColor(245,158,11),
    };

            double eased = EaseOutCubic(_anim);

            using (var bg = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(238, 242, 247) })
                canvas.DrawOval(box, bg);

            float start = -90f;
            for (int i = 0; i < _pieSlices.Count; i++)
            {
                var (name, sec) = _pieSlices[i];
                float sweep = (float)(360.0 * (sec / _pieTotalSeconds) * eased);
                if (sweep < 1f) sweep = 1f;

                using var seg = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = palette[i % palette.Length] };
                canvas.DrawArc(box, start, sweep, true, seg);
                start += sweep;
            }
            canvas.DrawOval(box, borderPaint);

            // ===== 右侧图例：名称 + 百分比 + 时长（右侧对齐到同一列） =====
            float legendX = box.Right + 32f;   // 整体更靠右
            float legendY = box.Top + 10f;
            float dot = 12f;
            float row = 26f;

            using var legendText = new SKPaint
            {
                IsAntialias = true,
                Color = new SKColor(55, 65, 81),
                TextSize = 13,
                FakeBoldText = true,
                Typeface = FONT_CJK,
                SubpixelText = true,
                LcdRenderText = true
            };
            using var durationText = new SKPaint
            {
                IsAntialias = true,
                Color = new SKColor(75, 85, 99), // 略深灰
                TextSize = 13,
                Typeface = FONT_CJK,
                SubpixelText = true,
                LcdRenderText = true
            };

            float labelStartX = legendX + dot + 10f; // 名称+百分比起点
            float gap = 12f;

            // 1) 先测量“名称 + 百分比”的最大宽度，用于统一对齐时长列
            float maxLabelW = 0f;
            for (int i = 0; i < _pieSlices.Count; i++)
            {
                var (name, sec) = _pieSlices[i];
                float pct = (float)(sec / _pieTotalSeconds * 100.0);
                string label = $"{name}  {pct:0.#}%";
                var b = new SKRect(); legendText.MeasureText(label, ref b);
                if (b.Width > maxLabelW) maxLabelW = b.Width;
            }
            float durationX = labelStartX + maxLabelW + gap;

            // 2) 绘制每行：圆点 + “名称  百分比” + “时长”
            for (int i = 0; i < _pieSlices.Count; i++)
            {
                var (name, sec) = _pieSlices[i];
                float pct = (float)(sec / _pieTotalSeconds * 100.0f);
                string label = $"{name}  {pct:0.#}%";
                string dur = FormatDuration(sec); // 例如 1h23m / 12m30s

                using var dotPaint = new SKPaint { IsAntialias = true, Color = palette[i % palette.Length], Style = SKPaintStyle.Fill };
                canvas.DrawCircle(legendX, legendY + i * row - 3f, dot / 2f, dotPaint);

                canvas.DrawText(label, labelStartX, legendY + i * row, legendText);
                canvas.DrawText(dur, durationX, legendY + i * row, durationText);
            }

            // 3) “总报警时长”放在图例下方
            string totalLine = L("总报警时长：") + FormatDuration(_pieTotalSeconds);
            using var totalPaint = new SKPaint
            {
                IsAntialias = true,
                Color = new SKColor(31, 41, 55),
                TextSize = 25,
                FakeBoldText = true,
                Typeface = FONT_CJK,
                SubpixelText = true,
                LcdRenderText = true
            };
            float totalY = legendY + _pieSlices.Count * row + 16f;
            canvas.DrawText(totalLine, legendX, totalY, totalPaint);

        }

    }
}

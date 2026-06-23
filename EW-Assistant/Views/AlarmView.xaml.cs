using EW_Assistant.Services;
using EW_Assistant.Warnings;
using SkiaSharp;
using SkiaSharp.Views.WPF;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.IO;
using System.Diagnostics;

namespace EW_Assistant.Views
{
    public partial class AlarmView : UserControl
    {
        // ===== 字体 =====
        private static readonly SKTypeface FONT_CJK = ResolveCjkTypeface();

        // === Auto Refresh ===
        private System.Windows.Threading.DispatcherTimer _autoTimer;
        private bool _isReloading = false;
        private bool _autoRefreshOnlyToday = true; // 只在当天自动刷新；如需全部日期都刷，设为 false

        private static string L(string chineseText)
        {
            return UiLanguageService.CurrentText(chineseText);
        }

        private static SKTypeface ResolveCjkTypeface()
        {
            return UiFontService.CreateSkiaTypeface("测试中文123");
        }

        // ===== 动画 =====
        private double _anim = 1.0;
        private readonly System.Diagnostics.Stopwatch _sw = new();
        private bool _isAnimating = false;
        private readonly TimeSpan _duration = TimeSpan.FromMilliseconds(520);
        private static double EaseOutCubic(double x) => 1 - Math.Pow(1 - x, 3);
        private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);

        // ===== 选中日期 =====
        private DateTime _day = DateTime.Today;

        // ===== 数据结构 =====
        private class DayItem { public DateTime Date; public int Count; public bool Missing; }
        private readonly List<DayItem> _week = new();
        private DateTime _weekAnchor = DateTime.Today;
        private DateTime _cacheAnchor = DateTime.MinValue;

        private readonly int[] _hour = new int[24];
        private bool _hourMissing = false;

        private class TopCat { public string Name = ""; public int Count; public double Seconds; }
        private readonly List<TopCat> _top = new();

        private readonly List<(string Name, double Seconds)> _pie = new();
        private double _pieTotal = 0;

        // KPI（当日）
        private int _kpiCount = 0;
        private double _kpiSeconds = 0;



        private sealed class AlarmRow
        {
            public DateTime Start;
            public string Code = "";
            public string Category = "";
            public double Seconds;
            public string Content = "";
            public string FileName = "";
        }

        public AlarmView()
        {
            InitializeComponent();

            Loaded += (_, __) =>
            {
                InitDateUi();
                ReloadAll();
                // 启动 10 秒自动刷新
                if (_autoTimer == null)
                {
                    _autoTimer = new System.Windows.Threading.DispatcherTimer();
                    _autoTimer.Interval = TimeSpan.FromSeconds(10);
                    _autoTimer.Tick += AutoTimer_Tick;
                }
                _autoTimer.Start();

            };

            Unloaded += (_, __) =>
            {
                CompositionTarget.Rendering -= OnRendering;
                // 停止定时器，避免卸载后仍触发
                if (_autoTimer != null)
                {
                    _autoTimer.Stop();
                    _autoTimer.Tick -= AutoTimer_Tick;
                    _autoTimer = null;
                }
            };

            // 绑定 DataGrid 的数据源
            if (FindName("DetailGrid") is DataGrid dg) dg.ItemsSource = _detailRows;

            // 绑定 TOP 卡片点击事件
            SkTop.MouseLeftButtonDown += SkTop_MouseLeftButtonDown;

        }

        private void BtnOpenAlarmFolder_Click(object sender, RoutedEventArgs e)
        {
            var root = ConfigService.Current.AlarmLogPath;
            if (string.IsNullOrWhiteSpace(root))
            {
                MainWindow.PostProgramInfo(L("报警路径未配置，无法打开目录。"), "warn");
                return;
            }

            try
            {
                if (!Directory.Exists(root))
                    Directory.CreateDirectory(root);

                using var proc = Process.Start(new ProcessStartInfo("explorer.exe", root)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MainWindow.PostProgramInfo(L("打开目录失败：") + ex.Message, "error");
            }
        }

        private void AutoTimer_Tick(object sender, EventArgs e)
        {
            if (_autoRefreshOnlyToday && _day.Date != DateTime.Today) return; // 仅当天自动刷
            SafeReloadAll(false, reloadWeek: true);
        }

        private void SafeReloadAll(bool animate, bool reloadWeek = false)
        {
            if (_isReloading) return;
            _isReloading = true;
            try { ReloadAll(animate, reloadWeek); }
            catch { /* log if needed */ }
            finally { _isReloading = false; }
        }

        // ====== 顶部日期选择 UI ======
        private static DateTime ClampToRecentRange(DateTime d)
        {
            var today = DateTime.Today;
            var min = today.AddDays(-6);
            if (d < min) d = min;
            if (d > today) d = today;
            return d.Date;
        }

        private void InitDateUi()
        {
            CboRecent.Items.Clear();
            var today = DateTime.Today;
            for (int i = 0; i < 7; i++)
            {
                var d = today.AddDays(-i);
                CboRecent.Items.Add(d.ToString("yyyy-MM-dd"));
            }

            _day = ClampToRecentRange(_day);
            int idx = 0;
            for (int i = 0; i < CboRecent.Items.Count; i++)
            {
                if (DateTime.TryParse((string)CboRecent.Items[i], out var d) && d.Date == _day)
                {
                    idx = i;
                    break;
                }
            }
            CboRecent.SelectedIndex = idx;
            UpdateDateButtons();
        }

        private void UpdateDateButtons()
        {
            var minDay = DateTime.Today.AddDays(-6);
            BtnPrev.IsEnabled = _day.Date > minDay;
            BtnNext.IsEnabled = _day.Date < DateTime.Today;
            BtnToday.IsEnabled = _day.Date != DateTime.Today;
            TxtSubTitle.Text = "  · " + L("当前：") + _day.ToString("yyyy-MM-dd");
        }

        private void BtnPrev_Click(object sender, RoutedEventArgs e) => SetDay(_day.AddDays(-1));
        private void BtnNext_Click(object sender, RoutedEventArgs e)
        {
            if (_day.Date >= DateTime.Today) return;
            SetDay(_day.AddDays(1));
        }
        private void BtnToday_Click(object sender, RoutedEventArgs e) => SetDay(DateTime.Today);

        private void CboRecent_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CboRecent.SelectedItem is string s && DateTime.TryParse(s, out var d))
                SetDay(d.Date);
        }
        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            ReloadAll();

        }

        private void SetDay(DateTime d)
        {
            var clamped = ClampToRecentRange(d);
            if (clamped.Date == _day.Date)
            {
                UpdateDateButtons();
                return;
            }

            _day = clamped;
            for (int i = 0; i < CboRecent.Items.Count; i++)
            {
                if (DateTime.TryParse((string)CboRecent.Items[i], out var cd) && cd.Date == _day)
                { CboRecent.SelectedIndex = i; break; }
            }
            UpdateDateButtons();
            ReloadAll(reloadWeek: false);
        }

        // 原: private void ReloadAll()
        // 改:
        private void ReloadAll(bool animate = true, bool reloadWeek = true)
        {
            var cfgPath = ConfigService.Current?.AlarmLogPath;
            bool needCache = reloadWeek || _cacheAnchor.Date != DateTime.Today;
            if (needCache)
            {
                AlarmLogCache.LoadRecent(cfgPath, days: 7);
                _cacheAnchor = DateTime.Today;
            }

            bool needWeek = reloadWeek || _weekAnchor.Date != DateTime.Today;
            if (needWeek) ReloadRecentWeek();
            ReloadDay(_day);

            TitleWeek.Text = string.Format(L("最近 7 天报警次数（截至 {0:MM-dd}）"), DateTime.Today);
            TitlePie.Text = string.Format(L("报警时长占比（{0:MM-dd}）"), _day);
            TitleHour.Text = string.Format(L("24 小时报警次数（{0:MM-dd}）"), _day);
            TitleTop.Text = string.Format(L("报警类别 TOP（{0:MM-dd}）"), _day);

            // KPI
            KpiCount.Text = _kpiCount.ToString("N0", CultureInfo.CurrentCulture);
            KpiSeconds.Text = FormatSecondsSmart(_kpiSeconds);
            KpiAvg.Text = (_kpiSeconds / Math.Max(_kpiCount, 1)).ToString("0.0", CultureInfo.InvariantCulture);

            ComputeLongestWindow(_day);
            RefreshDetailAfterReload();

            if (animate) StartAnimation();
            else RedrawNow();
        }


        private void RefreshDetailAfterReload()
        {
            if (!string.IsNullOrWhiteSpace(_selectedTopCategory))
                LoadDetailForCategory(_day, _selectedTopCategory!);
            else
                ClearDetail(L("在上面的『报警类别 TOP』中点击一个类别条，查看该类别的原始记录"));
        }

        private void ClearDetail(string? tip = null)
        {
            _detailRows.Clear();
            if (FindName("DetailTitle") is TextBlock ttl) ttl.Text = L("明细 · 未选择");
            if (FindName("DetailGrid") is FrameworkElement g) g.Visibility = Visibility.Collapsed;
            if (FindName("DetailEmpty") is FrameworkElement e)
            {
                if (e is TextBlock tb && !string.IsNullOrWhiteSpace(tip)) tb.Text = tip!;
                e.Visibility = Visibility.Visible;
            }
        }

        private void StartAnimation()
        {
            _anim = 0;
            _sw.Restart();
            _isAnimating = true;
            CompositionTarget.Rendering -= OnRendering;
            CompositionTarget.Rendering += OnRendering;
        }
        private void OnRendering(object? sender, EventArgs e)
        {
            if (!_isAnimating) return;
            _anim = Clamp01(_sw.Elapsed.TotalMilliseconds / _duration.TotalMilliseconds);

            SkAlarmWeek?.InvalidateVisual();
            SkHour?.InvalidateVisual();
            SkTop?.InvalidateVisual();
            SkPie?.InvalidateVisual();

            if (_anim >= 1.0)
            {
                _isAnimating = false;
                _sw.Stop();
                CompositionTarget.Rendering -= OnRendering;
                _anim = 1.0;
            }
        }

        // ====== 数据装填（单日） ======
        // ====== 数据装填（单日） —— 一次扫描产全量统计 ======
        private void ReloadDay(DateTime day)
        {
            Array.Clear(_hour, 0, _hour.Length);
            Array.Clear(_hourSeconds, 0, _hourSeconds.Length);
            _hourMissing = false;

            _top.Clear();
            _pie.Clear();
            _pieTotal = 0;

            _kpiCount = 0;
            _kpiSeconds = 0;

            var today = DateTime.Today;
            if (day.Date < today.AddDays(-6) || day.Date > today)
            {
                _hourMissing = true;
                return;
            }

            var raw = AlarmLogCache.GetDayRaw(day);
            if (raw == null || raw.Missing || raw.LinesAll == null || raw.LinesAll.Length == 0)
            {
                _hourMissing = true;
                return;
            }

            var hourlyCounts = AlarmLogCompute.GetHourlyCountsFromCache(day);
            var hourlySeconds = AlarmLogCompute.GetHourlySecondsFromCache(day);

            for (int i = 0; i < 24; i++)
            {
                if (hourlyCounts != null && i < hourlyCounts.Length) _hour[i] = hourlyCounts[i];
                if (hourlySeconds != null && i < hourlySeconds.Length) _hourSeconds[i] = hourlySeconds[i];
            }

            _kpiCount = _hour.Sum();
            _kpiSeconds = _hourSeconds.Sum();

            var catStats = AlarmLogCompute.GetCategoryStatsFromCache(day);
            var sorted = catStats
                .OrderByDescending(x => x.Seconds)
                .ThenByDescending(x => x.Count)
                .ToList();

            for (int i = 0; i < sorted.Count && i < 5; i++)
            {
                var s = sorted[i];
                _top.Add(new TopCat { Name = s.Category, Count = s.Count, Seconds = s.Seconds });
            }

            double totalSeconds = Math.Max(0.0, catStats.Sum(x => x.Seconds));
            double topSum = 0.0;
            foreach (var t in _top)
            {
                _pie.Add(new ValueTuple<string, double>(t.Name, t.Seconds));
                topSum += t.Seconds;
            }
            double others = Math.Max(0.0, totalSeconds - topSum);
            if (others > 0.0) _pie.Add(new ValueTuple<string, double>(L("其他"), others));
            _pieTotal = totalSeconds;
        }



        private void ReloadRecentWeek()
        {
            _week.Clear();
            _weekAnchor = DateTime.Today;
            foreach (var (d, count, missing) in AlarmLogCompute.GetDailyCountsFromCache(7))
            {
                _week.Add(new DayItem
                {
                    Date = d.Date,
                    Count = Math.Max(0, count),
                    Missing = missing
                });
            }
        }

        private IEnumerable<AlarmRow> EnumerateAlarmsFromCsv(DateTime day)
        {
            var today = DateTime.Today;
            if (day.Date < today.AddDays(-6) || day.Date > today) yield break; // 超出最近7天直接返回

            foreach (var r in AlarmLogCompute.EnumerateRecordsFromCache(day))
            {
                yield return new AlarmRow
                {
                    Start = r.Start,
                    Code = string.IsNullOrWhiteSpace(r.Code) ? "Unknown" : r.Code,
                    Category = string.IsNullOrWhiteSpace(r.Category) ? "Unknown" : r.Category,
                    Seconds = r.Seconds,
                    Content = r.Content,
                    FileName = r.FileName
                };
            }
        }

        // ===== 最长连续报警窗口（当天） —— 改为使用 _hourSeconds =====
        private void ComputeLongestWindow(DateTime day)
        {
            // 如果有“按小时累计秒数”，优先用它；否则退回“次数”
            bool useSec = false;
            for (int i = 0; i < 24; i++)
            {
                if (_hourSeconds[i] > 0.5) { useSec = true; break; }
            }

            int bestStart = -1, bestLen = 0;
            double bestSum = 0.0;

            int curStart = -1, curLen = 0;
            double curSum = 0.0;

            for (int h = 0; h < 24; h++)
            {
                bool on = useSec ? (_hourSeconds[h] > 0.5) : (_hour[h] > 0);
                if (on)
                {
                    if (curLen == 0) { curStart = h; curSum = 0.0; }
                    curLen++;
                    if (useSec) curSum += _hourSeconds[h];
                }
                else
                {
                    if (curLen > bestLen)
                    {
                        bestLen = curLen; bestStart = curStart; bestSum = curSum;
                    }
                    curLen = 0; curSum = 0.0;
                }
            }
            if (curLen > bestLen)
            {
                bestLen = curLen; bestStart = curStart; bestSum = curSum;
            }

            if (bestLen <= 0 || bestStart < 0)
            {
                LwRange.Text = L("无"); LwHours.Text = "—"; LwSeconds.Text = "—";
                LwBar.Visibility = Visibility.Collapsed;
                return;
            }

            int end = bestStart + bestLen;
            LwRange.Text = string.Format("{0:00}:00–{1:00}:00", bestStart, end);
            LwHours.Text = bestLen + " " + L("小时");
            LwSeconds.Text = useSec ? FormatSecondsSmart(bestSum) : "—";

            var span = Math.Max(1, Math.Min(24 - bestStart, bestLen));
            Grid.SetColumn(LwBar, bestStart);
            Grid.SetColumnSpan(LwBar, span);
            LwBar.Visibility = Visibility.Visible;
            LwBar.ToolTip = string.Format("{0:00}:00–{1:00}:00", bestStart, bestStart + span);
        }


        // ===== Skia 绘图 =====
        private void SkAlarmWeek_PaintSurface(object? s, SkiaSharp.Views.Desktop.SKPaintSurfaceEventArgs e)
        {
            var c = e.Surface.Canvas; var info = e.Info; c.Clear(SKColors.White);
            float chartPadLeft = 56, chartPadRight = 24, chartPadTop = 8, chartPadBottom = 46;
            var chart = new SKRect(chartPadLeft, chartPadTop, info.Width - chartPadRight, info.Height - chartPadBottom);

            using var grid = new SKPaint { Color = new SKColor(240, 243, 248), StrokeWidth = 1, IsStroke = true, IsAntialias = true };
            using var axis = new SKPaint { Color = new SKColor(228, 232, 240), StrokeWidth = 1, IsStroke = true, IsAntialias = true };
            using var txt = new SKPaint { Color = new SKColor(107, 114, 128), TextSize = 12, IsAntialias = true, Typeface = FONT_CJK };
            using var day = new SKPaint { Color = new SKColor(107, 114, 128), TextSize = 12, IsAntialias = true, Typeface = FONT_CJK };
            using var val = new SKPaint { Color = new SKColor(55, 65, 81), TextSize = 12, FakeBoldText = true, IsAntialias = true, Typeface = FONT_CJK };
            using var bar = new SKPaint { IsAntialias = true };

            int max = Math.Max(1, _week.Any() ? _week.Max(x => x.Count) : 1);
            int yMax = NiceCeil((int)Math.Ceiling(max * 1.10));

            for (int i = 0; i <= 4; i++)
            {
                float y = chart.Top + chart.Height * i / 4f;
                c.DrawLine(chart.Left, y, chart.Right, y, grid);
            }
            c.DrawLine(chart.Left, chart.Bottom, chart.Right, chart.Bottom, axis);
            c.DrawLine(chart.Left, chart.Top, chart.Left, chart.Bottom, axis);

            for (int i = 0; i <= 4; i++)
            {
                int v = yMax * (4 - i) / 4;
                var b = new SKRect(); txt.MeasureText(v.ToString(), ref b);
                float y = chart.Top + chart.Height * i / 4f;
                c.DrawText(v.ToString(), chart.Left - 8 - b.Width, y + b.Height / 2, txt);
            }
            if (_week.Count == 0) return;

            float slot = chart.Width / _week.Count;
            float bw = slot * 0.52f;
            float radius = 6f;
            var dark = new SKColor(234, 88, 12);
            var light = new SKColor(253, 186, 116);

            for (int i = 0; i < _week.Count; i++)
            {
                var it = _week[i];
                float cx = chart.Left + slot * i + slot / 2f;

                string lab = it.Date.ToString("MM-dd");
                var db = new SKRect(); day.MeasureText(lab, ref db);
                c.DrawText(lab, cx - db.MidX, chart.Bottom + 18, day);

                double local = Clamp01((_anim - i * 0.08) / (1.0 - Math.Min(1.0, i * 0.08)));
                double eased = EaseOutCubic(local);

                if (it.Missing)
                {
                    var tag = "0"; var tb = new SKRect(); val.MeasureText(tag, ref tb);
                    c.DrawText(tag, cx - tb.MidX, chart.Bottom - 6, val);
                    continue;
                }

                float h = chart.Height * (float)((it.Count / Math.Max(1.0, (double)yMax)) * eased);
                var r = new SKRect(cx - bw / 2, chart.Bottom - h, cx + bw / 2, chart.Bottom);
                using var rr = new SKRoundRect(r, radius, radius);
                using var shader = SKShader.CreateLinearGradient(new SKPoint(0, r.Top), new SKPoint(0, r.Bottom),
                    new[] { light, dark }, null, SKShaderTileMode.Clamp);
                bar.Shader = shader; c.DrawRoundRect(rr, bar); bar.Shader = null;

                var tag2 = it.Count.ToString();
                var tb2 = new SKRect(); val.MeasureText(tag2, ref tb2);
                float baseline = SafeBaseline(val, tag2, chart.Bottom - h - 6, chart.Top, chart.Bottom);
                c.DrawText(tag2, cx - tb2.MidX, baseline, val);
            }
        }

        private void SkHour_PaintSurface(object? s, SkiaSharp.Views.Desktop.SKPaintSurfaceEventArgs e)
        {
            var c = e.Surface.Canvas; var info = e.Info; c.Clear(SKColors.White);
            float chartPadLeft = 56, chartPadRight = 24, chartPadTop = 8, chartPadBottom = 46;
            var chart = new SKRect(chartPadLeft, chartPadTop, info.Width - chartPadRight, info.Height - chartPadBottom);

            using var grid = new SKPaint { Color = new SKColor(240, 243, 248), StrokeWidth = 1, IsStroke = true, IsAntialias = true };
            using var axis = new SKPaint { Color = new SKColor(228, 232, 240), StrokeWidth = 1, IsStroke = true, IsAntialias = true };
            using var txt = new SKPaint { Color = new SKColor(107, 114, 128), TextSize = 12, IsAntialias = true, Typeface = FONT_CJK };
            using var label = new SKPaint { Color = new SKColor(55, 65, 81), TextSize = 13, FakeBoldText = true, IsAntialias = true, Typeface = FONT_CJK };
            using var dotOutline = new SKPaint { Color = SKColors.White, StrokeWidth = 2, Style = SKPaintStyle.Stroke, IsAntialias = true };

            if (_hourMissing)
            {
                for (int i = 0; i <= 4; i++)
                {
                    float y = chart.Top + chart.Height * i / 4f;
                    c.DrawLine(chart.Left, y, chart.Right, y, grid);
                }
                c.DrawLine(chart.Left, chart.Bottom, chart.Right, chart.Bottom, axis);
                c.DrawLine(chart.Left, chart.Top, chart.Left, chart.Bottom, axis);
                var s0 = "0"; var bb = new SKRect(); txt.MeasureText(s0, ref bb);
                c.DrawText(s0, chart.MidX - bb.MidX, chart.MidY - bb.MidY, txt);
                return;
            }

            int max = Math.Max(1, _hour.Max());
            int yMax = NiceCeil((int)Math.Ceiling(max * 1.10));

            for (int i = 0; i <= 4; i++)
            {
                float y = chart.Top + chart.Height * i / 4f;
                c.DrawLine(chart.Left, y, chart.Right, y, grid);
            }
            c.DrawLine(chart.Left, chart.Bottom, chart.Right, chart.Bottom, axis);
            c.DrawLine(chart.Left, chart.Top, chart.Left, chart.Bottom, axis);

            for (int i = 0; i <= 4; i++)
            {
                int v = yMax * (4 - i) / 4;
                var b = new SKRect(); txt.MeasureText(v.ToString(), ref b);
                float y = chart.Top + chart.Height * i / 4f;
                c.DrawText(v.ToString(), chart.Left - 8 - b.Width, y + b.Height / 2, txt);
            }
            for (int h = 0; h < 24; h += 3)
            {
                float x = chart.Left + chart.Width * h / 23f;
                var b = new SKRect(); txt.MeasureText(h.ToString("00"), ref b);
                c.DrawText(h.ToString("00"), x - b.MidX, chart.Bottom + 18, txt);
            }

            var lineColor = new SKColor(239, 83, 80);
            using var line = new SKPaint { Color = lineColor, StrokeWidth = 2.5f, IsAntialias = true, Style = SKPaintStyle.Stroke };
            using var fill = new SKPaint { Color = new SKColor(lineColor.Red, lineColor.Green, lineColor.Blue, 38), IsAntialias = true, Style = SKPaintStyle.Fill };
            using var dot = new SKPaint { Color = lineColor, IsAntialias = true, Style = SKPaintStyle.Fill };

            double eased = EaseOutCubic(_anim);

            var pts = new SKPoint[24];
            for (int i = 0; i < 24; i++)
            {
                float x = chart.Left + chart.Width * i / 23f;
                float y = chart.Bottom - (float)((_hour[i] / Math.Max(1.0, (double)yMax)) * chart.Height * eased);
                pts[i] = new SKPoint(x, y);
            }

            using (var area = new SKPath())
            {
                area.MoveTo(pts[0]);
                for (int i = 1; i < pts.Length; i++) area.LineTo(pts[i]);
                area.LineTo(chart.Right, chart.Bottom);
                area.LineTo(chart.Left, chart.Bottom);
                area.Close();
                c.DrawPath(area, fill);
            }
            using (var path = new SKPath())
            {
                path.MoveTo(pts[0]);
                for (int i = 1; i < pts.Length; i++) path.LineTo(pts[i]);
                c.DrawPath(path, line);
            }

            int now = (_day.Date == DateTime.Today) ? DateTime.Now.Hour : -1;
            for (int i = 0; i < 24; i++)
            {
                float r = (i == now) ? 4.5f : 3f;
                c.DrawCircle(pts[i], r + 1.5f, dotOutline);
                c.DrawCircle(pts[i], r, dot);

                if (_hour[i] > 0)
                {
                    string sVal = _hour[i].ToString();
                    var tb = new SKRect(); label.MeasureText(sVal, ref tb);
                    float desired = pts[i].Y - 8f;
                    float baseline = SafeBaseline(label, sVal, desired, chart.Top, chart.Bottom, 8f, 6f);
                    bool rightHalf = i >= 12;
                    float tx = rightHalf ? (pts[i].X - tb.Width - 6f) : (pts[i].X + 6f);
                    tx = Math.Max(chart.Left + 2f, Math.Min(tx, chart.Right - tb.Width - 2f));
                    c.DrawText(sVal, tx, baseline, label);
                }
            }

            if (_hour.All(v => v == 0))
            {
                var tip = L("无报警数据"); var b = new SKRect(); txt.MeasureText(tip, ref b);
                c.DrawText(tip, chart.MidX - b.MidX, chart.MidY, txt);
            }
        }

        private void SkTop_PaintSurface(object? s, SkiaSharp.Views.Desktop.SKPaintSurfaceEventArgs e)
        {
            _topHits.Clear();

            var c = e.Surface.Canvas; var info = e.Info; c.Clear(SKColors.White);
            float padX = 10f, padY = 8f;
            var area = new SKRect(padX, padY, info.Width - padX, info.Height - padY);

            using var name = new SKPaint { Typeface = FONT_CJK, IsAntialias = true, Color = new SKColor(107, 114, 128), TextSize = 13 };
            using var num = new SKPaint { Typeface = FONT_CJK, IsAntialias = true, Color = new SKColor(55, 65, 81), TextSize = 16, FakeBoldText = true };
            using var track = new SKPaint { IsAntialias = true, Color = new SKColor(0xEE, 0xF2, 0xF7) };
            using var bar = new SKPaint { IsAntialias = true };

            if (_top.Count == 0)
            {
                var tip = L("暂无数据"); var b = new SKRect(); name.MeasureText(tip, ref b);
                c.DrawText(tip, area.MidX - b.MidX, area.MidY + b.Height / 2, name);
                return;
            }

            // 仅用于显示的“次数排序”视图（不修改 _top 本体，也不影响饼图）
            var view = _top
                .OrderByDescending(x => x.Count)      // ★ 按次数排
                .ThenByDescending(x => x.Seconds)     // 次要：时长
                .ToList();

            int rows = view.Count;
            float rowGap = 20f;
            float rowH = (area.Height - rowGap * (rows - 1)) / rows;

            float valueColW = 160f;
            float barLeft = area.Left;
            float barRight = area.Right - valueColW;
            float fullW = Math.Max(0, barRight - barLeft);

            float barH = 16f, radius = barH / 2f;
            var barDark = new SKColor(59, 130, 246);
            var barLight = new SKColor(147, 197, 253);

            double eased = EaseOutCubic(_anim);
            double maxCount = Math.Max(1.0, view.Max(x => (double)x.Count));   // 刻度用“次数”

            for (int i = 0; i < rows; i++)
            {
                var it = view[i];
                float yTop = area.Top + i * (rowH + rowGap);

                // 名称（自动省略）
                string title = EllipsisFit(it.Name ?? "", name, fullW - 6);
                var nb = new SKRect(); name.MeasureText(title, ref nb);
                float nameBase = yTop + nb.Height;
                c.DrawText(title, barLeft, nameBase, name);

                float yMid = nameBase + 6f + barH / 2f;

                // 灰轨
                var trackRect = new SKRect(barLeft, yMid - barH / 2, barRight, yMid + barH / 2);
                using (var rrTrack = new SKRoundRect(trackRect, radius, radius))
                    c.DrawRoundRect(rrTrack, track);

                // 蓝条（按“次数 / maxCount”）
                float ratio = (float)(it.Count / maxCount) * (float)eased;
                float w = fullW * Math.Max(0, Math.Min(1, ratio));

                if (it.Count > 0 && w < barH * 0.45f)
                {
                    using var shaderDot = SKShader.CreateLinearGradient(
                        new SKPoint(0, yMid - barH / 2), new SKPoint(0, yMid + barH / 2),
                        new[] { barLight, barDark }, null, SKShaderTileMode.Clamp);
                    bar.Shader = shaderDot;
                    c.DrawCircle(barLeft + radius, yMid, radius, bar);
                    bar.Shader = null;
                }
                else if (w > 0.5f)
                {
                    var fill = new SKRect(barLeft, yMid - barH / 2, barLeft + w, yMid + barH / 2);
                    using var shader = SKShader.CreateLinearGradient(
                        new SKPoint(0, fill.Top), new SKPoint(0, fill.Bottom),
                        new[] { barLight, barDark }, null, SKShaderTileMode.Clamp);
                    bar.Shader = shader;
                    using (var rrFill = new SKRoundRect(fill, radius, radius))
                        c.DrawRoundRect(rrFill, bar);
                    bar.Shader = null;
                }

                // 右侧文案：保持原样（先时长后次数），如需“次数优先显示”再改这行
                string right = string.Format(L("{0} 次 · {1}"), it.Count, FormatDuration(it.Seconds));

                var rb = new SKRect(); num.MeasureText(right, ref rb);
                float vx = area.Right - valueColW / 2f - rb.MidX;
                float vy = yMid + rb.Height / 2 - 1;
                c.DrawText(right, vx, vy, num);

                // === 收集命中区域（行区域：名称+间距+条形） ===
                float yBottom = yTop + nb.Height + 6f + barH;
                var rowRectPx = new SKRect(barLeft, yTop, barRight, yBottom);

                // 将 Skia 像素坐标换算为 WPF DIP 做命中测试
                float sx = (float)(e.Info.Width / Math.Max(1.0, SkTop.ActualWidth));
                float sy = (float)(e.Info.Height / Math.Max(1.0, SkTop.ActualHeight));
                Rect ToDip(SKRect r) => new Rect(r.Left / sx, r.Top / sy, r.Width / sx, r.Height / sy);

                _topHits.Add((ToDip(rowRectPx), it));
            }

           

        }



        private void SkPie_PaintSurface(object? s, SkiaSharp.Views.Desktop.SKPaintSurfaceEventArgs e)
        {
            var c = e.Surface.Canvas; var info = e.Info; c.Clear(SKColors.White);

            float pad = 8f;
            float w = info.Width, h = info.Height;

            float pieSize = Math.Min(h - pad * 2f, w * 0.60f - pad * 2f);
            pieSize = Math.Max(80f, pieSize);

            float cx = pad + pieSize / 2f;
            float cy = h / 2f;
            var box = new SKRect(cx - pieSize / 2f, cy - pieSize / 2f, cx + pieSize / 2f, cy + pieSize / 2f);

            using var border = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = new SKColor(255, 255, 255, 220), StrokeWidth = 1.5f };

            if (_pieTotal <= 0 || _pie.Count == 0)
            {
                using var bgFill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(238, 242, 247) };
                c.DrawOval(box, bgFill);
                using var t = new SKPaint { IsAntialias = true, Color = new SKColor(11, 18, 32), TextSize = 14, Typeface = FONT_CJK };
                var tip = L("暂无时长数据"); var tb = new SKRect(); t.MeasureText(tip, ref tb);
                c.DrawText(tip, (w - tb.Width) / 2f, cy - tb.MidY, t);
                return;
            }

            SKColor[] pal = {
                new SKColor(59,130,246), new SKColor(234,88,12), new SKColor(16,185,129),
                new SKColor(139,92,246), new SKColor(236,72,153), new SKColor(20,184,166),
                new SKColor(245,158,11),
            };

            using (var bg = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(238, 242, 247) })
                c.DrawOval(box, bg);

            double eased = EaseOutCubic(_anim);
            float start = -90f;
            for (int i = 0; i < _pie.Count; i++)
            {
                var (name, sec) = _pie[i];
                float sweep = (float)(360.0 * (sec / _pieTotal) * eased);
                if (sweep < 1f) sweep = 1f;

                using var seg = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = pal[i % pal.Length] };
                c.DrawArc(box, start, sweep, true, seg);
                start += sweep;
            }
            c.DrawOval(box, border);

            // 右侧图例
            float legendX = box.Right + 36f;
            float legendY = box.Top + 10f;
            float dot = 12f, row = 26f;

            using var label = new SKPaint { IsAntialias = true, Color = new SKColor(55, 65, 81), TextSize = 13, FakeBoldText = true, Typeface = FONT_CJK };
            using var durtx = new SKPaint { IsAntialias = true, Color = new SKColor(75, 85, 99), TextSize = 13, Typeface = FONT_CJK };

            float labelStartX = legendX + dot + 10f;
            float gap = 12f;

            float maxLabelW = 0f;
            for (int i = 0; i < _pie.Count; i++)
            {
                var (name, sec) = _pie[i];
                float pct = (float)(sec / _pieTotal * 100.0f);
                string part = $"{name}  {pct:0.#}%";
                var b = new SKRect(); label.MeasureText(part, ref b);
                if (b.Width > maxLabelW) maxLabelW = b.Width;
            }
            float durX = labelStartX + maxLabelW + gap;

            for (int i = 0; i < _pie.Count; i++)
            {
                var (name, sec) = _pie[i];
                float pct = (float)(sec / _pieTotal * 100.0f);
                string part = $"{name}  {pct:0.#}%";
                string dur = FormatDuration(sec);

                using var dp = new SKPaint { IsAntialias = true, Color = pal[i % pal.Length], Style = SKPaintStyle.Fill };
                c.DrawCircle(legendX, legendY + i * row - 3, dot / 2f, dp);

                c.DrawText(part, labelStartX, legendY + i * row, label);
                c.DrawText(dur, durX, legendY + i * row, durtx);
            }

            string total = L("总报警时长：") + FormatDuration(_pieTotal);
            using var totalPaint = new SKPaint { IsAntialias = true, Color = new SKColor(31, 41, 55), TextSize = 25, FakeBoldText = true, Typeface = FONT_CJK };
            float totalY = legendY + _pie.Count * row + 16f;
            c.DrawText(total, legendX, totalY, totalPaint);
        }

        // ===== 工具 =====


        private static string[] SplitAny(string s)
        {
            if (string.IsNullOrEmpty(s)) return Array.Empty<string>();

            // 先优先尝试 CSV 逗号（支持引号）
            if (s.IndexOf(',') >= 0)
            {
                var list = new List<string>();
                var sb = new StringBuilder();
                bool inQuote = false;

                for (int i = 0; i < s.Length; i++)
                {
                    char ch = s[i];
                    if (ch == '"')
                    {
                        // 处理转义 "" -> 一个 "
                        if (inQuote && i + 1 < s.Length && s[i + 1] == '"') { sb.Append('"'); i++; }
                        else { inQuote = !inQuote; }
                    }
                    else if (!inQuote && ch == ',')
                    {
                        list.Add(sb.ToString());
                        sb.Clear();
                    }
                    else
                    {
                        sb.Append(ch);
                    }
                }
                list.Add(sb.ToString());
                return list.ToArray();
            }

            // 其他分隔符兜底
            if (s.Contains("\t")) return s.Split('\t');
            if (s.Contains(";")) return s.Split(';');
            if (s.Contains("|")) return s.Split('|');
            return new[] { s };
        }

        private static readonly char[] QuoteChars = new[] { '"', '\'', '“', '”', '‘', '’' };
        private static string NormalizeHeader(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            var t = s.Trim();
            t = t.TrimStart('\uFEFF', '\u200B', '\u200E', '\u200F'); // BOM/零宽
            t = t.Replace('\u00A0', ' ');                            // NBSP -> space
            t = t.Normalize(NormalizationForm.FormKC);               // 全角->半角
            t = t.Trim(QuoteChars);                                  // 去首尾各种引号
            return t.Trim();
        }

        // 统一：去 BOM/零宽/中文引号/英文引号，并解转义 CSV 的 ""
        private static string NormalizeCell(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            var t = s.Trim();

            // 清理隐藏字符
            t = t.TrimStart('\uFEFF', '\u200B', '\u200E', '\u200F'); // BOM/零宽
            t = t.Replace('\u00A0', ' ');                             // NBSP -> space

            // 先去掉成对的中文引号等
            t = t.Trim(QuoteChars);

            // 再处理标准 CSV 的双引号包裹："...."
            if (t.Length >= 2 && t[0] == '"' && t[t.Length - 1] == '"')
            {
                t = t.Substring(1, t.Length - 2).Replace("\"\"", "\""); // "" -> "
            }

            return t.Trim();
        }


        private static int Find(string[] arr, params string[] keys)
        {
            var normKeys = keys.Select(k => NormalizeHeader(k).ToUpperInvariant()).ToArray();
            for (int i = 0; i < arr.Length; i++)
            {
                var t = NormalizeHeader(arr[i]).ToUpperInvariant();
                foreach (var k in normKeys)
                    if (t == k) return i;
            }
            return -1;
        }

        private static int ExtractHourLoose(string s)
        {
            s = NormalizeCell(s);
            if (string.IsNullOrWhiteSpace(s)) return -1;
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int h) && h >= 0 && h <= 23) return h;
            if (DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.None, out var dt)) return dt.Hour;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt)) return dt.Hour;
            var digits = new string(s.TakeWhile(char.IsDigit).ToArray());
            if (digits.Length > 0 && int.TryParse(digits, out h) && h >= 0 && h <= 23) return h;
            return -1;
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
        private static float SafeBaseline(SKPaint p, string text, float desired, float top, float bottom, float padTop = 10f, float padBottom = 4f)
        {
            var b = new SKRect(); p.MeasureText(text, ref b);
            float minBase = top + padTop - b.Top;
            float maxBase = bottom - padBottom;
            if (desired < minBase) return minBase;
            if (desired > maxBase) return maxBase;
            return desired;
        }
        private static string EllipsisFit(string s, SKPaint paint, float maxW)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var b = new SKRect(); paint.MeasureText(s, ref b);
            if (b.Width <= maxW) return s;
            const string ell = "…"; paint.MeasureText(ell, ref b);
            int left = 0, right = s.Length; string best = ell;
            while (left <= right)
            {
                int mid = (left + right) / 2;
                string sub = s.Substring(0, mid) + ell;
                paint.MeasureText(sub, ref b);
                if (b.Width <= maxW) { best = sub; left = mid + 1; } else right = mid - 1;
            }
            return best;
        }
        private static string FormatDuration(double seconds)
        {
            if (seconds <= 0) return "0s";
            var ts = TimeSpan.FromSeconds(seconds);
            if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h{ts.Minutes:D2}m";
            if (ts.TotalMinutes >= 1) return $"{(int)ts.TotalMinutes}m{ts.Seconds:D2}s";
            return $"{ts.Seconds}s";
        }
        private static string FormatSecondsSmart(double seconds)
        {
            if (seconds <= 0) return "0";
            if (seconds < 60) return seconds.ToString("N0", CultureInfo.CurrentCulture) + " s";
            double minutes = seconds / 60.0;
            if (minutes < 60) return minutes.ToString("N0", CultureInfo.CurrentCulture) + " m";
            double hours = minutes / 60.0;
            return hours.ToString("0.0", CultureInfo.InvariantCulture) + " h";
        }

        // …… 你的类里（AlarmView）新增：
        // 明细行模型
        private sealed class DetailRow
        {
            public string Time { get; set; } = "";
            public int TimeSecOfDay { get; set; }    // ← 新增：用于时间列排序
            public string Code { get; set; } = "";
            public string Category { get; set; } = "";
            public string Duration { get; set; } = "";
            public double DurationSec { get; set; }     // ← 新增：用于时长列排序
            public string Message { get; set; } = "";
        }


        // TOP 点击命中表
        private readonly List<(Rect Hit, TopCat Item)> _topHits = new();
        private string? _selectedTopCategory = null;

        // 明细数据源（绑定到 DataGrid）
        private readonly ObservableCollection<DetailRow> _detailRows = new();

        private void SkTop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var p = e.GetPosition(SkTop);
            foreach (var (hit, item) in _topHits)
            {
                if (hit.Contains(p))
                {
                    _selectedTopCategory = item.Name;
                    LoadDetailForCategory(_day, _selectedTopCategory);
                    e.Handled = true;
                    return;
                }
            }
        }
        private void LoadDetailForCategory(DateTime day, string category)
        {
            _detailRows.Clear();

            var rows = EnumerateAlarmsFromCsv(day)
                        .Where(r => string.Equals((r.Category ?? "").Trim(),
                                                  (category ?? "").Trim(),
                                                  StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(r => r.Start)
                        .ToList();

            if (FindName("DetailTitle") is TextBlock ttl)
                ttl.Text = string.Format(L("明细 · {0}（{1:yyyy-MM-dd}）"), category, day);

            if (rows.Count == 0)
            {
                if (FindName("DetailGrid") is FrameworkElement g) g.Visibility = Visibility.Collapsed;
                if (FindName("DetailEmpty") is FrameworkElement e) e.Visibility = Visibility.Visible;
                return;
            }

            foreach (var r in rows)
            {
                _detailRows.Add(new DetailRow
                {
                    Time = r.Start.ToString("HH:mm:ss"),
                    TimeSecOfDay = (int)r.Start.TimeOfDay.TotalSeconds, // ← 新增
                    Code = r.Code,
                    Category = r.Category,
                    Duration = FormatDuration(r.Seconds),
                    DurationSec = r.Seconds,                             // ← 新增
                    Message = r.Content
                });

            }

            if (FindName("DetailGrid") is FrameworkElement grid) grid.Visibility = Visibility.Visible;
            if (FindName("DetailEmpty") is FrameworkElement emp) emp.Visibility = Visibility.Collapsed;

            // 点击 TOP 后，自动滚到“明细”卡片处
            try
            {
                if (FindName("DetailTitle") is FrameworkElement anchor)
                    anchor.BringIntoView();
            }
            catch { /* 忽略滚动异常 */ }

        }
        private void DetailCard_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            // 优先让内层 DataGrid 自己滚（若它确实能滚）
            if (DetailGrid != null)
            {
                var inner = FindDescendant<ScrollViewer>(DetailGrid);
                if (inner != null && inner.ScrollableHeight > 0)
                {
                    if (e.Delta < 0) inner.LineDown();
                    else inner.LineUp();
                    e.Handled = true;   // 内层已消费
                    return;
                }
            }

            // 内层不能滚时，把滚轮传给外层页面滚动
            if (RootScroll != null)
            {
                RootScroll.ScrollToVerticalOffset(RootScroll.VerticalOffset - e.Delta);
                e.Handled = true;
            }
        }

        // 工具：找子层 ScrollViewer
        private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            for (int i = 0, n = VisualTreeHelper.GetChildrenCount(root); i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T t) return t;
                var deeper = FindDescendant<T>(child);
                if (deeper != null) return deeper;
            }
            return null;
        }
        // 每小时累计的报警“时长秒数”（供连续窗口分析使用）
        private readonly double[] _hourSeconds = new double[24];
        // 放在 AlarmView 类里（和 StartAnimation 同级）
        private void RedrawNow()
        {
            _isAnimating = false;
            CompositionTarget.Rendering -= OnRendering; // 断开逐帧
            _anim = 1.0;

            // 直接触发一次重绘
            SkAlarmWeek?.InvalidateVisual();
            SkHour?.InvalidateVisual();
            SkTop?.InvalidateVisual();
            SkPie?.InvalidateVisual();
        }

    }
}

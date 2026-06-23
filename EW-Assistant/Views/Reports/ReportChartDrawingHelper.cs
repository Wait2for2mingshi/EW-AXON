using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EW_Assistant.Services;
using EW_Assistant.Services.Reports;
using SkiaSharp;

namespace EW_Assistant.Views.Reports
{
    internal sealed class ReportChartHitInfo
    {
        public int PointIndex { get; set; }
        public int? SeriesIndex { get; set; }
        public ReportChartDataPoint DataPoint { get; set; }
    }

    internal static class ReportChartDrawingHelper
    {
        private static readonly SKTypeface UiTypeface = CreateUiTypeface();

        private static string L(string chineseText)
        {
            return UiLanguageService.CurrentText(chineseText);
        }

        public static void DrawChart(ReportChartDefinition chart, SKCanvas canvas, SKImageInfo info)
        {
            DrawChart(chart, canvas, info, null, null);
        }

        public static void DrawChart(ReportChartDefinition chart, SKCanvas canvas, SKImageInfo info, ReportChartHitInfo hoverHit, ReportChartHitInfo selectedHit)
        {
            if (canvas == null)
            {
                return;
            }

            canvas.Clear(SKColor.Parse("#FFFFFF"));

            if (chart == null)
            {
                DrawEmptyState(canvas, info, "暂无图表");
                return;
            }

            if (!chart.HasData)
            {
                DrawEmptyState(canvas, info, string.IsNullOrWhiteSpace(chart.EmptyHint) ? "暂无数据" : chart.EmptyHint);
                return;
            }

            switch (chart.Kind)
            {
                case ReportChartKind.StackedBar:
                    DrawStackedBarChart(canvas, info, chart, hoverHit?.PointIndex ?? -1, selectedHit?.PointIndex ?? -1);
                    break;
                case ReportChartKind.Bar:
                    DrawBarChart(canvas, info, chart, hoverHit?.PointIndex ?? -1, selectedHit?.PointIndex ?? -1);
                    break;
                case ReportChartKind.Line:
                    DrawLineChart(canvas, info, chart, hoverHit?.PointIndex ?? -1, selectedHit?.PointIndex ?? -1);
                    break;
                case ReportChartKind.Donut:
                    DrawDonutChart(canvas, info, chart, hoverHit?.PointIndex ?? -1, selectedHit?.PointIndex ?? -1);
                    break;
                default:
                    DrawEmptyState(canvas, info, "暂不支持该图表类型");
                    break;
            }
        }

        public static ReportChartHitInfo HitTest(ReportChartDefinition chart, SKImageInfo info, SKPoint point)
        {
            if (chart == null || !chart.HasData || !chart.IsInteractive)
            {
                return null;
            }

            switch (chart.Kind)
            {
                case ReportChartKind.StackedBar:
                    return HitTestStackedBar(chart, info, point);
                case ReportChartKind.Bar:
                    return HitTestBar(chart, info, point);
                case ReportChartKind.Line:
                    return HitTestLine(chart, info, point);
                case ReportChartKind.Donut:
                    return HitTestDonut(chart, info, point);
                default:
                    return null;
            }
        }

        private static void DrawStackedBarChart(SKCanvas canvas, SKImageInfo info, ReportChartDefinition chart, int hoverIndex, int selectedIndex)
        {
            var rect = CreatePlotRect(info);
            var count = GetPointCount(chart);
            if (count <= 0)
            {
                DrawEmptyState(canvas, info, chart.EmptyHint);
                return;
            }

            var maxValue = 0d;
            for (int i = 0; i < count; i++)
            {
                var sum = chart.Series.Sum(s => GetSeriesValue(s, i));
                if (sum > maxValue)
                {
                    maxValue = sum;
                }
            }

            if (maxValue <= 0)
            {
                DrawEmptyState(canvas, info, chart.EmptyHint);
                return;
            }

            var niceMax = GetNiceMax(maxValue, chart.ValueFormat);
            DrawGridAndAxes(canvas, rect, niceMax, chart.ValueFormat);

            using var barPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
            using var hoverPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2f, Color = SKColor.Parse("#60A5FA") };
            using var selectedPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3f, Color = SKColor.Parse("#1D4ED8") };
            var slotWidth = rect.Width / Math.Max(1, count);
            var barWidth = Math.Min(32f, slotWidth * 0.58f);

            for (int i = 0; i < count; i++)
            {
                var x = rect.Left + slotWidth * i + (slotWidth - barWidth) / 2f;
                var bottom = rect.Bottom;

                foreach (var series in chart.Series)
                {
                    var value = GetSeriesValue(series, i);
                    if (value <= 0)
                    {
                        continue;
                    }

                    var height = (float)(value / niceMax) * rect.Height;
                    var top = bottom - height;
                    barPaint.Color = ParseColor(series.ColorHex, "#94A3B8");
                    var round = new SKRoundRect(new SKRect(x, top, x + barWidth, bottom), 6f, 6f);
                    canvas.DrawRoundRect(round, barPaint);
                    bottom = top;
                }

                if (i == hoverIndex || i == selectedIndex)
                {
                    var total = chart.Series.Sum(series => GetSeriesValue(series, i));
                    if (total > 0)
                    {
                        var top = rect.Bottom - (float)(total / niceMax) * rect.Height;
                        var outlineRect = new SKRoundRect(new SKRect(x - 2f, top - 2f, x + barWidth + 2f, rect.Bottom + 1f), 8f, 8f);
                        canvas.DrawRoundRect(outlineRect, i == selectedIndex ? selectedPaint : hoverPaint);
                    }
                }
            }

            DrawXAxisLabels(canvas, rect, chart.Labels, count, true);
        }

        private static ReportChartHitInfo HitTestStackedBar(ReportChartDefinition chart, SKImageInfo info, SKPoint point)
        {
            var rect = CreatePlotRect(info);
            var count = GetPointCount(chart);
            if (count <= 0 || point.X < rect.Left || point.X > rect.Right || point.Y < rect.Top || point.Y > rect.Bottom)
            {
                return null;
            }

            var slotWidth = rect.Width / Math.Max(1, count);
            var index = Math.Min(count - 1, Math.Max(0, (int)((point.X - rect.Left) / slotWidth)));
            var value = chart.Series.Sum(series => GetSeriesValue(series, index));
            if (value <= 0)
            {
                return null;
            }

            var maxValue = 0d;
            for (int i = 0; i < count; i++)
            {
                maxValue = Math.Max(maxValue, chart.Series.Sum(series => GetSeriesValue(series, i)));
            }

            var niceMax = GetNiceMax(maxValue, chart.ValueFormat);
            var top = rect.Bottom - (float)(value / niceMax) * rect.Height;
            if (point.Y < top)
            {
                return null;
            }

            return CreateHitInfo(chart, index, null);
        }

        private static void DrawBarChart(SKCanvas canvas, SKImageInfo info, ReportChartDefinition chart, int hoverIndex, int selectedIndex)
        {
            var rect = CreatePlotRect(info);
            var count = GetPointCount(chart);
            if (count <= 0)
            {
                DrawEmptyState(canvas, info, chart.EmptyHint);
                return;
            }

            var series = chart.Series.FirstOrDefault();
            var maxValue = series != null ? series.Values.DefaultIfEmpty(0d).Max() : 0d;
            if (maxValue <= 0)
            {
                DrawEmptyState(canvas, info, chart.EmptyHint);
                return;
            }

            var niceMax = GetNiceMax(maxValue, chart.ValueFormat);
            DrawGridAndAxes(canvas, rect, niceMax, chart.ValueFormat);

            using var barPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = ParseColor(series?.ColorHex, "#2563EB") };
            using var hoverPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2f, Color = SKColor.Parse("#60A5FA") };
            using var selectedPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3f, Color = SKColor.Parse("#1D4ED8") };
            var slotWidth = rect.Width / Math.Max(1, count);
            var barWidth = Math.Min(30f, slotWidth * 0.58f);

            for (int i = 0; i < count; i++)
            {
                var value = GetSeriesValue(series, i);
                if (value <= 0)
                {
                    continue;
                }

                var height = (float)(value / niceMax) * rect.Height;
                var x = rect.Left + slotWidth * i + (slotWidth - barWidth) / 2f;
                var top = rect.Bottom - height;
                var round = new SKRoundRect(new SKRect(x, top, x + barWidth, rect.Bottom), 6f, 6f);
                canvas.DrawRoundRect(round, barPaint);

                if (i == hoverIndex || i == selectedIndex)
                {
                    var outline = new SKRoundRect(new SKRect(x - 2f, top - 2f, x + barWidth + 2f, rect.Bottom + 1f), 8f, 8f);
                    canvas.DrawRoundRect(outline, i == selectedIndex ? selectedPaint : hoverPaint);
                }
            }

            DrawXAxisLabels(canvas, rect, chart.Labels, count, true);
        }

        private static ReportChartHitInfo HitTestBar(ReportChartDefinition chart, SKImageInfo info, SKPoint point)
        {
            var rect = CreatePlotRect(info);
            var count = GetPointCount(chart);
            if (count <= 0 || point.X < rect.Left || point.X > rect.Right || point.Y < rect.Top || point.Y > rect.Bottom)
            {
                return null;
            }

            var series = chart.Series.FirstOrDefault();
            var slotWidth = rect.Width / Math.Max(1, count);
            var index = Math.Min(count - 1, Math.Max(0, (int)((point.X - rect.Left) / slotWidth)));
            var value = GetSeriesValue(series, index);
            if (value <= 0)
            {
                return null;
            }

            var maxValue = series != null ? series.Values.DefaultIfEmpty(0d).Max() : 0d;
            var niceMax = GetNiceMax(maxValue, chart.ValueFormat);
            var top = rect.Bottom - (float)(value / niceMax) * rect.Height;
            if (point.Y < top)
            {
                return null;
            }

            return CreateHitInfo(chart, index, 0);
        }

        private static void DrawLineChart(SKCanvas canvas, SKImageInfo info, ReportChartDefinition chart, int hoverIndex, int selectedIndex)
        {
            var rect = CreatePlotRect(info);
            var count = GetPointCount(chart);
            if (count <= 0)
            {
                DrawEmptyState(canvas, info, chart.EmptyHint);
                return;
            }

            var series = chart.Series.FirstOrDefault();
            var maxValue = series != null ? series.Values.DefaultIfEmpty(0d).Max() : 0d;
            if (chart.ValueFormat == ReportChartValueFormat.Percent)
            {
                maxValue = Math.Max(100d, maxValue);
            }

            if (maxValue <= 0)
            {
                DrawEmptyState(canvas, info, chart.EmptyHint);
                return;
            }

            var niceMax = GetNiceMax(maxValue, chart.ValueFormat);
            DrawGridAndAxes(canvas, rect, niceMax, chart.ValueFormat);

            using var linePaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 3f,
                Color = ParseColor(series?.ColorHex, "#16A34A"),
                StrokeCap = SKStrokeCap.Round,
                StrokeJoin = SKStrokeJoin.Round
            };
            using var fillPaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
                Color = linePaint.Color.WithAlpha(26)
            };
            using var pointPaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
                Color = linePaint.Color
            };
            using var hoverRingPaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 2f,
                Color = SKColor.Parse("#60A5FA")
            };
            using var selectedRingPaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 3f,
                Color = SKColor.Parse("#1D4ED8")
            };
            using var guidePaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.5f,
                Color = SKColor.Parse("#93C5FD")
            };

            var slotWidth = count > 1 ? rect.Width / (count - 1) : 0f;
            using var path = new SKPath();
            using var fillPath = new SKPath();

            for (int i = 0; i < count; i++)
            {
                var value = GetSeriesValue(series, i);
                var x = count == 1 ? rect.MidX : rect.Left + slotWidth * i;
                var y = rect.Bottom - (float)(value / niceMax) * rect.Height;

                if (i == 0)
                {
                    path.MoveTo(x, y);
                    fillPath.MoveTo(x, rect.Bottom);
                    fillPath.LineTo(x, y);
                }
                else
                {
                    path.LineTo(x, y);
                    fillPath.LineTo(x, y);
                }
            }

            if (count > 0)
            {
                var lastX = count == 1 ? rect.MidX : rect.Left + slotWidth * (count - 1);
                fillPath.LineTo(lastX, rect.Bottom);
                fillPath.Close();
            }

            canvas.DrawPath(fillPath, fillPaint);
            canvas.DrawPath(path, linePaint);

            for (int i = 0; i < count; i++)
            {
                var value = GetSeriesValue(series, i);
                var x = count == 1 ? rect.MidX : rect.Left + slotWidth * i;
                var y = rect.Bottom - (float)(value / niceMax) * rect.Height;
                canvas.DrawCircle(x, y, 4.5f, pointPaint);

                if (i == hoverIndex || i == selectedIndex)
                {
                    canvas.DrawLine(x, rect.Top, x, rect.Bottom, guidePaint);
                    canvas.DrawCircle(x, y, i == selectedIndex ? 9f : 8f, i == selectedIndex ? selectedRingPaint : hoverRingPaint);
                }
            }

            DrawXAxisLabels(canvas, rect, chart.Labels, count, false);
        }

        private static ReportChartHitInfo HitTestLine(ReportChartDefinition chart, SKImageInfo info, SKPoint point)
        {
            var rect = CreatePlotRect(info);
            var count = GetPointCount(chart);
            if (count <= 0 || point.X < rect.Left - 12f || point.X > rect.Right + 12f || point.Y < rect.Top - 12f || point.Y > rect.Bottom + 12f)
            {
                return null;
            }

            var series = chart.Series.FirstOrDefault();
            var maxValue = series != null ? series.Values.DefaultIfEmpty(0d).Max() : 0d;
            if (chart.ValueFormat == ReportChartValueFormat.Percent)
            {
                maxValue = Math.Max(100d, maxValue);
            }

            if (maxValue <= 0)
            {
                return null;
            }

            var niceMax = GetNiceMax(maxValue, chart.ValueFormat);
            var slotWidth = count > 1 ? rect.Width / (count - 1) : 0f;
            const float radius = 12f;
            var radiusSquared = radius * radius;
            var bestIndex = -1;
            var bestDistance = float.MaxValue;

            for (int i = 0; i < count; i++)
            {
                var value = GetSeriesValue(series, i);
                var x = count == 1 ? rect.MidX : rect.Left + slotWidth * i;
                var y = rect.Bottom - (float)(value / niceMax) * rect.Height;
                var dx = x - point.X;
                var dy = y - point.Y;
                var distance = dx * dx + dy * dy;
                if (distance <= radiusSquared && distance < bestDistance)
                {
                    bestDistance = distance;
                    bestIndex = i;
                }
            }

            return bestIndex >= 0 ? CreateHitInfo(chart, bestIndex, 0) : null;
        }

        private static void DrawDonutChart(SKCanvas canvas, SKImageInfo info, ReportChartDefinition chart, int hoverIndex, int selectedIndex)
        {
            var series = chart.Series.Where(s => GetSeriesValue(s, 0) > 0).ToList();
            if (series.Count == 0)
            {
                DrawEmptyState(canvas, info, chart.EmptyHint);
                return;
            }

            var total = series.Sum(s => GetSeriesValue(s, 0));
            if (total <= 0)
            {
                DrawEmptyState(canvas, info, chart.EmptyHint);
                return;
            }

            if (IsCompactDonutLayout(info))
            {
                DrawCompactDonutChart(canvas, info, chart, series, total, hoverIndex, selectedIndex);
                return;
            }

            var rect = new SKRect(0, 0, info.Width, info.Height);
            var centerX = rect.MidX;
            var centerY = rect.MidY;
            var radius = Math.Min(rect.Width, rect.Height) * 0.28f;
            var strokeWidth = radius * 0.36f;
            var donutRect = new SKRect(centerX - radius, centerY - radius, centerX + radius, centerY + radius);

            using var paint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = strokeWidth,
                StrokeCap = SKStrokeCap.Butt
            };

            float startAngle = -90f;
            foreach (var item in series)
            {
                var value = GetSeriesValue(item, 0);
                var sweep = (float)(value / total) * 360f;
                if (sweep >= 360f)
                {
                    sweep = 359.99f;
                }

                var originalIndex = ResolveOriginalSeriesIndex(chart, item);
                var isSelected = originalIndex == selectedIndex;
                var isHover = originalIndex == hoverIndex && !isSelected;
                paint.Color = ParseColor(item.ColorHex, "#94A3B8");
                paint.StrokeWidth = strokeWidth + (isSelected ? 10f : isHover ? 6f : 0f);
                canvas.DrawArc(donutRect, startAngle, sweep, false, paint);
                startAngle += sweep;
            }

            using var centerValuePaint = new SKPaint
            {
                IsAntialias = true,
                Color = SKColor.Parse("#111827"),
                TextSize = 28f,
                FakeBoldText = true,
                TextAlign = SKTextAlign.Center,
                Typeface = UiTypeface
            };
            using var centerTextPaint = new SKPaint
            {
                IsAntialias = true,
                Color = SKColor.Parse("#64748B"),
                TextSize = 14f,
                TextAlign = SKTextAlign.Center,
                Typeface = UiTypeface
            };

            var centerText = string.IsNullOrWhiteSpace(chart.CenterText) ? FormatValue(total, chart.ValueFormat) : chart.CenterText;
            FitCenteredText(centerValuePaint, centerText, radius * 1.5f);
            canvas.DrawText(centerText, centerX, centerY + 4f, centerValuePaint);

            if (!string.IsNullOrWhiteSpace(chart.CenterSubtext))
            {
                FitCenteredText(centerTextPaint, chart.CenterSubtext, radius * 1.7f);
                canvas.DrawText(chart.CenterSubtext, centerX, centerY + 30f, centerTextPaint);
            }
        }

        private static ReportChartHitInfo HitTestDonut(ReportChartDefinition chart, SKImageInfo info, SKPoint point)
        {
            var series = chart.Series.Where(s => GetSeriesValue(s, 0) > 0).ToList();
            if (series.Count == 0)
            {
                return null;
            }

            var total = series.Sum(s => GetSeriesValue(s, 0));
            if (total <= 0)
            {
                return null;
            }

            if (IsCompactDonutLayout(info))
            {
                return HitTestCompactDonut(chart, info, point, series, total);
            }

            var rect = new SKRect(0, 0, info.Width, info.Height);
            var radius = Math.Min(rect.Width, rect.Height) * 0.28f;
            var strokeWidth = radius * 0.36f;
            var center = new SKPoint(rect.MidX, rect.MidY);
            var dx = point.X - center.X;
            var dy = point.Y - center.Y;
            var distance = (float)Math.Sqrt(dx * dx + dy * dy);
            var innerRadius = radius - strokeWidth / 2f - 8f;
            var outerRadius = radius + strokeWidth / 2f + 8f;
            if (distance < innerRadius || distance > outerRadius)
            {
                return null;
            }

            var angle = (float)(Math.Atan2(dy, dx) * 180d / Math.PI);
            angle = (angle + 450f) % 360f;

            float startAngle = 0f;
            for (int i = 0; i < series.Count; i++)
            {
                var item = series[i];
                var value = GetSeriesValue(item, 0);
                var sweep = (float)(value / total) * 360f;
                if (sweep >= 360f)
                {
                    sweep = 359.99f;
                }

                if (angle >= startAngle && angle <= startAngle + sweep)
                {
                    var seriesIndex = ResolveOriginalSeriesIndex(chart, item);
                    var pointIndex = seriesIndex >= 0 ? seriesIndex : i;
                    return CreateHitInfo(chart, pointIndex, seriesIndex >= 0 ? seriesIndex : i);
                }

                startAngle += sweep;
            }

            return null;
        }

        private static void DrawCompactDonutChart(SKCanvas canvas, SKImageInfo info, ReportChartDefinition chart, IList<ReportChartSeriesDefinition> series, double total, int hoverIndex, int selectedIndex)
        {
            var centerText = string.IsNullOrWhiteSpace(chart.CenterText) ? FormatValue(total, chart.ValueFormat) : chart.CenterText;
            var hasSubtext = !string.IsNullOrWhiteSpace(chart.CenterSubtext);
            var barRect = GetCompactDonutBarRect(info, hasSubtext);

            using var valuePaint = new SKPaint
            {
                IsAntialias = true,
                Color = SKColor.Parse("#111827"),
                TextSize = 24f,
                FakeBoldText = true,
                TextAlign = SKTextAlign.Center,
                Typeface = UiTypeface
            };
            using var subtextPaint = new SKPaint
            {
                IsAntialias = true,
                Color = SKColor.Parse("#64748B"),
                TextSize = 12f,
                TextAlign = SKTextAlign.Center,
                Typeface = UiTypeface
            };
            using var trackPaint = new SKPaint
            {
                IsAntialias = true,
                Color = SKColor.Parse("#E5E7EB"),
                Style = SKPaintStyle.Fill
            };
            using var dividerPaint = new SKPaint
            {
                IsAntialias = true,
                Color = SKColor.Parse("#FFFFFF"),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1f
            };
            using var hoverStrokePaint = new SKPaint
            {
                IsAntialias = true,
                Color = SKColor.Parse("#60A5FA"),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 2f
            };
            using var selectedStrokePaint = new SKPaint
            {
                IsAntialias = true,
                Color = SKColor.Parse("#1D4ED8"),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 3f
            };

            FitCenteredText(valuePaint, centerText, info.Width - 16f);
            canvas.DrawText(centerText, info.Width / 2f, hasSubtext ? 24f : 30f, valuePaint);

            if (hasSubtext)
            {
                FitCenteredText(subtextPaint, chart.CenterSubtext, info.Width - 16f);
                canvas.DrawText(chart.CenterSubtext, info.Width / 2f, 43f, subtextPaint);
            }

            var round = Math.Min(barRect.Height / 2f, 8f);
            var trackRoundRect = new SKRoundRect(barRect, round, round);
            canvas.DrawRoundRect(trackRoundRect, trackPaint);

            canvas.Save();
            canvas.ClipRoundRect(trackRoundRect, antialias: true);

            float segmentLeft = barRect.Left;
            for (int i = 0; i < series.Count; i++)
            {
                var item = series[i];
                var value = GetSeriesValue(item, 0);
                var ratio = total > 0 ? (float)(value / total) : 0f;
                var segmentRight = i == series.Count - 1
                    ? barRect.Right
                    : segmentLeft + barRect.Width * ratio;
                if (segmentRight <= segmentLeft)
                {
                    continue;
                }

                using var fillPaint = new SKPaint
                {
                    IsAntialias = true,
                    Style = SKPaintStyle.Fill,
                    Color = ParseColor(item.ColorHex, "#94A3B8")
                };

                var segmentRect = new SKRect(segmentLeft, barRect.Top, segmentRight, barRect.Bottom);
                canvas.DrawRect(segmentRect, fillPaint);

                var originalIndex = ResolveOriginalSeriesIndex(chart, item);
                var isSelected = originalIndex == selectedIndex;
                var isHover = originalIndex == hoverIndex && !isSelected;
                if (isSelected || isHover)
                {
                    canvas.DrawRect(segmentRect, isSelected ? selectedStrokePaint : hoverStrokePaint);
                }

                if (i < series.Count - 1)
                {
                    canvas.DrawLine(segmentRight, barRect.Top + 1f, segmentRight, barRect.Bottom - 1f, dividerPaint);
                }

                segmentLeft = segmentRight;
            }

            canvas.Restore();
        }

        private static ReportChartHitInfo HitTestCompactDonut(ReportChartDefinition chart, SKImageInfo info, SKPoint point, IList<ReportChartSeriesDefinition> series, double total)
        {
            var barRect = GetCompactDonutBarRect(info, !string.IsNullOrWhiteSpace(chart.CenterSubtext));
            if (point.X < barRect.Left || point.X > barRect.Right || point.Y < barRect.Top - 6f || point.Y > barRect.Bottom + 6f)
            {
                return null;
            }

            float segmentLeft = barRect.Left;
            for (int i = 0; i < series.Count; i++)
            {
                var item = series[i];
                var value = GetSeriesValue(item, 0);
                var ratio = total > 0 ? (float)(value / total) : 0f;
                var segmentRight = i == series.Count - 1
                    ? barRect.Right
                    : segmentLeft + barRect.Width * ratio;
                if (segmentRight > segmentLeft && point.X >= segmentLeft && point.X <= segmentRight)
                {
                    var seriesIndex = ResolveOriginalSeriesIndex(chart, item);
                    var pointIndex = seriesIndex >= 0 ? seriesIndex : i;
                    return CreateHitInfo(chart, pointIndex, seriesIndex >= 0 ? seriesIndex : i);
                }

                segmentLeft = segmentRight;
            }

            return null;
        }

        private static void DrawGridAndAxes(SKCanvas canvas, SKRect rect, double maxValue, ReportChartValueFormat valueFormat)
        {
            using var gridPaint = new SKPaint
            {
                IsAntialias = true,
                Color = SKColor.Parse("#E5E7EB"),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1f
            };
            using var axisPaint = new SKPaint
            {
                IsAntialias = true,
                Color = SKColor.Parse("#CBD5E1"),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.2f
            };
            using var labelPaint = new SKPaint
            {
                IsAntialias = true,
                Color = SKColor.Parse("#64748B"),
                TextSize = 12f,
                TextAlign = SKTextAlign.Right,
                Typeface = UiTypeface
            };

            const int steps = 4;
            for (int i = 0; i <= steps; i++)
            {
                var y = rect.Bottom - rect.Height * i / steps;
                canvas.DrawLine(rect.Left, y, rect.Right, y, i == 0 ? axisPaint : gridPaint);
                var value = maxValue * i / steps;
                canvas.DrawText(FormatValue(value, valueFormat), rect.Left - 8f, y + 4f, labelPaint);
            }

            canvas.DrawLine(rect.Left, rect.Top, rect.Left, rect.Bottom, axisPaint);
            canvas.DrawLine(rect.Left, rect.Bottom, rect.Right, rect.Bottom, axisPaint);
        }

        private static void DrawXAxisLabels(SKCanvas canvas, SKRect rect, IList<string> labels, int count, bool useSlotCenters)
        {
            if (labels == null || labels.Count == 0 || count <= 0)
            {
                return;
            }

            using var labelPaint = new SKPaint
            {
                IsAntialias = true,
                Color = SKColor.Parse("#64748B"),
                TextSize = 12f,
                TextAlign = SKTextAlign.Center,
                Typeface = UiTypeface
            };

            var maxLabels = Math.Max(4, (int)(rect.Width / 58f));
            var step = Math.Max(1, (int)Math.Ceiling(count / (double)maxLabels));

            if (count == 1)
            {
                canvas.DrawText(labels[0], rect.MidX, rect.Bottom + 20f, labelPaint);
                return;
            }

            var slotWidth = useSlotCenters
                ? rect.Width / Math.Max(1, count)
                : rect.Width / Math.Max(1, count - 1);
            for (int i = 0; i < count; i++)
            {
                if (i % step != 0 && i != count - 1)
                {
                    continue;
                }

                var x = useSlotCenters
                    ? rect.Left + slotWidth * i + slotWidth / 2f
                    : rect.Left + slotWidth * i;
                var text = i < labels.Count ? labels[i] : string.Empty;
                canvas.DrawText(text, x, rect.Bottom + 20f, labelPaint);
            }
        }

        private static void DrawEmptyState(SKCanvas canvas, SKImageInfo info, string message)
        {
            using var titlePaint = new SKPaint
            {
                IsAntialias = true,
                Color = SKColor.Parse("#94A3B8"),
                TextSize = 16f,
                TextAlign = SKTextAlign.Center,
                Typeface = UiTypeface
            };
            using var boxPaint = new SKPaint
            {
                IsAntialias = true,
                Color = SKColor.Parse("#F8FAFC"),
                Style = SKPaintStyle.Fill
            };
            using var borderPaint = new SKPaint
            {
                IsAntialias = true,
                Color = SKColor.Parse("#E5E7EB"),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1f
            };

            var rect = new SKRect(24f, 18f, info.Width - 24f, info.Height - 18f);
            var round = new SKRoundRect(rect, 16f, 16f);
            canvas.DrawRoundRect(round, boxPaint);
            canvas.DrawRoundRect(round, borderPaint);
            var displayMessage = string.IsNullOrWhiteSpace(message) ? L("暂无图表数据") : L(message);
            canvas.DrawText(displayMessage, rect.MidX, rect.MidY, titlePaint);
        }

        private static SKRect CreatePlotRect(SKImageInfo info)
        {
            return new SKRect(56f, 12f, info.Width - 18f, info.Height - 36f);
        }

        private static SKTypeface CreateUiTypeface()
        {
            return UiFontService.CreateSkiaTypeface("报表图表123");
        }

        private static bool IsCompactDonutLayout(SKImageInfo info)
        {
            return info.Width <= 240 || info.Height <= 160;
        }

        private static SKRect GetCompactDonutBarRect(SKImageInfo info, bool hasSubtext)
        {
            var bottom = info.Height - 8f;
            var top = hasSubtext ? 54f : 46f;
            if (bottom - top < 10f)
            {
                top = Math.Max(18f, bottom - 12f);
            }

            return new SKRect(8f, top, info.Width - 8f, bottom);
        }

        private static int GetPointCount(ReportChartDefinition chart)
        {
            if (chart == null)
            {
                return 0;
            }

            var labelCount = chart.Labels != null ? chart.Labels.Count : 0;
            var seriesCount = chart.Series != null && chart.Series.Count > 0
                ? chart.Series.Max(s => s != null && s.Values != null ? s.Values.Count : 0)
                : 0;

            return Math.Max(labelCount, seriesCount);
        }

        private static ReportChartHitInfo CreateHitInfo(ReportChartDefinition chart, int pointIndex, int? seriesIndex)
        {
            if (chart == null || chart.DataPoints == null || pointIndex < 0 || pointIndex >= chart.DataPoints.Count)
            {
                return null;
            }

            var dataPoint = chart.DataPoints[pointIndex];
            return dataPoint == null
                ? null
                : new ReportChartHitInfo
                {
                    PointIndex = pointIndex,
                    SeriesIndex = seriesIndex,
                    DataPoint = dataPoint
                };
        }

        private static double GetSeriesValue(ReportChartSeriesDefinition series, int index)
        {
            if (series == null || series.Values == null || index < 0 || index >= series.Values.Count)
            {
                return 0d;
            }

            return series.Values[index];
        }

        private static double GetNiceMax(double value, ReportChartValueFormat format)
        {
            if (format == ReportChartValueFormat.Percent)
            {
                return 100d;
            }

            if (value <= 0)
            {
                return 1d;
            }

            var exponent = Math.Pow(10, Math.Floor(Math.Log10(value)));
            var normalized = value / exponent;

            if (normalized <= 1d) return exponent;
            if (normalized <= 2d) return 2d * exponent;
            if (normalized <= 5d) return 5d * exponent;
            return 10d * exponent;
        }

        private static string FormatValue(double value, ReportChartValueFormat format)
        {
            switch (format)
            {
                case ReportChartValueFormat.Percent:
                    return value.ToString("0", CultureInfo.InvariantCulture) + "%";
                case ReportChartValueFormat.DurationMinutes:
                    if (value >= 60d)
                    {
                        return (value / 60d).ToString("0.#", CultureInfo.InvariantCulture) + "h";
                    }

                    return value.ToString("0", CultureInfo.InvariantCulture) + "m";
                default:
                    if (value >= 10000d)
                    {
                        return (value / 1000d).ToString("0.#", CultureInfo.InvariantCulture) + "k";
                    }

                    return value.ToString("0", CultureInfo.InvariantCulture);
            }
        }

        private static int ResolveOriginalSeriesIndex(ReportChartDefinition chart, ReportChartSeriesDefinition targetSeries)
        {
            if (chart?.Series == null || targetSeries == null)
            {
                return -1;
            }

            for (int i = 0; i < chart.Series.Count; i++)
            {
                if (ReferenceEquals(chart.Series[i], targetSeries))
                {
                    return i;
                }
            }

            return -1;
        }

        private static void FitCenteredText(SKPaint paint, string text, float maxWidth)
        {
            if (paint == null || string.IsNullOrWhiteSpace(text) || maxWidth <= 0f)
            {
                return;
            }

            while (paint.TextSize > 10f && paint.MeasureText(text) > maxWidth)
            {
                paint.TextSize -= 1f;
            }
        }

        private static SKColor ParseColor(string colorHex, string fallbackHex)
        {
            try
            {
                return SKColor.Parse(string.IsNullOrWhiteSpace(colorHex) ? fallbackHex : colorHex);
            }
            catch
            {
                return SKColor.Parse(fallbackHex);
            }
        }
    }
}

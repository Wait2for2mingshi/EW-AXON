using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using EW_Assistant.Domain.Reports;

namespace EW_Assistant.Services.Reports
{
    internal static class ReportSvgChartRenderer
    {
        public static string Render(ReportChartDefinition chart, int width, int height)
        {
            if (chart == null || width <= 0 || height <= 0)
            {
                return string.Empty;
            }

            switch (chart.Kind)
            {
                case ReportChartKind.StackedBar:
                    return RenderStackedBar(chart, width, height);
                case ReportChartKind.Bar:
                    return RenderBar(chart, width, height);
                case ReportChartKind.Line:
                    return RenderLine(chart, width, height);
                case ReportChartKind.Donut:
                    return RenderDonut(chart, width, height);
                default:
                    return RenderEmpty(chart.EmptyHint, width, height);
            }
        }

        private static string RenderStackedBar(ReportChartDefinition chart, int width, int height)
        {
            var count = GetPointCount(chart);
            if (count <= 0)
            {
                return RenderEmpty(chart.EmptyHint, width, height);
            }

            var max = 0d;
            for (int i = 0; i < count; i++)
            {
                var sum = chart.Series.Sum(s => GetValue(s, i));
                if (sum > max)
                {
                    max = sum;
                }
            }

            if (max <= 0)
            {
                return RenderEmpty(chart.EmptyHint, width, height);
            }

            var niceMax = GetNiceMax(max, chart.ValueFormat);
            var layout = ChartLayout.Create(width, height);
            var sb = StartSvg(width, height);
            AppendGrid(sb, layout, niceMax, chart.ValueFormat);

            var slotWidth = layout.PlotWidth / Math.Max(1, count);
            var barWidth = Math.Min(34d, slotWidth * 0.58d);
            for (int i = 0; i < count; i++)
            {
                var x = layout.PlotLeft + slotWidth * i + (slotWidth - barWidth) / 2d;
                var bottom = layout.PlotBottom;
                foreach (var series in chart.Series)
                {
                    var value = GetValue(series, i);
                    if (value <= 0)
                    {
                        continue;
                    }

                    var barHeight = value / niceMax * layout.PlotHeight;
                    var top = bottom - barHeight;
                    sb.AppendFormat(
                        CultureInfo.InvariantCulture,
                        "<rect x=\"{0:F2}\" y=\"{1:F2}\" width=\"{2:F2}\" height=\"{3:F2}\" rx=\"6\" ry=\"6\" fill=\"{4}\"/>",
                        x,
                        top,
                        barWidth,
                        Math.Max(0.1d, bottom - top),
                        Encode(series.ColorHex ?? "#94A3B8"));
                    sb.AppendLine();
                    bottom = top;
                }
            }

            AppendXAxisLabels(sb, layout, chart.Labels, count, true);
            return EndSvg(sb);
        }

        private static string RenderBar(ReportChartDefinition chart, int width, int height)
        {
            var count = GetPointCount(chart);
            var series = chart.Series.FirstOrDefault();
            if (count <= 0 || series == null)
            {
                return RenderEmpty(chart.EmptyHint, width, height);
            }

            var max = series.Values.DefaultIfEmpty(0d).Max();
            if (max <= 0)
            {
                return RenderEmpty(chart.EmptyHint, width, height);
            }

            var niceMax = GetNiceMax(max, chart.ValueFormat);
            var layout = ChartLayout.Create(width, height);
            var sb = StartSvg(width, height);
            AppendGrid(sb, layout, niceMax, chart.ValueFormat);

            var slotWidth = layout.PlotWidth / Math.Max(1, count);
            var barWidth = Math.Min(32d, slotWidth * 0.58d);
            for (int i = 0; i < count; i++)
            {
                var value = GetValue(series, i);
                if (value <= 0)
                {
                    continue;
                }

                var x = layout.PlotLeft + slotWidth * i + (slotWidth - barWidth) / 2d;
                var barHeight = value / niceMax * layout.PlotHeight;
                var y = layout.PlotBottom - barHeight;
                sb.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "<rect x=\"{0:F2}\" y=\"{1:F2}\" width=\"{2:F2}\" height=\"{3:F2}\" rx=\"6\" ry=\"6\" fill=\"{4}\"/>",
                    x,
                    y,
                    barWidth,
                    Math.Max(0.1d, barHeight),
                    Encode(series.ColorHex ?? "#2563EB"));
                sb.AppendLine();
            }

            AppendXAxisLabels(sb, layout, chart.Labels, count, true);
            return EndSvg(sb);
        }

        private static string RenderLine(ReportChartDefinition chart, int width, int height)
        {
            var count = GetPointCount(chart);
            var series = chart.Series.FirstOrDefault();
            if (count <= 0 || series == null)
            {
                return RenderEmpty(chart.EmptyHint, width, height);
            }

            var max = series.Values.DefaultIfEmpty(0d).Max();
            if (chart.ValueFormat == ReportChartValueFormat.Percent)
            {
                max = Math.Max(100d, max);
            }

            if (max <= 0)
            {
                return RenderEmpty(chart.EmptyHint, width, height);
            }

            var niceMax = GetNiceMax(max, chart.ValueFormat);
            var layout = ChartLayout.Create(width, height);
            var sb = StartSvg(width, height);
            AppendGrid(sb, layout, niceMax, chart.ValueFormat);

            var stepX = count > 1 ? layout.PlotWidth / (count - 1d) : 0d;
            var points = new List<Tuple<double, double>>();
            for (int i = 0; i < count; i++)
            {
                var x = count == 1 ? layout.PlotLeft + layout.PlotWidth / 2d : layout.PlotLeft + stepX * i;
                var value = GetValue(series, i);
                var y = layout.PlotBottom - value / niceMax * layout.PlotHeight;
                points.Add(Tuple.Create(x, y));
            }

            var lineColor = series.ColorHex ?? "#16A34A";
            var area = new StringBuilder();
            area.AppendFormat(CultureInfo.InvariantCulture, "M {0:F2} {1:F2} ", points[0].Item1, layout.PlotBottom);
            foreach (var point in points)
            {
                area.AppendFormat(CultureInfo.InvariantCulture, "L {0:F2} {1:F2} ", point.Item1, point.Item2);
            }
            area.AppendFormat(CultureInfo.InvariantCulture, "L {0:F2} {1:F2} Z", points[points.Count - 1].Item1, layout.PlotBottom);
            sb.AppendLine("<path d=\"" + area + "\" fill=\"" + Encode(ToRgba(lineColor, 0.12)) + "\" stroke=\"none\"/>");

            var polyline = string.Join(" ", points.Select(p => string.Format(CultureInfo.InvariantCulture, "{0:F2},{1:F2}", p.Item1, p.Item2)));
            sb.AppendLine("<polyline fill=\"none\" stroke=\"" + Encode(lineColor) + "\" stroke-width=\"3\" stroke-linecap=\"round\" stroke-linejoin=\"round\" points=\"" + polyline + "\"/>");
            foreach (var point in points)
            {
                sb.AppendFormat(CultureInfo.InvariantCulture, "<circle cx=\"{0:F2}\" cy=\"{1:F2}\" r=\"4.5\" fill=\"{2}\"/>", point.Item1, point.Item2, Encode(lineColor));
                sb.AppendLine();
            }

            AppendXAxisLabels(sb, layout, chart.Labels, count, false);
            return EndSvg(sb);
        }

        private static string RenderDonut(ReportChartDefinition chart, int width, int height)
        {
            var series = (chart.Series ?? new List<ReportChartSeriesDefinition>()).Where(s => GetValue(s, 0) > 0).ToList();
            if (series.Count == 0)
            {
                return RenderEmpty(chart.EmptyHint, width, height);
            }

            var total = series.Sum(s => GetValue(s, 0));
            if (total <= 0)
            {
                return RenderEmpty(chart.EmptyHint, width, height);
            }

            if (IsCompactDonutLayout(width, height))
            {
                return RenderCompactDonut(chart, width, height, series, total);
            }

            var cx = width / 2d;
            var cy = height / 2d;
            var radius = Math.Min(width, height) * 0.28d;
            var strokeWidth = radius * 0.36d;
            var sb = StartSvg(width, height);

            var startAngle = -90d;
            foreach (var item in series)
            {
                var value = GetValue(item, 0);
                var sweep = value / total * 360d;
                if (sweep >= 360d)
                {
                    sweep = 359.99d;
                }

                sb.AppendLine(BuildArcPath(cx, cy, radius, startAngle, sweep, item.ColorHex ?? "#94A3B8", strokeWidth));
                startAngle += sweep;
            }

            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "<text x=\"{0:F2}\" y=\"{1:F2}\" text-anchor=\"middle\" font-size=\"28\" font-weight=\"700\" fill=\"#111827\">{2}</text>",
                cx,
                cy + 4d,
                Encode(chart.CenterText ?? FormatValue(total, chart.ValueFormat)));
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(chart.CenterSubtext))
            {
                sb.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "<text x=\"{0:F2}\" y=\"{1:F2}\" text-anchor=\"middle\" font-size=\"14\" fill=\"#64748B\">{2}</text>",
                    cx,
                    cy + 30d,
                    Encode(chart.CenterSubtext));
                sb.AppendLine();
            }

            return EndSvg(sb);
        }

        private static string RenderCompactDonut(ReportChartDefinition chart, int width, int height, IList<ReportChartSeriesDefinition> series, double total)
        {
            var centerText = chart.CenterText ?? FormatValue(total, chart.ValueFormat);
            var hasSubtext = !string.IsNullOrWhiteSpace(chart.CenterSubtext);
            var barTop = hasSubtext ? 54d : 46d;
            var barBottom = height - 8d;
            if (barBottom - barTop < 10d)
            {
                barTop = Math.Max(18d, barBottom - 12d);
            }

            var barLeft = 8d;
            var barRight = Math.Max(barLeft + 24d, width - 8d);
            var barWidth = Math.Max(1d, barRight - barLeft);
            var barHeight = Math.Max(8d, barBottom - barTop);
            var round = Math.Min(barHeight / 2d, 8d);

            var sb = StartSvg(width, height);
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "<text x=\"{0:F2}\" y=\"{1:F2}\" text-anchor=\"middle\" font-size=\"24\" font-weight=\"700\" fill=\"#111827\">{2}</text>",
                width / 2d,
                hasSubtext ? 24d : 30d,
                Encode(centerText));
            sb.AppendLine();

            if (hasSubtext)
            {
                sb.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "<text x=\"{0:F2}\" y=\"{1:F2}\" text-anchor=\"middle\" font-size=\"12\" fill=\"#64748B\">{2}</text>",
                    width / 2d,
                    43d,
                    Encode(chart.CenterSubtext));
                sb.AppendLine();
            }

            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "<rect x=\"{0:F2}\" y=\"{1:F2}\" width=\"{2:F2}\" height=\"{3:F2}\" rx=\"{4:F2}\" ry=\"{4:F2}\" fill=\"#E5E7EB\"/>",
                barLeft,
                barTop,
                barWidth,
                barHeight,
                round);
            sb.AppendLine();

            sb.AppendLine("<defs>");
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "<clipPath id=\"compact-donut-clip\"><rect x=\"{0:F2}\" y=\"{1:F2}\" width=\"{2:F2}\" height=\"{3:F2}\" rx=\"{4:F2}\" ry=\"{4:F2}\"/></clipPath>",
                barLeft,
                barTop,
                barWidth,
                barHeight,
                round);
            sb.AppendLine();
            sb.AppendLine("</defs>");
            sb.AppendLine("<g clip-path=\"url(#compact-donut-clip)\">");

            var segmentLeft = barLeft;
            for (int i = 0; i < series.Count; i++)
            {
                var item = series[i];
                var value = GetValue(item, 0);
                var ratio = total > 0d ? value / total : 0d;
                var segmentRight = i == series.Count - 1 ? barRight : segmentLeft + barWidth * ratio;
                if (segmentRight <= segmentLeft)
                {
                    continue;
                }

                sb.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "<rect x=\"{0:F2}\" y=\"{1:F2}\" width=\"{2:F2}\" height=\"{3:F2}\" fill=\"{4}\"/>",
                    segmentLeft,
                    barTop,
                    Math.Max(0.1d, segmentRight - segmentLeft),
                    barHeight,
                    Encode(item.ColorHex ?? "#94A3B8"));
                sb.AppendLine();

                if (i < series.Count - 1)
                {
                    sb.AppendFormat(
                        CultureInfo.InvariantCulture,
                        "<line x1=\"{0:F2}\" y1=\"{1:F2}\" x2=\"{0:F2}\" y2=\"{2:F2}\" stroke=\"#FFFFFF\" stroke-width=\"1\"/>",
                        segmentRight,
                        barTop + 1d,
                        barBottom - 1d);
                    sb.AppendLine();
                }

                segmentLeft = segmentRight;
            }

            sb.AppendLine("</g>");
            return EndSvg(sb);
        }

        private static string RenderEmpty(string message, int width, int height)
        {
            var sb = StartSvg(width, height);
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "<rect x=\"18\" y=\"18\" width=\"{0}\" height=\"{1}\" rx=\"18\" ry=\"18\" fill=\"#F8FAFC\" stroke=\"#E5E7EB\"/>",
                Math.Max(0, width - 36),
                Math.Max(0, height - 36));
            sb.AppendLine();
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "<text x=\"{0:F2}\" y=\"{1:F2}\" text-anchor=\"middle\" font-size=\"16\" fill=\"#94A3B8\">{2}</text>",
                width / 2d,
                height / 2d,
                Encode(string.IsNullOrWhiteSpace(message) ? "暂无图表数据" : message));
            sb.AppendLine();
            return EndSvg(sb);
        }

        private static StringBuilder StartSvg(int width, int height)
        {
            var sb = new StringBuilder();
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {0} {1}\" width=\"100%\" height=\"auto\" role=\"img\">",
                width,
                height);
            sb.AppendLine();
            sb.AppendLine("<rect x=\"0\" y=\"0\" width=\"100%\" height=\"100%\" fill=\"#FFFFFF\"/>");
            return sb;
        }

        private static string EndSvg(StringBuilder sb)
        {
            sb.AppendLine("</svg>");
            return sb.ToString();
        }

        private static bool IsCompactDonutLayout(int width, int height)
        {
            return width <= 240 || height <= 160;
        }

        private static void AppendGrid(StringBuilder sb, ChartLayout layout, double maxValue, ReportChartValueFormat format)
        {
            const int Steps = 4;
            for (int i = 0; i <= Steps; i++)
            {
                var y = layout.PlotBottom - layout.PlotHeight * i / Steps;
                var stroke = i == 0 ? "#CBD5E1" : "#E5E7EB";
                sb.AppendFormat(CultureInfo.InvariantCulture, "<line x1=\"{0:F2}\" y1=\"{1:F2}\" x2=\"{2:F2}\" y2=\"{1:F2}\" stroke=\"{3}\" stroke-width=\"1\"/>", layout.PlotLeft, y, layout.PlotRight, stroke);
                sb.AppendLine();
                sb.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "<text x=\"{0:F2}\" y=\"{1:F2}\" text-anchor=\"end\" font-size=\"12\" fill=\"#64748B\">{2}</text>",
                    layout.PlotLeft - 8d,
                    y + 4d,
                    Encode(FormatValue(maxValue * i / Steps, format)));
                sb.AppendLine();
            }

            sb.AppendFormat(CultureInfo.InvariantCulture, "<line x1=\"{0:F2}\" y1=\"{1:F2}\" x2=\"{0:F2}\" y2=\"{2:F2}\" stroke=\"#CBD5E1\" stroke-width=\"1.2\"/>", layout.PlotLeft, layout.PlotTop, layout.PlotBottom);
            sb.AppendLine();
            sb.AppendFormat(CultureInfo.InvariantCulture, "<line x1=\"{0:F2}\" y1=\"{1:F2}\" x2=\"{2:F2}\" y2=\"{1:F2}\" stroke=\"#CBD5E1\" stroke-width=\"1.2\"/>", layout.PlotLeft, layout.PlotBottom, layout.PlotRight);
            sb.AppendLine();
        }

        private static void AppendXAxisLabels(StringBuilder sb, ChartLayout layout, IList<string> labels, int count, bool useSlotCenters)
        {
            if (labels == null || labels.Count == 0 || count <= 0)
            {
                return;
            }

            var maxLabels = Math.Max(4, (int)(layout.PlotWidth / 60d));
            var step = Math.Max(1, (int)Math.Ceiling(count / (double)maxLabels));
            if (count == 1)
            {
                sb.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "<text x=\"{0:F2}\" y=\"{1:F2}\" text-anchor=\"middle\" font-size=\"12\" fill=\"#64748B\">{2}</text>",
                    layout.PlotLeft + layout.PlotWidth / 2d,
                    layout.PlotBottom + 22d,
                    Encode(labels[0]));
                sb.AppendLine();
                return;
            }

            var slotWidth = useSlotCenters
                ? layout.PlotWidth / Math.Max(1, count)
                : layout.PlotWidth / Math.Max(1, count - 1d);

            for (int i = 0; i < count; i++)
            {
                if (i % step != 0 && i != count - 1)
                {
                    continue;
                }

                var x = useSlotCenters
                    ? layout.PlotLeft + slotWidth * i + slotWidth / 2d
                    : layout.PlotLeft + slotWidth * i;
                var label = i < labels.Count ? labels[i] : string.Empty;
                sb.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "<text x=\"{0:F2}\" y=\"{1:F2}\" text-anchor=\"middle\" font-size=\"12\" fill=\"#64748B\">{2}</text>",
                    x,
                    layout.PlotBottom + 22d,
                    Encode(label));
                sb.AppendLine();
            }
        }

        private static string BuildArcPath(double cx, double cy, double radius, double startAngle, double sweepAngle, string color, double strokeWidth)
        {
            var start = Polar(cx, cy, radius, startAngle);
            var end = Polar(cx, cy, radius, startAngle + sweepAngle);
            var largeArc = sweepAngle > 180d ? 1 : 0;
            return string.Format(
                CultureInfo.InvariantCulture,
                "<path d=\"M {0:F2} {1:F2} A {2:F2} {2:F2} 0 {3} 1 {4:F2} {5:F2}\" fill=\"none\" stroke=\"{6}\" stroke-width=\"{7:F2}\" stroke-linecap=\"butt\"/>",
                start.Item1,
                start.Item2,
                radius,
                largeArc,
                end.Item1,
                end.Item2,
                Encode(color),
                strokeWidth);
        }

        private static Tuple<double, double> Polar(double cx, double cy, double radius, double angle)
        {
            var radians = Math.PI * angle / 180d;
            return Tuple.Create(
                cx + radius * Math.Cos(radians),
                cy + radius * Math.Sin(radians));
        }

        private static int GetPointCount(ReportChartDefinition chart)
        {
            if (chart == null)
            {
                return 0;
            }

            var labelCount = chart.Labels != null ? chart.Labels.Count : 0;
            var valueCount = chart.Series != null && chart.Series.Count > 0
                ? chart.Series.Max(s => s != null && s.Values != null ? s.Values.Count : 0)
                : 0;
            return Math.Max(labelCount, valueCount);
        }

        private static double GetValue(ReportChartSeriesDefinition series, int index)
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

            var exponent = Math.Pow(10d, Math.Floor(Math.Log10(value)));
            var normalized = value / exponent;
            if (normalized <= 1d)
            {
                return exponent;
            }

            if (normalized <= 2d)
            {
                return 2d * exponent;
            }

            if (normalized <= 5d)
            {
                return 5d * exponent;
            }

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

        private static string ToRgba(string colorHex, double alpha)
        {
            try
            {
                var parsed = colorHex ?? "#16A34A";
                if (parsed.StartsWith("#", StringComparison.Ordinal))
                {
                    parsed = parsed.Substring(1);
                }

                if (parsed.Length == 6)
                {
                    var r = int.Parse(parsed.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    var g = int.Parse(parsed.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    var b = int.Parse(parsed.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    return string.Format(CultureInfo.InvariantCulture, "rgba({0},{1},{2},{3:0.###})", r, g, b, alpha);
                }
            }
            catch
            {
                // ignore
            }

            return "rgba(37,99,235,0.12)";
        }

        private static string Encode(string text)
        {
            return WebUtility.HtmlEncode(text ?? string.Empty);
        }
    }

    internal sealed class ChartLayout
    {
        public double PlotLeft { get; private set; }
        public double PlotTop { get; private set; }
        public double PlotRight { get; private set; }
        public double PlotBottom { get; private set; }
        public double PlotWidth { get { return PlotRight - PlotLeft; } }
        public double PlotHeight { get { return PlotBottom - PlotTop; } }

        public static ChartLayout Create(int width, int height)
        {
            return new ChartLayout
            {
                PlotLeft = 68d,
                PlotTop = 20d,
                PlotRight = Math.Max(80d, width - 20d),
                PlotBottom = Math.Max(80d, height - 44d)
            };
        }
    }
}

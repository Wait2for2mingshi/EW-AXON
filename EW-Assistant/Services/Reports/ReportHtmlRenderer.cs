using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using EW_Assistant.Domain.Reports;

namespace EW_Assistant.Services.Reports
{
    internal static class ReportHtmlRenderer
    {
        public static string Render(ReportInfo info, StructuredReportPreview preview, IList<ReportChartAsset> chartAssets, bool inlineSvg)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html lang=\"zh-CN\">");
            sb.AppendLine("<head>");
            sb.AppendLine("<meta charset=\"utf-8\"/>");
            sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"/>");
            sb.AppendLine("<title>" + Encode(info?.Title ?? "报表") + "</title>");
            sb.AppendLine("<style>");
            sb.AppendLine(@"
:root{
  --bg:#edf2f7;
  --card:#ffffff;
  --line:#dbe3ee;
  --text:#132033;
  --muted:#66758a;
  --blue:#2563eb;
  --orange:#f97316;
  --green:#16a34a;
  --red:#dc2626;
  --shadow:0 12px 32px rgba(15,23,42,.08);
}
*{box-sizing:border-box}
body{
  margin:0;
  background:
    radial-gradient(circle at top left, rgba(37,99,235,.12), transparent 28%),
    linear-gradient(180deg,#f8fbff 0%,var(--bg) 100%);
  color:var(--text);
  font-family:'Source Han Sans SC','Source Han Sans CN','Source Han Sans','Noto Sans CJK SC','Microsoft YaHei UI','Segoe UI',sans-serif;
  line-height:1.6;
}
.page{max-width:1180px;margin:0 auto;padding:28px 24px 40px}
.hero{
  background:linear-gradient(135deg,#0f172a 0%,#1d4ed8 58%,#38bdf8 100%);
  color:#fff;border-radius:24px;padding:28px 32px;box-shadow:var(--shadow);position:relative;overflow:hidden
}
.hero:after{
  content:'';position:absolute;right:-70px;top:-70px;width:220px;height:220px;border-radius:50%;
  background:rgba(255,255,255,.12)
}
.hero h1{margin:0;font-size:30px;letter-spacing:.4px}
.hero-meta{display:flex;flex-wrap:wrap;gap:10px;margin-top:14px}
.chip{
  display:inline-flex;align-items:center;padding:7px 12px;border-radius:999px;
  background:rgba(255,255,255,.14);border:1px solid rgba(255,255,255,.2);font-size:13px
}
.grid{display:grid;gap:16px}
.kpis{grid-template-columns:repeat(auto-fit,minmax(220px,1fr));margin-top:18px}
.card{
  background:var(--card);border:1px solid var(--line);border-radius:20px;padding:18px 20px;
  box-shadow:var(--shadow)
}
.section-title{margin:0 0 10px;font-size:20px}
.kpi{position:relative;overflow:hidden}
.kpi:before{
  content:'';position:absolute;left:18px;top:0;width:44px;height:4px;border-radius:999px;background:var(--accent,#2563eb)
}
.kpi-label{margin-top:10px;font-size:12px;color:var(--muted)}
.kpi-value{margin-top:8px;font-size:34px;font-weight:700;line-height:1.15}
.kpi-hint{margin-top:10px;color:var(--muted);font-size:13px}
.compact-chart-card{display:flex;flex-direction:column}
.compact-chart-title{margin:0;font-size:16px;font-weight:600;color:var(--text)}
.compact-chart-sub{margin:4px 0 0;color:var(--muted);font-size:13px}
.compact-chart-legend{display:flex;flex-wrap:wrap;gap:8px 12px;margin-top:10px}
.compact-chart-legend-item{display:inline-flex;align-items:center;color:#475569;font-size:12px}
.compact-chart-dot{width:9px;height:9px;border-radius:999px;display:inline-block;margin-right:6px}
.compact-chart-stage{
  margin-top:8px;
  background:linear-gradient(180deg,#ffffff 0%,#f9fbff 100%);
  border:1px solid #e8eef7;
  border-radius:14px;
  padding:0;
  overflow:hidden
}
.charts{grid-template-columns:repeat(2,minmax(0,1fr));margin-top:18px}
.chart-card.full{grid-column:1/-1}
.chart-sub{margin:0 0 12px;color:var(--muted);font-size:13px}
.chart-stage{
  background:linear-gradient(180deg,#ffffff 0%,#f9fbff 100%);
  border:1px solid #e8eef7;border-radius:16px;padding:8px;min-height:220px
}
.analysis,.detail,.notes{margin-top:18px}
.markdown h1,.markdown h2,.markdown h3{margin:20px 0 10px}
.markdown h2{font-size:22px}
.markdown h3{font-size:18px}
.markdown p{margin:10px 0;color:#26364d}
.markdown ul{margin:10px 0 10px 22px;padding:0}
.markdown li{margin:6px 0}
.markdown blockquote{
  margin:12px 0;padding:12px 16px;border-left:4px solid #93c5fd;
  background:#eff6ff;color:#1e3a8a;border-radius:12px
}
.markdown code{
  padding:2px 6px;border-radius:6px;background:#eef2ff;color:#1d4ed8;font-family:Consolas,monospace
}
table{width:100%;border-collapse:collapse}
th,td{padding:10px 12px;border-bottom:1px solid #e5e7eb;text-align:left;font-size:13px;vertical-align:top}
th{background:#f8fafc;color:#475569}
tr:nth-child(even) td{background:#fbfdff}
.notes-list{margin:0;padding-left:20px;color:var(--muted)}
.notes-list li{margin:8px 0}
.footer{margin-top:18px;color:var(--muted);font-size:12px;text-align:right}
@media print{
  body{background:#fff}
  .page{max-width:none;padding:0}
  .hero,.card{box-shadow:none}
}
@media (max-width:860px){
  .page{padding:18px 14px 28px}
  .hero{padding:22px 20px}
  .hero h1{font-size:24px}
  .charts{grid-template-columns:1fr}
  .chart-card.full{grid-column:auto}
}
");
            sb.AppendLine("</style>");
            sb.AppendLine("</head>");
            sb.AppendLine("<body>");
            sb.AppendLine("<div class=\"page\">");
            sb.AppendLine("<section class=\"hero\">");
            sb.AppendLine("<h1>" + Encode(info?.Title ?? "报表") + "</h1>");
            sb.AppendLine("<div class=\"hero-meta\">");
            sb.AppendLine("<span class=\"chip\">类型：" + Encode(info?.TypeDisplayName ?? info?.Type.ToString() ?? "报表") + "</span>");
            sb.AppendLine("<span class=\"chip\">区间：" + Encode(info?.DateLabel ?? "未解析日期") + "</span>");
            sb.AppendLine("<span class=\"chip\">源文件：" + Encode(info?.FileName ?? "未知") + "</span>");
            sb.AppendLine("<span class=\"chip\">更新时间：" + Encode(info != null ? info.GeneratedAt.ToString("yyyy-MM-dd HH:mm") : DateTime.Now.ToString("yyyy-MM-dd HH:mm")) + "</span>");
            sb.AppendLine("</div>");
            sb.AppendLine("</section>");

            if (preview?.Kpis != null && preview.Kpis.Count > 0)
            {
                sb.AppendLine("<section class=\"grid kpis\">");
                foreach (var kpi in preview.Kpis)
                {
                    var accent = EncodeBrushHex(kpi?.AccentBrush, "#2563eb");
                    sb.AppendLine("<article class=\"card kpi\" style=\"--accent:" + accent + "\">");
                    sb.AppendLine("<div class=\"kpi-label\">" + Encode(kpi?.Title) + "</div>");
                    sb.AppendLine("<div class=\"kpi-value\">" + Encode(kpi?.Value) + "</div>");
                    if (!string.IsNullOrWhiteSpace(kpi?.Hint))
                    {
                        sb.AppendLine("<div class=\"kpi-hint\">" + Encode(kpi.Hint) + "</div>");
                    }
                    sb.AppendLine("</article>");
                }

                AppendCompactTertiaryChart(sb, preview, chartAssets, inlineSvg);
                sb.AppendLine("</section>");
            }
            else
            {
                var hasCompactTertiaryChart = preview?.TertiaryChart != null && preview.TertiaryChart.Kind == ReportChartKind.Donut;
                if (hasCompactTertiaryChart)
                {
                    sb.AppendLine("<section class=\"grid kpis\">");
                    AppendCompactTertiaryChart(sb, preview, chartAssets, inlineSvg);
                    sb.AppendLine("</section>");
                }
            }

            AppendCharts(sb, preview, chartAssets, inlineSvg);
            AppendAnalysis(sb, preview);
            AppendDetailTable(sb, preview);
            AppendNotes(sb, preview);

            sb.AppendLine("<div class=\"footer\">导出时间：" + Encode(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")) + "</div>");
            sb.AppendLine("</div>");
            sb.AppendLine("</body>");
            sb.AppendLine("</html>");
            return sb.ToString();
        }

        private static void AppendCharts(StringBuilder sb, StructuredReportPreview preview, IList<ReportChartAsset> chartAssets, bool inlineSvg)
        {
            var items = new[]
            {
                new { Chart = preview?.PrimaryChart, Asset = chartAssets?.FirstOrDefault(a => a.FileName == "primary-chart.svg"), Full = false },
                new { Chart = preview?.SecondaryChart, Asset = chartAssets?.FirstOrDefault(a => a.FileName == "secondary-chart.svg"), Full = false },
                new { Chart = preview?.TertiaryChart != null && preview.TertiaryChart.Kind != ReportChartKind.Donut ? preview.TertiaryChart : null, Asset = chartAssets?.FirstOrDefault(a => a.FileName == "tertiary-chart.svg"), Full = true }
            }.Where(x => x.Chart != null).ToList();

            if (items.Count == 0)
            {
                return;
            }

            sb.AppendLine("<section class=\"grid charts\">");
            foreach (var item in items)
            {
                sb.AppendLine("<article class=\"card chart-card" + (item.Full ? " full" : string.Empty) + "\">");
                sb.AppendLine("<h2 class=\"section-title\">" + Encode(item.Chart.Title) + "</h2>");
                if (!string.IsNullOrWhiteSpace(item.Chart.Subtitle))
                {
                    sb.AppendLine("<p class=\"chart-sub\">" + Encode(item.Chart.Subtitle) + "</p>");
                }

                sb.AppendLine("<div class=\"chart-stage\">");
                if (inlineSvg)
                {
                    sb.AppendLine(item.Asset != null ? item.Asset.Svg : ReportSvgChartRenderer.Render(item.Chart, item.Full ? 860 : 520, item.Full ? 340 : 320));
                }
                else if (item.Asset != null)
                {
                    sb.AppendLine("<img src=\"assets/" + Encode(item.Asset.FileName) + "\" alt=\"" + Encode(item.Chart.Title) + "\" style=\"width:100%;height:auto;display:block;\"/>");
                }
                else
                {
                    sb.AppendLine(ReportSvgChartRenderer.Render(item.Chart, item.Full ? 860 : 520, item.Full ? 340 : 320));
                }
                sb.AppendLine("</div>");
                sb.AppendLine("</article>");
            }
            sb.AppendLine("</section>");
        }

        private static void AppendCompactTertiaryChart(StringBuilder sb, StructuredReportPreview preview, IList<ReportChartAsset> chartAssets, bool inlineSvg)
        {
            var chart = preview?.TertiaryChart;
            if (chart == null || chart.Kind != ReportChartKind.Donut)
            {
                return;
            }

            var asset = chartAssets?.FirstOrDefault(a => a.FileName == "tertiary-chart.svg");
            sb.AppendLine("<article class=\"card compact-chart-card\">");
            sb.AppendLine("<h2 class=\"compact-chart-title\">" + Encode(chart.Title) + "</h2>");
            if (!string.IsNullOrWhiteSpace(chart.Subtitle))
            {
                sb.AppendLine("<p class=\"compact-chart-sub\">" + Encode(chart.Subtitle) + "</p>");
            }

            if (chart.Series != null && chart.Series.Count > 0)
            {
                sb.AppendLine("<div class=\"compact-chart-legend\">");
                foreach (var series in chart.Series)
                {
                    sb.AppendLine(
                        "<span class=\"compact-chart-legend-item\"><span class=\"compact-chart-dot\" style=\"background:" +
                        Encode(series?.ColorHex ?? "#94A3B8") +
                        "\"></span>" +
                        Encode(series?.Name) +
                        "</span>");
                }
                sb.AppendLine("</div>");
            }

            sb.AppendLine("<div class=\"compact-chart-stage\">");
            if (inlineSvg)
            {
                sb.AppendLine(asset != null ? asset.Svg : ReportSvgChartRenderer.Render(chart, 280, 92));
            }
            else if (asset != null)
            {
                sb.AppendLine("<img src=\"assets/" + Encode(asset.FileName) + "\" alt=\"" + Encode(chart.Title) + "\" style=\"width:100%;height:auto;display:block;\"/>");
            }
            else
            {
                sb.AppendLine(ReportSvgChartRenderer.Render(chart, 280, 92));
            }

            sb.AppendLine("</div>");
            sb.AppendLine("</article>");
        }

        private static void AppendAnalysis(StringBuilder sb, StructuredReportPreview preview)
        {
            sb.AppendLine("<section class=\"card analysis\">");
            sb.AppendLine("<h2 class=\"section-title\">AI 分析正文</h2>");
            sb.AppendLine("<div class=\"markdown\">");
            sb.AppendLine(SimpleMarkdownHtmlConverter.Convert(preview?.AnalysisMarkdown));
            sb.AppendLine("</div>");
            sb.AppendLine("</section>");
        }

        private static void AppendDetailTable(StringBuilder sb, StructuredReportPreview preview)
        {
            if (preview?.DetailTable == null || preview.DetailTable.Rows.Count == 0)
            {
                return;
            }

            sb.AppendLine("<section class=\"card detail\">");
            sb.AppendLine("<h2 class=\"section-title\">" + Encode(preview.DetailTitle ?? "明细数据") + "</h2>");
            sb.AppendLine("<table>");
            sb.AppendLine("<thead><tr>");
            foreach (DataColumn column in preview.DetailTable.Columns)
            {
                sb.AppendLine("<th>" + Encode(column.ColumnName) + "</th>");
            }
            sb.AppendLine("</tr></thead>");
            sb.AppendLine("<tbody>");
            foreach (DataRow row in preview.DetailTable.Rows)
            {
                sb.AppendLine("<tr>");
                foreach (DataColumn column in preview.DetailTable.Columns)
                {
                    sb.AppendLine("<td>" + Encode(Convert.ToString(row[column], CultureInfo.InvariantCulture)) + "</td>");
                }
                sb.AppendLine("</tr>");
            }
            sb.AppendLine("</tbody></table>");
            sb.AppendLine("</section>");
        }

        private static void AppendNotes(StringBuilder sb, StructuredReportPreview preview)
        {
            if (preview?.Notes == null || preview.Notes.Count == 0)
            {
                return;
            }

            sb.AppendLine("<section class=\"card notes\">");
            sb.AppendLine("<h2 class=\"section-title\">注意事项</h2>");
            sb.AppendLine("<ul class=\"notes-list\">");
            foreach (var note in preview.Notes.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                sb.AppendLine("<li>" + Encode(note) + "</li>");
            }
            sb.AppendLine("</ul>");
            sb.AppendLine("</section>");
        }

        private static string Encode(string text)
        {
            return WebUtility.HtmlEncode(text ?? string.Empty);
        }

        private static string EncodeBrushHex(System.Windows.Media.Brush brush, string fallback)
        {
            if (brush is System.Windows.Media.SolidColorBrush solid)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "#{0:X2}{1:X2}{2:X2}",
                    solid.Color.R,
                    solid.Color.G,
                    solid.Color.B);
            }

            return fallback;
        }
    }

    internal static class SimpleMarkdownHtmlConverter
    {
        public static string Convert(string markdown)
        {
            if (string.IsNullOrWhiteSpace(markdown))
            {
                return "<blockquote>暂无分析正文。</blockquote>";
            }

            var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var sb = new StringBuilder();
            var paragraph = new List<string>();
            var inList = false;
            var inQuote = false;

            Action flushParagraph = () =>
            {
                if (paragraph.Count == 0) return;
                var content = string.Join("<br/>", paragraph.Select(FormatInline));
                sb.AppendLine("<p>" + content + "</p>");
                paragraph.Clear();
            };

            Action closeList = () =>
            {
                if (!inList) return;
                sb.AppendLine("</ul>");
                inList = false;
            };

            Action closeQuote = () =>
            {
                if (!inQuote) return;
                sb.AppendLine("</blockquote>");
                inQuote = false;
            };

            foreach (var rawLine in lines)
            {
                var line = rawLine ?? string.Empty;
                var trimmed = line.Trim();

                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    flushParagraph();
                    closeList();
                    closeQuote();
                    continue;
                }

                if (trimmed.StartsWith("### ", StringComparison.Ordinal))
                {
                    flushParagraph();
                    closeList();
                    closeQuote();
                    sb.AppendLine("<h3>" + FormatInline(trimmed.Substring(4)) + "</h3>");
                    continue;
                }

                if (trimmed.StartsWith("## ", StringComparison.Ordinal))
                {
                    flushParagraph();
                    closeList();
                    closeQuote();
                    sb.AppendLine("<h2>" + FormatInline(trimmed.Substring(3)) + "</h2>");
                    continue;
                }

                if (trimmed.StartsWith("# ", StringComparison.Ordinal))
                {
                    flushParagraph();
                    closeList();
                    closeQuote();
                    sb.AppendLine("<h1>" + FormatInline(trimmed.Substring(2)) + "</h1>");
                    continue;
                }

                if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
                {
                    flushParagraph();
                    closeQuote();
                    if (!inList)
                    {
                        sb.AppendLine("<ul>");
                        inList = true;
                    }
                    sb.AppendLine("<li>" + FormatInline(trimmed.Substring(2)) + "</li>");
                    continue;
                }

                if (trimmed.StartsWith("> ", StringComparison.Ordinal))
                {
                    flushParagraph();
                    closeList();
                    if (!inQuote)
                    {
                        sb.AppendLine("<blockquote>");
                        inQuote = true;
                    }
                    sb.AppendLine("<p>" + FormatInline(trimmed.Substring(2)) + "</p>");
                    continue;
                }

                closeList();
                closeQuote();
                paragraph.Add(trimmed);
            }

            flushParagraph();
            closeList();
            closeQuote();
            return sb.ToString();
        }

        private static string FormatInline(string text)
        {
            var encoded = WebUtility.HtmlEncode(text ?? string.Empty);
            encoded = Regex.Replace(encoded, @"\[(.+?)\]\((https?://.+?)\)", "<a href=\"$2\" target=\"_blank\" rel=\"noreferrer\">$1</a>");
            encoded = Regex.Replace(encoded, @"\*\*(.+?)\*\*", "<strong>$1</strong>");
            encoded = Regex.Replace(encoded, @"`(.+?)`", "<code>$1</code>");
            encoded = Regex.Replace(encoded, @"(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)", "<em>$1</em>");
            return encoded;
        }
    }
}

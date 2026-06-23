using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using EW_Assistant.Domain.Reports;
using Newtonsoft.Json;

namespace EW_Assistant.Services.Reports
{
    public enum ReportExportFormat
    {
        Package,
        Html,
        Pdf,
        Text
    }

    /// <summary>
    /// 报表导出服务：支持导出图文包、HTML、PDF 和纯文本。
    /// </summary>
    public class ReportExportService
    {
        private readonly ReportStorageService _storage;
        private readonly StructuredReportPreviewBuilder _previewBuilder;

        public ReportExportService(ReportStorageService storage = null, StructuredReportPreviewBuilder previewBuilder = null)
        {
            _storage = storage ?? new ReportStorageService();
            _previewBuilder = previewBuilder ?? new StructuredReportPreviewBuilder(_storage);
        }

        public void Export(ReportInfo info, ReportExportFormat format, string targetPath)
        {
            if (info == null)
            {
                throw new ArgumentNullException("info");
            }

            if (string.IsNullOrWhiteSpace(targetPath))
            {
                throw new ArgumentException("导出路径不能为空。", "targetPath");
            }

            switch (format)
            {
                case ReportExportFormat.Package:
                    ExportPackage(info, targetPath);
                    break;
                case ReportExportFormat.Html:
                    ExportHtml(info, targetPath);
                    break;
                case ReportExportFormat.Pdf:
                    ExportPdf(info, targetPath);
                    break;
                case ReportExportFormat.Text:
                    ExportText(info, targetPath);
                    break;
                default:
                    throw new InvalidOperationException("不支持的导出格式：" + format);
            }
        }

        private void ExportPackage(ReportInfo info, string targetPath)
        {
            var bundle = BuildBundle(info);
            var tempDir = CreateTempExportDirectory();

            try
            {
                WriteBundleFiles(tempDir, bundle, usePackageHtml: true, includeChartAssets: true);

                EnsureTargetParentDirectory(targetPath);
                if (File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                }

                ZipFile.CreateFromDirectory(tempDir, targetPath, CompressionLevel.Optimal, false);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        private void ExportHtml(ReportInfo info, string targetPath)
        {
            var bundle = BuildBundle(info);
            EnsureTargetParentDirectory(targetPath);
            File.WriteAllText(targetPath, bundle.StandaloneHtml, new UTF8Encoding(true));
        }

        private void ExportPdf(ReportInfo info, string targetPath)
        {
            var bundle = BuildBundle(info);
            BrowserPdfExporter.ExportHtmlToPdf(
                bundle.StandaloneHtml,
                targetPath,
                "未检测到可用于导出 PDF 的 Edge/Chrome 浏览器。建议先导出 HTML，或在系统中安装 Microsoft Edge。");
        }

        private void ExportText(ReportInfo info, string targetPath)
        {
            var bundle = BuildBundle(info);
            EnsureTargetParentDirectory(targetPath);
            File.WriteAllText(targetPath, BuildPlainTextReport(bundle), new UTF8Encoding(true));
        }

        private ReportExportBundle BuildBundle(ReportInfo info)
        {
            var preview = _previewBuilder.Build(info);
            var sourceMarkdown = SafeRead(() => _storage.ReadReportContent(info));
            var analysisMarkdown = !string.IsNullOrWhiteSpace(preview.AnalysisMarkdown)
                ? preview.AnalysisMarkdown
                : SafeRead(() => _storage.ReadReportAnalysisContent(info));
            var dataJson = SafeRead(() => _storage.ReadReportDataSnapshot(info));

            var chartAssets = new List<ReportChartAsset>();
            AddChartAsset(chartAssets, "primary-chart.svg", preview.PrimaryChart, 860, 320);
            AddChartAsset(chartAssets, "secondary-chart.svg", preview.SecondaryChart, 860, 320);
            AddChartAsset(
                chartAssets,
                "tertiary-chart.svg",
                preview.TertiaryChart,
                preview?.TertiaryChart != null && preview.TertiaryChart.Kind == ReportChartKind.Donut ? 280 : 860,
                preview?.TertiaryChart != null && preview.TertiaryChart.Kind == ReportChartKind.Donut ? 92 : 340);

            return new ReportExportBundle
            {
                Info = info,
                Preview = preview,
                SourceMarkdown = sourceMarkdown,
                AnalysisMarkdown = analysisMarkdown,
                DataJson = dataJson,
                ChartAssets = chartAssets,
                StandaloneHtml = ReportHtmlRenderer.Render(info, preview, chartAssets, true),
                PackageHtml = ReportHtmlRenderer.Render(info, preview, chartAssets, false)
            };
        }

        private static void AddChartAsset(IList<ReportChartAsset> assets, string fileName, ReportChartDefinition chart, int width, int height)
        {
            if (assets == null || chart == null)
            {
                return;
            }

            assets.Add(new ReportChartAsset
            {
                FileName = fileName,
                Chart = chart,
                Svg = ReportSvgChartRenderer.Render(chart, width, height)
            });
        }

        private static string SafeRead(Func<string> reader)
        {
            try
            {
                return reader != null ? (reader() ?? string.Empty) : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static void EnsureTargetParentDirectory(string path)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        private static string CreateTempExportDirectory()
        {
            var path = Path.Combine(Path.GetTempPath(), "ew-report-export-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private void WriteBundleFiles(string rootDir, ReportExportBundle bundle, bool usePackageHtml, bool includeChartAssets)
        {
            var htmlPath = Path.Combine(rootDir, "report.html");
            File.WriteAllText(htmlPath, usePackageHtml ? bundle.PackageHtml : bundle.StandaloneHtml, new UTF8Encoding(true));

            if (!string.IsNullOrWhiteSpace(bundle.SourceMarkdown))
            {
                File.WriteAllText(Path.Combine(rootDir, "report.txt"), BuildPlainTextReport(bundle), new UTF8Encoding(true));
            }

            if (!string.IsNullOrWhiteSpace(bundle.AnalysisMarkdown))
            {
                File.WriteAllText(Path.Combine(rootDir, "analysis.txt"), MarkdownPlainTextConverter.Convert(bundle.AnalysisMarkdown), new UTF8Encoding(true));
            }

            if (!string.IsNullOrWhiteSpace(bundle.DataJson))
            {
                File.WriteAllText(Path.Combine(rootDir, "data.json"), bundle.DataJson, new UTF8Encoding(true));
            }

            if (includeChartAssets && bundle.ChartAssets != null && bundle.ChartAssets.Count > 0)
            {
                var assetDir = Path.Combine(rootDir, "assets");
                Directory.CreateDirectory(assetDir);
                foreach (var asset in bundle.ChartAssets.Where(a => !string.IsNullOrWhiteSpace(a?.Svg)))
                {
                    File.WriteAllText(Path.Combine(assetDir, asset.FileName), asset.Svg, new UTF8Encoding(true));
                }
            }

            var manifest = new
            {
                title = bundle.Info.Title,
                reportType = bundle.Info.Type.ToString(),
                dateLabel = bundle.Info.DateLabel,
                sourceFileName = bundle.Info.FileName,
                sourceFilePath = bundle.Info.FilePath,
                reportGeneratedAt = bundle.Info.GeneratedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                exportedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                includedFiles = BuildIncludedFiles(bundle, includeChartAssets)
            };
            File.WriteAllText(Path.Combine(rootDir, "manifest.json"), JsonConvert.SerializeObject(manifest, Formatting.Indented), new UTF8Encoding(true));
        }

        private static IList<string> BuildIncludedFiles(ReportExportBundle bundle, bool includeChartAssets)
        {
            var files = new List<string> { "report.html" };
            if (!string.IsNullOrWhiteSpace(bundle.SourceMarkdown)) files.Add("report.txt");
            if (!string.IsNullOrWhiteSpace(bundle.AnalysisMarkdown)) files.Add("analysis.txt");
            if (!string.IsNullOrWhiteSpace(bundle.DataJson)) files.Add("data.json");
            files.Add("manifest.json");

            if (includeChartAssets && bundle.ChartAssets != null)
            {
                foreach (var asset in bundle.ChartAssets.Where(a => !string.IsNullOrWhiteSpace(a?.Svg)))
                {
                    files.Add("assets/" + asset.FileName);
                }
            }

            return files;
        }

        private static string BuildPlainTextReport(ReportExportBundle bundle)
        {
            if (bundle == null)
            {
                return string.Empty;
            }

            var sections = new List<string>();
            var header = BuildPlainTextHeader(bundle.Info);
            if (!string.IsNullOrWhiteSpace(header))
            {
                sections.Add(header);
            }

            var reportText = MarkdownPlainTextConverter.Convert(bundle.SourceMarkdown);
            if (!string.IsNullOrWhiteSpace(reportText))
            {
                sections.Add(reportText);
            }
            else
            {
                var analysisText = MarkdownPlainTextConverter.Convert(bundle.AnalysisMarkdown);
                if (!string.IsNullOrWhiteSpace(analysisText))
                {
                    sections.Add("AI 分析正文" + Environment.NewLine + analysisText);
                }
            }

            return string.Join(Environment.NewLine + Environment.NewLine, sections.Where(s => !string.IsNullOrWhiteSpace(s))).Trim() +
                   Environment.NewLine;
        }

        private static string BuildPlainTextHeader(ReportInfo info)
        {
            if (info == null)
            {
                return string.Empty;
            }

            var lines = new List<string>();
            if (!string.IsNullOrWhiteSpace(info.Title))
            {
                lines.Add(info.Title.Trim());
            }

            if (!string.IsNullOrWhiteSpace(info.TypeDisplayName))
            {
                lines.Add("报表类型：" + info.TypeDisplayName.Trim());
            }

            if (!string.IsNullOrWhiteSpace(info.DateLabel))
            {
                lines.Add("统计周期：" + info.DateLabel.Trim());
            }

            lines.Add("生成时间：" + info.GeneratedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            lines.Add("导出时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            return string.Join(Environment.NewLine, lines);
        }

        private static class MarkdownPlainTextConverter
        {
            public static string Convert(string markdown)
            {
                if (string.IsNullOrWhiteSpace(markdown))
                {
                    return string.Empty;
                }

                var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                var sb = new StringBuilder();
                var inCodeBlock = false;
                var pendingBlank = false;

                foreach (var rawLine in lines)
                {
                    var line = rawLine ?? string.Empty;
                    var trimmed = line.Trim();

                    if (trimmed.StartsWith("```", StringComparison.Ordinal))
                    {
                        inCodeBlock = !inCodeBlock;
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(trimmed))
                    {
                        pendingBlank = sb.Length > 0;
                        continue;
                    }

                    if (pendingBlank && sb.Length > 0)
                    {
                        sb.AppendLine();
                        pendingBlank = false;
                    }

                    if (!inCodeBlock && IsMarkdownTableSeparator(trimmed))
                    {
                        continue;
                    }

                    var text = inCodeBlock
                        ? line.TrimEnd()
                        : ConvertLine(trimmed);

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        sb.AppendLine(text);
                    }
                }

                return TrimTrailingBlankLines(sb.ToString());
            }

            private static string ConvertLine(string line)
            {
                if (LooksLikeMarkdownTableRow(line))
                {
                    return string.Join("\t", SplitMarkdownTableRow(line).Select(CleanInlineMarkdown).Where(c => !string.IsNullOrWhiteSpace(c)));
                }

                line = Regex.Replace(line, @"^#{1,6}\s+", string.Empty);
                line = Regex.Replace(line, @"^>\s?", string.Empty);
                line = Regex.Replace(line, @"^\s*[-*+]\s+", "- ");
                line = Regex.Replace(line, @"^\s*(\d+)[.)]\s+", "$1. ");
                return CleanInlineMarkdown(line);
            }

            private static string CleanInlineMarkdown(string text)
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    return string.Empty;
                }

                var cleaned = text.Trim();
                cleaned = Regex.Replace(cleaned, @"!\[(.*?)\]\((.*?)\)", "$1");
                cleaned = Regex.Replace(cleaned, @"\[(.*?)\]\((.*?)\)", m =>
                {
                    var label = m.Groups[1].Value;
                    var url = m.Groups[2].Value;
                    return string.IsNullOrWhiteSpace(url) ? label : label + " (" + url + ")";
                });
                cleaned = cleaned.Replace("**", string.Empty)
                                 .Replace("__", string.Empty)
                                 .Replace("`", string.Empty);
                cleaned = Regex.Replace(cleaned, @"(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)", "$1");
                cleaned = Regex.Replace(cleaned, @"(?<!_)_(?!_)(.+?)(?<!_)_(?!_)", "$1");
                cleaned = Regex.Replace(cleaned, @"<[^>]+>", string.Empty);
                cleaned = WebUtility.HtmlDecode(cleaned);
                return Regex.Replace(cleaned, @"[ \t]+", " ").Trim();
            }

            private static bool LooksLikeMarkdownTableRow(string line)
            {
                return !string.IsNullOrWhiteSpace(line) &&
                       line.IndexOf('|') >= 0 &&
                       (line.StartsWith("|", StringComparison.Ordinal) || line.EndsWith("|", StringComparison.Ordinal));
            }

            private static bool IsMarkdownTableSeparator(string line)
            {
                if (!LooksLikeMarkdownTableRow(line))
                {
                    return false;
                }

                var cells = SplitMarkdownTableRow(line);
                return cells.Count > 0 && cells.All(c => Regex.IsMatch(c.Trim(), @"^:?-{3,}:?$"));
            }

            private static IList<string> SplitMarkdownTableRow(string line)
            {
                var normalized = line.Trim();
                if (normalized.StartsWith("|", StringComparison.Ordinal))
                {
                    normalized = normalized.Substring(1);
                }

                if (normalized.EndsWith("|", StringComparison.Ordinal))
                {
                    normalized = normalized.Substring(0, normalized.Length - 1);
                }

                return normalized.Split('|').Select(c => c.Trim()).ToList();
            }

            private static string TrimTrailingBlankLines(string value)
            {
                if (string.IsNullOrEmpty(value))
                {
                    return string.Empty;
                }

                return value.TrimEnd('\r', '\n', ' ', '\t');
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch
            {
                // 临时目录删除失败时忽略，避免影响主流程
            }
        }
    }
}

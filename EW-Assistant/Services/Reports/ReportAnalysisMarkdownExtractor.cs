using System;
using System.Collections.Generic;
using System.Linq;
using EW_Assistant.Domain.Reports;

namespace EW_Assistant.Services.Reports
{
    internal static class ReportAnalysisMarkdownExtractor
    {
        public static string Extract(ReportType type, string markdown)
        {
            if (string.IsNullOrWhiteSpace(markdown))
            {
                return string.Empty;
            }

            switch (type)
            {
                case ReportType.DailyProd:
                    {
                        var modernDailyProd = ExtractBetween(
                            markdown,
                            new[] { "## 产能趋势", "## 状态与产能洞察", "## 趋势洞察" },
                            new[] { "\n## 注意事项" });
                        if (!string.IsNullOrWhiteSpace(modernDailyProd))
                        {
                            return modernDailyProd;
                        }

                        return ExtractDailyLegacy(markdown, "## 产能与良率诊断");
                    }

                case ReportType.DailyAlarm:
                    {
                        var modernDailyAlarm = ExtractBetween(
                            markdown,
                            new[] { "## 当日观察" },
                            new[] { "\n## 注意事项" });
                        if (!string.IsNullOrWhiteSpace(modernDailyAlarm))
                        {
                            return modernDailyAlarm;
                        }

                        return ExtractDailyLegacy(markdown, "## 概览结论");
                    }

                case ReportType.WeeklyProd:
                case ReportType.WeeklyAlarm:
                    return ExtractWeekly(markdown);

                default:
                    return string.Empty;
            }
        }

        private static string ExtractWeekly(string markdown)
        {
            var modern = ExtractBetween(
                markdown,
                new[] { "## 周度产能总结", "## 状态增强周度总结", "## AI 周度总结" },
                new[] { "\n## 异常/缺失" });
            if (!string.IsNullOrWhiteSpace(modern))
            {
                return modern;
            }

            var legacy = ExtractFromNthHeading(markdown, "## 周度KPI", 2, new[] { "\n## 异常/缺失" });
            return !string.IsNullOrWhiteSpace(legacy) ? legacy : string.Empty;
        }

        private static string ExtractDailyLegacy(string markdown, string startHeading)
        {
            if (string.IsNullOrWhiteSpace(markdown) || string.IsNullOrWhiteSpace(startHeading))
            {
                return string.Empty;
            }

            var start = markdown.IndexOf(startHeading, StringComparison.Ordinal);
            if (start < 0)
            {
                return string.Empty;
            }

            var noteHeading = "\n## 注意事项";
            var firstNote = markdown.IndexOf(noteHeading, start + startHeading.Length, StringComparison.Ordinal);
            if (firstNote < 0)
            {
                return markdown.Substring(start).Trim();
            }

            var secondNote = markdown.IndexOf(noteHeading, firstNote + noteHeading.Length, StringComparison.Ordinal);
            var end = secondNote >= 0 ? secondNote : markdown.Length;
            if (end <= start)
            {
                return string.Empty;
            }

            return markdown.Substring(start, end - start).Trim();
        }

        private static string ExtractBetween(string markdown, IEnumerable<string> startMarkers, IEnumerable<string> endMarkers)
        {
            var start = FindFirst(markdown, startMarkers);
            if (start < 0)
            {
                return string.Empty;
            }

            var end = FindFirstAfter(markdown, endMarkers, start + 1);
            if (end < 0)
            {
                end = markdown.Length;
            }

            if (end <= start)
            {
                return string.Empty;
            }

            return markdown.Substring(start, end - start).Trim();
        }

        private static string ExtractFromNthHeading(string markdown, string heading, int occurrence, IEnumerable<string> endMarkers)
        {
            if (string.IsNullOrWhiteSpace(markdown) || string.IsNullOrWhiteSpace(heading) || occurrence <= 0)
            {
                return string.Empty;
            }

            var start = IndexOfNth(markdown, heading, occurrence);
            if (start < 0)
            {
                return string.Empty;
            }

            var end = FindFirstAfter(markdown, endMarkers, start + heading.Length);
            if (end < 0)
            {
                end = markdown.Length;
            }

            if (end <= start)
            {
                return string.Empty;
            }

            return markdown.Substring(start, end - start).Trim();
        }

        private static int FindFirst(string text, IEnumerable<string> markers)
        {
            if (string.IsNullOrWhiteSpace(text) || markers == null)
            {
                return -1;
            }

            var indexes = markers
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Select(m => text.IndexOf(m, StringComparison.Ordinal))
                .Where(i => i >= 0)
                .ToList();

            return indexes.Count > 0 ? indexes.Min() : -1;
        }

        private static int FindFirstAfter(string text, IEnumerable<string> markers, int startIndex)
        {
            if (string.IsNullOrWhiteSpace(text) || markers == null)
            {
                return -1;
            }

            var indexes = markers
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Select(m => text.IndexOf(m, startIndex, StringComparison.Ordinal))
                .Where(i => i >= 0)
                .ToList();

            return indexes.Count > 0 ? indexes.Min() : -1;
        }

        private static int IndexOfNth(string text, string value, int occurrence)
        {
            var index = -1;
            var count = 0;
            var start = 0;

            while (start < text.Length)
            {
                index = text.IndexOf(value, start, StringComparison.Ordinal);
                if (index < 0)
                {
                    return -1;
                }

                count++;
                if (count == occurrence)
                {
                    return index;
                }

                start = index + value.Length;
            }

            return -1;
        }
    }
}

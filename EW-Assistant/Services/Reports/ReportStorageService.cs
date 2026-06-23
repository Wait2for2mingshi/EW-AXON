using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using EW_Assistant.Domain.Reports;
using EW_Assistant;

namespace EW_Assistant.Services.Reports
{
    /// <summary>
    /// 报表存储与索引服务，负责扫描本地文件、读取正文与导出。
    /// </summary>
    public class ReportStorageService
    {
        private const string ReportsRoot = @"D:\DataAI\Reports";

        private static readonly Dictionary<ReportType, string> s_typeDisplayNames = new Dictionary<ReportType, string>
        {
            { ReportType.DailyProd, "当日产能报表" },
            { ReportType.WeeklyProd, "产能周报" },
            { ReportType.DailyAlarm, "当日报警报表" },
            { ReportType.WeeklyAlarm, "报警周报" }
        };

        public ReportStorageService()
        {
            EnsureDirectories();
        }

        /// <summary>根目录，当前固定为 D:\DataAI\Reports。</summary>
        public string RootDirectory
        {
            get { return ReportsRoot; }
        }

        /// <summary>
        /// 判断指定日期的日报文件是否已存在。
        /// </summary>
        public bool DailyReportExists(ReportType type, DateTime date)
        {
            if (!type.IsDaily())
            {
                return false;
            }

            var path = BuildReportFilePath(type, date.Date, null);
            return IsReportPackageComplete(path);
        }

        /// <summary>获取指定日期的日报元数据（若文件存在）。</summary>
        public ReportInfo GetDailyReportInfo(ReportType type, DateTime date)
        {
            var path = BuildReportFilePath(type, date.Date, null);
            if (!IsReportPackageComplete(path)) return null;
            return GetReportInfoByPath(type, path);
        }

        /// <summary>
        /// 判断指定结束日期的周报文件是否已存在（区间=结束日向前 6 天）。
        /// </summary>
        public bool WeeklyReportExists(ReportType type, DateTime endDate)
        {
            if (!type.IsWeekly())
            {
                return false;
            }

            var start = endDate.Date.AddDays(-6);
            var path = BuildReportFilePath(type, start, endDate.Date);
            return IsReportPackageComplete(path);
        }

        /// <summary>获取指定结束日对应周报的元数据（若文件存在）。</summary>
        public ReportInfo GetWeeklyReportInfo(ReportType type, DateTime endDate)
        {
            var start = endDate.Date.AddDays(-6);
            var path = BuildReportFilePath(type, start, endDate.Date);
            if (!IsReportPackageComplete(path)) return null;
            return GetReportInfoByPath(type, path);
        }

        public string GetDailyReportPath(ReportType type, DateTime date)
        {
            if (!type.IsDaily())
            {
                throw new ArgumentException("日报类型不匹配。", "type");
            }

            return BuildReportFilePath(type, date.Date, null);
        }

        public string GetWeeklyReportPath(ReportType type, DateTime startDate, DateTime endDate)
        {
            if (!type.IsWeekly())
            {
                throw new ArgumentException("周报类型不匹配。", "type");
            }

            if (endDate < startDate)
            {
                var tmp = startDate;
                startDate = endDate;
                endDate = tmp;
            }

            return BuildReportFilePath(type, startDate.Date, endDate.Date);
        }

        public bool IsReportPackageComplete(string markdownFilePath)
        {
            if (string.IsNullOrWhiteSpace(markdownFilePath) || !File.Exists(markdownFilePath))
            {
                return false;
            }

            return File.Exists(BuildArtifactPath(markdownFilePath, ".analysis.md")) &&
                   File.Exists(BuildArtifactPath(markdownFilePath, ".data.json"));
        }

        /// <summary>
        /// 扫描全部报表类型的索引列表。
        /// </summary>
        public IList<ReportInfo> GetAllReports()
        {
            var result = new List<ReportInfo>();
            foreach (ReportType type in Enum.GetValues(typeof(ReportType)))
            {
                result.AddRange(GetReportsByType(type));
            }

            return SortReports(result).ToList();
        }

        /// <summary>
        /// 按报表类型扫描索引列表，仅读取元数据。
        /// </summary>
        public IList<ReportInfo> GetReportsByType(ReportType type)
        {
            var list = new List<ReportInfo>();
            try
            {
                var dir = EnsureTypeDirectory(type);
                var files = Directory.GetFiles(dir, "*.md", SearchOption.TopDirectoryOnly)
                    .Where(f => !IsArtifactMarkdownFile(f))
                    .ToArray();
                foreach (var file in files)
                {
                    try
                    {
                        var info = ParseReportInfo(type, file);
                        if (info != null)
                        {
                            list.Add(info);
                        }
                    }
                    catch (Exception ex)
                    {
                        LogScanIssue("解析报表文件失败：" + file + "，原因：" + ex.Message);
                    }
                }
            }
            catch
            {
                // 扫描失败时返回已收集的数据，避免影响 UI。
                LogScanIssue("扫描报表目录失败：" + type);
            }

            return SortReports(list).ToList();
        }

        /// <summary>
        /// 读取指定报表的 Markdown 正文。
        /// </summary>
        public string ReadReportContent(ReportInfo info)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.FilePath))
            {
                return string.Empty;
            }

            return File.ReadAllText(info.FilePath, Encoding.UTF8);
        }

        /// <summary>
        /// 读取报表配套的分析正文，供结构化图文页直接展示。
        /// </summary>
        public string ReadReportAnalysisContent(ReportInfo info)
        {
            return ReadArtifactContent(info, ".analysis.md");
        }

        /// <summary>
        /// 读取报表配套的数据快照 JSON，供结构化图文页回放图表。
        /// </summary>
        public string ReadReportDataSnapshot(ReportInfo info)
        {
            return ReadArtifactContent(info, ".data.json");
        }

        /// <summary>
        /// 保存日报 Markdown 内容，并返回写入的文件路径。
        /// </summary>
        public string SaveReportContent(ReportType type, DateTime date, string markdown)
        {
            if (!type.IsDaily())
            {
                throw new ArgumentException("日报类型不匹配，请使用周报保存重载。", "type");
            }

            var path = BuildReportFilePath(type, date, null);
            EnsureDirectoryForPath(path);
            File.WriteAllText(path, markdown ?? string.Empty, Encoding.UTF8);
            return path;
        }

        /// <summary>
        /// 保存周报 Markdown 内容，并返回写入的文件路径。
        /// </summary>
        public string SaveReportContent(ReportType type, DateTime startDate, DateTime endDate, string markdown)
        {
            if (!type.IsWeekly())
            {
                throw new ArgumentException("周报类型不匹配，请使用日报保存重载。", "type");
            }

            if (endDate < startDate)
            {
                var tmp = startDate;
                startDate = endDate;
                endDate = tmp;
            }

            var path = BuildReportFilePath(type, startDate, endDate);
            EnsureDirectoryForPath(path);
            File.WriteAllText(path, markdown ?? string.Empty, Encoding.UTF8);
            return path;
        }

        public string SaveReportPackage(ReportType type, DateTime date, string markdown, string analysisMarkdown, string dataJson)
        {
            if (!type.IsDaily())
            {
                throw new ArgumentException("日报类型不匹配，请使用周报保存重载。", "type");
            }

            var path = BuildReportFilePath(type, date.Date, null);
            SaveReportPackage(path, markdown, analysisMarkdown, dataJson);
            return path;
        }

        public string SaveReportPackage(ReportType type, DateTime startDate, DateTime endDate, string markdown, string analysisMarkdown, string dataJson)
        {
            if (!type.IsWeekly())
            {
                throw new ArgumentException("周报类型不匹配，请使用日报保存重载。", "type");
            }

            if (endDate < startDate)
            {
                var tmp = startDate;
                startDate = endDate;
                endDate = tmp;
            }

            var path = BuildReportFilePath(type, startDate.Date, endDate.Date);
            SaveReportPackage(path, markdown, analysisMarkdown, dataJson);
            return path;
        }

        /// <summary>
        /// 保存报表配套工件，便于图文页直接读取分析正文和结构化数据。
        /// </summary>
        public void SaveReportArtifacts(string markdownFilePath, string analysisMarkdown, string dataJson)
        {
            if (string.IsNullOrWhiteSpace(markdownFilePath))
            {
                return;
            }

            EnsureDirectoryForPath(markdownFilePath);

            var analysisPath = BuildArtifactPath(markdownFilePath, ".analysis.md");
            var dataPath = BuildArtifactPath(markdownFilePath, ".data.json");

            File.WriteAllText(analysisPath, analysisMarkdown ?? string.Empty, Encoding.UTF8);
            File.WriteAllText(dataPath, dataJson ?? string.Empty, Encoding.UTF8);
        }

        /// <summary>
        /// 删除报表正文及其配套工件。
        /// </summary>
        public void DeleteReport(ReportInfo info)
        {
            if (info == null)
            {
                return;
            }

            DeleteReport(info.FilePath);
        }

        /// <summary>
        /// 删除指定 Markdown 报表及其配套工件。
        /// </summary>
        public void DeleteReport(string markdownFilePath)
        {
            if (string.IsNullOrWhiteSpace(markdownFilePath))
            {
                return;
            }

            DeleteFileIfExists(markdownFilePath);
            DeleteFileIfExists(BuildArtifactPath(markdownFilePath, ".analysis.md"));
            DeleteFileIfExists(BuildArtifactPath(markdownFilePath, ".data.json"));
        }

        /// <summary>
        /// 导出报表文件到用户指定的位置。
        /// </summary>
        public void ExportReportFile(ReportInfo info, string targetPath)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.FilePath) || string.IsNullOrWhiteSpace(targetPath))
            {
                return;
            }

            File.Copy(info.FilePath, targetPath, true);
        }

        /// <summary>返回报表类型的中文展示名。</summary>
        public string GetTypeDisplayName(ReportType type)
        {
            string name;
            if (s_typeDisplayNames.TryGetValue(type, out name))
            {
                return name;
            }

            return type.ToString();
        }

        /// <summary>
        /// 通过文件路径重新解析报表信息，便于生成后立即刷新索引。
        /// </summary>
        public ReportInfo GetReportInfoByPath(ReportType type, string filePath)
        {
            try
            {
                return ParseReportInfo(type, filePath);
            }
            catch
            {
                return null;
            }
        }

        private void EnsureDirectories()
        {
            Directory.CreateDirectory(ReportsRoot);
            foreach (ReportType type in Enum.GetValues(typeof(ReportType)))
            {
                EnsureTypeDirectory(type);
            }
        }

        private string EnsureTypeDirectory(ReportType type)
        {
            var dir = Path.Combine(ReportsRoot, type.ToString());
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static IEnumerable<ReportInfo> SortReports(IEnumerable<ReportInfo> reports)
        {
            return reports.OrderByDescending(r => r.Date ?? r.EndDate ?? r.StartDate ?? r.GeneratedAt)
                          .ThenByDescending(r => r.GeneratedAt);
        }

        private ReportInfo ParseReportInfo(ReportType type, string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return null;
            }

            var fileName = Path.GetFileName(filePath);
            var nameWithoutExt = Path.GetFileNameWithoutExtension(filePath) ?? string.Empty;

            DateTime? date = null;
            DateTime? startDate = null;
            DateTime? endDate = null;

            var suffix = TrimTypePrefix(type, nameWithoutExt);
            if (type.IsDaily())
            {
                DateTime parsed;
                if (DateTime.TryParseExact(suffix, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
                {
                    date = parsed;
                }
            }
            else
            {
                var parts = suffix.Split(new[] { '~' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2)
                {
                    DateTime start;
                    if (DateTime.TryParseExact(parts[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out start))
                    {
                        startDate = start;
                    }

                    DateTime end;
                    if (DateTime.TryParseExact(parts[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out end))
                    {
                        endDate = end;
                    }
                }
            }

            var size = GetFileSize(filePath);
            var info = new ReportInfo
            {
                Type = type,
                Id = !string.IsNullOrWhiteSpace(nameWithoutExt) ? nameWithoutExt : Guid.NewGuid().ToString("N"),
                TypeDisplayName = GetTypeDisplayName(type),
                Title = BuildTitle(type, date, startDate, endDate, nameWithoutExt),
                DateLabel = BuildDateLabel(type, date, startDate, endDate),
                Date = date,
                StartDate = startDate,
                EndDate = endDate,
                FilePath = filePath,
                FileName = fileName,
                GeneratedAt = File.GetLastWriteTime(filePath),
                IsToday = date.HasValue && date.Value.Date == DateTime.Now.Date,
                FileSize = size,
                FileSizeText = FormatFileSize(size)
            };

            return info;
        }

        private static string TrimTypePrefix(ReportType type, string nameWithoutExt)
        {
            if (string.IsNullOrEmpty(nameWithoutExt))
            {
                return string.Empty;
            }

            var prefix = type.ToString();
            if (nameWithoutExt.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return nameWithoutExt.Substring(prefix.Length).TrimStart('_');
            }

            return nameWithoutExt;
        }

        private string BuildTitle(ReportType type, DateTime? date, DateTime? startDate, DateTime? endDate, string fallbackName)
        {
            var typeName = GetTypeDisplayName(type);
            if (type.IsDaily())
            {
                if (date.HasValue)
                {
                    return string.Format(CultureInfo.InvariantCulture, "{0}（{1:yyyy-MM-dd}）", typeName, date.Value);
                }
            }
            else
            {
                if (startDate.HasValue && endDate.HasValue)
                {
                    return string.Format(CultureInfo.InvariantCulture, "{0}（{1:yyyy-MM-dd}~{2:yyyy-MM-dd}）", typeName, startDate.Value, endDate.Value);
                }
            }

            return string.Format(CultureInfo.InvariantCulture, "{0}（{1}）", typeName, fallbackName);
        }

        private string BuildDateLabel(ReportType type, DateTime? date, DateTime? startDate, DateTime? endDate)
        {
            if (type.IsDaily())
            {
                if (date.HasValue)
                {
                    return date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                }

                return "未解析日期";
            }

            if (startDate.HasValue && endDate.HasValue)
            {
                return string.Format(CultureInfo.InvariantCulture, "{0:yyyy-MM-dd} ~ {1:yyyy-MM-dd}", startDate.Value, endDate.Value);
            }

            return "未解析日期区间";
        }

        private string BuildReportFilePath(ReportType type, DateTime startDate, DateTime? endDate)
        {
            var dir = EnsureTypeDirectory(type);
            string fileName;
            if (type.IsDaily())
            {
                fileName = string.Format(CultureInfo.InvariantCulture, "{0}_{1:yyyy-MM-dd}.md", type, startDate);
            }
            else
            {
                var end = endDate ?? startDate;
                fileName = string.Format(CultureInfo.InvariantCulture, "{0}_{1:yyyy-MM-dd}~{2:yyyy-MM-dd}.md", type, startDate, end);
            }

            return Path.Combine(dir, fileName);
        }

        private void EnsureDirectoryForPath(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }

        private void SaveReportPackage(string markdownFilePath, string markdown, string analysisMarkdown, string dataJson)
        {
            EnsureDirectoryForPath(markdownFilePath);

            var analysisPath = BuildArtifactPath(markdownFilePath, ".analysis.md");
            var dataPath = BuildArtifactPath(markdownFilePath, ".data.json");
            var tempMarkdownPath = BuildTempSiblingPath(markdownFilePath);
            var tempAnalysisPath = BuildTempSiblingPath(analysisPath);
            var tempDataPath = BuildTempSiblingPath(dataPath);

            try
            {
                File.WriteAllText(tempAnalysisPath, analysisMarkdown ?? string.Empty, Encoding.UTF8);
                File.WriteAllText(tempDataPath, dataJson ?? string.Empty, Encoding.UTF8);
                File.WriteAllText(tempMarkdownPath, markdown ?? string.Empty, Encoding.UTF8);

                PromoteTempFile(tempAnalysisPath, analysisPath);
                PromoteTempFile(tempDataPath, dataPath);
                PromoteTempFile(tempMarkdownPath, markdownFilePath);
            }
            catch
            {
                DeleteFileIfExists(tempMarkdownPath);
                DeleteFileIfExists(tempAnalysisPath);
                DeleteFileIfExists(tempDataPath);
                throw;
            }
        }

        private static string ReadArtifactContent(ReportInfo info, string artifactExtension)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.FilePath))
            {
                return string.Empty;
            }

            try
            {
                var path = BuildArtifactPath(info.FilePath, artifactExtension);
                return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool IsArtifactMarkdownFile(string path)
        {
            return path != null &&
                   path.EndsWith(".analysis.md", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildArtifactPath(string markdownFilePath, string artifactExtension)
        {
            return Path.ChangeExtension(markdownFilePath, artifactExtension);
        }

        private static string BuildTempSiblingPath(string targetPath)
        {
            var directory = Path.GetDirectoryName(targetPath) ?? string.Empty;
            var fileName = Path.GetFileName(targetPath) ?? Guid.NewGuid().ToString("N");
            return Path.Combine(directory, fileName + ".tmp." + Guid.NewGuid().ToString("N"));
        }

        private static void PromoteTempFile(string tempPath, string targetPath)
        {
            if (File.Exists(targetPath))
            {
                File.Replace(tempPath, targetPath, null, true);
                return;
            }

            File.Move(tempPath, targetPath);
        }

        private static void DeleteFileIfExists(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // 删除失败时忽略，避免影响主流程
            }
        }

        private static long GetFileSize(string filePath)
        {
            try
            {
                var fi = new FileInfo(filePath);
                return fi.Exists ? fi.Length : 0;
            }
            catch
            {
                return 0;
            }
        }

        private void LogScanIssue(string message)
        {
            try
            {
                MainWindow.PostProgramInfo(message, "warn");
            }
            catch
            {
                // 吞掉日志异常，避免影响 UI
            }
        }

        private static string FormatFileSize(long size)
        {
            const double OneK = 1024d;
            const double OneM = OneK * 1024d;
            const double OneG = OneM * 1024d;

            if (size >= OneG)
            {
                return string.Format(CultureInfo.InvariantCulture, "{0:0.##} GB", size / OneG);
            }

            if (size >= OneM)
            {
                return string.Format(CultureInfo.InvariantCulture, "{0:0.##} MB", size / OneM);
            }

            if (size >= OneK)
            {
                return string.Format(CultureInfo.InvariantCulture, "{0:0.##} KB", size / OneK);
            }

            return string.Format(CultureInfo.InvariantCulture, "{0} B", size);
        }
    }
}

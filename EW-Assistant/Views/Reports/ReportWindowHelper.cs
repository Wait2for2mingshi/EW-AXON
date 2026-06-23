using System;
using System.Diagnostics;
using System.IO;
using EW_Assistant.Domain.Reports;
using EW_Assistant.Services;
using EW_Assistant.Services.Reports;
using Microsoft.Win32;

namespace EW_Assistant.Views.Reports
{
    internal static class ReportWindowHelper
    {
        private static string L(string chineseText)
        {
            return UiLanguageService.CurrentText(chineseText);
        }

        private static string BuildExportFilter()
        {
            return L("图文报表包") + " (*.zip)|*.zip|"
                   + L("HTML 文件") + " (*.html)|*.html|"
                   + L("PDF 文件") + " (*.pdf)|*.pdf|"
                   + L("纯文本文件") + " (*.txt)|*.txt";
        }

        public static SaveFileDialog CreateExportDialog(ReportInfo info, string title)
        {
            return new SaveFileDialog
            {
                Filter = BuildExportFilter(),
                FilterIndex = 1,
                FileName = BuildDefaultExportFileName(info),
                AddExtension = true,
                Title = string.IsNullOrWhiteSpace(title) ? L("导出报表") : title
            };
        }

        public static ReportExportFormat ResolveExportFormat(int filterIndex, string fileName)
        {
            switch (filterIndex)
            {
                case 1:
                    return ReportExportFormat.Package;
                case 2:
                    return ReportExportFormat.Html;
                case 3:
                    return ReportExportFormat.Pdf;
                case 4:
                    return ReportExportFormat.Text;
            }

            var extension = Path.GetExtension(fileName) ?? string.Empty;
            if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                return ReportExportFormat.Package;
            }

            if (extension.Equals(".html", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".htm", StringComparison.OrdinalIgnoreCase))
            {
                return ReportExportFormat.Html;
            }

            if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                return ReportExportFormat.Pdf;
            }

            return ReportExportFormat.Text;
        }

        public static string NormalizeExportFilePath(ReportExportFormat format, string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return fileName;
            }

            var extension = Path.GetExtension(fileName) ?? string.Empty;
            switch (format)
            {
                case ReportExportFormat.Package:
                    return extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
                        ? fileName
                        : Path.ChangeExtension(fileName, ".zip");
                case ReportExportFormat.Html:
                    return extension.Equals(".html", StringComparison.OrdinalIgnoreCase) ||
                           extension.Equals(".htm", StringComparison.OrdinalIgnoreCase)
                        ? fileName
                        : Path.ChangeExtension(fileName, ".html");
                case ReportExportFormat.Pdf:
                    return extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)
                        ? fileName
                        : Path.ChangeExtension(fileName, ".pdf");
                case ReportExportFormat.Text:
                    return extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)
                        ? fileName
                        : Path.ChangeExtension(fileName, ".txt");
                default:
                    return fileName;
            }
        }

        public static bool TryRevealInExplorer(string filePath, out string errorMessage)
        {
            errorMessage = string.Empty;
            if (string.IsNullOrWhiteSpace(filePath))
            {
                errorMessage = L("请选择有效的报表文件。");
                return false;
            }

            if (!File.Exists(filePath))
            {
                errorMessage = L("报表文件不存在，可能已被移动或删除。");
                return false;
            }

            try
            {
                Process.Start("explorer.exe", "/select,\"" + filePath + "\"");
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = L("打开资源管理器失败：") + ex.Message;
                return false;
            }
        }

        private static string BuildDefaultExportFileName(ReportInfo info)
        {
            var baseName = Path.GetFileNameWithoutExtension(info?.FileName);
            if (string.IsNullOrWhiteSpace(baseName))
            {
                baseName = "report";
            }

            return baseName;
        }
    }
}

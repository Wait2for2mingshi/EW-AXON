using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace EW_Assistant.Services.Reports
{
    internal static class BrowserPdfExporter
    {
        public static void ExportHtmlToPdf(string html, string targetPath, string missingBrowserMessage)
        {
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                throw new ArgumentException("导出路径不能为空。", "targetPath");
            }

            var browserPath = TryResolvePdfBrowserPath();
            if (string.IsNullOrWhiteSpace(browserPath))
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(missingBrowserMessage)
                    ? "未检测到可用于导出 PDF 的 Edge/Chrome 浏览器。"
                    : missingBrowserMessage);
            }

            var tempDir = CreateTempExportDirectory();
            try
            {
                var htmlPath = Path.Combine(tempDir, "report.html");
                File.WriteAllText(htmlPath, string.IsNullOrWhiteSpace(html) ? BuildEmptyHtml() : html, new UTF8Encoding(true));

                EnsureTargetParentDirectory(targetPath);
                if (File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                }

                var htmlUri = new Uri(htmlPath).AbsoluteUri;
                var arguments = string.Format(
                    CultureInfo.InvariantCulture,
                    "--headless --disable-gpu --allow-file-access-from-files --print-to-pdf=\"{0}\" \"{1}\"",
                    targetPath,
                    htmlUri);

                using (var process = new Process())
                {
                    process.StartInfo = new ProcessStartInfo
                    {
                        FileName = browserPath,
                        Arguments = arguments,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };

                    process.Start();
                    if (!process.WaitForExit(45000))
                    {
                        try { process.Kill(); } catch { }
                        throw new InvalidOperationException("导出 PDF 超时，浏览器未在预期时间内完成渲染。");
                    }

                    if (process.ExitCode != 0 || !File.Exists(targetPath))
                    {
                        throw new InvalidOperationException("浏览器返回失败，未能生成 PDF 文件。");
                    }
                }
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        private static string TryResolvePdfBrowserPath()
        {
            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe")
            };

            return candidates.FirstOrDefault(File.Exists);
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
            var path = Path.Combine(Path.GetTempPath(), "ew-pdf-export-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
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
                // 临时目录删除失败时忽略，避免影响主流程。
            }
        }

        private static string BuildEmptyHtml()
        {
            return "<!DOCTYPE html><html><head><meta charset=\"utf-8\"/></head><body>暂无内容</body></html>";
        }
    }
}

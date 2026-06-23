using System;
using System.IO;
using System.Text;
using EW_Assistant.Diagnostics;
using Newtonsoft.Json;

namespace McpServer
{
    public class Base
    {
        public class AppConfig
        {
            // 产能/报警/IO 路径均与 WPF AppConfig 统一
            public string AlarmLogPath { get; set; } = string.Empty;
            public string ProductionLogPath { get; set; } = string.Empty;
            public string IoMapCsvPath { get; set; } = @"D:\";
            public string MCPServerIP { get; set; } = "127.0.0.1:8081";
            public string MachineCommandBaseUrl { get; set; } = "http://127.0.0.1:8081";

            public string URL { get; set; } = string.Empty;
            public string AutoKey { get; set; } = string.Empty;
            public string AutoVisionKey { get; set; } = string.Empty;
            public string AutoVisionImagePathA { get; set; } = string.Empty;
            public string AutoVisionImagePathB { get; set; } = string.Empty;
            public string AutoVisionEmptyReferenceImagePath { get; set; } = @"Doc\无料基准图.jpg";
            public bool AutoVisionImageTestMode { get; set; }
            public int AutoVisionImageLookbackSeconds { get; set; } = 10;
            public int AutoVisionCooldownSeconds { get; set; } = 180;
            public string ChatKey { get; set; } = string.Empty;
            public string DocumentKey { get; set; } = string.Empty;
            public string EarlyWarningKey { get; set; } = string.Empty;
            public bool EnableAgentControlModule { get; set; } = true;
            public bool ClearMachineAlarmsShadowMode { get; set; }
            public bool FlatFileLayout { get; set; }
            public bool UseOkNgSplitTables { get; set; }
        }

        private const string ConfigRoot = @"D:\";
        private const string ConfigFileName = "AppConfig.json";
        private static readonly TimeSpan ConfigIssueLogInterval = TimeSpan.FromMinutes(30);
        private static readonly object _cfgLock = new object();
        private static readonly object _cfgIssueLogLock = new object();
        private static DateTime _lastConfigIssueLogAtUtc = DateTime.MinValue;
        private static string _lastConfigIssueKey = string.Empty;

        /// <summary>
        /// 读取配置；当文件不存在或损坏时，返回默认配置。
        /// </summary>
        public static AppConfig ReadAppConfig()
        {
            lock (_cfgLock)
            {
                // 配置固定放在 D:\AppConfig.json
                var cfgPath = Path.Combine(ConfigRoot, ConfigFileName);

                try
                {
                    if (File.Exists(cfgPath))
                    {
                        using (var r = new StreamReader(cfgPath, new UTF8Encoding(false)))
                        {
                            var json = r.ReadToEnd();
                            var cfg = JsonConvert.DeserializeObject<AppConfig>(json) ?? new AppConfig();
                            FillMissingFields(cfg);
                            return cfg;
                        }
                    }
                    else
                    {
                        TryLogConfigReadIssue(
                            issueKey: "config_missing",
                            message: $"未找到配置文件，已回退默认配置。文件={cfgPath}");
                    }
                }
                catch (Exception ex)
                {
                    TryLogConfigReadIssue(
                        issueKey: "config_read_failed",
                        message: $"读取配置失败，已回退默认配置。文件={cfgPath}",
                        ex: ex);
                }

                var def = CreateDefault();
                FillMissingFields(def);
                return def;
            }
        }

        // —— 内部工具 —— //

        private static AppConfig CreateDefault()
        {
            return new AppConfig
            {
                AlarmLogPath = @"D:\",
                ProductionLogPath = @"D:\",
                IoMapCsvPath = @"D:\",
                MCPServerIP = "127.0.0.1:8081",
                MachineCommandBaseUrl = "http://127.0.0.1:8081"
            };
        }

        private static void FillMissingFields(AppConfig cfg)
        {
            if (cfg == null) return;

            if (string.IsNullOrWhiteSpace(cfg.ProductionLogPath))
                cfg.ProductionLogPath = @"D:\";
            if (string.IsNullOrWhiteSpace(cfg.AlarmLogPath))
                cfg.AlarmLogPath = @"D:\";

            if (string.IsNullOrWhiteSpace(cfg.IoMapCsvPath))
                cfg.IoMapCsvPath = @"D:\";
            if (string.IsNullOrWhiteSpace(cfg.MCPServerIP))
                cfg.MCPServerIP = "127.0.0.1:8081";
            else
                cfg.MCPServerIP = cfg.MCPServerIP.Trim();

            cfg.MachineCommandBaseUrl = NormalizeHttpAddress(cfg.MachineCommandBaseUrl, "http://127.0.0.1:8081", ensureTrailingSlash: false);
            if (cfg.AutoVisionKey == null)
                cfg.AutoVisionKey = string.Empty;
            cfg.AutoVisionImagePathA = cfg.AutoVisionImagePathA == null ? string.Empty : cfg.AutoVisionImagePathA.Trim();
            cfg.AutoVisionImagePathB = cfg.AutoVisionImagePathB == null ? string.Empty : cfg.AutoVisionImagePathB.Trim();
            cfg.AutoVisionEmptyReferenceImagePath = string.IsNullOrWhiteSpace(cfg.AutoVisionEmptyReferenceImagePath)
                ? @"Doc\无料基准图.jpg"
                : cfg.AutoVisionEmptyReferenceImagePath.Trim();
            if (cfg.AutoVisionImageLookbackSeconds <= 0)
                cfg.AutoVisionImageLookbackSeconds = 10;
            if (cfg.AutoVisionImageLookbackSeconds > 600)
                cfg.AutoVisionImageLookbackSeconds = 600;
            if (cfg.AutoVisionCooldownSeconds < 0)
                cfg.AutoVisionCooldownSeconds = 0;
            if (cfg.AutoVisionCooldownSeconds > 3600)
                cfg.AutoVisionCooldownSeconds = 3600;
        }

        public static bool IsAgentControlModuleEnabled(AppConfig? cfg = null)
        {
            var current = cfg ?? ReadAppConfig();
            return current?.EnableAgentControlModule ?? true;
        }

        private static string NormalizeHttpAddress(string rawValue, string fallbackValue, bool ensureTrailingSlash)
        {
            var candidate = string.IsNullOrWhiteSpace(rawValue) ? fallbackValue : rawValue.Trim();
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return fallbackValue;
            }

            if (!candidate.Contains("://"))
            {
                candidate = "http://" + candidate;
            }

            candidate = ensureTrailingSlash
                ? candidate.TrimEnd('/') + "/"
                : candidate.TrimEnd('/');

            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
            {
                return fallbackValue;
            }

            var isHttpScheme =
                string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
            if (!isHttpScheme || string.IsNullOrWhiteSpace(uri.Host))
            {
                return fallbackValue;
            }

            return candidate;
        }

        private static void TryLogConfigReadIssue(string issueKey, string message, Exception ex = null)
        {
            try
            {
                lock (_cfgIssueLogLock)
                {
                    var nowUtc = DateTime.UtcNow;
                    var normalizedKey = string.IsNullOrWhiteSpace(issueKey) ? "config_issue" : issueKey.Trim();
                    if (string.Equals(_lastConfigIssueKey, normalizedKey, StringComparison.OrdinalIgnoreCase)
                        && _lastConfigIssueLogAtUtc != DateTime.MinValue
                        && nowUtc - _lastConfigIssueLogAtUtc < ConfigIssueLogInterval)
                    {
                        return;
                    }

                    _lastConfigIssueKey = normalizedKey;
                    _lastConfigIssueLogAtUtc = nowUtc;
                }

                var dir = @"D:\Data\AiLog\McpServer";
                try
                {
                    Directory.CreateDirectory(dir);
                }
                catch
                {
                    dir = Path.Combine(AppContext.BaseDirectory ?? ".", "AiLog", "McpServer");
                    Directory.CreateDirectory(dir);
                }

                var path = Path.Combine(dir, "config-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");
                var sb = new StringBuilder();
                sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}");
                if (ex != null)
                {
                    sb.AppendLine(ex.ToString());
                }

                sb.AppendLine();
                File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));
                LogRetentionPolicy.TryCleanupFiles(
                    dir,
                    "*.log",
                    SearchOption.TopDirectoryOnly,
                    TimeSpan.FromDays(30));
            }
            catch
            {
                // 配置告警日志失败不影响主流程
            }
        }

    }
}

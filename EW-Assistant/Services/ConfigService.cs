using EW_Assistant.Settings;
using EW_Assistant.Warnings;
using Newtonsoft.Json;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace EW_Assistant.Services
{
    public static class ConfigService
    {
        private static readonly object SyncRoot = new object();
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        public static string FilePath => AgentControlPaths.AppConfigFilePath;

        private static AppConfig _current;
        public static AppConfig Current => _current ??= Load();

        public static event EventHandler<AppConfig> ConfigChanged;

        public static AppConfig Load()
        {
            AppConfig published;
            lock (SyncRoot)
            {
                if (TryLoadFromDisk(out var loaded))
                {
                    _current = loaded;
                    published = _current;
                }
                else
                {
                    // 读盘失败时仅回退到内存配置/默认配置，不反写磁盘，避免把损坏或临时异常的配置覆盖掉。
                    published = _current?.Clone() ?? AppConfig.CreateDefault();
                    EnsureMcpFields(published);
                    _current = published;
                }
            }

            ConfigChanged?.Invoke(null, published);
            return published;
        }

        public static void Save(AppConfig cfg)
        {
            AppConfig published;
            lock (SyncRoot)
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                EnsureMcpFields(cfg);

                var json = JsonConvert.SerializeObject(cfg, Formatting.Indented);
                WriteFileAtomically(FilePath, json, Utf8NoBom);

                if (_current == null)
                {
                    _current = cfg.Clone();
                }
                else
                {
                    _current.CopyFrom(cfg);
                }

                published = _current;
            }

            ConfigChanged?.Invoke(null, published);
        }

        private static bool TryLoadFromDisk(out AppConfig cfg)
        {
            cfg = null;

            try
            {
                if (!File.Exists(FilePath))
                    return false;

                var json = File.ReadAllText(FilePath, Utf8NoBom);
                var loaded = JsonConvert.DeserializeObject<AppConfig>(json);
                if (loaded == null)
                    return false;

                EnsureMcpFields(loaded);
                cfg = loaded;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void WriteFileAtomically(string path, string content, Encoding encoding)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var tempPath = Path.Combine(
                string.IsNullOrEmpty(directory) ? AppDomain.CurrentDomain.BaseDirectory : directory,
                Path.GetFileName(path) + ".tmp." + Guid.NewGuid().ToString("N"));

            try
            {
                using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(fs, encoding))
                {
                    writer.Write(content ?? string.Empty);
                    writer.Flush();
                    fs.Flush(true);
                }

                if (File.Exists(path))
                    File.Replace(tempPath, path, null, true);
                else
                    File.Move(tempPath, path);
            }
            catch
            {
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch
                {
                    // 临时文件清理失败不覆盖原始保存异常。
                }

                throw;
            }
        }

        private static void EnsureMcpFields(AppConfig cfg)
        {
            if (cfg == null)
                return;

            if (string.IsNullOrWhiteSpace(cfg.ProductionLogPath))
                cfg.ProductionLogPath = @"D:\";

            if (string.IsNullOrWhiteSpace(cfg.AlarmLogPath))
                cfg.AlarmLogPath = @"D:\";

            cfg.MachineStateLogPath = cfg.MachineStateLogPath == null ? string.Empty : cfg.MachineStateLogPath.Trim();

            if (string.IsNullOrWhiteSpace(cfg.IoMapCsvPath))
                cfg.IoMapCsvPath = @"D:\";
            if (string.IsNullOrWhiteSpace(cfg.MCPServerIP))
                cfg.MCPServerIP = "127.0.0.1:8081";
            else
                cfg.MCPServerIP = cfg.MCPServerIP.Trim();

            cfg.MachineCommandBaseUrl = NormalizeMachineCommandBaseUrl(cfg.MachineCommandBaseUrl);
            cfg.WorkHttpServerPrefix = NormalizeWorkHttpServerPrefix(cfg.WorkHttpServerPrefix);
            if (cfg.AutoVisionKey == null)
                cfg.AutoVisionKey = string.Empty;
            if (string.IsNullOrWhiteSpace(cfg.AutoVisionImagePathA))
                cfg.AutoVisionImagePathA = AppConfig.DefaultAutoVisionImagePathA;
            else
                cfg.AutoVisionImagePathA = cfg.AutoVisionImagePathA.Trim();
            cfg.AutoVisionImagePathB = cfg.AutoVisionImagePathB == null ? string.Empty : cfg.AutoVisionImagePathB.Trim();
            if (string.IsNullOrWhiteSpace(cfg.AutoVisionEmptyReferenceImagePath))
                cfg.AutoVisionEmptyReferenceImagePath = AppConfig.DefaultAutoVisionEmptyReferenceImagePath;
            else
                cfg.AutoVisionEmptyReferenceImagePath = cfg.AutoVisionEmptyReferenceImagePath.Trim();
            if (cfg.AutoVisionImageLookbackSeconds <= 0)
                cfg.AutoVisionImageLookbackSeconds = 10;
            if (cfg.AutoVisionImageLookbackSeconds > 600)
                cfg.AutoVisionImageLookbackSeconds = 600;
            if (cfg.AutoVisionCooldownSeconds < 0)
                cfg.AutoVisionCooldownSeconds = 0;
            if (cfg.AutoVisionCooldownSeconds > 3600)
                cfg.AutoVisionCooldownSeconds = 3600;
            if (cfg.ReportKey == null)
                cfg.ReportKey = string.Empty;
            if (cfg.MachineStateKey == null)
                cfg.MachineStateKey = string.Empty;
            if (cfg.BrainKey == null)
                cfg.BrainKey = string.Empty;
            if (cfg.ExecutorKey == null)
                cfg.ExecutorKey = string.Empty;
            if (cfg.TitleBarText == null)
                cfg.TitleBarText = "T66-TCT Program Powered By EW AI";
            cfg.UiLanguage = UiLanguageService.Normalize(cfg.UiLanguage);
            if (string.IsNullOrWhiteSpace(cfg.User))
                cfg.User = Environment.MachineName;
            if (cfg.MachineCode == null)
                cfg.MachineCode = string.Empty;
            if (cfg.PerformanceKey == null)
                cfg.PerformanceKey = string.Empty;
            if (cfg.SettingsEditPasswordHash == null)
                cfg.SettingsEditPasswordHash = string.Empty;
            if (cfg.DiskUsageThresholdPercent <= 0f)
                cfg.DiskUsageThresholdPercent = 90f;
            if (cfg.DiskUsageThresholdPercent > 100f)
                cfg.DiskUsageThresholdPercent = 100f;
            cfg.WarningOptions = WarningRuleOptions.Normalize(cfg.WarningOptions);
        }

        public static bool TryNormalizeHttpAddress(string rawValue, bool ensureTrailingSlash, out string normalized)
        {
            normalized = NormalizeHttpAddress(rawValue, string.Empty, ensureTrailingSlash, fallbackWhenInvalid: false);
            return !string.IsNullOrWhiteSpace(normalized);
        }

        public static string NormalizeMachineCommandBaseUrl(string rawValue)
        {
            return NormalizeHttpAddress(rawValue, "http://127.0.0.1:8081", ensureTrailingSlash: false, fallbackWhenInvalid: true);
        }

        public static string NormalizeWorkHttpServerPrefix(string rawValue)
        {
            return NormalizeHttpAddress(rawValue, "http://127.0.0.1:8091/", ensureTrailingSlash: true, fallbackWhenInvalid: true);
        }

        private static string NormalizeHttpAddress(string rawValue, string fallbackValue, bool ensureTrailingSlash, bool fallbackWhenInvalid)
        {
            var candidate = string.IsNullOrWhiteSpace(rawValue) ? fallbackValue : rawValue.Trim();
            if (string.IsNullOrWhiteSpace(candidate))
                return fallbackWhenInvalid ? fallbackValue : string.Empty;

            if (!candidate.Contains("://"))
                candidate = "http://" + candidate;

            candidate = ensureTrailingSlash
                ? candidate.TrimEnd('/') + "/"
                : candidate.TrimEnd('/');

            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
                return fallbackWhenInvalid ? fallbackValue : string.Empty;

            var isHttpScheme =
                string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
            if (!isHttpScheme || string.IsNullOrWhiteSpace(uri.Host))
                return fallbackWhenInvalid ? fallbackValue : string.Empty;

            return candidate;
        }

        public static bool IsAgentControlModuleEnabled(AppConfig cfg = null)
        {
            var current = cfg ?? _current;
            if (current == null)
                current = Load();

            return current?.EnableAgentControlModule ?? true;
        }

        public static bool IsAutoWindowsNotificationEnabled(AppConfig cfg = null)
        {
            var current = cfg ?? _current;
            if (current == null)
                current = Load();

            return current?.EnableAutoWindowsNotification ?? true;
        }

        public static bool IsEnglishUi(AppConfig cfg = null)
        {
            var current = cfg ?? _current;
            if (current == null)
                current = Load();

            return UiLanguageService.IsEnglish(current);
        }

        public static bool HasSettingsEditPassword(AppConfig cfg = null)
        {
            var current = cfg ?? _current;
            if (current == null)
                current = Load();

            return !string.IsNullOrWhiteSpace(current?.SettingsEditPasswordHash);
        }

        public static bool VerifySettingsEditPassword(string plainPassword, AppConfig cfg = null)
        {
            var current = cfg ?? _current;
            if (current == null)
                current = Load();

            var storedHash = (current?.SettingsEditPasswordHash ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(storedHash))
                return true;

            var candidateHash = HashSettingsEditPassword(plainPassword);
            return string.Equals(storedHash, candidateHash, StringComparison.OrdinalIgnoreCase);
        }

        public static string HashSettingsEditPassword(string plainPassword)
        {
            var normalized = (plainPassword ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
                return string.Empty;

            using var sha256 = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(normalized);
            var hashBytes = sha256.ComputeHash(bytes);
            var sb = new StringBuilder(hashBytes.Length * 2);
            foreach (var b in hashBytes)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        public static string ResolveBrainWorkflowApiKey(AppConfig cfg = null)
        {
            var current = cfg ?? _current;
            if (current == null)
                current = Load();

            return (current?.BrainKey ?? string.Empty).Trim();
        }

        public static string ResolveExecutorWorkflowApiKey(AppConfig cfg = null)
        {
            var current = cfg ?? _current;
            if (current == null)
                current = Load();

            return (current?.ExecutorKey ?? string.Empty).Trim();
        }
    }
}

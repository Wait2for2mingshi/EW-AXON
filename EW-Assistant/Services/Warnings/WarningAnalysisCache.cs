using EW_Assistant.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace EW_Assistant.Warnings
{
    /// <summary>
    /// 单条预警的 AI 分析记录。
    /// </summary>
    public class WarningAnalysisRecord
    {
        public string Key { get; set; }               // 唯一键：RuleId + 时间段
        public string RuleId { get; set; }
        public string RuleName { get; set; }
        public string Level { get; set; }
        public string Type { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }

        public string AiMarkdown { get; set; }        // AI 分析的 Markdown 文本
        public string EngineVersion { get; set; }     // 规则引擎版本，便于缓存失效

        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    /// <summary>
    /// 负责本地 JSON 缓存的读写。
    /// </summary>
    public class WarningAnalysisCache
    {
        private const int MaxRecordCount = 500;
        private static readonly TimeSpan ErrorReportInterval = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan RetentionWindow = TimeSpan.FromDays(7);
        private readonly string _filePath;
        private readonly object _lock = new object();
        private readonly Dictionary<string, WarningAnalysisRecord> _records =
            new Dictionary<string, WarningAnalysisRecord>(StringComparer.OrdinalIgnoreCase);
        private DateTime _lastLoadErrorReportedAt = DateTime.MinValue;
        private DateTime _lastSaveErrorReportedAt = DateTime.MinValue;

        private static readonly JsonSerializerOptions s_jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        public WarningAnalysisCache(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                var dataDir = Path.Combine(@"D:\", "DataAI");
                _filePath = Path.Combine(dataDir, "WarningAnalysisCache.json");
            }
            else
            {
                _filePath = filePath;
            }
        }

        /// <summary>
        /// 从 JSON 文件加载缓存，文件不存在时创建空集合。
        /// </summary>
        public void Load()
        {
            lock (_lock)
            {
                _records.Clear();

                try
                {
                    EnsureDirectory();
                    if (!File.Exists(_filePath))
                    {
                        return;
                    }

                    var json = File.ReadAllText(_filePath, Encoding.UTF8);
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        return;
                    }

                    var items = JsonSerializer.Deserialize<List<WarningAnalysisRecord>>(json, s_jsonOptions);
                    if (items == null) return;

                    var sanitized = false;
                    foreach (var item in items)
                    {
                        if (item != null && !string.IsNullOrWhiteSpace(item.Key))
                        {
                            var cleanedMarkdown = DifyOutputSanitizer.Clean(item.AiMarkdown);
                            if (!string.Equals(item.AiMarkdown, cleanedMarkdown, StringComparison.Ordinal))
                            {
                                item.AiMarkdown = cleanedMarkdown;
                                sanitized = true;
                            }
                            _records[item.Key] = item;
                        }
                    }

                    if (PruneUnsafe(DateTime.Now) || sanitized)
                    {
                        SaveUnsafe();
                    }
                }
                catch (Exception ex)
                {
                    ReportPersistenceIssue(ref _lastLoadErrorReportedAt, "预警 AI 缓存读取失败：" + _filePath, ex);
                }
            }
        }

        /// <summary>
        /// 将当前缓存写回 JSON。写失败会静默，避免影响预警主流程。
        /// </summary>
        public void Save()
        {
            lock (_lock)
            {
                SaveUnsafe();
            }
        }

        /// <summary>根据 key 读取缓存，未命中或 key 为空返回 false。</summary>
        public bool TryGet(string key, out WarningAnalysisRecord record)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                record = null;
                return false;
            }

            lock (_lock)
            {
                return _records.TryGetValue(key, out record);
            }
        }

        /// <summary>
        /// 插入或更新缓存，并立即持久化。
        /// </summary>
        public bool Upsert(WarningAnalysisRecord record, bool saveImmediately = true)
        {
            if (record == null || string.IsNullOrWhiteSpace(record.Key))
            {
                return false;
            }

            lock (_lock)
            {
                if (_records.TryGetValue(record.Key, out var existing))
                {
                    if (record.CreatedAt == default(DateTime))
                    {
                        record.CreatedAt = existing.CreatedAt;
                    }
                }
                else
                {
                    if (record.CreatedAt == default(DateTime))
                    {
                        record.CreatedAt = DateTime.Now;
                    }
                }

                var now = DateTime.Now;
                record.UpdatedAt = now;
                record.AiMarkdown = DifyOutputSanitizer.Clean(record.AiMarkdown);
                _records[record.Key] = record;
                PruneUnsafe(now);
                if (saveImmediately)
                {
                    SaveUnsafe();
                }

                return true;
            }
        }

        private void EnsureDirectory()
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }

        private void SaveUnsafe()
        {
            try
            {
                EnsureDirectory();
                var ordered = _records.Values
                    .OrderByDescending(GetReferenceTime)
                    .ToList();
                var json = JsonSerializer.Serialize(ordered, s_jsonOptions);
                var temp = _filePath + ".tmp";
                File.WriteAllText(temp, json, Encoding.UTF8);
                File.Copy(temp, _filePath, true);
                File.Delete(temp);
            }
            catch (Exception ex)
            {
                ReportPersistenceIssue(ref _lastSaveErrorReportedAt, "预警 AI 缓存写入失败：" + _filePath, ex);
            }
        }

        private bool PruneUnsafe(DateTime now)
        {
            var changed = false;
            var cutoff = now - RetentionWindow;
            var keysToRemove = new List<string>();
            foreach (var pair in _records)
            {
                var record = pair.Value;
                if (record == null || string.IsNullOrWhiteSpace(pair.Key))
                {
                    keysToRemove.Add(pair.Key);
                    continue;
                }

                var referenceTime = GetReferenceTime(record);
                if (referenceTime != default(DateTime) && referenceTime < cutoff)
                {
                    keysToRemove.Add(pair.Key);
                }
            }

            for (int i = 0; i < keysToRemove.Count; i++)
            {
                changed = _records.Remove(keysToRemove[i]) || changed;
            }

            if (_records.Count > MaxRecordCount)
            {
                var overflowKeys = _records.Values
                    .OrderByDescending(GetReferenceTime)
                    .Skip(MaxRecordCount)
                    .Select(record => record.Key)
                    .Where(key => !string.IsNullOrWhiteSpace(key))
                    .ToList();

                for (int i = 0; i < overflowKeys.Count; i++)
                {
                    changed = _records.Remove(overflowKeys[i]) || changed;
                }
            }

            return changed;
        }

        private static DateTime GetReferenceTime(WarningAnalysisRecord record)
        {
            if (record == null)
                return DateTime.MinValue;

            if (record.UpdatedAt != default(DateTime))
                return record.UpdatedAt;
            if (record.CreatedAt != default(DateTime))
                return record.CreatedAt;
            if (record.EndTime != default(DateTime))
                return record.EndTime;
            return record.StartTime;
        }

        private void ReportPersistenceIssue(ref DateTime lastReportedAt, string message, Exception ex)
        {
            var now = DateTime.Now;
            if (lastReportedAt != DateTime.MinValue && now - lastReportedAt < ErrorReportInterval)
            {
                return;
            }

            lastReportedAt = now;
            try
            {
                EW_Assistant.MainWindow.PostProgramInfo(message + "，" + ex.Message, "warn");
            }
            catch
            {
                // 避免在异常路径中再抛出 UI 相关错误
            }
        }
    }
}

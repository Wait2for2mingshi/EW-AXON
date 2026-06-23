using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace EW_Assistant.Services
{
    /// <summary>
    /// 异常诊断经验库：按报警内容维护“现象 + 人工对策”可复用备选项，仅供详情页手动增删改维护。
    /// </summary>
    public static class ExceptionDiagnosisTemplateService
    {
        private const string DataRoot = @"D:\DataAI";
        private const string TemplateFileName = "exception_diagnosis_templates.json";
        private const int MaxStoredTemplateCount = 2000;
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
        private static readonly object SyncRoot = new object();

        public static IReadOnlyList<DiagnosisTemplateRecord> LoadTemplatesForAlarm(string alarmContent, int maxCount = 200)
        {
            var normalizedAlarmContent = NormalizeText(alarmContent);
            if (string.IsNullOrWhiteSpace(normalizedAlarmContent))
            {
                return Array.Empty<DiagnosisTemplateRecord>();
            }

            lock (SyncRoot)
            {
                var store = LoadNormalizedStoreUnsafe(persistIfNeeded: true);
                return store.Items
                    .Where(IsVisibleTemplate)
                    .Where(item => string.Equals(
                        NormalizeText(item.AlarmContent),
                        normalizedAlarmContent,
                        StringComparison.Ordinal))
                    .OrderByDescending(item => item.LastUsedAt == default ? item.UpdatedAt : item.LastUsedAt)
                    .ThenByDescending(item => item.UpdatedAt == default ? item.CreatedAt : item.UpdatedAt)
                    .ThenBy(item => item.Phenomenon ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .Take(Math.Max(1, maxCount))
                    .Select(CloneRecord)
                    .ToList();
            }
        }

        public static DiagnosisTemplateRecord UpsertTemplate(
            string templateId,
            string alarmContent,
            string phenomenon,
            string manualCountermeasure)
        {
            var normalizedAlarmContent = NormalizeText(alarmContent);
            var normalizedPhenomenon = NormalizeText(phenomenon);
            var normalizedCountermeasure = NormalizeText(manualCountermeasure);
            if (string.IsNullOrWhiteSpace(normalizedAlarmContent)
                || string.IsNullOrWhiteSpace(normalizedPhenomenon)
                || string.IsNullOrWhiteSpace(normalizedCountermeasure))
            {
                return null;
            }

            lock (SyncRoot)
            {
                var store = LoadNormalizedStoreUnsafe(persistIfNeeded: true);
                var now = DateTime.Now;
                var fingerprint = BuildFingerprint(normalizedAlarmContent, normalizedPhenomenon, normalizedCountermeasure);
                var target = FindById(store, templateId);
                var duplicate = store.Items.FirstOrDefault(item =>
                    !item.IsDeleted
                    && string.Equals(item.Fingerprint, fingerprint, StringComparison.Ordinal)
                    && !string.Equals(item.Id, target?.Id, StringComparison.OrdinalIgnoreCase));

                if (duplicate != null)
                {
                    if (target != null && !string.Equals(target.Id, duplicate.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        target.IsDeleted = true;
                        target.UpdatedAt = now;
                    }

                    duplicate.Phenomenon = normalizedPhenomenon;
                    duplicate.ManualCountermeasure = normalizedCountermeasure;
                    duplicate.Fingerprint = fingerprint;
                    duplicate.IsDeleted = false;
                    duplicate.UpdatedAt = now;
                    duplicate.LastUsedAt = now;
                    SaveStoreUnsafe(store);
                    return CloneRecord(duplicate);
                }

                if (target == null)
                {
                    target = store.Items.FirstOrDefault(item =>
                        string.Equals(item.Fingerprint, fingerprint, StringComparison.Ordinal));
                }

                if (target == null)
                {
                    target = new DiagnosisTemplateRecord
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        CreatedAt = now
                    };
                    store.Items.Add(target);
                }

                target.Phenomenon = normalizedPhenomenon;
                target.ManualCountermeasure = normalizedCountermeasure;
                target.AlarmContent = normalizedAlarmContent;
                target.Fingerprint = fingerprint;
                target.IsDeleted = false;
                if (target.CreatedAt == default)
                {
                    target.CreatedAt = now;
                }

                target.UpdatedAt = now;
                target.LastUsedAt = now;
                SaveStoreUnsafe(store);
                return CloneRecord(target);
            }
        }

        public static bool DeleteTemplate(string templateId)
        {
            if (string.IsNullOrWhiteSpace(templateId))
            {
                return false;
            }

            lock (SyncRoot)
            {
                var store = LoadNormalizedStoreUnsafe(persistIfNeeded: true);
                var target = FindById(store, templateId);
                if (target == null || target.IsDeleted)
                {
                    return false;
                }

                target.IsDeleted = true;
                target.UpdatedAt = DateTime.Now;
                SaveStoreUnsafe(store);
                return true;
            }
        }

        private static DiagnosisTemplateRecord FindById(TemplateStore store, string templateId)
        {
            if (store == null || string.IsNullOrWhiteSpace(templateId))
            {
                return null;
            }

            return store.Items.FirstOrDefault(item =>
                string.Equals(item?.Id, templateId.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        private static TemplateStore LoadStoreUnsafe()
        {
            try
            {
                var path = GetTemplateFilePath();
                if (!File.Exists(path))
                {
                    return new TemplateStore();
                }

                var json = File.ReadAllText(path, Utf8NoBom);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return new TemplateStore();
                }

                return JsonConvert.DeserializeObject<TemplateStore>(json) ?? new TemplateStore();
            }
            catch
            {
                return new TemplateStore();
            }
        }

        private static TemplateStore LoadNormalizedStoreUnsafe(bool persistIfNeeded)
        {
            var store = LoadStoreUnsafe();
            var normalized = NormalizeStore(store);
            if (persistIfNeeded && !StoreEquals(store, normalized))
            {
                SaveNormalizedStoreUnsafe(normalized);
            }

            return normalized;
        }

        private static void SaveStoreUnsafe(TemplateStore store)
        {
            SaveNormalizedStoreUnsafe(NormalizeStore(store));
        }

        private static void SaveNormalizedStoreUnsafe(TemplateStore store)
        {
            var path = GetTemplateFilePath();
            EnsureDirectory(Path.GetDirectoryName(path));
            var json = JsonConvert.SerializeObject(store ?? new TemplateStore(), Formatting.Indented);
            WriteAllTextAtomic(path, json, Utf8NoBom);
        }

        private static TemplateStore NormalizeStore(TemplateStore store)
        {
            var items = (store?.Items ?? new List<DiagnosisTemplateRecord>())
                .Where(item => item != null)
                .Select(NormalizeRecord)
                .Where(IsVisibleTemplate)
                .OrderByDescending(GetSortTime)
                .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderByDescending(GetSortTime)
                .GroupBy(item => item.Fingerprint, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderByDescending(GetSortTime)
                .ThenBy(item => item.AlarmContent ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Phenomenon ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .Take(MaxStoredTemplateCount)
                .ToList();

            return new TemplateStore { Items = items };
        }

        private static DiagnosisTemplateRecord NormalizeRecord(DiagnosisTemplateRecord source)
        {
            if (source == null)
            {
                return null;
            }

            var alarmContent = NormalizeText(source.AlarmContent);
            var phenomenon = NormalizeText(source.Phenomenon);
            var manualCountermeasure = NormalizeText(source.ManualCountermeasure);
            var createdAt = source.CreatedAt == default ? DateTime.Now : source.CreatedAt;
            var updatedAt = source.UpdatedAt == default ? createdAt : source.UpdatedAt;
            var lastUsedAt = source.LastUsedAt == default ? updatedAt : source.LastUsedAt;

            return new DiagnosisTemplateRecord
            {
                Id = (source.Id ?? string.Empty).Trim(),
                AlarmContent = alarmContent,
                Phenomenon = phenomenon,
                ManualCountermeasure = manualCountermeasure,
                Fingerprint = BuildFingerprint(alarmContent, phenomenon, manualCountermeasure),
                CreatedAt = createdAt,
                UpdatedAt = updatedAt,
                LastUsedAt = lastUsedAt,
                IsDeleted = source.IsDeleted
            };
        }

        private static DateTime GetSortTime(DiagnosisTemplateRecord item)
        {
            return item?.LastUsedAt != default
                ? item.LastUsedAt
                : (item?.UpdatedAt != default ? item.UpdatedAt : item?.CreatedAt ?? default);
        }

        private static bool StoreEquals(TemplateStore left, TemplateStore right)
        {
            var leftItems = left?.Items ?? new List<DiagnosisTemplateRecord>();
            var rightItems = right?.Items ?? new List<DiagnosisTemplateRecord>();
            if (leftItems.Count != rightItems.Count)
            {
                return false;
            }

            for (var i = 0; i < leftItems.Count; i++)
            {
                if (!RecordEquals(leftItems[i], rightItems[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool RecordEquals(DiagnosisTemplateRecord left, DiagnosisTemplateRecord right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left == null || right == null)
            {
                return false;
            }

            return string.Equals(left.Id ?? string.Empty, right.Id ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(left.AlarmContent ?? string.Empty, right.AlarmContent ?? string.Empty, StringComparison.Ordinal)
                   && string.Equals(left.Phenomenon ?? string.Empty, right.Phenomenon ?? string.Empty, StringComparison.Ordinal)
                   && string.Equals(left.ManualCountermeasure ?? string.Empty, right.ManualCountermeasure ?? string.Empty, StringComparison.Ordinal)
                   && string.Equals(left.Fingerprint ?? string.Empty, right.Fingerprint ?? string.Empty, StringComparison.Ordinal)
                   && left.CreatedAt == right.CreatedAt
                   && left.UpdatedAt == right.UpdatedAt
                   && left.LastUsedAt == right.LastUsedAt
                   && left.IsDeleted == right.IsDeleted;
        }

        private static DiagnosisTemplateRecord CloneRecord(DiagnosisTemplateRecord source)
        {
            if (source == null)
            {
                return null;
            }

            return new DiagnosisTemplateRecord
            {
                Id = source.Id,
                AlarmContent = source.AlarmContent,
                Phenomenon = source.Phenomenon,
                ManualCountermeasure = source.ManualCountermeasure,
                Fingerprint = source.Fingerprint,
                CreatedAt = source.CreatedAt,
                UpdatedAt = source.UpdatedAt,
                LastUsedAt = source.LastUsedAt,
                IsDeleted = source.IsDeleted
            };
        }

        private static bool IsVisibleTemplate(DiagnosisTemplateRecord item)
        {
            return item != null
                   && !item.IsDeleted
                   && !string.IsNullOrWhiteSpace(item.AlarmContent)
                   && !string.IsNullOrWhiteSpace(item.Phenomenon)
                   && !string.IsNullOrWhiteSpace(item.ManualCountermeasure);
        }

        private static string GetTemplateFilePath()
        {
            return Path.Combine(DataRoot, TemplateFileName);
        }

        private static void WriteAllTextAtomic(string path, string content, Encoding encoding)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            EnsureDirectory(Path.GetDirectoryName(path));
            var tempPath = path + ".tmp." + Guid.NewGuid().ToString("N");
            File.WriteAllText(tempPath, content ?? string.Empty, encoding ?? Utf8NoBom);

            try
            {
                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(tempPath, path, null, true);
                        tempPath = null;
                        return;
                    }
                    catch
                    {
                        File.Copy(tempPath, path, true);
                    }
                }
                else
                {
                    File.Move(tempPath, path);
                    tempPath = null;
                    return;
                }

                File.Delete(tempPath);
                tempPath = null;
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(tempPath))
                {
                    try
                    {
                        if (File.Exists(tempPath))
                        {
                            File.Delete(tempPath);
                        }
                    }
                    catch
                    {
                        // 清理临时文件失败忽略。
                    }
                }
            }
        }

        private static void EnsureDirectory(string path)
        {
            if (!string.IsNullOrWhiteSpace(path) && !Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
        }

        private static string NormalizeText(string value)
        {
            return (value ?? string.Empty)
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Trim();
        }

        private static string BuildFingerprint(string alarmContent, string phenomenon, string manualCountermeasure)
        {
            return NormalizeText(alarmContent)
                   + "\u001f"
                   + NormalizeText(phenomenon)
                   + "\u001f"
                   + NormalizeText(manualCountermeasure);
        }

        private sealed class TemplateStore
        {
            public List<DiagnosisTemplateRecord> Items { get; set; } = new List<DiagnosisTemplateRecord>();
        }

        public sealed class DiagnosisTemplateRecord
        {
            public string Id { get; set; } = string.Empty;
            public string AlarmContent { get; set; } = string.Empty;
            public string Phenomenon { get; set; } = string.Empty;
            public string ManualCountermeasure { get; set; } = string.Empty;
            public string Fingerprint { get; set; } = string.Empty;
            public DateTime CreatedAt { get; set; }
            public DateTime UpdatedAt { get; set; }
            public DateTime LastUsedAt { get; set; }
            public bool IsDeleted { get; set; }
        }
    }
}

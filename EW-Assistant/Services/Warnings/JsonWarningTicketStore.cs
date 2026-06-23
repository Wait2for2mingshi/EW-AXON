using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace EW_Assistant.Warnings
{
    /// <summary>
    /// 基于本地 JSON 文件的工单存储，路径默认为 %AppData%\EW-Assistant\warning_tickets.json。
    /// </summary>
    public class JsonWarningTicketStore : IWarningTicketStore
    {
        private const int TicketRetentionDays = 7;
        private static readonly TimeSpan ErrorReportInterval = TimeSpan.FromMinutes(5);
        private readonly string _filePath;
        private readonly object _lock = new object();
        private DateTime _lastLoadErrorReportedAt = DateTime.MinValue;
        private DateTime _lastSaveErrorReportedAt = DateTime.MinValue;

        public JsonWarningTicketStore(string filePath = null)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                var dir = Path.Combine("D:\\", "DataAI");
                _filePath = Path.Combine(dir, "warning_tickets.json");
            }
            else
            {
                _filePath = filePath;
            }
        }

        /// <summary>读取所有工单，失败或文件缺失返回空列表，自动剔除过期项。</summary>
        public IList<WarningTicketRecord> LoadAll()
        {
            lock (_lock)
            {
                try
                {
                    EnsureDirectory();
                    if (!File.Exists(_filePath)) return new List<WarningTicketRecord>();

                    using (var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                    {
                        var json = reader.ReadToEnd();
                        if (string.IsNullOrWhiteSpace(json)) return new List<WarningTicketRecord>();
                        var records = JsonConvert.DeserializeObject<List<WarningTicketRecord>>(json);
                        return RemoveExpired(records);
                    }
                }
                catch (Exception ex)
                {
                    ReportPersistenceIssue(ref _lastLoadErrorReportedAt, "预警工单读取失败：" + _filePath, ex);
                    return new List<WarningTicketRecord>();
                }
            }
        }

        /// <summary>全量覆盖写入工单列表，失败静默，写入前会剔除过期数据。</summary>
        public void SaveAll(IList<WarningTicketRecord> tickets)
        {
            lock (_lock)
            {
                try
                {
                    EnsureDirectory();
                    var pruned = RemoveExpired(tickets);
                    var json = JsonConvert.SerializeObject(pruned ?? new List<WarningTicketRecord>(), Formatting.Indented);
                    var temp = _filePath + ".tmp";
                    var bytes = new UTF8Encoding(false).GetBytes(json);
                    using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        fs.Write(bytes, 0, bytes.Length);
                        fs.Flush(true);
                    }
                    File.Copy(temp, _filePath, true);
                    File.Delete(temp);
                }
                catch (Exception ex)
                {
                    ReportPersistenceIssue(ref _lastSaveErrorReportedAt, "预警工单写入失败：" + _filePath, ex);
                }
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

        /// <summary>移除超过保留期的工单，避免文件无限增长。</summary>
        private List<WarningTicketRecord> RemoveExpired(IList<WarningTicketRecord> source)
        {
            var list = source == null ? new List<WarningTicketRecord>() : new List<WarningTicketRecord>(source);
            var now = DateTime.Now;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var t = list[i];
                if (t == null)
                {
                    list.RemoveAt(i);
                    continue;
                }

                var lastSeen = t.LastSeen != default(DateTime)
                    ? t.LastSeen
                    : (t.UpdatedAt != default(DateTime) ? t.UpdatedAt : t.CreatedAt);

                if (lastSeen == default(DateTime))
                {
                    lastSeen = now;
                }

                if ((now - lastSeen).TotalDays > TicketRetentionDays)
                {
                    list.RemoveAt(i);
                }
            }

            return list;
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

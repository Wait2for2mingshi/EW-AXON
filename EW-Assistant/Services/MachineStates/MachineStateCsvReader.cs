using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using EW_Assistant.Domain.MachineStates;
using EW_Assistant.Warnings;
using Microsoft.VisualBasic.FileIO;

namespace EW_Assistant.Services.MachineStates
{
    /// <summary>
    /// 读取 MACHINESTATE CSV，保留原始状态片段供后续聚合。
    /// </summary>
    public class MachineStateCsvReader
    {
        private const int MaxWarnings = 80;

        public MachineStateReadResult Read(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentNullException("path", "请选择 MACHINESTATE CSV 文件。");
            }

            if (!File.Exists(path))
            {
                throw new FileNotFoundException("MACHINESTATE CSV 文件不存在。", path);
            }

            var result = new MachineStateReadResult
            {
                SourceFilePath = path,
                SourceFileName = Path.GetFileName(path),
                SourceHash = CalculateSha1(path)
            };

            using (var reader = CsvEncoding.OpenReader(path))
            {
                var headerLine = ReadHeaderLine(reader);
                if (string.IsNullOrWhiteSpace(headerLine))
                {
                    throw new InvalidOperationException("CSV 文件为空，未找到表头。");
                }

                var delimiter = DetectDelimiter(headerLine);
                var headers = headerLine.Split(new[] { delimiter }, StringSplitOptions.None)
                                        .Select(NormalizeHeader)
                                        .ToArray();
                var map = BuildIndexMap(headers);
                if (IsStateChangeRecordFormat(map))
                {
                    ReadStateChangeRecordRows(reader, delimiter, map, result);
                }
                else
                {
                    AddMissingColumnWarnings(map, result.Warnings);
                    ReadMachineStateRows(reader, delimiter, map, result);
                }
            }

            if (result.Records.Count == 0)
            {
                AddWarning(result.Warnings, "未解析到有效状态片段，请确认文件是否为 MACHINESTATE 或状态记录表导出。");
            }

            return result;
        }

        private static void ReadMachineStateRows(StreamReader reader, char delimiter, IDictionary<string, int> map, MachineStateReadResult result)
        {
            using (var parser = CreateParser(reader, delimiter))
            {
                var rowNumber = 1;
                while (!parser.EndOfData)
                {
                    var dataRowNumber = rowNumber + 1;
                    string[] cells;
                    try
                    {
                        cells = parser.ReadFields();
                    }
                    catch (MalformedLineException ex)
                    {
                        AddWarning(result.Warnings, "第 " + dataRowNumber + " 行 CSV 格式异常，已跳过：" + ex.Message);
                        rowNumber = dataRowNumber;
                        continue;
                    }

                    rowNumber = dataRowNumber;
                    if (cells == null || cells.Length == 0 || cells.All(string.IsNullOrWhiteSpace))
                    {
                        continue;
                    }

                    result.RowCount++;
                    var record = BuildRecord(cells, map, dataRowNumber, result.Warnings);
                    if (record != null)
                    {
                        result.Records.Add(record);
                    }
                }
            }
        }

        private static void ReadStateChangeRecordRows(StreamReader reader, char delimiter, IDictionary<string, int> map, MachineStateReadResult result)
        {
            AddMissingStateChangeColumnWarnings(map, result.Warnings);

            var rows = new List<StateChangeRow>();
            using (var parser = CreateParser(reader, delimiter))
            {
                var rowNumber = 1;
                while (!parser.EndOfData)
                {
                    var dataRowNumber = rowNumber + 1;
                    string[] cells;
                    try
                    {
                        cells = parser.ReadFields();
                    }
                    catch (MalformedLineException ex)
                    {
                        AddWarning(result.Warnings, "第 " + dataRowNumber + " 行 CSV 格式异常，已跳过：" + ex.Message);
                        rowNumber = dataRowNumber;
                        continue;
                    }

                    rowNumber = dataRowNumber;
                    if (cells == null || cells.Length == 0 || cells.All(string.IsNullOrWhiteSpace))
                    {
                        continue;
                    }

                    result.RowCount++;
                    var row = BuildStateChangeRow(cells, map, dataRowNumber, result.Warnings);
                    if (row != null)
                    {
                        rows.Add(row);
                    }
                }
            }

            var ordered = rows
                .Where(r => r.ChangeTime.HasValue && !string.IsNullOrWhiteSpace(r.CurrentState))
                .GroupBy(r => r.ChangeTime.Value)
                .Select(g => g.OrderBy(x => x.RowNumber).Last())
                .OrderBy(r => r.ChangeTime.Value)
                .ThenBy(r => r.RowNumber)
                .ToList();

            AddInitialHistoryRecord(result.Records, ordered);
            for (var i = 0; i < ordered.Count; i++)
            {
                var row = ordered[i];
                var start = row.ChangeTime.Value;
                DateTime end;
                if (i + 1 < ordered.Count)
                {
                    end = ordered[i + 1].ChangeTime.Value;
                }
                else
                {
                    var day = (row.Date ?? start.Date).Date;
                    end = day.AddDays(1);
                }

                var duration = (end - start).TotalSeconds;
                if (duration <= 0d)
                {
                    continue;
                }

                result.Records.Add(new MachineStateRecord
                {
                    RowNumber = row.RowNumber,
                    StartTime = start,
                    EndTime = end,
                    DurationSeconds = duration,
                    StateCode = row.CurrentState,
                    MachineState = row.CurrentState,
                    ErrorCode = row.StatusCode,
                    ErrorMessage = row.StatusMessage,
                    ErrorDetail = row.StatusDetail
                });
            }
        }

        private static TextFieldParser CreateParser(StreamReader reader, char delimiter)
        {
            var parser = new TextFieldParser(reader);
            parser.TextFieldType = FieldType.Delimited;
            parser.SetDelimiters(delimiter.ToString());
            parser.HasFieldsEnclosedInQuotes = true;
            parser.TrimWhiteSpace = false;
            return parser;
        }

        private static MachineStateRecord BuildRecord(string[] cells, IDictionary<string, int> map, int rowNumber, IList<string> warnings)
        {
            var start = ParseDateTime(GetCell(cells, map, "start_time"));
            var end = ParseDateTime(GetCell(cells, map, "end_time"));
            var duration = ParseDouble(GetCell(cells, map, "time_in_state"));

            if (duration <= 0d && start.HasValue && end.HasValue && end.Value > start.Value)
            {
                duration = (end.Value - start.Value).TotalSeconds;
            }

            if (!end.HasValue && start.HasValue && duration > 0d)
            {
                end = start.Value.AddSeconds(duration);
                AddWarning(warnings, "第 " + rowNumber + " 行缺少 end_time，已按 start_time + time_in_state 补齐。");
            }

            if (duration <= 0d)
            {
                AddWarning(warnings, "第 " + rowNumber + " 行缺少有效 time_in_state，已跳过。");
                return null;
            }

            if (!start.HasValue)
            {
                AddWarning(warnings, "第 " + rowNumber + " 行缺少有效 start_time，将只参与总量统计，不能拆入小时趋势。");
            }

            return new MachineStateRecord
            {
                RowNumber = rowNumber,
                StartTime = start,
                EndTime = end,
                DurationSeconds = duration,
                StateCode = Clean(GetCell(cells, map, "state")),
                MachineState = Clean(GetCell(cells, map, "d.machine_state")),
                Entity = Clean(GetCell(cells, map, "entity")),
                Station = Clean(GetCell(cells, map, "station")),
                Line = Clean(GetCell(cells, map, "line")),
                Rfid = Clean(GetCell(cells, map, "rfid")),
                ErrorCode = Clean(GetCell(cells, map, "d.code")),
                ErrorMessage = Clean(GetCell(cells, map, "d.error_message")),
                ErrorDetail = Clean(GetCell(cells, map, "d.error_detail"))
            };
        }

        private static StateChangeRow BuildStateChangeRow(string[] cells, IDictionary<string, int> map, int rowNumber, IList<string> warnings)
        {
            var date = ParseDate(GetCellAny(cells, map, "日期", "date", "day"));
            var changeTime = ParseDateTimeWithFallbackDate(
                GetCellAny(cells, map, "设备状态改变时间", "状态改变时间", "change_time", "changed_at", "state_change_time"),
                date);
            var currentState = Clean(GetCellAny(cells, map, "设备当前状态", "当前状态", "current_state", "currentstate"));

            if (!changeTime.HasValue)
            {
                AddWarning(warnings, "第 " + rowNumber + " 行缺少有效状态改变时间，已跳过。");
                return null;
            }

            if (string.IsNullOrWhiteSpace(currentState))
            {
                AddWarning(warnings, "第 " + rowNumber + " 行缺少设备当前状态，已跳过。");
                return null;
            }

            return new StateChangeRow
            {
                RowNumber = rowNumber,
                Date = date,
                ChangeTime = changeTime,
                CurrentState = currentState,
                PreviousState = Clean(GetCellAny(cells, map, "设备历史状态", "历史状态", "previous_state", "previousstate")),
                StatusMessage = Clean(GetCellAny(cells, map, "设备状态信息", "状态信息", "message", "status_message", "error_message")),
                StatusCode = Clean(GetCellAny(cells, map, "设备状态代码", "状态代码", "code", "status_code", "error_code")),
                StatusDetail = Clean(GetCellAny(cells, map, "设备状态细节", "状态细节", "detail", "status_detail", "error_detail"))
            };
        }

        private static void AddInitialHistoryRecord(IList<MachineStateRecord> records, IList<StateChangeRow> ordered)
        {
            if (records == null || ordered == null || ordered.Count == 0)
            {
                return;
            }

            var first = ordered[0];
            if (!first.ChangeTime.HasValue || string.IsNullOrWhiteSpace(first.PreviousState))
            {
                return;
            }

            var dayStart = (first.Date ?? first.ChangeTime.Value.Date).Date;
            if (first.ChangeTime.Value <= dayStart)
            {
                return;
            }

            records.Add(new MachineStateRecord
            {
                RowNumber = first.RowNumber,
                StartTime = dayStart,
                EndTime = first.ChangeTime.Value,
                DurationSeconds = (first.ChangeTime.Value - dayStart).TotalSeconds,
                StateCode = first.PreviousState,
                MachineState = first.PreviousState,
                ErrorMessage = "首条状态记录前的历史状态"
            });
        }

        private static string ReadHeaderLine(StreamReader reader)
        {
            while (!reader.EndOfStream)
            {
                var line = reader.ReadLine();
                if (!string.IsNullOrWhiteSpace(line))
                {
                    return line;
                }
            }

            return null;
        }

        private static IDictionary<string, int> BuildIndexMap(string[] headers)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < headers.Length; i++)
            {
                if (!map.ContainsKey(headers[i]))
                {
                    map.Add(headers[i], i);
                }
            }

            return map;
        }

        private static bool IsStateChangeRecordFormat(IDictionary<string, int> map)
        {
            return ContainsAny(map, "设备当前状态", "当前状态", "current_state", "currentstate") &&
                   ContainsAny(map, "设备状态改变时间", "状态改变时间", "change_time", "changed_at", "state_change_time");
        }

        private static bool ContainsAny(IDictionary<string, int> map, params string[] names)
        {
            if (map == null || names == null)
            {
                return false;
            }

            foreach (var name in names)
            {
                if (map.ContainsKey(NormalizeHeader(name)))
                {
                    return true;
                }
            }

            return false;
        }

        private static void AddMissingColumnWarnings(IDictionary<string, int> map, IList<string> warnings)
        {
            var required = new[]
            {
                "start_time",
                "end_time",
                "time_in_state",
                "state",
                "entity",
                "station",
                "line",
                "d.error_message"
            };

            foreach (var name in required)
            {
                if (!map.ContainsKey(NormalizeHeader(name)))
                {
                    AddWarning(warnings, "CSV 缺少字段：" + name);
                }
            }
        }

        private static void AddMissingStateChangeColumnWarnings(IDictionary<string, int> map, IList<string> warnings)
        {
            if (!ContainsAny(map, "日期", "date", "day"))
            {
                AddWarning(warnings, "状态记录表缺少字段：日期；将优先使用状态改变时间中的日期。");
            }

            if (!ContainsAny(map, "设备当前状态", "当前状态", "current_state", "currentstate"))
            {
                AddWarning(warnings, "状态记录表缺少字段：设备当前状态。");
            }

            if (!ContainsAny(map, "设备状态改变时间", "状态改变时间", "change_time", "changed_at", "state_change_time"))
            {
                AddWarning(warnings, "状态记录表缺少字段：设备状态改变时间。");
            }
        }

        private static string GetCell(string[] cells, IDictionary<string, int> map, string name)
        {
            int index;
            if (!map.TryGetValue(NormalizeHeader(name), out index) || index < 0 || index >= cells.Length)
            {
                return null;
            }

            return cells[index];
        }

        private static string GetCellAny(string[] cells, IDictionary<string, int> map, params string[] names)
        {
            if (names == null)
            {
                return null;
            }

            foreach (var name in names)
            {
                var value = GetCell(cells, map, name);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return null;
        }

        private static DateTime? ParseDate(string value)
        {
            var parsed = ParseDateTime(value);
            return parsed.HasValue ? (DateTime?)parsed.Value.Date : null;
        }

        private static DateTime? ParseDateTimeWithFallbackDate(string value, DateTime? fallbackDate)
        {
            var parsed = ParseDateTime(value);
            if (parsed.HasValue)
            {
                if (fallbackDate.HasValue && parsed.Value.Date != fallbackDate.Value.Date)
                {
                    // 状态记录表的“日期”列是业务日期；部分导出会在改变时间里带入错误日期，仅时间有效。
                    return fallbackDate.Value.Date + parsed.Value.TimeOfDay;
                }

                return parsed;
            }

            var text = Clean(value);
            if (!fallbackDate.HasValue || string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            TimeSpan time;
            if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out time) ||
                TimeSpan.TryParse(text, CultureInfo.CurrentCulture, out time))
            {
                return fallbackDate.Value.Date + time;
            }

            return null;
        }

        private static DateTime? ParseDateTime(string value)
        {
            var text = Clean(value);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            DateTimeOffset dto;
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out dto))
            {
                return dto.DateTime;
            }

            var normalized = NormalizeIsoDateTimeText(text);
            if (!string.Equals(normalized, text, StringComparison.Ordinal) &&
                DateTimeOffset.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out dto))
            {
                return dto.DateTime;
            }

            DateTime dt;
            if (DateTime.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out dt) ||
                DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out dt))
            {
                return dt;
            }

            return null;
        }

        private static string NormalizeIsoDateTimeText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return text;
            }

            var normalized = Regex.Replace(text.Trim(), @"([+-]\d{2})(\d{2})$", "$1:$2");
            normalized = Regex.Replace(normalized, @"(\.\d{7})\d+([+-]\d{2}:?\d{2}|Z)?$", "$1$2", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, @"([+-]\d{2})(\d{2})$", "$1:$2");
            return normalized;
        }

        private static double ParseDouble(string value)
        {
            var text = Clean(value);
            if (string.IsNullOrWhiteSpace(text))
            {
                return 0d;
            }

            double parsed;
            if (double.TryParse(text.Replace(",", string.Empty), NumberStyles.Any, CultureInfo.InvariantCulture, out parsed))
            {
                return parsed;
            }

            if (double.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out parsed))
            {
                return parsed;
            }

            return 0d;
        }

        private static string Clean(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var text = value.Trim();
            if (text.Length >= 2)
            {
                var first = text[0];
                var last = text[text.Length - 1];
                if ((first == '"' && last == '"') || (first == '\'' && last == '\''))
                {
                    text = text.Substring(1, text.Length - 2).Replace("\"\"", "\"");
                }
            }

            return text.Trim();
        }

        private static string NormalizeHeader(string value)
        {
            return (value ?? string.Empty).Trim().Trim('\uFEFF').Trim('"', '\'').Replace(" ", string.Empty).ToLowerInvariant();
        }

        private static char DetectDelimiter(string headerLine)
        {
            var candidates = new[] { ',', ';', '\t' };
            return candidates.OrderByDescending(c => headerLine.Count(ch => ch == c)).First();
        }

        private static string CalculateSha1(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sha1 = SHA1.Create())
            {
                var hash = sha1.ComputeHash(fs);
                return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant().Substring(0, 10);
            }
        }

        private static void AddWarning(IList<string> warnings, string message)
        {
            if (warnings == null || string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            if (warnings.Count < MaxWarnings)
            {
                warnings.Add(message);
            }
            else if (warnings.Count == MaxWarnings)
            {
                warnings.Add("更多 CSV 解析警告已省略，请优先检查缺失时间或格式异常的行。");
            }
        }

        private sealed class StateChangeRow
        {
            public int RowNumber { get; set; }
            public DateTime? Date { get; set; }
            public DateTime? ChangeTime { get; set; }
            public string CurrentState { get; set; }
            public string PreviousState { get; set; }
            public string StatusMessage { get; set; }
            public string StatusCode { get; set; }
            public string StatusDetail { get; set; }
        }
    }
}

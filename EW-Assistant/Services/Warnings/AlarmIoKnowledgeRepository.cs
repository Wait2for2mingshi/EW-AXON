using ClosedXML.Excel;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace EW_Assistant.Warnings
{
    /// <summary>
    /// 报警-IO 知识库条目。
    /// </summary>
    public sealed class AlarmIoKnowledgeItem
    {
        public string AlarmCode { get; set; } = string.Empty;
        public string AlarmName { get; set; } = string.Empty;
        public string Goal { get; set; } = string.Empty;
        public IReadOnlyList<string> DoList { get; set; } = Array.Empty<string>();
        public IReadOnlyList<string> DiList { get; set; } = Array.Empty<string>();
        public string IoMeaning { get; set; } = string.Empty;
        public string Expectation { get; set; } = string.Empty;
        public bool IsVisionRelated { get; set; }
    }

    /// <summary>
    /// 负责加载“报警知识库IO.xlsx”，并形成结构化信息。
    /// </summary>
    public static class AlarmIoKnowledgeRepository
    {
        private const string DefaultFileName = "报警知识库IO.xlsx";
        private static IReadOnlyList<AlarmIoKnowledgeItem> _items = Array.Empty<AlarmIoKnowledgeItem>();
        private static readonly Dictionary<string, List<AlarmIoKnowledgeItem>> _byCode =
            new Dictionary<string, List<AlarmIoKnowledgeItem>>(StringComparer.OrdinalIgnoreCase);

        public static IReadOnlyList<AlarmIoKnowledgeItem> Items => _items;
        public static int Count => _items.Count;
        public static DateTime? LastLoadedAt { get; private set; }
        public static string LastLoadedPath { get; private set; } = string.Empty;

        public static bool TryLoadFromIoMapPath(string ioMapCsvPath, out string message)
        {
            message = string.Empty;

            try
            {
                var knowledgePath = ResolveKnowledgePath(ioMapCsvPath);
                if (string.IsNullOrWhiteSpace(knowledgePath))
                {
                    Clear();
                    message = "IoMapCsvPath 未配置，未加载报警知识库。";
                    return false;
                }

                if (!File.Exists(knowledgePath))
                {
                    Clear();
                    message = "未找到报警知识库文件：" + knowledgePath;
                    return false;
                }

                LoadFromXlsx(knowledgePath);
                message = "报警知识库已加载，共 " + Count + " 条。";
                return true;
            }
            catch (Exception ex)
            {
                Clear();
                message = "读取报警知识库IO失败：" + ex.Message;
                return false;
            }
        }

        public static IReadOnlyList<AlarmIoKnowledgeItem> FindByAlarmCode(string alarmCode)
        {
            if (string.IsNullOrWhiteSpace(alarmCode))
            {
                return Array.Empty<AlarmIoKnowledgeItem>();
            }

            if (_byCode.TryGetValue(alarmCode.Trim(), out var list))
            {
                return list;
            }

            return Array.Empty<AlarmIoKnowledgeItem>();
        }

        public static bool TryMatchByErrorDesc(string errorDesc, out AlarmIoKnowledgeItem item)
        {
            item = null;
            if (string.IsNullOrWhiteSpace(errorDesc))
            {
                return false;
            }

            if (_items == null || _items.Count == 0)
            {
                return false;
            }

            var text = errorDesc.Trim();
            AlarmIoKnowledgeItem best = null;
            var bestLen = -1;

            for (int i = 0; i < _items.Count; i++)
            {
                var candidate = _items[i];
                var matchText = FindBestMatchedText(text, candidate, bestLen);
                if (string.IsNullOrWhiteSpace(matchText))
                {
                    continue;
                }

                best = candidate;
                bestLen = matchText.Length;
            }

            if (best == null)
            {
                return false;
            }

            item = best;
            return true;
        }

        private static string FindBestMatchedText(string text, AlarmIoKnowledgeItem candidate, int minLength)
        {
            if (candidate == null || string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            string best = string.Empty;
            foreach (var raw in new[] { candidate.AlarmCode, candidate.AlarmName })
            {
                var value = raw?.Trim();
                if (string.IsNullOrWhiteSpace(value)) continue;
                if (value.Length < minLength) continue;

                if (text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0
                    && value.Length >= best.Length)
                {
                    best = value;
                }
            }

            return best;
        }

        public static string FormatItemForLog(AlarmIoKnowledgeItem item)
        {
            if (item == null)
            {
                return string.Empty;
            }

            var doList = FormatIoList(item.DoList);
            var diList = FormatIoList(item.DiList);

            return string.Format(
                "报警知识库：alarm_code={0}; alarm_name={1}; goal={2}; do_list={3}; di_list={4}; io_meaning={5}; expectation={6}; is_vision_related={7}",
                item.AlarmCode ?? string.Empty,
                item.AlarmName ?? string.Empty,
                item.Goal ?? string.Empty,
                doList,
                diList,
                item.IoMeaning ?? string.Empty,
                item.Expectation ?? string.Empty,
                item.IsVisionRelated ? "TRUE" : "FALSE"
            );
        }

        public static string ResolveKnowledgePath(string ioMapCsvPath)
        {
            if (string.IsNullOrWhiteSpace(ioMapCsvPath))
            {
                return string.Empty;
            }

            var trimmed = ioMapCsvPath.Trim();
            string baseDir;
            if (trimmed.EndsWith("\\", StringComparison.Ordinal) || trimmed.EndsWith("/", StringComparison.Ordinal))
            {
                baseDir = trimmed;
            }
            else if (Path.HasExtension(trimmed))
            {
                baseDir = Path.GetDirectoryName(trimmed);
            }
            else
            {
                baseDir = trimmed;
            }

            if (string.IsNullOrWhiteSpace(baseDir))
            {
                return string.Empty;
            }

            return Path.Combine(baseDir, DefaultFileName);
        }

        private static void LoadFromXlsx(string path, string sheetName = null, int headerRow = 1)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(path);
            }

            // 允许其他进程同时打开 Excel
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var workbook = new XLWorkbook(stream);
            var sheet = string.IsNullOrWhiteSpace(sheetName) ? workbook.Worksheets.FirstOrDefault() : workbook.Worksheet(sheetName);
            if (sheet == null)
            {
                throw new InvalidDataException("未找到工作表");
            }

            var used = sheet.RangeUsed();
            var lastRow = used?.RangeAddress.LastAddress.RowNumber ?? sheet.LastRowUsed()?.RowNumber() ?? 0;
            if (lastRow <= headerRow)
            {
                throw new InvalidDataException("表中无数据或仅表头");
            }

            var lastCol = sheet.Row(headerRow).LastCellUsed()?.Address.ColumnNumber ?? 0;
            if (lastCol <= 0)
            {
                throw new InvalidDataException("表头为空");
            }

            var header = ResolveHeader(sheet, headerRow, lastCol);

            var list = new List<AlarmIoKnowledgeItem>();
            for (int r = headerRow + 1; r <= lastRow; r++)
            {
                var code = GetCellText(sheet, r, header.AlarmCode);
                var name = GetCellText(sheet, r, header.AlarmName);
                var goal = GetCellText(sheet, r, header.Goal);
                var doRaw = GetCellText(sheet, r, header.DoList);
                var diRaw = GetCellText(sheet, r, header.DiList);
                var ioMeaning = GetCellText(sheet, r, header.IoMeaning);
                var expectation = GetCellText(sheet, r, header.Expectation);
                var visionRaw = GetCellText(sheet, r, header.VisionRelated);

                var doList = ParseIoList(doRaw);
                var diList = ParseIoList(diRaw);
                var isVisionRelated = ParseBooleanFlag(visionRaw);

                if (IsRowEmpty(code, name, goal, doList, diList, ioMeaning, expectation))
                {
                    continue;
                }

                list.Add(new AlarmIoKnowledgeItem
                {
                    AlarmCode = code,
                    AlarmName = name,
                    Goal = goal,
                    DoList = doList,
                    DiList = diList,
                    IoMeaning = ioMeaning,
                    Expectation = expectation,
                    IsVisionRelated = isVisionRelated
                });
            }

            ApplySnapshot(list, path);
        }

        private static HeaderIndices ResolveHeader(IXLWorksheet sheet, int headerRow, int lastCol)
        {
            var indices = new HeaderIndices();

            for (int c = 1; c <= lastCol; c++)
            {
                var raw = sheet.Cell(headerRow, c).GetString();
                var header = NormalizeHeader(raw);
                if (string.IsNullOrWhiteSpace(header))
                {
                    continue;
                }

                if (indices.AlarmCode == 0 && HeaderMatch(header, s_alarmCodeHeaders)) indices.AlarmCode = c;
                if (indices.AlarmName == 0 && HeaderMatch(header, s_alarmNameHeaders)) indices.AlarmName = c;
                if (indices.Goal == 0 && HeaderMatch(header, s_goalHeaders)) indices.Goal = c;
                if (indices.DoList == 0 && HeaderMatch(header, s_doListHeaders)) indices.DoList = c;
                if (indices.DiList == 0 && HeaderMatch(header, s_diListHeaders)) indices.DiList = c;
                if (indices.IoMeaning == 0 && HeaderMatch(header, s_ioMeaningHeaders)) indices.IoMeaning = c;
                if (indices.Expectation == 0 && HeaderMatch(header, s_expectationHeaders)) indices.Expectation = c;
                if (indices.VisionRelated == 0 && HeaderMatch(header, s_visionRelatedHeaders)) indices.VisionRelated = c;
            }

            var missing = new List<string>();
            if (indices.AlarmCode == 0) missing.Add("报警代码(alarm_code)");
            if (indices.AlarmName == 0) missing.Add("报警名称(alarm_name)");
            if (indices.Goal == 0) missing.Add("背景/目标(goal)");
            if (indices.DoList == 0) missing.Add("输出IO列表(do_list)");
            if (indices.DiList == 0) missing.Add("输入IO列表(di_list)");
            if (indices.IoMeaning == 0) missing.Add("IO含义(io_meaning)");
            if (indices.Expectation == 0) missing.Add("期望条件(expectation)");

            if (missing.Count > 0)
            {
                throw new InvalidDataException("表头缺少：" + string.Join("、", missing));
            }

            return indices;
        }

        private static void ApplySnapshot(List<AlarmIoKnowledgeItem> list, string path)
        {
            _items = list.AsReadOnly();
            _byCode.Clear();
            foreach (var item in list)
            {
                var key = item.AlarmCode ?? string.Empty;
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                if (!_byCode.TryGetValue(key, out var bucket))
                {
                    bucket = new List<AlarmIoKnowledgeItem>();
                    _byCode.Add(key, bucket);
                }

                bucket.Add(item);
            }

            LastLoadedPath = path ?? string.Empty;
            LastLoadedAt = DateTime.Now;
        }

        private static void Clear()
        {
            _items = Array.Empty<AlarmIoKnowledgeItem>();
            _byCode.Clear();
            LastLoadedPath = string.Empty;
            LastLoadedAt = null;
        }

        private static string GetCellText(IXLWorksheet sheet, int row, int col)
        {
            if (col <= 0)
            {
                return string.Empty;
            }

            var text = sheet.Cell(row, col).GetString();
            return NormalizeCell(text);
        }

        private static string NormalizeCell(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            return text.Trim().Trim('"').Trim('“').Trim('”').Trim('\'').Trim('\uFEFF');
        }

        private static IReadOnlyList<string> ParseIoList(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return Array.Empty<string>();
            }

            var parts = raw.Split(new[]
            {
                ',', '，', ';', '；', '|', '、', '\n', '\r', '\t'
            }, StringSplitOptions.RemoveEmptyEntries);

            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in parts)
            {
                var item = part.Trim();
                if (string.IsNullOrWhiteSpace(item)) continue;
                if (seen.Add(item))
                {
                    list.Add(item);
                }
            }

            return list;
        }

        private static bool ParseBooleanFlag(string raw)
        {
            var text = NormalizeCell(raw);
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            if (bool.TryParse(text, out var parsed))
            {
                return parsed;
            }

            var normalized = NormalizeHeader(text);
            return normalized == "true"
                   || normalized == "1"
                   || normalized == "yes"
                   || normalized == "y"
                   || normalized == "是"
                   || normalized == "真";
        }

        private static string FormatIoList(IReadOnlyList<string> list)
        {
            if (list == null || list.Count == 0)
            {
                return "空";
            }

            return string.Join("、", list);
        }

        private static bool IsRowEmpty(string code, string name, string goal, IReadOnlyList<string> doList, IReadOnlyList<string> diList, string ioMeaning, string expectation)
        {
            if (!string.IsNullOrWhiteSpace(code)) return false;
            if (!string.IsNullOrWhiteSpace(name)) return false;
            if (!string.IsNullOrWhiteSpace(goal)) return false;
            if (!string.IsNullOrWhiteSpace(ioMeaning)) return false;
            if (!string.IsNullOrWhiteSpace(expectation)) return false;
            if (doList != null && doList.Count > 0) return false;
            if (diList != null && diList.Count > 0) return false;
            return true;
        }

        private static bool HeaderMatch(string normalizedHeader, string[] tokens)
        {
            foreach (var token in tokens)
            {
                var normalizedToken = NormalizeHeader(token);
                if (string.IsNullOrEmpty(normalizedToken)) continue;
                if (normalizedHeader.Contains(normalizedToken)) return true;
            }
            return false;
        }

        private static string NormalizeHeader(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            foreach (var ch in text.Trim())
            {
                if (char.IsWhiteSpace(ch)) continue;
                if (ch == '\uFEFF') continue;
                if (ch == '(' || ch == ')' || ch == '（' || ch == '）' ||
                    ch == '[' || ch == ']' || ch == '【' || ch == '】' ||
                    ch == ':' || ch == '：' || ch == '/' || ch == '\\')
                {
                    continue;
                }

                sb.Append(char.ToLowerInvariant(ch));
            }

            return sb.ToString();
        }

        private static readonly string[] s_alarmCodeHeaders =
        {
            "报警代码", "报警码", "报警编号", "alarm_code", "alarmcode"
        };

        private static readonly string[] s_alarmNameHeaders =
        {
            "报警名称", "报警内容", "报警描述", "alarm_name", "alarmname"
        };

        private static readonly string[] s_goalHeaders =
        {
            "背景/目标", "背景目标", "背景", "目标", "goal"
        };

        private static readonly string[] s_doListHeaders =
        {
            "输出io列表", "输出io", "do_list", "do列表"
        };

        private static readonly string[] s_diListHeaders =
        {
            "输入io列表", "输入io", "di_list", "di列表"
        };

        private static readonly string[] s_ioMeaningHeaders =
        {
            "io含义", "io意义", "io说明", "io_meaning"
        };

        private static readonly string[] s_expectationHeaders =
        {
            "期望条件", "期望", "expectation", "expected"
        };

        private static readonly string[] s_visionRelatedHeaders =
        {
            "是否视觉相关", "视觉相关", "是否视觉", "is_vision_related", "vision_related", "vision"
        };

        private struct HeaderIndices
        {
            public int AlarmCode;
            public int AlarmName;
            public int Goal;
            public int DoList;
            public int DiList;
            public int IoMeaning;
            public int Expectation;
            public int VisionRelated;
        }
    }
}

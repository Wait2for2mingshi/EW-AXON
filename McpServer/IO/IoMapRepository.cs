using ClosedXML.Excel;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;

namespace EW_Assistant.Io
{
    public sealed class IoEntry
    {
        public required string Name { get; init; }
        public int Index { get; init; }                 // 1-based（组内序号）
        public string CloseAddress { get; set; } = "";  // e.g. 30000
        public string OpenAddress { get; set; } = "";  // e.g. 30001

        public int CheckIndex { get; init; }       // 1..16（LSB 编号：1=最低位，16=最高位）
        public string CheckAddress { get; init; } = ""; // e.g. 30100/20/40/… + 每16个+1
    }

    public static class IoMapRepository
    {
        // 仅两组：列1、列9
        //          nameCol  close    open     checkBase
        private static readonly (int nameCol, string close, string open, int checkBase)[] GROUPS = new[]
        {
            (1,  "30000", "30001", 30100), // 列1：关=30000，开=30001，读回起始=30100
            (9,  "30003", "30002", 30140), // 列9：关=30002，开=30003，读回起始=30140
        };

        private static readonly ConcurrentDictionary<string, IoEntry> _byName =
            new(StringComparer.OrdinalIgnoreCase);

        public static int Count => _byName.Count;

        public static bool TryGetEntry(string name, out IoEntry? entry)
            => _byName.TryGetValue((name ?? "").Trim(), out entry);

        // 兼容旧接口：name -> index
        public static bool TryGetIndex(string name, out int index)
        {
            if (TryGetEntry(name, out var e) && e != null) { index = e.Index; return true; }
            index = -1; return false;
        }

        public static IEnumerable<KeyValuePair<string, int>> Snapshot()
            => _byName.Select(kv => new KeyValuePair<string, int>(kv.Key, kv.Value.Index))
                      .OrderBy(kv => kv.Value);

        public static IEnumerable<IoEntry> SnapshotByName()
            => _byName.OrderBy(kv => kv.Value.Index).Select(kv => kv.Value);

        public static void LoadFromXlsx(string xlsxPath, string? sheetName = null, int headerRow = 1)
        {
            if (!File.Exists(xlsxPath)) throw new FileNotFoundException(xlsxPath);

            // 允许其他进程同时打开 Excel
            using var stream = new FileStream(xlsxPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var wb = new XLWorkbook(stream);
            var ws = string.IsNullOrWhiteSpace(sheetName) ? wb.Worksheets.FirstOrDefault()
                                                          : wb.Worksheet(sheetName);
            if (ws == null) throw new InvalidDataException("未找到工作表");

            var used = ws.RangeUsed();
            var lastRow = used?.RangeAddress.LastAddress.RowNumber
                          ?? ws.LastRowUsed()?.RowNumber() ?? 0;
            if (lastRow <= headerRow) throw new InvalidDataException("表中无数据或仅表头");

            _byName.Clear();

            // 仅跑两组：列1/列9
            foreach (var g in GROUPS)
            {
                int nextIndex = 1; // 每组内从 1 开始

                for (int r = headerRow + 1; r <= lastRow; r++)
                {
                    var name = ws.Cell(r, g.nameCol).GetString()?.Trim().Trim('"').Trim('\uFEFF');
                    if (string.IsNullOrEmpty(name)) continue;
                    if (_byName.ContainsKey(name)) continue;  // 去重

                    // 每 16 个 IO，检查地址 +1
                    int checkAddr = g.checkBase + ((nextIndex - 1) / 16);
                    int checkIndex = ((nextIndex - 1) % 16) + 1;   // 1..16

                    var entry = new IoEntry
                    {
                        Name = name,
                        Index = nextIndex++,
                        CloseAddress = g.close,
                        OpenAddress = g.open,
                        CheckAddress = checkAddr.ToString(),
                        CheckIndex = checkIndex
                    };

                    _byName[name] = entry;
                }
            }
        }
    }
}

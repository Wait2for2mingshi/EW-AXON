using System;

namespace EW_Assistant.Domain.Reports
{
    /// <summary>
    /// 报表的基本元数据，仅包含索引信息，不包含正文内容。
    /// </summary>
    public class ReportInfo
    {
        public ReportType Type { get; set; }

        /// <summary>便于内部识别与去重的唯一标识，例如 DailyProd_2025-12-03。</summary>
        public string Id { get; set; }

        /// <summary>类型对应的中文展示名。</summary>
        public string TypeDisplayName { get; set; }

        /// <summary>UI 展示用标题，例如“当日产能报表（2025-12-03）”。</summary>
        public string Title { get; set; }

        /// <summary>日期或区间的展示文本，便于列表展示。</summary>
        public string DateLabel { get; set; }

        /// <summary>日报日期。</summary>
        public DateTime? Date { get; set; }

        /// <summary>周报起始日期。</summary>
        public DateTime? StartDate { get; set; }

        /// <summary>周报结束日期。</summary>
        public DateTime? EndDate { get; set; }

        /// <summary>报表对应的完整文件路径。</summary>
        public string FilePath { get; set; }

        /// <summary>文件生成时间，默认使用 LastWriteTime。</summary>
        public DateTime GeneratedAt { get; set; }

        /// <summary>是否为当天报表（仅对日报有效）。</summary>
        public bool IsToday { get; set; }

        /// <summary>文件大小（字节）。</summary>
        public long FileSize { get; set; }

        /// <summary>文件名，方便 UI 绑定。</summary>
        public string FileName { get; set; }

        /// <summary>格式化后的文件大小文本。</summary>
        public string FileSizeText { get; set; }
    }
}

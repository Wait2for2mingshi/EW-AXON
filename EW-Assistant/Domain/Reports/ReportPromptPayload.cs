using System;

namespace EW_Assistant.Domain.Reports
{
    /// <summary>
    /// 报表 Prompt 载体，便于向 Dify 传递 REPORT_TASK 与 REPORT_DATA_JSON。
    /// </summary>
    public class ReportPromptPayload
    {
        public string ReportTask { get; set; }

        public string ReportDataJson { get; set; }

        /// <summary>
        /// 兼容性字符串，将两段内容拼接为一份 User Prompt。
        /// </summary>
        public string CombinedPrompt { get; set; }
    }
}

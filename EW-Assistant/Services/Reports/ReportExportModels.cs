using System.Collections.Generic;
using EW_Assistant.Domain.Reports;

namespace EW_Assistant.Services.Reports
{
    internal class ReportExportBundle
    {
        public ReportInfo Info { get; set; }
        public StructuredReportPreview Preview { get; set; }
        public string SourceMarkdown { get; set; }
        public string AnalysisMarkdown { get; set; }
        public string DataJson { get; set; }
        public IList<ReportChartAsset> ChartAssets { get; set; }
        public string StandaloneHtml { get; set; }
        public string PackageHtml { get; set; }
    }

    internal class ReportChartAsset
    {
        public string FileName { get; set; }
        public ReportChartDefinition Chart { get; set; }
        public string Svg { get; set; }
    }
}

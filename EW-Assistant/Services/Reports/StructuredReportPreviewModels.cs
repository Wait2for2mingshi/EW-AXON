using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Windows.Media;

namespace EW_Assistant.Services.Reports
{
    public class StructuredReportPreview
    {
        public StructuredReportPreview()
        {
            Kpis = new List<ReportPreviewKpi>();
            Notes = new List<string>();
        }

        public IList<ReportPreviewKpi> Kpis { get; set; }
        public ReportChartDefinition PrimaryChart { get; set; }
        public ReportChartDefinition SecondaryChart { get; set; }
        public ReportChartDefinition TertiaryChart { get; set; }
        public string DetailTitle { get; set; }
        public DataTable DetailTable { get; set; }
        public string AnalysisMarkdown { get; set; }
        public IList<string> Notes { get; set; }
    }

    public class ReportPreviewKpi
    {
        public string Title { get; set; }
        public string Value { get; set; }
        public string Hint { get; set; }
        public Brush AccentBrush { get; set; }
    }

    public enum ReportChartKind
    {
        Bar,
        StackedBar,
        Line,
        Donut
    }

    public enum ReportChartValueFormat
    {
        Number,
        Percent,
        DurationMinutes
    }

    public class ReportChartDefinition
    {
        public ReportChartDefinition()
        {
            Labels = new List<string>();
            Series = new List<ReportChartSeriesDefinition>();
            DataPoints = new List<ReportChartDataPoint>();
        }

        public string Title { get; set; }
        public string Subtitle { get; set; }
        public ReportChartKind Kind { get; set; }
        public ReportChartValueFormat ValueFormat { get; set; }
        public IList<string> Labels { get; set; }
        public IList<ReportChartSeriesDefinition> Series { get; set; }
        public IList<ReportChartDataPoint> DataPoints { get; set; }
        public string CenterText { get; set; }
        public string CenterSubtext { get; set; }
        public string EmptyHint { get; set; }

        public bool HasData
        {
            get
            {
                return Series != null &&
                       Series.Any(s => s != null && s.Values != null && s.Values.Any(v => Math.Abs(v) > 0.0001d));
            }
        }

        public bool IsInteractive
        {
            get
            {
                return DataPoints != null && DataPoints.Count > 0;
            }
        }
    }

    public class ReportChartSeriesDefinition
    {
        public ReportChartSeriesDefinition()
        {
            Values = new List<double>();
        }

        public string Name { get; set; }
        public string ColorHex { get; set; }
        public Brush LegendBrush { get; set; }
        public IList<double> Values { get; set; }
    }
}

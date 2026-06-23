using System.Collections.Generic;
using System.Data;
using System.Windows.Media;

namespace EW_Assistant.Services.Reports
{
    public class ReportChartMetricItem
    {
        public string Name { get; set; }
        public string Value { get; set; }
        public Brush AccentBrush { get; set; }
    }

    public class ReportChartDataPoint
    {
        public ReportChartDataPoint()
        {
            Metrics = new List<ReportChartMetricItem>();
        }

        public string Label { get; set; }
        public string DetailTitle { get; set; }
        public string Summary { get; set; }
        public IList<ReportChartMetricItem> Metrics { get; set; }
        public DataTable DetailTable { get; set; }

        public bool HasMetrics => Metrics != null && Metrics.Count > 0;
        public bool HasDetailTable => DetailTable != null && DetailTable.Rows.Count > 0;
    }
}

using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Windows;
using EW_Assistant.Services;
using EW_Assistant.Services.Reports;

namespace EW_Assistant.Views.Reports
{
    public partial class ReportChartDrilldownWindow : Window
    {
        public ReportChartDrilldownWindow(string reportTitle, ReportChartDataPoint dataPoint)
        {
            InitializeComponent();
            ViewModel = new ReportChartDrilldownViewModel(reportTitle, dataPoint);
            DataContext = ViewModel;
            Title = string.IsNullOrWhiteSpace(ViewModel.WindowTitle) ? L("图表详情") : ViewModel.WindowTitle;
        }

        public ReportChartDrilldownViewModel ViewModel { get; }

        private static string L(string chineseText)
        {
            return UiLanguageService.CurrentText(chineseText);
        }

        public sealed class ReportChartDrilldownViewModel
        {
            public ReportChartDrilldownViewModel(string reportTitle, ReportChartDataPoint dataPoint)
            {
                var safePoint = dataPoint ?? new ReportChartDataPoint();
                WindowTitle = !string.IsNullOrWhiteSpace(safePoint.DetailTitle) ? safePoint.DetailTitle : L("图表详情");
                Subtitle = string.IsNullOrWhiteSpace(reportTitle)
                    ? safePoint.Label ?? string.Empty
                    : reportTitle + (string.IsNullOrWhiteSpace(safePoint.Label) ? string.Empty : " · " + safePoint.Label);
                Summary = safePoint.Summary ?? string.Empty;
                Metrics = (safePoint.Metrics ?? new List<ReportChartMetricItem>()).ToList();
                DetailTableView = safePoint.DetailTable != null ? safePoint.DetailTable.DefaultView : null;
            }

            public string WindowTitle { get; }
            public string Subtitle { get; }
            public string Summary { get; }
            public IList<ReportChartMetricItem> Metrics { get; }
            public DataView DetailTableView { get; }

            public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);
            public bool HasMetrics => Metrics != null && Metrics.Count > 0;
            public bool HasDetailTable => DetailTableView != null && DetailTableView.Count > 0;
        }
    }
}

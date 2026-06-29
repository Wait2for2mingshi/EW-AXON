using EW_Assistant.Services.PreventiveMaintenance;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace EW_Assistant.Views
{
    public partial class PreventiveMaintenanceView : UserControl, INotifyPropertyChanged
    {
        private readonly PreventiveMaintenanceAnalyzer _analyzer = new PreventiveMaintenanceAnalyzer();
        private readonly PreventiveMaintenanceAiService _aiService = new PreventiveMaintenanceAiService();
        private CancellationTokenSource _loadCts;
        private PreventiveMaintenanceReport _currentReport;
        private bool _hasLoaded;
        private bool _isBusy;
        private string _statusText = "等待分析";
        private string _aiSummaryMarkdown = "等待生成维护建议。";
        private string _riskScoreText = "-";
        private string _riskLevelText = "-";
        private string _alarmCountText = "-";
        private string _alarmDeltaText = "-";
        private string _downtimeText = "-";
        private string _downtimeDeltaText = "-";
        private string _yieldText = "-";
        private string _yieldDeltaText = "-";

        public ObservableCollection<PreventiveRiskItem> RiskItems { get; } = new ObservableCollection<PreventiveRiskItem>();
        public ObservableCollection<PreventiveDailyTrend> DailyTrends { get; } = new ObservableCollection<PreventiveDailyTrend>();
        public ObservableCollection<PreventiveCategoryTrend> CategoryTrends { get; } = new ObservableCollection<PreventiveCategoryTrend>();

        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                if (_isBusy == value) return;
                _isBusy = value;
                OnPropertyChanged();
            }
        }

        public string StatusText
        {
            get => _statusText;
            set
            {
                if (_statusText == value) return;
                _statusText = value ?? string.Empty;
                OnPropertyChanged();
            }
        }

        public string AiSummaryMarkdown
        {
            get => _aiSummaryMarkdown;
            set
            {
                if (_aiSummaryMarkdown == value) return;
                _aiSummaryMarkdown = value ?? string.Empty;
                OnPropertyChanged();
            }
        }

        public string RiskScoreText
        {
            get => _riskScoreText;
            set { if (_riskScoreText != value) { _riskScoreText = value ?? string.Empty; OnPropertyChanged(); } }
        }

        public string RiskLevelText
        {
            get => _riskLevelText;
            set { if (_riskLevelText != value) { _riskLevelText = value ?? string.Empty; OnPropertyChanged(); } }
        }

        public string AlarmCountText
        {
            get => _alarmCountText;
            set { if (_alarmCountText != value) { _alarmCountText = value ?? string.Empty; OnPropertyChanged(); } }
        }

        public string AlarmDeltaText
        {
            get => _alarmDeltaText;
            set { if (_alarmDeltaText != value) { _alarmDeltaText = value ?? string.Empty; OnPropertyChanged(); } }
        }

        public string DowntimeText
        {
            get => _downtimeText;
            set { if (_downtimeText != value) { _downtimeText = value ?? string.Empty; OnPropertyChanged(); } }
        }

        public string DowntimeDeltaText
        {
            get => _downtimeDeltaText;
            set { if (_downtimeDeltaText != value) { _downtimeDeltaText = value ?? string.Empty; OnPropertyChanged(); } }
        }

        public string YieldText
        {
            get => _yieldText;
            set { if (_yieldText != value) { _yieldText = value ?? string.Empty; OnPropertyChanged(); } }
        }

        public string YieldDeltaText
        {
            get => _yieldDeltaText;
            set { if (_yieldDeltaText != value) { _yieldDeltaText = value ?? string.Empty; OnPropertyChanged(); } }
        }

        public PreventiveMaintenanceView()
        {
            InitializeComponent();
            DataContext = this;
            Loaded += PreventiveMaintenanceView_Loaded;
            Unloaded += PreventiveMaintenanceView_Unloaded;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private async void PreventiveMaintenanceView_Loaded(object sender, RoutedEventArgs e)
        {
            if (_hasLoaded)
            {
                return;
            }

            _hasLoaded = true;
            await ReloadAsync(true).ConfigureAwait(true);
        }

        private void PreventiveMaintenanceView_Unloaded(object sender, RoutedEventArgs e)
        {
            CancelCurrentLoad();
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await ReloadAsync(true).ConfigureAwait(true);
        }

        private async void WindowCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_hasLoaded)
            {
                return;
            }

            await ReloadAsync(true).ConfigureAwait(true);
        }

        private async Task ReloadAsync(bool runAi)
        {
            CancelCurrentLoad();
            _loadCts = new CancellationTokenSource();
            var token = _loadCts.Token;
            var days = GetSelectedWindowDays();

            try
            {
                IsBusy = true;
                StatusText = $"正在分析最近 {days} 天报警与产能数据...";
                AiSummaryMarkdown = "正在生成规则摘要...";

                var report = await Task.Run(() => _analyzer.Analyze(days), token).ConfigureAwait(true);
                token.ThrowIfCancellationRequested();
                ApplyReport(report);

                AiSummaryMarkdown = BuildLocalMarkdown(report);
                if (runAi)
                {
                    StatusText = $"规则分析完成，正在调用 AI 生成维护建议...";
                    var ai = await _aiService.AnalyzeAsync(report, token).ConfigureAwait(true);
                    token.ThrowIfCancellationRequested();
                    if (ai.IsSuccess && !string.IsNullOrWhiteSpace(ai.Markdown))
                    {
                        AiSummaryMarkdown = ai.Markdown;
                        StatusText = $"已完成 AI 预防维护分析，数据锚点 {report.AnchorDate:yyyy-MM-dd}";
                    }
                    else
                    {
                        AiSummaryMarkdown = BuildLocalMarkdown(report) + Environment.NewLine + Environment.NewLine
                            + $"**AI 状态**：{ai.ErrorMessage}";
                        StatusText = $"已完成本地规则分析，AI 总结未生成，数据锚点 {report.AnchorDate:yyyy-MM-dd}";
                    }
                }
                else
                {
                    StatusText = $"已完成本地规则分析，数据锚点 {report.AnchorDate:yyyy-MM-dd}";
                }
            }
            catch (OperationCanceledException)
            {
                // view unloaded or another refresh started
            }
            catch (Exception ex)
            {
                StatusText = "预防维护分析失败：" + ex.Message;
                AiSummaryMarkdown = "分析失败：" + ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void ApplyReport(PreventiveMaintenanceReport report)
        {
            _currentReport = report;

            RiskItems.Clear();
            foreach (var item in report.RiskItems)
            {
                RiskItems.Add(item);
            }

            DailyTrends.Clear();
            foreach (var item in report.DailyTrends)
            {
                DailyTrends.Add(item);
            }

            CategoryTrends.Clear();
            foreach (var item in report.CategoryTrends)
            {
                CategoryTrends.Add(item);
            }

            RiskScoreText = report.OverallRiskScore + " 分";
            RiskLevelText = report.OverallRiskLevel;
            AlarmCountText = report.CurrentAlarmCount + " 次";
            AlarmDeltaText = FormatDelta(report.CurrentAlarmCount - report.BaselineAlarmCount, "较对比窗口");
            DowntimeText = report.CurrentDowntimeMinutes.ToString("F1") + " 分钟";
            DowntimeDeltaText = FormatDelta(report.CurrentDowntimeMinutes - report.BaselineDowntimeMinutes, "较对比窗口", "F1");
            YieldText = report.CurrentYield <= 0d ? "-" : report.CurrentYield.ToString("P2");
            YieldDeltaText = report.BaselineYield <= 0d
                ? "对比窗口无产能"
                : FormatDelta((report.CurrentYield - report.BaselineYield) * 100d, "较对比窗口", "F2") + " 个百分点";
        }

        private int GetSelectedWindowDays()
        {
            if (WindowCombo?.SelectedItem is ComboBoxItem item
                && item.Tag != null
                && int.TryParse(item.Tag.ToString(), out var days)
                && days > 0)
            {
                return days;
            }

            return 7;
        }

        private void CancelCurrentLoad()
        {
            try
            {
                _loadCts?.Cancel();
                _loadCts?.Dispose();
            }
            catch
            {
                // ignore cancel errors
            }
            finally
            {
                _loadCts = null;
            }
        }

        private static string BuildLocalMarkdown(PreventiveMaintenanceReport report)
        {
            if (report == null)
            {
                return "暂无分析结果。";
            }

            var text = new System.Text.StringBuilder();
            text.AppendLine("## 规则分析结论");
            text.AppendLine();
            text.AppendLine($"分析窗口：{report.CurrentStart:yyyy-MM-dd} ~ {report.CurrentEnd:yyyy-MM-dd}");
            text.AppendLine();
            text.AppendLine($"对比窗口：{report.BaselineStart:yyyy-MM-dd} ~ {report.BaselineEnd:yyyy-MM-dd}");
            text.AppendLine();
            text.AppendLine($"综合风险：**{report.OverallRiskLevel} / {report.OverallRiskScore} 分**");
            text.AppendLine();
            text.AppendLine($"报警次数：{report.CurrentAlarmCount} 次，对比 {report.BaselineAlarmCount} 次");
            text.AppendLine();
            text.AppendLine($"报警时长：{report.CurrentDowntimeMinutes:F1} 分钟，对比 {report.BaselineDowntimeMinutes:F1} 分钟");
            text.AppendLine();
            text.AppendLine($"当前良率：{(report.CurrentYield <= 0d ? "-" : report.CurrentYield.ToString("P2"))}");
            text.AppendLine();
            text.AppendLine("## 本地维护建议");
            text.AppendLine();
            text.AppendLine(report.OverallSummary);

            if (report.RiskItems.Count > 0)
            {
                text.AppendLine();
                text.AppendLine("## 重点风险项");
                for (int i = 0; i < report.RiskItems.Count; i++)
                {
                    var item = report.RiskItems[i];
                    text.AppendLine();
                    text.AppendLine($"### 风险项 {i + 1}：{item.Code}");
                    text.AppendLine();
                    text.AppendLine($"风险等级：**{item.RiskLevel} / {item.RiskScore} 分**");
                    text.AppendLine();
                    text.AppendLine($"报警内容：{item.Message}");
                    text.AppendLine();
                    text.AppendLine($"风险原因：{item.ReasonSummary}。");
                    text.AppendLine();
                    text.AppendLine($"建议点检：{item.SuggestedChecks}");
                }
            }

            return text.ToString();
        }

        private static string FormatDelta(int delta, string prefix)
        {
            if (delta > 0) return $"{prefix} +{delta}";
            if (delta < 0) return $"{prefix} {delta}";
            return $"{prefix} 持平";
        }

        private static string FormatDelta(double delta, string prefix, string format)
        {
            if (Math.Abs(delta) < 0.0001d) return $"{prefix} 持平";
            if (delta > 0) return $"{prefix} +{delta.ToString(format)}";
            return $"{prefix} {delta.ToString(format)}";
        }

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

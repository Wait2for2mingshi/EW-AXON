using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using EW_Assistant.Domain.Reports;
using EW_Assistant.Services;
using EW_Assistant.Services.Reports;

namespace EW_Assistant.ViewModels
{
    public enum ReportRegenerateOutcome
    {
        Generated,
        Skipped,
        Failed
    }

    /// <summary>
    /// 报表中心视图模型，负责绑定本地报表索引与结构化图文预览。
    /// </summary>
    public class ReportsCenterViewModel : ViewModelBase
    {
        private readonly ReportStorageService _storage;
        private readonly ReportGeneratorService _generator;
        private readonly StructuredReportPreviewBuilder _previewBuilder;
        private readonly ReportExportService _exporter;
        private CancellationTokenSource _loadReportsCts;
        private CancellationTokenSource _loadPreviewCts;
        private int _reportLoadVersion;
        private int _previewLoadVersion;
        private ReportType _currentReportType;
        private string _previewMarkdown;
        private ReportInfo _selectedReport;
        private bool _isBusy;
        private ReportChartDefinition _primaryChart;
        private ReportChartDefinition _secondaryChart;
        private ReportChartDefinition _tertiaryChart;
        private string _detailTitle;
        private DataView _detailTableView;

        public ObservableCollection<ReportInfo> Reports { get; private set; }
        public ObservableCollection<ReportPreviewKpi> PreviewKpis { get; private set; }
        public ObservableCollection<string> PreviewNotes { get; private set; }
        public ObservableCollection<string> PreviewSummaryPoints { get; private set; }

        /// <summary>当前选中的报表类型。</summary>
        public ReportType CurrentReportType
        {
            get { return _currentReportType; }
            set { SetProperty(ref _currentReportType, value); }
        }

        /// <summary>当前选中的报表项。</summary>
        public ReportInfo SelectedReport
        {
            get { return _selectedReport; }
            set
            {
                if (SetProperty(ref _selectedReport, value))
                {
                    _ = LoadSelectedReportContentAsync(value);
                }
            }
        }

        /// <summary>AI 分析正文 Markdown。</summary>
        public string PreviewMarkdown
        {
            get { return _previewMarkdown; }
            set { SetProperty(ref _previewMarkdown, value); }
        }

        /// <summary>生成或切换时的忙碌状态。</summary>
        public bool IsBusy
        {
            get { return _isBusy; }
            set { SetProperty(ref _isBusy, value); }
        }

        public ReportChartDefinition PrimaryChart
        {
            get { return _primaryChart; }
            set
            {
                if (SetProperty(ref _primaryChart, value))
                {
                    OnPropertyChanged(nameof(HasPrimaryChart));
                }
            }
        }

        public ReportChartDefinition SecondaryChart
        {
            get { return _secondaryChart; }
            set
            {
                if (SetProperty(ref _secondaryChart, value))
                {
                    OnPropertyChanged(nameof(HasSecondaryChart));
                }
            }
        }

        public ReportChartDefinition TertiaryChart
        {
            get { return _tertiaryChart; }
            set
            {
                if (SetProperty(ref _tertiaryChart, value))
                {
                    OnPropertyChanged(nameof(HasTertiaryChart));
                }
            }
        }

        public string DetailTitle
        {
            get { return _detailTitle; }
            set { SetProperty(ref _detailTitle, value); }
        }

        public DataView DetailTableView
        {
            get { return _detailTableView; }
            set
            {
                if (SetProperty(ref _detailTableView, value))
                {
                    OnPropertyChanged(nameof(HasDetailTable));
                }
            }
        }

        public bool HasPrimaryChart
        {
            get { return PrimaryChart != null; }
        }

        public bool HasSecondaryChart
        {
            get { return SecondaryChart != null; }
        }

        public bool HasTertiaryChart
        {
            get { return TertiaryChart != null; }
        }

        public bool HasDetailTable
        {
            get { return DetailTableView != null && DetailTableView.Count > 0; }
        }

        public bool HasPreviewNotes
        {
            get { return PreviewNotes != null && PreviewNotes.Count > 0; }
        }

        public bool HasPreviewSummaryPoints
        {
            get { return PreviewSummaryPoints != null && PreviewSummaryPoints.Count > 0; }
        }

        public ReportsCenterViewModel()
        {
            _storage = new ReportStorageService();
            _generator = new ReportGeneratorService(_storage, new LlmWorkflowClient());
            _previewBuilder = new StructuredReportPreviewBuilder(_storage);
            _exporter = new ReportExportService(_storage, _previewBuilder);
            Reports = new ObservableCollection<ReportInfo>();
            PreviewKpis = new ObservableCollection<ReportPreviewKpi>();
            PreviewNotes = new ObservableCollection<string>();
            PreviewSummaryPoints = new ObservableCollection<string>();
            DetailTitle = L("明细数据");
            _ = SwitchReportTypeAsync(ReportType.DailyProd);
        }

        private static string L(string chineseText)
        {
            return UiLanguageService.CurrentText(chineseText);
        }

        /// <summary>
        /// 切换报表类型并重新生成索引列表。
        /// </summary>
        public Task SwitchReportTypeAsync(ReportType type)
        {
            CurrentReportType = type;
            return LoadReportsAsync(type);
        }

        /// <summary>人工触发：按当前选中项重新生成。</summary>
        public async Task<ReportRegenerateOutcome> RegenerateAsync(ReportInfo info)
        {
            if (info == null) return ReportRegenerateOutcome.Failed;
            if (IsBusy) return ReportRegenerateOutcome.Failed;

            IsBusy = true;
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            try
            {
                var generated = await GenerateByReportInfoAsync(info, cts.Token, true);
                await ReloadReportsAndReselectAsync(info.Type, generated);
                return generated != null ? ReportRegenerateOutcome.Generated : ReportRegenerateOutcome.Skipped;
            }
            catch (OperationCanceledException)
            {
                ResetPreview(L("重生成超时或已取消。"));
                return ReportRegenerateOutcome.Failed;
            }
            catch (ReportGenerationException ex)
            {
                ResetPreview(ex.Message);
                return ReportRegenerateOutcome.Failed;
            }
            catch (Exception ex)
            {
                ResetPreview(L("重生成报表失败：") + ex.Message);
                return ReportRegenerateOutcome.Failed;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task LoadReportsAsync(ReportType type)
        {
            var version = Interlocked.Increment(ref _reportLoadVersion);
            var cts = ReplaceCancellationTokenSource(ref _loadReportsCts);
            var token = cts.Token;

            CancelPreviewLoad();
            ReplaceItems(Reports, null);
            SelectedReport = null;
            ResetPreview(L("正在加载报表列表，请稍候..."));

            try
            {
                var reports = await Task.Run(() => _storage.GetReportsByType(type), token);
                if (token.IsCancellationRequested || version != _reportLoadVersion || CurrentReportType != type)
                {
                    return;
                }

                ReplaceItems(Reports, reports);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested || version != _reportLoadVersion || CurrentReportType != type)
                {
                    return;
                }

                ResetPreview(L("扫描报表目录时出错：") + ex.Message);
                return;
            }

            if (Reports.Count > 0)
            {
                SelectedReport = Reports[0];
            }
            else
            {
                SelectedReport = null;
                ResetPreview(L("当前类型暂无报表文件。"));
            }
        }

        private async Task LoadSelectedReportContentAsync(ReportInfo info)
        {
            var version = Interlocked.Increment(ref _previewLoadVersion);
            var cts = ReplaceCancellationTokenSource(ref _loadPreviewCts);
            var token = cts.Token;

            if (info == null)
            {
                ResetPreview(L("请选择左侧报表以查看图文预览。"));
                return;
            }

            ResetPreview(L("正在加载报表预览，请稍候..."), info.Title);

            try
            {
                var preview = await Task.Run(() => _previewBuilder.Build(info), token);
                if (token.IsCancellationRequested || version != _previewLoadVersion || !IsCurrentSelection(info))
                {
                    return;
                }

                ApplyPreview(preview);
            }
            catch (OperationCanceledException)
            {
                // 选择切换时静默取消旧任务
            }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested || version != _previewLoadVersion || !IsCurrentSelection(info))
                {
                    return;
                }

                ResetPreview(L("读取报表失败：") + ex.Message, info.Title);
            }
        }

        private void ApplyPreview(StructuredReportPreview preview)
        {
            ReplaceItems(PreviewKpis, preview?.Kpis);
            ReplaceItems(PreviewNotes, preview?.Notes?.Where(x => !string.IsNullOrWhiteSpace(x)));
            ReplaceItems(PreviewSummaryPoints, BuildSummaryPoints(preview));

            PreviewMarkdown = !string.IsNullOrWhiteSpace(preview?.AnalysisMarkdown)
                ? preview.AnalysisMarkdown
                : BuildPlaceholderMarkdown(L("当前报表暂无 AI 分析正文。"));
            PrimaryChart = preview?.PrimaryChart;
            SecondaryChart = preview?.SecondaryChart;
            TertiaryChart = preview?.TertiaryChart;
            DetailTitle = !string.IsNullOrWhiteSpace(preview?.DetailTitle) ? preview.DetailTitle : L("明细数据");
            DetailTableView = preview?.DetailTable != null ? preview.DetailTable.DefaultView : null;

            OnPropertyChanged(nameof(HasPreviewNotes));
            OnPropertyChanged(nameof(HasPreviewSummaryPoints));
        }

        private void ResetPreview(string message, string title = null)
        {
            ReplaceItems(PreviewKpis, null);
            ReplaceItems(PreviewNotes, null);
            ReplaceItems(PreviewSummaryPoints, new[] { string.IsNullOrWhiteSpace(message) ? L("暂无摘要内容。") : message });
            PreviewMarkdown = BuildPlaceholderMarkdown(message, title);
            PrimaryChart = null;
            SecondaryChart = null;
            TertiaryChart = null;
            DetailTitle = L("明细数据");
            DetailTableView = null;
            OnPropertyChanged(nameof(HasPreviewNotes));
            OnPropertyChanged(nameof(HasPreviewSummaryPoints));
        }

        private string BuildPlaceholderMarkdown(string message, string title = null)
        {
            var typeName = GetCurrentTypeDisplayName();
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(title))
            {
                sb.AppendLine("### " + title);
                sb.AppendLine();
            }
            sb.AppendLine("> " + message);
            sb.AppendLine();
            sb.AppendLine("- " + L("报表类型：") + (typeName ?? L("未选择")));
            sb.AppendLine("- " + L("更新时间：") + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            return sb.ToString();
        }

        /// <summary>
        /// 导出报表文件，失败时返回错误信息。
        /// </summary>
        public bool TryExportReport(ReportInfo info, ReportExportFormat format, string targetPath, out string errorMessage)
        {
            errorMessage = string.Empty;
            if (info == null)
            {
                errorMessage = L("未选择报表。");
                return false;
            }

            if (string.IsNullOrWhiteSpace(targetPath))
            {
                errorMessage = L("未指定导出路径。");
                return false;
            }

            try
            {
                _exporter.Export(info, format, targetPath);
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        public Task<StructuredReportPreview> BuildPreviewSnapshotAsync(ReportInfo info)
        {
            if (info == null)
            {
                return Task.FromResult<StructuredReportPreview>(null);
            }

            return Task.Run(() => _previewBuilder.Build(info));
        }

        private async Task<ReportInfo> GenerateByReportInfoAsync(ReportInfo info, CancellationToken token, bool force)
        {
            if (info == null)
            {
                return null;
            }

            switch (info.Type)
            {
                case ReportType.DailyProd:
                    return await _generator.GenerateDailyProdAsync(info.Date ?? DateTime.Today, token, force);
                case ReportType.DailyAlarm:
                    return await _generator.GenerateDailyAlarmAsync(info.Date ?? DateTime.Today, token, force);
                case ReportType.WeeklyProd:
                    return await _generator.GenerateWeeklyProdAsync(info.EndDate ?? DateTime.Today, token, force);
                case ReportType.WeeklyAlarm:
                    return await _generator.GenerateWeeklyAlarmAsync(info.EndDate ?? DateTime.Today, token, force);
                default:
                    return null;
            }
        }

        private async Task ReloadReportsAndReselectAsync(ReportType type, ReportInfo generated)
        {
            await LoadReportsAsync(type);
            if (generated == null)
            {
                return;
            }

            var target = FindMatchingReport(generated);
            if (target != null)
            {
                SelectedReport = target;
            }
        }

        private ReportInfo FindMatchingReport(ReportInfo generated)
        {
            if (generated == null || Reports == null || Reports.Count == 0)
            {
                return null;
            }

            return Reports.FirstOrDefault(r =>
                (!string.IsNullOrWhiteSpace(generated.Id) && string.Equals(r.Id, generated.Id, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(generated.FilePath) && string.Equals(r.FilePath, generated.FilePath, StringComparison.OrdinalIgnoreCase)));
        }

        private bool IsCurrentSelection(ReportInfo info)
        {
            if (info == null || SelectedReport == null)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(info.FilePath) && !string.IsNullOrWhiteSpace(SelectedReport.FilePath))
            {
                return string.Equals(info.FilePath, SelectedReport.FilePath, StringComparison.OrdinalIgnoreCase);
            }

            if (!string.IsNullOrWhiteSpace(info.Id) && !string.IsNullOrWhiteSpace(SelectedReport.Id))
            {
                return string.Equals(info.Id, SelectedReport.Id, StringComparison.OrdinalIgnoreCase);
            }

            return ReferenceEquals(info, SelectedReport);
        }

        private void CancelPreviewLoad()
        {
            var previous = Interlocked.Exchange(ref _loadPreviewCts, null);
            Interlocked.Increment(ref _previewLoadVersion);
            if (previous == null)
            {
                return;
            }

            try
            {
                previous.Cancel();
            }
            catch
            {
                // ignore
            }
            finally
            {
                previous.Dispose();
            }
        }

        private static CancellationTokenSource ReplaceCancellationTokenSource(ref CancellationTokenSource field)
        {
            var replacement = new CancellationTokenSource();
            var previous = Interlocked.Exchange(ref field, replacement);
            if (previous == null)
            {
                return replacement;
            }

            try
            {
                previous.Cancel();
            }
            catch
            {
                // ignore
            }
            finally
            {
                previous.Dispose();
            }

            return replacement;
        }

        private string GetCurrentTypeDisplayName()
        {
            return _storage != null ? L(_storage.GetTypeDisplayName(CurrentReportType)) : null;
        }

        private IList<string> BuildSummaryPoints(StructuredReportPreview preview)
        {
            var result = new List<string>();

            AppendSummaryCandidates(result, ExtractSummaryLines(preview?.AnalysisMarkdown), 3);
            AppendSummaryCandidates(result, preview?.Notes, 3);

            if (result.Count < 3 && preview?.Kpis != null)
            {
                AppendSummaryCandidates(
                    result,
                    preview.Kpis
                        .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Hint))
                        .Select(x => x.Title + "：" + x.Hint),
                    3);
            }

            if (result.Count == 0)
            {
                result.Add(L("当前报表暂无摘要内容，可双击左侧报表查看完整图文详情。"));
            }

            return result;
        }

        private static IEnumerable<string> ExtractSummaryLines(string markdown)
        {
            if (string.IsNullOrWhiteSpace(markdown))
            {
                yield break;
            }

            var lines = markdown.Replace("\r", string.Empty).Split('\n');
            foreach (var raw in lines)
            {
                var normalized = NormalizeSummaryLine(raw);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    yield return Shorten(normalized, 54);
                }
            }
        }

        private static void AppendSummaryCandidates(ICollection<string> target, IEnumerable<string> candidates, int maxCount)
        {
            if (target == null || candidates == null || maxCount <= 0)
            {
                return;
            }

            foreach (var item in candidates)
            {
                var normalized = NormalizeSummaryLine(item);
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    continue;
                }

                if (target.Contains(normalized))
                {
                    continue;
                }

                target.Add(Shorten(normalized, 54));
                if (target.Count >= maxCount)
                {
                    break;
                }
            }
        }

        private static void ReplaceItems<T>(ObservableCollection<T> target, IEnumerable<T> items)
        {
            if (target == null)
            {
                return;
            }

            target.Clear();
            if (items == null)
            {
                return;
            }

            foreach (var item in items)
            {
                target.Add(item);
            }
        }

        private static string NormalizeSummaryLine(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var trimmed = raw.Trim();
            if (trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                return null;
            }

            var text = Regex.Replace(trimmed, @"^\s*(?:[-*+]\s+|\d+\.\s+|\d+\)\s+)", string.Empty);
            text = text.Replace("**", string.Empty)
                       .Replace("__", string.Empty)
                       .Replace("`", string.Empty)
                       .Replace(">", string.Empty);
            text = Regex.Replace(text, @"\[(.*?)\]\((.*?)\)", "$1");
            text = Regex.Replace(text, @"\s+", " ").Trim();

            if (string.IsNullOrWhiteSpace(text) || text == "无")
            {
                return null;
            }

            return text.Length <= 2 ? null : text;
        }

        private static string Shorten(string text, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(text) || maxLength <= 0 || text.Length <= maxLength)
            {
                return text;
            }

            return text.Substring(0, maxLength - 1) + "…";
        }
    }
}

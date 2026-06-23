using EW_Assistant.Services;
using EW_Assistant.Services.Warnings;
using EW_Assistant.Warnings;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;

namespace EW_Assistant.ViewModels
{
    /// <summary>
    /// 预警中心视图模型，负责将后台快照映射到 UI。
    /// </summary>
    public class WarningCenterViewModel : INotifyPropertyChanged
    {
        private WarningItemViewModel _selected;
        private string _aiAnalysisText = L("当前还未接入 AI 分析，下面仅展示预警基本信息。");
        private string _lastUpdatedText = string.Empty;
        private readonly Dictionary<string, WarningTicketRecord> _ticketMap =
            new Dictionary<string, WarningTicketRecord>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _analysisMap =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<WarningTicketRecord> _allTickets = new List<WarningTicketRecord>();
        private readonly WarningMonitorService _service;
        private string _filterStatus = "Pending";
        private bool _isAttached;

        private static string L(string chineseText)
        {
            return UiLanguageService.CurrentText(chineseText);
        }

        private static bool IsEnglishUi()
        {
            return UiLanguageService.IsEnglish(ConfigService.Current);
        }

        public ObservableCollection<WarningItemViewModel> Warnings { get; } = new ObservableCollection<WarningItemViewModel>();

        public WarningItemViewModel SelectedWarning
        {
            get => _selected;
            set
            {
                if (!Equals(_selected, value))
                {
                    _selected = value;
                    OnPropertyChanged();
                    UpdateAnalysisText();
                }
            }
        }

        public string AiAnalysisText
        {
            get => _aiAnalysisText;
            set
            {
                if (_aiAnalysisText != value)
                {
                    _aiAnalysisText = value;
                    OnPropertyChanged();
                }
            }
        }

        public string LastUpdatedText
        {
            get => _lastUpdatedText;
            set
            {
                if (_lastUpdatedText != value)
                {
                    _lastUpdatedText = value;
                    OnPropertyChanged();
                }
            }
        }

        public string FilterStatus
        {
            get => _filterStatus;
            set
            {
                if (_filterStatus != value)
                {
                    _filterStatus = value;
                    OnPropertyChanged();
                    ApplyFilterAndRender(SelectedWarning?.Key);
                }
            }
        }

        public WarningCenterViewModel()
        {
            _service = WarningMonitorService.Instance;
            RefreshFromSnapshot(_service.GetSnapshot());
            UpdateAnalysisText();
        }

        public void Attach()
        {
            if (_isAttached)
            {
                return;
            }

            _service.SnapshotUpdated += WarningMonitorService_SnapshotUpdated;
            _isAttached = true;
            RefreshFromSnapshot(_service.GetSnapshot());
        }

        public void Detach()
        {
            if (!_isAttached)
            {
                return;
            }

            _service.SnapshotUpdated -= WarningMonitorService_SnapshotUpdated;
            _isAttached = false;
        }

        /// <summary>
        /// 兼容旧调用，触发一次后台强制刷新。
        /// </summary>
        public void LoadWarningsFromCsv()
        {
            var _ = _service.RefreshNowAsync(true);
        }

        /// <summary>
        /// 兼容旧调用，触发后台补齐缺失 AI 分析。
        /// </summary>
        public Task AnalyzeMissingWarningsAsync()
        {
            return _service.AnalyzeMissingWarningsAsync();
        }

        public void MarkProcessedSelected()
        {
            if (SelectedWarning == null || string.IsNullOrWhiteSpace(SelectedWarning.Key))
            {
                return;
            }

            _service.MarkProcessed(SelectedWarning.Key);
        }

        private void WarningMonitorService_SnapshotUpdated(object sender, WarningMonitorSnapshotEventArgs e)
        {
            var snapshot = e?.Snapshot ?? WarningMonitorSnapshot.Empty;
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                RefreshFromSnapshot(snapshot);
                return;
            }

            dispatcher.BeginInvoke(new Action(() => RefreshFromSnapshot(snapshot)));
        }

        private void RefreshFromSnapshot(WarningMonitorSnapshot snapshot)
        {
            snapshot = snapshot ?? WarningMonitorSnapshot.Empty;
            var selectedKey = SelectedWarning?.Key;

            _ticketMap.Clear();
            _analysisMap.Clear();
            _allTickets.Clear();

            foreach (var ticket in snapshot.Tickets ?? new List<WarningTicketRecord>())
            {
                if (ticket == null || string.IsNullOrWhiteSpace(ticket.Fingerprint))
                {
                    continue;
                }

                _ticketMap[ticket.Fingerprint] = ticket;
                _allTickets.Add(ticket);
            }

            foreach (var pair in snapshot.Analyses ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
                {
                    continue;
                }

                _analysisMap[pair.Key] = pair.Value;
            }

            LastUpdatedText = snapshot.UpdatedAt == default(DateTime)
                ? L(snapshot.LastUpdatedText ?? string.Empty)
                : L("上次更新：") + snapshot.UpdatedAt.ToString("HH:mm:ss");

            ApplyFilterAndRender(selectedKey);
        }

        private void UpdateAnalysisText()
        {
            if (Warnings.Count == 0)
            {
                AiAnalysisText = L("最近 24 小时没有触发任何预警。");
                return;
            }

            if (SelectedWarning == null)
            {
                AiAnalysisText = L("请选择一条预警");
                return;
            }

            if (SelectedWarning.HasAiMarkdown && !string.IsNullOrEmpty(SelectedWarning.AiMarkdown))
            {
                AiAnalysisText = SelectedWarning.AiMarkdown;
            }
            else
            {
                AiAnalysisText = L("已检测到预警，但 AI 分析尚未生成，请稍后。");
            }
        }

        private static int LevelRank(string level)
        {
            switch ((level ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "critical": return 3;
                case "warning": return 2;
                case "info": return 1;
                default: return 0;
            }
        }

        private static string MapLevelDisplay(string level)
        {
            var lv = (level ?? string.Empty).Trim().ToLowerInvariant();
            switch (lv)
            {
                case "critical": return IsEnglishUi() ? "Critical" : "严重";
                case "warning": return IsEnglishUi() ? "Warning" : "警告";
                case "info": return IsEnglishUi() ? "Info" : "提示";
                default: return string.IsNullOrEmpty(level) ? (IsEnglishUi() ? "Info" : "提示") : level;
            }
        }

        private static string MapTypeDisplay(string type)
        {
            var tp = (type ?? string.Empty).Trim().ToLowerInvariant();
            switch (tp)
            {
                case "yield": return L("良率");
                case "throughput": return L("产能");
                case "alarm": return L("报警");
                case "combined": return L("综合");
                default: return string.IsNullOrEmpty(type) ? L("其他") : type;
            }
        }

        private void ApplyFilterAndRender(string preferredKey = null)
        {
            Warnings.Clear();
            var filtered = FilterTickets(_allTickets, _filterStatus)
                .OrderByDescending(t => LevelRank(t.Level))
                .ThenByDescending(t => t.LastSeen)
                .ToList();

            foreach (var ticket in filtered)
            {
                var item = ConvertToViewModel(ticket);
                if (item != null)
                {
                    Warnings.Add(item);
                }
            }

            if (Warnings.Count == 0)
            {
                SelectedWarning = null;
                UpdateAnalysisText();
                return;
            }

            WarningItemViewModel matched = null;
            if (!string.IsNullOrWhiteSpace(preferredKey))
            {
                matched = Warnings.FirstOrDefault(w =>
                    string.Equals(w.Key, preferredKey, StringComparison.OrdinalIgnoreCase));
            }

            SelectedWarning = matched ?? Warnings[0];
            UpdateAnalysisText();
        }

        private static IEnumerable<WarningTicketRecord> FilterTickets(IEnumerable<WarningTicketRecord> tickets, string filter)
        {
            var list = new List<WarningTicketRecord>();
            if (tickets == null)
            {
                return list;
            }

            var f = (filter ?? string.Empty).Trim().ToLowerInvariant();
            foreach (var ticket in tickets)
            {
                if (ticket == null)
                {
                    continue;
                }

                var status = (ticket.Status ?? string.Empty).Trim().ToLowerInvariant();
                if (status == "ignored" && ticket.IgnoredUntil.HasValue && ticket.IgnoredUntil.Value > DateTime.Now)
                {
                    continue;
                }

                if (f == "all")
                {
                    list.Add(ticket);
                    continue;
                }

                if (f == "pending")
                {
                    if (status == "active" || status == "acknowledged")
                    {
                        list.Add(ticket);
                    }
                    continue;
                }

                if (f == "active" && status == "active")
                {
                    list.Add(ticket);
                    continue;
                }

                if (f == "acknowledged" && status == "acknowledged")
                {
                    list.Add(ticket);
                    continue;
                }

                if (f == "ignored" && status == "ignored")
                {
                    list.Add(ticket);
                    continue;
                }

                if (f == "processed" && status == "processed")
                {
                    list.Add(ticket);
                    continue;
                }

                if (f == "resolved" && status == "resolved")
                {
                    list.Add(ticket);
                    continue;
                }
            }

            return list;
        }

        private WarningItemViewModel ConvertToViewModel(WarningTicketRecord ticket)
        {
            if (ticket == null)
            {
                return null;
            }

            var timeRange = ticket.StartTime.ToString("HH:mm") + " - " + ticket.EndTime.ToString("HH:mm");
            var title = ticket.RuleName;
            var isYieldMetric = string.Equals(ticket.Type ?? string.Empty, "yield", StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(ticket.MetricName) && ticket.MetricName.Contains("良率"));
            Func<double, string> formatMetric = value => isYieldMetric ? (value * 100).ToString("F1") : value.ToString("F1");

            string metricText = string.Empty;
            if (ticket.ThresholdValue.HasValue)
            {
                metricText = string.Format(L("{0} 当前 {1}，阈值 {2}"),
                    string.IsNullOrEmpty(ticket.MetricName) ? L("指标") : ticket.MetricName,
                    formatMetric(ticket.CurrentValue),
                    formatMetric(ticket.ThresholdValue.Value));
            }
            else if (ticket.CurrentValue > 0)
            {
                metricText = string.Format(L("{0} 当前 {1}"),
                    string.IsNullOrEmpty(ticket.MetricName) ? L("指标") : ticket.MetricName,
                    formatMetric(ticket.CurrentValue));
            }

            var displaySummary = BuildDisplaySummary(ticket);
            var displayStatus = MapTicketStatusDisplay(ticket.Status);
            var aiMarkdown = string.Empty;
            _analysisMap.TryGetValue(ticket.Fingerprint ?? string.Empty, out aiMarkdown);

            return new WarningItemViewModel
            {
                Key = ticket.Fingerprint,
                Level = ticket.Level ?? "Info",
                Type = ticket.Type ?? "Yield",
                LevelDisplay = MapLevelDisplay(ticket.Level),
                TypeDisplay = MapTypeDisplay(ticket.Type),
                TimeRange = timeRange,
                Title = string.IsNullOrEmpty(title) ? L("预警") : title,
                Summary = displaySummary,
                RuleName = ticket.RuleName ?? string.Empty,
                RuleId = ticket.RuleId ?? string.Empty,
                MetricName = ticket.MetricName ?? string.Empty,
                MetricText = metricText,
                CurrentValue = formatMetric(ticket.CurrentValue),
                BaselineValue = ticket.BaselineValue.HasValue ? formatMetric(ticket.BaselineValue.Value) : string.Empty,
                ThresholdValue = ticket.ThresholdValue.HasValue ? formatMetric(ticket.ThresholdValue.Value) : string.Empty,
                Status = displayStatus,
                AiMarkdown = aiMarkdown ?? string.Empty,
                HasAiMarkdown = !string.IsNullOrWhiteSpace(aiMarkdown),
                SortTime = ticket.LastSeen,
                LastDetected = ticket.LastSeen,
                OccurrenceCount = ticket.OccurrenceCount
            };
        }

        private static string BuildDisplaySummary(WarningTicketRecord ticket)
        {
            var text = ticket == null ? string.Empty : (ticket.Summary ?? string.Empty);
            if (string.IsNullOrWhiteSpace(text))
            {
                var rule = ticket == null ? L("预警") : (ticket.RuleName ?? L("预警"));
                var metric = ticket == null ? string.Empty : (ticket.MetricName ?? string.Empty);
                return string.Format(L("{0} 指标：{1}"), rule, metric).Trim();
            }

            var idx = text.IndexOf("EVIDENCE", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                text = text.Substring(0, idx);
            }

            text = text.Trim().TrimEnd('。', '，', '.');
            const int maxLen = 140;
            if (text.Length > maxLen)
            {
                text = text.Substring(0, maxLen) + "...";
            }

            return text;
        }

        private static string MapTicketStatusDisplay(string status)
        {
            var st = (status ?? string.Empty).Trim().ToLowerInvariant();
            switch (st)
            {
                case "active": return L("待处理");
                case "acknowledged": return L("已确认");
                case "ignored": return L("已忽略");
                case "processed": return L("已处理");
                case "resolved": return L("已关闭");
                default: return string.IsNullOrEmpty(status) ? L("待处理") : status;
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// UI 侧预警模型。
    /// </summary>
    public class WarningItemViewModel : INotifyPropertyChanged
    {
        private string _status;
        private string _aiMarkdown;
        private bool _hasAiMarkdown;
        private int _occurrenceCount;

        public string Key { get; set; }
        public string Level { get; set; }
        public string Type { get; set; }
        public string LevelDisplay { get; set; }
        public string TypeDisplay { get; set; }

        public string Title { get; set; }
        public string Summary { get; set; }
        public string RuleName { get; set; }
        public string RuleId { get; set; }

        public string TimeRange { get; set; }

        public string MetricName { get; set; }
        public string MetricText { get; set; }
        public string CurrentValue { get; set; }
        public string BaselineValue { get; set; }
        public string ThresholdValue { get; set; }

        public string Status
        {
            get => _status;
            set
            {
                if (_status != value)
                {
                    _status = value;
                    OnPropertyChanged();
                }
            }
        }

        public string AiMarkdown
        {
            get => _aiMarkdown;
            set
            {
                if (_aiMarkdown != value)
                {
                    _aiMarkdown = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool HasAiMarkdown
        {
            get => _hasAiMarkdown;
            set
            {
                if (_hasAiMarkdown != value)
                {
                    _hasAiMarkdown = value;
                    OnPropertyChanged();
                }
            }
        }

        public DateTime SortTime { get; set; }
        public DateTime LastDetected { get; set; }

        public int OccurrenceCount
        {
            get => _occurrenceCount;
            set
            {
                if (_occurrenceCount != value)
                {
                    _occurrenceCount = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

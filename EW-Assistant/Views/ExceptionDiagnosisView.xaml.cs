using EW_Assistant.Diagnostics;
using EW_Assistant.Net;
using EW_Assistant.Services;
using EW_Assistant.Settings;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace EW_Assistant.Views
{
    /// <summary>
    /// AUTO 异常诊断页：聚合本地 WorkHttpServer 受理状态、审计状态文件、MCP 调用记录与最终回答。
    /// </summary>
    public partial class ExceptionDiagnosisView : UserControl, INotifyPropertyChanged
    {
        private const string AutoAuditStateRoot = @"D:\DataAI";
        private const string AutoAuditStateDirName = "auto_clear_alarm_states";
        private const string AutoAuditSnapshotFileName = "auto_clear_alarm_state.json";
        private const int MaxRecentTraceCount = 40;
        private const int MaxSummaryCsvRecordCount = 400;
        private const string EmptyCurrentNodeText = "流程尚未开始。";
        private const string EmptyCurrentMcpText = "当前还没有记录到 MCP 调用。";
        private const string EmptyFinalReplyText = "暂无最终回答。";
        private const int FinalReplyRevealTargetFrames = 90;
        private static readonly TimeSpan ActiveRefreshInterval = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan IdleRefreshInterval = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan CurrentExecutionFreshWindow = TimeSpan.FromMinutes(15);

        private readonly DispatcherTimer _refreshTimer = new DispatcherTimer();
        private readonly DispatcherTimer _finalReplyTypewriterTimer = new DispatcherTimer();
        private readonly Dictionary<string, AutoClearAlarmAudit.SummaryCsvRecord> _csvRecordMap =
            new Dictionary<string, AutoClearAlarmAudit.SummaryCsvRecord>(StringComparer.OrdinalIgnoreCase);
        private ScrollViewer _finalReplyScrollViewer;

        private bool _eventsSubscribed;
        private bool _isRefreshing;
        private bool _refreshPending;
        private bool _refreshScheduled;
        private bool _finalReplyScrollPending;
        private string _currentNodeDetailText = L(EmptyCurrentNodeText);
        private string _currentMcpDetailText = L(EmptyCurrentMcpText);
        private string _currentFinalReplyText = L(EmptyFinalReplyText);
        private string _animatedFinalReplyText = L(EmptyFinalReplyText);
        private string _finalReplyRevealText = string.Empty;
        private string _currentVisionImagePath = string.Empty;
        private string _currentNodeRecordSignature = string.Empty;
        private int _finalReplyRevealIndex;
        private bool _isFinalReplyRevealActive;
        private DateTime? _selectedRecordDate = DateTime.Today;
        private bool _showArchivedRecords;

        public ExceptionDiagnosisView()
        {
            InitializeComponent();
            DataContext = this;
            _refreshTimer.Interval = IdleRefreshInterval;
            _refreshTimer.Tick += RefreshTimer_Tick;
            _finalReplyTypewriterTimer.Interval = TimeSpan.FromMilliseconds(18);
            _finalReplyTypewriterTimer.Tick += FinalReplyTypewriterTimer_Tick;
            WireCollectionState(CurrentNodeRecordItems, nameof(HasCurrentNodeRecords), nameof(HasNoCurrentNodeRecords));
            WireCollectionState(CurrentMcpRecordItems, nameof(HasCurrentMcpRecords), nameof(HasNoCurrentMcpRecords));
            Loaded += ExceptionDiagnosisView_Loaded;
            Unloaded += ExceptionDiagnosisView_Unloaded;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public ObservableCollection<AutoTraceListItem> TraceItems { get; } =
            new ObservableCollection<AutoTraceListItem>();

        public ObservableCollection<AutoTraceListItem> ArchivedTraceItems { get; } =
            new ObservableCollection<AutoTraceListItem>();

        public ObservableCollection<CompactSequenceItem> CurrentNodeRecordItems { get; } =
            new ObservableCollection<CompactSequenceItem>();

        public ObservableCollection<CompactSequenceItem> CurrentMcpRecordItems { get; } =
            new ObservableCollection<CompactSequenceItem>();

        private static string L(string chineseText)
        {
            return UiLanguageService.Text(chineseText, ConfigService.Current);
        }

        public bool HasCurrentNodeRecords => CurrentNodeRecordItems.Count > 0;

        public bool HasNoCurrentNodeRecords => !HasCurrentNodeRecords;

        public bool HasCurrentMcpRecords => CurrentMcpRecordItems.Count > 0;

        public bool HasNoCurrentMcpRecords => !HasCurrentMcpRecords;

        public string CurrentNodeDetailText
        {
            get => _currentNodeDetailText;
            set => SetField(ref _currentNodeDetailText, value);
        }

        public string CurrentMcpDetailText
        {
            get => _currentMcpDetailText;
            set => SetField(ref _currentMcpDetailText, value);
        }

        public string CurrentFinalReplyText
        {
            get => _currentFinalReplyText;
            set
            {
                var next = value ?? string.Empty;
                if (SetField(ref _currentFinalReplyText, next))
                {
                    StartFinalReplyReveal(next);
                }
            }
        }

        public string AnimatedFinalReplyText
        {
            get => _animatedFinalReplyText;
            private set => SetField(ref _animatedFinalReplyText, value);
        }

        public bool IsFinalReplyRevealActive
        {
            get => _isFinalReplyRevealActive;
            private set => SetField(ref _isFinalReplyRevealActive, value);
        }

        public string CurrentVisionImagePath
        {
            get => _currentVisionImagePath;
            set
            {
                if (SetField(ref _currentVisionImagePath, value))
                {
                    OnPropertyChanged(nameof(HasCurrentVisionImage));
                    OnPropertyChanged(nameof(HasNoCurrentVisionImage));
                }
            }
        }

        public bool HasCurrentVisionImage => !string.IsNullOrWhiteSpace(CurrentVisionImagePath);

        public bool HasNoCurrentVisionImage => !HasCurrentVisionImage;

        public DateTime? SelectedRecordDate
        {
            get => _selectedRecordDate;
            set
            {
                var normalized = (value ?? DateTime.Today).Date;
                if (_selectedRecordDate.HasValue && _selectedRecordDate.Value.Date == normalized)
                {
                    return;
                }

                _selectedRecordDate = normalized;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsTodayRecordDateSelected));
                RequestRefresh();
            }
        }

        public bool IsTodayRecordDateSelected => SelectedRecordDay == DateTime.Today;

        public bool IsRecentRecordsTabActive => !_showArchivedRecords;

        public bool IsArchivedRecordsTabActive => _showArchivedRecords;

        private DateTime SelectedRecordDay => (SelectedRecordDate ?? DateTime.Today).Date;

        private async void ExceptionDiagnosisView_Loaded(object sender, RoutedEventArgs e)
        {
            SubscribeEvents();
            await RefreshViewAsync();
            _refreshTimer.Start();
        }

        private void ExceptionDiagnosisView_Unloaded(object sender, RoutedEventArgs e)
        {
            _refreshTimer.Stop();
            _finalReplyTypewriterTimer.Stop();
            UnsubscribeEvents();
        }

        private void RefreshTimer_Tick(object sender, EventArgs e)
        {
            RequestRefresh();
        }

        private void FinalReplyTypewriterTimer_Tick(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_finalReplyRevealText))
            {
                CompleteFinalReplyReveal();
                return;
            }

            var step = Math.Max(1, _finalReplyRevealText.Length / FinalReplyRevealTargetFrames);
            _finalReplyRevealIndex = Math.Min(_finalReplyRevealText.Length, _finalReplyRevealIndex + step);
            AnimatedFinalReplyText = _finalReplyRevealText.Substring(0, _finalReplyRevealIndex);
            ScrollFinalReplyToEnd();

            if (_finalReplyRevealIndex >= _finalReplyRevealText.Length)
            {
                CompleteFinalReplyReveal();
            }
        }

        private void StartFinalReplyReveal(string text)
        {
            _finalReplyTypewriterTimer.Stop();
            _finalReplyRevealText = text ?? string.Empty;
            _finalReplyRevealIndex = 0;

            if (!ShouldAnimateFinalReply(_finalReplyRevealText))
            {
                AnimatedFinalReplyText = _finalReplyRevealText;
                IsFinalReplyRevealActive = false;
                ScrollFinalReplyToEnd();
                return;
            }

            AnimatedFinalReplyText = string.Empty;
            IsFinalReplyRevealActive = true;
            BeginFinalReplyGlowEffect();
            _finalReplyTypewriterTimer.Start();
        }

        private bool ShouldAnimateFinalReply(string text)
        {
            return IsLoaded
                   && !string.IsNullOrWhiteSpace(text)
                   && !string.Equals(text, L(EmptyFinalReplyText), StringComparison.Ordinal);
        }

        private void CompleteFinalReplyReveal()
        {
            _finalReplyTypewriterTimer.Stop();
            AnimatedFinalReplyText = _finalReplyRevealText ?? string.Empty;
            IsFinalReplyRevealActive = false;
            ScrollFinalReplyToEnd();
        }

        private void BeginFinalReplyGlowEffect()
        {
            if (FinalReplyGlowOverlay == null || FinalReplyGlowTransform == null)
            {
                return;
            }

            var hostWidth = FinalReplyTextHost == null ? ActualWidth : FinalReplyTextHost.ActualWidth;
            if (double.IsNaN(hostWidth) || hostWidth < 1)
            {
                hostWidth = 720;
            }

            FinalReplyGlowOverlay.BeginAnimation(UIElement.OpacityProperty, null);
            FinalReplyGlowTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
            FinalReplyGlowOverlay.Opacity = 0;
            FinalReplyGlowTransform.X = -320;

            var opacityFrames = new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromMilliseconds(1450)
            };
            opacityFrames.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
            opacityFrames.KeyFrames.Add(new LinearDoubleKeyFrame(0.72, KeyTime.FromPercent(0.18)));
            opacityFrames.KeyFrames.Add(new LinearDoubleKeyFrame(0.48, KeyTime.FromPercent(0.72)));
            opacityFrames.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));

            var sweepAnimation = new DoubleAnimation
            {
                From = -320,
                To = hostWidth + 320,
                Duration = TimeSpan.FromMilliseconds(1450),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            FinalReplyGlowOverlay.BeginAnimation(UIElement.OpacityProperty, opacityFrames);
            FinalReplyGlowTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, sweepAnimation);
        }

        private async Task RefreshViewAsync()
        {
            if (_isRefreshing)
            {
                _refreshPending = true;
                return;
            }

            _isRefreshing = true;
            var selectedDate = SelectedRecordDay;
            try
            {
                var payload = await Task.Run(() => LoadRefreshPayload(selectedDate));
                if (SelectedRecordDay != selectedDate)
                {
                    _refreshPending = true;
                    return;
                }

                ReplaceCsvRecordMap(payload.CsvRecords);

                var currentSnapshot = DetermineCurrentSnapshot(payload.Snapshots, selectedDate);
                UpdateRefreshTimerInterval(currentSnapshot);
                ApplyCurrentSnapshot(currentSnapshot);
                UpdateTraceItems(payload.CsvRecords);
            }
            catch (Exception ex)
            {
                MainWindow.PostProgramInfo("[ExceptionDiagnosis] " + L("刷新异常：") + ex.Message, "warn");
            }
            finally
            {
                _isRefreshing = false;
                if (_refreshPending)
                {
                    _refreshPending = false;
                    await RefreshViewAsync();
                }
            }
        }

        private RefreshPayload LoadRefreshPayload(DateTime selectedDate)
        {
            return new RefreshPayload
            {
                Snapshots = LoadRecentSnapshots(),
                CsvRecords = LoadSummaryCsvRecords(selectedDate)
            };
        }

        private List<AutoClearAlarmAudit.SummaryCsvRecord> LoadSummaryCsvRecords(DateTime selectedDate)
        {
            return AutoClearAlarmAudit.ReadSummaryCsvRecordsByDate(selectedDate, MaxSummaryCsvRecordCount)
                .Where(record => record != null)
                .OrderByDescending(record => record.TriggeredAt ?? record.TriggerDate)
                .ToList();
        }

        private List<AutoTraceSnapshot> LoadRecentSnapshots()
        {
            var result = new List<AutoTraceSnapshot>();

            foreach (var traceId in AutoClearAlarmAudit.ReadRecentTraceIds(MaxRecentTraceCount * 2))
            {
                var snapshot = ReadSnapshotFromFile(GetTraceStatePath(traceId));
                if (snapshot != null && !string.IsNullOrWhiteSpace(snapshot.TraceId))
                {
                    result.Add(snapshot);
                }
            }

            if (result.Count == 0)
            {
                var stateRoot = Path.Combine(AutoAuditStateRoot, AutoAuditStateDirName);
                if (Directory.Exists(stateRoot))
                {
                    var candidatePaths = Directory.EnumerateFiles(stateRoot, "*.json", SearchOption.TopDirectoryOnly)
                        .OrderByDescending(GetFileLastWriteTimeSafe)
                        .Take(MaxRecentTraceCount * 2)
                        .ToList();

                    foreach (var path in candidatePaths)
                    {
                        var snapshot = ReadSnapshotFromFile(path);
                        if (snapshot != null && !string.IsNullOrWhiteSpace(snapshot.TraceId))
                        {
                            result.Add(snapshot);
                        }
                    }
                }
            }

            if (result.Count == 0)
            {
                var snapshotPath = Path.Combine(AutoAuditStateRoot, AutoAuditSnapshotFileName);
                var snapshot = ReadSnapshotFromFile(snapshotPath);
                if (snapshot != null && !string.IsNullOrWhiteSpace(snapshot.TraceId))
                {
                    result.Add(snapshot);
                }
            }

            return result
                .GroupBy(x => x.TraceId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(item => item.LastUpdatedAt == default ? item.StartedAt : item.LastUpdatedAt)
                    .First())
                .OrderByDescending(item => item.LastUpdatedAt == default ? item.StartedAt : item.LastUpdatedAt)
                .Take(MaxRecentTraceCount)
                .ToList();
        }

        private static string GetTraceStatePath(string traceId)
        {
            if (string.IsNullOrWhiteSpace(traceId))
            {
                return string.Empty;
            }

            return Path.Combine(AutoAuditStateRoot, AutoAuditStateDirName, traceId + ".json");
        }

        private static DateTime GetFileLastWriteTimeSafe(string path)
        {
            try
            {
                return File.Exists(path) ? File.GetLastWriteTime(path) : DateTime.MinValue;
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        private static AutoTraceSnapshot ReadSnapshotFromFile(string path)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    {
                        return null;
                    }

                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(fs, Encoding.UTF8, true);
                    var json = reader.ReadToEnd();
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        return null;
                    }

                    var jo = JObject.Parse(json);
                    var snapshot = new AutoTraceSnapshot
                    {
                        TraceId = (jo.Value<string>("TraceId") ?? string.Empty).Trim(),
                        Status = NormalizeStatus(jo.Value<string>("Status")),
                        InputMessage = jo.Value<string>("InputMessage") ?? string.Empty,
                        FinalReply = jo.Value<string>("FinalReply") ?? string.Empty,
                        StartedAt = ReadMeaningfulDate(jo["StartedAt"]),
                        LastUpdatedAt = ReadMeaningfulDate(jo["LastUpdatedAt"]),
                        FinalizedAt = ReadMeaningfulNullableDate(jo["FinalizedAt"]),
                        ClearMachineAlarmsCalled = jo.Value<bool?>("ClearMachineAlarmsCalled") ?? false,
                        IsVisionRelated = jo.Value<bool?>("IsVisionRelated") ?? false,
                        AutoVisionSourceImagePath = jo.Value<string>("AutoVisionSourceImagePath") ?? string.Empty,
                        AutoVisionProcessedImagePath = jo.Value<string>("AutoVisionProcessedImagePath") ?? string.Empty
                    };

                    var toolCalls = jo["McpToolCalls"] as JArray;
                    if (toolCalls != null)
                    {
                        snapshot.McpToolCalls.AddRange(toolCalls
                            .OfType<JObject>()
                            .Select(token => (JObject)token.DeepClone()));
                    }

                    var nodeExecutions = jo["NodeExecutions"] as JArray;
                    if (nodeExecutions != null)
                    {
                        snapshot.NodeExecutions.AddRange(nodeExecutions
                            .OfType<JObject>()
                            .Select(token => (JObject)token.DeepClone()));
                    }

                    return string.IsNullOrWhiteSpace(snapshot.TraceId) ? null : snapshot;
                }
                catch
                {
                    if (attempt >= 1)
                    {
                        break;
                    }
                }
            }

            return null;
        }

        private static DateTime ReadMeaningfulDate(JToken token)
        {
            var value = ReadMeaningfulNullableDate(token);
            return value ?? default;
        }

        private static DateTime? ReadMeaningfulNullableDate(JToken token)
        {
            try
            {
                var value = token?.Value<DateTime?>();
                if (!value.HasValue || value.Value <= DateTime.MinValue.AddYears(1))
                {
                    return null;
                }

                return value.Value;
            }
            catch
            {
                return null;
            }
        }

        private static string NormalizeStatus(string raw)
        {
            return (raw ?? string.Empty).Trim().ToLowerInvariant();
        }

        private void WireCollectionState<T>(ObservableCollection<T> collection, string visiblePropertyName, string emptyPropertyName)
        {
            if (collection == null)
            {
                return;
            }

            collection.CollectionChanged += (_, __) =>
            {
                OnPropertyChanged(visiblePropertyName);
                OnPropertyChanged(emptyPropertyName);
            };
        }

        private void ReplaceCsvRecordMap(IEnumerable<AutoClearAlarmAudit.SummaryCsvRecord> records)
        {
            _csvRecordMap.Clear();
            foreach (var record in records ?? Enumerable.Empty<AutoClearAlarmAudit.SummaryCsvRecord>())
            {
                var key = BuildSummaryCsvRecordKey(record);
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                _csvRecordMap[key] = record;
            }
        }

        private AutoTraceSnapshot DetermineCurrentSnapshot(IReadOnlyList<AutoTraceSnapshot> snapshots, DateTime selectedDate)
        {
            if (snapshots == null || snapshots.Count == 0)
            {
                return null;
            }

            var running = snapshots
                .Where(IsCurrentExecutionCandidate)
                .OrderByDescending(snapshot => snapshot.LastUpdatedAt == default ? snapshot.StartedAt : snapshot.LastUpdatedAt)
                .FirstOrDefault();

            if (running != null)
            {
                return running;
            }

            return snapshots
                .Where(snapshot => IsSnapshotOnDate(snapshot, selectedDate))
                .OrderByDescending(GetSnapshotReferenceTime)
                .FirstOrDefault();
        }

        private static bool IsSnapshotOnDate(AutoTraceSnapshot snapshot, DateTime date)
        {
            var referenceTime = GetSnapshotReferenceTime(snapshot);
            return referenceTime != default && referenceTime.Date == date.Date;
        }

        private static DateTime GetSnapshotReferenceTime(AutoTraceSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return default;
            }

            if (snapshot.FinalizedAt.HasValue && snapshot.FinalizedAt.Value != default)
            {
                return snapshot.FinalizedAt.Value;
            }

            if (snapshot.LastUpdatedAt != default)
            {
                return snapshot.LastUpdatedAt;
            }

            return snapshot.StartedAt;
        }

        private static bool IsRunningLike(string status)
        {
            return string.Equals(status, "pending", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(status, "accepted", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(status, "running", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsCurrentExecutionCandidate(AutoTraceSnapshot snapshot)
        {
            if (snapshot == null || !IsRunningLike(snapshot.Status))
            {
                return false;
            }

            if (DifyWorkflowClient.IsBusy)
            {
                return true;
            }

            var referenceTime = snapshot.LastUpdatedAt == default ? snapshot.StartedAt : snapshot.LastUpdatedAt;
            if (referenceTime == default)
            {
                return false;
            }

            return DateTime.Now - referenceTime <= CurrentExecutionFreshWindow;
        }

        private void UpdateRefreshTimerInterval(AutoTraceSnapshot snapshot)
        {
            var targetInterval = ShouldUseActiveRefreshInterval(snapshot)
                ? ActiveRefreshInterval
                : IdleRefreshInterval;
            if (_refreshTimer.Interval != targetInterval)
            {
                _refreshTimer.Interval = targetInterval;
            }
        }

        private static bool ShouldUseActiveRefreshInterval(AutoTraceSnapshot snapshot)
        {
            return DifyWorkflowClient.IsBusy || IsCurrentExecutionCandidate(snapshot);
        }

        private void ApplyCurrentSnapshot(AutoTraceSnapshot snapshot)
        {
            if (snapshot == null)
            {
                ResetCurrentSnapshotView();
                return;
            }

            CurrentFinalReplyText = string.IsNullOrWhiteSpace(snapshot.FinalReply)
                ? L(EmptyFinalReplyText)
                : UiLanguageService.ApplyDisplayTextReplacements(snapshot.FinalReply);
            CurrentVisionImagePath = GetPreferredVisionImagePath(snapshot);

            UpdateCurrentNodeSummary(snapshot);
            UpdateCurrentMcpSummary(snapshot);
        }

        private void ResetCurrentSnapshotView()
        {
            CurrentFinalReplyText = L(EmptyFinalReplyText);
            CurrentVisionImagePath = string.Empty;
            _currentNodeRecordSignature = string.Empty;
            ApplySequenceSection(CurrentNodeRecordItems, Array.Empty<string>(), L(EmptyCurrentNodeText), text => CurrentNodeDetailText = text);
            ApplySequenceSection(CurrentMcpRecordItems, Array.Empty<string>(), L(EmptyCurrentMcpText), text => CurrentMcpDetailText = text);
        }

        private void UpdateCurrentNodeSummary(AutoTraceSnapshot snapshot)
        {
            var nodeLines = BuildNodeRecordLines(snapshot);
            var nextSignature = BuildSequenceSignature(nodeLines);
            var shouldScrollToLatest = nodeLines.Count > 0
                                       && !string.Equals(_currentNodeRecordSignature, nextSignature, StringComparison.Ordinal);
            _currentNodeRecordSignature = nextSignature;

            ApplySequenceSection(
                CurrentNodeRecordItems,
                nodeLines,
                BuildCurrentNodeDetailText(snapshot),
                text => CurrentNodeDetailText = text);

            if (shouldScrollToLatest)
            {
                ScrollCurrentNodeRecordsToEnd();
            }
        }

        private void UpdateCurrentMcpSummary(AutoTraceSnapshot snapshot)
        {
            ApplySequenceSection(
                CurrentMcpRecordItems,
                BuildMcpRecordLines(snapshot),
                BuildCurrentMcpDetailText(snapshot),
                text => CurrentMcpDetailText = text);
        }

        private void ApplySequenceSection(
            ObservableCollection<CompactSequenceItem> targetCollection,
            IReadOnlyList<string> lines,
            string emptyText,
            Action<string> setDetailText)
        {
            ReplaceItems(targetCollection, BuildSequenceItems(lines));
            setDetailText?.Invoke(lines != null && lines.Count > 0 ? string.Empty : emptyText);
        }

        private static List<CompactSequenceItem> BuildSequenceItems(IReadOnlyList<string> lines)
        {
            var items = new List<CompactSequenceItem>();
            if (lines == null)
            {
                return items;
            }

            for (var i = 0; i < lines.Count; i++)
            {
                items.Add(new CompactSequenceItem
                {
                    IndexText = (i + 1).ToString(),
                    Title = lines[i]
                });
            }

            return items;
        }

        private static string BuildSequenceSignature(IReadOnlyList<string> lines)
        {
            return lines == null || lines.Count == 0
                ? string.Empty
                : string.Join("\u001F", lines);
        }

        private void ScrollCurrentNodeRecordsToEnd()
        {
            if (!IsLoaded)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                CurrentNodeRecordsScrollViewer?.ScrollToEnd();
            }), DispatcherPriority.ContextIdle);
        }

        private void ScrollFinalReplyToEnd()
        {
            if (!IsLoaded || _finalReplyScrollPending)
            {
                return;
            }

            _finalReplyScrollPending = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _finalReplyScrollPending = false;
                if (IsFinalReplyRevealActive)
                {
                    FinalReplyRevealScrollViewer?.UpdateLayout();
                    FinalReplyRevealScrollViewer?.ScrollToEnd();
                    return;
                }

                FinalReplyMarkdownViewer?.UpdateLayout();
                MarkdownFontHelper.ApplyAppFont(FinalReplyMarkdownViewer?.Document, 18);
                var scroller = GetFinalReplyScrollViewer();
                if (scroller == null)
                {
                    return;
                }

                scroller.UpdateLayout();
                scroller.ScrollToVerticalOffset(scroller.ScrollableHeight);
            }), DispatcherPriority.ContextIdle);
        }

        private ScrollViewer GetFinalReplyScrollViewer()
        {
            if (_finalReplyScrollViewer == null)
            {
                _finalReplyScrollViewer = FindChildScrollViewer(FinalReplyMarkdownViewer);
            }

            return _finalReplyScrollViewer;
        }

        private static ScrollViewer FindChildScrollViewer(DependencyObject root)
        {
            if (root == null)
            {
                return null;
            }

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is ScrollViewer sv)
                {
                    return sv;
                }

                var result = FindChildScrollViewer(child);
                if (result != null)
                {
                    return result;
                }
            }

            return null;
        }

        private static void ReplaceItems<T>(ObservableCollection<T> targetCollection, IEnumerable<T> items)
        {
            if (targetCollection == null)
            {
                return;
            }

            targetCollection.Clear();
            foreach (var item in items ?? Enumerable.Empty<T>())
            {
                targetCollection.Add(item);
            }
        }

        private static string BuildCurrentNodeDetailText(AutoTraceSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return L(EmptyCurrentNodeText);
            }

            var finalReplyPreview = string.IsNullOrWhiteSpace(snapshot.FinalReply)
                ? string.Empty
                : ShortenSingleLine(snapshot.FinalReply, 100);

            return NormalizeStatus(snapshot.Status) switch
            {
                "pending" => L("等待本地 WorkHttpServer 受理。"),
                "accepted" => L("请求已受理，等待进入第一个 workflow 节点。"),
                "rejected" => string.IsNullOrWhiteSpace(finalReplyPreview) ? L("本次请求未被执行。") : finalReplyPreview,
                "dispatch_failed" => string.IsNullOrWhiteSpace(finalReplyPreview) ? L("workflow 调度失败。") : finalReplyPreview,
                "succeeded" => string.IsNullOrWhiteSpace(finalReplyPreview) ? MapStatusText(snapshot.Status) : finalReplyPreview,
                "failed" => string.IsNullOrWhiteSpace(finalReplyPreview) ? MapStatusText(snapshot.Status) : finalReplyPreview,
                _ => L("正在等待新的节点执行记录。")
            };
        }

        private static string BuildCurrentMcpDetailText(AutoTraceSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return L(EmptyCurrentMcpText);
            }

            return IsFinalStatus(snapshot.Status)
                ? L("本次流程没有触发任何 MCP 调用。")
                : L(EmptyCurrentMcpText);
        }

        private static List<string> BuildNodeRecordLines(AutoTraceSnapshot snapshot)
        {
            return OrderNodeExecutions(snapshot?.NodeExecutions)
                .Select(BuildNodeExecutionTitle)
                .Where(title => !string.IsNullOrWhiteSpace(title))
                .ToList();
        }

        private static List<string> BuildMcpRecordLines(AutoTraceSnapshot snapshot)
        {
            var lines = new List<string>();
            if (snapshot == null)
            {
                return lines;
            }

            foreach (var call in snapshot.McpToolCalls)
            {
                if (call == null)
                {
                    continue;
                }

                var title = BuildMcpRecordTitle(call);
                lines.Add(title);
            }

            if (lines.Count == 0 && snapshot.ClearMachineAlarmsCalled)
            {
                lines.Add("ClearMachineAlarms");
            }

            return lines;
        }

        private static IReadOnlyList<JObject> OrderNodeExecutions(IReadOnlyList<JObject> nodeExecutions)
        {
            if (nodeExecutions == null || nodeExecutions.Count == 0)
            {
                return Array.Empty<JObject>();
            }

            return nodeExecutions
                .Where(node => node != null)
                .OrderBy(node =>
                {
                    var startedAt = ReadMeaningfulDate(node["StartedAt"]);
                    return startedAt == default ? ReadMeaningfulDate(node["LastUpdatedAt"]) : startedAt;
                })
                .ThenBy(node => ReadMeaningfulDate(node["FinishedAt"]))
                .ToList();
        }

        private static string BuildMcpRecordTitle(JObject call)
        {
            var title = (call?.Value<string>("ToolName") ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(title) ? L("MCP 已执行动作") : title;
        }

        private void UpdateTraceItems(IReadOnlyList<AutoClearAlarmAudit.SummaryCsvRecord> records)
        {
            ReplaceItems(TraceItems, BuildTraceListItems(records, archived: false));
            ReplaceItems(ArchivedTraceItems, BuildTraceListItems(records, archived: true));
            UpdateVisionPreviewFromRecords(records);
        }

        private void UpdateVisionPreviewFromRecords(IEnumerable<AutoClearAlarmAudit.SummaryCsvRecord> records)
        {
            if (HasCurrentVisionImage)
            {
                return;
            }

            var candidates = (records ?? Enumerable.Empty<AutoClearAlarmAudit.SummaryCsvRecord>())
                .Where(record => _showArchivedRecords ? ShouldShowInArchivedRecords(record) : ShouldShowInRecentRecords(record));

            foreach (var record in candidates)
            {
                var path = ResolveVisionImagePath(record);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    CurrentVisionImagePath = path;
                    return;
                }
            }
        }

        private void VisionImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount < 2)
            {
                return;
            }

            e.Handled = true;
            OpenVisionImagePreviewWindow(CurrentVisionImagePath);
        }

        private void OpenVisionImagePreviewWindow(string imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            {
                MessageBox.Show(L("图片文件无法打开：") + imagePath, L("图片预览"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ImageSource imageSource;
            try
            {
                imageSource = LoadOriginalImage(imagePath);
            }
            catch (Exception ex)
            {
                MessageBox.Show(L("图片文件无法打开：") + ex.Message, L("图片预览"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var scale = new ScaleTransform(1, 1);
            var image = new Image
            {
                Source = imageSource,
                Stretch = Stretch.None,
                LayoutTransform = scale,
                SnapsToDevicePixels = true,
                Cursor = Cursors.Hand
            };

            var scroll = new ScrollViewer
            {
                Content = image,
                Background = Brushes.Black,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            var window = new Window
            {
                Title = string.IsNullOrWhiteSpace(Path.GetFileName(imagePath)) ? L("图片预览") : Path.GetFileName(imagePath),
                Width = 980,
                Height = 720,
                MinWidth = 520,
                MinHeight = 360,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = Brushes.Black,
                Content = scroll
            };

            var owner = Window.GetWindow(this);
            if (owner != null)
            {
                window.Owner = owner;
            }

            void SetScale(double value)
            {
                var next = Math.Max(0.1, Math.Min(8.0, value));
                scale.ScaleX = next;
                scale.ScaleY = next;
            }

            void FitToWindow()
            {
                if (!(imageSource is BitmapSource bitmap))
                {
                    SetScale(1);
                    return;
                }

                var viewportWidth = Math.Max(1, scroll.ViewportWidth - 24);
                var viewportHeight = Math.Max(1, scroll.ViewportHeight - 24);
                var fit = Math.Min(viewportWidth / bitmap.PixelWidth, viewportHeight / bitmap.PixelHeight);
                SetScale(double.IsInfinity(fit) || double.IsNaN(fit) ? 1 : Math.Min(1, fit));
            }

            scroll.PreviewMouseWheel += (_, args) =>
            {
                var oldScale = scale.ScaleX;
                var factor = args.Delta > 0 ? 1.12 : 0.88;
                var mouseOnImage = args.GetPosition(image);
                var mouseOnViewport = args.GetPosition(scroll);

                SetScale(oldScale * factor);
                var newScale = scale.ScaleX;
                scroll.UpdateLayout();
                scroll.ScrollToHorizontalOffset(mouseOnImage.X * newScale - mouseOnViewport.X);
                scroll.ScrollToVerticalOffset(mouseOnImage.Y * newScale - mouseOnViewport.Y);
                args.Handled = true;
            };

            var isDragging = false;
            var lastDragPoint = new Point();

            scroll.PreviewMouseLeftButtonDown += (_, args) =>
            {
                isDragging = true;
                lastDragPoint = args.GetPosition(scroll);
                scroll.CaptureMouse();
                image.Cursor = Cursors.SizeAll;
                args.Handled = true;
            };

            scroll.PreviewMouseMove += (_, args) =>
            {
                if (!isDragging || args.LeftButton != MouseButtonState.Pressed)
                {
                    return;
                }

                var current = args.GetPosition(scroll);
                var delta = current - lastDragPoint;
                scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - delta.X);
                scroll.ScrollToVerticalOffset(scroll.VerticalOffset - delta.Y);
                lastDragPoint = current;
                args.Handled = true;
            };

            scroll.PreviewMouseLeftButtonUp += (_, args) =>
            {
                if (!isDragging)
                {
                    return;
                }

                isDragging = false;
                scroll.ReleaseMouseCapture();
                image.Cursor = Cursors.Hand;
                args.Handled = true;
            };

            scroll.LostMouseCapture += (_, __) =>
            {
                isDragging = false;
                image.Cursor = Cursors.Hand;
            };

            window.PreviewKeyDown += (_, args) =>
            {
                if (args.Key == Key.Escape)
                {
                    window.Close();
                    args.Handled = true;
                }
            };
            window.Loaded += (_, __) => window.Dispatcher.BeginInvoke(new Action(FitToWindow), DispatcherPriority.Background);
            window.Show();
        }

        private static ImageSource LoadOriginalImage(string filePath)
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(filePath, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }

        private IEnumerable<AutoTraceListItem> BuildTraceListItems(IEnumerable<AutoClearAlarmAudit.SummaryCsvRecord> records, bool archived)
        {
            foreach (var record in records ?? Enumerable.Empty<AutoClearAlarmAudit.SummaryCsvRecord>())
            {
                if (archived ? ShouldShowInArchivedRecords(record) : ShouldShowInRecentRecords(record))
                {
                    yield return BuildTraceListItem(record, archived);
                }
            }
        }

        private static bool ShouldShowInRecentRecords(AutoClearAlarmAudit.SummaryCsvRecord record)
        {
            return record != null && !IsManuallyClassified(record);
        }

        private static bool ShouldShowInArchivedRecords(AutoClearAlarmAudit.SummaryCsvRecord record)
        {
            return record != null && IsManuallyClassified(record);
        }

        private static bool IsManuallyClassified(AutoClearAlarmAudit.SummaryCsvRecord record)
        {
            return record != null
                   && !string.IsNullOrWhiteSpace(record.Phenomenon)
                   && !string.IsNullOrWhiteSpace(record.ManualCountermeasure);
        }

        private AutoTraceListItem BuildTraceListItem(AutoClearAlarmAudit.SummaryCsvRecord record, bool archived)
        {
            var titleText = ShortenSingleLine(UiLanguageService.ApplyDisplayTextReplacements(record?.AlarmContent), 34);
            if (string.IsNullOrWhiteSpace(titleText))
            {
                titleText = L("未填写报警内容");
            }

            var summaryText = archived
                ? BuildArchivedSummaryText(record)
                : BuildRecentSummaryText(record);

            return new AutoTraceListItem
            {
                RecordKey = BuildSummaryCsvRecordKey(record),
                StatusText = archived ? L("已归类") : L("待填写"),
                StatusLevel = archived ? "ok" : "warn",
                StartedAtText = FormatSummaryCsvTime(record),
                TitleText = titleText,
                SummaryText = summaryText
            };
        }

        private static string BuildRecentSummaryText(AutoClearAlarmAudit.SummaryCsvRecord record)
        {
            if (record == null)
            {
                return L("点击查看后补录");
            }

            var reply = string.IsNullOrWhiteSpace(record.AiReply)
                ? L("点击查看后补录")
                : ShortenSingleLine(UiLanguageService.ApplyDisplayTextReplacements(record.AiReply), 32);
            return reply;
        }

        private static string BuildArchivedSummaryText(AutoClearAlarmAudit.SummaryCsvRecord record)
        {
            if (record == null)
            {
                return L("已归类。");
            }

            if (!string.IsNullOrWhiteSpace(record.Phenomenon))
            {
                return L("现象：") + ShortenSingleLine(UiLanguageService.ApplyDisplayTextReplacements(record.Phenomenon), 24);
            }

            if (!string.IsNullOrWhiteSpace(record.ManualCountermeasure))
            {
                return L("对策：") + ShortenSingleLine(UiLanguageService.ApplyDisplayTextReplacements(record.ManualCountermeasure), 24);
            }

            return L("已归类。");
        }

        private static string FormatSummaryCsvTime(AutoClearAlarmAudit.SummaryCsvRecord record)
        {
            if (record?.TriggeredAt.HasValue == true)
            {
                return record.TriggeredAt.Value.ToString("MM-dd HH:mm:ss");
            }

            return string.IsNullOrWhiteSpace(record?.TriggerTimeText)
                ? "-"
                : record.TriggerTimeText.Trim();
        }

        private static string BuildSummaryCsvRecordKey(AutoClearAlarmAudit.SummaryCsvRecord record)
        {
            if (record == null)
            {
                return string.Empty;
            }

            return string.Join("\u001f", new[]
            {
                (record.CsvPath ?? string.Empty).Trim(),
                record.RowIndex.ToString()
            });
        }

        private AutoClearAlarmAudit.SummaryCsvRecord ResolveSummaryCsvRecordByKey(string recordKey)
        {
            if (string.IsNullOrWhiteSpace(recordKey))
            {
                return null;
            }

            if (_csvRecordMap.TryGetValue(recordKey.Trim(), out var record))
            {
                return record;
            }

            var records = LoadSummaryCsvRecords(SelectedRecordDay);
            ReplaceCsvRecordMap(records);
            return _csvRecordMap.TryGetValue(recordKey.Trim(), out record) ? record : null;
        }

        private ExceptionDiagnosisRecordWindow.RecordDialogViewModel BuildRecordDialogViewModel(
            AutoClearAlarmAudit.SummaryCsvRecord record,
            string visionImagePath)
        {
            return new ExceptionDiagnosisRecordWindow.RecordDialogViewModel
            {
                CsvPathText = record?.CsvPath ?? string.Empty,
                TriggerTimeText = record?.TriggeredAt.HasValue == true
                    ? record.TriggeredAt.Value.ToString("yyyy-MM-dd HH:mm:ss")
                    : (record?.TriggerTimeText ?? string.Empty),
                AlarmContentText = string.IsNullOrWhiteSpace(record?.AlarmContent) ? L("无") : record.AlarmContent,
                AiReplyText = string.IsNullOrWhiteSpace(record?.AiReply) ? L("无") : record.AiReply,
                McpCallText = string.IsNullOrWhiteSpace(record?.McpCall) ? L("无") : record.McpCall,
                VisionImagePathText = visionImagePath ?? string.Empty,
                PhenomenonText = record?.Phenomenon ?? string.Empty,
                ManualCountermeasureText = record?.ManualCountermeasure ?? string.Empty
            };
        }

        private static string ResolveVisionImagePath(AutoClearAlarmAudit.SummaryCsvRecord record)
        {
            var snapshot = ResolveVisionSnapshot(record);
            if (snapshot == null || !snapshot.IsVisionRelated)
            {
                return string.Empty;
            }

            return ResolveExistingImagePath(
                snapshot.AutoVisionProcessedImagePath,
                snapshot.AutoVisionSourceImagePath);
        }

        private static string GetPreferredVisionImagePath(AutoTraceSnapshot snapshot)
        {
            if (snapshot == null || !snapshot.IsVisionRelated)
            {
                return string.Empty;
            }

            return ResolveExistingImagePath(
                snapshot.AutoVisionProcessedImagePath,
                snapshot.AutoVisionSourceImagePath);
        }

        private static AutoTraceSnapshot ResolveVisionSnapshot(AutoClearAlarmAudit.SummaryCsvRecord record)
        {
            if (record == null)
            {
                return null;
            }

            var stateRoot = Path.Combine(AutoAuditStateRoot, AutoAuditStateDirName);
            if (!Directory.Exists(stateRoot))
            {
                return null;
            }

            try
            {
                return Directory.EnumerateFiles(stateRoot, "*.json", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(GetFileLastWriteTimeSafe)
                    .Select(ReadSnapshotFromFile)
                    .Where(snapshot => IsVisionSnapshotForRecord(snapshot, record))
                    .OrderByDescending(snapshot => snapshot.LastUpdatedAt == default ? snapshot.StartedAt : snapshot.LastUpdatedAt)
                    .FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        private static bool IsVisionSnapshotForRecord(AutoTraceSnapshot snapshot, AutoClearAlarmAudit.SummaryCsvRecord record)
        {
            if (snapshot == null || record == null || !snapshot.IsVisionRelated)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(ResolveExistingImagePath(snapshot.AutoVisionProcessedImagePath, snapshot.AutoVisionSourceImagePath)))
            {
                return false;
            }

            var recordTime = record.TriggeredAt;
            if (recordTime.HasValue && snapshot.StartedAt != default)
            {
                if (snapshot.StartedAt.Date != recordTime.Value.Date)
                {
                    return false;
                }

                if (Math.Abs((snapshot.StartedAt - recordTime.Value).TotalSeconds) > 2)
                {
                    return false;
                }
            }
            else if (snapshot.StartedAt != default && record.TriggerDate != default && snapshot.StartedAt.Date != record.TriggerDate.Date)
            {
                return false;
            }

            var recordAlarm = NormalizeRecordMatchText(record.AlarmContent);
            var snapshotAlarm = NormalizeRecordMatchText(ExtractAlarmContentFromInputMessage(snapshot.InputMessage));
            return string.IsNullOrWhiteSpace(recordAlarm)
                   || string.IsNullOrWhiteSpace(snapshotAlarm)
                   || string.Equals(recordAlarm, snapshotAlarm, StringComparison.Ordinal);
        }

        private static string ResolveExistingImagePath(params string[] paths)
        {
            foreach (var path in paths ?? Array.Empty<string>())
            {
                var normalized = (path ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    continue;
                }

                try
                {
                    if (File.Exists(normalized))
                    {
                        return normalized;
                    }
                }
                catch
                {
                    // 单个路径不可访问时继续尝试下一个路径。
                }
            }

            return string.Empty;
        }

        private static string ExtractAlarmContentFromInputMessage(string inputMessage)
        {
            var text = (inputMessage ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var lines = text
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (line.StartsWith("内容=", StringComparison.Ordinal))
                {
                    var value = line.Substring("内容=".Length).Trim();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }

            return text;
        }

        private static string NormalizeRecordMatchText(string value)
        {
            return (value ?? string.Empty)
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Trim();
        }

        private static string BuildNodeExecutionTitle(JObject node)
        {
            if (node == null)
            {
                return L("未命名节点");
            }

            var title = ShortenSingleLine(node.Value<string>("Title"), 80);
            if (!string.IsNullOrWhiteSpace(title))
            {
                return L(title);
            }

            return FormatAutoNodeTitle(node.Value<string>("Title"), node.Value<string>("NodeType"));
        }

        private static bool IsFinalStatus(string status)
        {
            return string.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(status, "rejected", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(status, "dispatch_failed", StringComparison.OrdinalIgnoreCase);
        }

        private static string MapStatusText(string status)
        {
            return NormalizeStatus(status) switch
            {
                "pending" => L("等待受理"),
                "accepted" => L("已受理"),
                "running" => L("分析中"),
                "succeeded" => L("已完成"),
                "failed" => L("执行失败"),
                "rejected" => L("未受理"),
                "dispatch_failed" => L("启动失败"),
                _ => L("未知状态")
            };
        }

        private static string ShortenSingleLine(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = value
                .Replace("\r", " ")
                .Replace("\n", " / ")
                .Trim();
            if (normalized.Length <= maxLength)
            {
                return normalized;
            }

            if (maxLength <= 3)
            {
                return normalized.Substring(0, maxLength);
            }

            return normalized.Substring(0, maxLength - 3) + "...";
        }

        private static string FormatAutoNodeTitle(string title, string nodeType)
        {
            var normalizedTitle = ShortenSingleLine(title, 60);
            if (!string.IsNullOrWhiteSpace(normalizedTitle))
            {
                return UiLanguageService.ApplyDisplayTextReplacements(normalizedTitle);
            }

            var normalizedType = (nodeType ?? string.Empty).Trim().ToLowerInvariant();
            return normalizedType switch
            {
                "start" => L("开始节点"),
                "llm" => L("AI 判断节点"),
                "agent" => L("Agent 节点"),
                "if_else" => L("条件判断节点"),
                "if-else" => L("条件判断节点"),
                "knowledge-retrieval" => L("知识库检索节点"),
                "http-request" => L("HTTP 请求节点"),
                "code" => L("代码执行节点"),
                "tool" => L("工具执行节点"),
                "output" => L("输出节点"),
                "end" => L("结束节点"),
                _ => L("未命名节点")
            };
        }

        private void SubscribeEvents()
        {
            if (_eventsSubscribed)
            {
                return;
            }

            MainWindow.AutoTriggeredNotified += MainWindow_AutoTriggeredNotified;
            MainWindow.AutoAcceptedNotified += MainWindow_AutoAcceptedNotified;
            MainWindow.AutoRejectedNotified += MainWindow_AutoRejectedNotified;
            MainWindow.AutoDispatchFailedNotified += MainWindow_AutoDispatchFailedNotified;
            MainWindow.AutoWorkflowStartedNotified += MainWindow_AutoWorkflowStartedNotified;
            MainWindow.AutoNodeStartedNotified += MainWindow_AutoNodeStartedNotified;
            MainWindow.AutoNodeFinishedNotified += MainWindow_AutoNodeFinishedNotified;
            MainWindow.AutoWorkflowFinishedNotified += MainWindow_AutoWorkflowFinishedNotified;
            ConfigService.ConfigChanged += ConfigService_ConfigChanged;
            _eventsSubscribed = true;
        }

        private void UnsubscribeEvents()
        {
            if (!_eventsSubscribed)
            {
                return;
            }

            MainWindow.AutoTriggeredNotified -= MainWindow_AutoTriggeredNotified;
            MainWindow.AutoAcceptedNotified -= MainWindow_AutoAcceptedNotified;
            MainWindow.AutoRejectedNotified -= MainWindow_AutoRejectedNotified;
            MainWindow.AutoDispatchFailedNotified -= MainWindow_AutoDispatchFailedNotified;
            MainWindow.AutoWorkflowStartedNotified -= MainWindow_AutoWorkflowStartedNotified;
            MainWindow.AutoNodeStartedNotified -= MainWindow_AutoNodeStartedNotified;
            MainWindow.AutoNodeFinishedNotified -= MainWindow_AutoNodeFinishedNotified;
            MainWindow.AutoWorkflowFinishedNotified -= MainWindow_AutoWorkflowFinishedNotified;
            ConfigService.ConfigChanged -= ConfigService_ConfigChanged;
            _eventsSubscribed = false;
        }

        private void ConfigService_ConfigChanged(object sender, AppConfig cfg)
        {
            _ = sender;
            _ = cfg;
            RequestRefresh();
        }

        private void MainWindow_AutoTriggeredNotified(string traceId, string machineCode, string prompt)
        {
            _ = traceId;
            _ = machineCode;
            _ = prompt;
            RequestRefresh();
        }

        private void MainWindow_AutoAcceptedNotified(string traceId)
        {
            _ = traceId;
            RequestRefresh();
        }

        private void MainWindow_AutoRejectedNotified(string traceId, string reason)
        {
            _ = traceId;
            _ = reason;
            RequestRefresh();
        }

        private void MainWindow_AutoDispatchFailedNotified(string traceId, string message)
        {
            _ = traceId;
            _ = message;
            RequestRefresh();
        }

        private void MainWindow_AutoWorkflowStartedNotified(string traceId, string workflowRunId, string taskId)
        {
            _ = traceId;
            _ = workflowRunId;
            _ = taskId;
            RequestRefresh();
        }

        private void MainWindow_AutoNodeStartedNotified(string traceId, string title, string nodeType)
        {
            _ = traceId;
            _ = title;
            _ = nodeType;
            RequestRefresh();
        }

        private void MainWindow_AutoNodeFinishedNotified(string traceId, string title, string nodeType, double? elapsedSeconds, string previewText)
        {
            _ = traceId;
            _ = title;
            _ = nodeType;
            _ = elapsedSeconds;
            _ = previewText;
            RequestRefresh();
        }

        private void MainWindow_AutoWorkflowFinishedNotified(string traceId, bool succeeded, string finalText)
        {
            _ = traceId;
            _ = succeeded;
            _ = finalText;
            RequestRefresh();
        }

        private void RequestRefresh()
        {
            if (_refreshScheduled)
            {
                _refreshPending = true;
                return;
            }

            _refreshScheduled = true;
            _ = Dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    await RefreshViewAsync();
                }
                finally
                {
                    _refreshScheduled = false;
                }
            }));
        }

        private void BtnShowRecentRecords_Click(object sender, RoutedEventArgs e)
        {
            SetRecordTab(showArchivedRecords: false);
        }

        private void BtnShowArchivedRecords_Click(object sender, RoutedEventArgs e)
        {
            SetRecordTab(showArchivedRecords: true);
        }

        private void BtnJumpToToday_Click(object sender, RoutedEventArgs e)
        {
            SelectedRecordDate = DateTime.Today;
        }

        private void SetRecordTab(bool showArchivedRecords)
        {
            if (_showArchivedRecords == showArchivedRecords)
            {
                return;
            }

            _showArchivedRecords = showArchivedRecords;
            OnPropertyChanged(nameof(IsRecentRecordsTabActive));
            OnPropertyChanged(nameof(IsArchivedRecordsTabActive));
        }

        private async void TraceRecordButton_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is FrameworkElement element))
            {
                return;
            }

            var recordKey = element.Tag as string;
            if (string.IsNullOrWhiteSpace(recordKey))
            {
                return;
            }

            var record = ResolveSummaryCsvRecordByKey(recordKey);
            if (record == null)
            {
                MainWindow.PostProgramInfo("[ExceptionDiagnosis] " + L("打开记录失败：未找到对应 CSV 记录。"), "warn");
                return;
            }

            var visionImagePath = ResolveVisionImagePath(record);
            CurrentVisionImagePath = visionImagePath;

            var dialog = new ExceptionDiagnosisRecordWindow(BuildRecordDialogViewModel(record, visionImagePath))
            {
                Owner = Window.GetWindow(this)
            };

            if (dialog.ShowDialog() == true)
            {
                var saved = AutoClearAlarmAudit.SaveSummaryCsvRecord(
                    dialog.ViewModel.CsvPathText,
                    record.RowIndex,
                    record.TriggerTimeText,
                    record.AlarmContent,
                    dialog.ViewModel.PhenomenonText,
                    dialog.ViewModel.ManualCountermeasureText);

                if (!saved)
                {
                    MainWindow.PostProgramInfo("[ExceptionDiagnosis] " + L("保存失败：未找到对应 CSV 行。"), "warn");
                    return;
                }

                MainWindow.PostProgramInfo("[ExceptionDiagnosis] " + L("已保存到本地 CSV。"), "ok");
                await RefreshViewAsync();
            }
        }

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public sealed class AutoTraceListItem
        {
            public string RecordKey { get; set; } = string.Empty;
            public string StatusText { get; set; } = string.Empty;
            public string StatusLevel { get; set; } = "info";
            public string StartedAtText { get; set; } = string.Empty;
            public string TitleText { get; set; } = string.Empty;
            public string SummaryText { get; set; } = string.Empty;
        }

        public sealed class CompactSequenceItem
        {
            public string IndexText { get; set; } = string.Empty;
            public string Title { get; set; } = string.Empty;
        }

        private sealed class RefreshPayload
        {
            public List<AutoTraceSnapshot> Snapshots { get; set; } = new List<AutoTraceSnapshot>();
            public List<AutoClearAlarmAudit.SummaryCsvRecord> CsvRecords { get; set; } =
                new List<AutoClearAlarmAudit.SummaryCsvRecord>();
        }

        private sealed class AutoTraceSnapshot
        {
            public string TraceId { get; set; } = string.Empty;
            public string Status { get; set; } = string.Empty;
            public string InputMessage { get; set; } = string.Empty;
            public string FinalReply { get; set; } = string.Empty;
            public DateTime StartedAt { get; set; }
            public DateTime LastUpdatedAt { get; set; }
            public DateTime? FinalizedAt { get; set; }
            public bool ClearMachineAlarmsCalled { get; set; }
            public bool IsVisionRelated { get; set; }
            public string AutoVisionSourceImagePath { get; set; } = string.Empty;
            public string AutoVisionProcessedImagePath { get; set; } = string.Empty;
            public List<JObject> McpToolCalls { get; } = new List<JObject>();
            public List<JObject> NodeExecutions { get; } = new List<JObject>();
        }
    }
}

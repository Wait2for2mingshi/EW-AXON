using EW_Assistant.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace EW_Assistant.ViewModels
{
    public sealed class PerformanceMonitorViewModel : ViewModelBase, IDisposable
    {
        private const int MaxAiHistory = 200;
        private const int MaxEvents = 500;
        private static readonly TimeSpan EventRetention = TimeSpan.FromDays(7);
        private readonly Dispatcher _dispatcher;
        private readonly PerformanceMonitorService _monitorService;
        private readonly PerformanceAutoAnalysisService _autoAnalysisService;
        private readonly AiPerformanceAnalysisService _aiService;
        private readonly AiAnalysisContextBuilder _contextBuilder = new AiAnalysisContextBuilder();
        private readonly AiAnalysisHistoryStore _historyStore = AiAnalysisHistoryStore.Instance;
        private float _totalCpuUsage;
        private string _aiAnalysisResult = string.Empty;
        private bool _isAiAnalyzing;
        private AiAnalysisRecord _latestAiRecord;
        private AiAnalysisRecord _selectedAiRecord;
        private string _selectedAiMarkdown = L("暂无 AI 分析结果");
        private MemorySnapshot _memorySnapshot;
        private string _cpuAlertMessage = string.Empty;
        private bool _isCpuAlertActive;
        private string _diskAlertMessage = string.Empty;
        private bool _isDiskAlertActive;
        private bool _isSubscribed;

        private static string L(string chineseText)
        {
            return UiLanguageService.CurrentText(chineseText);
        }

        public PerformanceMonitorViewModel(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            TopProcesses = new ObservableCollection<ProcessSnapshot>();
            Events = new ObservableCollection<PerformanceEvent>();
            AiHistory = new ObservableCollection<AiAnalysisRecord>();
            DiskSnapshots = new ObservableCollection<DiskUsageSnapshot>();
            _memorySnapshot = new MemorySnapshot();
            _aiService = new AiPerformanceAnalysisService();
            _monitorService = PerformanceMonitorService.Instance;
            _autoAnalysisService = PerformanceAutoAnalysisService.Instance;
        }

        public float TotalCpuUsage
        {
            get => _totalCpuUsage;
            private set => SetProperty(ref _totalCpuUsage, value);
        }

        public ObservableCollection<ProcessSnapshot> TopProcesses { get; }
        public ObservableCollection<PerformanceEvent> Events { get; }
        public ObservableCollection<AiAnalysisRecord> AiHistory { get; }
        public ObservableCollection<DiskUsageSnapshot> DiskSnapshots { get; }

        public string AiAnalysisResult
        {
            get => _aiAnalysisResult;
            private set => SetProperty(ref _aiAnalysisResult, value);
        }

        public bool IsAiAnalyzing
        {
            get => _isAiAnalyzing;
            private set => SetProperty(ref _isAiAnalyzing, value);
        }

        public AiAnalysisRecord LatestAiRecord
        {
            get => _latestAiRecord;
            private set => SetProperty(ref _latestAiRecord, value);
        }

        public AiAnalysisRecord SelectedAiRecord
        {
            get => _selectedAiRecord;
            set
            {
                if (SetProperty(ref _selectedAiRecord, value))
                {
                    UpdateSelectedAiMarkdown(value);
                }
            }
        }

        public string SelectedAiMarkdown
        {
            get => _selectedAiMarkdown;
            private set => SetProperty(ref _selectedAiMarkdown, value);
        }

        public MemorySnapshot CurrentMemory
        {
            get => _memorySnapshot;
            private set => SetProperty(ref _memorySnapshot, value);
        }

        public string CpuAlertMessage
        {
            get => _cpuAlertMessage;
            private set => SetProperty(ref _cpuAlertMessage, value);
        }

        public bool IsCpuAlertActive
        {
            get => _isCpuAlertActive;
            private set => SetProperty(ref _isCpuAlertActive, value);
        }

        public async Task TriggerTestCpuAlertAndAnalyzeAsync()
        {
            var testSnapshot = _monitorService.TriggerTestCpuAlert();
            ApplyCpuAlertState();

            if (IsAiAnalyzing)
                return;

            var eventsSnapshot = _monitorService.GetEventsSnapshot();
            var averageCpu = _monitorService.AverageCpuUsage5Min;
            var context = _contextBuilder.Build(testSnapshot, averageCpu, eventsSnapshot);
            await AnalyzeAsync(context);
        }

        public string DiskAlertMessage
        {
            get => _diskAlertMessage;
            private set => SetProperty(ref _diskAlertMessage, value);
        }

        public bool IsDiskAlertActive
        {
            get => _isDiskAlertActive;
            private set => SetProperty(ref _isDiskAlertActive, value);
        }

        public void Start()
        {
            _monitorService.Start();
            Subscribe();
            LoadAiHistory();
            RefreshFromService();
        }

        public void Stop()
        {
            Unsubscribe();
        }

        private void OnSnapshotUpdated(object sender, CpuSnapshotEventArgs e)
        {
            if (e == null || e.Snapshot == null)
                return;

            if (!_dispatcher.CheckAccess())
            {
                _dispatcher.BeginInvoke(new Action(() => ApplySnapshot(e.Snapshot)));
                return;
            }

            ApplySnapshot(e.Snapshot);
        }

        private void ApplySnapshot(CpuSnapshot snapshot)
        {
            TotalCpuUsage = snapshot.TotalCpuUsage;

            TopProcesses.Clear();
            foreach (var item in snapshot.TopProcesses)
            {
                TopProcesses.Add(item);
            }

            ApplyCpuAlertState();
        }

        private void OnMemorySnapshotUpdated(object sender, MemorySnapshotEventArgs e)
        {
            if (e == null || e.Snapshot == null)
                return;

            if (!_dispatcher.CheckAccess())
            {
                _dispatcher.BeginInvoke(new Action(() => UpdateMemorySnapshot(e.Snapshot)));
                return;
            }

            UpdateMemorySnapshot(e.Snapshot);
        }

        private void UpdateMemorySnapshot(MemorySnapshot snapshot)
        {
            CurrentMemory = snapshot;
        }

        private void OnDiskSnapshotUpdated(object sender, DiskUsageSnapshotEventArgs e)
        {
            if (e == null || e.Snapshots == null)
                return;

            if (!_dispatcher.CheckAccess())
            {
                _dispatcher.BeginInvoke(new Action(() => UpdateDiskSnapshotsAndAlert(e.Snapshots)));
                return;
            }

            UpdateDiskSnapshotsAndAlert(e.Snapshots);
        }

        private void UpdateDiskSnapshots(IReadOnlyList<DiskUsageSnapshot> snapshots)
        {
            DiskSnapshots.Clear();
            for (int i = 0; i < snapshots.Count; i++)
            {
                DiskSnapshots.Add(snapshots[i]);
            }
        }

        private void UpdateDiskSnapshotsAndAlert(IReadOnlyList<DiskUsageSnapshot> snapshots)
        {
            UpdateDiskSnapshots(snapshots);
            ApplyDiskAlertState();
        }

        private void OnPerformanceEventsRaised(object sender, PerformanceEventsEventArgs e)
        {
            if (e == null || e.Events == null)
                return;

            if (!_dispatcher.CheckAccess())
            {
                _dispatcher.BeginInvoke(new Action(() => OnPerformanceEventsRaised(sender, e)));
                return;
            }

            foreach (var evt in e.Events)
            {
                Events.Add(evt);
            }

            TrimEvents(DateTime.Now);
        }

        private void OnAutoAnalysisCompleted(object sender, AiAnalysisCompletedEventArgs e)
        {
            if (e == null || e.Record == null)
                return;

            InsertAiRecord(e.Record, false, false);
        }

        private void Subscribe()
        {
            if (_isSubscribed)
                return;

            _monitorService.CpuSnapshotUpdated += OnSnapshotUpdated;
            _monitorService.MemorySnapshotUpdated += OnMemorySnapshotUpdated;
            _monitorService.DiskSnapshotUpdated += OnDiskSnapshotUpdated;
            _monitorService.PerformanceEventsRaised += OnPerformanceEventsRaised;
            _autoAnalysisService.AnalysisCompleted += OnAutoAnalysisCompleted;
            _isSubscribed = true;
        }

        private void Unsubscribe()
        {
            if (!_isSubscribed)
                return;

            _monitorService.CpuSnapshotUpdated -= OnSnapshotUpdated;
            _monitorService.MemorySnapshotUpdated -= OnMemorySnapshotUpdated;
            _monitorService.DiskSnapshotUpdated -= OnDiskSnapshotUpdated;
            _monitorService.PerformanceEventsRaised -= OnPerformanceEventsRaised;
            _autoAnalysisService.AnalysisCompleted -= OnAutoAnalysisCompleted;
            _isSubscribed = false;
        }

        private void RefreshFromService()
        {
            var snapshot = _monitorService.LastCpuSnapshot;
            if (snapshot != null)
                ApplySnapshot(snapshot);

            var memory = _monitorService.LastMemorySnapshot;
            if (memory != null)
                UpdateMemorySnapshot(memory);

            var disks = _monitorService.LastDiskSnapshots;
            if (disks != null)
                UpdateDiskSnapshots(disks);
            ApplyDiskAlertState();

            ApplyCpuAlertState();

            var eventsSnapshot = _monitorService.GetEventsSnapshot();
            if (eventsSnapshot != null && eventsSnapshot.Count > 0)
            {
                Events.Clear();
                foreach (var evt in eventsSnapshot)
                {
                    Events.Add(evt);
                }

                TrimEvents(DateTime.Now);
            }
            else
            {
                Events.Clear();
            }
        }

        private void ApplyDiskAlertState()
        {
            DiskAlertMessage = _monitorService.DiskAlertMessage ?? string.Empty;
            IsDiskAlertActive = _monitorService.IsDiskAlertActive;
        }

        private void ApplyCpuAlertState()
        {
            CpuAlertMessage = _monitorService.CpuAlertMessage ?? string.Empty;
            IsCpuAlertActive = _monitorService.IsCpuAlertActive;
        }

        public async Task AnalyzeAsync(AiAnalysisContext context)
        {
            if (context == null)
                return;

            SetAiAnalyzing(true);
            try
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                var result = await _aiService.AnalyzeAsync(context, timeoutCts.Token).ConfigureAwait(false);
                AppendAiRecord(result, DateTime.Now);
            }
            catch (OperationCanceledException)
            {
                AppendAiRecord(L("性能 AI 分析超时，已在 2 分钟后中止。"), DateTime.Now);
            }
            catch (Exception ex)
            {
                AppendAiRecord(L("调用 AI 分析出错：") + ex.Message, DateTime.Now);
            }
            finally
            {
                SetAiAnalyzing(false);
            }
        }

        private void AppendAiRecord(string result, DateTime timestamp)
        {
            var record = AiAnalysisRecordBuilder.Build(result, timestamp);
            InsertAiRecord(record, true, true);
        }

        private void InsertAiRecord(AiAnalysisRecord record, bool persist, bool selectRecord)
        {
            if (record == null)
                return;

            if (!_dispatcher.CheckAccess())
            {
                _dispatcher.BeginInvoke(new Action(() => InsertAiRecord(record, persist, selectRecord)));
                return;
            }

            if (AiHistory.Count > 0 && AiHistory[0].Timestamp == record.Timestamp && AiHistory[0].Content == record.Content)
                return;

            LatestAiRecord = record;
            AiAnalysisResult = record.Content ?? string.Empty;
            AiHistory.Insert(0, record);
            if (selectRecord)
                SelectedAiRecord = record;
            TrimAiHistory(DateTime.Now);
            if (AiHistory.Count > MaxAiHistory)
                AiHistory.RemoveAt(AiHistory.Count - 1);

            if (persist)
                _historyStore.Append(record);
        }

        private void SetAiAnalyzing(bool value)
        {
            if (!_dispatcher.CheckAccess())
            {
                _dispatcher.BeginInvoke(new Action(() => SetAiAnalyzing(value)));
                return;
            }

            IsAiAnalyzing = value;
        }


        private void TrimAiHistory(DateTime now)
        {
            var cutoff = now.AddDays(-7);
            for (int i = AiHistory.Count - 1; i >= 0; i--)
            {
                if (AiHistory[i].Timestamp < cutoff)
                    AiHistory.RemoveAt(i);
            }
        }

        private void TrimEvents(DateTime now)
        {
            var cutoff = now - EventRetention;
            for (int i = Events.Count - 1; i >= 0; i--)
            {
                var evt = Events[i];
                if (evt == null)
                {
                    Events.RemoveAt(i);
                    continue;
                }

                if (evt.StartTime != default && evt.StartTime < cutoff)
                {
                    Events.RemoveAt(i);
                }
            }

            while (Events.Count > MaxEvents)
            {
                Events.RemoveAt(0);
            }
        }

        private void LoadAiHistory()
        {
            var snapshot = _historyStore.GetSnapshot();
            if (snapshot.Count > 0)
            {
                AiHistory.Clear();
                for (int i = 0; i < snapshot.Count; i++)
                {
                    AiHistory.Add(snapshot[i]);
                }

                LatestAiRecord = AiHistory[0];
                AiAnalysisResult = LatestAiRecord.Content;
                SelectedAiRecord = LatestAiRecord;
            }
            else
            {
                SelectedAiRecord = null;
            }
        }

        private void UpdateSelectedAiMarkdown(AiAnalysisRecord record)
        {
            if (record == null || string.IsNullOrWhiteSpace(record.Content))
            {
                SelectedAiMarkdown = L("暂无 AI 分析结果");
                return;
            }

            SelectedAiMarkdown = record.Content;
        }

        public void Dispose()
        {
            Unsubscribe();
        }
    }
}

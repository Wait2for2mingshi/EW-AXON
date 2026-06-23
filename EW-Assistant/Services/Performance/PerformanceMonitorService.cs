using System;
using System.Collections.Generic;
using EW_Assistant.Settings;

namespace EW_Assistant.Services
{
    public sealed class PerformanceEventsEventArgs : EventArgs
    {
        public PerformanceEventsEventArgs(IReadOnlyList<PerformanceEvent> eventsList)
        {
            Events = eventsList ?? throw new ArgumentNullException(nameof(eventsList));
        }

        public IReadOnlyList<PerformanceEvent> Events { get; }
    }

    /// <summary>
    /// 性能监控后台服务：持续采集并维护最新快照与规则事件。
    /// </summary>
    public sealed class PerformanceMonitorService : IDisposable
    {
        private static readonly Lazy<PerformanceMonitorService> InstanceLazy =
            new Lazy<PerformanceMonitorService>(() => new PerformanceMonitorService());

        private sealed class CpuUsageSample
        {
            public DateTime Timestamp { get; set; }
            public float Usage { get; set; }
        }

        private static readonly TimeSpan CpuAverageWindow = TimeSpan.FromMinutes(5);
        private readonly object _syncRoot = new object();
        private readonly LocalPerformanceCollector _cpuCollector;
        private readonly MemoryUsageCollector _memoryCollector;
        private readonly DiskUsageCollector _diskCollector;
        private readonly PerformanceRuleEngine _ruleEngine;
        private readonly DiskUsageRuleEngine _diskRuleEngine;
        private readonly Queue<CpuUsageSample> _cpuSamples = new Queue<CpuUsageSample>();
        private bool _started;
        private DateTime? _manualCpuAlertUntil;
        private string _manualCpuAlertMessage = string.Empty;

        private PerformanceMonitorService()
        {
            _cpuCollector = new LocalPerformanceCollector(null, TimeSpan.FromSeconds(1));
            _cpuCollector.SnapshotUpdated += OnCpuSnapshotUpdated;
            _memoryCollector = new MemoryUsageCollector(TimeSpan.FromSeconds(1));
            _memoryCollector.SnapshotUpdated += OnMemorySnapshotUpdated;
            _diskCollector = new DiskUsageCollector(TimeSpan.FromSeconds(15));
            _diskCollector.SnapshotUpdated += OnDiskSnapshotUpdated;
            _ruleEngine = new PerformanceRuleEngine();
            _diskRuleEngine = new DiskUsageRuleEngine();
            ApplyConfig(ConfigService.Current);
            ConfigService.ConfigChanged += OnConfigChanged;
        }

        public static PerformanceMonitorService Instance => InstanceLazy.Value;

        public CpuSnapshot LastCpuSnapshot { get; private set; } = new CpuSnapshot();
        public MemorySnapshot LastMemorySnapshot { get; private set; } = new MemorySnapshot();
        public IReadOnlyList<DiskUsageSnapshot> LastDiskSnapshots { get; private set; } =
            new List<DiskUsageSnapshot>();
        public string CpuAlertMessage { get; private set; } = string.Empty;
        public bool IsCpuAlertActive { get; private set; }
        public string DiskAlertMessage { get; private set; } = string.Empty;
        public bool IsDiskAlertActive { get; private set; }
        public float AverageCpuUsage5Min { get; private set; }

        public event EventHandler<CpuSnapshotEventArgs> CpuSnapshotUpdated;
        public event EventHandler<MemorySnapshotEventArgs> MemorySnapshotUpdated;
        public event EventHandler<DiskUsageSnapshotEventArgs> DiskSnapshotUpdated;
        public event EventHandler<PerformanceEventsEventArgs> PerformanceEventsRaised;

        public void Start()
        {
            var cfg = ConfigService.Current;
            if (cfg != null && !cfg.EnablePerformanceMonitor)
                return;

            lock (_syncRoot)
            {
                if (_started)
                    return;
                _started = true;
            }

            _cpuCollector.Start();
            _memoryCollector.Start();
            _diskCollector.Start();
        }

        public void Stop()
        {
            lock (_syncRoot)
            {
                if (!_started)
                    return;
                _started = false;
            }

            _cpuCollector.Stop();
            _memoryCollector.Stop();
            _diskCollector.Stop();
        }

        public IReadOnlyList<PerformanceEvent> GetEventsSnapshot()
        {
            return _ruleEngine.GetEventsSnapshot();
        }

        private void OnCpuSnapshotUpdated(object sender, CpuSnapshotEventArgs e)
        {
            var snapshot = e?.Snapshot;
            if (snapshot == null)
                return;

            IReadOnlyList<PerformanceEvent> newEvents;
            lock (_syncRoot)
            {
                LastCpuSnapshot = snapshot;
                newEvents = _ruleEngine.ProcessSnapshot(snapshot);
                UpdateCpuAverage(snapshot);
                var now = snapshot.Timestamp == default(DateTime) ? DateTime.Now : snapshot.Timestamp;
                if (_manualCpuAlertUntil.HasValue && _manualCpuAlertUntil.Value > now)
                {
                    CpuAlertMessage = _manualCpuAlertMessage;
                    IsCpuAlertActive = !string.IsNullOrWhiteSpace(_manualCpuAlertMessage);
                }
                else
                {
                    _manualCpuAlertUntil = null;
                    _manualCpuAlertMessage = string.Empty;
                    var cpuAlert = _ruleEngine.GetHighCpuAlertMessage(snapshot);
                    CpuAlertMessage = cpuAlert;
                    IsCpuAlertActive = !string.IsNullOrWhiteSpace(cpuAlert);
                }
            }

            CpuSnapshotUpdated?.Invoke(this, new CpuSnapshotEventArgs(snapshot));
            if (newEvents.Count > 0)
                PerformanceEventsRaised?.Invoke(this, new PerformanceEventsEventArgs(newEvents));
        }

        public CpuSnapshot TriggerTestCpuAlert(TimeSpan? duration = null)
        {
            PerformanceEvent testEvent;
            CpuSnapshot testSnapshot;
            lock (_syncRoot)
            {
                var now = DateTime.Now;
                var snapshot = LastCpuSnapshot ?? new CpuSnapshot { Timestamp = now };
                testSnapshot = new CpuSnapshot
                {
                    Timestamp = now,
                    TotalCpuUsage = Math.Max(snapshot.TotalCpuUsage, 99f),
                    TopProcesses = snapshot.TopProcesses ?? new List<ProcessSnapshot>()
                };

                var message = _ruleEngine.GetHighCpuAlertMessage(testSnapshot);
                if (string.IsNullOrWhiteSpace(message))
                    message = "CPU 高占用测试报警已触发。";
                message = "测试报警：" + message;

                _manualCpuAlertMessage = message;
                _manualCpuAlertUntil = now.Add(duration ?? TimeSpan.FromSeconds(10));
                CpuAlertMessage = _manualCpuAlertMessage;
                IsCpuAlertActive = true;

                testEvent = new PerformanceEvent
                {
                    EventType = "CPU_TOTAL_HIGH_TEST",
                    StartTime = now,
                    RelatedProcess = string.Empty,
                    Description = message
                };
                _ruleEngine.AppendEvents(new[] { testEvent });
            }

            PerformanceEventsRaised?.Invoke(this, new PerformanceEventsEventArgs(new[] { testEvent }));
            return testSnapshot;
        }

        private void OnMemorySnapshotUpdated(object sender, MemorySnapshotEventArgs e)
        {
            var snapshot = e?.Snapshot;
            if (snapshot == null)
                return;

            lock (_syncRoot)
            {
                LastMemorySnapshot = snapshot;
            }

            MemorySnapshotUpdated?.Invoke(this, e);
        }

        private void OnDiskSnapshotUpdated(object sender, DiskUsageSnapshotEventArgs e)
        {
            var snapshots = e?.Snapshots;
            if (snapshots == null)
                return;

            IReadOnlyList<PerformanceEvent> newEvents = Array.Empty<PerformanceEvent>();
            string alertMessage;
            lock (_syncRoot)
            {
                LastDiskSnapshots = snapshots;
                newEvents = _diskRuleEngine.ProcessSnapshot(e.Timestamp, snapshots);
                if (newEvents.Count > 0)
                    _ruleEngine.AppendEvents(newEvents);
                alertMessage = _diskRuleEngine.GetActiveAlertMessage();
                DiskAlertMessage = alertMessage;
                IsDiskAlertActive = !string.IsNullOrWhiteSpace(alertMessage);
            }

            DiskSnapshotUpdated?.Invoke(this, e);
            if (newEvents.Count > 0)
                PerformanceEventsRaised?.Invoke(this, new PerformanceEventsEventArgs(newEvents));
        }

        public void Dispose()
        {
            Stop();
            ConfigService.ConfigChanged -= OnConfigChanged;
            _cpuCollector.SnapshotUpdated -= OnCpuSnapshotUpdated;
            _cpuCollector.Dispose();
            _memoryCollector.SnapshotUpdated -= OnMemorySnapshotUpdated;
            _memoryCollector.Dispose();
            _diskCollector.SnapshotUpdated -= OnDiskSnapshotUpdated;
            _diskCollector.Dispose();
        }

        private void OnConfigChanged(object sender, AppConfig cfg)
        {
            ApplyConfig(cfg);
        }

        private void ApplyConfig(AppConfig cfg)
        {
            var threshold = cfg?.DiskUsageThresholdPercent ?? 90f;
            _diskRuleEngine.UpdateThreshold(threshold);
        }

        private void UpdateCpuAverage(CpuSnapshot snapshot)
        {
            var timestamp = snapshot.Timestamp == default(DateTime) ? DateTime.Now : snapshot.Timestamp;
            _cpuSamples.Enqueue(new CpuUsageSample
            {
                Timestamp = timestamp,
                Usage = snapshot.TotalCpuUsage
            });

            var cutoff = timestamp - CpuAverageWindow;
            while (_cpuSamples.Count > 0 && _cpuSamples.Peek().Timestamp < cutoff)
                _cpuSamples.Dequeue();

            double sum = 0;
            int count = 0;
            foreach (var sample in _cpuSamples)
            {
                sum += sample.Usage;
                count++;
            }

            AverageCpuUsage5Min = count > 0 ? (float)(sum / count) : snapshot.TotalCpuUsage;
        }
    }
}

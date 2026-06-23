using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace EW_Assistant.Services
{
    public sealed class AiAnalysisCompletedEventArgs : EventArgs
    {
        public AiAnalysisCompletedEventArgs(AiAnalysisRecord record)
        {
            Record = record;
        }

        public AiAnalysisRecord Record { get; }
    }

    /// <summary>
    /// 性能报警自动触发 AI 分析（后台运行，不依赖 UI）。
    /// </summary>
    public sealed class PerformanceAutoAnalysisService : IDisposable
    {
        private static readonly Lazy<PerformanceAutoAnalysisService> InstanceLazy =
            new Lazy<PerformanceAutoAnalysisService>(() => new PerformanceAutoAnalysisService());

        private static readonly TimeSpan AutoAnalysisCooldown = TimeSpan.FromHours(2);
        private static readonly TimeSpan TriggerHistoryRetention = AutoAnalysisCooldown;
        private static readonly TimeSpan AnalysisTimeout = TimeSpan.FromMinutes(2);
        private const int MaxTrackedTriggerCount = 64;

        private readonly object _syncRoot = new object();
        private readonly PerformanceMonitorService _monitorService;
        private readonly AiAnalysisContextBuilder _contextBuilder = new AiAnalysisContextBuilder();
        private readonly AiAnalysisHistoryStore _historyStore = AiAnalysisHistoryStore.Instance;
        private AiPerformanceAnalysisService _aiService;
        private CancellationTokenSource _stopCts = new CancellationTokenSource();
        private bool _started;
        private bool _isAnalyzing;
        private bool _hasPending;
        private readonly Dictionary<string, DateTime> _lastTriggerTimes =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        private PerformanceAutoAnalysisService()
        {
            _monitorService = PerformanceMonitorService.Instance;
            _aiService = new AiPerformanceAnalysisService();
        }

        public static PerformanceAutoAnalysisService Instance => InstanceLazy.Value;

        public event EventHandler<AiAnalysisCompletedEventArgs> AnalysisCompleted;

        public void Start()
        {
            var cfg = ConfigService.Current;
            if (cfg != null && !cfg.EnablePerformanceMonitor)
                return;

            lock (_syncRoot)
            {
                if (_started)
                    return;
                if (_stopCts.IsCancellationRequested)
                    _stopCts = new CancellationTokenSource();
                _started = true;
            }

            _monitorService.PerformanceEventsRaised += OnPerformanceEventsRaised;
        }

        public void Stop()
        {
            lock (_syncRoot)
            {
                if (!_started)
                    return;
                _started = false;
                _hasPending = false;
                try { _stopCts.Cancel(); } catch { }
            }

            _monitorService.PerformanceEventsRaised -= OnPerformanceEventsRaised;
        }

        private void OnPerformanceEventsRaised(object sender, PerformanceEventsEventArgs e)
        {
            if (e == null || e.Events == null || e.Events.Count == 0)
                return;

            var now = DateTime.Now;
            lock (_syncRoot)
            {
                if (!TryMarkTrigger(e.Events, now))
                    return;

                if (_isAnalyzing)
                {
                    _hasPending = true;
                    return;
                }

                _isAnalyzing = true;
            }

            var context = BuildContext();
            if (context == null)
            {
                lock (_syncRoot)
                {
                    _isAnalyzing = false;
                    _hasPending = false;
                }
                return;
            }

            _ = AnalyzeWithPendingAsync(context, GetStopToken());
        }

        private bool TryMarkTrigger(IReadOnlyList<PerformanceEvent> eventsList, DateTime now)
        {
            CleanupOldTriggers(now);

            // 同一类报警短时间内只触发一次，避免频繁占用 AI 资源。
            for (int i = 0; i < eventsList.Count; i++)
            {
                var evt = eventsList[i];
                if (evt == null || IsTestEvent(evt))
                    continue;

                var key = BuildEventKey(evt);
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                if (_lastTriggerTimes.TryGetValue(key, out var last) && now - last < AutoAnalysisCooldown)
                    continue;

                _lastTriggerTimes[key] = now;
                TrimTrackedTriggerCount();
                return true;
            }

            return false;
        }

        private void CleanupOldTriggers(DateTime now)
        {
            if (_lastTriggerTimes.Count == 0)
                return;

            var cutoff = now - TriggerHistoryRetention;
            var toRemove = new List<string>();
            foreach (var pair in _lastTriggerTimes)
            {
                if (pair.Value < cutoff)
                    toRemove.Add(pair.Key);
            }

            for (int i = 0; i < toRemove.Count; i++)
                _lastTriggerTimes.Remove(toRemove[i]);
        }

        private void TrimTrackedTriggerCount()
        {
            if (_lastTriggerTimes.Count <= MaxTrackedTriggerCount)
                return;

            var ordered = _lastTriggerTimes
                .OrderBy(pair => pair.Value)
                .ToList();

            var removeCount = _lastTriggerTimes.Count - MaxTrackedTriggerCount;
            for (int i = 0; i < removeCount && i < ordered.Count; i++)
            {
                _lastTriggerTimes.Remove(ordered[i].Key);
            }
        }

        private AiAnalysisContext BuildContext()
        {
            var snapshot = _monitorService.LastCpuSnapshot ?? new CpuSnapshot { Timestamp = DateTime.Now };
            var eventsSnapshot = _monitorService.GetEventsSnapshot();
            var averageCpu = _monitorService.AverageCpuUsage5Min;
            return _contextBuilder.Build(snapshot, averageCpu, eventsSnapshot);
        }

        private static bool ShouldAutoAnalyze(IReadOnlyList<PerformanceEvent> eventsList)
        {
            for (int i = 0; i < eventsList.Count; i++)
            {
                var evt = eventsList[i];
                if (evt == null)
                    continue;

                if (!IsTestEvent(evt))
                    return true;
            }

            return false;
        }

        private static bool IsTestEvent(PerformanceEvent evt)
        {
            if (evt == null || string.IsNullOrWhiteSpace(evt.EventType))
                return false;

            return evt.EventType.IndexOf("TEST", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string BuildEventKey(PerformanceEvent evt)
        {
            if (evt == null)
                return string.Empty;

            var type = evt.EventType ?? string.Empty;
            var related = evt.RelatedProcess ?? string.Empty;
            if (string.IsNullOrWhiteSpace(type) && string.IsNullOrWhiteSpace(related))
                return string.Empty;

            return string.Concat(type, "|", related);
        }

        private async Task AnalyzeWithPendingAsync(AiAnalysisContext context, CancellationToken stopToken)
        {
            while (true)
            {
                AiAnalysisRecord record;
                try
                {
                    stopToken.ThrowIfCancellationRequested();
                    using var timeoutCts = new CancellationTokenSource(AnalysisTimeout);
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, stopToken);
                    var result = await _aiService.AnalyzeAsync(context, linkedCts.Token).ConfigureAwait(false);
                    record = AiAnalysisRecordBuilder.Build(result, DateTime.Now);
                }
                catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
                {
                    lock (_syncRoot)
                    {
                        _isAnalyzing = false;
                        _hasPending = false;
                    }
                    return;
                }
                catch (OperationCanceledException)
                {
                    record = AiAnalysisRecordBuilder.Build("性能 AI 分析超时，已在 " + (int)AnalysisTimeout.TotalMinutes + " 分钟后中止。", DateTime.Now);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(ex);
                    record = AiAnalysisRecordBuilder.Build("调用 AI 分析出错：" + ex.Message, DateTime.Now);
                }

                if (stopToken.IsCancellationRequested)
                {
                    lock (_syncRoot)
                    {
                        _isAnalyzing = false;
                        _hasPending = false;
                    }
                    return;
                }

                _historyStore.Append(record);
                AnalysisCompleted?.Invoke(this, new AiAnalysisCompletedEventArgs(record));

                bool shouldContinue;
                lock (_syncRoot)
                {
                    if (!_started || stopToken.IsCancellationRequested)
                    {
                        _isAnalyzing = false;
                        _hasPending = false;
                        return;
                    }

                    if (_hasPending)
                    {
                        _hasPending = false;
                        shouldContinue = true;
                    }
                    else
                    {
                        _isAnalyzing = false;
                        shouldContinue = false;
                    }
                }

                if (!shouldContinue)
                    return;

                stopToken.ThrowIfCancellationRequested();
                var nextContext = BuildContext();
                if (nextContext == null)
                {
                    lock (_syncRoot)
                    {
                        _isAnalyzing = false;
                    }
                    return;
                }

                context = nextContext;
            }
        }

        private CancellationToken GetStopToken()
        {
            var cts = _stopCts;
            return cts == null ? CancellationToken.None : cts.Token;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}

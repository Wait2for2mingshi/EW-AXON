using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace EW_Assistant.Services
{
    public sealed class PerformanceRuleEngine
    {
        private const float HighCpuThreshold = 80f;
        private const int MaxStoredEvents = 500;
        private static readonly TimeSpan EventRetention = TimeSpan.FromDays(7);

        private readonly object _syncRoot = new object();
        private readonly List<PerformanceEvent> _events = new List<PerformanceEvent>();
        private bool _highCpuActive;
        private DateTime _highCpuStart;

        /// <summary>返回当前事件列表快照。</summary>
        public IReadOnlyList<PerformanceEvent> GetEventsSnapshot()
        {
            lock (_syncRoot)
            {
                TrimExpiredEventsUnsafe(DateTime.Now);
                return _events.ToList();
            }
        }

        /// <summary>追加外部事件到列表。</summary>
        public void AppendEvents(IEnumerable<PerformanceEvent> eventsList)
        {
            if (eventsList == null)
                return;

            lock (_syncRoot)
            {
                foreach (var evt in eventsList)
                {
                    if (evt != null)
                        _events.Add(evt);
                }

                TrimExpiredEventsUnsafe(DateTime.Now);
            }
        }

        /// <summary>处理一次采样并返回新增事件。</summary>
        public IReadOnlyList<PerformanceEvent> ProcessSnapshot(CpuSnapshot snapshot)
        {
            if (snapshot == null)
                return Array.Empty<PerformanceEvent>();

            var newEvents = new List<PerformanceEvent>();
            lock (_syncRoot)
            {
                EvaluateHighCpu(snapshot, newEvents);

                if (newEvents.Count > 0)
                {
                    _events.AddRange(newEvents);
                    TrimExpiredEventsUnsafe(snapshot.Timestamp == default ? DateTime.Now : snapshot.Timestamp);
                }
            }

            return newEvents;
        }

        public string GetHighCpuAlertMessage(CpuSnapshot snapshot)
        {
            if (snapshot == null)
                return string.Empty;

            if (snapshot.TotalCpuUsage < HighCpuThreshold)
                return string.Empty;

            var processSummary = BuildProcessSummary(snapshot.TopProcesses, 3);
            if (string.IsNullOrWhiteSpace(processSummary))
            {
                return string.Format("CPU 总占用率 {0:F1}% 超过阈值 {1:F1}%。",
                    snapshot.TotalCpuUsage, HighCpuThreshold);
            }

            return string.Format("CPU 总占用率 {0:F1}% 超过阈值 {1:F1}%。高占用进程：{2}",
                snapshot.TotalCpuUsage, HighCpuThreshold, processSummary);
        }

        private void EvaluateHighCpu(CpuSnapshot snapshot, List<PerformanceEvent> newEvents)
        {
            if (snapshot.TotalCpuUsage >= HighCpuThreshold)
            {
                if (!_highCpuActive)
                {
                    _highCpuActive = true;
                    _highCpuStart = snapshot.Timestamp;
                    var description = GetHighCpuAlertMessage(snapshot);
                    newEvents.Add(new PerformanceEvent
                    {
                        EventType = "CPU_TOTAL_HIGH",
                        StartTime = _highCpuStart,
                        RelatedProcess = string.Empty,
                        Description = description
                    });
                }
            }
            else
            {
                _highCpuActive = false;
                _highCpuStart = DateTime.MinValue;
            }
        }

        private static string BuildProcessSummary(IReadOnlyList<ProcessSnapshot> processes, int maxCount)
        {
            if (processes == null || processes.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();
            var count = Math.Min(maxCount, processes.Count);
            for (int i = 0; i < count; i++)
            {
                var proc = processes[i];
                if (proc == null)
                    continue;

                if (sb.Length > 0)
                    sb.Append("；");

                var name = string.IsNullOrWhiteSpace(proc.Name) ? "未知进程" : proc.Name;
                sb.Append(name);
                sb.Append(" (PID=");
                sb.Append(proc.Pid);
                sb.Append(") ");
                sb.Append(proc.CpuUsage.ToString("F1"));
                sb.Append("%");
            }

            return sb.ToString();
        }

        private void TrimExpiredEventsUnsafe(DateTime now)
        {
            var cutoff = now - EventRetention;
            for (int i = _events.Count - 1; i >= 0; i--)
            {
                var evt = _events[i];
                if (evt == null)
                {
                    _events.RemoveAt(i);
                    continue;
                }

                if (evt.StartTime != default && evt.StartTime < cutoff)
                {
                    _events.RemoveAt(i);
                }
            }

            if (_events.Count > MaxStoredEvents)
            {
                _events.RemoveRange(0, _events.Count - MaxStoredEvents);
            }
        }
    }
}

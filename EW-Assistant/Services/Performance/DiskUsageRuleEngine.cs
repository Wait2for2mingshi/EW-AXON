using System;
using System.Collections.Generic;
using System.Text;

namespace EW_Assistant.Services
{
    /// <summary>
    /// 磁盘使用率规则引擎（仅处理固定磁盘容量报警）。
    /// </summary>
    public sealed class DiskUsageRuleEngine
    {
        private sealed class DiskUsageState
        {
            public float LastUsagePercent { get; set; }
        }

        private float _thresholdPercent = 90f;

        private readonly object _syncRoot = new object();
        private readonly Dictionary<string, DiskUsageState> _states =
            new Dictionary<string, DiskUsageState>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<PerformanceEvent> ProcessSnapshot(DateTime timestamp, IReadOnlyList<DiskUsageSnapshot> snapshots)
        {
            if (snapshots == null || snapshots.Count == 0)
                return Array.Empty<PerformanceEvent>();

            var newEvents = new List<PerformanceEvent>();
            lock (_syncRoot)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < snapshots.Count; i++)
                {
                    var item = snapshots[i];
                    if (item == null)
                        continue;

                    var drive = item.DriveLetter ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(drive))
                        continue;

                    seen.Add(drive);
                    if (item.UsagePercent >= _thresholdPercent)
                    {
                        if (!_states.TryGetValue(drive, out var state))
                        {
                            state = new DiskUsageState();
                            _states[drive] = state;
                            newEvents.Add(new PerformanceEvent
                            {
                                EventType = "DISK_USAGE_HIGH",
                                StartTime = timestamp,
                                RelatedProcess = drive,
                                Description = string.Format("磁盘 {0} 使用率超过 {1:F1}%，当前 {2:F1}%。",
                                    drive, _thresholdPercent, item.UsagePercent)
                            });
                        }

                        state.LastUsagePercent = item.UsagePercent;
                    }
                    else
                    {
                        _states.Remove(drive);
                    }
                }

                if (seen.Count > 0)
                {
                    var toRemove = new List<string>();
                    foreach (var key in _states.Keys)
                    {
                        if (!seen.Contains(key))
                            toRemove.Add(key);
                    }

                    for (int i = 0; i < toRemove.Count; i++)
                        _states.Remove(toRemove[i]);
                }
            }

            return newEvents;
        }

        public string GetActiveAlertMessage()
        {
            lock (_syncRoot)
            {
                StringBuilder builder = null;
                foreach (var pair in _states)
                {
                    var state = pair.Value;
                    if (state == null)
                        continue;

                    var message = string.Format("磁盘 {0} 使用率超过 {1:F1}%，当前 {2:F1}%。",
                        pair.Key, _thresholdPercent, state.LastUsagePercent);
                    if (builder == null)
                    {
                        builder = new StringBuilder(message);
                    }
                    else
                    {
                        builder.AppendLine();
                        builder.Append(message);
                    }
                }

                return builder == null ? string.Empty : builder.ToString();
            }
        }

        public void UpdateThreshold(float thresholdPercent)
        {
            lock (_syncRoot)
            {
                if (thresholdPercent <= 0f)
                    thresholdPercent = 90f;
                if (thresholdPercent > 100f)
                    thresholdPercent = 100f;

                _thresholdPercent = thresholdPercent;

                _states.Clear();
            }
        }
    }
}

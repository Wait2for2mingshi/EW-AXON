using System;
using System.Collections.Generic;

namespace EW_Assistant.Services
{
    /// <summary>
    /// CPU 使用趋势分析（维护最近 15 分钟窗口）。
    /// </summary>
    public sealed class CpuTrendAnalyzer
    {
        private sealed class CpuUsageSample
        {
            public DateTime Timestamp { get; set; }
            public float Usage { get; set; }
        }

        private static readonly TimeSpan Window1Min = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan Window5Min = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan Window15Min = TimeSpan.FromMinutes(15);
        private const float FluctuationThreshold = 10f;

        private readonly object _syncRoot = new object();
        private readonly Queue<CpuUsageSample> _samples = new Queue<CpuUsageSample>();

        public CpuTrendSnapshot Update(float usagePercent, DateTime timestamp)
        {
            if (timestamp == default(DateTime))
                timestamp = DateTime.Now;

            var clamped = ClampPercent(usagePercent);
            lock (_syncRoot)
            {
                _samples.Enqueue(new CpuUsageSample
                {
                    Timestamp = timestamp,
                    Usage = clamped
                });
                TrimOld(timestamp);
                return BuildSnapshot(timestamp);
            }
        }

        public CpuTrendSnapshot GetSnapshot(DateTime now)
        {
            if (now == default(DateTime))
                now = DateTime.Now;

            lock (_syncRoot)
            {
                TrimOld(now);
                return BuildSnapshot(now);
            }
        }

        private void TrimOld(DateTime now)
        {
            var cutoff = now - Window15Min;
            while (_samples.Count > 0 && _samples.Peek().Timestamp < cutoff)
                _samples.Dequeue();
        }

        private CpuTrendSnapshot BuildSnapshot(DateTime now)
        {
            if (_samples.Count == 0)
            {
                return new CpuTrendSnapshot
                {
                    Avg1Min = 0,
                    Avg5Min = 0,
                    Avg15Min = 0,
                    Max = 0,
                    Min = 0,
                    Fluctuation = 0,
                    StabilityLevel = "Stable"
                };
            }

            var avg1 = ComputeAverage(now - Window1Min);
            var avg5 = ComputeAverage(now - Window5Min);
            var avg15 = ComputeAverage(now - Window15Min);

            float max = 0f;
            float min = 100f;
            foreach (var sample in _samples)
            {
                if (sample.Usage > max) max = sample.Usage;
                if (sample.Usage < min) min = sample.Usage;
            }

            if (min > max)
            {
                min = max;
            }

            var fluctuation = max - min;
            var stability = fluctuation >= FluctuationThreshold ? "Fluctuating" : "Stable";

            return new CpuTrendSnapshot
            {
                Avg1Min = avg1,
                Avg5Min = avg5,
                Avg15Min = avg15,
                Max = max,
                Min = min,
                Fluctuation = fluctuation,
                StabilityLevel = stability
            };
        }

        private float ComputeAverage(DateTime windowStart)
        {
            double sum = 0;
            int count = 0;
            foreach (var sample in _samples)
            {
                if (sample.Timestamp >= windowStart)
                {
                    sum += sample.Usage;
                    count++;
                }
            }

            if (count == 0) return 0f;
            return (float)(sum / count);
        }

        private static float ClampPercent(float value)
        {
            if (value < 0f) return 0f;
            if (value > 100f) return 100f;
            return value;
        }
    }
}

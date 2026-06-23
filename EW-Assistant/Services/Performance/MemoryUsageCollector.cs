using Microsoft.VisualBasic.Devices;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace EW_Assistant.Services
{
    public sealed class MemorySnapshotEventArgs : EventArgs
    {
        public MemorySnapshotEventArgs(MemorySnapshot snapshot)
        {
            Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        }

        public MemorySnapshot Snapshot { get; }
    }

    /// <summary>
    /// 本机内存使用率采集器（默认 1 秒采样一次）。
    /// </summary>
    public sealed class MemoryUsageCollector : IDisposable
    {
        private readonly TimeSpan _interval;
        private readonly object _syncRoot = new object();
        private ComputerInfo _computerInfo;
        private Timer _timer;
        private int _capturing;

        public MemoryUsageCollector(TimeSpan? interval = null)
        {
            var value = interval ?? TimeSpan.FromSeconds(1);
            if (value < TimeSpan.FromSeconds(1))
                value = TimeSpan.FromSeconds(1);
            _interval = value;
        }

        public MemorySnapshot LastSnapshot { get; private set; } = new MemorySnapshot();

        public event EventHandler<MemorySnapshotEventArgs> SnapshotUpdated;

        public void Start()
        {
            lock (_syncRoot)
            {
                if (_timer != null)
                {
                    _timer.Change(_interval, _interval);
                    return;
                }

                _timer = new Timer(OnTimer, null, _interval, _interval);
            }
        }

        public void Stop()
        {
            lock (_syncRoot)
            {
                _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                _timer?.Dispose();
                _timer = null;
            }
        }

        public Task<MemorySnapshot> CaptureOnceAsync()
        {
            return Task.Run(() => CaptureAndPublish());
        }

        private void OnTimer(object state)
        {
            CaptureAndPublish();
        }

        private MemorySnapshot CaptureAndPublish()
        {
            if (Interlocked.Exchange(ref _capturing, 1) == 1)
                return LastSnapshot;

            try
            {
                var snapshot = CaptureSnapshotInternal();
                PublishSnapshot(snapshot);
                return snapshot;
            }
            finally
            {
                Interlocked.Exchange(ref _capturing, 0);
            }
        }

        private void PublishSnapshot(MemorySnapshot snapshot)
        {
            if (snapshot == null)
                return;

            LastSnapshot = snapshot;
            var handler = SnapshotUpdated;
            if (handler != null)
                handler(this, new MemorySnapshotEventArgs(snapshot));
        }

        private MemorySnapshot CaptureSnapshotInternal()
        {
            var now = DateTime.Now;
            long totalMb = 0;
            long availableMb = 0;

            try
            {
                var info = EnsureComputerInfo();
                if (info == null)
                    throw new InvalidOperationException("ComputerInfo 初始化失败。");

                var totalBytes = info.TotalPhysicalMemory;
                var availableBytes = info.AvailablePhysicalMemory;
                totalMb = BytesToMb(totalBytes);
                availableMb = BytesToMb(availableBytes);
            }
            catch
            {
                totalMb = 0;
                availableMb = 0;
            }

            var usedMb = totalMb > 0 ? Math.Max(0, totalMb - availableMb) : 0;
            var usagePercent = totalMb > 0 ? (float)(usedMb * 100.0 / totalMb) : 0f;

            return new MemorySnapshot
            {
                Timestamp = now,
                TotalMemoryMb = totalMb,
                UsedMemoryMb = usedMb,
                AvailableMemoryMb = availableMb,
                UsagePercent = ClampPercent(usagePercent)
            };
        }

        private static long BytesToMb(ulong bytes)
        {
            return (long)(bytes / 1024 / 1024);
        }

        private static float ClampPercent(float value)
        {
            if (value < 0f) return 0f;
            if (value > 100f) return 100f;
            return value;
        }

        private ComputerInfo EnsureComputerInfo()
        {
            if (_computerInfo != null)
                return _computerInfo;

            try
            {
                _computerInfo = new ComputerInfo();
            }
            catch
            {
                _computerInfo = null;
            }

            return _computerInfo;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}

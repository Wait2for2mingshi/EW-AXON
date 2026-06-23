using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EW_Assistant.Services
{
    public sealed class DiskUsageSnapshotEventArgs : EventArgs
    {
        public DiskUsageSnapshotEventArgs(DateTime timestamp, IReadOnlyList<DiskUsageSnapshot> snapshots)
        {
            Timestamp = timestamp;
            Snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        }

        public DateTime Timestamp { get; }
        public IReadOnlyList<DiskUsageSnapshot> Snapshots { get; }
    }

    /// <summary>
    /// 固定磁盘容量采集器（默认 15 秒刷新一次）。
    /// </summary>
    public sealed class DiskUsageCollector : IDisposable
    {
        private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan MaxInterval = TimeSpan.FromSeconds(30);

        private readonly TimeSpan _interval;
        private readonly object _syncRoot = new object();
        private Timer _timer;
        private int _capturing;

        public DiskUsageCollector(TimeSpan? interval = null)
        {
            var value = interval ?? TimeSpan.FromSeconds(15);
            if (value < MinInterval) value = MinInterval;
            if (value > MaxInterval) value = MaxInterval;
            _interval = value;
        }

        public IReadOnlyList<DiskUsageSnapshot> LastSnapshots { get; private set; } =
            new List<DiskUsageSnapshot>();

        public event EventHandler<DiskUsageSnapshotEventArgs> SnapshotUpdated;

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

        public Task<IReadOnlyList<DiskUsageSnapshot>> CaptureOnceAsync()
        {
            return Task.Run(() => CaptureAndPublish());
        }

        private void OnTimer(object state)
        {
            CaptureAndPublish();
        }

        private IReadOnlyList<DiskUsageSnapshot> CaptureAndPublish()
        {
            if (Interlocked.Exchange(ref _capturing, 1) == 1)
                return LastSnapshots;

            try
            {
                var timestamp = DateTime.Now;
                var snapshots = CaptureSnapshotInternal();
                PublishSnapshot(timestamp, snapshots);
                return snapshots;
            }
            finally
            {
                Interlocked.Exchange(ref _capturing, 0);
            }
        }

        private void PublishSnapshot(DateTime timestamp, IReadOnlyList<DiskUsageSnapshot> snapshots)
        {
            if (snapshots == null)
                return;

            LastSnapshots = snapshots;
            var handler = SnapshotUpdated;
            if (handler != null)
                handler(this, new DiskUsageSnapshotEventArgs(timestamp, snapshots));
        }

        private static IReadOnlyList<DiskUsageSnapshot> CaptureSnapshotInternal()
        {
            var list = new List<DiskUsageSnapshot>();
            DriveInfo[] drives;
            try
            {
                drives = DriveInfo.GetDrives();
            }
            catch
            {
                drives = new DriveInfo[0];
            }

            foreach (var drive in drives)
            {
                if (drive == null)
                    continue;
                if (drive.DriveType != DriveType.Fixed)
                    continue;
                if (!drive.IsReady)
                    continue;

                try
                {
                    var totalBytes = drive.TotalSize;
                    var freeBytes = drive.AvailableFreeSpace;
                    var usedBytes = totalBytes - freeBytes;

                    var totalGb = BytesToGb(totalBytes);
                    var freeGb = BytesToGb(freeBytes);
                    var usagePercent = totalBytes > 0 ? (float)(usedBytes * 100.0 / totalBytes) : 0f;

                    list.Add(new DiskUsageSnapshot
                    {
                        DriveLetter = TrimDriveLetter(drive.Name),
                        TotalGb = totalGb,
                        FreeGb = freeGb,
                        UsagePercent = ClampPercent(usagePercent)
                    });
                }
                catch
                {
                    // 单盘异常时跳过，继续采集其他磁盘
                }
            }

            return list;
        }

        private static string TrimDriveLetter(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return string.Empty;
            return name.TrimEnd('\\');
        }

        private static long BytesToGb(long bytes)
        {
            return bytes / 1024 / 1024 / 1024;
        }

        private static float ClampPercent(float value)
        {
            if (value < 0f) return 0f;
            if (value > 100f) return 100f;
            return value;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}

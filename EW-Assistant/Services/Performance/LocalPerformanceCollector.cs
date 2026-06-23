using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace EW_Assistant.Services
{
    public sealed class CpuSnapshot
    {
        public DateTime Timestamp { get; set; }
        public float TotalCpuUsage { get; set; }
        public List<ProcessSnapshot> TopProcesses { get; set; } = new List<ProcessSnapshot>();
    }

    public sealed class ProcessSnapshot
    {
        public string Name { get; set; } = string.Empty;
        public int Pid { get; set; }
        public float CpuUsage { get; set; }
        public long MemoryMb { get; set; }
        public TimeSpan Uptime { get; set; }
    }

    public sealed class CpuSnapshotEventArgs : EventArgs
    {
        public CpuSnapshotEventArgs(CpuSnapshot snapshot)
        {
            Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        }

        public CpuSnapshot Snapshot { get; }
    }

    public sealed class LocalPerformanceCollector : IDisposable
    {
        private const int ProcessQueryInformation = 0x0400;
        private const int ProcessQueryLimitedInformation = 0x1000;
        private const int ProcessVmRead = 0x0010;

        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME
        {
            public uint dwLowDateTime;
            public uint dwHighDateTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_MEMORY_COUNTERS
        {
            public int cb;
            public int PageFaultCount;
            public long PeakWorkingSetSize;
            public long WorkingSetSize;
            public long QuotaPeakPagedPoolUsage;
            public long QuotaPagedPoolUsage;
            public long QuotaPeakNonPagedPoolUsage;
            public long QuotaNonPagedPoolUsage;
            public long PagefileUsage;
            public long PeakPagefileUsage;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int desiredAccess, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessTimes(
            IntPtr hProcess,
            out FILETIME creationTime,
            out FILETIME exitTime,
            out FILETIME kernelTime,
            out FILETIME userTime);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(
            IntPtr hProcess,
            int flags,
            StringBuilder exeName,
            ref int size);

        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessMemoryInfo(
            IntPtr hProcess,
            out PROCESS_MEMORY_COUNTERS counters,
            int size);

        private sealed class ProcessCpuSample
        {
            public DateTime SampleTimeUtc { get; set; }
            public TimeSpan TotalProcessorTime { get; set; }
        }

        private readonly Dispatcher _dispatcher;
        private readonly TimeSpan _interval;
        private readonly Dictionary<int, ProcessCpuSample> _lastProcessSamples = new Dictionary<int, ProcessCpuSample>();
        private readonly object _syncRoot = new object();
        private Timer _timer;
        private PerformanceCounter _totalCpuCounter;
        private bool _totalCpuReady;
        private bool _totalCpuInitFailed;
        private int _capturing;

        public LocalPerformanceCollector(Dispatcher dispatcher, TimeSpan? interval = null)
        {
            _dispatcher = dispatcher;
            _interval = interval ?? TimeSpan.FromSeconds(2);

            _totalCpuCounter = null;
        }

        public CpuSnapshot LastSnapshot { get; private set; } = new CpuSnapshot();

        public event EventHandler<CpuSnapshotEventArgs> SnapshotUpdated;

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

        public Task<CpuSnapshot> CaptureOnceAsync()
        {
            return Task.Run(() => CaptureAndPublish());
        }

        private void OnTimer(object state)
        {
            CaptureAndPublish();
        }

        private CpuSnapshot CaptureAndPublish()
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

        private void PublishSnapshot(CpuSnapshot snapshot)
        {
            if (snapshot == null)
                return;

            LastSnapshot = snapshot;
            var handler = SnapshotUpdated;
            if (handler == null)
                return;

            if (_dispatcher != null && !_dispatcher.HasShutdownStarted && !_dispatcher.HasShutdownFinished)
            {
                _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => handler(this, new CpuSnapshotEventArgs(snapshot))));
                return;
            }

            handler(this, new CpuSnapshotEventArgs(snapshot));
        }

        private CpuSnapshot CaptureSnapshotInternal()
        {
            var now = DateTime.Now;
            var nowUtc = DateTime.UtcNow;
            Process[] processes;
            try
            {
                processes = Process.GetProcesses();
            }
            catch
            {
                processes = Array.Empty<Process>();
            }

            var alivePids = new HashSet<int>();
            var snapshots = new List<ProcessSnapshot>(processes.Length);
            float sumCpuUsage = 0f;

            foreach (var process in processes)
            {
                try
                {
                    var pid = process.Id;
                    alivePids.Add(pid);

                    string name = string.Empty;
                    TryGetProcessName(pid, out name);

                    float cpuUsage = 0f;
                    if (TryGetTotalProcessorTime(pid, out var processCpuTime))
                    {
                        if (_lastProcessSamples.TryGetValue(pid, out var lastSample))
                        {
                            var elapsedMs = (nowUtc - lastSample.SampleTimeUtc).TotalMilliseconds;
                            var deltaMs = (processCpuTime - lastSample.TotalProcessorTime).TotalMilliseconds;
                            if (elapsedMs > 0 && deltaMs >= 0)
                            {
                                cpuUsage = (float)(deltaMs / (elapsedMs * Environment.ProcessorCount) * 100.0);
                                cpuUsage = ClampPercent(cpuUsage);
                            }
                        }

                        _lastProcessSamples[pid] = new ProcessCpuSample
                        {
                            SampleTimeUtc = nowUtc,
                            TotalProcessorTime = processCpuTime
                        };
                    }

                    sumCpuUsage += cpuUsage;

                    snapshots.Add(new ProcessSnapshot
                    {
                        Name = name,
                        Pid = pid,
                        CpuUsage = cpuUsage
                    });
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }

            CleanupMissingProcesses(alivePids);

            var top = snapshots
                .OrderByDescending(p => p.CpuUsage)
                .ThenBy(p => p.Pid)
                .Take(5)
                .ToList();

            FillProcessDetails(top, now);

            var totalCpu = ReadTotalCpuUsage(sumCpuUsage);

            return new CpuSnapshot
            {
                Timestamp = now,
                TotalCpuUsage = totalCpu,
                TopProcesses = top
            };
        }

        private static void FillProcessDetails(List<ProcessSnapshot> top, DateTime now)
        {
            if (top == null || top.Count == 0)
                return;

            foreach (var item in top)
            {
                if (item == null)
                    continue;

                if (TryGetWorkingSet(item.Pid, out var workingSet))
                    item.MemoryMb = workingSet / 1024 / 1024;

                if (TryGetStartTime(item.Pid, out var startTimeUtc))
                {
                    var startTime = startTimeUtc.ToLocalTime();
                    if (startTime <= now)
                        item.Uptime = now - startTime;
                }
            }
        }

        private void CleanupMissingProcesses(HashSet<int> alivePids)
        {
            if (alivePids.Count == 0)
            {
                _lastProcessSamples.Clear();
                return;
            }

            var dead = new List<int>();
            foreach (var pid in _lastProcessSamples.Keys)
            {
                if (!alivePids.Contains(pid))
                    dead.Add(pid);
            }

            foreach (var pid in dead)
                _lastProcessSamples.Remove(pid);
        }

        private float ReadTotalCpuUsage(float sumCpuUsage)
        {
            EnsureTotalCpuCounter();
            if (_totalCpuCounter == null)
                return ClampPercent(sumCpuUsage);

            try
            {
                if (!_totalCpuReady)
                {
                    _totalCpuCounter.NextValue();
                    _totalCpuReady = true;
                    return ClampPercent(sumCpuUsage);
                }

                return ClampPercent(_totalCpuCounter.NextValue());
            }
            catch
            {
                return ClampPercent(sumCpuUsage);
            }
        }

        private void EnsureTotalCpuCounter()
        {
            if (_totalCpuCounter != null || _totalCpuInitFailed)
                return;

            try
            {
                _totalCpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                _totalCpuReady = false;
            }
            catch
            {
                _totalCpuCounter = null;
                _totalCpuInitFailed = true;
            }
        }

        private static float ClampPercent(float value)
        {
            if (value < 0f) return 0f;
            if (value > 100f) return 100f;
            return value;
        }

        private static bool TryOpenProcess(int pid, int access, out IntPtr handle)
        {
            handle = OpenProcess(access, false, pid);
            if (handle != IntPtr.Zero)
                return true;

            if ((access & ProcessQueryLimitedInformation) != 0)
            {
                var fallbackAccess = (access & ~ProcessQueryLimitedInformation) | ProcessQueryInformation;
                handle = OpenProcess(fallbackAccess, false, pid);
            }

            return handle != IntPtr.Zero;
        }

        private static long FileTimeToTicks(FILETIME time)
        {
            return ((long)time.dwHighDateTime << 32) | time.dwLowDateTime;
        }

        private static bool TryGetProcessTimes(int pid, out TimeSpan totalCpu, out DateTime startTimeUtc)
        {
            totalCpu = TimeSpan.Zero;
            startTimeUtc = DateTime.MinValue;

            if (!TryOpenProcess(pid, ProcessQueryLimitedInformation, out var handle))
                return false;

            try
            {
                if (!GetProcessTimes(handle, out var creation, out _, out var kernel, out var user))
                    return false;

                var totalTicks = FileTimeToTicks(kernel) + FileTimeToTicks(user);
                if (totalTicks < 0)
                    totalTicks = 0;
                totalCpu = TimeSpan.FromTicks(totalTicks);

                var creationTicks = FileTimeToTicks(creation);
                if (creationTicks > 0)
                    startTimeUtc = DateTime.FromFileTimeUtc(creationTicks);

                return true;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        private static bool TryGetTotalProcessorTime(int pid, out TimeSpan totalCpu)
        {
            return TryGetProcessTimes(pid, out totalCpu, out _);
        }

        private static bool TryGetWorkingSet(int pid, out long workingSet)
        {
            workingSet = 0;

            if (!TryOpenProcess(pid, ProcessQueryLimitedInformation | ProcessVmRead, out var handle))
                return false;

            try
            {
                var counters = new PROCESS_MEMORY_COUNTERS
                {
                    cb = Marshal.SizeOf(typeof(PROCESS_MEMORY_COUNTERS))
                };

                if (!GetProcessMemoryInfo(handle, out counters, counters.cb))
                    return false;

                workingSet = counters.WorkingSetSize;
                return workingSet >= 0;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        private static bool TryGetStartTime(int pid, out DateTime startTimeUtc)
        {
            if (!TryGetProcessTimes(pid, out _, out startTimeUtc))
                return false;

            return startTimeUtc != DateTime.MinValue;
        }

        private static bool TryGetProcessName(int pid, out string name)
        {
            name = string.Empty;

            if (!TryOpenProcess(pid, ProcessQueryLimitedInformation, out var handle))
                return false;

            try
            {
                var builder = new StringBuilder(512);
                var size = builder.Capacity;
                if (!QueryFullProcessImageName(handle, 0, builder, ref size) || size <= 0)
                    return false;

                var path = builder.ToString(0, size);
                if (string.IsNullOrWhiteSpace(path))
                    return false;

                name = Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrEmpty(name))
                    name = path;

                return true;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        public void Dispose()
        {
            Stop();
            _totalCpuCounter?.Dispose();
            _totalCpuCounter = null;
        }
    }
}

using EW_Assistant.Services;
using EW_Assistant.Warnings;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace EW_Assistant.Services.Warnings
{
    /// <summary>
    /// 预警后台监控快照，供 UI 按需渲染。
    /// </summary>
    public sealed class WarningMonitorSnapshot
    {
        public static WarningMonitorSnapshot Empty
        {
            get
            {
                return new WarningMonitorSnapshot
                {
                    UpdatedAt = DateTime.MinValue,
                    LastUpdatedText = string.Empty,
                    Tickets = new List<WarningTicketRecord>(),
                    Analyses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                };
            }
        }

        public DateTime UpdatedAt { get; set; }
        public string LastUpdatedText { get; set; }
        public List<WarningTicketRecord> Tickets { get; set; }
        public Dictionary<string, string> Analyses { get; set; }
    }

    /// <summary>
    /// 预警快照更新事件参数。
    /// </summary>
    public sealed class WarningMonitorSnapshotEventArgs : EventArgs
    {
        public WarningMonitorSnapshotEventArgs(WarningMonitorSnapshot snapshot)
        {
            Snapshot = snapshot ?? WarningMonitorSnapshot.Empty;
        }

        public WarningMonitorSnapshot Snapshot { get; }
    }

    /// <summary>
    /// 预警后台监控服务，负责文件轮询、工单状态维护与 AI 分析补齐。
    /// </summary>
    public sealed class WarningMonitorService : IDisposable
    {
        private const int MaxAttemptedKeyCount = 256;
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

        private readonly object _lock = new object();
        private readonly Dictionary<string, WarningItem> _warningMap =
            new Dictionary<string, WarningItem>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, WarningTicketRecord> _ticketMap =
            new Dictionary<string, WarningTicketRecord>(StringComparer.OrdinalIgnoreCase);
        private readonly List<WarningTicketRecord> _allTickets = new List<WarningTicketRecord>();
        private readonly HashSet<string> _aiAttemptedKeys =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly WarningAnalysisCache _analysisCache;
        private readonly IWarningTicketStore _ticketStore;

        private WarningRuleOptions _options;
        private WarningMonitorSnapshot _snapshot;
        private AiWarningAnalysisService _aiService;
        private Timer _refreshTimer;
        private CancellationTokenSource _stopCts = new CancellationTokenSource();
        private DateTime _lastAlarmWriteTime = DateTime.MinValue;
        private DateTime _lastProdWriteTime = DateTime.MinValue;
        private int _isStarted;
        private int _isRefreshing;
        private int _isAnalyzing;
        private bool _ticketsLoaded;
        private bool _disposed;

        public static WarningMonitorService Instance { get; } = new WarningMonitorService();

        private WarningMonitorService()
        {
            _analysisCache = new WarningAnalysisCache(null);
            _ticketStore = new JsonWarningTicketStore(null);
            _options = WarningRuleOptions.Normalize(ConfigService.Current?.WarningOptions);
            _aiService = new AiWarningAnalysisService();
            _snapshot = WarningMonitorSnapshot.Empty;
            _analysisCache.Load();
        }

        public event EventHandler<WarningMonitorSnapshotEventArgs> SnapshotUpdated;

        public void Start()
        {
            if (_disposed || Interlocked.Exchange(ref _isStarted, 1) == 1)
            {
                return;
            }

            lock (_lock)
            {
                if (_stopCts.IsCancellationRequested)
                {
                    _stopCts = new CancellationTokenSource();
                }

                _analysisCache.Load();
                _options = WarningRuleOptions.Normalize(ConfigService.Current?.WarningOptions);
                _aiService = new AiWarningAnalysisService();
                LoadTicketsUnsafe(DateTime.Now);
            }

            ConfigService.ConfigChanged += OnConfigChanged;
            _refreshTimer = new Timer(OnRefreshTimerTick, null, PollInterval, PollInterval);
            var _ = RefreshNowAsync(true);
        }

        public void Stop()
        {
            if (Interlocked.Exchange(ref _isStarted, 0) == 0)
            {
                return;
            }

            ConfigService.ConfigChanged -= OnConfigChanged;
            try
            {
                _stopCts.Cancel();
            }
            catch
            {
                // 忽略退出阶段的取消异常
            }

            var timer = Interlocked.Exchange(ref _refreshTimer, null);
            try
            {
                timer?.Dispose();
            }
            catch
            {
                // 忽略退出阶段的释放异常
            }
        }

        public WarningMonitorSnapshot GetSnapshot()
        {
            lock (_lock)
            {
                return CloneSnapshot(_snapshot);
            }
        }

        public Task RefreshNowAsync(bool force = false)
        {
            return RefreshCoreAsync(force, GetStopToken());
        }

        public async Task AnalyzeMissingWarningsAsync()
        {
            await AnalyzeMissingWarningsAsync(GetStopToken()).ConfigureAwait(false);
        }

        private async Task AnalyzeMissingWarningsAsync(CancellationToken token)
        {
            if (_disposed || Volatile.Read(ref _isStarted) == 0)
            {
                return;
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            if (Interlocked.Exchange(ref _isAnalyzing, 1) == 1)
            {
                return;
            }

            try
            {
                List<WarningItem> pendingItems;
                var anyAnalysisSaved = false;
                lock (_lock)
                {
                    pendingItems = _warningMap.Values
                        .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Key))
                        .Where(item =>
                        {
                            WarningAnalysisRecord record;
                            return !_analysisCache.TryGet(item.Key, out record) || record == null || string.IsNullOrWhiteSpace(record.AiMarkdown);
                        })
                        .Where(item => !_aiAttemptedKeys.Contains(item.Key))
                        .Select(CloneWarningItem)
                        .ToList();
                }

                foreach (var item in pendingItems)
                {
                    token.ThrowIfCancellationRequested();

                    if (item == null || string.IsNullOrWhiteSpace(item.Key))
                    {
                        continue;
                    }

                    var result = await _aiService.AnalyzeAsync(item, token).ConfigureAwait(false);
                    WarningMonitorSnapshot snapshotToPublish = null;

                    lock (_lock)
                    {
                        _aiAttemptedKeys.Add(item.Key);
                        PruneAttemptedKeysUnsafe();

                        if (result != null && result.IsSuccess && !string.IsNullOrWhiteSpace(result.Markdown))
                        {
                            anyAnalysisSaved = _analysisCache.Upsert(new WarningAnalysisRecord
                            {
                                Key = item.Key,
                                RuleId = item.RuleId,
                                RuleName = item.RuleName,
                                Level = item.Level,
                                Type = item.Type,
                                StartTime = item.StartTime,
                                EndTime = item.EndTime,
                                AiMarkdown = result.Markdown,
                                EngineVersion = string.Empty
                            }, saveImmediately: false) || anyAnalysisSaved;

                            snapshotToPublish = BuildSnapshotUnsafe(DateTime.Now);
                            _snapshot = snapshotToPublish;
                        }
                    }

                    if (snapshotToPublish != null)
                    {
                        PublishSnapshot(snapshotToPublish);
                    }
                }

                token.ThrowIfCancellationRequested();
                if (anyAnalysisSaved)
                {
                    _analysisCache.Save();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // 程序退出或服务停止时取消在途分析，不作为失败提示。
            }
            catch (Exception ex)
            {
                MainWindow.PostProgramInfo("预警 AI 分析失败：" + ex.Message, "warn");
            }
            finally
            {
                Interlocked.Exchange(ref _isAnalyzing, 0);
            }
        }

        public bool MarkProcessed(string fingerprint)
        {
            if (string.IsNullOrWhiteSpace(fingerprint))
            {
                return false;
            }

            WarningMonitorSnapshot snapshotToPublish = null;

            lock (_lock)
            {
                WarningTicketRecord ticket;
                if (!_ticketMap.TryGetValue(fingerprint, out ticket) || ticket == null)
                {
                    return false;
                }

                ticket.Status = "Processed";
                ticket.ProcessedAt = DateTime.Now;
                ticket.LastSeen = DateTime.Now;
                ticket.UpdatedAt = DateTime.Now;
                SaveTicketsUnsafe();

                snapshotToPublish = BuildSnapshotUnsafe(DateTime.Now);
                _snapshot = snapshotToPublish;
            }

            PublishSnapshot(snapshotToPublish);
            return true;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Stop();
        }

        private void OnRefreshTimerTick(object state)
        {
            var _ = RefreshNowAsync(false);
        }

        private void OnConfigChanged(object sender, Settings.AppConfig cfg)
        {
            lock (_lock)
            {
                _options = WarningRuleOptions.Normalize(cfg?.WarningOptions);
                _aiService = new AiWarningAnalysisService();
                _aiAttemptedKeys.Clear();
                _lastAlarmWriteTime = DateTime.MinValue;
                _lastProdWriteTime = DateTime.MinValue;
                LoadTicketsUnsafe(DateTime.Now);
            }

            var _ = RefreshNowAsync(true);
        }

        private async Task RefreshCoreAsync(bool force, CancellationToken token)
        {
            if (_disposed || Volatile.Read(ref _isStarted) == 0)
            {
                return;
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            if (Interlocked.Exchange(ref _isRefreshing, 1) == 1)
            {
                return;
            }

            try
            {
                var latestAlarm = GetLatestAlarmWriteTime();
                var latestProd = GetLatestProductionWriteTime();
                var shouldRefresh = force;

                lock (_lock)
                {
                    if (latestAlarm > _lastAlarmWriteTime)
                    {
                        _lastAlarmWriteTime = latestAlarm;
                        shouldRefresh = true;
                    }

                    if (latestProd > _lastProdWriteTime)
                    {
                        _lastProdWriteTime = latestProd;
                        shouldRefresh = true;
                    }
                }

                if (!shouldRefresh)
                {
                    return;
                }

                token.ThrowIfCancellationRequested();
                var now = DateTime.Now;
                IList<WarningTicketRecord> newTickets;
                WarningMonitorSnapshot snapshotToPublish;

                lock (_lock)
                {
                    if (!_ticketsLoaded)
                    {
                        LoadTicketsUnsafe(now);
                    }

                    var warnings = BuildWarningsUnsafe(now);
                    newTickets = MergeTicketsUnsafe(warnings, now);
                    PruneAttemptedKeysUnsafe();

                    snapshotToPublish = BuildSnapshotUnsafe(now);
                    _snapshot = snapshotToPublish;
                }

                token.ThrowIfCancellationRequested();
                PublishSnapshot(snapshotToPublish);
                BroadcastNewTickets(newTickets);
                await AnalyzeMissingWarningsAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // 停止服务时取消本轮刷新，不记录为异常。
            }
            catch (Exception ex)
            {
                MainWindow.PostProgramInfo("预警后台刷新失败：" + ex.Message, "warn");
            }
            finally
            {
                Interlocked.Exchange(ref _isRefreshing, 0);
            }
        }

        private CancellationToken GetStopToken()
        {
            var cts = _stopCts;
            return cts == null ? CancellationToken.None : cts.Token;
        }

        private List<WarningItem> BuildWarningsUnsafe(DateTime now)
        {
            _warningMap.Clear();

            var prodReader = new ProductionCsvReader(LocalDataConfig.ProductionCsvRoot);
            var alarmReader = new AlarmCsvReader(LocalDataConfig.AlarmCsvRoot);
            var engine = new WarningRuleEngine(prodReader, alarmReader);
            var items = engine.BuildWarnings(now) ?? new List<WarningItem>();
            var normalized = new List<WarningItem>();

            foreach (var item in items)
            {
                if (item == null)
                {
                    continue;
                }

                var fingerprint = BuildFingerprint(item);
                if (string.IsNullOrWhiteSpace(fingerprint))
                {
                    continue;
                }

                item.Key = fingerprint;
                _warningMap[fingerprint] = CloneWarningItem(item);
                normalized.Add(CloneWarningItem(item));
            }

            return normalized;
        }

        private IList<WarningTicketRecord> MergeTicketsUnsafe(IList<WarningItem> items, DateTime now)
        {
            var newTickets = new List<WarningTicketRecord>();
            var ticketsChanged = false;

            foreach (var ticket in _ticketMap.Values)
            {
                ticketsChanged = NormalizeExistingTicketUnsafe(ticket, now) || ticketsChanged;
            }

            foreach (var item in items ?? new List<WarningItem>())
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Key))
                {
                    continue;
                }

                WarningTicketRecord ticket;
                if (!_ticketMap.TryGetValue(item.Key, out ticket))
                {
                    ticket = new WarningTicketRecord
                    {
                        Fingerprint = item.Key,
                        RuleId = item.RuleId,
                        RuleName = item.RuleName,
                        Level = item.Level,
                        Type = item.Type,
                        StartTime = item.StartTime,
                        EndTime = item.EndTime,
                        Summary = item.Summary,
                        MetricName = item.MetricName,
                        CurrentValue = item.CurrentValue,
                        BaselineValue = item.BaselineValue,
                        ThresholdValue = item.ThresholdValue,
                        Status = "Active",
                        CreatedAt = now,
                        UpdatedAt = now,
                        FirstSeen = item.StartTime,
                        LastSeen = now,
                        OccurrenceCount = 1
                    };
                    _ticketMap[item.Key] = ticket;
                    newTickets.Add(CloneTicket(ticket));
                    ticketsChanged = true;
                }
                else
                {
                    ticketsChanged = true;
                    ticket.RuleId = item.RuleId;
                    ticket.RuleName = item.RuleName;
                    ticket.Level = item.Level;
                    ticket.Type = item.Type;
                    ticket.StartTime = item.StartTime;
                    ticket.EndTime = item.EndTime;
                    ticket.Summary = item.Summary;
                    ticket.MetricName = item.MetricName;
                    ticket.CurrentValue = item.CurrentValue;
                    ticket.BaselineValue = item.BaselineValue;
                    ticket.ThresholdValue = item.ThresholdValue;
                    ticket.LastSeen = now;
                    ticket.UpdatedAt = now;
                    ticket.OccurrenceCount = ticket.OccurrenceCount <= 0 ? 1 : ticket.OccurrenceCount + 1;

                    var status = (ticket.Status ?? string.Empty).Trim();
                    if (string.Equals(status, "Resolved", StringComparison.OrdinalIgnoreCase))
                    {
                        ticket.Status = "Active";
                    }
                    else if (string.Equals(status, "Ignored", StringComparison.OrdinalIgnoreCase)
                        && ticket.IgnoredUntil.HasValue
                        && ticket.IgnoredUntil.Value < now)
                    {
                        ticket.Status = "Active";
                        ticket.IgnoredUntil = null;
                    }
                    else if (string.Equals(status, "Processed", StringComparison.OrdinalIgnoreCase))
                    {
                        ticket.Status = "Processed";
                    }
                }
            }

            RebuildAllTicketsUnsafe();

            if (ticketsChanged)
            {
                SaveTicketsUnsafe();
            }

            return newTickets;
        }

        private WarningMonitorSnapshot BuildSnapshotUnsafe(DateTime now)
        {
            var analyses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ticket in _allTickets)
            {
                if (ticket == null || string.IsNullOrWhiteSpace(ticket.Fingerprint))
                {
                    continue;
                }

                WarningAnalysisRecord record;
                if (_analysisCache.TryGet(ticket.Fingerprint, out record)
                    && record != null
                    && !string.IsNullOrWhiteSpace(record.AiMarkdown))
                {
                    analyses[ticket.Fingerprint] = record.AiMarkdown;
                }
            }

            return new WarningMonitorSnapshot
            {
                UpdatedAt = now,
                LastUpdatedText = "上次更新：" + now.ToString("HH:mm:ss"),
                Tickets = _allTickets.Select(CloneTicket).ToList(),
                Analyses = analyses
            };
        }

        private void PublishSnapshot(WarningMonitorSnapshot snapshot)
        {
            var handler = SnapshotUpdated;
            if (handler == null)
            {
                return;
            }

            try
            {
                handler(this, new WarningMonitorSnapshotEventArgs(CloneSnapshot(snapshot)));
            }
            catch
            {
                // 单个订阅者异常不影响后台服务主流程
            }
        }

        private void BroadcastNewTickets(IList<WarningTicketRecord> tickets)
        {
            if (tickets == null || tickets.Count == 0)
            {
                return;
            }

            foreach (var ticket in tickets)
            {
                if (ticket == null)
                {
                    continue;
                }

                var level = MapLevelDisplay(ticket.Level);
                var type = MapTypeDisplay(ticket.Type);
                var title = string.IsNullOrEmpty(ticket.RuleName) ? "预警" : ticket.RuleName;
                var time = ticket.StartTime.ToString("MM-dd HH:mm");
                var summary = BuildDisplaySummary(ticket);
                var metric = string.IsNullOrEmpty(ticket.MetricName) ? string.Empty : ("，指标：" + ticket.MetricName);
                var message = string.Format("【预警触发】[{0}/{1}] {2} @ {3}{4}。{5}",
                    level,
                    type,
                    title,
                    time,
                    metric,
                    summary);
                MainWindow.PostProgramInfo(message, "warn");
            }
        }

        private void SaveTicketsUnsafe()
        {
            _ticketStore.SaveAll(_allTickets);
        }

        private void LoadTicketsUnsafe(DateTime now)
        {
            _ticketMap.Clear();
            _allTickets.Clear();

            var existing = _ticketStore.LoadAll() ?? new List<WarningTicketRecord>();
            var changed = false;
            foreach (var ticket in existing)
            {
                if (ticket == null || string.IsNullOrWhiteSpace(ticket.Fingerprint))
                {
                    continue;
                }

                changed = NormalizeExistingTicketUnsafe(ticket, now) || changed;
                _ticketMap[ticket.Fingerprint] = ticket;
            }

            RebuildAllTicketsUnsafe();
            _ticketsLoaded = true;

            if (changed)
            {
                SaveTicketsUnsafe();
            }
        }

        private bool NormalizeExistingTicketUnsafe(WarningTicketRecord ticket, DateTime now)
        {
            if (ticket == null)
            {
                return false;
            }

            var changed = false;
            var status = (ticket.Status ?? string.Empty).Trim();
            if (ticket.LastSeen == default(DateTime))
            {
                ticket.LastSeen = ticket.CreatedAt == default(DateTime) ? now : ticket.CreatedAt;
                changed = true;
            }

            if (ticket.LastSeen != default(DateTime))
            {
                var minutesSinceSeen = (now - ticket.LastSeen).TotalMinutes;
                if (minutesSinceSeen > _options.ResolveGraceMinutes
                    && !string.Equals(status, "Resolved", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(status, "Processed", StringComparison.OrdinalIgnoreCase))
                {
                    ticket.Status = "Resolved";
                    ticket.UpdatedAt = now;
                    changed = true;
                }
            }

            return changed;
        }

        private void RebuildAllTicketsUnsafe()
        {
            _allTickets.Clear();
            foreach (var pair in _ticketMap)
            {
                if (pair.Value != null)
                {
                    _allTickets.Add(pair.Value);
                }
            }
        }

        private void PruneAttemptedKeysUnsafe()
        {
            if (_aiAttemptedKeys.Count == 0)
            {
                return;
            }

            var activeKeys = new HashSet<string>(
                _warningMap.Keys.Where(key => !string.IsNullOrWhiteSpace(key)),
                StringComparer.OrdinalIgnoreCase);

            _aiAttemptedKeys.RemoveWhere(key => string.IsNullOrWhiteSpace(key) || !activeKeys.Contains(key));
            if (_aiAttemptedKeys.Count <= MaxAttemptedKeyCount)
            {
                return;
            }

            var keepKeys = new HashSet<string>(
                _warningMap.Keys
                    .Where(key => !string.IsNullOrWhiteSpace(key))
                    .Take(MaxAttemptedKeyCount),
                StringComparer.OrdinalIgnoreCase);

            _aiAttemptedKeys.RemoveWhere(key => !keepKeys.Contains(key));
        }

        private DateTime GetLatestAlarmWriteTime()
        {
            try
            {
                var root = LocalDataConfig.AlarmCsvRoot;
                var watchMode = LocalDataConfig.WatchMode;
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                {
                    return _lastAlarmWriteTime;
                }

                return watchMode
                    ? GetLatestWatchModeWriteTime(root, _lastAlarmWriteTime)
                    : GetLatestFlatWriteTime(root, _lastAlarmWriteTime);
            }
            catch
            {
                return _lastAlarmWriteTime;
            }
        }

        private DateTime GetLatestProductionWriteTime()
        {
            try
            {
                var root = LocalDataConfig.ProductionCsvRoot;
                var watchMode = LocalDataConfig.WatchMode;
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                {
                    return _lastProdWriteTime;
                }

                return watchMode
                    ? GetLatestWatchModeWriteTime(root, _lastProdWriteTime)
                    : GetLatestFlatWriteTime(root, _lastProdWriteTime);
            }
            catch
            {
                return _lastProdWriteTime;
            }
        }

        private static DateTime GetLatestFlatWriteTime(string root, DateTime fallback)
        {
            var max = DateTime.MinValue;
            var files = Directory.GetFiles(root, "*.csv", SearchOption.TopDirectoryOnly);
            foreach (var path in files)
            {
                var time = File.GetLastWriteTime(path);
                if (time > max)
                {
                    max = time;
                }
            }

            return max == DateTime.MinValue ? fallback : max;
        }

        private static DateTime GetLatestWatchModeWriteTime(string root, DateTime fallback)
        {
            var max = DateTime.MinValue;
            var dirs = Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly);
            foreach (var dir in dirs)
            {
                var name = Path.GetFileName(dir);
                DateTime _;
                if (!DateTime.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                {
                    continue;
                }

                var files = Directory.GetFiles(dir, "*.csv", SearchOption.TopDirectoryOnly);
                foreach (var path in files)
                {
                    var time = File.GetLastWriteTime(path);
                    if (time > max)
                    {
                        max = time;
                    }
                }
            }

            return max == DateTime.MinValue ? fallback : max;
        }

        private static string BuildFingerprint(WarningItem item)
        {
            if (item == null)
            {
                return string.Empty;
            }

            var dimension = string.IsNullOrWhiteSpace(item.MetricName) ? "default" : item.MetricName.Trim();
            return string.Format(CultureInfo.InvariantCulture,
                "{0}|{1}|{2:yyyyMMddHH}|{3}",
                item.RuleId ?? "UNKNOWN",
                item.Type ?? "Other",
                item.StartTime,
                dimension);
        }

        private static string BuildDisplaySummary(WarningTicketRecord ticket)
        {
            var text = ticket == null ? string.Empty : (ticket.Summary ?? string.Empty);
            if (string.IsNullOrWhiteSpace(text))
            {
                var rule = ticket == null ? "预警" : (ticket.RuleName ?? "预警");
                var metric = ticket == null ? string.Empty : (ticket.MetricName ?? string.Empty);
                return string.Format("{0} 指标：{1}", rule, metric).Trim();
            }

            var idx = text.IndexOf("EVIDENCE", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                text = text.Substring(0, idx);
            }

            text = text.Trim().TrimEnd('。', '，', '.');
            const int maxLen = 140;
            if (text.Length > maxLen)
            {
                text = text.Substring(0, maxLen) + "...";
            }

            return text;
        }

        private static string MapLevelDisplay(string level)
        {
            var lv = (level ?? string.Empty).Trim().ToLowerInvariant();
            switch (lv)
            {
                case "critical": return "严重";
                case "warning": return "警告";
                case "info": return "提示";
                default: return string.IsNullOrEmpty(level) ? "提示" : level;
            }
        }

        private static string MapTypeDisplay(string type)
        {
            var tp = (type ?? string.Empty).Trim().ToLowerInvariant();
            switch (tp)
            {
                case "yield": return "良率";
                case "throughput": return "产能";
                case "alarm": return "报警";
                case "combined": return "综合";
                default: return string.IsNullOrEmpty(type) ? "其他" : type;
            }
        }

        private static WarningMonitorSnapshot CloneSnapshot(WarningMonitorSnapshot source)
        {
            if (source == null)
            {
                return WarningMonitorSnapshot.Empty;
            }

            return new WarningMonitorSnapshot
            {
                UpdatedAt = source.UpdatedAt,
                LastUpdatedText = source.LastUpdatedText ?? string.Empty,
                Tickets = (source.Tickets ?? new List<WarningTicketRecord>()).Select(CloneTicket).ToList(),
                Analyses = new Dictionary<string, string>(
                    source.Analyses ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase)
            };
        }

        private static WarningItem CloneWarningItem(WarningItem source)
        {
            if (source == null)
            {
                return null;
            }

            return new WarningItem
            {
                Key = source.Key,
                RuleId = source.RuleId,
                RuleName = source.RuleName,
                Level = source.Level,
                Type = source.Type,
                StartTime = source.StartTime,
                EndTime = source.EndTime,
                FirstDetected = source.FirstDetected,
                LastDetected = source.LastDetected,
                MetricName = source.MetricName,
                CurrentValue = source.CurrentValue,
                BaselineValue = source.BaselineValue,
                ThresholdValue = source.ThresholdValue,
                Status = source.Status,
                Summary = source.Summary
            };
        }

        private static WarningTicketRecord CloneTicket(WarningTicketRecord source)
        {
            if (source == null)
            {
                return null;
            }

            return new WarningTicketRecord
            {
                Fingerprint = source.Fingerprint,
                RuleId = source.RuleId,
                RuleName = source.RuleName,
                Level = source.Level,
                Type = source.Type,
                StartTime = source.StartTime,
                EndTime = source.EndTime,
                Summary = source.Summary,
                MetricName = source.MetricName,
                CurrentValue = source.CurrentValue,
                BaselineValue = source.BaselineValue,
                ThresholdValue = source.ThresholdValue,
                Status = source.Status,
                CreatedAt = source.CreatedAt,
                UpdatedAt = source.UpdatedAt,
                FirstSeen = source.FirstSeen,
                LastSeen = source.LastSeen,
                AcknowledgedAt = source.AcknowledgedAt,
                ProcessedAt = source.ProcessedAt,
                IgnoredUntil = source.IgnoredUntil,
                OccurrenceCount = source.OccurrenceCount
            };
        }
    }
}

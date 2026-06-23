using System.Collections.Concurrent;

namespace EW_Assistant.McpTools
{
    internal static class UiLaneScheduler
    {
        private sealed class LaneGateEntry
        {
            public SemaphoreSlim Gate { get; } = new(1, 1);
            public object SyncRoot { get; } = new object();
            public int ActiveUsers { get; set; }
            public bool IsRetired { get; set; }
        }

        private static readonly SemaphoreSlim s_globalGate = new(2, 2);
        private static readonly ConcurrentDictionary<string, LaneGateEntry> s_laneGates =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 按 lane 串行执行，同时受全局并发阈值约束，避免多任务争抢同一 UI 环境。
        /// </summary>
        public static async Task<T> RunAsync<T>(string lane, Func<Task<T>> work)
        {
            if (work == null)
                throw new ArgumentNullException(nameof(work));

            var laneKey = NormalizeLane(lane);
            var laneEntry = AcquireLaneEntry(laneKey);

            await laneEntry.Gate.WaitAsync().ConfigureAwait(false);
            await s_globalGate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await work().ConfigureAwait(false);
            }
            finally
            {
                s_globalGate.Release();
                laneEntry.Gate.Release();
                ReleaseLaneEntry(laneKey, laneEntry);
            }
        }

        public static string NormalizeLane(string lane)
        {
            if (string.IsNullOrWhiteSpace(lane))
                return "default";
            return lane.Trim();
        }

        private static LaneGateEntry AcquireLaneEntry(string laneKey)
        {
            while (true)
            {
                var entry = s_laneGates.GetOrAdd(laneKey, _ => new LaneGateEntry());
                lock (entry.SyncRoot)
                {
                    if (entry.IsRetired)
                        continue;

                    entry.ActiveUsers++;
                    return entry;
                }
            }
        }

        private static void ReleaseLaneEntry(string laneKey, LaneGateEntry entry)
        {
            LaneGateEntry? retiredEntry = null;
            lock (entry.SyncRoot)
            {
                if (entry.ActiveUsers > 0)
                {
                    entry.ActiveUsers--;
                }

                if (entry.ActiveUsers != 0 || !ShouldRetireLane(laneKey))
                    return;

                if (s_laneGates.TryGetValue(laneKey, out var current) && ReferenceEquals(current, entry))
                {
                    entry.IsRetired = true;
                    if (s_laneGates.TryRemove(laneKey, out var removedEntry))
                    {
                        retiredEntry = removedEntry;
                        // 交给锁外释放，避免在临界区内做重操作。
                    }
                }
            }

            if (retiredEntry != null)
            {
                try
                {
                    retiredEntry.Gate.Dispose();
                }
                catch
                {
                    // ignore
                }
            }
        }

        private static bool ShouldRetireLane(string laneKey)
        {
            return !string.Equals(laneKey, "default", StringComparison.OrdinalIgnoreCase);
        }
    }

    internal enum UiRiskLevel
    {
        ReadOnly,
        Low,
        High,
        Blocked
    }

    internal static class UiActionPolicy
    {
        /// <summary>
        /// 动作归一化：支持中英文常用表达，统一到标准动作名。
        /// </summary>
        public static string NormalizeAction(string action)
        {
            var raw = (action ?? string.Empty).Trim().ToLowerInvariant();
            return raw switch
            {
                "activate" or "focus" or "激活" or "聚焦" => "activate",
                "click" or "单击" or "点击" => "click",
                "double_click" or "doubleclick" or "双击" => "double_click",
                "right_click" or "rightclick" or "右击" or "右键" => "right_click",
                "set_text" or "type" or "输入" or "写入" => "set_text",
                "close" or "关闭" => "close",
                _ => raw
            };
        }

        /// <summary>
        /// 风险策略：高风险动作必须显式确认。
        /// </summary>
        public static (bool allowed, UiRiskLevel risk, string reason, string action) Evaluate(string action, bool confirmed)
        {
            var normalized = NormalizeAction(action);

            if (string.IsNullOrWhiteSpace(normalized))
                return (false, UiRiskLevel.Blocked, "缺少 action 参数。", normalized);

            if (normalized is "activate")
                return (true, UiRiskLevel.Low, "ok", normalized);

            if (normalized is "click" or "double_click" or "right_click" or "set_text")
                return (true, UiRiskLevel.Low, "ok", normalized);

            if (normalized is "close")
            {
                if (!confirmed)
                    return (false, UiRiskLevel.High, "close 为高风险动作，需 confirmed=true。", normalized);
                return (true, UiRiskLevel.High, "ok", normalized);
            }

            return (false, UiRiskLevel.Blocked, $"不支持的 action：{action}", normalized);
        }
    }

    internal sealed class UiSnapshotRecord
    {
        public string SnapshotId { get; init; } = string.Empty;
        public string Lane { get; init; } = "default";
        public DateTime CapturedAtUtc { get; init; }
        public Dictionary<string, long> RefMap { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class UiActionCacheRecord
    {
        public DateTime ExpiresAtUtc { get; init; }
        public string ResultJson { get; init; } = string.Empty;
    }

    internal static class UiSnapshotStore
    {
        private static readonly ConcurrentDictionary<string, UiSnapshotRecord> s_snapshots =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, UiActionCacheRecord> s_actionCache =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan SnapshotTtl = TimeSpan.FromMinutes(20);
        private static readonly TimeSpan ActionCacheTtl = TimeSpan.FromMinutes(10);
        private static int s_sequence = 0;

        public static UiSnapshotRecord Create(string lane, Dictionary<string, long> refMap)
        {
            CleanupExpired();

            var id = $"snap-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Interlocked.Increment(ref s_sequence)}";
            var item = new UiSnapshotRecord
            {
                SnapshotId = id,
                Lane = UiLaneScheduler.NormalizeLane(lane),
                CapturedAtUtc = DateTime.UtcNow,
                RefMap = refMap ?? new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            };
            s_snapshots[id] = item;
            return item;
        }

        public static bool TryResolveRef(string snapshotId, string refId, out long hwnd, out string reason)
        {
            hwnd = 0;
            reason = string.Empty;

            CleanupExpired();

            if (string.IsNullOrWhiteSpace(snapshotId))
            {
                reason = "缺少 snapshotId。";
                return false;
            }

            if (string.IsNullOrWhiteSpace(refId))
            {
                reason = "缺少 refId。";
                return false;
            }

            if (!s_snapshots.TryGetValue(snapshotId.Trim(), out var snapshot) || snapshot == null)
            {
                reason = "快照不存在或已过期。";
                return false;
            }

            if (!snapshot.RefMap.TryGetValue(refId.Trim(), out hwnd))
            {
                reason = $"ref 不存在：{refId}";
                return false;
            }

            return true;
        }

        public static bool TryGetCachedAction(string lane, string idempotencyKey, out string resultJson)
        {
            resultJson = string.Empty;
            CleanupExpired();

            var key = BuildActionCacheKey(lane, idempotencyKey);
            if (string.IsNullOrWhiteSpace(key))
                return false;

            if (!s_actionCache.TryGetValue(key, out var item) || item == null)
                return false;

            if (item.ExpiresAtUtc <= DateTime.UtcNow)
            {
                s_actionCache.TryRemove(key, out _);
                return false;
            }

            resultJson = item.ResultJson ?? string.Empty;
            return !string.IsNullOrWhiteSpace(resultJson);
        }

        public static void PutCachedAction(string lane, string idempotencyKey, string resultJson)
        {
            var key = BuildActionCacheKey(lane, idempotencyKey);
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(resultJson))
                return;

            CleanupExpired();
            s_actionCache[key] = new UiActionCacheRecord
            {
                ExpiresAtUtc = DateTime.UtcNow.Add(ActionCacheTtl),
                ResultJson = resultJson
            };
        }

        private static string BuildActionCacheKey(string lane, string idempotencyKey)
        {
            if (string.IsNullOrWhiteSpace(idempotencyKey))
                return string.Empty;

            var laneKey = UiLaneScheduler.NormalizeLane(lane);
            return laneKey + "::" + idempotencyKey.Trim();
        }

        private static void CleanupExpired()
        {
            var now = DateTime.UtcNow;

            foreach (var pair in s_snapshots)
            {
                if ((now - pair.Value.CapturedAtUtc) > SnapshotTtl)
                    s_snapshots.TryRemove(pair.Key, out _);
            }

            foreach (var pair in s_actionCache)
            {
                if (pair.Value.ExpiresAtUtc <= now)
                    s_actionCache.TryRemove(pair.Key, out _);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;

namespace EW_Assistant.Diagnostics
{
    /// <summary>
    /// 轻量级日志保留策略，按目录低频触发清理，避免长期运行时磁盘无限增长。
    /// </summary>
    internal static class LogRetentionPolicy
    {
        private sealed class CleanupState
        {
            public DateTime LastCleanupAtUtc { get; set; }
            public bool IsRunning { get; set; }
        }

        private sealed class CleanupFileCandidate
        {
            public CleanupFileCandidate(string path, DateTime lastWriteUtc)
            {
                Path = path ?? string.Empty;
                LastWriteUtc = lastWriteUtc;
            }

            public string Path { get; }
            public DateTime LastWriteUtc { get; }
        }

        private static readonly object s_syncRoot = new object();
        private static readonly Dictionary<string, CleanupState> s_cleanupStates =
            new Dictionary<string, CleanupState>(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan DefaultCleanupInterval = TimeSpan.FromMinutes(30);

        public static void TryCleanupFiles(
            string root,
            string searchPattern,
            SearchOption searchOption,
            TimeSpan retention,
            bool deleteEmptyDirectories = false,
            int maxDeletesPerRun = 200)
        {
            TryCleanupFiles(
                root,
                searchPattern,
                searchOption,
                retention,
                DefaultCleanupInterval,
                deleteEmptyDirectories,
                maxDeletesPerRun);
        }

        public static void TryCleanupFiles(
            string root,
            string searchPattern,
            SearchOption searchOption,
            TimeSpan retention,
            TimeSpan cleanupInterval,
            bool deleteEmptyDirectories,
            int maxDeletesPerRun)
        {
            if (string.IsNullOrWhiteSpace(root)
                || retention <= TimeSpan.Zero
                || cleanupInterval <= TimeSpan.Zero
                || maxDeletesPerRun <= 0)
            {
                return;
            }

            if (!Directory.Exists(root))
            {
                return;
            }

            var key = BuildCleanupKey(root, searchPattern, searchOption, deleteEmptyDirectories);
            var nowUtc = DateTime.UtcNow;
            CleanupState state;
            lock (s_syncRoot)
            {
                CleanupState? existingState;
                s_cleanupStates.TryGetValue(key, out existingState);
                if (existingState == null)
                {
                    existingState = new CleanupState();
                    s_cleanupStates[key] = existingState;
                }

                state = existingState;
                if (state.IsRunning)
                {
                    return;
                }

                if (state.LastCleanupAtUtc != default(DateTime)
                    && nowUtc - state.LastCleanupAtUtc < cleanupInterval)
                {
                    return;
                }

                state.IsRunning = true;
            }

            var filesCleanupSucceeded = false;
            try
            {
                CleanupFiles(root, searchPattern, searchOption, nowUtc - retention, maxDeletesPerRun);
                filesCleanupSucceeded = true;
            }
            catch
            {
                // 清理失败不影响主流程。
            }

            try
            {
                if (deleteEmptyDirectories)
                {
                    CleanupEmptyDirectories(root, maxDeletesPerRun);
                }
            }
            catch
            {
                // 清理失败不影响主流程。
            }
            finally
            {
                lock (s_syncRoot)
                {
                    state.IsRunning = false;
                    if (filesCleanupSucceeded)
                    {
                        state.LastCleanupAtUtc = DateTime.UtcNow;
                    }
                }
            }
        }

        private static void CleanupFiles(
            string root,
            string searchPattern,
            SearchOption searchOption,
            DateTime cutoffUtc,
            int maxDeletesPerRun)
        {
            var pattern = string.IsNullOrWhiteSpace(searchPattern) ? "*" : searchPattern;
            var candidates = new List<CleanupFileCandidate>();
            foreach (var path in Directory.EnumerateFiles(root, pattern, searchOption))
            {
                try
                {
                    var lastWriteUtc = File.GetLastWriteTimeUtc(path);
                    if (lastWriteUtc == DateTime.MinValue)
                    {
                        lastWriteUtc = File.GetCreationTimeUtc(path);
                    }

                    if (lastWriteUtc != DateTime.MinValue && lastWriteUtc < cutoffUtc)
                    {
                        candidates.Add(new CleanupFileCandidate(path, lastWriteUtc));
                    }
                }
                catch
                {
                    // 单个文件枚举或取时间失败忽略。
                }
            }

            candidates.Sort((left, right) =>
            {
                var compare = left.LastWriteUtc.CompareTo(right.LastWriteUtc);
                if (compare != 0)
                {
                    return compare;
                }

                return StringComparer.OrdinalIgnoreCase.Compare(left.Path, right.Path);
            });

            var deleteCount = Math.Min(maxDeletesPerRun, candidates.Count);
            for (int i = 0; i < deleteCount; i++)
            {
                try
                {
                    File.Delete(candidates[i].Path);
                }
                catch
                {
                    // 单个文件清理失败忽略。
                }
            }
        }

        private static void CleanupEmptyDirectories(string root, int maxDeletesPerRun)
        {
            var directories = Directory.GetDirectories(root, "*", SearchOption.AllDirectories);
            Array.Sort(directories, (left, right) => right.Length.CompareTo(left.Length));

            var deleted = 0;
            for (int i = 0; i < directories.Length; i++)
            {
                if (deleted >= maxDeletesPerRun)
                {
                    break;
                }

                var dir = directories[i];
                try
                {
                    if (Directory.GetFiles(dir).Length == 0 && Directory.GetDirectories(dir).Length == 0)
                    {
                        Directory.Delete(dir, false);
                        deleted++;
                    }
                }
                catch
                {
                    // 单个目录清理失败忽略。
                }
            }
        }

        private static string BuildCleanupKey(
            string root,
            string searchPattern,
            SearchOption searchOption,
            bool deleteEmptyDirectories)
        {
            var normalizedRoot = root.Trim();
            try
            {
                normalizedRoot = Path.GetFullPath(normalizedRoot);
            }
            catch
            {
                // 路径归一化失败时退回原值，避免影响日志主流程。
            }

            return normalizedRoot
                   + "|"
                   + (searchPattern ?? "*")
                   + "|"
                   + searchOption
                   + "|"
                   + deleteEmptyDirectories;
        }
    }
}

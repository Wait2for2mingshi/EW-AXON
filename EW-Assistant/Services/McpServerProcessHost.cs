using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace EW_Assistant.Services
{
    /// <summary>
    /// 负责托管 McpServer.exe 的生命周期（随 UI 启停）
    /// </summary>
    public sealed class McpServerProcessHost : IDisposable
    {
        private static readonly Lazy<McpServerProcessHost> _lazy =
            new(() => new McpServerProcessHost());

        public static McpServerProcessHost Instance => _lazy.Value;

        private readonly string _exePath;
        private readonly object _syncRoot = new();
        private Process _process;
        private bool _stopRequested;
        private bool _restartScheduled;
        private int _restartAttempts;

        private McpServerProcessHost()
        {
            _exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "McpServer", "McpServer.exe");
        }

        /// <summary>
        /// 若进程未启动则尝试拉起，返回当前是否处于运行状态
        /// </summary>
        public bool StartIfNeeded(Action<string, string> logger = null)
        {
            lock (_syncRoot)
            {
                _stopRequested = false;

                if (_process != null && _process.HasExited)
                {
                    _process.Dispose();
                    _process = null;
                }

                if (_process != null && !_process.HasExited)
                {
                    _restartAttempts = 0;
                    logger?.Invoke("MCP Server 已在运行，无需重复启动。", "info");
                    return true;
                }

                TerminateExistingProcesses(logger);

                if (!File.Exists(_exePath))
                {
                    logger?.Invoke($"未找到 MCP Server 可执行文件：{_exePath}", "warn");
                    return false;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = _exePath,
                    WorkingDirectory = Path.GetDirectoryName(_exePath) ?? AppDomain.CurrentDomain.BaseDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                try
                {
                    var process = Process.Start(startInfo);
                    if (process == null)
                    {
                        logger?.Invoke("启动 MCP Server 失败（返回空进程）。", "error");
                        return false;
                    }

                    if (process.WaitForExit(500))
                    {
                        logger?.Invoke($"MCP Server 进程启动后立即退出（ExitCode={process.ExitCode}）。", "error");
                        process.Dispose();
                        return false;
                    }

                    WatchProcess(process, logger);
                    _restartAttempts = 0;
                    _restartScheduled = false;
                    logger?.Invoke($"MCP Server 已启动（PID={process.Id}）。", "ok");
                    return true;
                }
                catch (Exception ex)
                {
                    logger?.Invoke($"启动 MCP Server 失败：{ex.Message}", "error");
                    _process = null;
                    return false;
                }
            }
        }

        /// <summary>
        /// 请求终止当前 MCP 进程
        /// </summary>
        public void Stop(Action<string, string> logger = null)
        {
            lock (_syncRoot)
            {
                _stopRequested = true;
                _restartScheduled = false;

                if (_process == null)
                    return;

                try
                {
                    var proc = _process;
                    if (proc != null && !proc.HasExited)
                    {
                        try
                        {
                            // 先尝试温和退出，再在超时后强杀
                            proc.CloseMainWindow();
                        }
                        catch
                        {
                            // 忽略无窗口异常
                        }

                        if (!proc.WaitForExit(3000))
                        {
                            proc.Kill();
                            proc.WaitForExit(2000);
                        }
                    }

                    logger?.Invoke("MCP Server 已停止。", "info");
                }
                catch (Exception ex)
                {
                    logger?.Invoke($"停止 MCP Server 失败：{ex.Message}", "error");
                }
                finally
                {
                    _process?.Dispose();
                    _process = null;
                }
            }
        }

        public void Dispose()
        {
            Stop();
        }

        /// <summary>
        /// 清理当前机器上已存在的 MCP Server 进程，确保重新拉起的是本目录版本
        /// </summary>
        private void TerminateExistingProcesses(Action<string, string> logger)
        {
            try
            {
                var exeName = Path.GetFileNameWithoutExtension(_exePath);
                if (string.IsNullOrEmpty(exeName))
                    return;

                foreach (var candidate in Process.GetProcessesByName(exeName))
                {
                    Process proc = null;
                    try
                    {
                        proc = candidate;
                        var pid = proc.Id;
                        string path = string.Empty;
                        try
                        {
                            path = proc.MainModule?.FileName ?? string.Empty;
                        }
                        catch (Exception inner)
                        {
                            logger?.Invoke($"读取 MCP Server 进程路径失败（PID={pid}）：{inner.Message}", "warn");
                        }

                        try
                        {
                            proc.CloseMainWindow();
                        }
                        catch
                        {
                            // 可能无窗口，忽略异常
                        }

                        if (!proc.WaitForExit(1000))
                        {
                            proc.Kill();
                            proc.WaitForExit(2000);
                        }

                        if (proc.HasExited)
                        {
                            var fullPath = string.IsNullOrEmpty(path) ? string.Empty : Path.GetFullPath(path);
                            var pathInfo = string.IsNullOrEmpty(fullPath) ? string.Empty : $", Path={fullPath}";
                            logger?.Invoke($"已终止存在的 MCP Server 实例（PID={pid}{pathInfo}），准备重启。", "warn");
                        }
                    }
                    catch (Exception ex)
                    {
                        logger?.Invoke($"终止已存在的 MCP Server 失败：{ex.Message}", "error");
                    }
                    finally
                    {
                        proc?.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.Invoke($"列举 MCP Server 进程失败：{ex.Message}", "warn");
            }
        }

        private void WatchProcess(Process process, Action<string, string> logger)
        {
            process.EnableRaisingEvents = true;
            process.Exited += (_, __) =>
            {
                var exitCodeText = "unknown";
                try
                {
                    exitCodeText = process.ExitCode.ToString();
                }
                catch
                {
                    // ignore
                }

                logger?.Invoke($"MCP Server 进程已退出（ExitCode={exitCodeText}）。", "warn");
                lock (_syncRoot)
                {
                    if (ReferenceEquals(_process, process))
                    {
                        _process = null;
                    }
                }

                try
                {
                    process.Dispose();
                }
                catch
                {
                    // ignore
                }

                ScheduleRestartIfNeeded(logger, exitCodeText);
            };

            _process = process;
        }

        private void ScheduleRestartIfNeeded(Action<string, string> logger, string exitCodeText)
        {
            int attempt;
            TimeSpan delay;
            lock (_syncRoot)
            {
                if (_stopRequested || _restartScheduled)
                    return;

                _restartScheduled = true;
                _restartAttempts++;
                attempt = _restartAttempts;
                delay = TimeSpan.FromSeconds(Math.Min(5 * attempt, 60));
            }

            _ = Task.Run(async () =>
            {
                logger?.Invoke(
                    $"MCP Server 异常退出（ExitCode={exitCodeText}），将在 {delay.TotalSeconds:0} 秒后尝试第 {attempt} 次自动重启。",
                    "warn");

                try
                {
                    await Task.Delay(delay).ConfigureAwait(false);
                }
                catch
                {
                    // ignore
                }

                lock (_syncRoot)
                {
                    if (_stopRequested)
                    {
                        _restartScheduled = false;
                        return;
                    }
                }

                var started = StartIfNeeded(logger);
                lock (_syncRoot)
                {
                    _restartScheduled = false;
                    if (_stopRequested)
                        return;

                    if (started)
                    {
                        _restartAttempts = 0;
                    }
                }

                if (started)
                {
                    logger?.Invoke("MCP Server 已自动恢复。", "ok");
                    return;
                }

                logger?.Invoke("MCP Server 自动重启失败，稍后继续重试。", "warn");
                ScheduleRestartIfNeeded(logger, exitCodeText);
            });
        }
    }
}

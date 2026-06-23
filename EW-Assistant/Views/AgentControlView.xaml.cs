using EW_Assistant.Services;
using EW_Assistant.Settings;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace EW_Assistant.Views
{
    /// <summary>
    /// 智能体控制入口页：聚焦 Brain 路由、Executor 执行与日志排障。
    /// </summary>
    public partial class AgentControlView : UserControl, INotifyPropertyChanged
    {
        public bool IsModuleEnabled => AgentAutomationService.ModuleEnabled;

        private string _runtimeStatus = "状态检测中...";
        public string RuntimeStatus
        {
            get => _runtimeStatus;
            set
            {
                if (!string.Equals(_runtimeStatus, value, StringComparison.Ordinal))
                {
                    _runtimeStatus = value;
                    OnPropertyChanged();
                }
            }
        }

        private string _moduleStatus = string.Empty;
        public string ModuleStatus
        {
            get => _moduleStatus;
            set
            {
                if (!string.Equals(_moduleStatus, value, StringComparison.Ordinal))
                {
                    _moduleStatus = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _configSubscribed;

        public AgentControlView()
        {
            InitializeComponent();
            DataContext = this;
            RefreshModuleStatus();
            Loaded += AgentControlView_Loaded;
            Unloaded += AgentControlView_Unloaded;
        }

        private async void AgentControlView_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_configSubscribed)
            {
                ConfigService.ConfigChanged += ConfigService_ConfigChanged;
                _configSubscribed = true;
            }

            RefreshModuleStatus();
            await RefreshRuntimeStatusAsync();
        }

        private void AgentControlView_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_configSubscribed)
            {
                ConfigService.ConfigChanged -= ConfigService_ConfigChanged;
                _configSubscribed = false;
            }
        }

        private void ConfigService_ConfigChanged(object sender, AppConfig cfg)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                OnPropertyChanged(nameof(IsModuleEnabled));
                RefreshModuleStatus();
                StartRuntimeStatusRefresh();
            }), DispatcherPriority.Background);
        }

        private void RefreshModuleStatus()
        {
            ModuleStatus = IsModuleEnabled
                ? "已启用"
                : "已冻结";
        }

        private async Task RefreshRuntimeStatusAsync()
        {
            if (!IsModuleEnabled)
            {
                RuntimeStatus = "模块已冻结";
                return;
            }

            try
            {
                var cfg = ConfigService.Current ?? ConfigService.Load();
                var endpoint = cfg?.MCPServerIP;
                if (!TryParseEndpoint(endpoint, out var host, out var port))
                {
                    RuntimeStatus = "地址无效";
                    return;
                }

                var reachable = await ProbeTcpAsync(host, port, 1500);
                var statusText = reachable ? "在线" : "离线";
                RuntimeStatus = $"{host}:{port} {statusText}";
            }
            catch (Exception ex)
            {
                RuntimeStatus = "状态刷新失败：" + ex.Message;
            }
        }

        private static bool TryParseEndpoint(string endpoint, out string host, out int port)
        {
            host = string.Empty;
            port = 0;
            if (string.IsNullOrWhiteSpace(endpoint))
                return false;

            var text = endpoint.Trim();
            if (!text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                text = "http://" + text;
            }

            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
                return false;

            if (string.IsNullOrWhiteSpace(uri.Host) || uri.Port <= 0)
                return false;

            host = uri.Host;
            port = uri.Port;
            return true;
        }

        private static async Task<bool> ProbeTcpAsync(string host, int port, int timeoutMs)
        {
            return await Task.Run(() =>
            {
                try
                {
                    using var client = new TcpClient();
                    var asyncResult = client.BeginConnect(host, port, null, null);
                    using (asyncResult.AsyncWaitHandle)
                    {
                        if (!asyncResult.AsyncWaitHandle.WaitOne(timeoutMs))
                            return false;
                    }

                    try
                    {
                        client.EndConnect(asyncResult);
                        return client.Connected;
                    }
                    catch
                    {
                        return false;
                    }
                }
                catch
                {
                    return false;
                }
            }, CancellationToken.None).ConfigureAwait(false);
        }

        private void StartRuntimeStatusRefresh()
        {
            var task = RefreshRuntimeStatusAsync();
            task.ContinueWith(
                t =>
                {
                    var _ = t.Exception;
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private async void BtnRefreshStatus_Click(object sender, RoutedEventArgs e)
        {
            await RefreshRuntimeStatusAsync();
            MainWindow.PostProgramInfo("[AgentControl] 已刷新智能体模块运行状态。", "info");
        }

        private void BtnOpenRouteLogs_Click(object sender, RoutedEventArgs e)
        {
            OpenLatestLogOrFolder(AgentControlPaths.RouterLogRoot, "路由日志");
        }

        private void BtnOpenExecutorLogs_Click(object sender, RoutedEventArgs e)
        {
            OpenLatestLogOrFolder(AgentControlPaths.ExecutorLogRoot, "执行日志");
        }

        private static void OpenLatestLogOrFolder(string rootPath, string label)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(rootPath))
                    throw new InvalidOperationException("日志目录为空。");

                Directory.CreateDirectory(rootPath);
                var latestLog = Directory
                    .GetFiles(rootPath, "*.log", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();

                var target = string.IsNullOrWhiteSpace(latestLog) ? rootPath : latestLog;
                using var proc = Process.Start(new ProcessStartInfo(target)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MainWindow.PostProgramInfo($"[AgentControl] 打开{label}目录失败：{ex.Message}", "error");
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using EW_Assistant.Infrastructure.Inventory;
using EW_Assistant.Services;
using EW_Assistant.Services.Warnings;
using EW_Assistant.Settings;

namespace EW_Assistant
{
    public partial class App : Application
    {
        private static readonly DependencyProperty UiLocalizedProperty =
            DependencyProperty.RegisterAttached(
                "UiLocalized",
                typeof(bool),
                typeof(App),
                new PropertyMetadata(false));


        public App()
        {
            // 非 UI 线程的兜底
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
            EventManager.RegisterClassHandler(
                typeof(Window),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(Window_Loaded));
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            try
            {
                // 初始化库存模块，使用文件仓储，后续可替换为 DbInventoryRepository 而无需改动 VM/View。
                InventoryModule.EnsureInitialized();
            }
            catch (Exception ex)
            {
                MessageBox.Show("初始化库存模块失败：" + ex.Message, "启动错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            try
            {
                AiAnalysisHistoryStore.Instance.Initialize();
                WarningMonitorService.Instance.Start();
                UpdatePerformanceMonitorState(ConfigService.Current);
                ConfigService.ConfigChanged += OnConfigChanged;
                Exit += (_, __) =>
                {
                    ConfigService.ConfigChanged -= OnConfigChanged;
                    try { WarningMonitorService.Instance.Stop(); } catch { }
                    try { PerformanceAutoAnalysisService.Instance.Stop(); } catch { }
                    try { PerformanceMonitorService.Instance.Stop(); } catch { }
                };
            }
            catch
            {
                // 后台监控失败时不阻断主流程
            }
        }

        private static void Window_Loaded(object sender, RoutedEventArgs e)
        {
            var window = sender as Window;
            if (window == null)
                return;

            if ((bool)window.GetValue(UiLocalizedProperty))
                return;

            window.SetValue(UiLocalizedProperty, true);
            UiLanguageService.ApplyStaticText(window, ConfigService.Current);
        }

        private void Application_DispatcherUnhandledException(
     object sender,
     DispatcherUnhandledExceptionEventArgs e)
        {
            var ex = e.Exception;
            var msg = ex != null ? ex.Message : "发生了未知错误。";

            try
            {
                global::EW_Assistant.MainWindow.PostProgramInfo("UI 线程异常：" + msg, "error");
            }
            catch
            {
                // 确保这里绝对不再抛异常
            }

            MessageBox.Show(
                msg,
                "程序运行出错",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            e.Handled = true;
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception;
            var msg = ex != null ? ex.ToString() : "非 UI 线程发生未知错误。";

            try
            {
                global::EW_Assistant.MainWindow.PostProgramInfo("非 UI 线程异常：" + msg, "error");
            }
            catch
            {
            }
        }


        private void TaskScheduler_UnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            try
            {
                global::EW_Assistant.MainWindow.PostProgramInfo("任务调度异常：" + e.Exception, "error");
            }
            catch
            {
            }

            // 如果不希望因为这个导致进程终止：
            e.SetObserved();
        }

        private void OnConfigChanged(object sender, AppConfig cfg)
        {
            UpdatePerformanceMonitorState(cfg);
        }

        private void UpdatePerformanceMonitorState(AppConfig cfg)
        {
            var enabled = cfg?.EnablePerformanceMonitor ?? true;
            if (enabled)
            {
                PerformanceMonitorService.Instance.Start();
                PerformanceAutoAnalysisService.Instance.Start();
            }
            else
            {
                PerformanceAutoAnalysisService.Instance.Stop();
                PerformanceMonitorService.Instance.Stop();
            }
        }

    }
}

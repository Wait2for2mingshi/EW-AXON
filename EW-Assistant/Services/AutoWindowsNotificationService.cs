using System;
using System.Diagnostics;
using System.Windows;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace EW_Assistant.Services
{
    /// <summary>
    /// 负责发出 AUTO 触发后的 Windows 通知，并在点击后把主窗体切到“异常诊断”。
    /// </summary>
    public static class AutoWindowsNotificationService
    {
        private static readonly object SyncRoot = new object();
        private static Forms.NotifyIcon _notifyIcon;

        public static void ShowAutoTriggeredNotification(string errorCode, string prompt)
        {
            if (!ConfigService.IsAutoWindowsNotificationEnabled())
            {
                Dispose();
                return;
            }

            var app = Application.Current;
            if (app?.Dispatcher == null)
            {
                return;
            }

            _ = app.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    EnsureNotifyIcon();
                    if (_notifyIcon == null)
                    {
                        return;
                    }

                    _notifyIcon.BalloonTipTitle = L("异常诊断");
                    _notifyIcon.BalloonTipText = BuildBalloonText(errorCode, prompt);
                    _notifyIcon.BalloonTipIcon = Forms.ToolTipIcon.Info;
                    _notifyIcon.ShowBalloonTip(5000);
                }
                catch
                {
                    // 通知失败不影响主流程
                }
            }));
        }

        public static void Dispose()
        {
            lock (SyncRoot)
            {
                if (_notifyIcon == null)
                {
                    return;
                }

                try
                {
                    _notifyIcon.Visible = false;
                    _notifyIcon.Dispose();
                }
                catch
                {
                    // 忽略销毁异常
                }
                finally
                {
                    _notifyIcon = null;
                }
            }
        }

        private static void EnsureNotifyIcon()
        {
            lock (SyncRoot)
            {
                if (_notifyIcon != null)
                {
                    return;
                }

                _notifyIcon = new Forms.NotifyIcon
                {
                    Text = "EW Assistant " + L("异常诊断"),
                    Visible = true,
                    Icon = LoadAppIconSafe()
                };
                _notifyIcon.BalloonTipClicked += NotifyIcon_Click;
                _notifyIcon.Click += NotifyIcon_Click;
                _notifyIcon.DoubleClick += NotifyIcon_Click;
            }
        }

        private static Drawing.Icon LoadAppIconSafe()
        {
            try
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(exePath))
                {
                    var icon = Drawing.Icon.ExtractAssociatedIcon(exePath);
                    if (icon != null)
                    {
                        return icon;
                    }
                }
            }
            catch
            {
                // 回退默认图标
            }

            return Drawing.SystemIcons.Application;
        }

        private static void NotifyIcon_Click(object sender, EventArgs e)
        {
            try
            {
                MainWindow.OpenExceptionDiagnosisFromNotification();
            }
            catch
            {
                // 点击通知失败时忽略，避免影响托盘事件线程
            }
        }

        private static string BuildBalloonText(string errorCode, string prompt)
        {
            var code = UiLanguageService.ApplyDisplayTextReplacements(NormalizeErrorCode(errorCode, prompt));
            if (string.IsNullOrWhiteSpace(code))
            {
                return L("捕获到机台报警，进入AI分析流程。");
            }

            return UiLanguageService.ApplyDisplayTextReplacements(
                string.Format(L("捕获到机台报警：报警代码为：{0}，进入AI分析流程。"), code));
        }

        private static string NormalizeErrorCode(string errorCode, string prompt)
        {
            var normalizedCode = (errorCode ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(normalizedCode) && !string.Equals(normalizedCode, "0", StringComparison.OrdinalIgnoreCase))
            {
                return normalizedCode;
            }

            var normalizedPrompt = (prompt ?? string.Empty)
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim();

            if (string.IsNullOrWhiteSpace(normalizedPrompt))
            {
                return string.Empty;
            }

            var index = normalizedPrompt.IndexOf('，');
            if (index > 0)
            {
                normalizedPrompt = normalizedPrompt.Substring(0, index).Trim();
            }

            return normalizedPrompt;
        }

        private static string L(string text)
        {
            return UiLanguageService.CurrentText(text);
        }
    }
}

// ConfigView.xaml.cs
using EW_Assistant.Services;
using EW_Assistant.Settings;
using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace EW_Assistant.Views
{
    public partial class ConfigView : UserControl
    {
        private const int MinSettingsPasswordLength = 4;
        private bool _isEditUnlocked;
        private bool _hasInitializedLockState;
        private readonly string _activeUiLanguage;

        public AppConfig Config { get; private set; }

        public ConfigView()
        {
            InitializeComponent();
            _activeUiLanguage = UiLanguageService.Normalize(ConfigService.Current?.UiLanguage);
            LoadEditableConfig(ConfigService.Current);
            UiLanguageService.ApplyStaticText(this, BuildActiveUiConfig());
            RefreshUiLanguageControls();
        }

        private static string L(string chineseText)
        {
            return UiLanguageService.CurrentText(chineseText);
        }

        private void LoadEditableConfig(AppConfig source)
        {
            var editable = (source ?? AppConfig.CreateDefault()).Clone();
            var hasPasswordProtection = ConfigService.HasSettingsEditPassword(editable);
            if (!_hasInitializedLockState)
            {
                _isEditUnlocked = !hasPasswordProtection;
                _hasInitializedLockState = true;
            }
            else if (!hasPasswordProtection)
            {
                _isEditUnlocked = true;
            }

            if (Config == null)
            {
                Config = editable;
                DataContext = Config;
            }
            else
            {
                Config.CopyFrom(editable);
            }

            SyncSecurityControls(editable);
            RefreshUiLanguageControls();
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!CanEditConfig())
                {
                    MainWindow.PostProgramInfo(L("当前设置页已锁定，请先输入密码解锁后再保存。"), "warn");
                    return;
                }

                var saveModel = Config?.Clone() ?? AppConfig.CreateDefault();
                if (!TryApplySettingsPassword(saveModel, out var passwordValidationMessage))
                {
                    MainWindow.PostProgramInfo(L(passwordValidationMessage), "warn");
                    return;
                }

                if (string.IsNullOrWhiteSpace(saveModel.ProductionLogPath)
                    || string.IsNullOrWhiteSpace(saveModel.AlarmLogPath)
                    || string.IsNullOrWhiteSpace(saveModel.PartCsvPath)
                    || string.IsNullOrWhiteSpace(saveModel.IoMapCsvPath)
                    || string.IsNullOrWhiteSpace(saveModel.MCPServerIP)
                    || string.IsNullOrWhiteSpace(saveModel.MachineCommandBaseUrl)
                    || string.IsNullOrWhiteSpace(saveModel.WorkHttpServerPrefix)
                    || string.IsNullOrWhiteSpace(saveModel.URL)
                    || string.IsNullOrWhiteSpace(saveModel.User)
                    || string.IsNullOrWhiteSpace(saveModel.MachineCode)
                    || (saveModel.EnablePerformanceMonitor && string.IsNullOrWhiteSpace(saveModel.PerformanceKey)))
                {
                    MainWindow.PostProgramInfo(
                               L("内容不能为空。"), "warn");
                    return;
                }

                if (!ConfigService.TryNormalizeHttpAddress(saveModel.MachineCommandBaseUrl, ensureTrailingSlash: false, out var normalizedMachineCommandBaseUrl))
                {
                    MainWindow.PostProgramInfo(L("机台命令网关地址格式无效，请输入类似 http://127.0.0.1:8081 的地址。"), "warn");
                    return;
                }

                if (!ConfigService.TryNormalizeHttpAddress(saveModel.WorkHttpServerPrefix, ensureTrailingSlash: true, out var normalizedWorkHttpServerPrefix))
                {
                    MainWindow.PostProgramInfo(L("本地 HTTP 监听地址格式无效，请输入类似 http://127.0.0.1:8091/ 的地址。"), "warn");
                    return;
                }

                saveModel.MachineCommandBaseUrl = normalizedMachineCommandBaseUrl;
                saveModel.WorkHttpServerPrefix = normalizedWorkHttpServerPrefix;

                if (saveModel.DiskUsageThresholdPercent <= 0f || saveModel.DiskUsageThresholdPercent > 100f)
                {
                    MainWindow.PostProgramInfo(L("磁盘报警阈值需在 1-100 之间。"), "warn");
                    return;
                }

                if (saveModel.AutoVisionImageLookbackSeconds <= 0 || saveModel.AutoVisionImageLookbackSeconds > 600)
                {
                    MainWindow.PostProgramInfo(L("视觉图片 AUTO 触发前取图秒数需在 1-600 之间。"), "warn");
                    return;
                }

                if (saveModel.AutoVisionCooldownSeconds < 0 || saveModel.AutoVisionCooldownSeconds > 3600)
                {
                    MainWindow.PostProgramInfo(L("视觉 AUTO 冷却秒数需在 0-3600 之间。"), "warn");
                    return;
                }

                if (!string.IsNullOrWhiteSpace(saveModel.AutoVisionEmptyReferenceImagePath)
                    && Path.IsPathRooted(saveModel.AutoVisionEmptyReferenceImagePath.Trim()))
                {
                    MainWindow.PostProgramInfo(L("无料基准图路径需填写执行程序相对路径，例如 Doc\\无料基准图.jpg。"), "warn");
                    return;
                }

                var previousLanguage = ConfigService.Current?.UiLanguage;
                saveModel.UiLanguage = UiLanguageService.Normalize(saveModel.UiLanguage);

                ConfigService.Save(saveModel);
                _isEditUnlocked = !ConfigService.HasSettingsEditPassword(saveModel);
                LoadEditableConfig(ConfigService.Current);
                UiLanguageService.ApplyStaticText(this, BuildActiveUiConfig());
                MainWindow.PostProgramInfo(
                    BuildSaveSuccessMessage(previousLanguage, saveModel.UiLanguage), "ok");
            }
            catch (Exception ex)
            {
                MessageBox.Show(L("保存失败：\n") + ex.Message, L("错误"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        private void BtnReload_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 从磁盘重新加载配置
                var fresh = ConfigService.Load();
                _isEditUnlocked = !ConfigService.HasSettingsEditPassword(fresh);
                LoadEditableConfig(fresh);
                UiLanguageService.ApplyStaticText(this, BuildActiveUiConfig());

                // 走信息流
                MainWindow.PostProgramInfo(
                    L("已从磁盘重新读取配置：") + ConfigService.FilePath, "info");
            }
            catch (Exception ex)
            {
                MainWindow.PostProgramInfo(L("重新读取失败：") + ex.Message, "error");
            }
        }

        private void BtnOpenLog_Click(object sender, RoutedEventArgs e)
        {
            var logRoot = AgentControlPaths.AiLogRoot;
            try
            {
                if (!Directory.Exists(logRoot))
                    Directory.CreateDirectory(logRoot);

                using var proc = Process.Start(new ProcessStartInfo("explorer.exe", logRoot)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MainWindow.PostProgramInfo(L("打开日志目录失败：") + ex.Message, "error");
            }
        }

        private void BtnUnlockConfig_Click(object sender, RoutedEventArgs e)
        {
            var unlockPassword = UnlockPasswordBox?.Password ?? string.Empty;
            if (!ConfigService.VerifySettingsEditPassword(unlockPassword))
            {
                MainWindow.PostProgramInfo(L("设置页密码不正确，无法解锁。"), "warn");
                try { UnlockPasswordBox?.SelectAll(); } catch { }
                return;
            }

            _isEditUnlocked = true;
            if (UnlockPasswordBox != null)
            {
                UnlockPasswordBox.Clear();
            }

            ApplyEditLockState();
            MainWindow.PostProgramInfo(L("设置页已解锁，可编辑配置。"), "info");
        }

        private void BtnLockConfig_Click(object sender, RoutedEventArgs e)
        {
            if (!HasPersistedPasswordProtection())
            {
                return;
            }

            _isEditUnlocked = false;
            ApplyEditLockState();
            MainWindow.PostProgramInfo(L("设置页已锁定。"), "info");
        }

        private void ChkEnableSettingsPasswordProtection_Changed(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;
            ApplyEditLockState();
        }

        private void BtnToggleUiLanguage_Click(object sender, RoutedEventArgs e)
        {
            if (!CanEditConfig())
            {
                MainWindow.PostProgramInfo(L("当前设置页已锁定，请先输入密码解锁后再切换界面语言。"), "warn");
                return;
            }

            if (Config == null)
                return;

            Config.UiLanguage = UiLanguageService.IsEnglish(Config)
                ? UiLanguageService.Chinese
                : UiLanguageService.English;

            RefreshUiLanguageControls();
            MainWindow.PostProgramInfo(L("界面语言已选择，保存配置并重启程序后生效。"), "info");
        }

        private void SyncSecurityControls(AppConfig source)
        {
            var hasPasswordProtection = ConfigService.HasSettingsEditPassword(source);
            if (ChkEnableSettingsPasswordProtection != null)
            {
                ChkEnableSettingsPasswordProtection.IsChecked = hasPasswordProtection;
            }

            ClearSecurityEditors();
            ApplyEditLockState();
        }

        private void ClearSecurityEditors()
        {
            try { UnlockPasswordBox?.Clear(); } catch { }
            try { NewSettingsPasswordBox?.Clear(); } catch { }
            try { ConfirmSettingsPasswordBox?.Clear(); } catch { }
        }

        private bool CanEditConfig()
        {
            return !HasPersistedPasswordProtection() || _isEditUnlocked;
        }

        private bool HasPersistedPasswordProtection()
        {
            return ConfigService.HasSettingsEditPassword(ConfigService.Current ?? Config);
        }

        private void ApplyEditLockState()
        {
            var hasPersistedPasswordProtection = HasPersistedPasswordProtection();
            var willEnablePasswordProtection = ChkEnableSettingsPasswordProtection?.IsChecked == true;
            var canEdit = !hasPersistedPasswordProtection || _isEditUnlocked;

            if (ConfigEditorHost != null)
            {
                ConfigEditorHost.IsEnabled = canEdit;
            }

            if (LanguageSettingsPanel != null)
            {
                LanguageSettingsPanel.IsEnabled = canEdit;
            }

            if (BtnSave != null)
            {
                BtnSave.IsEnabled = canEdit;
            }

            if (UnlockPanel != null)
            {
                UnlockPanel.Visibility = hasPersistedPasswordProtection && !canEdit
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (BtnLockConfig != null)
            {
                BtnLockConfig.Visibility = hasPersistedPasswordProtection && canEdit
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (ChkEnableSettingsPasswordProtection != null)
            {
                ChkEnableSettingsPasswordProtection.IsEnabled = canEdit || !hasPersistedPasswordProtection;
            }

            if (PasswordEditPanel != null)
            {
                PasswordEditPanel.IsEnabled = canEdit && willEnablePasswordProtection;
            }

            if (TxtSecurityStatus != null)
            {
                TxtSecurityStatus.Text = UiLanguageService.Text(
                    BuildSecurityStatusText(hasPersistedPasswordProtection, willEnablePasswordProtection, canEdit),
                    BuildActiveUiConfig());
            }
        }

        private static string BuildSecurityStatusText(bool hasPersistedPasswordProtection, bool willEnablePasswordProtection, bool canEdit)
        {
            if (hasPersistedPasswordProtection)
            {
                return canEdit
                    ? "当前已启用密码保护，设置页已解锁。"
                    : "当前已启用密码保护，设置页已锁定。";
            }

            return willEnablePasswordProtection
                ? "当前未启用密码保护，本次保存后生效。"
                : "当前未启用密码保护，可直接编辑配置。";
        }

        private bool TryApplySettingsPassword(AppConfig saveModel, out string validationMessage)
        {
            validationMessage = string.Empty;
            if (saveModel == null)
            {
                validationMessage = L("配置对象无效。");
                return false;
            }

            var enablePasswordProtection = ChkEnableSettingsPasswordProtection?.IsChecked == true;
            var newPassword = (NewSettingsPasswordBox?.Password ?? string.Empty).Trim();
            var confirmPassword = (ConfirmSettingsPasswordBox?.Password ?? string.Empty).Trim();

            if (!enablePasswordProtection)
            {
                saveModel.SettingsEditPasswordHash = string.Empty;
                return true;
            }

            if (!string.IsNullOrWhiteSpace(newPassword) || !string.IsNullOrWhiteSpace(confirmPassword))
            {
                if (newPassword.Length < MinSettingsPasswordLength)
                {
                    validationMessage = L("设置页密码至少需要 ") + MinSettingsPasswordLength + L(" 位。");
                    return false;
                }

                if (!string.Equals(newPassword, confirmPassword, StringComparison.Ordinal))
                {
                    validationMessage = L("两次输入的设置页密码不一致。");
                    return false;
                }

                saveModel.SettingsEditPasswordHash = ConfigService.HashSettingsEditPassword(newPassword);
                return true;
            }

            if (!string.IsNullOrWhiteSpace(saveModel.SettingsEditPasswordHash))
            {
                return true;
            }

            validationMessage = L("已启用设置页密码保护，请先输入新密码并确认。");
            return false;
        }

        private void RefreshUiLanguageControls()
        {
            if (Config == null)
                return;

            Config.UiLanguage = UiLanguageService.Normalize(Config.UiLanguage);
            var selectedEnglish = UiLanguageService.IsEnglish(Config);
            var displayConfig = BuildActiveUiConfig();

            if (BtnToggleUiLanguage != null)
            {
                var text = selectedEnglish ? "切换为中文版" : "切换为英文版";
                BtnToggleUiLanguage.Content = UiLanguageService.Text(text, displayConfig);
            }
        }

        private AppConfig BuildActiveUiConfig()
        {
            return new AppConfig { UiLanguage = _activeUiLanguage };
        }

        private static string BuildSaveSuccessMessage(string previousLanguage, string savedLanguage)
        {
            var languageChanged = !string.Equals(
                UiLanguageService.Normalize(previousLanguage),
                UiLanguageService.Normalize(savedLanguage),
                StringComparison.Ordinal);

            var languageNote = languageChanged
                ? UiLanguageService.CurrentText("界面语言修改请重启程序后生效；")
                : string.Empty;

            return UiLanguageService.CurrentText("配置已保存：")
                   + ConfigService.FilePath
                   + UiLanguageService.CurrentText("。")
                   + languageNote
                   + UiLanguageService.CurrentText("IP/端口相关修改请重启程序后生效。");
        }
    }
}

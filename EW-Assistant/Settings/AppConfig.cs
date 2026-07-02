using EW_Assistant.Warnings;
using Newtonsoft.Json;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EW_Assistant.Settings
{
    public class AppConfig : INotifyPropertyChanged
    {
        public const string DefaultAutoVisionImagePathA = @"F:\SaveImage\Tool\01";
        public const string DefaultAutoVisionEmptyReferenceImagePath = @"Doc\无料基准图.jpg";

        private string _productionLogPath = string.Empty;
        public string ProductionLogPath
        {
            get => _productionLogPath;
            set { if (_productionLogPath != value) { _productionLogPath = value; OnPropertyChanged(); } }
        }

        private string _alarmLogPath = string.Empty;
        public string AlarmLogPath
        {
            get => _alarmLogPath;
            set { if (_alarmLogPath != value) { _alarmLogPath = value; OnPropertyChanged(); } }
        }

        private string _partCsvPath = string.Empty;
        public string PartCsvPath
        {
            get => _partCsvPath;
            set { if (_partCsvPath != value) { _partCsvPath = value; OnPropertyChanged(); } }
        }

        private string _machineStateLogPath = string.Empty;
        public string MachineStateLogPath
        {
            get => _machineStateLogPath;
            set { if (_machineStateLogPath != value) { _machineStateLogPath = value; OnPropertyChanged(); } }
        }

        private string _ioMapCsvPath = string.Empty;
        public string IoMapCsvPath
        {
            get => _ioMapCsvPath;
            set { if (_ioMapCsvPath != value) { _ioMapCsvPath = value; OnPropertyChanged(); } }
        }

        private string _mcpServerIP = "127.0.0.1:8081";
        public string MCPServerIP
        {
            get => _mcpServerIP;
            set { if (_mcpServerIP != value) { _mcpServerIP = value; OnPropertyChanged(); } }
        }

        private string _machineCommandBaseUrl = "http://127.0.0.1:8081";
        public string MachineCommandBaseUrl
        {
            get => _machineCommandBaseUrl;
            set { if (_machineCommandBaseUrl != value) { _machineCommandBaseUrl = value; OnPropertyChanged(); } }
        }

        private string _workHttpServerPrefix = "http://127.0.0.1:8091/";
        public string WorkHttpServerPrefix
        {
            get => _workHttpServerPrefix;
            set { if (_workHttpServerPrefix != value) { _workHttpServerPrefix = value; OnPropertyChanged(); } }
        }

        private string _url = string.Empty;
        public string URL
        {
            get => _url;
            set { if (_url != value) { _url = value; OnPropertyChanged(); } }
        }

        private string _autoKey = string.Empty;
        public string AutoKey
        {
            get => _autoKey;
            set { if (_autoKey != value) { _autoKey = value; OnPropertyChanged(); } }
        }

        private string _autoVisionKey = string.Empty;
        public string AutoVisionKey
        {
            get => _autoVisionKey;
            set { if (_autoVisionKey != value) { _autoVisionKey = value; OnPropertyChanged(); } }
        }

        private string _autoVisionImagePathA = DefaultAutoVisionImagePathA;
        public string AutoVisionImagePathA
        {
            get => _autoVisionImagePathA;
            set { if (_autoVisionImagePathA != value) { _autoVisionImagePathA = value; OnPropertyChanged(); } }
        }

        private string _autoVisionImagePathB = string.Empty;
        public string AutoVisionImagePathB
        {
            get => _autoVisionImagePathB;
            set { if (_autoVisionImagePathB != value) { _autoVisionImagePathB = value; OnPropertyChanged(); } }
        }

        private string _autoVisionEmptyReferenceImagePath = DefaultAutoVisionEmptyReferenceImagePath;
        public string AutoVisionEmptyReferenceImagePath
        {
            get => _autoVisionEmptyReferenceImagePath;
            set { if (_autoVisionEmptyReferenceImagePath != value) { _autoVisionEmptyReferenceImagePath = value; OnPropertyChanged(); } }
        }

        private bool _autoVisionImageTestMode;
        public bool AutoVisionImageTestMode
        {
            get => _autoVisionImageTestMode;
            set { if (_autoVisionImageTestMode != value) { _autoVisionImageTestMode = value; OnPropertyChanged(); } }
        }

        private int _autoVisionImageLookbackSeconds = 10;
        public int AutoVisionImageLookbackSeconds
        {
            get => _autoVisionImageLookbackSeconds;
            set { if (_autoVisionImageLookbackSeconds != value) { _autoVisionImageLookbackSeconds = value; OnPropertyChanged(); } }
        }

        private int _autoVisionCooldownSeconds = 180;
        public int AutoVisionCooldownSeconds
        {
            get => _autoVisionCooldownSeconds;
            set { if (_autoVisionCooldownSeconds != value) { _autoVisionCooldownSeconds = value; OnPropertyChanged(); } }
        }

        private string _titleBarText = "T66-TCT Program Powered By EW AI";
        public string TitleBarText
        {
            get => _titleBarText;
            set { if (_titleBarText != value) { _titleBarText = value; OnPropertyChanged(); } }
        }

        private string _uiLanguage = "zh-CN";
        public string UiLanguage
        {
            get => _uiLanguage;
            set { if (_uiLanguage != value) { _uiLanguage = value; OnPropertyChanged(); } }
        }

        private string _user = string.Empty;
        public string User
        {
            get => _user;
            set { if (_user != value) { _user = value; OnPropertyChanged(); } }
        }

        private string _machineCode = string.Empty;
        public string MachineCode
        {
            get => _machineCode;
            set { if (_machineCode != value) { _machineCode = value; OnPropertyChanged(); } }
        }

        private string _chatKey = string.Empty;
        public string ChatKey
        {
            get => _chatKey;
            set { if (_chatKey != value) { _chatKey = value; OnPropertyChanged(); } }
        }

        private string _documentKey = string.Empty;
        public string DocumentKey
        {
            get => _documentKey;
            set { if (_documentKey != value) { _documentKey = value; OnPropertyChanged(); } }
        }

        private string _brainKey = string.Empty;
        public string BrainKey
        {
            get => _brainKey;
            set { if (_brainKey != value) { _brainKey = value; OnPropertyChanged(); } }
        }

        private string _executorKey = string.Empty;
        public string ExecutorKey
        {
            get => _executorKey;
            set { if (_executorKey != value) { _executorKey = value; OnPropertyChanged(); } }
        }

        private string _reportKey = string.Empty;
        public string ReportKey
        {
            get => _reportKey;
            set { if (_reportKey != value) { _reportKey = value; OnPropertyChanged(); } }
        }

        private string _machineStateKey = string.Empty;
        public string MachineStateKey
        {
            get => _machineStateKey;
            set { if (_machineStateKey != value) { _machineStateKey = value; OnPropertyChanged(); } }
        }

        private string _earlyWarningKey = string.Empty;
        public string EarlyWarningKey
        {
            get => _earlyWarningKey;
            set { if (_earlyWarningKey != value) { _earlyWarningKey = value; OnPropertyChanged(); } }
        }

        private string _performanceKey = string.Empty;
        public string PerformanceKey
        {
            get => _performanceKey;
            set { if (_performanceKey != value) { _performanceKey = value; OnPropertyChanged(); } }
        }

        private bool _enablePerformanceMonitor = true;
        public bool EnablePerformanceMonitor
        {
            get => _enablePerformanceMonitor;
            set { if (_enablePerformanceMonitor != value) { _enablePerformanceMonitor = value; OnPropertyChanged(); } }
        }

        private bool _enableAutoWindowsNotification = true;
        public bool EnableAutoWindowsNotification
        {
            get => _enableAutoWindowsNotification;
            set { if (_enableAutoWindowsNotification != value) { _enableAutoWindowsNotification = value; OnPropertyChanged(); } }
        }

        private float _diskUsageThresholdPercent = 90f;
        public float DiskUsageThresholdPercent
        {
            get => _diskUsageThresholdPercent;
            set
            {
                if (Math.Abs(_diskUsageThresholdPercent - value) > 0.01f)
                {
                    _diskUsageThresholdPercent = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _enableAgentControlModule = true;
        public bool EnableAgentControlModule
        {
            get => _enableAgentControlModule;
            set { if (_enableAgentControlModule != value) { _enableAgentControlModule = value; OnPropertyChanged(); } }
        }

        private bool _clearMachineAlarmsShadowMode;
        public bool ClearMachineAlarmsShadowMode
        {
            get => _clearMachineAlarmsShadowMode;
            set
            {
                if (_clearMachineAlarmsShadowMode != value)
                {
                    _clearMachineAlarmsShadowMode = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _flatFileLayout;
        [JsonProperty("flatFileLayout")]
        public bool FlatFileLayout
        {
            get => _flatFileLayout;
            set { if (_flatFileLayout != value) { _flatFileLayout = value; OnPropertyChanged(); } }
        }

        private bool _useOkNgSplitTables;
        public bool UseOkNgSplitTables
        {
            get => _useOkNgSplitTables;
            set { if (_useOkNgSplitTables != value) { _useOkNgSplitTables = value; OnPropertyChanged(); } }
        }

        private string _settingsEditPasswordHash = string.Empty;
        public string SettingsEditPasswordHash
        {
            get => _settingsEditPasswordHash;
            set { if (_settingsEditPasswordHash != value) { _settingsEditPasswordHash = value; OnPropertyChanged(); } }
        }

        private WarningRuleOptions _warningOptions = WarningRuleOptions.CreateDefault();
        [JsonProperty("warningOptions")]
        public WarningRuleOptions WarningOptions
        {
            get => _warningOptions;
            set
            {
                var normalized = WarningRuleOptions.Normalize(value);
                if (!ReferenceEquals(_warningOptions, normalized))
                {
                    _warningOptions = normalized;
                    OnPropertyChanged();
                }
                else
                {
                    _warningOptions = normalized;
                }
            }
        }

        public static AppConfig CreateDefault() => new AppConfig();

        public AppConfig Clone()
        {
            var clone = new AppConfig();
            clone.CopyFrom(this);
            return clone;
        }

        public void CopyFrom(AppConfig source)
        {
            if (source == null)
                return;

            ProductionLogPath = source.ProductionLogPath;
            AlarmLogPath = source.AlarmLogPath;
            PartCsvPath = source.PartCsvPath;
            MachineStateLogPath = source.MachineStateLogPath;
            IoMapCsvPath = source.IoMapCsvPath;
            MCPServerIP = source.MCPServerIP;
            MachineCommandBaseUrl = source.MachineCommandBaseUrl;
            WorkHttpServerPrefix = source.WorkHttpServerPrefix;
            URL = source.URL;
            AutoKey = source.AutoKey;
            AutoVisionKey = source.AutoVisionKey;
            AutoVisionImagePathA = source.AutoVisionImagePathA;
            AutoVisionImagePathB = source.AutoVisionImagePathB;
            AutoVisionEmptyReferenceImagePath = source.AutoVisionEmptyReferenceImagePath;
            AutoVisionImageTestMode = source.AutoVisionImageTestMode;
            AutoVisionImageLookbackSeconds = source.AutoVisionImageLookbackSeconds;
            AutoVisionCooldownSeconds = source.AutoVisionCooldownSeconds;
            TitleBarText = source.TitleBarText;
            UiLanguage = source.UiLanguage;
            User = source.User;
            MachineCode = source.MachineCode;
            ChatKey = source.ChatKey;
            DocumentKey = source.DocumentKey;
            BrainKey = source.BrainKey;
            ExecutorKey = source.ExecutorKey;
            ReportKey = source.ReportKey;
            MachineStateKey = source.MachineStateKey;
            EarlyWarningKey = source.EarlyWarningKey;
            PerformanceKey = source.PerformanceKey;
            EnablePerformanceMonitor = source.EnablePerformanceMonitor;
            EnableAutoWindowsNotification = source.EnableAutoWindowsNotification;
            DiskUsageThresholdPercent = source.DiskUsageThresholdPercent;
            EnableAgentControlModule = source.EnableAgentControlModule;
            ClearMachineAlarmsShadowMode = source.ClearMachineAlarmsShadowMode;
            FlatFileLayout = source.FlatFileLayout;
            UseOkNgSplitTables = source.UseOkNgSplitTables;
            SettingsEditPasswordHash = source.SettingsEditPasswordHash;
            WarningOptions = source.WarningOptions?.Clone() ?? WarningRuleOptions.CreateDefault();
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}

using Newtonsoft.Json;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EW_Assistant.Warnings
{
    /// <summary>
    /// 预警规则的集中配置项，避免散落的魔法数。
    /// </summary>
    public class WarningRuleOptions : INotifyPropertyChanged
    {
        public const int DefaultLookbackDays = 14;
        public const int DefaultMinHistorySamples = 100;
        public const int DefaultMinYieldSamples = 30;
        public const double DefaultYieldWarning = 0.97d;
        public const double DefaultYieldCritical = 0.95d;
        public const double DefaultTrendYieldThreshold = 0.98d;
        public const double DefaultThroughputWarningRatio = 0.9d;
        public const double DefaultThroughputCriticalRatio = 0.8d;
        public const int DefaultAlarmCountWarning = 5;
        public const int DefaultAlarmCountCritical = 10;
        public const double DefaultAlarmDowntimeWarningMin = 10d;
        public const double DefaultAlarmDowntimeCriticalMin = 20d;
        public const int DefaultTrendWindowHours = 6;
        public const int DefaultTrendMinTriggers = 4;
        public const string DefaultBaselineOutlierMode = "P10P90";
        public const double DefaultSuppressionHours = 2d;
        public const int DefaultResolveGraceMinutes = 120;

        private int _lookbackDays = DefaultLookbackDays;
        private int _minHistorySamples = DefaultMinHistorySamples;
        private int _minYieldSamples = DefaultMinYieldSamples;
        private double _yieldWarning = DefaultYieldWarning;
        private double _yieldCritical = DefaultYieldCritical;
        private double _trendYieldThreshold = DefaultTrendYieldThreshold;
        private double _throughputWarningRatio = DefaultThroughputWarningRatio;
        private double _throughputCriticalRatio = DefaultThroughputCriticalRatio;
        private int _alarmCountWarning = DefaultAlarmCountWarning;
        private int _alarmCountCritical = DefaultAlarmCountCritical;
        private double _alarmDowntimeWarningMin = DefaultAlarmDowntimeWarningMin;
        private double _alarmDowntimeCriticalMin = DefaultAlarmDowntimeCriticalMin;
        private int _trendWindowHours = DefaultTrendWindowHours;
        private int _trendMinTriggers = DefaultTrendMinTriggers;
        private bool _baselineExcludeDowntime;
        private string _baselineOutlierMode = DefaultBaselineOutlierMode;
        private double _suppressionHours = DefaultSuppressionHours;
        private int _resolveGraceMinutes = DefaultResolveGraceMinutes;

        [JsonProperty("lookbackDays")]
        public int LookbackDays
        {
            get => _lookbackDays;
            set { if (_lookbackDays != value) { _lookbackDays = value; OnPropertyChanged(); } }
        }

        [JsonProperty("minHistorySamples")]
        public int MinHistorySamples
        {
            get => _minHistorySamples;
            set { if (_minHistorySamples != value) { _minHistorySamples = value; OnPropertyChanged(); } }
        }

        [JsonProperty("minYieldSamples")]
        public int MinYieldSamples
        {
            get => _minYieldSamples;
            set { if (_minYieldSamples != value) { _minYieldSamples = value; OnPropertyChanged(); } }
        }

        [JsonProperty("yieldWarning")]
        public double YieldWarning
        {
            get => _yieldWarning;
            set { if (Math.Abs(_yieldWarning - value) > double.Epsilon) { _yieldWarning = value; OnPropertyChanged(); } }
        }

        [JsonProperty("yieldCritical")]
        public double YieldCritical
        {
            get => _yieldCritical;
            set { if (Math.Abs(_yieldCritical - value) > double.Epsilon) { _yieldCritical = value; OnPropertyChanged(); } }
        }

        [JsonProperty("trendYieldThreshold")]
        public double TrendYieldThreshold
        {
            get => _trendYieldThreshold;
            set { if (Math.Abs(_trendYieldThreshold - value) > double.Epsilon) { _trendYieldThreshold = value; OnPropertyChanged(); } }
        }

        [JsonProperty("throughputWarningRatio")]
        public double ThroughputWarningRatio
        {
            get => _throughputWarningRatio;
            set { if (Math.Abs(_throughputWarningRatio - value) > double.Epsilon) { _throughputWarningRatio = value; OnPropertyChanged(); } }
        }

        [JsonProperty("throughputCriticalRatio")]
        public double ThroughputCriticalRatio
        {
            get => _throughputCriticalRatio;
            set { if (Math.Abs(_throughputCriticalRatio - value) > double.Epsilon) { _throughputCriticalRatio = value; OnPropertyChanged(); } }
        }

        [JsonProperty("alarmCountWarning")]
        public int AlarmCountWarning
        {
            get => _alarmCountWarning;
            set { if (_alarmCountWarning != value) { _alarmCountWarning = value; OnPropertyChanged(); } }
        }

        [JsonProperty("alarmCountCritical")]
        public int AlarmCountCritical
        {
            get => _alarmCountCritical;
            set { if (_alarmCountCritical != value) { _alarmCountCritical = value; OnPropertyChanged(); } }
        }

        [JsonProperty("alarmDowntimeWarningMin")]
        public double AlarmDowntimeWarningMin
        {
            get => _alarmDowntimeWarningMin;
            set { if (Math.Abs(_alarmDowntimeWarningMin - value) > double.Epsilon) { _alarmDowntimeWarningMin = value; OnPropertyChanged(); } }
        }

        [JsonProperty("alarmDowntimeCriticalMin")]
        public double AlarmDowntimeCriticalMin
        {
            get => _alarmDowntimeCriticalMin;
            set { if (Math.Abs(_alarmDowntimeCriticalMin - value) > double.Epsilon) { _alarmDowntimeCriticalMin = value; OnPropertyChanged(); } }
        }

        [JsonProperty("trendWindowHours")]
        public int TrendWindowHours
        {
            get => _trendWindowHours;
            set { if (_trendWindowHours != value) { _trendWindowHours = value; OnPropertyChanged(); } }
        }

        [JsonProperty("trendMinTriggers")]
        public int TrendMinTriggers
        {
            get => _trendMinTriggers;
            set { if (_trendMinTriggers != value) { _trendMinTriggers = value; OnPropertyChanged(); } }
        }

        [JsonProperty("baselineExcludeDowntime")]
        public bool BaselineExcludeDowntime
        {
            get => _baselineExcludeDowntime;
            set { if (_baselineExcludeDowntime != value) { _baselineExcludeDowntime = value; OnPropertyChanged(); } }
        }

        [JsonProperty("baselineOutlierMode")]
        public string BaselineOutlierMode
        {
            get => _baselineOutlierMode;
            set
            {
                var normalized = NormalizeOutlierMode(value);
                if (!string.Equals(_baselineOutlierMode, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    _baselineOutlierMode = normalized;
                    OnPropertyChanged();
                }
            }
        }

        [JsonProperty("suppressionHours")]
        public double SuppressionHours
        {
            get => _suppressionHours;
            set { if (Math.Abs(_suppressionHours - value) > double.Epsilon) { _suppressionHours = value; OnPropertyChanged(); } }
        }

        [JsonProperty("resolveGraceMinutes")]
        public int ResolveGraceMinutes
        {
            get => _resolveGraceMinutes;
            set { if (_resolveGraceMinutes != value) { _resolveGraceMinutes = value; OnPropertyChanged(); } }
        }

        /// <summary>创建一份带默认阈值的配置，便于初始化或重置。</summary>
        public static WarningRuleOptions CreateDefault()
        {
            return new WarningRuleOptions
            {
                LookbackDays = DefaultLookbackDays,
                MinHistorySamples = DefaultMinHistorySamples,
                MinYieldSamples = DefaultMinYieldSamples,
                YieldWarning = DefaultYieldWarning,
                YieldCritical = DefaultYieldCritical,
                TrendYieldThreshold = DefaultTrendYieldThreshold,
                ThroughputWarningRatio = DefaultThroughputWarningRatio,
                ThroughputCriticalRatio = DefaultThroughputCriticalRatio,
                AlarmCountWarning = DefaultAlarmCountWarning,
                AlarmCountCritical = DefaultAlarmCountCritical,
                AlarmDowntimeWarningMin = DefaultAlarmDowntimeWarningMin,
                AlarmDowntimeCriticalMin = DefaultAlarmDowntimeCriticalMin,
                TrendWindowHours = DefaultTrendWindowHours,
                TrendMinTriggers = DefaultTrendMinTriggers,
                BaselineExcludeDowntime = false,
                BaselineOutlierMode = DefaultBaselineOutlierMode,
                SuppressionHours = DefaultSuppressionHours,
                ResolveGraceMinutes = DefaultResolveGraceMinutes
            };
        }

        public static WarningRuleOptions Normalize(WarningRuleOptions input)
        {
            var opt = input ?? CreateDefault();

            if (opt.LookbackDays <= 0) opt.LookbackDays = DefaultLookbackDays;
            if (opt.MinHistorySamples <= 0) opt.MinHistorySamples = DefaultMinHistorySamples;
            if (opt.MinYieldSamples <= 0) opt.MinYieldSamples = DefaultMinYieldSamples;
            if (opt.YieldWarning <= 0) opt.YieldWarning = DefaultYieldWarning;
            if (opt.YieldCritical <= 0) opt.YieldCritical = DefaultYieldCritical;
            if (opt.TrendYieldThreshold <= 0) opt.TrendYieldThreshold = DefaultTrendYieldThreshold;
            if (opt.ThroughputWarningRatio <= 0) opt.ThroughputWarningRatio = DefaultThroughputWarningRatio;
            if (opt.ThroughputCriticalRatio <= 0) opt.ThroughputCriticalRatio = DefaultThroughputCriticalRatio;
            if (opt.AlarmCountWarning <= 0) opt.AlarmCountWarning = DefaultAlarmCountWarning;
            if (opt.AlarmCountCritical <= 0) opt.AlarmCountCritical = DefaultAlarmCountCritical;
            if (opt.AlarmDowntimeWarningMin <= 0) opt.AlarmDowntimeWarningMin = DefaultAlarmDowntimeWarningMin;
            if (opt.AlarmDowntimeCriticalMin <= 0) opt.AlarmDowntimeCriticalMin = DefaultAlarmDowntimeCriticalMin;
            if (opt.TrendWindowHours <= 0) opt.TrendWindowHours = DefaultTrendWindowHours;
            if (opt.TrendMinTriggers <= 0) opt.TrendMinTriggers = DefaultTrendMinTriggers;
            if (opt.SuppressionHours < 0) opt.SuppressionHours = DefaultSuppressionHours;
            if (opt.ResolveGraceMinutes <= 0) opt.ResolveGraceMinutes = DefaultResolveGraceMinutes;
            opt.BaselineOutlierMode = NormalizeOutlierMode(opt.BaselineOutlierMode);

            return opt;
        }

        public WarningRuleOptions Clone()
        {
            return new WarningRuleOptions
            {
                LookbackDays = LookbackDays,
                MinHistorySamples = MinHistorySamples,
                MinYieldSamples = MinYieldSamples,
                YieldWarning = YieldWarning,
                YieldCritical = YieldCritical,
                TrendYieldThreshold = TrendYieldThreshold,
                ThroughputWarningRatio = ThroughputWarningRatio,
                ThroughputCriticalRatio = ThroughputCriticalRatio,
                AlarmCountWarning = AlarmCountWarning,
                AlarmCountCritical = AlarmCountCritical,
                AlarmDowntimeWarningMin = AlarmDowntimeWarningMin,
                AlarmDowntimeCriticalMin = AlarmDowntimeCriticalMin,
                TrendWindowHours = TrendWindowHours,
                TrendMinTriggers = TrendMinTriggers,
                BaselineExcludeDowntime = BaselineExcludeDowntime,
                BaselineOutlierMode = BaselineOutlierMode,
                SuppressionHours = SuppressionHours,
                ResolveGraceMinutes = ResolveGraceMinutes
            };
        }

        private static string NormalizeOutlierMode(string mode)
        {
            var m = (mode ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(m)) return DefaultBaselineOutlierMode;

            if (string.Equals(m, "None", StringComparison.OrdinalIgnoreCase)) return "None";
            if (string.Equals(m, "P10P90", StringComparison.OrdinalIgnoreCase)) return "P10P90";
            if (string.Equals(m, "IQR", StringComparison.OrdinalIgnoreCase)) return "IQR";
            return DefaultBaselineOutlierMode;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            var handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(name));
            }
        }
    }
}

using EW_Assistant.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace EW_Assistant.Warnings
{
    /// <summary>
    /// 基于本地 CSV 的预警规则引擎（基线、趋势、组合规则均可配置）。
    /// </summary>
    public class WarningRuleEngine
    {
        private static readonly TimeSpan BaselineCacheTtl = TimeSpan.FromMinutes(5);
        private static readonly Dictionary<string, BaselineCacheEntry> s_baselineCache =
            new Dictionary<string, BaselineCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, DateTime> s_ruleSuppressUntil =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private static readonly object s_cacheLock = new object();
        private static readonly object s_suppressLock = new object();

        private readonly ProductionCsvReader _prodReader;
        private readonly AlarmCsvReader _alarmReader;
        private readonly WarningRuleOptions _options;

        public WarningRuleEngine(ProductionCsvReader prodReader, AlarmCsvReader alarmReader, WarningRuleOptions options = null)
        {
            _prodReader = prodReader ?? throw new ArgumentNullException(nameof(prodReader));
            _alarmReader = alarmReader ?? throw new ArgumentNullException(nameof(alarmReader));
            _options = WarningRuleOptions.Normalize(options ?? ConfigService.Current?.WarningOptions ?? WarningRuleOptions.CreateDefault()).Clone();
        }

        /// <summary>
        /// 按规则构建最近 24 小时的预警列表。
        /// </summary>
        public IList<WarningItem> BuildWarnings(DateTime now)
        {
            var bucket = new Dictionary<string, WarningItem>(StringComparer.OrdinalIgnoreCase);
            var windowStart = now.AddHours(-12);

            var productions = _prodReader.GetProductionRange(windowStart, now) ?? new List<ProductionHourRecord>();
            var alarms = _alarmReader.GetAlarms(windowStart, now) ?? new List<AlarmHourStat>();
            var alarmTotals = BuildAlarmAggregates(alarms);
            var alarmsByHour = GroupAlarmsByHour(alarms);

            var baseline = GetBaselineEntry(now);

            foreach (var rec in productions)
            {
                if (rec.Hour < windowStart || rec.Hour >= now) continue;

                ApplyYieldRule(bucket, rec, productions);
                ApplyThroughputRule(bucket, rec, baseline, productions);
                ApplyCombinedRules(bucket, rec, alarmTotals, alarmsByHour, baseline, productions);
            }

            foreach (var stat in alarms)
            {
                if (stat.Hour < windowStart || stat.Hour >= now) continue;
                ApplyAlarmRule(bucket, stat);
            }

            ApplyTrendRules(bucket, productions, baseline, now);

            return bucket.Values
                .OrderBy(w => w.StartTime)
                .ThenByDescending(w => RankLevel(w.Level))
                .ThenBy(w => w.RuleId)
                .ToList();
        }

        #region 规则实现

        /// <summary>良率绝对阈值规则：样本足够且低于阈值时生成预警。</summary>
        private void ApplyYieldRule(IDictionary<string, WarningItem> bucket, ProductionHourRecord rec, IList<ProductionHourRecord> allProductions)
        {
            if (rec == null || rec.Total < _options.MinYieldSamples) return;

            var yield = rec.Yield;
            string level;
            double threshold;
            if (yield < _options.YieldCritical)
            {
                level = "Critical";
                threshold = _options.YieldCritical;
            }
            else if (yield < _options.YieldWarning)
            {
                level = "Warning";
                threshold = _options.YieldWarning;
            }
            else
            {
                return;
            }

            var start = rec.Hour;
            var end = start.AddHours(1);
            var yieldSeries = BuildYieldSeries(allProductions, start, 6);
            var summary = string.Format(CultureInfo.InvariantCulture,
                "{0:HH}:00-{1:HH}:00 良率 {2}，低于阈值 {3}（样本 {4}，Pass={5}，Fail={6}）。",
                start, end, ToPercent(yield), ToPercent(threshold), rec.Total, rec.Pass, rec.Fail);

            var item = new WarningItem
            {
                Key = FormatKey("LOW_YIELD_ABS", start),
                RuleId = "LOW_YIELD_ABS",
                RuleName = "低良率阈值",
                Level = level,
                Type = "Yield",
                StartTime = start,
                EndTime = end,
                FirstDetected = start,
                LastDetected = start,
                MetricName = "良率",
                CurrentValue = yield,
                ThresholdValue = threshold,
                Status = "Active",
                Summary = summary
            };

            TryAddWarning(bucket, item, (oldVal, newVal) => Math.Min(oldVal, newVal));
        }

        /// <summary>产能规则：对比计划/基线，低于阈值时触发。</summary>
        private void ApplyThroughputRule(IDictionary<string, WarningItem> bucket, ProductionHourRecord rec, BaselineCacheEntry baseline, IList<ProductionHourRecord> allProductions)
        {
            if (rec == null) return;
            if (rec.Total <= 0) return; // 计划性停机或无产出不计入预警

            var plan = ResolvePlan(rec, baseline);
            if (plan.Plan <= 0)
            {
                // 零产出场景不提示样本不足，直接跳过
                return;
            }

            var actual = rec.Total;
            string level;
            double ratio;
            if (actual < plan.Plan * _options.ThroughputCriticalRatio)
            {
                level = "Critical";
                ratio = _options.ThroughputCriticalRatio;
            }
            else if (actual < plan.Plan * _options.ThroughputWarningRatio)
            {
                level = "Warning";
                ratio = _options.ThroughputWarningRatio;
            }
            else
            {
                return;
            }

            var threshold = plan.Plan * ratio;
            var start = rec.Hour;
            var end = start.AddHours(1);
            var ratioText = ToPercent(ratio);
            var planSource = plan.Source == "Planned" ? "工单计划" : plan.Source == "Baseline" ? "历史基线" : "默认产能";
            var throughputSeries = BuildThroughputSeries(allProductions, start, 6);
            var summary = string.Format(CultureInfo.InvariantCulture,
                "{0:HH}:00-{1:HH}:00 产量 {2}，低于{3} {4:F0} 的 {5}（阈值 {6:F0}，基线样本 {7}，基线值 {8}）。",
                start, end, actual, planSource, plan.Plan, ratioText, threshold, plan.BaselineSamples,
                plan.BaselineValue.HasValue ? plan.BaselineValue.Value.ToString("F1", CultureInfo.InvariantCulture) : "N/A");

            var item = new WarningItem
            {
                Key = FormatKey("LOW_THROUGHPUT_PLAN", start),
                RuleId = "LOW_THROUGHPUT_PLAN",
                RuleName = "产能低于计划/基线",
                Level = level,
                Type = "Throughput",
                StartTime = start,
                EndTime = end,
                FirstDetected = start,
                LastDetected = start,
                MetricName = "产量",
                CurrentValue = actual,
                BaselineValue = plan.BaselineValue ?? plan.Plan,
                ThresholdValue = threshold,
                Status = "Active",
                Summary = summary
            };

            TryAddWarning(bucket, item, (oldVal, newVal) => Math.Min(oldVal, newVal));
        }

        /// <summary>趋势规则：在窗口内累计多次产能异常则触发。</summary>
        private void ApplyTrendRules(IDictionary<string, WarningItem> bucket, IList<ProductionHourRecord> productions, BaselineCacheEntry baseline, DateTime now)
        {
            var windowEnd = now;
            var windowStart = now.AddHours(-_options.TrendWindowHours);
            var windowProd = productions
                .Where(p => p.Hour >= windowStart && p.Hour < windowEnd)
                .OrderBy(p => p.Hour)
                .ToList();

            if (windowProd.Count == 0) return;

            var lowThroughputHours = 0;
            foreach (var rec in windowProd)
            {
                if (rec.Total <= 0) continue;
                var plan = ResolvePlan(rec, baseline);
                if (plan.Plan <= 0) continue;
                var threshold = plan.Plan * _options.ThroughputWarningRatio;
                if (rec.Total < threshold)
                {
                    lowThroughputHours++;
                }
            }

            if (lowThroughputHours >= _options.TrendMinTriggers)
            {
                var summary = string.Format(CultureInfo.InvariantCulture,
                    "过去 {0} 小时中有 {1} 小时产能低于基线×{2}（{3:HH}:00-{4:HH}:00，窗口样本 {5}）。",
                    _options.TrendWindowHours, lowThroughputHours, ToPercent(_options.ThroughputWarningRatio), windowStart, windowEnd, windowProd.Count);

                var item = new WarningItem
                {
                    Key = FormatKey("LOW_THROUGHPUT_TREND", windowStart),
                    RuleId = "LOW_THROUGHPUT_TREND",
                    RuleName = "产能下行趋势",
                    Level = "Warning",
                    Type = "Throughput",
                    StartTime = windowStart,
                    EndTime = windowEnd,
                    FirstDetected = windowStart,
                    LastDetected = windowEnd,
                    MetricName = "产能趋势",
                    CurrentValue = lowThroughputHours,
                    ThresholdValue = _options.TrendMinTriggers,
                    Status = "Active",
                    Summary = summary
                };

                TryAddWarning(bucket, item, (oldVal, newVal) => Math.Max(oldVal, newVal));
            }

            var lowYieldHours = 0;
            foreach (var rec in windowProd)
            {
                if (rec.Total < _options.MinYieldSamples) continue;
                if (rec.Yield < _options.TrendYieldThreshold)
                {
                    lowYieldHours++;
                }
            }

            if (lowYieldHours >= _options.TrendMinTriggers)
            {
                var summary = string.Format(CultureInfo.InvariantCulture,
                    "过去 {0} 小时中有 {1} 小时良率低于 {2}（{3:HH}:00-{4:HH}:00，窗口样本 {5}）。",
                    _options.TrendWindowHours, lowYieldHours, ToPercent(_options.TrendYieldThreshold), windowStart, windowEnd, windowProd.Count);

                var item = new WarningItem
                {
                    Key = FormatKey("LOW_YIELD_TREND", windowStart),
                    RuleId = "LOW_YIELD_TREND",
                    RuleName = "良率下行趋势",
                    Level = "Warning",
                    Type = "Yield",
                    StartTime = windowStart,
                    EndTime = windowEnd,
                    FirstDetected = windowStart,
                    LastDetected = windowEnd,
                    MetricName = "良率趋势",
                    CurrentValue = lowYieldHours,
                    ThresholdValue = _options.TrendMinTriggers,
                    Status = "Active",
                    Summary = summary
                };

                TryAddWarning(bucket, item, (oldVal, newVal) => Math.Max(oldVal, newVal));
            }
        }

        /// <summary>良率/产能与报警的组合规则：低良率或低产能且有报警时触发。</summary>
        private void ApplyCombinedRules(
            IDictionary<string, WarningItem> bucket,
            ProductionHourRecord rec,
            Dictionary<DateTime, AlarmAggregate> alarmTotals,
            Dictionary<DateTime, List<AlarmHourStat>> alarmsByHour,
            BaselineCacheEntry baseline,
            IList<ProductionHourRecord> allProductions)
        {
            if (rec == null) return;

            AlarmAggregate agg;
            alarmTotals.TryGetValue(rec.Hour, out agg);
            var alarmSeverity = GetAlarmSeverity(agg);
            var topAlarm = GetTopAlarmStat(rec.Hour, alarmsByHour);
            var topAlarmText = topAlarm == null || string.IsNullOrWhiteSpace(topAlarm.Message)
                ? string.Empty
                : "，报警描述：" + topAlarm.Message;

            if (alarmSeverity != null && rec.Total >= _options.MinYieldSamples && rec.Yield < _options.YieldCritical)
            {
                var start = rec.Hour;
                var end = start.AddHours(1);
                var topText = BuildTopAlarmSummary(rec.Hour, alarmsByHour);
                var summary = string.Format(CultureInfo.InvariantCulture,
                    "{0:HH}:00-{1:HH}:00 良率 {2}（样本 {6}）且报警 {3} 次/停机 {4:F1} 分钟，关联报警 {5}。",
                    start, end, ToPercent(rec.Yield), agg == null ? 0 : agg.Count, agg == null ? 0d : agg.DowntimeMinutes, topText, rec.Total,
                    BuildYieldSeries(allProductions, start, 6));
                if (!string.IsNullOrEmpty(topAlarmText))
                {
                    summary += topAlarmText;
                }

                var item = new WarningItem
                {
                    Key = FormatKey("YIELD_ALARM_COMBINED", start),
                    RuleId = "YIELD_ALARM_COMBINED",
                    RuleName = "良率异常伴随报警",
                    Level = alarmSeverity == "Critical" ? "Critical" : "Warning",
                    Type = "Combined",
                    StartTime = start,
                    EndTime = end,
                    FirstDetected = start,
                    LastDetected = start,
                    MetricName = "良率+报警",
                    CurrentValue = rec.Yield,
                    ThresholdValue = _options.YieldCritical,
                    Status = "Active",
                    Summary = summary
                };

                TryAddWarning(bucket, item, (oldVal, newVal) => Math.Min(oldVal, newVal));
            }

            var plan = ResolvePlan(rec, baseline);
            if (rec.Total <= 0) return; // 停机不计入组合
            if (plan.Plan <= 0 || alarmSeverity == null) return;

            var critThreshold = plan.Plan * _options.ThroughputCriticalRatio;
            if (rec.Total < critThreshold)
            {
                var start = rec.Hour;
                var end = start.AddHours(1);
                var topText = BuildTopAlarmSummary(rec.Hour, alarmsByHour);
                var summary = string.Format(CultureInfo.InvariantCulture,
                    "{0:HH}:00-{1:HH}:00 产量 {2} 低于基线阈值 {3:F0}（基线 {7}，样本 {8}），且报警 {4} 次/停机 {5:F1} 分钟。Top：{6}",
                    start, end, rec.Total, critThreshold, agg == null ? 0 : agg.Count, agg == null ? 0d : agg.DowntimeMinutes, topText,
                    plan.BaselineValue.HasValue ? plan.BaselineValue.Value.ToString("F1", CultureInfo.InvariantCulture) : "N/A",
                    plan.BaselineSamples);
                if (!string.IsNullOrEmpty(topAlarmText))
                {
                    summary += topAlarmText;
                }

                var item = new WarningItem
                {
                    Key = FormatKey("THROUGHPUT_ALARM_COMBINED", start),
                    RuleId = "THROUGHPUT_ALARM_COMBINED",
                    RuleName = "产能异常伴随报警",
                    Level = alarmSeverity == "Critical" ? "Critical" : "Warning",
                    Type = "Combined",
                    StartTime = start,
                    EndTime = end,
                    FirstDetected = start,
                    LastDetected = start,
                    MetricName = "产能+报警",
                    CurrentValue = rec.Total,
                    BaselineValue = plan.BaselineValue ?? plan.Plan,
                    ThresholdValue = critThreshold,
                    Status = "Active",
                    Summary = summary + topAlarmText
                };

                TryAddWarning(bucket, item, (oldVal, newVal) => Math.Min(oldVal, newVal));
            }
        }

        /// <summary>报警单项规则：按报警次数或停机分钟筛选高频/长时报警。</summary>
        private void ApplyAlarmRule(IDictionary<string, WarningItem> bucket, AlarmHourStat stat)
        {
            if (stat == null) return;

            var agg = new AlarmAggregate
            {
                Count = stat.Count,
                DowntimeMinutes = stat.DowntimeMinutes
            };
            var severity = GetAlarmSeverity(agg);
            if (severity == null) return;

            var start = stat.Hour;
            var end = start.AddHours(1);
            var metricByDowntime = stat.DowntimeMinutes >= stat.Count && stat.DowntimeMinutes >= _options.AlarmDowntimeWarningMin;
            var metricName = metricByDowntime ? "停机分钟" : "报警次数";
            var currentValue = metricByDowntime ? stat.DowntimeMinutes : stat.Count;
            var threshold = metricByDowntime
                ? (severity == "Critical" ? _options.AlarmDowntimeCriticalMin : _options.AlarmDowntimeWarningMin)
                : (severity == "Critical" ? _options.AlarmCountCritical : _options.AlarmCountWarning);

            var code = string.IsNullOrWhiteSpace(stat.Code) ? "UNKNOWN" : stat.Code;
            var summary = string.Format(CultureInfo.InvariantCulture,
                "{0:HH}:00-{1:HH}:00 报警 {2} 次数 {3}，停机 {4:F1} 分钟。EVIDENCE{{alarm_code={2}; alarm_count={3}; downtime_min={4:F1}; alarm_text={5}}}",
                start, end, code, stat.Count, stat.DowntimeMinutes,
                string.IsNullOrWhiteSpace(stat.Message) ? string.Empty : stat.Message);

            var ruleId = string.Format(CultureInfo.InvariantCulture, "ALARM_FREQUENT_{0}", string.IsNullOrWhiteSpace(stat.Code) ? "UNKNOWN" : stat.Code);
            var item = new WarningItem
            {
                Key = FormatKey(ruleId, start),
                RuleId = ruleId,
                RuleName = "报警高频或停机",
                Level = severity,
                Type = "Alarm",
                StartTime = start,
                EndTime = end,
                FirstDetected = start,
                LastDetected = start,
                MetricName = metricName,
                CurrentValue = currentValue,
                ThresholdValue = threshold,
                Status = "Active",
                Summary = summary
            };

            TryAddWarning(bucket, item, (oldVal, newVal) => Math.Max(oldVal, newVal));
        }

        #endregion

        #region 基线与计划

        private void MaybeAddThroughputInfo(IDictionary<string, WarningItem> bucket, ProductionHourRecord rec, PlanResult plan)
        {
            if (rec == null) return;

            var start = rec.Hour;
            var end = start.AddHours(1);
            var samples = plan.BaselineSamples;
            var ruleId = "THROUGHPUT_BASELINE_MISSING";
            var summary = string.Format(CultureInfo.InvariantCulture,
                "{0:HH}:00-{1:HH}:00 缺少计划产能，历史基线样本 {2}（需 ≥ {3}，基线值 {4}），跳过产能预警。",
                start, end, samples, _options.MinHistorySamples, plan.BaselineValue.HasValue ? plan.BaselineValue.Value.ToString("F1", CultureInfo.InvariantCulture) : "N/A");

            var item = new WarningItem
            {
                Key = FormatKey(ruleId, start),
                RuleId = ruleId,
                RuleName = "产能基线缺失或样本不足",
                Level = "Info",
                Type = "Throughput",
                StartTime = start,
                EndTime = end,
                FirstDetected = start,
                LastDetected = start,
                MetricName = "基线样本",
                CurrentValue = samples,
                ThresholdValue = _options.MinHistorySamples,
                Status = "Active",
                Summary = summary
            };

            TryAddWarning(bucket, item, (oldVal, newVal) => Math.Max(oldVal, newVal));
        }

        private PlanResult ResolvePlan(ProductionHourRecord rec, BaselineCacheEntry baseline)
        {
            if (rec == null)
            {
                return new PlanResult { Plan = 0, Source = "None" };
            }

            if (rec.PlannedOutput.HasValue && rec.PlannedOutput.Value > 0)
            {
                return new PlanResult
                {
                    Plan = rec.PlannedOutput.Value,
                    Source = "Planned",
                    BaselineValue = null,
                    BaselineSamples = 0
                };
            }

            var baselineValue = GetBaselineValue(baseline, rec.Hour.Hour);
            if (baselineValue.Median.HasValue && baselineValue.SampleCount >= _options.MinHistorySamples)
            {
                return new PlanResult
                {
                    Plan = Math.Round(baselineValue.Median.Value),
                    Source = "Baseline",
                    BaselineValue = baselineValue.Median,
                    BaselineSamples = baselineValue.SampleCount
                };
            }

            return new PlanResult
            {
                Plan = 0,
                Source = "None",
                BaselineValue = baselineValue.Median,
                BaselineSamples = baselineValue.SampleCount
            };
        }

        private BaselineCacheEntry GetBaselineEntry(DateTime now)
        {
            var cacheKey = BuildBaselineCacheKey();
            BaselineCacheEntry entry;

            lock (s_cacheLock)
            {
                if (s_baselineCache.TryGetValue(cacheKey, out entry))
                {
                    if ((now - entry.GeneratedAt) < BaselineCacheTtl)
                    {
                        return entry;
                    }
                }
            }

            var start = now.AddDays(-_options.LookbackDays);
            var prodHistory = _prodReader.GetProductionRange(start, now) ?? new List<ProductionHourRecord>();
            var alarmHistory = _options.BaselineExcludeDowntime
                ? _alarmReader.GetAlarms(start, now) ?? new List<AlarmHourStat>()
                : new List<AlarmHourStat>();
            var downtimeMap = BuildAlarmAggregates(alarmHistory);

            var hourly = new Dictionary<int, BaselineValue>();
            for (int h = 0; h < 24; h++)
            {
                var candidates = prodHistory
                    .Where(p => p.Hour.Hour == h && p.Total > 0)
                    .ToList();

                if (_options.BaselineExcludeDowntime && candidates.Count > 0)
                {
                    candidates = candidates
                        .Where(p =>
                        {
                            AlarmAggregate agg;
                            if (downtimeMap.TryGetValue(p.Hour, out agg))
                            {
                                return agg.DowntimeMinutes < _options.AlarmDowntimeWarningMin;
                            }
                            return true;
                        })
                        .ToList();
                }

                var values = candidates.Select(p => (double)p.Total).ToList();
                values = ApplyOutlierFilter(values, _options.BaselineOutlierMode);
                var median = CalcMedian(values);

                hourly[h] = new BaselineValue
                {
                    Median = median,
                    SampleCount = values.Count
                };
            }

            entry = new BaselineCacheEntry
            {
                GeneratedAt = now
            };
            foreach (var kvp in hourly)
            {
                entry.Hourly[kvp.Key] = kvp.Value;
            }

            lock (s_cacheLock)
            {
                s_baselineCache[cacheKey] = entry;
            }
            return entry;
        }

        private string BuildBaselineCacheKey()
        {
            return string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2}|{3}|{4}",
                _prodReader.Root ?? string.Empty,
                _alarmReader.Root ?? string.Empty,
                _options.LookbackDays,
                _options.BaselineExcludeDowntime,
                _options.BaselineOutlierMode ?? "None");
        }

        private static List<double> ApplyOutlierFilter(List<double> values, string mode)
        {
            if (values == null) return new List<double>();
            var sorted = values.OrderBy(v => v).ToList();
            if (sorted.Count == 0) return sorted;

            if (string.Equals(mode, "P10P90", StringComparison.OrdinalIgnoreCase))
            {
                if (sorted.Count < 5) return sorted;
                var p10 = CalcPercentile(sorted, 0.1);
                var p90 = CalcPercentile(sorted, 0.9);
                return sorted.Where(v => v >= p10 && v <= p90).ToList();
            }

            if (string.Equals(mode, "IQR", StringComparison.OrdinalIgnoreCase))
            {
                if (sorted.Count < 4) return sorted;
                var q1 = CalcPercentile(sorted, 0.25);
                var q3 = CalcPercentile(sorted, 0.75);
                var iqr = q3 - q1;
                var low = q1 - 1.5 * iqr;
                var high = q3 + 1.5 * iqr;
                return sorted.Where(v => v >= low && v <= high).ToList();
            }

            return sorted;
        }

        private static double? CalcMedian(IList<double> sortedValues)
        {
            if (sortedValues == null || sortedValues.Count == 0) return null;
            var sorted = sortedValues.OrderBy(v => v).ToList();
            return CalcPercentile(sorted, 0.5);
        }

        private static double CalcPercentile(IList<double> sorted, double p)
        {
            if (sorted == null || sorted.Count == 0) return 0d;
            var n = sorted.Count;
            var rank = (n - 1) * p;
            var low = (int)Math.Floor(rank);
            var high = (int)Math.Ceiling(rank);
            if (low == high) return sorted[low];
            var weight = rank - low;
            return sorted[low] + (sorted[high] - sorted[low]) * weight;
        }

        private BaselineValue GetBaselineValue(BaselineCacheEntry entry, int hour)
        {
            if (entry != null && entry.Hourly.TryGetValue(hour, out var value))
            {
                return value;
            }

            return new BaselineValue
            {
                Median = null,
                SampleCount = 0
            };
        }

        #endregion

        #region 去重与缓存

        private void TryAddWarning(IDictionary<string, WarningItem> bucket, WarningItem item, Func<double, double, double> pickCurrent)
        {
            if (item == null) return;
            if (IsSuppressed(item)) return;

            Upsert(bucket, item, pickCurrent ?? ((oldVal, newVal) => newVal));
            MarkSuppression(item);
        }

        private bool IsSuppressed(WarningItem item)
        {
            if (item == null || _options.SuppressionHours <= 0) return false;

            lock (s_suppressLock)
            {
                if (s_ruleSuppressUntil.TryGetValue(item.RuleId, out var until))
                {
                    if (item.StartTime < until)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private void MarkSuppression(WarningItem item)
        {
            if (item == null || _options.SuppressionHours <= 0) return;
            var until = item.EndTime.AddHours(_options.SuppressionHours);

            lock (s_suppressLock)
            {
                if (!s_ruleSuppressUntil.TryGetValue(item.RuleId, out var existing) || until > existing)
                {
                    s_ruleSuppressUntil[item.RuleId] = until;
                }
            }
        }

        private static void Upsert(IDictionary<string, WarningItem> bucket, WarningItem incoming, Func<double, double, double> pickCurrent)
        {
            if (!bucket.TryGetValue(incoming.Key, out var existing))
            {
                bucket[incoming.Key] = incoming;
                return;
            }

            existing.FirstDetected = incoming.FirstDetected < existing.FirstDetected ? incoming.FirstDetected : existing.FirstDetected;
            existing.LastDetected = incoming.LastDetected > existing.LastDetected ? incoming.LastDetected : existing.LastDetected;

            var newValue = pickCurrent(existing.CurrentValue, incoming.CurrentValue);
            if (Math.Abs(newValue - existing.CurrentValue) > double.Epsilon)
            {
                existing.CurrentValue = newValue;
                existing.Summary = incoming.Summary;
            }

            if (incoming.ThresholdValue.HasValue)
            {
                existing.ThresholdValue = incoming.ThresholdValue;
            }
            if (incoming.BaselineValue.HasValue)
            {
                existing.BaselineValue = incoming.BaselineValue;
            }
            if (!string.IsNullOrWhiteSpace(incoming.MetricName))
            {
                existing.MetricName = incoming.MetricName;
            }
            if (!string.IsNullOrWhiteSpace(incoming.Status))
            {
                existing.Status = incoming.Status;
            }

            if (RankLevel(incoming.Level) > RankLevel(existing.Level))
            {
                existing.Level = incoming.Level;
                existing.Summary = incoming.Summary;
            }
        }

        #endregion

        #region 辅助方法

        private static string BuildThroughputSeries(IList<ProductionHourRecord> productions, DateTime anchor, int hours)
        {
            if (productions == null || productions.Count == 0) return "[]";
            var start = anchor.AddHours(-hours + 1);
            var seq = productions
                .Where(p => p.Hour >= start && p.Hour <= anchor)
                .OrderBy(p => p.Hour)
                .Select(p => p.Total.ToString("F0", CultureInfo.InvariantCulture))
                .ToList();
            return seq.Count == 0 ? "[]" : "[" + string.Join(",", seq) + "]";
        }

        private static string BuildYieldSeries(IList<ProductionHourRecord> productions, DateTime anchor, int hours)
        {
            if (productions == null || productions.Count == 0) return "[]";
            var start = anchor.AddHours(-hours + 1);
            var seq = productions
                .Where(p => p.Hour >= start && p.Hour <= anchor && p.Total > 0)
                .OrderBy(p => p.Hour)
                .Select(p => (p.Yield * 100d).ToString("F1", CultureInfo.InvariantCulture))
                .ToList();
            return seq.Count == 0 ? "[]" : "[" + string.Join(",", seq) + "]";
        }

        private static Dictionary<DateTime, AlarmAggregate> BuildAlarmAggregates(IList<AlarmHourStat> alarms)
        {
            var map = new Dictionary<DateTime, AlarmAggregate>();
            if (alarms == null) return map;

            foreach (var stat in alarms)
            {
                AlarmAggregate agg;
                if (!map.TryGetValue(stat.Hour, out agg))
                {
                    agg = new AlarmAggregate();
                    map[stat.Hour] = agg;
                }

                agg.Count += stat.Count;
                agg.DowntimeMinutes += stat.DowntimeMinutes;
            }

            return map;
        }

        private static Dictionary<DateTime, List<AlarmHourStat>> GroupAlarmsByHour(IList<AlarmHourStat> alarms)
        {
            var map = new Dictionary<DateTime, List<AlarmHourStat>>();
            if (alarms == null) return map;

            foreach (var stat in alarms)
            {
                List<AlarmHourStat> list;
                if (!map.TryGetValue(stat.Hour, out list))
                {
                    list = new List<AlarmHourStat>();
                    map[stat.Hour] = list;
                }
                list.Add(stat);
            }

            return map;
        }

        private string GetAlarmSeverity(AlarmAggregate agg)
        {
            if (agg == null) return null;

            if (agg.DowntimeMinutes >= _options.AlarmDowntimeCriticalMin || agg.Count >= _options.AlarmCountCritical)
                return "Critical";

            if (agg.DowntimeMinutes >= _options.AlarmDowntimeWarningMin || agg.Count >= _options.AlarmCountWarning)
                return "Warning";

            return null;
        }

        private static string BuildTopAlarmSummary(DateTime hour, Dictionary<DateTime, List<AlarmHourStat>> alarmsByHour)
        {
            if (alarmsByHour == null || !alarmsByHour.TryGetValue(hour, out var list) || list.Count == 0)
            {
                return "无关联报警";
            }

            var top = list
                .OrderByDescending(a => a.DowntimeMinutes)
                .ThenByDescending(a => a.Count)
                .Take(2)
                .Select(a => string.Format(CultureInfo.InvariantCulture, "{0}({1}次/{2:F1}分)",
                    string.IsNullOrWhiteSpace(a.Code) ? "UNKNOWN" : a.Code,
                    a.Count,
                    a.DowntimeMinutes));

            return string.Join("，", top);
        }

        private static AlarmHourStat GetTopAlarmStat(DateTime hour, Dictionary<DateTime, List<AlarmHourStat>> alarmsByHour)
        {
            if (alarmsByHour == null || !alarmsByHour.TryGetValue(hour, out var list) || list.Count == 0)
            {
                return null;
            }

            return list
                .OrderByDescending(a => a.DowntimeMinutes)
                .ThenByDescending(a => a.Count)
                .ThenBy(a => string.IsNullOrWhiteSpace(a.Code) ? "UNKNOWN" : a.Code, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        private static string FormatKey(string ruleId, DateTime startTime)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0}|{1:yyyyMMddHH}", ruleId, startTime);
        }

        private static int RankLevel(string level)
        {
            switch ((level ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "critical": return 3;
                case "warning": return 2;
                case "info": return 1;
                default: return 0;
            }
        }

        private static string ToPercent(double value)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:F1}%", value * 100);
        }

        #endregion

        #region 内部模型

        private sealed class BaselineCacheEntry
        {
            public DateTime GeneratedAt { get; set; }
            public Dictionary<int, BaselineValue> Hourly { get; } = new Dictionary<int, BaselineValue>();
        }

        private sealed class BaselineValue
        {
            public double? Median { get; set; }
            public int SampleCount { get; set; }
        }

        private sealed class AlarmAggregate
        {
            public int Count { get; set; }
            public double DowntimeMinutes { get; set; }
        }

        private sealed class PlanResult
        {
            public double Plan { get; set; }
            public string Source { get; set; }
            public double? BaselineValue { get; set; }
            public int BaselineSamples { get; set; }
        }

        #endregion
    }
}

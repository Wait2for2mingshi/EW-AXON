using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using EW_Assistant.Domain.Reports;

namespace EW_Assistant.Services.Reports
{
    /// <summary>
    /// 报表调度器：在后台自动补生成基础日报/周报。
    /// </summary>
    public class ReportScheduler
    {
        private static readonly TimeSpan AutoGenerationRetryCooldown = TimeSpan.FromMinutes(30);
        private readonly ReportStorageService _storage;
        private readonly ReportGeneratorService _generator;
        private volatile bool _autoGenerationSuspended;
        private DateTime _autoGenerationSuspendedAt = DateTime.MinValue;
        private string _autoGenerationSuspensionReason;

        public ReportScheduler(ReportStorageService storage = null, ReportGeneratorService generator = null)
        {
            _storage = storage ?? new ReportStorageService();
            _generator = generator ?? new ReportGeneratorService(_storage, new LlmWorkflowClient());
        }

        /// <summary>
        /// 当前是否因报表 Workflow 鉴权或网络发送异常而暂停自动补齐。
        /// </summary>
        public bool IsAutoGenerationSuspended
        {
            get { return _autoGenerationSuspended; }
        }

        public string AutoGenerationSuspensionReason
        {
            get { return _autoGenerationSuspensionReason; }
        }

        /// <summary>
        /// 配置更新后重置自动补齐暂停状态，允许下一轮重新尝试。
        /// </summary>
        public void ResetAutoGenerationSuspension()
        {
            if (!_autoGenerationSuspended)
            {
                return;
            }

            _autoGenerationSuspended = false;
            _autoGenerationSuspendedAt = DateTime.MinValue;
            _autoGenerationSuspensionReason = null;
            Log("报表模块状态已重置，下一轮自动补齐将重新尝试。", "info");
        }

        /// <summary>
        /// 确保当天的两份日报与上一自然周的两份周报已生成，异常仅记录不抛出。
        /// </summary>
        public async Task EnsureBasicReportsAsync(CancellationToken token = default(CancellationToken))
        {
            if (_autoGenerationSuspended)
            {
                if (DateTime.Now - _autoGenerationSuspendedAt < AutoGenerationRetryCooldown)
                {
                    return;
                }

                _autoGenerationSuspended = false;
                _autoGenerationSuspendedAt = DateTime.MinValue;
                _autoGenerationSuspensionReason = null;
                Log("报表模块暂停冷却已结束，开始自动重试。", "info");
            }

            var today = DateTime.Today;
            var weekStart = GetPreviousWeekStart(today);
            var weekEnd = weekStart.AddDays(6);

            // 逐日补齐近 3 天（含今天）
            for (int i = 2; i >= 0; i--)
            {
                var date = today.AddDays(-i);
                if (date >= today)
                {
                    continue; // 当天尚未结束，跳过
                }

                if (!await EnsureDailyAsync(ReportType.DailyProd, date, token).ConfigureAwait(false))
                {
                    return;
                }

                if (!await EnsureDailyAsync(ReportType.DailyAlarm, date, token).ConfigureAwait(false))
                {
                    return;
                }
            }

            if (!await EnsureWeeklyAsync(ReportType.WeeklyProd, weekStart, weekEnd, token).ConfigureAwait(false))
            {
                return;
            }

            await EnsureWeeklyAsync(ReportType.WeeklyAlarm, weekStart, weekEnd, token).ConfigureAwait(false);
        }

        private async Task<bool> EnsureDailyAsync(ReportType type, DateTime date, CancellationToken token)
        {
            if (_autoGenerationSuspended)
            {
                return false;
            }

            if (_storage.DailyReportExists(type, date))
            {
                return true;
            }

            try
            {
                if (type == ReportType.DailyProd)
                {
                    await _generator.GenerateDailyProdAsync(date, token).ConfigureAwait(false);
                }
                else if (type == ReportType.DailyAlarm)
                {
                    await _generator.GenerateDailyAlarmAsync(date, token).ConfigureAwait(false);
                }

                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (TrySuspendAutoGeneration(type, date, null, ex))
                {
                    return false;
                }

                Log("自动生成日报失败：" + ex.Message, "warn");
                return true;
            }
        }

        private async Task<bool> EnsureWeeklyAsync(ReportType type, DateTime startDate, DateTime endDate, CancellationToken token)
        {
            if (_autoGenerationSuspended)
            {
                return false;
            }

            if (_storage.WeeklyReportExists(type, endDate))
            {
                return true;
            }

            var rangeText = string.Format("{0:yyyy-MM-dd}~{1:yyyy-MM-dd}", startDate, endDate);

            try
            {
                if (type == ReportType.WeeklyProd)
                {
                    await _generator.GenerateWeeklyProdAsync(endDate, token).ConfigureAwait(false);
                }
                else if (type == ReportType.WeeklyAlarm)
                {
                    await _generator.GenerateWeeklyAlarmAsync(endDate, token).ConfigureAwait(false);
                }

                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (TrySuspendAutoGeneration(type, startDate, endDate, ex))
                {
                    return false;
                }

                Log("自动生成周报失败（" + rangeText + "）： " + ex.Message, "warn");
                return true;
            }
        }

        private bool TrySuspendAutoGeneration(ReportType type, DateTime startDate, DateTime? endDate, Exception ex)
        {
            var reason = BuildAutoGenerationSuspensionReason(type, startDate, endDate, ex);
            if (string.IsNullOrWhiteSpace(reason))
            {
                return false;
            }

            _autoGenerationSuspended = true;
            _autoGenerationSuspendedAt = DateTime.Now;
            _autoGenerationSuspensionReason = reason;
            Log(reason, "warn");
            return true;
        }

        private string BuildAutoGenerationSuspensionReason(ReportType type, DateTime startDate, DateTime? endDate, Exception ex)
        {
            var displayName = _storage.GetTypeDisplayName(type);
            var scope = endDate.HasValue
                ? string.Format("{0} {1:yyyy-MM-dd}~{2:yyyy-MM-dd}", displayName, startDate, endDate.Value)
                : string.Format("{0} {1:yyyy-MM-dd}", displayName, startDate);

            if (IsWorkflowUnauthorized(ex))
            {
                return "检测到报表模块未就绪，已停止后续自动生成。命中任务：" + scope + "；原因：Workflow 返回 HTTP 401（Access token is invalid / unauthorized）。修正 URL 或 ReportKey 并重新保存配置后会立即恢复；若未人工处理，系统将在 30 分钟后自动重试。";
            }

            var transportMessage = ReportGeneratorService.GetWorkflowTransportFailureMessage(ex);
            if (!string.IsNullOrWhiteSpace(transportMessage))
            {
                return "检测到报表 Workflow 网络发送异常，已停止本轮后续自动生成。命中任务：" + scope + "；原因：" + transportMessage + "。系统将在 30 分钟后自动重试，也可重新保存配置后立即恢复。";
            }

            return null;
        }

        private static bool IsWorkflowUnauthorized(Exception ex)
        {
            var current = ex;
            while (current != null)
            {
                var message = current.Message ?? string.Empty;
                if (message.IndexOf("HTTP 401", StringComparison.OrdinalIgnoreCase) >= 0
                    && (message.IndexOf("Access token is invalid", StringComparison.OrdinalIgnoreCase) >= 0
                        || message.IndexOf("\"code\":\"unauthorized\"", StringComparison.OrdinalIgnoreCase) >= 0
                        || message.IndexOf("unauthorized", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    return true;
                }

                current = current.InnerException;
            }

            return false;
        }

        private DateTime GetPreviousWeekStart(DateTime referenceDate)
        {
            // 以周一为自然周起始
            var day = referenceDate.Date;
            int diff = (int)day.DayOfWeek - (int)DayOfWeek.Monday;
            if (diff < 0) diff += 7;
            var currentWeekStart = day.AddDays(-diff);
            return currentWeekStart.AddDays(-7);
        }

        private void Log(string message, string level)
        {
            try
            {
                MainWindow.PostProgramInfo(message, level);
            }
            catch
            {
                Debug.WriteLine("[ReportScheduler] " + message);
            }
        }
    }
}

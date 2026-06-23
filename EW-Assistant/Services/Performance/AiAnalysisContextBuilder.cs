using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace EW_Assistant.Services
{
    public sealed class AiAnalysisContextBuilder
    {
        public AiAnalysisContext Build(CpuSnapshot latestSnapshot, float averageCpuUsage5Min, IEnumerable<PerformanceEvent> recentEvents)
        {
            var snapshot = latestSnapshot ?? new CpuSnapshot { Timestamp = DateTime.Now };
            if (snapshot.Timestamp == default)
                snapshot.Timestamp = DateTime.Now;

            var topProcesses = snapshot.TopProcesses ?? new List<ProcessSnapshot>();
            var eventsList = recentEvents == null
                ? new List<PerformanceEvent>()
                : recentEvents.Where(e => e != null).ToList();

            return new AiAnalysisContext
            {
                Timestamp = snapshot.Timestamp,
                CurrentCpuUsage = snapshot.TotalCpuUsage,
                AverageCpuUsage5Min = averageCpuUsage5Min,
                TopProcessSummary = BuildTopProcessSummary(topProcesses),
                EventSummary = BuildEventSummary(eventsList),
                HistoricalComparison = BuildHistoricalComparison(snapshot.TotalCpuUsage, averageCpuUsage5Min),
                TopProcesses = new List<ProcessSnapshot>(topProcesses),
                RecentEvents = eventsList
            };
        }

        private static string BuildTopProcessSummary(IReadOnlyList<ProcessSnapshot> processes)
        {
            if (processes == null || processes.Count == 0)
                return "未获取到 Top 进程数据。";

            var sb = new StringBuilder();
            sb.Append("Top 进程：");
            var count = Math.Min(5, processes.Count);
            for (int i = 0; i < count; i++)
            {
                var proc = processes[i];
                if (proc == null)
                    continue;

                if (sb.Length > 0 && sb[sb.Length - 1] != '：')
                    sb.Append("；");

                var name = string.IsNullOrWhiteSpace(proc.Name) ? "未知进程" : proc.Name;
                sb.Append(name);
                sb.Append(" (PID=");
                sb.Append(proc.Pid.ToString(CultureInfo.InvariantCulture));
                sb.Append(") CPU ");
                sb.Append(proc.CpuUsage.ToString("F1", CultureInfo.InvariantCulture));
                sb.Append("%，内存 ");
                sb.Append(proc.MemoryMb.ToString(CultureInfo.InvariantCulture));
                sb.Append("MB，运行 ");
                sb.Append(FormatUptime(proc.Uptime));
            }

            return sb.ToString();
        }

        private static string BuildEventSummary(IReadOnlyList<PerformanceEvent> eventsList)
        {
            if (eventsList == null || eventsList.Count == 0)
                return "最近未触发性能规则。";

            var sb = new StringBuilder();
            sb.Append("最近触发 ");
            sb.Append(eventsList.Count.ToString(CultureInfo.InvariantCulture));
            sb.Append(" 条规则：");

            for (int i = 0; i < eventsList.Count; i++)
            {
                var evt = eventsList[i];
                if (evt == null)
                    continue;

                if (i > 0)
                    sb.Append("；");

                if (!string.IsNullOrWhiteSpace(evt.Description))
                {
                    sb.Append(evt.Description.Trim());
                }
                else if (!string.IsNullOrWhiteSpace(evt.EventType))
                {
                    sb.Append(evt.EventType.Trim());
                }
                else
                {
                    sb.Append("未知规则");
                }
            }

            return sb.ToString();
        }

        private static string BuildHistoricalComparison(float currentCpu, float averageCpu)
        {
            var delta = currentCpu - averageCpu;
            var direction = "持平";
            if (delta > 0.01f)
                direction = "高于";
            else if (delta < -0.01f)
                direction = "低于";

            var absDelta = Math.Abs(delta);
            return string.Format(CultureInfo.InvariantCulture,
                "当前 CPU {0:F1}%，近 5 分钟均值 {1:F1}%，{2}均值 {3:F1} 个百分点。",
                currentCpu, averageCpu, direction, absDelta);
        }

        private static string FormatUptime(TimeSpan uptime)
        {
            if (uptime < TimeSpan.Zero)
                return "00:00:00";

            if (uptime.TotalDays >= 1)
                return uptime.ToString("dd\\.hh\\:mm\\:ss", CultureInfo.InvariantCulture);

            return uptime.ToString("hh\\:mm\\:ss", CultureInfo.InvariantCulture);
        }
    }
}

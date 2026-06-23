namespace EW_Assistant.Domain.Reports
{
    public static class ReportTypeExtensions
    {
        public static bool IsDaily(this ReportType type)
        {
            return type == ReportType.DailyProd || type == ReportType.DailyAlarm;
        }

        public static bool IsWeekly(this ReportType type)
        {
            return type == ReportType.WeeklyProd || type == ReportType.WeeklyAlarm;
        }
    }
}

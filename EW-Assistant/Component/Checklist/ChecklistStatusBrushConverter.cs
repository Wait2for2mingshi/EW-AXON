using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace EW_Assistant.Component.Checklist
{
    /// <summary>根据 Checklist 状态返回对应的背景或边框画刷。</summary>
    public sealed class ChecklistStatusBrushConverter : IValueConverter
    {
        private static readonly SolidColorBrush PendingBackground = CreateFrozenBrush(Color.FromRgb(243, 244, 246));
        private static readonly SolidColorBrush PendingBorder = CreateFrozenBrush(Color.FromRgb(203, 213, 225));
        private static readonly SolidColorBrush DoneBackground = CreateFrozenBrush(Color.FromRgb(222, 247, 236));
        private static readonly SolidColorBrush DoneBorder = CreateFrozenBrush(Color.FromRgb(49, 196, 141));
        private static readonly SolidColorBrush AbnormalBackground = CreateFrozenBrush(Color.FromRgb(255, 236, 232));
        private static readonly SolidColorBrush AbnormalBorder = CreateFrozenBrush(Color.FromRgb(244, 63, 94));

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var status = ChecklistItemStatus.Pending;
            if (value is ChecklistItemStatus)
                status = (ChecklistItemStatus)value;

            var forBorder = parameter != null &&
                            string.Equals(parameter.ToString(), "border", StringComparison.OrdinalIgnoreCase);

            if (forBorder)
                return GetBorderBrush(status);
            return GetBackgroundBrush(status);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }

        private static Brush GetBackgroundBrush(ChecklistItemStatus status)
        {
            switch (status)
            {
                case ChecklistItemStatus.Done:
                    return DoneBackground;
                case ChecklistItemStatus.Abnormal:
                    return AbnormalBackground;
                default:
                    return PendingBackground;
            }
        }

        private static Brush GetBorderBrush(ChecklistItemStatus status)
        {
            switch (status)
            {
                case ChecklistItemStatus.Done:
                    return DoneBorder;
                case ChecklistItemStatus.Abnormal:
                    return AbnormalBorder;
                default:
                    return PendingBorder;
            }
        }

        private static SolidColorBrush CreateFrozenBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}

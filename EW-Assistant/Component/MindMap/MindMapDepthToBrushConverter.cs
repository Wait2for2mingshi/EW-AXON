using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace EW_Assistant.Component.MindMap
{
    public sealed class MindMapDepthToBrushConverter : IValueConverter
    {
        private static readonly Color[][] Palette =
        {
            new [] { ColorFromHex("#C9D6FF"), ColorFromHex("#E2E9FF"), ColorFromHex("#9DB8FF") },
            new [] { ColorFromHex("#B3E5FC"), ColorFromHex("#E0F7FA"), ColorFromHex("#7FC4DA") },
            new [] { ColorFromHex("#C8FACC"), ColorFromHex("#E6FFEC"), ColorFromHex("#8AD496") },
            new [] { ColorFromHex("#FFE29A"), ColorFromHex("#FFF7D6"), ColorFromHex("#F4C96A") }
        };

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            int depth = 0;
            if (value is MindMapVisualNode node) depth = node.Depth;
            else if (value is int intDepth) depth = intDepth;

            var colors = Palette[Math.Min(depth, Palette.Length - 1)];
            var mode = parameter as string;

            if (string.Equals(mode, "edge", StringComparison.OrdinalIgnoreCase))
            {
                return new SolidColorBrush(colors[2]);
            }

            var brush = new LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0, 0),
                EndPoint = new System.Windows.Point(0, 1)
            };
            brush.GradientStops.Add(new GradientStop(colors[0], 0));
            brush.GradientStops.Add(new GradientStop(colors[1], 1));
            return brush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();

        private static Color ColorFromHex(string hex)
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }
    }
}

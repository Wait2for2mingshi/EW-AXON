using System.Collections;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace EW_Assistant.Services
{
    internal static class MarkdownFontHelper
    {
        private static readonly FontFamily AppFontFamily = new FontFamily(UiFontService.AppFontFamily);

        public static void ApplyAppFont(FlowDocument document, double? fontSize = null)
        {
            if (document == null)
            {
                return;
            }

            ApplyFont(document, fontSize);
        }

        private static void ApplyFont(object node, double? fontSize)
        {
            if (node is FlowDocument document)
            {
                document.FontFamily = AppFontFamily;
                if (fontSize.HasValue)
                {
                    document.FontSize = fontSize.Value;
                }
            }

            if (node is TextElement textElement)
            {
                textElement.FontFamily = AppFontFamily;
                if (fontSize.HasValue)
                {
                    textElement.FontSize = fontSize.Value;
                }
            }

            if (node is DependencyObject dependencyObject)
            {
                foreach (var child in LogicalTreeHelper.GetChildren(dependencyObject))
                {
                    ApplyFont(child, fontSize);
                }

                return;
            }

            if (node is IEnumerable children && !(node is string))
            {
                foreach (var child in children)
                {
                    ApplyFont(child, fontSize);
                }
            }
        }
    }
}

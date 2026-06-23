using SkiaSharp;
using System;
using System.Windows;

namespace EW_Assistant.Services
{
    public static class UiFontService
    {
        public const string AppFontFamily =
            "pack://application:,,,/Resources/Fonts/#Source Han Sans SC, Source Han Sans SC, Source Han Sans CN, Source Han Sans, Noto Sans CJK SC, Microsoft YaHei UI, Segoe UI";

        private static readonly Uri SourceHanSansResourceUri =
            new Uri("pack://application:,,,/Resources/Fonts/SourceHanSansSC-Regular.otf", UriKind.Absolute);

        public static SKTypeface CreateSkiaTypeface(string sampleText)
        {
            var bundled = TryCreateBundledSkiaTypeface(sampleText);
            if (bundled != null)
            {
                return bundled;
            }

            string[] candidates =
            {
                "Source Han Sans SC",
                "Source Han Sans CN",
                "Source Han Sans",
                "Noto Sans CJK SC",
                "Noto Sans SC",
                "Microsoft YaHei UI",
                "Microsoft YaHei",
                "SimHei",
                "SimSun",
                "PingFang SC",
                "Segoe UI"
            };

            foreach (var family in candidates)
            {
                try
                {
                    var typeface = SKTypeface.FromFamilyName(family);
                    if (typeface == null)
                    {
                        continue;
                    }

                    using var paint = new SKPaint { Typeface = typeface, TextSize = 12, IsAntialias = true };
                    if (string.IsNullOrWhiteSpace(sampleText) || paint.ContainsGlyphs(sampleText))
                    {
                        return typeface;
                    }

                    typeface.Dispose();
                }
                catch
                {
                    // 继续尝试下一个字体。
                }
            }

            return SKTypeface.Default;
        }

        private static SKTypeface TryCreateBundledSkiaTypeface(string sampleText)
        {
            try
            {
                var info = Application.GetResourceStream(SourceHanSansResourceUri);
                if (info == null)
                {
                    return null;
                }

                using (info.Stream)
                {
                    var typeface = SKTypeface.FromStream(info.Stream);
                    if (typeface == null)
                    {
                        return null;
                    }

                    using var paint = new SKPaint { Typeface = typeface, TextSize = 12, IsAntialias = true };
                    if (string.IsNullOrWhiteSpace(sampleText) || paint.ContainsGlyphs(sampleText))
                    {
                        return typeface;
                    }

                    typeface.Dispose();
                }
            }
            catch
            {
                // 内置字体不可读时回退系统字体。
            }

            return null;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Drawing;

namespace VT9Status
{
    // One ground (the frosted quire), one ink, one accent. Groups are separated by tone
    // steps and space rather than rules, shadows or gradients.
    internal sealed class Theme
    {
        internal static readonly Color Ink = Color.FromArgb(0x11, 0x18, 0x27);
        internal static readonly Color InkSoft = Color.FromArgb(0x4B, 0x52, 0x5D);
        internal static readonly Color Muted = Color.FromArgb(0x64, 0x74, 0x8B);
        internal static readonly Color Faint = Color.FromArgb(0x8A, 0x94, 0xA6);
        internal static readonly Color Accent = Color.FromArgb(0x30, 0x4F, 0xFE);
        internal static readonly Color AccentDeep = Color.FromArgb(0x22, 0x37, 0x9B);
        internal static readonly Color AccentPressed = Color.FromArgb(0x2A, 0x45, 0xE0);
        internal static readonly Color Low = Color.FromArgb(0xC8, 0x64, 0x3A);
        internal static readonly Color Surface = Color.FromArgb(0xFF, 0xFF, 0xFF);
        internal static readonly Color ChipBg = Color.FromArgb(0xED, 0xF2, 0xF7);
        internal static readonly Color ChipHover = Color.FromArgb(0xE4, 0xEA, 0xF2);
        internal static readonly Color GroupBg = Color.FromArgb(0xF8, 0xFA, 0xFC);
        internal static readonly Color DisabledInk = Color.FromArgb(0x94, 0xA3, 0xB8);
        internal static readonly Color Track = Color.FromArgb(0xE2, 0xE8, 0xF0);
        internal static readonly Color SwitchOff = Color.FromArgb(0xCB, 0xD5, 0xE1);
        internal static readonly Color FooterBtn = Color.FromArgb(0xE2, 0xE8, 0xF0);
        internal static readonly Color FooterInk = Color.FromArgb(0x33, 0x41, 0x55);
        internal static readonly Color CloseHover = Color.FromArgb(0xD6, 0x4A, 0x3E);
        internal static readonly Color Hairline = Color.FromArgb(0xE2, 0xE8, 0xF0);
        // 概览页产品图区（设计稿里是直接写死的一组浅蓝）
        internal static readonly Color ProductBg = Color.FromArgb(0xEE, 0xF2, 0xFB);
        internal static readonly Color ProductLine = Color.FromArgb(0xC9, 0xCF, 0xF7);
        // dump 摘要与外观保存提示的中性正文色（比 Muted 更深一档）
        internal static readonly Color Note = Color.FromArgb(0x55, 0x60, 0x5A);
        // Acrylic 不可用时的整窗实色回退（取自设计稿渐变中段）
        internal static readonly Color WindowFallback = Color.FromArgb(0xED, 0xF1, 0xF6);

        // 玻璃质感：底座 / 胶囊的渐变端点。集中在这里才能保证"改一处颜色，全局一致"。
        internal static readonly Color GlassFillTop = Color.FromArgb(217, 255, 255, 255);
        internal static readonly Color GlassFillMid = Color.FromArgb(128, 255, 255, 255);
        internal static readonly Color GlassFillBottom = Color.FromArgb(191, 255, 255, 255);
        internal static readonly Color GlassStrokeTop = Color.FromArgb(242, 255, 255, 255);
        internal static readonly Color GlassStrokeBottom = Color.FromArgb(102, 255, 255, 255);
        internal static readonly Color CapsuleFillTop = Color.FromArgb(230, 0xCF, 0xD8, 0xF9);
        internal static readonly Color CapsuleFillBottom = Color.FromArgb(204, 0xBB, 0xC8, 0xF5);
        internal static readonly Color CapsuleEdge = Color.FromArgb(0x9E, 0xB1, 0xF2);
        internal static readonly Color CapsuleShadow = Color.FromArgb(0x3C, 0x52, 0xA3);
        internal static readonly Color DockShadow = Color.FromArgb(0x7E, 0x8D, 0xA3);

        // 卡片 / 外壳的 1px 高光描边（设计稿的 card-soft-shadow 降级后的唯一"体积"线索）
        internal static readonly Color CardStroke = Color.FromArgb(166, 255, 255, 255);
        internal static readonly Color WindowStroke = Color.FromArgb(191, 255, 255, 255);

        internal const string HexInk = "#111827";
        internal const string HexInkSoft = "#4B525D";
        internal const string HexMuted = "#64748B";
        internal const string HexFaint = "#8A94A6";
        internal const string HexAccent = "#304FFE";
        internal const string HexAccentDeep = "#22379B";
        internal const string HexAccentPressed = "#2A45E0";
        internal const string HexLow = "#C8643A";
        internal const string HexSurface = "#FFFFFF";
        internal const string HexChipBg = "#EDF2F7";
        internal const string HexChipHover = "#E4EAF2";
        internal const string HexGroupBg = "#F8FAFC";
        internal const string HexSliderAccent = "#2563EB";
        internal const string HexTrack = "#E2E8F0";
        internal const string HexSwitchOff = "#CBD5E1";
        internal const string HexFooterBtn = "#E2E8F0";
        internal const string HexFooterInk = "#334155";
        internal const string HexCloseHover = "#D64A3E";
        internal const string HexHairline = "#E2E8F0";
        internal const string HexCardStroke = "#A6FFFFFF";
        internal const string HexWindowStroke = "#BFFFFFFF";
        internal const string HexWhiteHighlight = "#BFFFFFFF";

        internal static System.Windows.Media.Color Media(Color c)
        {
            return System.Windows.Media.Color.FromArgb(c.A, c.R, c.G, c.B);
        }

        internal static System.Windows.Media.SolidColorBrush Brush(Color c)
        {
            System.Windows.Media.SolidColorBrush brush = new System.Windows.Media.SolidColorBrush(Media(c));
            brush.Freeze();
            return brush;
        }

        internal static System.Windows.Media.SolidColorBrush NewBrush(Color c)
        {
            return new System.Windows.Media.SolidColorBrush(Media(c));
        }

        internal readonly float Scale;
        private readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();

        internal Theme(float scale)
        {
            Scale = scale <= 0 ? 1F : scale;
        }

        internal int S(int value)
        {
            return (int)Math.Round(value * Scale);
        }

        internal float S(float value)
        {
            return value * Scale;
        }

        // Pixel-sized fonts so that everything is measured in the same logical unit as
        // the hand-computed layout.
        // 像素字号，和 Fonts 里那套命名尺寸同一单位。
        internal Font Font(float pixelSize, FontStyle style)
        {
            return Face(Fonts.Family, pixelSize, style);
        }

        // 以前数字另用 Segoe UI，和界面中文字形不一致；现在与正文同族，
        // 需要强调数字时用 FontWeights/字重而不是换字体。
        internal Font Numerals(float pixelSize, FontStyle style)
        {
            return Face(Fonts.Family, pixelSize, style);
        }

        private Font Face(string family, float pixelSize, FontStyle style)
        {
            string key = family + "|" + pixelSize.ToString("0.##") + "|" + (int)style;
            Font font;
            if (!fonts.TryGetValue(key, out font))
            {
                font = new Font(family, pixelSize * Scale, style, GraphicsUnit.Pixel);
                fonts[key] = font;
            }
            return font;
        }

        internal void Dispose()
        {
            foreach (Font font in fonts.Values) font.Dispose();
            fonts.Clear();
        }
    }
}

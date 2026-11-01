using System.Drawing;

namespace VT9Status
{
    // Central font sizes in device-independent pixels.
    internal static class Fonts
    {
        // Tray/GDI retains its Chinese face; WPF uses Segoe UI with Chinese fallback.
        internal const string Family = "Microsoft YaHei UI";
        internal static readonly System.Windows.Media.FontFamily Wpf = new System.Windows.Media.FontFamily("Segoe UI, Microsoft YaHei UI");

        // 字号（像素）。WPF 的 FontSize 是设备无关像素，GDI 这边统一用 GraphicsUnit.Pixel，
        // 两条路径的数字因此可以直接互相核对。
        internal const double Meta = 12;     // 滑块刻度（本次只剩参数页刻度，DPI/休眠不再画）
        internal const double Sub = 13.5;    // 卡片说明、页脚、状态、右侧 meta
        internal const double Chip = 13.5;   // pill / chip 文字
        internal const double SwitchLabel = 15;
        internal const double Body = 14.5;   // 行标签、开关标签、大按钮文字
        internal const double Step = 16;     // ± 微调按钮
        internal const double NavLensScale = 1.20;
        internal const double Nav = 18;      // Dock 标签（本次最显眼的一处放大）
        internal const double Label = 18;    // 卡片标题
        internal const double Heading = 18;  // 外观设置浮层标题
        internal const double Title = 20;    // 窗口标题
        // 卡片读数：15px 配 SemiBold，精炼紧凑，与行标签比例协调，呼吸感充足，不压迫右侧边界。
        internal const double Value = 15;    // 卡片读数（概览页参数卡、休眠时长、档位值）
        internal const double ValueHero = 15;// 核心指标大读数（如设备电量 100%）
        internal const double Display = 40;  // DPI 页当前档位大数字

        // ── GDI（托盘菜单、图标预览图）──
        // GDI 用 GraphicsUnit.Pixel 而不是点，避免 9pt/9.5pt 这类换算后与界面差一两像素。
        internal static Font Gdi(double pixelSize, FontStyle style)
        {
            return new Font(Family, (float)pixelSize, style, GraphicsUnit.Pixel);
        }

        internal static readonly FontStyle Regular = FontStyle.Regular;
        internal static readonly FontStyle Bold = FontStyle.Bold;
    }
}

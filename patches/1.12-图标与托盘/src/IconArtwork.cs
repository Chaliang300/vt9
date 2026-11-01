using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace VT9Status
{
    internal enum TrayState
    {
        Normal,
        Low,
        Charging,
        Offline,
        Write
    }

    // Vector artwork for the executable icon and the notification-area icon.
    // The path data below is copied verbatim from 图标系统.svg and parsed at run time, so the
    // program and the design file describe the same curves instead of two look-alikes.
    // Nothing is rasterised from an export, so every DPI stays crisp.
    internal static class IconArtwork
    {
        private const float AppUnits = 256F;
        private const float TrayUnits = 16F;                 // 导出样例以 16px 为准，即 24u = 16px
        private const float UnitPixels = TrayUnits / 24F;    // 1u = 0.66667px @16px

        // 应用图标：左右两半鼠标，中缝 1.3u；原点是图标左上角，整体下移到 y=48.8333
        private const string LeftHalf = "M64.5833 0C28.75 0 0 31.6667 0 74.1667L0 90.8333C0 129.1667 31.6667 157.5 64.5833 157.5L64.5833 0Z";
        private const string RightHalf = "M0 0C35.8333 0 64.5833 31.6667 64.5833 74.1667L64.5833 90.8333C64.5833 129.1667 32.9167 157.5 0 157.5L0 0Z";

        // 托盘：外轮廓与内轮廓，构成 2.2u 的环
        private const string TrayOuter = "M9.9333 5.6667L9.9333 8.4667Q9.9333 10.4884 8.301 11.7616Q6.7988 12.9333 4.6 12.9333Q2.4012 12.9333 0.899 11.7616Q-0.7333 10.4884 -0.7333 8.4667L-0.7333 5.6667Q-0.7333 3.0061 0.7556 1.1713Q2.3011 -0.7333 4.6 -0.7333Q6.8989 -0.7333 8.4444 1.1713Q9.9333 3.0061 9.9333 5.6667Z";
        private const string TrayInner = "M8.4667 5.6667L8.4667 8.4667Q8.4667 9.7723 7.399 10.6051Q6.2944 11.4667 4.6 11.4667Q2.9056 11.4667 1.801 10.6051Q0.7333 9.7723 0.7333 8.4667L0.7333 5.6667Q0.7333 3.5263 1.8944 2.0954Q2.9997 0.7333 4.6 0.7333Q6.2003 0.7333 7.3056 2.0954Q8.4667 3.5263 8.4667 5.6667Z";
        private const string BoltPath = "M2.8667 0L0 4.6667L1.7333 4.6667L1 7.7333L3.8667 3.0667L2.1333 3.0667Z";

        private static readonly Color Accent = C(0x3B4BD8);
        private static readonly Color AppLight = C(0x495BEF);
        private static readonly Color AppDark = C(0x2B30AF);
        private static readonly Color Ink = C(0x1C2230);
        private static readonly Color AmberOnDark = C(0xE8A33D);
        private static readonly Color AmberOnLight = C(0xC77C1E);
        private static readonly Color ChargingGreen = C(0x2FA36B);

        private static Color C(int rgb)
        {
            return Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        }

        private static void Smooth(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        }

        // ---- 应用图标 -------------------------------------------------------

        // 超椭圆方形（圆角 = 边长 22.5%），135° 靛蓝渐变，白色鼠标剪影，顶部 1.25u 滚轮点。
        internal static Bitmap App(int size)
        {
            Bitmap bitmap = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent);
                Smooth(g);
                float k = size / AppUnits;
                using (GraphicsPath plate = Rounded(new RectangleF(0F, 0F, size, size), 58F * k))
                using (LinearGradientBrush gradient = new LinearGradientBrush(new RectangleF(0F, 0F, size, size), AppLight, AppDark, 45F))
                {
                    g.FillPath(gradient, plate);
                }
                using (GraphicsPath mouse = new GraphicsPath())
                {
                    mouse.FillMode = FillMode.Winding;
                    mouse.AddPath(Svg(LeftHalf, 58F * k, 48.8333F * k, k), false);
                    mouse.AddPath(Svg(RightHalf, 133.4167F * k, 48.8333F * k, k), false);
                    g.FillPath(Brushes.White, mouse);
                }
                using (GraphicsPath wheel = new GraphicsPath())
                {
                    // 滚轮白点：中心 (128,83)，半径 10.4167（256 单位）
                    wheel.AddEllipse((128F - 10.4167F) * k, (83F - 10.4167F) * k, 20.8333F * k, 20.8333F * k);
                    g.FillPath(Brushes.White, wheel);
                }
            }
            return bitmap;
        }

        // ---- 系统托盘 -------------------------------------------------------

        // 任务栏明暗决定反色，尺寸跟随系统缩放到 16/20/24（更高为 28/32）。
        // 图标是矢量绘制的，放大即清晰，不让外壳拉伸位图。
        internal static Bitmap Tray(int size, TrayState state, int? level, bool lightTaskbar)
        {
            Bitmap bitmap = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent);
                Smooth(g);
                g.ScaleTransform(size / TrayUnits, size / TrayUnits);

                Color ink = TrayColor(state, lightTaskbar);
                if (state == TrayState.Offline) ink = Color.FromArgb(102, ink.R, ink.G, ink.B);   // 已断开：整体 40%

                if (level.HasValue || state == TrayState.Charging)
                {
                    float percent = level.HasValue ? level.Value : 60F;
                    if (percent < 0F) percent = 0F;
                    if (percent > 100F) percent = 100F;
                    if (state == TrayState.Charging && percent < 60F) percent = 60F;
                    // 水位公式 h = 1.8 + 电量% × 0.126（u），底部固定 19.2u
                    float height = (1.8F + 0.126F * percent) * UnitPixels;
                    using (GraphicsPath water = Rounded(new RectangleF(5.2667F, 12.8F - height, 5.4667F, height), Math.Min(2F, height / 2F)))
                    using (SolidBrush brush = new SolidBrush(ink))
                    {
                        g.FillPath(brush, water);
                    }
                }
                using (GraphicsPath ring = new GraphicsPath(FillMode.Alternate))
                using (SolidBrush brush = new SolidBrush(ink))
                {
                    ring.AddPath(Svg(TrayOuter, 3.4F, 1.9333F, 1F), false);
                    ring.AddPath(Svg(TrayInner, 3.4F, 1.9333F, 1F), false);
                    g.FillPath(brush, ring);
                }
                if (state == TrayState.Charging)
                {
                    using (GraphicsPath bolt = Svg(BoltPath, 6.0667F, 4.0667F, 1F))
                    using (SolidBrush brush = new SolidBrush(ink))
                    {
                        g.FillPath(brush, bolt);
                    }
                }
                if (state == TrayState.Offline)
                {
                    using (Pen slash = new Pen(Color.FromArgb(102, ink.R, ink.G, ink.B), 1.5F))
                    {
                        slash.StartCap = LineCap.Round; slash.EndCap = LineCap.Round;
                        g.DrawLine(slash, 3.6F, 12.6F, 12.4F, 3.4F);
                    }
                }
            }
            return bitmap;
        }

        private static Color TrayColor(TrayState state, bool lightTaskbar)
        {
            switch (state)
            {
                case TrayState.Write: return Accent;
                case TrayState.Charging: return ChargingGreen;
                case TrayState.Low: return lightTaskbar ? AmberOnLight : AmberOnDark;
                default: return lightTaskbar ? Ink : Color.White;
            }
        }

        // 托盘图标像素尺寸：100%/125%/150% 对应 16/20/24，更高缩放给 28/32。
        internal static int TraySize()
        {
            int dpi = 96;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey("Control Panel\\Desktop\\WindowMetrics"))
                {
                    if (key != null)
                    {
                        object value = key.GetValue("AppliedDPI");
                        if (value is int && (int)value >= 96) dpi = (int)value;
                    }
                }
            }
            catch (Exception) { dpi = 96; }
            int size = (int)Math.Round(TrayUnits * dpi / 96.0);
            if (size < 16) size = 16;
            if (size > 32) size = 32;
            return size;
        }

        internal static bool LightTaskbar()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize"))
                {
                    object value = key == null ? null : key.GetValue("SystemUsesLightTheme");
                    return value is int && (int)value == 1;
                }
            }
            catch (Exception) { return false; }
        }

        private static GraphicsPath Rounded(RectangleF bounds, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float d = radius * 2F;
            if (d <= 0F) { path.AddRectangle(bounds); return path; }
            if (d > bounds.Width) d = bounds.Width;
            if (d > bounds.Height) d = bounds.Height;
            path.AddArc(bounds.X, bounds.Y, d, d, 180F, 90F);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270F, 90F);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0F, 90F);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90F, 90F);
            path.CloseFigure();
            return path;
        }

        // ---- SVG 路径 -------------------------------------------------------

        // 只支持本设计文件用到的绝对指令 M/L/Q/C/Z。Q 用 2/3 法则精确转成三次贝塞尔，
        // 因此曲线与 图标系统.svg 完全一致；调用方给的是偏移和缩放（设计单位 → 像素）。
        private static GraphicsPath Svg(string data, float offsetX, float offsetY, float scale)
        {
            GraphicsPath path = new GraphicsPath(FillMode.Winding);
            List<string> tokens = new List<string>();
            StringBuilder token = new StringBuilder();
            foreach (char ch in data)
            {
                if (char.IsLetter(ch))
                {
                    if (token.Length > 0) { tokens.Add(token.ToString()); token.Length = 0; }
                    tokens.Add(ch.ToString());
                }
                else if (ch == ' ' || ch == ',' || ch == '\t' || ch == '\r' || ch == '\n')
                {
                    if (token.Length > 0) { tokens.Add(token.ToString()); token.Length = 0; }
                }
                else token.Append(ch);
            }
            if (token.Length > 0) tokens.Add(token.ToString());

            char mode = 'M';
            PointF point = PointF.Empty;
            int i = 0;
            while (i < tokens.Count)
            {
                string currentToken = tokens[i];
                if (currentToken.Length == 1 && char.IsLetter(currentToken[0]))
                {
                    mode = char.ToUpperInvariant(currentToken[0]);
                    i++;
                    if (mode == 'Z') { path.CloseFigure(); continue; }
                    continue;
                }
                switch (mode)
                {
                    case 'M':
                    {
                        PointF to = At(tokens, i, offsetX, offsetY, scale);
                        path.StartFigure(); point = to; i += 2;
                        // 后续隐式 L
                        mode = 'L';
                        break;
                    }
                    case 'L':
                    {
                        PointF to = At(tokens, i, offsetX, offsetY, scale);
                        path.AddLine(point, to); point = to; i += 2;
                        break;
                    }
                    case 'Q':
                    {
                        PointF control = At(tokens, i, offsetX, offsetY, scale);
                        PointF to = At(tokens, i + 2, offsetX, offsetY, scale);
                        PointF c1 = new PointF(point.X + (control.X - point.X) * 2F / 3F, point.Y + (control.Y - point.Y) * 2F / 3F);
                        PointF c2 = new PointF(to.X + (control.X - to.X) * 2F / 3F, to.Y + (control.Y - to.Y) * 2F / 3F);
                        path.AddBezier(point, c1, c2, to); point = to; i += 4;
                        break;
                    }
                    case 'C':
                    {
                        PointF c1 = At(tokens, i, offsetX, offsetY, scale);
                        PointF c2 = At(tokens, i + 2, offsetX, offsetY, scale);
                        PointF to = At(tokens, i + 4, offsetX, offsetY, scale);
                        path.AddBezier(point, c1, c2, to); point = to; i += 6;
                        break;
                    }
                    default: i++; break;
                }
            }
            return path;
        }

        private static PointF At(List<string> tokens, int index, float offsetX, float offsetY, float scale)
        {
            float x = float.Parse(tokens[index], System.Globalization.CultureInfo.InvariantCulture);
            float y = float.Parse(tokens[index + 1], System.Globalization.CultureInfo.InvariantCulture);
            return new PointF(offsetX + x * scale, offsetY + y * scale);
        }

        // ---- .ico 导出 ------------------------------------------------------

        // 16–64 用 32 位 DIB（兼容性最好），128/256 用 PNG（Vista 起的标准做法）。
        internal static void ExportIco(string path, int[] sizes)
        {
            List<byte[]> images = new List<byte[]>();
            foreach (int size in sizes)
            {
                using (Bitmap bitmap = App(size))
                {
                    if (size >= 128)
                    {
                        using (MemoryStream png = new MemoryStream())
                        {
                            bitmap.Save(png, ImageFormat.Png);
                            images.Add(png.ToArray());
                        }
                    }
                    else images.Add(Dib(bitmap, size));
                }
            }
            string folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            using (FileStream file = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (BinaryWriter writer = new BinaryWriter(file))
            {
                writer.Write((short)0); writer.Write((short)1); writer.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    int size = sizes[i];
                    writer.Write((byte)(size >= 256 ? 0 : size));
                    writer.Write((byte)(size >= 256 ? 0 : size));
                    writer.Write((byte)0); writer.Write((byte)0);
                    writer.Write((short)1); writer.Write((short)32);
                    writer.Write(images[i].Length); writer.Write(offset);
                    offset += images[i].Length;
                }
                foreach (byte[] image in images) writer.Write(image);
                writer.Flush();
            }
        }

        private static byte[] Dib(Bitmap bitmap, int size)
        {
            int maskStride = ((size + 31) / 32) * 4;
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                writer.Write(40);                      // biSize
                writer.Write(size);                    // biWidth
                writer.Write(size * 2);                // biHeight（XOR + AND）
                writer.Write((short)1);                // biPlanes
                writer.Write((short)32);               // biBitCount
                writer.Write(0); writer.Write(0);      // 压缩方式、图像大小
                writer.Write(0); writer.Write(0);      // 分辨率
                writer.Write(0); writer.Write(0);      // 调色板
                for (int y = size - 1; y >= 0; y--)
                    for (int x = 0; x < size; x++)
                    {
                        Color pixel = bitmap.GetPixel(x, y);
                        writer.Write(pixel.B); writer.Write(pixel.G); writer.Write(pixel.R); writer.Write(pixel.A);
                    }
                writer.Write(new byte[maskStride * size]);
                writer.Flush();
                return stream.ToArray();
            }
        }

        // ---- 预览图 ---------------------------------------------------------

        internal static void Preview(string path)
        {
            const int Width = 960, Height = 1000;
            Color ink = C(0x12181A), muted = C(0x6E7873), faint = C(0x8A9491);
            using (Bitmap sheet = new Bitmap(Width, Height))
            using (Graphics g = Graphics.FromImage(sheet))
            using (Font title = new Font("Microsoft YaHei UI", 15F, FontStyle.Bold))
            using (Font note = new Font("Microsoft YaHei UI", 9.5F))
            using (Font label = new Font("Segoe UI", 8.5F))
            {
                g.Clear(C(0xF7F8FA));
                Smooth(g);
                g.DrawString("雷柏 VT9 控制台 · 应用图标与系统托盘", title, new SolidBrush(ink), 40F, 30F);
                g.DrawString("应用图标七档尺寸；托盘为鼠标轮廓 + 电量水位。曲线与 图标系统.svg 同源。", note,
                    new SolidBrush(muted), 40F, 60F);

                Panel(g, 40, 90, 880, 340, Color.White, C(0xE6EAEC));
                g.DrawString("浅色任务栏 / 浅色桌面", label, new SolidBrush(faint), 64F, 106F);
                Ladder(g, 64, 132, label, faint);
                Strip(g, 64, 308, 832, 104, true, label, faint);

                Panel(g, 40, 448, 880, 340, C(0x1E2027), C(0x343741));
                g.DrawString("深色任务栏 / 深色桌面", label, new SolidBrush(C(0xA6AFB8)), 64F, 464F);
                Ladder(g, 64, 490, label, C(0x8A9491));
                Strip(g, 64, 666, 832, 104, false, label, C(0x8A9491));

                Panel(g, 40, 806, 880, 160, C(0x26272E), C(0x343741));
                g.DrawString("16px 实际像素 ×4（最近邻放大，检查托盘图标在 100% 缩放下是否还成立）", label,
                    new SolidBrush(C(0x8A9491)), 64F, 822F);
                TrayState[] zoom = { TrayState.Normal, TrayState.Normal, TrayState.Low, TrayState.Charging, TrayState.Offline };
                int?[] zoomLevels = { 100, 34, 15, 34, 76 };
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                for (int i = 0; i < zoom.Length; i++)
                {
                    using (Bitmap icon = Tray(16, zoom[i], zoomLevels[i], false))
                        g.DrawImage(icon, new Rectangle(64 + i * 84, 846, 64, 64), 0, 0, 16, 16, GraphicsUnit.Pixel);
                }
                using (Bitmap app = App(16))
                    g.DrawImage(app, new Rectangle(64 + zoom.Length * 84, 846, 64, 64), 0, 0, 16, 16, GraphicsUnit.Pixel);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                sheet.Save(path, ImageFormat.Png);
            }
        }

        private static void Panel(Graphics g, int x, int y, int width, int height, Color fill, Color border)
        {
            using (GraphicsPath path = Rounded(new RectangleF(x, y, width, height), 16F))
            using (SolidBrush brush = new SolidBrush(fill))
            using (Pen pen = new Pen(border))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }
        }

        private static void Ladder(Graphics g, int x, int y, Font label, Color labelColor)
        {
            int[] sizes = { 128, 64, 48, 32, 24, 16 };
            int cursor = x;
            foreach (int size in sizes)
            {
                using (Bitmap icon = App(size))
                    g.DrawImageUnscaled(icon, cursor, y + (128 - size));
                g.DrawString(size + " px", label, new SolidBrush(labelColor), cursor, y + 146);
                cursor += Math.Max(size, 48) + 22;
            }
        }

        private static void Strip(Graphics g, int x, int y, int width, int height, bool light, Font label, Color labelColor)
        {
            int[] sizes = { 16, 20, 24 };
            TrayState[] states = { TrayState.Normal, TrayState.Normal, TrayState.Low, TrayState.Charging, TrayState.Offline, TrayState.Write };
            int?[] levels = { 100, 76, 15, 34, 76, null };
            string[] captions = { "满电 100%", "76%", "≤20%", "充电中", "已断开", "写入瞬间" };

            using (GraphicsPath bar = Rounded(new RectangleF(x, y, width, height), 10F))
            using (SolidBrush fill = new SolidBrush(light ? C(0xF3F4F6) : C(0x26272E)))
                g.FillPath(fill, bar);

            for (int row = 0; row < sizes.Length; row++)
            {
                int size = sizes[row];
                int top = y + 10 + row * 27;
                g.DrawString(size + "px", label, new SolidBrush(labelColor), x + 12F, top + size / 2 - 7);
                for (int i = 0; i < states.Length; i++)
                {
                    int left = x + 60 + i * 108;
                    using (Bitmap icon = Tray(size, states[i], levels[i], light))
                        g.DrawImageUnscaled(icon, left, top + (24 - size) / 2);
                }
            }
            for (int i = 0; i < captions.Length; i++)
                g.DrawString(captions[i], label, new SolidBrush(labelColor), x + 60 + i * 108, y + height - 18);
        }
    }
}

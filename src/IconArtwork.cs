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
    internal enum TrayState { Normal, Wired, Charging, Offline }

    // Application artwork remains unchanged. Tray artwork uses the 24u ring gauge design.
    internal static class IconArtwork
    {
        private const float AppUnits = 256F;
        private const float TrayUnits = 16F;

        // 应用图标：左右两半鼠标，中缝 1.3u；原点是图标左上角，整体下移到 y=48.8333
        private const string LeftHalf = "M64.5833 0C28.75 0 0 31.6667 0 74.1667L0 90.8333C0 129.1667 31.6667 157.5 64.5833 157.5L64.5833 0Z";
        private const string RightHalf = "M0 0C35.8333 0 64.5833 31.6667 64.5833 74.1667L64.5833 90.8333C64.5833 129.1667 32.9167 157.5 0 157.5L0 0Z";

        private static readonly Color AppLight = C(0x495BEF);
        private static readonly Color AppDark = C(0x2B30AF);
        private static readonly Color Ink = C(0x1C2230);
        private static readonly Color AmberOnDark = C(0xE8A33D);
        private static readonly Color AmberOnLight = C(0xC77C1E);
        private static readonly Color CriticalOnDark = C(0xF2685E);
        private static readonly Color CriticalOnLight = C(0xC0392B);
        private static readonly Color ChargingGreen = C(0x2FA36B);
        private static readonly Color ChargingGreenOnLight = C(0x1E8A57);

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

        internal static TrayState ResolveTrayState(bool connected, bool charging, bool wired)
        {
            if (!connected) return TrayState.Offline;
            if (charging) return TrayState.Charging;
            return wired ? TrayState.Wired : TrayState.Normal;
        }

        internal static float TraySweep(int? level)
        {
            return level.HasValue ? 270F * Math.Max(0, Math.Min(100, level.Value)) / 100F : 0;
        }

        internal static Color TrayColor(TrayState state, int? level, bool lightTaskbar)
        {
            if (state == TrayState.Charging) return lightTaskbar ? ChargingGreenOnLight : ChargingGreen;
            if (state != TrayState.Offline && level.HasValue)
            {
                if (level.Value < 20) return lightTaskbar ? CriticalOnLight : CriticalOnDark;
                if (level.Value <= 40) return lightTaskbar ? AmberOnLight : AmberOnDark;
            }
            return lightTaskbar ? Ink : Color.White;
        }

        internal static string TrayKey(int size, TrayState state, int? level, bool lightTaskbar)
        {
            return size + ":" + state + ":" + (state == TrayState.Offline ? "unknown" : level.ToString()) + ":" + lightTaskbar;
        }

        // 270-degree arc, radius 8, width 3.1 in a 24x24 viewBox. Starts at bottom right,
        // progresses counterclockwise and leaves the bottom gap open even at 100%.
        internal static Bitmap Tray(int size, TrayState state, int? level, bool lightTaskbar)
        {
            Bitmap bitmap = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent);
                Smooth(g);
                g.ScaleTransform(size / 24F, size / 24F);
                Color ink = TrayColor(state, level, lightTaskbar);
                // 未连接时整图 40% 不透明度：底环比其它状态更实，保证在浅色任务栏上仍能看出
                // 这里“有一个图标”，只是没有数据；14% 的底环在系统托盘里近乎不可见。
                using (Pen track = new Pen(Color.FromArgb(state == TrayState.Offline ? 102 : lightTaskbar ? 36 : 51, ink), 3.1F))
                {
                    track.StartCap = track.EndCap = LineCap.Round;
                    g.DrawArc(track, 4F, 4F, 16F, 16F, 45F, -270F);
                }
                // 断开 / 尚未收到上报：45° 斜线，与 1.12 的断开态一致。
                if (state == TrayState.Offline)
                {
                    using (Pen slash = new Pen(Color.FromArgb(102, ink), 1.8F))
                    {
                        slash.StartCap = slash.EndCap = LineCap.Round;
                        float reach = 8F * 0.7071F;
                        g.DrawLine(slash, 12F - reach, 12F - reach, 12F + reach, 12F + reach);
                    }
                    return bitmap;
                }
                float sweep = TraySweep(level);
                if (sweep > 0)
                {
                    using (Pen arc = new Pen(ink, 3.1F))
                    {
                        arc.StartCap = arc.EndCap = LineCap.Round;
                        g.DrawArc(arc, 4F, 4F, 16F, 16F, 45F, -sweep);
                    }
                }
                if (state == TrayState.Charging)
                {
                    using (GraphicsPath bolt = Svg("M13.2 7.9L9.5 13.1L11.9 13.1L10.9 16.7L14.7 11.5L12.3 11.5Z", 0, 0, 1))
                    using (SolidBrush fill = new SolidBrush(ink)) g.FillPath(fill, bolt);
                }
                else if (level.HasValue && state == TrayState.Wired)
                {
                    using (SolidBrush fill = new SolidBrush(ink)) g.FillEllipse(fill, 9.4F, 9.4F, 5.2F, 5.2F);
                }
                else if (level.HasValue)
                {
                    using (GraphicsPath signal = Svg("M9.6 11.1Q12 8 14.4 11.1", 0, 0, 1))
                    using (Pen pen = new Pen(ink, lightTaskbar ? 1.5F : 1.6F))
                    using (SolidBrush fill = new SolidBrush(ink))
                    {
                        pen.StartCap = pen.EndCap = LineCap.Round;
                        g.DrawPath(pen, signal);
                        float radius = lightTaskbar ? 1.25F : 1.3F;
                        g.FillEllipse(fill, 12-radius, 14.3F-radius, radius*2, radius*2);
                    }
                }
            }
            return bitmap;
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

        internal static void TrayPreview(string path)
        {
            TrayState[] states = { TrayState.Normal, TrayState.Normal, TrayState.Normal, TrayState.Normal,
                TrayState.Normal, TrayState.Normal, TrayState.Normal, TrayState.Wired, TrayState.Charging, TrayState.Offline };
            int?[] levels = { 0, 12, 20, 40, 41, 73, 100, 73, 34, null };
            string[] labels = { "0%", "12%", "20%", "40%", "41%", "73%", "100%", "USB 73%", "充电 34%", "未知" };
            using (Bitmap sheet = new Bitmap(1000, 456))
            using (Graphics g = Graphics.FromImage(sheet))
            using (Font title = Fonts.Gdi(20, FontStyle.Bold))
            using (Font label = Fonts.Gdi(13.5, FontStyle.Regular))
            using (SolidBrush ink = new SolidBrush(Ink))
            {
                g.Clear(C(0xF7F8FA)); Smooth(g);
                g.DrawString("系统托盘 · 实时电量环", title, ink, 24, 16);
                g.DrawString("每组上方为16px原尺寸，下方为4倍像素细节；环长按真实电量绘制。", label, ink, 24, 49);
                for (int row = 0; row < 2; row++)
                {
                    bool light = row == 1;
                    int top = 80 + row * 184;
                    using (SolidBrush bar = new SolidBrush(light ? C(0xF1F2F4) : C(0x26272E)))
                    using (SolidBrush text = new SolidBrush(light ? Ink : C(0xCCD0DA)))
                    {
                        g.FillRectangle(bar, 16, top, 968, 168);
                        g.DrawString(light ? "浅色任务栏" : "深色任务栏", label, text, 32, top + 10);
                        for (int i = 0; i < states.Length; i++)
                        {
                            int x = 32 + i * 94;
                            using (Bitmap icon = Tray(16, states[i], levels[i], light))
                            {
                                g.DrawImageUnscaled(icon, x + 24, top + 43);
                                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                                g.PixelOffsetMode = PixelOffsetMode.Half;
                                g.DrawImage(icon, new Rectangle(x, top + 74, 64, 64), 0, 0, 16, 16, GraphicsUnit.Pixel);
                                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            }
                            g.DrawString(labels[i], label, text, x, top + 144);
                        }
                    }
                }
                sheet.Save(path, ImageFormat.Png);
            }
        }

        internal static void Preview(string path)
        {
            const int Width = 960, Height = 1000;
            Color ink = C(0x12181A), muted = C(0x6E7873), faint = C(0x8A9491);
            using (Bitmap sheet = new Bitmap(Width, Height))
            using (Graphics g = Graphics.FromImage(sheet))
            using (Font title = Fonts.Gdi(Fonts.Title, FontStyle.Bold))
            using (Font note = Fonts.Gdi(Fonts.Meta, FontStyle.Regular))
            using (Font label = Fonts.Gdi(Fonts.Meta, FontStyle.Regular))
            {
                g.Clear(C(0xF7F8FA));
                Smooth(g);
                g.DrawString("雷柏 VT9 控制台 · 应用图标与系统托盘", title, new SolidBrush(ink), 40F, 30F);
                g.DrawString("应用图标七档尺寸；托盘为实时电量环；环长=电量，环心区分无线、有线和充电。", note,
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
                TrayState[] zoom = { TrayState.Normal, TrayState.Normal, TrayState.Normal, TrayState.Wired, TrayState.Charging, TrayState.Offline };
                int?[] zoomLevels = { 73, 35, 12, 100, 34, null };
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
            TrayState[] states = { TrayState.Normal, TrayState.Normal, TrayState.Normal, TrayState.Normal, TrayState.Wired, TrayState.Charging, TrayState.Offline };
            int?[] levels = { 100, 73, 35, 12, 100, 34, null };
            string[] captions = { "100%", "73%", "35% 琥珀", "12% 红", "USB", "充电 34%", "未知 / 断开" };

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
                    int left = x + 60 + i * 102;
                    using (Bitmap icon = Tray(size, states[i], levels[i], light))
                        g.DrawImageUnscaled(icon, left, top + (24 - size) / 2);
                }
            }
            for (int i = 0; i < captions.Length; i++)
                g.DrawString(captions[i], label, new SolidBrush(labelColor), x + 60 + i * 102, y + height - 18);
        }
    }
}

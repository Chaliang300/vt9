using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace VT9Status
{
    // 托盘菜单按设计稿的克制规则重绘：白底、靛蓝 7% 悬停、无边框重线、12px 圆角。
    // 菜单只做窗口已有的操作，不新增对设备的写入路径。
    internal sealed partial class MainForm
    {
        private ContextMenuStrip trayMenu;
        private ToolStripMenuItem trayHeader, trayPosition, trayAutoStart;

        private void BuildTrayMenu()
        {
            trayMenu = new ContextMenuStrip
            {
                Renderer = new TrayMenuRenderer(),
                ShowImageMargin = false,
                ShowCheckMargin = true,
                Font = Fonts.Gdi(Fonts.Body, Fonts.Regular),
                Padding = new Padding(6, 6, 6, 6),
                DropShadowEnabled = false,
                MinimumSize = new Size(216, 0),
                BackColor = Color.White,
                ForeColor = Theme.Ink
            };
            trayHeader = TrayMenuItem("VT9 · 等待上报", null);
            trayHeader.Name = "trayHeader";
            trayHeader.Enabled = false;
            trayHeader.Font = Fonts.Gdi(Fonts.Body, Fonts.Bold);
            trayMenu.Items.Add(trayHeader);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(TrayMenuItem("显示状态", delegate { ShowPanel(); }));
            trayPosition = TrayMenuItem("读取配置", delegate { ShowPanel(); RunReadConfig(); });
            trayMenu.Items.Add(trayPosition);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayAutoStart = TrayMenuItem("登录时自动启动", null);
            trayAutoStart.Click += delegate
            {
                autoStartOn = !autoStartOn;
                ApplyAutoStart(autoStartOn);
                if (autoCheck != null) autoCheck.IsChecked = autoStartOn;
                Invalidate();
            };
            trayMenu.Items.Add(trayAutoStart);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(TrayMenuItem("退出", delegate { ExitApp(); }));
            trayMenu.Opening += delegate { UpdateTrayMenu(); };
            trayMenu.SizeChanged += delegate { ShapeTrayMenu(); };
        }

        private ToolStripMenuItem TrayMenuItem(string caption, EventHandler action)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(caption);
            item.Padding = new Padding(2, 5, 2, 5);
            item.ForeColor = Theme.Ink;
            if (action != null) item.Click += action;
            return item;
        }

        private void UpdateTrayMenu()
        {
            string state = level.HasValue ? "电量 " + level.Value + "%" : "等待上报";
            Reading shown = snapshot.DisplayReading;
            if (shown != null && level.HasValue) state = shown.Dpi.ToString() + " DPI · " + state;
            trayHeader.Text = "VT9 · " + state;
            trayAutoStart.Checked = autoStartOn;
            trayPosition.Enabled = !configBusy;
            trayPosition.Text = configBusy ? "正在读取配置…" : "读取配置";
            ShapeTrayMenu();
        }

        // 诊断用：把真实托盘菜单显示出来并截图，避免只能靠人工点右键确认配色。
        // 截图时勾选态强制打开，否则“登录时自动启动”未勾选时看不到复选标记的绘制效果。
        internal void PreviewTrayMenu(string path)
        {
            bool restore = autoStartOn;
            autoStartOn = true;
            trayMenu.Show(new Point(240, 240));
            System.Windows.Threading.DispatcherTimer shot = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(700) };
            shot.Tick += delegate
            {
                shot.Stop();
                try
                {
                    Point origin = trayMenu.PointToScreen(Point.Empty);
                    Rectangle area = new Rectangle(origin.X - 16, origin.Y - 16, trayMenu.Width + 32, trayMenu.Height + 32);
                    using (Bitmap bitmap = new Bitmap(area.Width, area.Height))
                    {
                        using (Graphics graphics = Graphics.FromImage(bitmap))
                            graphics.CopyFromScreen(area.Left, area.Top, 0, 0, area.Size);
                        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                    }
                    Program.Log("托盘菜单预览已保存：" + path);
                }
                catch (Exception ex) { Program.Log("托盘菜单预览失败：" + ex.Message); }
                autoStartOn = restore;
                trayMenu.Close();
                if (System.Windows.Application.Current != null) System.Windows.Application.Current.Shutdown();
            };
            shot.Start();
        }

        private void ShapeTrayMenu()
        {
            if (trayMenu == null || trayMenu.Width <= 0 || trayMenu.Height <= 0) return;
            using (GraphicsPath path = RoundRect(new Rectangle(0, 0, trayMenu.Width, trayMenu.Height), 10))
            {
                Region previous = trayMenu.Region;
                trayMenu.Region = new Region(path);
                if (previous != null) previous.Dispose();
            }
        }

        private static GraphicsPath RoundRect(Rectangle bounds, int radius)        {
            GraphicsPath path = new GraphicsPath();
            int d = radius * 2;
            if (d > bounds.Width) d = bounds.Width;
            if (d > bounds.Height) d = bounds.Height;
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private sealed class TrayMenuColors : ProfessionalColorTable
        {
            public override Color MenuItemSelected { get { return Color.FromArgb(241, 242, 253); } }
            public override Color MenuItemSelectedGradientBegin { get { return Color.FromArgb(241, 242, 253); } }
            public override Color MenuItemSelectedGradientEnd { get { return Color.FromArgb(241, 242, 253); } }
            public override Color MenuItemBorder { get { return Color.FromArgb(241, 242, 253); } }
            public override Color MenuItemPressedGradientBegin { get { return Color.FromArgb(232, 234, 252); } }
            public override Color MenuItemPressedGradientEnd { get { return Color.FromArgb(232, 234, 252); } }
            public override Color ToolStripDropDownBackground { get { return Color.White; } }
            public override Color ImageMarginGradientBegin { get { return Color.White; } }
            public override Color ImageMarginGradientMiddle { get { return Color.White; } }
            public override Color ImageMarginGradientEnd { get { return Color.White; } }
            public override Color SeparatorDark { get { return Color.FromArgb(230, 234, 236); } }
            public override Color SeparatorLight { get { return Color.FromArgb(230, 234, 236); } }
        }

        private sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
        {
            internal TrayMenuRenderer() : base(new TrayMenuColors()) { RoundedEdges = false; }

            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                e.Graphics.Clear(Color.White);
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                using (GraphicsPath path = RoundRect(new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1), 10))
                using (Pen pen = new Pen(Color.FromArgb(230, 234, 236)))
                    e.Graphics.DrawPath(pen, path);
            }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                using (Pen pen = new Pen(Color.FromArgb(230, 234, 236)))
                {
                    int y = e.Item.Height / 2;
                    e.Graphics.DrawLine(pen, 12, y, e.Item.Width - 12, y);
                }
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                if (e.Item.Name == "trayHeader") e.TextColor = Color.FromArgb(74, 86, 82);
                else if (!e.Item.Enabled) e.TextColor = Color.FromArgb(138, 148, 145);
                else e.TextColor = Theme.Ink;
                base.OnRenderItemText(e);
            }

            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle box = e.ImageRectangle;
                float x = box.Left + box.Width * 0.28F, y = box.Top + box.Height * 0.5F;
                using (Pen pen = new Pen(Theme.Accent, 1.6F))
                {
                    pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
                    g.DrawLines(pen, new[]
                    {
                        new PointF(x, y),
                        new PointF(x + box.Width * 0.18F, y + box.Height * 0.20F),
                        new PointF(x + box.Width * 0.46F, y - box.Height * 0.20F)
                    });
                }
            }
        }
    }
}

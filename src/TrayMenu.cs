using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace VT9Status
{
    // 托盘菜单按设计稿的克制规则重绘：白底、靛蓝 7% 悬停、无边框重线、10px 圆角。
    // 结构上把“设备摘要”拆成两行（DPI / 电量与连接），让每次右键先看到当前状态再看操作。
    // 菜单只做窗口已有的操作，不新增对设备的写入路径。
    internal sealed partial class MainForm
    {
        private ContextMenuStrip trayMenu;
        private ToolStripMenuItem trayHeader, traySubHeader, trayPosition, trayAutoStart, trayExit;

        private void BuildTrayMenu()
        {
            trayMenu = new ContextMenuStrip
            {
                Renderer = new TrayMenuRenderer(),
                ShowImageMargin = false,
                ShowCheckMargin = true,
                Font = Fonts.Gdi(Fonts.Chip, Fonts.Regular),
                Padding = new Padding(0, 6, 0, 6),
                DropShadowEnabled = false,
                MinimumSize = new Size(200, 0),
                BackColor = Color.White,
                ForeColor = Theme.Ink
            };

            // 摘要两行：主行设备与 DPI，副行电量与连接状态。
            trayHeader = TrayMenuItem("VT9", null);
            trayHeader.Name = "trayHeader";
            trayHeader.Enabled = false;
            trayHeader.Font = Fonts.Gdi(Fonts.Sub, Fonts.Bold);
            trayHeader.Padding = new Padding(2, 3, 2, 0);
            trayMenu.Items.Add(trayHeader);

            traySubHeader = TrayMenuItem("尚未收到电量", null);
            traySubHeader.Name = "traySubHeader";
            traySubHeader.Enabled = false;
            traySubHeader.Font = Fonts.Gdi(Fonts.Meta, Fonts.Regular);
            traySubHeader.Padding = new Padding(2, 0, 2, 4);
            trayMenu.Items.Add(traySubHeader);

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
            trayExit = TrayMenuItem("退出", delegate { ExitApp(); });
            trayExit.Name = "trayExit";
            trayMenu.Items.Add(trayExit);

            trayMenu.Opening += delegate { UpdateTrayMenu(); };
            trayMenu.SizeChanged += delegate { ShapeTrayMenu(); };
        }

        private ToolStripMenuItem TrayMenuItem(string caption, EventHandler action)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(caption);
            // 32px 行高：13.5px 文字 + 上下各 7px，比默认的紧凑行距更好点中。
            item.Padding = new Padding(2, 7, 2, 7);
            item.ForeColor = Theme.Ink;
            if (action != null) item.Click += action;
            return item;
        }

        private void UpdateTrayMenu()
        {
            Reading shown = snapshot.DisplayReading;
            trayHeader.Text = shown != null && level.HasValue ? "VT9 · " + shown.Dpi + " DPI" : "VT9";

            string state = level.HasValue ? "电量 " + level.Value + "%" : "尚未收到电量";
            if (snapshot.Open) state += charge.Fresh && charge.Last != null && charge.Last.Charging ? " · 充电中" : " · 已连接";
            traySubHeader.Text = state;

            trayAutoStart.Checked = autoStartOn;
            trayPosition.Enabled = !configBusy;
            trayPosition.Text = configBusy ? "正在读取配置…" : "读取配置";
            ShapeTrayMenu();
        }

        // 右键菜单以“图标左下角”为锚点向上展开：菜单底边贴住图标上沿、左边缘与图标左边缘对齐，
        // 再整体夹进工作区（任务栏在底部时就是贴着任务栏上沿）。拿不到图标矩形时退回鼠标位置。
        // 以前是 ContextMenuStrip.Show(Cursor.Position)，等于用鼠标点当菜单左上角，菜单会往右下角甩。
        internal void ShowTrayMenu()
        {
            UpdateTrayMenu();   // 先把摘要文字算好，PreferredSize 才是最终尺寸
            Size size = trayMenu.GetPreferredSize(Size.Empty);
            Rectangle icon;
            Point anchor;
            if (TryTrayRect(out icon)) anchor = new Point(icon.Left, icon.Top);
            else anchor = System.Windows.Forms.Cursor.Position;

            Rectangle area = Screen.FromPoint(anchor).WorkingArea;
            int x = anchor.X;
            int y = anchor.Y - size.Height;
            if (x + size.Width > area.Right) x = area.Right - size.Width;
            if (x < area.Left) x = area.Left;
            if (y + size.Height > area.Bottom) y = area.Bottom - size.Height;
            if (y < area.Top) y = area.Top;
            Program.Log("托盘菜单：锚点 " + anchor.X + "," + anchor.Y + "，尺寸 " + size.Width + "x" + size.Height
                + "，位置 " + x + "," + y + "，工作区 " + area);
            trayMenu.Show(new Point(x, y));
        }

        // 诊断用：把真实托盘菜单显示出来并截图，避免只能靠人工点右键确认配色。
        // 截图时勾选态强制打开，否则“登录时自动启动”未勾选时看不到复选标记的绘制效果。
        internal void PreviewTrayMenu(string path)
        {
            bool restore = autoStartOn;
            autoStartOn = true;
            UpdateTrayMenu();
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
            public override Color SeparatorDark { get { return Color.FromArgb(233, 238, 243); } }
            public override Color SeparatorLight { get { return Color.FromArgb(233, 238, 243); } }
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
                using (Pen pen = new Pen(Color.FromArgb(233, 238, 243)))
                {
                    int y = e.Item.Height / 2;
                    e.Graphics.DrawLine(pen, 12, y, e.Item.Width - 12, y);
                }
            }

            // 悬停画成内缩的圆角块，而不是铺满整行的矩形——菜单底色保持干净，视觉重心落在文字上。
            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                if (!e.Item.Selected || !e.Item.Enabled) return;
                bool exit = e.Item.Name == "trayExit";
                Rectangle bounds = new Rectangle(6, 2, e.Item.Width - 12, e.Item.Height - 4);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath path = RoundRect(bounds, 8))
                using (SolidBrush fill = new SolidBrush(exit ? Color.FromArgb(253, 241, 240) : Color.FromArgb(241, 242, 253)))
                    e.Graphics.FillPath(fill, path);
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                if (e.Item.Name == "trayHeader") e.TextColor = Theme.Ink;
                else if (e.Item.Name == "traySubHeader") e.TextColor = Theme.Muted;
                else if (!e.Item.Enabled) e.TextColor = Theme.DisabledInk;
                else if (e.Item.Name == "trayExit" && e.Item.Selected) e.TextColor = Theme.CloseHover;
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

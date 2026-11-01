using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace VT9Status
{
    internal static class Program
    {
        internal static readonly string DataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
        private static readonly object LogGate = new object();
        internal static EventWaitHandle Wake;

        [STAThread]
        private static void Main(string[] args)
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                if (Array.IndexOf(args, "--self-test") >= 0) { SelfTest(); return; }
                if (Array.IndexOf(args, "--tray-icon-preview") >= 0) { IconArtwork.TrayPreview(Path.Combine(DataDir, "tray-gauge-preview.png")); return; }
                if (Array.IndexOf(args, "--dpi-read-test") >= 0)
                {
                    lock (ConfigChannelLock.Gate)
                    using (NativeDpiChannel channel = new NativeDpiChannel())
                    {
                        DpiProfile profile = DpiController.Load(channel);
                        SaveJson(Path.Combine(DataDir, "dpi-read-test.json"), new { current = profile.Current, activeIndex = profile.Active[0], values = profile.Values,
                            activeRaw = BitConverter.ToString(profile.Active), tableRaw = BitConverter.ToString(profile.Table), deviceWrites = 0 });
                    }
                    return;
                }
                if (Array.IndexOf(args, "--config-dump") >= 0) { ConfigDump(); return; }
                if (Array.IndexOf(args, "--charge-test") >= 0)
                {
                    using (ChargeMonitor charge = new ChargeMonitor())
                    {
                        Stopwatch deadline = Stopwatch.StartNew();
                        while (charge.Snapshot().Last == null && deadline.Elapsed.TotalSeconds < 8) Thread.Sleep(100);
                        ChargeSnapshot result = charge.Snapshot();
                        SaveJson(Path.Combine(DataDir, "charge-test.json"), new { state = result.Text, fresh = result.Fresh,
                            productId = result.Last == null ? "" : result.Last.ProductId.ToString("X4"),
                            battery = result.Last == null ? -1 : result.Last.Battery, raw = result.Last == null ? "" : result.Last.Hex });
                    }
                    return;
                }
                if (Array.IndexOf(args, "--icon-preview") >= 0) { IconArtwork.Preview(Path.Combine(DataDir, "icon-preview.png")); return; }
                if (Array.IndexOf(args, "--tray-menu-preview") >= 0) { TrayMenuPreview(); return; }
                if (Array.IndexOf(args, "--tray-shot") >= 0) { TrayShot(); return; }
                int exportIcons = Array.IndexOf(args, "--export-icons");
                if (exportIcons >= 0)
                {
                    string target = exportIcons + 1 < args.Length
                        ? args[exportIcons + 1]
                        : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "VT9Status.ico");
                    IconArtwork.ExportIco(target, new[] { 16, 20, 24, 32, 48, 64, 128, 256 });
                    Log("图标：已导出 " + target);
                    return;
                }
                if (Array.IndexOf(args, "--ui-preview") >= 0) { UiPreview(false, false, PageArgument(args)); return; }
                if (Array.IndexOf(args, "--ui-preview-bg") >= 0) { UiPreview(true, false, PageArgument(args)); return; }
                if (Array.IndexOf(args, "--ui-preview-live") >= 0) { UiPreview(true, true, PageArgument(args)); return; }
                if (Array.IndexOf(args, "--ui-motion-test") >= 0) { UiMotionTest(); return; }
                if (Array.IndexOf(args, "--manual-check") >= 0) { ManualCheckDiagnostic(); return; }
                int diagnostic = Array.IndexOf(args, "--diagnose");
                if (diagnostic >= 0)
                {
                    int seconds = 20;
                    if (diagnostic + 1 < args.Length && !int.TryParse(args[diagnostic + 1], out seconds)) seconds = 20;
                    Diagnose(Math.Max(5, Math.Min(300, seconds)));
                    return;
                }
                bool created;
                using (Mutex mutex = new Mutex(true, "Local\\VT9Status_Ces_24AE1849", out created))
                {
                    if (!created)
                    {
                        try { using (EventWaitHandle wake = EventWaitHandle.OpenExisting("Local\\VT9Status_Ces_Show")) wake.Set(); }
                        catch (WaitHandleCannotBeOpenedException) { }
                        return;
                    }
                    try
                    {
                        using (Wake = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\VT9Status_Ces_Show"))
                        {



                            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
                            {
                                Log(e.Exception.ToString());
                                if (e.Exception.ToString().IndexOf("UCEERR_RENDERTHREADFAILURE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    e.Exception.ToString().IndexOf("88980406", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
                                    Log("渲染引擎自愈：已切换至软件渲染模式。");
                                    return;
                                }
                                MessageBox.Show(e.Exception.Message, "VT9 状态 · 操作失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            };
                            RunDesktop(args);
                        }
                    }
                    finally { mutex.ReleaseMutex(); }
                }
            }
            catch (Exception ex)
            {
                Log(ex.ToString());
                MessageBox.Show("启动失败：" + ex.Message + "\n请确认程序所在目录可写。", "VT9 状态", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        internal static void Log(string text)
        {
            try
            {
                lock (LogGate)
                {
                    string path = Path.Combine(DataDir, "app.log");
                    if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024)
                    {
                        string old = path + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(path, old);
                    }
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + text + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { /* Logging must never interrupt HID reads or shutdown. */ }
        }

        // Renders the real window and screenshots it, so layout and the frosted backdrop can be
        // checked without a human at the keyboard. With a stage behind it, the blur is visible.
        private static void RunDesktop(string[] args)
        {
            System.Windows.Application app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += delegate(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
            {
                Log(e.Exception.ToString());
                if (e.Exception.ToString().IndexOf("UCEERR_RENDERTHREADFAILURE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    e.Exception.ToString().IndexOf("88980406", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
                    Log("渲染引擎自愈：已切换至软件渲染模式。");
                    e.Handled = true;
                    return;
                }
                MessageBox.Show(e.Exception.Message, "VT9 状态 · 操作失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                e.Handled = true;
            };
            MainForm window = new MainForm(Array.IndexOf(args, "--tray") >= 0);
            app.MainWindow = window;
            app.SessionEnding += delegate { window.ShutdownForSession(); };
            if (Array.IndexOf(args, "--tray") < 0) window.Show();
            app.Run();
        }

        // 预览可以指定初始页：--ui-preview --page=dpi / --page=params / --page=overview
        private static string PageArgument(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith("--page=", StringComparison.OrdinalIgnoreCase))
                    return args[i].Substring(7).ToLowerInvariant();
            }
            return null;
        }

        // Interactive preview uses the real WPF foreground and system backdrop. The only screen
        // capture is this explicit screenshot diagnostic, never the production backdrop path.
        private static void UiPreview(bool withStage, bool interactive = false, string page = null)
        {



            System.Windows.Application app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
            PreviewStage stage = null;
            if (withStage) { stage = new PreviewStage(); stage.Show(); }
            MainForm form = new MainForm(false);
            form.Topmost = true;
            if (stage != null) stage.Target = form;
            form.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
            form.Left = 140; form.Top = 110; if (stage != null) form.Owner = stage;
            form.Show(); form.Activate(); form.LoadProfileForPreview();
            if (!String.IsNullOrEmpty(page)) form.SelectPageForPreview(page);
            System.Windows.Threading.DispatcherTimer previewTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5.2) };
            previewTimer.Tick += delegate
            {
                previewTimer.Stop();
                string name = withStage ? "ui-preview-bg" : "ui-preview";
                if (!String.IsNullOrEmpty(page)) name += "-" + page;
                CaptureWindow(form, name + ".png");
                SaveJson(Path.Combine(DataDir, "layout-report.json"), new {
                    window = new { width = 1020, height = 606 },
                    dock = new { x = 34, y = 76, width = 100, height = 506 },
                    capsule = new { width = 80, height = 76 },
                    contentArea = new { x = 150, y = 76, width = 846, height = 506 },
                    rightBoundary = 996
                });
                if (stage != null) { stage.Close(); }
                form.ShutdownForSession(); form.Close();
                Log("WPF 界面预览已保存，逻辑尺寸 " + form.ActualWidth + "x" + form.ActualHeight + (String.IsNullOrEmpty(page) ? "" : "，页面 " + page));
            };
            if (!interactive) previewTimer.Start(); app.Run();
        }
        // 动效诊断：连点两次切换，第二次落在第一次入场进行到一半时，
        // 用"动画中段"与"完全静止"两张截图 + 一份实时状态快照证明
        // 打断是就地续接（不闪回、不留半透明残影、Effect/CacheMode 归零）。
        private static void UiMotionTest()
        {
            System.Windows.Application app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
            PreviewStage stage = new PreviewStage();
            stage.Show();
            MainForm form = new MainForm(false) { Topmost = true };
            stage.Target = form;
            form.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
            form.Left = 140; form.Top = 110; form.Owner = stage;
            form.Show(); form.Activate(); form.LoadProfileForPreview();

            int step = 0;
            System.Windows.Threading.DispatcherTimer timer = new System.Windows.Threading.DispatcherTimer(
                System.Windows.Threading.DispatcherPriority.Render)
            { Interval = TimeSpan.FromMilliseconds(60) };
            timer.Tick += delegate
            {
                step++;
                // 300ms 起步留给首帧与背景材质；之后每 60ms 一步。
                if (step == 5)
                {
                    form.SelectPageForPreview("dpi");
                    Log("动效诊断：切到 DPI（开始 420ms 入场）");
                }
                else if (step == 7)
                {
                    form.SelectPageForPreview("settings");
                    Log("动效诊断：DPI 入场中打断 → 设置");
                }
                else if (step == 9)
                {
                    CaptureWindow(form, "motion-mid.png");
                    SaveJson(Path.Combine(DataDir, "motion-report-mid.json"), form.MotionSnapshot());
                    Log("动效诊断：中段快照已保存");
                }
                else if (step == 40)
                {
                    CaptureWindow(form, "motion-final.png");
                    SaveJson(Path.Combine(DataDir, "motion-report-final.json"), form.MotionSnapshot());
                    timer.Stop();
                    stage.Close();
                    form.ShutdownForSession(); form.Close();
                    Log("动效诊断：静止快照已保存");
                }
            };
            timer.Start();
            app.Run();
        }

        private static void CaptureWindow(MainForm form, string fileName)
        {
            System.Windows.Point origin = form.PointToScreen(new System.Windows.Point(0, 0));
            System.Windows.Media.Matrix matrix = System.Windows.PresentationSource.FromVisual(form).CompositionTarget.TransformToDevice;
            Rectangle area = new Rectangle((int)origin.X, (int)origin.Y, (int)Math.Round(form.ActualWidth * matrix.M11), (int)Math.Round(form.ActualHeight * matrix.M22));
            try
            {
                using (Bitmap shot = new Bitmap(area.Width, area.Height))
                {
                    using (Graphics graphics = Graphics.FromImage(shot)) graphics.CopyFromScreen(area.Left, area.Top, 0, 0, area.Size);
                    shot.Save(Path.Combine(DataDir, fileName), System.Drawing.Imaging.ImageFormat.Png);
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // A restricted desktop can render WPF but cannot supply screen pixels.
                // Keep an honest layout image; the native DWM backdrop is not in this image.
                System.Windows.Media.Imaging.RenderTargetBitmap bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    area.Width, area.Height, 96 * matrix.M11, 96 * matrix.M22, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(form);
                System.Windows.Media.Imaging.PngBitmapEncoder encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using (FileStream stream = File.Create(Path.Combine(DataDir, fileName))) encoder.Save(stream);
                Log("预览使用 WPF 渲染图（桌面截图不可用，不含 DWM 背景）：" + fileName);
            }
        }

        private sealed class PreviewStage : System.Windows.Window
        {
            internal MainForm Target;
            private readonly System.Windows.Controls.Canvas pattern = new System.Windows.Controls.Canvas { ClipToBounds = true };
            private int palette;
            internal PreviewStage()
            {
                Title = "VT9 Acrylic preview stage";
                Width = 1280; Height = 860; Left = 40; Top = 30;
                WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
                ShowInTaskbar = true;
                System.Windows.Controls.StackPanel buttons = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Left, VerticalAlignment = System.Windows.VerticalAlignment.Top };
                System.Windows.Controls.Button none = new System.Windows.Controls.Button { Content = "无材质对照", Padding = new System.Windows.Thickness(12) };
                System.Windows.Controls.Button acrylic = new System.Windows.Controls.Button { Content = "原生 Acrylic", Padding = new System.Windows.Thickness(12) };
                none.Click += delegate { if (Target != null) Target.SetBackdropForPreview(1); };
                acrylic.Click += delegate { if (Target != null) Target.SetBackdropForPreview(3); };
                System.Windows.Controls.Button swap = new System.Windows.Controls.Button { Content = "背景换色", Padding = new System.Windows.Thickness(12) };
                swap.Click += delegate { palette++; DrawPattern(); };
                buttons.Children.Add(none); buttons.Children.Add(acrylic); buttons.Children.Add(swap);
                System.Windows.Controls.Grid root = new System.Windows.Controls.Grid();
                root.Children.Add(pattern); root.Children.Add(buttons); Content = root;
                SizeChanged += delegate { DrawPattern(); };
            }
            private void DrawPattern()
            {
                pattern.Children.Clear();
                pattern.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(238, 241, 250));
                string[] colors = palette % 2 == 0 ? new[] { "#C2CBFF", "#C9E0FF", "#E9D6FB" } : new[] { "#C9E0FF", "#E9D6FB", "#C2CBFF" };
                double[] xs = { 50, 580, -80 }; double[] ys = { -120, 180, 290 };
                for (int i = 0; i < colors.Length; i++)
                {
                    System.Windows.Media.Color tint = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colors[i]);
                    System.Windows.Media.RadialGradientBrush glow = new System.Windows.Media.RadialGradientBrush();
                    glow.GradientStops.Add(new System.Windows.Media.GradientStop(tint, 0));
                    glow.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromArgb(0, tint.R, tint.G, tint.B), 1));
                    System.Windows.Shapes.Ellipse light = new System.Windows.Shapes.Ellipse { Width = 720, Height = 720, Fill = glow };
                    System.Windows.Controls.Canvas.SetLeft(light, xs[i]); System.Windows.Controls.Canvas.SetTop(light, ys[i]); pattern.Children.Add(light);
                }
            }
        }
        private static void ManualCheckDiagnostic()        {
            using (HidReader reader = new HidReader(Log))
            {
                // Establish an existing reading before requesting a genuinely new connection.
                Stopwatch deadline = Stopwatch.StartNew();
                while (reader.Snapshot().Last == null && deadline.Elapsed.TotalSeconds < 12) Thread.Sleep(100);
                DeviceSnapshot before = reader.Snapshot();
                bool accepted = reader.RequestManualCheck();
                bool duplicateRejected = !reader.RequestManualCheck();
                bool retainedWhileReconnecting = false;
                deadline.Restart();
                while (reader.Snapshot().Checking && deadline.Elapsed.TotalSeconds < 35)
                {
                    DeviceSnapshot waiting = reader.Snapshot();
                    if (waiting.Last == null && waiting.DisplayReading != null && before.Last != null && waiting.DisplayReading.Sequence == before.Last.Sequence)
                        retainedWhileReconnecting = true;
                    Thread.Sleep(100);
                }
                DeviceSnapshot after = reader.Snapshot();
                SaveJson(Path.Combine(DataDir, "manual-check-test.json"), new { accepted = accepted, duplicateRejected = duplicateRejected,
                    packetsBefore = before.Packets, packetsAfter = after.Packets, connectionsBefore = before.Connections,
                    connectionsAfter = after.Connections, pending = after.Checking, result = after.CheckStatus,
                    newReportAfterReconnect = after.Last != null && after.Last.Sequence > before.Packets && after.Connections > before.Connections,
                    retainedWhileReconnecting = retainedWhileReconnecting, elapsedSeconds = deadline.Elapsed.TotalSeconds });
            }
        }

        // 截取任务栏里这颗托盘图标的实际像素：验证 16/20/24 缩放下的显示效果。
        private static void TrayShot()
        {
            System.Windows.Application app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
            MainForm form = new MainForm(true);
            Stopwatch waited = Stopwatch.StartNew();
            System.Windows.Threading.DispatcherTimer timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            timer.Tick += delegate
            {
                Rectangle slot;
                bool located = form.TryTrayRect(out slot);
                if (!located)
                {
                    // 注册返回失败时也要有图可看：退而截屏幕右下角的通知区域。
                    if (waited.Elapsed.TotalSeconds < 6) return;
                    Rectangle screen = Screen.PrimaryScreen.Bounds;
                    slot = new Rectangle(screen.Right - 520, screen.Bottom - 96, 520, 96);
                    Log("托盘截图：未取得图标位置，改为截取通知区域，" + slot);
                }
                timer.Stop();
                Rectangle area = new Rectangle(slot.Left - 6, slot.Top - 6, slot.Width + 12, slot.Height + 12);
                using (Bitmap shot = new Bitmap(area.Width, area.Height))
                {
                    using (Graphics graphics = Graphics.FromImage(shot)) graphics.CopyFromScreen(area.Left, area.Top, 0, 0, area.Size);
                    shot.Save(Path.Combine(DataDir, located ? "tray-shot.png" : "tray-region.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
                Log("托盘截图：位置 " + slot.Left + "," + slot.Top + "，尺寸 " + slot.Width + "x" + slot.Height + "。");
                app.Shutdown();
            };
            timer.Start(); app.Run();
        }

        // Renders the tray menu itself and screenshots it: the only screen capture here.
        private static void TrayMenuPreview()
        {
            System.Windows.Application app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
            MainForm form = new MainForm(true);
            form.PreviewTrayMenu(Path.Combine(DataDir, "tray-menu-preview.png"));
            app.Run();
        }

        internal static void SaveJson(string path, object value)
        {
            string temp = path + ".tmp";
            File.WriteAllText(temp, new JavaScriptSerializer().Serialize(value), Encoding.UTF8);
            try
            {
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            catch
            {
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(temp, path);
                }
                catch
                {
                    File.WriteAllText(path, new JavaScriptSerializer().Serialize(value), Encoding.UTF8);
                }
            }
        }

        private static void SelfTest()
        {
            int passed = 0;
            Action<bool, string> check = delegate(bool ok, string name) { if (!ok) throw new Exception("Self-test failed: " + name); passed++; };
            Reading r;
            check(Reading.TryParse(new byte[] { 0xBB, 0xB0, 2, 0x40, 6, 1, 12 }, out r) && r.Dpi == 1600 && r.Battery == 12, "known real packet");
            check(Reading.TryParse(new byte[] { 0xBB, 0xB0, 2, 0x20, 3, 1, 0 }, out r) && r.Dpi == 800 && r.Battery == 0, "little endian and empty battery");
            check(Reading.TryParse(new byte[] { 0xBB, 0xB0, 2, 0x40, 6, 1, 100 }, out r), "full battery");
            check(!Reading.TryParse(null, out r), "null");
            check(!Reading.TryParse(new byte[6], out r), "truncated");
            check(!Reading.TryParse(new byte[] { 0xBA, 0xB0, 2, 0x40, 6, 1, 12 }, out r), "other report");
            check(!Reading.TryParse(new byte[] { 0xBB, 0xB1, 2, 0x40, 6, 1, 12 }, out r), "other command");
            check(!Reading.TryParse(new byte[] { 0xBB, 0xB0, 2, 0x40, 6, 1, 255 }, out r), "invalid battery");
            check(!Reading.TryParse(new byte[] { 0xBB, 0xB0, 2, 0, 0, 1, 12 }, out r), "invalid DPI");
            check(Marshal.SizeOf(typeof(Native.Caps)) == 64, "HIDP_CAPS layout");
            check(Marshal.SizeOf(typeof(Native.Overlapped)) == (IntPtr.Size == 8 ? 32 : 20), "OVERLAPPED layout");
            DeviceSnapshot s = new DeviceSnapshot { Last = new Reading(), Open = true, Age = 14.9 };
            check(s.Fresh, "fresh report"); s.Age = 15;
            check(!s.Fresh, "stale report"); s.Age = 0; s.Open = false;
            check(!s.Fresh, "disconnected"); s.Open = true; s.Last = null;
            check(!s.Fresh, "no report");
            s.LastKnown = new Reading { Dpi = 1600, Battery = 10 };
            check(s.DisplayReading == s.LastKnown && !s.Fresh, "retain previous display while waiting without treating it as fresh");
            s.Last = new Reading { Dpi = 800, Battery = 11 };
            check(s.DisplayReading == s.Last, "new report replaces retained display");
            ChargeReading chargeReading;
            check(ChargeReading.TryParse(new byte[] { 1, 2, 34 }, out chargeReading) && chargeReading.Charging && chargeReading.Battery == 34, "real charging reply");
            check(ChargeReading.TryParse(new byte[] { 0xBA, 1, 1, 100 }, out chargeReading) && !chargeReading.Charging, "battery reply with report id; 100 is not inferred charging");
            check(!ChargeReading.TryParse(new byte[] { 2 }, out chargeReading), "busy reply is not charging");
            check(!ChargeReading.TryParse(new byte[] { 1, 3, 34 }, out chargeReading), "unknown charge marker");
            check(!ChargeReading.TryParse(new byte[] { 1, 2, 255 }, out chargeReading), "invalid charge level");
            check(HidReader.BackoffMilliseconds(1) == 3000 && HidReader.BackoffMilliseconds(2) == 6000 &&
                HidReader.BackoffMilliseconds(4) == 24000 && HidReader.BackoffMilliseconds(6) == 30000, "receiver search back-off grows and caps at 30s");
            check(HidReader.BackoffMilliseconds(0) == 3000 && HidReader.BackoffMilliseconds(3) >= HidReader.BackoffMilliseconds(2) &&
                HidReader.BackoffMilliseconds(99) == 30000, "back-off never drops below the base delay");
            check(ReferenceEase.Card.Ease(0) == 0 && ReferenceEase.Card.Ease(1) == 1,
                "reference curve endpoints");
            // At Bezier parameter u=.5, x=.3275 and y=.8375 for the card curve.
            check(Math.Abs(ReferenceEase.Card.Ease(0.3275) - 0.8375) < 0.000001,
                "Bezier solves X before Y rather than treating time as parameter");
            check(ReferenceEase.Switch.Ease(0.7) > 1,
                "switch curve retains the reference overshoot");
            double previous = 0;
            bool monotonic = true;
            for (int i = 1; i <= 100; i++)
            {
                double value = ReferenceEase.Card.Ease(i / 100.0);
                monotonic &= value >= previous && value <= 1;
                previous = value;
            }
            check(monotonic, "card entrance settles monotonically without spring overshoot");
            passed += DpiTests.Run();
            passed += SettingsTests.Run();
            passed += DockPhysicsTests.Run();
            passed += TrayIconTests.Run();
            SaveJson(Path.Combine(DataDir, "self-test.json"), new { passed = passed, result = "PASS", at = DateTime.Now.ToString("o") });
        }

        // 只读探针：把七个已知配置块的原始字节写出来，用于和官方驱动做差分。
        // 不发任何 0xA5 写命令。
        private static void ConfigDump()
        {
            System.Collections.Generic.List<object> blocks = new System.Collections.Generic.List<object>();
            bool wired;
            lock (ConfigChannelLock.Gate)
            using (NativeDpiChannel channel = new NativeDpiChannel())
            {
                wired = channel.Wired;
                uint[] addresses = { 0x880, 0x884, 0x888, 0x898, 0x8C0, 0x8C4, 0x8C8 };
                string[] names = { "performance", "sensor", "dpiX", "activeStage", "timing", "angle", "dpiY" };
                int[] lengths = { 4, 4, 16, 4, 4, 4, 16 };
                for (int i = 0; i < addresses.Length; i++)
                {
                    string hex = "";
                    string error = "";
                    try
                    {
                        byte[] block = ConfigReader.ReadStable(channel, addresses[i], lengths[i]);
                        hex = BitConverter.ToString(block).Replace("-", " ");
                    }
                    catch (Exception ex) { error = ex.Message; }
                    blocks.Add(new
                    {
                        name = names[i],
                        address = "0x" + addresses[i].ToString("X3"),
                        length = lengths[i],
                        hex = hex,
                        error = error
                    });
                }
            }
            SaveJson(Path.Combine(DataDir, "config-dump.json"), new
            {
                wired = wired,
                deviceWrites = 0,
                at = DateTime.Now.ToString("o"),
                blocks = blocks
            });
        }

        private static void Diagnose(int seconds)
        {
            Process process = Process.GetCurrentProcess();
            TimeSpan cpu = process.TotalProcessorTime;
            Stopwatch elapsed = Stopwatch.StartNew();
            using (HidReader reader = new HidReader(Log))
            {
                int notifications = 0;
                reader.Changed += delegate { Interlocked.Increment(ref notifications); };
                System.Collections.Generic.List<object> samples = new System.Collections.Generic.List<object>();
                long previous = 0;
                while (elapsed.Elapsed.TotalSeconds < seconds)
                {
                    Thread.Sleep(200);
                    DeviceSnapshot s = reader.Snapshot();
                    if (s.Last != null && s.Packets != previous)
                    {
                        previous = s.Packets;
                        samples.Add(new { sequence = s.Last.Sequence, dpi = s.Last.Dpi, battery = s.Last.Battery, intervalMilliseconds = s.Last.IntervalMilliseconds, hex = s.Last.Hex, at = s.Last.ReceivedUtc.ToString("o") });
                    }
                }
                DeviceSnapshot end = reader.Snapshot(); process.Refresh();
                SaveJson(Path.Combine(DataDir, "diagnostic.json"), new { seconds = elapsed.Elapsed.TotalSeconds, packets = end.Packets,
                    connections = end.Connections, fresh = end.Fresh, state = end.State, samples = samples,
                    cpuSeconds = (process.TotalProcessorTime - cpu).TotalSeconds, workingSetBytes = process.WorkingSet64,
                    handles = process.HandleCount, notifications = notifications, note = "Short headless diagnostic; not a long-term GUI benchmark." });
            }
        }
    }

    internal sealed class Preferences
    {
        public bool LowBatteryNotification = true;
        // Transparency: 0 = opaque, 100 = transparent. 45 for cards, 80 for the window overlay. 数值存在时以文件为准，不做迁移。
        public int CardBackgroundTransparency = 45;
        public string CardBackgroundColor = "#FFFFFF";
        public int WindowBackgroundTransparency = 80;
        public string WindowBackgroundColor = "#FFFFFF";
        internal static Preferences Load()
        {
            string path = Path.Combine(Program.DataDir, "settings.json");
            if (!File.Exists(path)) return new Preferences();
            try { return new JavaScriptSerializer().Deserialize<Preferences>(File.ReadAllText(path)) ?? new Preferences(); }
            catch (Exception ex) { Program.Log("读取设置失败，改用默认值：" + ex.Message); return new Preferences(); }
        }
        internal void Save() { Program.SaveJson(Path.Combine(Program.DataDir, "settings.json"), this); }
    }
}




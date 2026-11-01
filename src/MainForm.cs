using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Brushes = System.Windows.Media.Brushes;

namespace VT9Status
{
    // Foreground only: DWM owns the live backdrop, including while DragMove is running.
    internal sealed partial class MainForm : Window
    {
        private readonly HidReader reader;
        private readonly ChargeMonitor charger;
        private NativeTrayIcon tray;   // 推迟到首帧之后创建，见构造函数里的 BeginInvoke
        private readonly DispatcherTimer clock;
        private readonly Preferences preferences;
        private Icon trayIcon;
        private string iconKey, lastSignature;
        private int updateQueued;
        private bool exiting, notified, initializing = true, autoStartOn, IsDisposed, trayHintLogged;
        private bool taskbarLight;
        private DeviceSnapshot snapshot = new DeviceSnapshot();
        private ChargeSnapshot charge = new ChargeSnapshot();
        private int? level;
        private bool batteryFresh;
        private string statusLabel = "等待上报", deviceLine = "正在查找接收器";
        private string chargeStatus = "";
        private const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        private const string RunName = "VT9Status_Ces";
        private HwndSource source;
        private bool frameExtended;
        private bool backdropAvailable;   // DWM 系统背衬可用（ExtendFrame + 属性 38 都成功）
        private bool backdropShownOnce;   // 首帧显示由 OnSourceInitialized/OnContentRendered 负责
        private DispatcherTimer backdropActivation;
        private DispatcherTimer deferredConfig;
        private int trayPixelSize = 16;
        private readonly Canvas surface = new Canvas();
        private CheckBox autoCheck, alertsCheck;
        private Style buttonStyle;

        internal MainForm(bool hidden)
        {
            preferences = Preferences.Load(); autoStartOn = IsAutoStart();
            Program.Mark("设置读取完成");
            Title = "雷柏 VT9 控制台"; Width = 1020; Height = 606;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            AllowsTransparency = false; Background = Brushes.Transparent; UseLayoutRounding = true;
            // 字体族与基准字号只在 Fonts 里定义：窗口级设置即可覆盖所有继承的文本，
            // 托盘菜单这些 WinForms 部分另行引用同一个族（见 TrayMenu.cs）。
            FontFamily = Fonts.Wpf;
            FontSize = Fonts.Body; Foreground = Brush(Theme.Ink);
            ApplyWindowIcon();
            BuildSurface();
            Program.Mark("界面构建完成");
            taskbarLight = IconArtwork.LightTaskbar();
            trayPixelSize = IconArtwork.TraySize();
            reader = new HidReader(Program.Log); charger = new ChargeMonitor();
            Program.Mark("设备通道初始化完成");
            reader.Changed += OnReaderChanged; charger.Changed += OnReaderChanged;
            clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            clock.Tick += delegate
            {
                RefreshState();
                if (Program.Wake != null && Program.Wake.WaitOne(0)) ShowPanel();
            };
            clock.Start(); initializing = false; RefreshState();
            Program.Mark("首次状态刷新完成");
            // 托盘（HwndSource + WinForms 菜单，实测约 110ms）不影响首帧，整体推迟到窗口画出来之后：
            // 先建菜单再建图标，最后补一次状态刷新把提示文字与图标补上。
            Dispatcher.BeginInvoke(new System.Action(delegate
            {
                if (IsDisposed) return;
                if (trayMenu == null) BuildTrayMenu();
                tray = new NativeTrayIcon { Text = "VT9 · 等待设备报告", ContextMenuStrip = trayMenu, Visible = true };
                tray.Click += delegate { ShowPanel(); };
                tray.MenuRequested += delegate { ShowTrayMenu(); };
                iconKey = ""; RefreshState();
                Program.Mark("托盘菜单与图标就绪");
            }), DispatcherPriority.Background);
            IsVisibleChanged += delegate
            {
                if (!IsVisible) return;
                RefreshState(); Invalidate();
                // 每次显示都重建背衬：窗口「先创建、后显示」或 Hide()/Show() 之后，DWM 会沿用
                // 旧窗框缓存，整窗被画成一块不透明灰矩形——表现为「从托盘恢复后背景异常」。
                Dispatcher.BeginInvoke(new System.Action(delegate
                {
                    if (IsDisposed || !IsVisible) return;
                    EnsureForeground();   // 先让窗口真的到最前，再重算窗框与材质
                    ApplyBackdrop();
                }), DispatcherPriority.Background);
                if (backdropShownOnce) Program.Log("背景：窗口重新显示，重建系统背衬。");
                backdropShownOnce = true;
                // 设备配置读取改到入场动画之后：它一完成就会触发整页重绘，和卡片入场抢 UI 线程。
                if (deferredConfig == null)
                {
                    deferredConfig = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
                    deferredConfig.Tick += delegate { deferredConfig.Stop(); EnsureConfigLoaded(); };
                }
                deferredConfig.Stop(); deferredConfig.Start();
            };
            // For --tray, Program runs the dispatcher without showing this window at all.
        }

        private static SolidColorBrush Brush(System.Drawing.Color color)
        {
            SolidColorBrush brush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(color.A, color.R, color.G, color.B));
            brush.Freeze(); return brush;
        }

        // 窗口与任务栏按钮使用嵌入在 exe 里的同一套应用图标，不再借用系统通用图标。
        private void ApplyWindowIcon()
        {
            try
            {
                using (System.Drawing.Icon source = System.Drawing.Icon.ExtractAssociatedIcon(Forms.Application.ExecutablePath))
                {
                    if (source == null) return;
                    Icon = Imaging.CreateBitmapSourceFromHIcon(source.Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                }
            }
            catch (Exception ex) { Program.Log("窗口图标：使用系统默认图标，" + ex.Message); }
        }
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e); source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            source.AddHook(WindowMessage); ApplyBackdrop();
        }
        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            SetBackdropSurface();   // 材质若已就绪就交出透明底，让 Acrylic 透出来
        }
        protected override void OnDeactivated(EventArgs e)
        {
            base.OnDeactivated(e);
            // 实测失焦后 DWM 仍在合成 Acrylic，这里不再改铺实色（那是旧版「灰板」的来源）。
            SetBackdropSurface();
        }
        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            Program.Mark("首帧渲染完成");
            ApplyBackdrop();
        }
        // 背景异常的两个真实成因（都靠抓屏逐帧定位，见 artifacts/tray-diag/bg_core.py）：
        //
        // 1) 「先创建、后显示」的窗口没有被 DWM 重算窗框。--tray 自启后第一次从托盘打开、
        //    以及 Hide() 之后再 Show()，DWM 仍按旧窗框缓存合成，整窗被画成一整块不透明的
        //    灰矩形，圆角与透明一起丢。此时属性 38 读回 3 也没用，它只是记录的值。
        //    对策：每次显示都重做帧扩展，并用 SWP_FRAMECHANGED 逼 DWM 丢掉窗框缓存（见 ApplyBackdrop）。
        //
        // 2) 曾经认为「Win11 只为活动窗口合成 Acrylic」，于是失去焦点就铺一层实色兜底。
        //    实测（Win11 24H2，属性 38=3）窗口失焦后 DWM 仍在合成，抓屏四角仍是各不相同的
        //    壁纸色；而那层兜底本身是常数色，反倒把窗口变成一块灰板。对策：删掉按焦点切换的逻辑。
        //
        // 现在只在材质真的不是 Acrylic 时才铺设计兜底色，其余一律交给 DWM。
        private void SetBackdropSurface()
        {
            int material = 0;
            bool acrylic = false;
            if (backdropAvailable && frameExtended && source != null && source.Handle != IntPtr.Zero)
            {
                try { acrylic = DwmGetWindowAttribute(source.Handle, 38, out material, 4) == 0 && material == 3; }
                catch (DllNotFoundException) { acrylic = false; }
                catch (EntryPointNotFoundException) { acrylic = false; }
            }
            Background = acrylic ? Brushes.Transparent : Brush(Theme.WindowFallback);
        }
        private void ApplyBackdrop()
        {
            if (source == null || IsDisposed) return;
            bool enabled = false;
            try
            {
                bool composed;
                if (!SystemParameters.HighContrast && DwmIsCompositionEnabled(out composed) == 0 && composed)
                {
                    // 帧扩展与两条属性每次都要重做：窗口「先创建、后显示」时（--tray 自启形态）
                    // 或在 Hide()/Show() 之后，DWM 会丢掉这些状态，只做一次的话窗口会变成
                    // 透明底 + 无背衬 = 透出黑色，叠上白色蒙层就成了中灰。
                    source.CompositionTarget.BackgroundColor = Colors.Transparent;
                    Margins margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                    frameExtended = DwmExtendFrameIntoClientArea(source.Handle, ref margins) == 0;
                    int light = 0; DwmSetWindowAttribute(source.Handle, 20, ref light, 4);
                    int rounded = 2; DwmSetWindowAttribute(source.Handle, 33, ref rounded, 4);
                    int none = 1;
                    enabled = frameExtended && DwmSetWindowAttribute(source.Handle, 38, ref none, 4) == 0;
                    // 关键一步。窗口「先创建、后显示」（--tray 自启、或 Hide() 之后再 Show()）时，
                    // DWM 仍按旧的窗框缓存合成：整窗被画成一整块不透明的灰矩形，圆角和透明一起丢，
                    // 看起来就是背景坏掉。属性 38 读回 3 也没用——那只是记录的值，窗框没重算。
                    // SWP_FRAMECHANGED 让 DWM 丢掉缓存、按当前样式重新生成窗框。
                    if (enabled) SetWindowPos(source.Handle, IntPtr.Zero, 0, 0, 0, 0,
                        SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
                }
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            backdropAvailable = enabled;
            if (enabled)
            {
                int current = -1;
                try { DwmGetWindowAttribute(source.Handle, 38, out current, 4); } catch { }
                Program.Log("背景：帧扩展=" + frameExtended + " 属性38=" + current + " 前台=" + (GetForegroundWindow() == source.Handle) + " 可见=" + IsVisible);
            }
            SetBackdropSurface();
            if (backdropActivation != null) backdropActivation.Stop();
            if (!enabled) { Program.Log("背景：原生 Acrylic 不可用，使用实色回退。"); return; }
            // Reproduce the working None -> Acrylic transition on separate compositor frames.
            // This one-shot runs only when showing/reconfiguring the window, never while dragging.
            backdropActivation = new DispatcherTimer(DispatcherPriority.ContextIdle) { Interval = TimeSpan.FromMilliseconds(120) };
            backdropActivation.Tick += delegate
            {
                backdropActivation.Stop();
                if (IsDisposed || source == null || !IsVisible) return;
                int acrylic = 3;
                int hr = DwmSetWindowAttribute(source.Handle, 38, ref acrylic, 4);
                SetBackdropSurface();   // 读到 3 才真的放透明，失败则保留设计兜底色
                surface.InvalidateVisual();
                Program.Log(hr == 0 ? "背景：已自动完成无材质 → 原生 Acrylic，后续由 DWM 合成。" : "背景：Acrylic 激活失败，HRESULT=" + hr);
            };
            backdropActivation.Start();
        }
        internal void SetBackdropForPreview(int mode)
        {
            if (source == null) return;
            int hr = DwmSetWindowAttribute(source.Handle, 38, ref mode, 4);
            dpiMessage = "材质对照 " + mode + "，HRESULT=" + hr;
            Invalidate();
        }
        private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam, ref bool handled)
        {
            if (message == 0x0219 && wparam.ToInt64() == 7 && !exiting) { reader.ReconnectForDeviceChange(); charger.RequestRefresh(); }
            if (message == 0x0006) Dispatcher.BeginInvoke(new System.Action(SetBackdropSurface));   // WM_ACTIVATE：焦点变了就重算底
            if (message == 0x02E0) { trayPixelSize = IconArtwork.TraySize(); iconKey = ""; }   // 跨屏换 DPI 时重算图标像素尺寸
            if (message == 0x031E || message == 0x001A || message == 0x031A) Dispatcher.BeginInvoke(new System.Action(ApplyBackdrop));
            // 任务栏换深浅色后托盘图标必须立刻反色，否则会和任务栏同色看不见。
            if (message == 0x001A || message == 0x031A) Dispatcher.BeginInvoke(new System.Action(RefreshTaskbarTheme));
            return IntPtr.Zero;
        }
        protected override void OnStateChanged(EventArgs e)
        { base.OnStateChanged(e); if (WindowState == WindowState.Minimized) HideToTray(); }
        protected override void OnClosing(CancelEventArgs e)
        {
            Program.Log("窗口：收到关闭请求 主动退出=" + exiting + " 配置忙=" + configBusy + " 会话结束=" + sessionEnding
                + " 托盘可用=" + (tray != null && tray.IsRegistered));
            // 配置写入/读取还没结束时，不能立刻销毁窗口——写到一半退出可能让设备配置停在半截状态。
            // 但「让用户点了退出却退不掉」是错的：以前这里 e.Cancel = true 并把窗口重新弹出来，
            // 点「退出」反而把窗口拉回前台，看起来就是「退出没反应」。现在改为：先挂住这次退出，
            // 等正在进行的操作收尾后再自动继续退出（见 ResumePendingExit），用户不需要再点一次。
            if (configBusy && !sessionEnding)
            {
                e.Cancel = true;
                pendingExit = true;
                dpiMessage = "正在完成配置读写，随之退出…";
                Invalidate();
                Program.Log("退出：配置读写进行中，挂起退出等待收尾。");
            }
            // 没有托盘时“退出”菜单也点不到，关闭窗口必须真的退出，否则会留下一个既无界面
            // 也无图标的进程，还一直占着单实例互斥锁，导致之后双击启动都没有反应。
            else if (!exiting && (tray == null || !tray.IsRegistered))
            {
                exiting = true;
                Program.Log("托盘：不可用，关闭窗口即退出。");
            }
            else if (!exiting) { e.Cancel = true; HideToTray(); }
            base.OnClosing(e);
        }

        // 挂起的退出：配置读写一收尾就自动接着退，不再要求用户重按一次。
        private bool pendingExit;

        internal void ResumePendingExit()
        {
            if (!pendingExit || IsDisposed) return;
            pendingExit = false;
            Program.Log("退出：配置读写已完成，继续退出。");
            exiting = true;
            Close();
        }
        protected override void OnClosed(EventArgs e)
        {
            StopDockFollow();
            IsDisposed = true; clock.Stop(); if (backdropActivation != null) backdropActivation.Stop();
            if (source != null) { source.RemoveHook(WindowMessage); source = null; }
            reader.Changed -= OnReaderChanged; charger.Changed -= OnReaderChanged;
            if (tray != null) { tray.Visible = false; tray.Dispose(); } if (trayMenu != null) trayMenu.Dispose(); if (trayIcon != null) trayIcon.Dispose();
            reader.Dispose(); charger.Dispose(); base.OnClosed(e);
            if (System.Windows.Application.Current != null) System.Windows.Application.Current.Shutdown();
        }
        private bool sessionEnding;
        internal void ShutdownForSession() { sessionEnding = true; exiting = true; }
        [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left, Right, Top, Bottom; }
        [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
        [DllImport("dwmapi.dll")] private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter,
            int x, int y, int width, int height, uint flags);
        private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004,
            SWP_NOACTIVATE = 0x0010, SWP_FRAMECHANGED = 0x0020;

        // 窗口圆角只有一个来源：DWM 的属性 33 = DWMWCP_ROUND，半径固定 8 DIP（125% 缩放下 10 设备像素），
        // 应用没有接口改成 24 那一档（ROUNDSMALL 是 4，比 8 还小）。
        // WPF 这边的 CornerRadius 必须跟着这个值走，否则两套弧不同心：外圈是 DWM 的 8 DIP 切边，
        // 内圈是 WPF 的描边弧，中间那条 Acrylic + 白色蒙层的三角区就把「两层圆角」显出来了。
        // 注意 Border.CornerRadius 只裁背景与描边，不裁 Child，所以半径改了描边位置也就跟着改了。
        internal const double WindowCornerRadius = 8;
    }
}

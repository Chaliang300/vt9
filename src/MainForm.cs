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
        private readonly NativeTrayIcon tray;
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
        private DispatcherTimer backdropActivation;
        private readonly Canvas surface = new Canvas();
        private CheckBox autoCheck, alertsCheck;
        private TextBlock autoStatusText, alertsStatusText;
        private Style buttonStyle;

        internal MainForm(bool hidden)
        {
            preferences = Preferences.Load(); autoStartOn = IsAutoStart();
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
            BuildTrayMenu();
            taskbarLight = IconArtwork.LightTaskbar();
            tray = new NativeTrayIcon { Text = "VT9 · 等待设备报告", ContextMenuStrip = trayMenu, Visible = true };
            tray.Click += delegate { ShowPanel(); };
            tray.MenuRequested += delegate { ShowTrayMenu(); };
            reader = new HidReader(Program.Log); charger = new ChargeMonitor();
            reader.Changed += OnReaderChanged; charger.Changed += OnReaderChanged;
            clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            clock.Tick += delegate
            {
                RefreshState();
                if (Program.Wake != null && Program.Wake.WaitOne(0)) ShowPanel();
            };
            clock.Start(); initializing = false; RefreshState();
            IsVisibleChanged += delegate
            {
                if (!IsVisible) return;
                RefreshState(); Invalidate(); ApplyBackdrop(); EnsureConfigLoaded();
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
        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            ApplyBackdrop();
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
                    if (!frameExtended)
                    {
                        source.CompositionTarget.BackgroundColor = Colors.Transparent;
                        Margins margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                        frameExtended = DwmExtendFrameIntoClientArea(source.Handle, ref margins) == 0;
                        int light = 0; DwmSetWindowAttribute(source.Handle, 20, ref light, 4);
                        int rounded = 2; DwmSetWindowAttribute(source.Handle, 33, ref rounded, 4);
                    }
                    int none = 1;
                    enabled = frameExtended && DwmSetWindowAttribute(source.Handle, 38, ref none, 4) == 0;
                }
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            Background = enabled ? Brushes.Transparent : Brush(Theme.WindowFallback);
            if (backdropActivation != null) backdropActivation.Stop();
            if (!enabled) { Program.Log("背景：原生 Acrylic 不可用，使用实色回退。"); return; }
            // Reproduce the working None -> Acrylic transition on separate compositor frames.
            // This one-shot runs only when showing/reconfiguring the window, never while dragging.
            backdropActivation = new DispatcherTimer(DispatcherPriority.ContextIdle) { Interval = TimeSpan.FromMilliseconds(250) };
            backdropActivation.Tick += delegate
            {
                backdropActivation.Stop();
                if (IsDisposed || source == null || !IsVisible) return;
                int acrylic = 3;
                int hr = DwmSetWindowAttribute(source.Handle, 38, ref acrylic, 4);
                if (hr != 0) Background = Brush(Theme.WindowFallback);
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
            if (message == 0x031E || message == 0x001A || message == 0x031A) Dispatcher.BeginInvoke(new System.Action(ApplyBackdrop));
            // 任务栏换深浅色后托盘图标必须立刻反色，否则会和任务栏同色看不见。
            if (message == 0x001A || message == 0x031A) Dispatcher.BeginInvoke(new System.Action(RefreshTaskbarTheme));
            return IntPtr.Zero;
        }
        protected override void OnStateChanged(EventArgs e)
        { base.OnStateChanged(e); if (WindowState == WindowState.Minimized) HideToTray(); }
        protected override void OnClosing(CancelEventArgs e)
        {
            if (configBusy && !sessionEnding) { e.Cancel = true; exiting = false; dpiMessage = "配置写入正在完成，请稍候再退出。"; ShowPanel(); }
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
        protected override void OnClosed(EventArgs e)
        {
            StopDockFollow();
            IsDisposed = true; clock.Stop(); if (backdropActivation != null) backdropActivation.Stop();
            if (source != null) { source.RemoveHook(WindowMessage); source = null; }
            reader.Changed -= OnReaderChanged; charger.Changed -= OnReaderChanged;
            tray.Visible = false; tray.Dispose(); trayMenu.Dispose(); if (trayIcon != null) trayIcon.Dispose();
            reader.Dispose(); charger.Dispose(); base.OnClosed(e);
            if (System.Windows.Application.Current != null) System.Windows.Application.Current.Shutdown();
        }
        private bool sessionEnding;
        internal void ShutdownForSession() { sessionEnding = true; exiting = true; }
        [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left, Right, Top, Bottom; }
        [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("dwmapi.dll")] private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);
    }
}

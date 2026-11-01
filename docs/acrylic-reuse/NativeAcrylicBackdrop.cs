using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace DesktopBackdrop
{
    // Attach on the UI thread before Window.Show(). Keep one instance per window.
    // Windows 11 Build 22621+ supports the requested material; other systems fall back.
    public sealed class NativeAcrylicBackdrop : IDisposable
    {
        private const int SystemBackdropType = 38;
        private const int None = 1, Acrylic = 3;
        private readonly Window window;
        private readonly DispatcherTimer activation;
        private readonly Action<string> log;
        private HwndSource source;
        private bool framePrepared, disposed;

        public NativeAcrylicBackdrop(Window window, Action<string> log = null)
        {
            if (window == null) throw new ArgumentNullException("window");
            window.Dispatcher.VerifyAccess();
            if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
                throw new InvalidOperationException("Attach the backdrop before the window handle is created.");
            if (window.AllowsTransparency)
                throw new InvalidOperationException("Use AllowsTransparency=false for this composition path.");
            this.window = window;
            this.log = log;
            window.Background = Brushes.Transparent;
            activation = new DispatcherTimer(DispatcherPriority.ContextIdle, window.Dispatcher);
            activation.Interval = TimeSpan.FromMilliseconds(250);
            activation.Tick += ActivateAcrylic;
            window.SourceInitialized += SourceInitialized;
            window.ContentRendered += ContentRendered;
            window.IsVisibleChanged += VisibilityChanged;
            window.Closed += Closed;
        }

        private void SourceInitialized(object sender, EventArgs e)
        {
            source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
            if (source == null) { Fallback(); return; }
            source.AddHook(WindowMessage);
            Rebind();
        }

        private void ContentRendered(object sender, EventArgs e) { Rebind(); }
        private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (window.IsVisible) Rebind();
            else activation.Stop();
        }

        // Only show/restore/theme changes call this method. Never call it from drag events,
        // rendering callbacks, mouse-move handlers or a repeating background-refresh timer.
        public void Rebind()
        {
            window.Dispatcher.VerifyAccess();
            activation.Stop();
            if (disposed || source == null || source.IsDisposed) return;
            try
            {
                bool composed;
                if (SystemParameters.HighContrast || DwmIsCompositionEnabled(out composed) != 0 || !composed)
                { Fallback(); return; }
                if (!framePrepared)
                {
                    source.CompositionTarget.BackgroundColor = Colors.Transparent;
                    Margins margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                    if (DwmExtendFrameIntoClientArea(source.Handle, ref margins) != 0)
                    { Fallback(); return; }
                    int light = 0;
                    DwmSetWindowAttribute(source.Handle, 20, ref light, sizeof(int));
                    framePrepared = true;
                }
                int none = None;
                if (DwmSetWindowAttribute(source.Handle, SystemBackdropType, ref none, sizeof(int)) != 0)
                { Fallback(); return; }
                window.Background = Brushes.Transparent;
                // Two separate dispatcher/compositor opportunities reproduce the transition
                // verified in VT9Status. 250ms is an empirical starting value, not an API rule.
                if (window.IsVisible) activation.Start();
            }
            catch (DllNotFoundException) { Fallback(); }
            catch (EntryPointNotFoundException) { Fallback(); }
        }

        private void ActivateAcrylic(object sender, EventArgs e)
        {
            activation.Stop();
            if (disposed || source == null || source.IsDisposed || !window.IsVisible) return;
            int acrylic = Acrylic;
            int hr = DwmSetWindowAttribute(source.Handle, SystemBackdropType, ref acrylic, sizeof(int));
            if (hr != 0) { Fallback(); return; }
            // Do not recreate the composition target or glass frame here.
            window.InvalidateVisual();
            if (log != null) log("Native Acrylic requested after None; verify the actual image.");
        }

        private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam, ref bool handled)
        {
            if (message == 0x031E) framePrepared = false; // WM_DWMCOMPOSITIONCHANGED
            if ((message == 0x031E || message == 0x001A || message == 0x031A) && !disposed)
                window.Dispatcher.BeginInvoke(new Action(Rebind));
            return IntPtr.Zero;
        }

        private void Fallback()
        {
            activation.Stop();
            window.Background = SystemParameters.HighContrast ? SystemColors.WindowBrush
                : new SolidColorBrush(Color.FromRgb(246, 248, 247));
            if (log != null) log("Native Acrylic unavailable; solid background selected.");
        }

        private void Closed(object sender, EventArgs e) { Dispose(); }
        public void Dispose()
        {
            window.Dispatcher.VerifyAccess();
            if (disposed) return;
            disposed = true;
            activation.Stop(); activation.Tick -= ActivateAcrylic;
            window.SourceInitialized -= SourceInitialized;
            window.ContentRendered -= ContentRendered;
            window.IsVisibleChanged -= VisibilityChanged;
            window.Closed -= Closed;
            if (source != null && !source.IsDisposed) source.RemoveHook(WindowMessage);
            source = null;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Margins { public int Left, Right, Top, Bottom; }
        [DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        [DllImport("dwmapi.dll")]
        private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);
    }
}

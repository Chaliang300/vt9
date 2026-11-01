using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace VT9Status
{
    // A persistent WPF message window owns the icon even while the main window is hidden.
    // Every Shell registration is checked; Explorer restarts and failed modifications recover.
    internal sealed class NativeTrayIcon : IDisposable
    {
        private const int Callback = 0x8001;
        private readonly HwndSource owner;
        private readonly int taskbarCreated;
        private readonly DispatcherTimer retry;
        private Icon icon;
        private string text = "VT9";
        private bool visible, disposed, registered, failureLogged;
        internal Forms.ContextMenuStrip ContextMenuStrip { get; set; }
        internal event EventHandler Click;
        internal bool IsRegistered { get { return registered; } }
        internal NativeTrayIcon()
        {
            HwndSourceParameters parameters = new HwndSourceParameters("VT9 tray message window");
            parameters.WindowStyle = unchecked((int)0x80000000);
            parameters.Width = parameters.Height = 0;
            owner = new HwndSource(parameters); owner.AddHook(Messages);
            taskbarCreated = RegisterWindowMessage("TaskbarCreated");
            retry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            retry.Tick += delegate { EnsureRegistered(); }; retry.Start();
        }
        internal string Text
        {
            get { return text; }
            set { if (text == value) return; text = value; EnsureRegistered(); }
        }
        internal Icon Icon
        {
            get { return icon; }
            set { icon = value; EnsureRegistered(); }
        }
        internal bool Visible
        {
            get { return visible; }
            set
            {
                visible = value;
                if (visible) EnsureRegistered();
                else if (!disposed) { Data data = MakeData(); Shell_NotifyIcon(2, ref data); registered = false; }
            }
        }
        private Data MakeData()
        {
            return new Data { Size = Marshal.SizeOf(typeof(Data)), Window = owner.Handle, Id = 1,
                Flags = 1 | 2 | 4, Message = Callback, Icon = icon == null ? IntPtr.Zero : icon.Handle,
                Tip = text.Length > 127 ? text.Substring(0, 127) : text, Guid = Guid.Empty,
                Info = "", InfoTitle = "" };
        }
        internal void EnsureRegistered()
        {
            if (disposed || !visible || icon == null) return;
            Data data = MakeData();
            if (registered && Shell_NotifyIcon(1, ref data)) return;
            bool added = Shell_NotifyIcon(0, ref data);
            int error = added ? 0 : Marshal.GetLastWin32Error();
            if (!added) { added = Shell_NotifyIcon(1, ref data); if (!added) error = Marshal.GetLastWin32Error(); }
            registered = added;
            if (added)
            {
                failureLogged = false;
                Program.Log("托盘：Shell_NotifyIcon 注册成功，HWND=" + owner.Handle);
                Identifier id = new Identifier { Size = Marshal.SizeOf(typeof(Identifier)), Window = owner.Handle, Id = 1, Guid = Guid.Empty };
                Rect rect; int result = Shell_NotifyIconGetRect(ref id, out rect);
                Program.Log("托盘：图标位置查询 HRESULT=" + result + "，位置=" + rect.Left + "," + rect.Top + "," + rect.Right + "," + rect.Bottom);
            }
            else if (!failureLogged)
            {
                // 失败只记一次：Text 每秒都在变，否则日志会被同一行刷满。
                failureLogged = true;
                // 错误 5 = 拒绝访问。任务栏属于 Explorer，进程若被降到更低完整性级别
                // （沙箱 / 受限环境启动），Shell_NotifyIcon 会被 UIPI 拦下，与图标本身无关。
                Program.Log("托盘：Shell_NotifyIcon 注册失败，Win32=" + error + (error == 5
                    ? "（拒绝访问：本进程完整性级别低于任务栏，通常是受限环境启动所致）" : "") + "，HWND=" + owner.Handle);
            }
        }
        // 图标在任务栏里的实际位置：诊断截图用它定位，不靠猜坐标。
        internal bool TryGetRect(out Rectangle rect)
        {
            rect = Rectangle.Empty;
            if (disposed || !registered) return false;
            Identifier id = new Identifier { Size = Marshal.SizeOf(typeof(Identifier)), Window = owner.Handle, Id = 1, Guid = Guid.Empty };
            Rect area;
            if (Shell_NotifyIconGetRect(ref id, out area) != 0) return false;
            rect = new Rectangle(area.Left, area.Top, area.Right - area.Left, area.Bottom - area.Top);
            return true;
        }

        internal void ShowBalloonTip(int duration, string title, string message, Forms.ToolTipIcon kind)        {
            EnsureRegistered(); if (!registered) return;
            Data data = MakeData(); data.Flags |= 0x10; data.InfoTitle = title; data.Info = message;
            data.Timeout = duration; data.InfoFlags = kind == Forms.ToolTipIcon.Warning ? 2 : 1;
            Shell_NotifyIcon(1, ref data);
        }
        private IntPtr Messages(IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam, ref bool handled)
        {
            if (message == taskbarCreated) { registered = false; EnsureRegistered(); }
            if (message == Callback)
            {
                int action = lparam.ToInt32() & 0xffff;
                if (action == 0x202 && Click != null) Click(this, EventArgs.Empty);
                if (action == 0x205 && ContextMenuStrip != null)
                {
                    SetForegroundWindow(owner.Handle);
                    ContextMenuStrip.Show(Forms.Cursor.Position);
                }
                handled = true;
            }
            return IntPtr.Zero;
        }
        public void Dispose()
        {
            if (disposed) return;
            Visible = false; retry.Stop(); disposed = true; owner.RemoveHook(Messages); owner.Dispose();
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Data
        {
            public int Size; public IntPtr Window; public int Id, Flags, Message; public IntPtr Icon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
            public int State, StateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
            public int Timeout;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
            public int InfoFlags; public Guid Guid; public IntPtr BalloonIcon;
        }
        [StructLayout(LayoutKind.Sequential)] private struct Identifier
        { public int Size; public IntPtr Window; public int Id; public Guid Guid; }
        [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Shell_NotifyIcon(int operation, ref Data data);
        [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref Identifier id, out Rect rect);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessage(string message);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    }
}

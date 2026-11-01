using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.Win32;

namespace VT9Status
{
    internal sealed partial class MainForm
    {
        private void OnReaderChanged()
        {
            // Device reports drive the surface directly; the one-second clock only maintains
            // age, expiry and single-instance activation.
            if (exiting || IsDisposed || Dispatcher.HasShutdownStarted || Interlocked.Exchange(ref updateQueued, 1) != 0) return;
            try
            {
                Dispatcher.BeginInvoke(new System.Action(delegate
                {
                    Interlocked.Exchange(ref updateQueued, 0);
                    if (!exiting && !IsDisposed) RefreshState();
                }));
            }
            catch (InvalidOperationException) { Interlocked.Exchange(ref updateQueued, 0); }
        }

        internal void RefreshState()
        {
            snapshot = reader.Snapshot();
            charge = charger.Snapshot();
            Reading shown = snapshot.DisplayReading;

            bool fromQuery = charge.Last != null && (shown == null || charge.Last.ReceivedUtc > shown.ReceivedUtc);
            level = fromQuery ? (int?)charge.Last.Battery : shown == null ? (int?)null : shown.Battery;
            batteryFresh = fromQuery ? charge.Fresh : snapshot.Fresh;

            deviceLine = snapshot.State;
            // 状态胶囊已从界面移除；statusLabel 仍参与重绘签名，避免状态变化时不刷新。
            if (snapshot.Fresh) statusLabel = "接收中";
            else if (!snapshot.Open) statusLabel = "未连接";
            else if (shown != null) statusLabel = "等待更新";
            else statusLabel = "等待上报";

            chargeStatus = charge.Text;

            bool chargingNow = charge.Fresh && charge.Last != null && charge.Last.Charging;
            bool queryConnected = charge.Fresh && charge.Last != null;
            bool connected = snapshot.Open || queryConnected;
            bool wired = snapshot.Open ? deviceLine.IndexOf("有线", StringComparison.Ordinal) >= 0
                : queryConnected && charge.Last.ProductId == 0x4191;
            TrayState trayState = IconArtwork.ResolveTrayState(connected, chargingNow, wired);
            int traySize = trayPixelSize;   // 已在构造时读一次注册表，DPI 变化时（WM_DPICHANGED）重算
            string key = IconArtwork.TrayKey(traySize, trayState, level, taskbarLight);
            if (key != iconKey)
            {
                UpdateTrayIcon(traySize, trayState, level);
                iconKey = key;
            }

            string tooltip = snapshot.Fresh
                ? "VT9 · " + snapshot.Last.Dpi + " DPI · 电量 " + snapshot.Last.Battery + "%"
                : shown == null ? "VT9 · 尚未收到电量，等待设备报告"
                : "VT9 · 上次电量 " + shown.Battery + "% · " + shown.ReceivedUtc.ToLocalTime().ToString("HH:mm:ss") + " · 等待更新";
            if (fromQuery && level.HasValue) tooltip = "VT9 · 电量 " + level.Value + "% · " + charge.Text;
            if (tray != null) tray.Text = tooltip.Length > 63 ? tooltip.Substring(0, 63) : tooltip;

            if (batteryFresh && level.HasValue && level.Value > 20) notified = false;
            if (batteryFresh && level.HasValue && level.Value <= 20 && !notified && preferences.LowBatteryNotification)
            {
                notified = true;
                if (tray != null) tray.ShowBalloonTip(5000, "VT9 电量较低", "当前剩余电量 " + level.Value + "%，请按需充电。", ToolTipIcon.Warning);
            }

            // Repaint only when something the surface shows actually changed: repainting the whole
            // window once a second costs far more than the timestamp is worth.
            string signature = statusLabel + "|" + deviceLine + "|" + chargeStatus + "|" +
                level + "|" + batteryFresh + "|" + (config == null ? "-" : config.Summary()) + "|" +
                draftActive + "|" + DraftsDirty + "|" + dpiMessage + "|" + autoStartOn + "|" +
                preferences.LowBatteryNotification + "|" +
                (charge.Fresh && charge.Last != null && charge.Last.Charging);
            if (signature != lastSignature)
            {
                lastSignature = signature;
                if (IsVisible) Invalidate();
            }
        }

        // Rebuild the ring on each actual percentage, mode, theme or DPI change.
        private void UpdateTrayIcon(int size, TrayState state, int? level)
        {
            if (tray == null) return;   // 托盘还没建好（首帧之后的延迟初始化）
            using (Bitmap bitmap = IconArtwork.Tray(size, state, level, taskbarLight))
            {
                IntPtr handle = bitmap.GetHicon();
                Icon next;
                try { using (Icon borrowed = System.Drawing.Icon.FromHandle(handle)) next = (Icon)borrowed.Clone(); }
                finally { Native.DestroyIcon(handle); }
                Icon previous = trayIcon;
                trayIcon = next;
                tray.Icon = next;
                if (previous != null) previous.Dispose();
            }
        }

        private void RefreshTrayAfterWrite()
        {
            if (IsDisposed) return;
            iconKey = "";
            RefreshState();
        }

        private void RefreshTaskbarTheme()
        {
            if (IsDisposed) return;
            bool light = IconArtwork.LightTaskbar();
            if (light == taskbarLight) return;
            taskbarLight = light; iconKey = "";
            Program.Log("托盘：任务栏切换为" + (light ? "浅色" : "深色") + "，图标重新着色。");
            RefreshState();
        }

        private void HideToTray()
        {
            if (tray == null) return;   // 托盘还没建好，等同不可用
            tray.EnsureRegistered();
            if (!tray.IsRegistered)
            {
                WindowState = System.Windows.WindowState.Normal;
                dpiMessage = "托盘图标不可用，已保留窗口；关闭窗口即退出。";
                Invalidate();
                // 点一次“收起”就记一行会刷满日志（连点几次就是几行），这里只记第一次。
                if (!trayHintLogged)
                {
                    trayHintLogged = true;
                    Program.Log("托盘：注册未成功，取消隐藏主窗口（托盘不可用时关闭窗口即退出）。");
                }
                return;
            }
            trayHintLogged = false;
            Program.Log("托盘：图标已注册，收起主窗口。");
            Hide();
        }

        internal bool TryTrayRect(out Rectangle rect)
        {
            rect = Rectangle.Empty;
            return tray != null && tray.TryGetRect(out rect);
        }

        private void ShowPanel()
        {
            if (IsDisposed) return;
            if (!IsVisible) Show();
            WindowState = System.Windows.WindowState.Normal;
            Activate();
            EnsureForeground();
            RefreshState();
            Invalidate();
        }

        // 从托盘恢复时窗口常常「显示出来了但不是活动窗口」——Activate() 会被前台锁忽略。
        // 抢前台的目的只是让窗口出现在最前面、键盘焦点落回来；背景材质不再依赖这一点
        // （实测 DWM 对非活动窗口同样合成 Acrylic，之前按焦点切实色兜底反而造成了灰板）。
        private void EnsureForeground()
        {
            if (source == null) return;
            IntPtr hwnd = source.Handle;
            if (hwnd == IntPtr.Zero || GetForegroundWindow() == hwnd) return;
            try
            {
                bool topmost = Topmost;
                Topmost = true; Topmost = false;
                if (topmost) Topmost = true;
                SetForegroundWindow(hwnd);
                if (GetForegroundWindow() != hwnd) ForceForeground(hwnd);
                Program.Log(GetForegroundWindow() == hwnd
                    ? "窗口：从托盘恢复并已取得前台。"
                    : "窗口：恢复后仍非前台（前台锁拒绝），窗口已置于最前。");
            }
            catch (Exception ex) { Program.Log("窗口：获取前台失败，" + ex.Message); }
            SetBackdropSurface();   // 材质过渡若尚未生效，这里继续用兜底色
        }

        // SetForegroundWindow 被前台锁挡住时的兜底：把当前前台线程的输入队列临时接过来，
        // 抢到之后再解除。托盘应用恢复窗口基本都要走这一手，否则窗口会以非活动状态出现。
        private static void ForceForeground(IntPtr hwnd)
        {
            IntPtr foreground = GetForegroundWindow();
            uint pid;
            uint foregroundThread = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, out pid);
            uint self = GetCurrentThreadId();
            bool attached = foregroundThread != 0 && foregroundThread != self
                && AttachThreadInput(foregroundThread, self, true);
            try
            {
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached) AttachThreadInput(foregroundThread, self, false);
            }
        }

        private void ExitApp()
        {
            Program.Log("退出：收到退出请求，开始关闭。");
            exiting = true;
            Close();
        }

        private static string StartupCommand()
        {
            return "\"" + Application.ExecutablePath + "\" --tray";
        }

        internal static string StartupShortcutPath
        {
            get
            {
                string dir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                return Path.Combine(dir, "VT9Status.lnk");
            }
        }

        internal static string LegacyStartupShortcutPath
        {
            get
            {
                string dir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                return Path.Combine(dir, "雷柏 VT9 控制台.lnk");
            }
        }

        internal static bool TrySetRegistryAutoStart(string command)
        {
            try
            {
                // 优先以写权限打开已有 Run 键；如果打开失败再尝试 CreateSubKey
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (key != null)
                    {
                        key.SetValue(RunName, command);
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Program.Log("自启动：写入注册表被拒绝或失败（" + ex.Message + "），将回退至启动文件夹。");
            }
            return false;
        }

        internal static void TryRemoveRegistryAutoStart()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key != null)
                    {
                        key.DeleteValue(RunName, false);
                    }
                }
            }
            catch (Exception ex)
            {
                Program.Log("自启动：从注册表删除失败（" + ex.Message + "），忽略此错误。");
            }
        }

        internal static bool IsRegistryAutoStartConfigured()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (key == null) return false;
                    string val = key.GetValue(RunName) as string;
                    if (string.IsNullOrEmpty(val)) return false;
                    if (String.Equals(val, StartupCommand(), StringComparison.OrdinalIgnoreCase)) return true;
                    // 路径容错：只要指向当前同一可执行文件名并带 --tray，亦视为已开启
                    if (val.IndexOf("VT9Status", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        val.IndexOf("--tray", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                    return false;
                }
            }
            catch { return false; }
        }

        internal static bool CreateShortcut(string lnkPath, string target, string arguments, string dir, string description)
        {
            try
            {
                IShellLinkW link = (IShellLinkW)new ShellLink();
                link.SetPath(target);
                if (!string.IsNullOrEmpty(arguments)) link.SetArguments(arguments);
                if (!string.IsNullOrEmpty(dir)) link.SetWorkingDirectory(dir);
                if (!string.IsNullOrEmpty(description)) link.SetDescription(description);
                ((IPersistFile)link).Save(lnkPath, true);
                return File.Exists(lnkPath);
            }
            catch (Exception ex)
            {
                Program.Log("自启动：创建快捷方式失败（" + lnkPath + "）：" + ex.Message);
                return false;
            }
        }

        internal static string ReadShortcutTarget(string lnkPath)
        {
            try
            {
                if (!File.Exists(lnkPath)) return null;
                IShellLinkW link = (IShellLinkW)new ShellLink();
                ((IPersistFile)link).Load(lnkPath, 0);
                StringBuilder sb = new StringBuilder(260);
                link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
                return sb.ToString();
            }
            catch (Exception ex)
            {
                Program.Log("自启动：解析快捷方式失败（" + lnkPath + "）：" + ex.Message);
                return null;
            }
        }

        internal static bool CreateStartupShortcut()
        {
            string target = Application.ExecutablePath;
            return CreateShortcut(StartupShortcutPath, target, "--tray", Path.GetDirectoryName(target), "雷柏 VT9 控制台");
        }

        internal static void RemoveStartupShortcut()
        {
            string[] paths = { StartupShortcutPath, LegacyStartupShortcutPath };
            foreach (string p in paths)
            {
                try
                {
                    if (File.Exists(p)) File.Delete(p);
                }
                catch (Exception ex)
                {
                    Program.Log("自启动：删除启动快捷方式失败（" + p + "）：" + ex.Message);
                }
            }
        }

        internal static bool IsStartupShortcutConfigured()
        {
            string[] paths = { StartupShortcutPath, LegacyStartupShortcutPath };
            string currentExe = Application.ExecutablePath;
            foreach (string path in paths)
            {
                if (!File.Exists(path)) continue;
                string target = ReadShortcutTarget(path);
                if (target == null) continue;
                if (target.Length == 0) return true;
                if (String.Equals(target, currentExe, StringComparison.OrdinalIgnoreCase)) return true;
                if (String.Equals(Path.GetFileName(target), Path.GetFileName(currentExe), StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        internal static bool IsAutoStart()
        {
            return IsRegistryAutoStartConfigured() || IsStartupShortcutConfigured();
        }

        private void ApplyAutoStart(bool on)
        {
            if (initializing) return;
            try
            {
                if (on)
                {
                    // 优先尝试注册表方式
                    bool regOk = TrySetRegistryAutoStart(StartupCommand());
                    if (regOk)
                    {
                        RemoveStartupShortcut();
                        Program.Log("自启动：已通过注册表 CurrentVersion\\Run 成功配置。");
                    }
                    else
                    {
                        // 注册表写入受限（被杀软拦截等），自动平滑降级至启动文件夹快捷方式
                        bool lnkOk = CreateStartupShortcut();
                        if (lnkOk)
                        {
                            Program.Log("自启动：已自动回退并通过启动文件夹快捷方式成功配置。");
                        }
                        else
                        {
                            throw new InvalidOperationException("注册表与启动文件夹均无法写入，请检查安全软件设置。");
                        }
                    }
                }
                else
                {
                    // 取消自启动：两处同时清理
                    TryRemoveRegistryAutoStart();
                    RemoveStartupShortcut();
                    Program.Log("自启动：已关闭并清理注册表与启动快捷方式。");
                }
            }
            catch (Exception ex)
            {
                Program.Log("自启动设置失败：" + ex.Message);
                MessageBox.Show("无法保存自启动设置：" + ex.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                autoStartOn = IsAutoStart();
                if (autoCheck != null) autoCheck.IsChecked = autoStartOn;
                if (trayAutoStart != null) trayAutoStart.Checked = autoStartOn;
                Invalidate();
            }
        }

        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLink { }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
            void Resolve(IntPtr hwnd, uint fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        // 抢前台用（见 ForceForeground）
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attachTo, uint attachFrom, bool attach);
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);
    }
}

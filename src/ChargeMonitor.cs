using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace VT9Status
{
    internal sealed class ChargeReading
    {
        internal bool Charging;
        internal int Battery;
        internal ushort ProductId;
        internal DateTime ReceivedUtc;
        internal string Hex;
        internal string Text { get { return Charging ? "充电中" : "未充电"; } }

        internal static bool TryParse(byte[] bytes, out ChargeReading result)
        {
            result = null;
            if (bytes == null || bytes.Length < 3) return false;
            int offset = bytes[0] == 0xBA ? 1 : 0;
            if (bytes.Length < offset + 3 || bytes[offset] != 1 || (bytes[offset + 1] != 1 && bytes[offset + 1] != 2) || bytes[offset + 2] > 100) return false;
            result = new ChargeReading { Charging = bytes[offset + 1] == 2, Battery = bytes[offset + 2], ReceivedUtc = DateTime.UtcNow,
                Hex = BitConverter.ToString(bytes, 0, offset + 3).Replace('-', ' ') };
            return true;
        }
    }

    internal sealed class ChargeSnapshot
    {
        internal ChargeReading Last;
        internal bool Current;
        internal bool Fresh { get { return Current && Last != null && (DateTime.UtcNow - Last.ReceivedUtc).TotalSeconds < 45; } }
        internal string Text { get { return Last == null ? "未知（等待设备回复）" : Fresh ? Last.Text : "上次" + Last.Text + "，等待更新"; } }
    }

    internal sealed class ChargeMonitor : IDisposable
    {
        private readonly ManualResetEvent stop = new ManualResetEvent(false);
        private readonly AutoResetEvent refresh = new AutoResetEvent(false);
        private readonly object gate = new object();
        private readonly Thread worker;
        private ChargeReading last;
        private bool current;
        private long silentSince;
        internal event Action Changed;

        internal ChargeMonitor()
        {
            worker = new Thread(Run) { IsBackground = true, Name = "VT9 charge state" };
            worker.Start();
        }
        internal ChargeSnapshot Snapshot() { lock (gate) return new ChargeSnapshot { Last = last, Current = current }; }
        internal void RequestRefresh() { refresh.Set(); }
        private void Run()
        {
            while (!stop.WaitOne(0))
            {
                ChargeReading reading = null;
                try { lock (ConfigChannelLock.Gate) reading = Query(); }
                catch (Exception ex) { Program.Log("充电状态查询失败：" + ex.Message); }
                if (reading == null && (Stopwatch.GetTimestamp() - silentSince) / (double)Stopwatch.Frequency >= 300)
                {
                    // A sleeping mouse ignores the query; say so once every five minutes instead
                    // of silently leaving the charge state stale with no explanation.
                    silentSince = Stopwatch.GetTimestamp();
                    Program.Log("充电状态查询：设备未答复，稍后重试（鼠标可能处于休眠）。");
                }
                lock (gate) { current = reading != null; if (reading != null) last = reading; }
                Action handler = Changed;
                if (handler != null) handler();
                if (WaitHandle.WaitAny(new WaitHandle[] { stop, refresh }, 15000) == 0) break;
            }
        }
        private ChargeReading Query()
        {
            ushort product;
            string path = Native.FindChargeCollection(out product);
            if (path == null) return null;
            using (SafeFileHandle h = Native.CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
            {
                if (h.IsInvalid) return null;
                // Read-only battery command 0xAA. No configuration or EEPROM writes.
                byte[] query = new byte[32]; query[0] = 0xBA; query[1] = product == 0x4191 ? (byte)0xFF : (byte)0xA5; query[2] = 0xAA;
                for (int attempt = 0; attempt < 2 && !stop.WaitOne(0); attempt++)
                {
                    uint written;
                    if (!WriteFile(h, query, 32, out written, IntPtr.Zero) || written != 32) return null;
                    for (int poll = 0; poll < 3; poll++)
                    {
                        if (stop.WaitOne(50)) return null;
                        byte[] answer = new byte[32]; answer[0] = 0xBA;
                        ChargeReading reading;
                        if (HidD_GetInputReport(h, answer, answer.Length) && ChargeReading.TryParse(answer, out reading))
                        { reading.ProductId = product; return reading; }
                    }
                }
            }
            return null;
        }
        public void Dispose()
        {
            stop.Set();
            if (worker.Join(4000)) { stop.Dispose(); refresh.Dispose(); }
            else Program.Log("充电状态线程未在 4 秒内退出，句柄留待进程结束时释放。");
        }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteFile(SafeFileHandle h, byte[] buffer, uint length, out uint written, IntPtr overlapped);
        [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetInputReport(SafeFileHandle h, [In, Out] byte[] report, int length);
    }
}

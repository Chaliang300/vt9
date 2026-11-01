using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace VT9Status
{
    internal sealed class Reading
    {
        public int Dpi;
        public int Battery;
        public DateTime ReceivedUtc;
        public string Hex;
        public long Sequence;
        public double? IntervalMilliseconds;
        public ushort ProductId;

        public static bool TryParse(byte[] bytes, out Reading result, ushort productId = 0x1849)
        {
            result = null;
            if (bytes == null || bytes.Length != 7 || bytes[0] != 0xBB || bytes[1] != 0xB0) return false;
            int dpi = bytes[3] | bytes[4] << 8;
            if (dpi == 0 || bytes[6] > 100) return false;
            result = new Reading { Dpi = dpi, Battery = bytes[6], ReceivedUtc = DateTime.UtcNow,
                ProductId = productId,
                Hex = BitConverter.ToString(bytes).Replace('-', ' ') };
            return true;
        }
    }

    internal sealed class DeviceSnapshot
    {
        public Reading Last;
        public Reading LastKnown;
        public bool Open;
        public string State = "正在查找接收器";
        public long Packets;
        public int Connections;
        public double Age = double.PositiveInfinity;
        public bool Checking;
        public string CheckStatus;
        public double CheckElapsed;
        public bool Fresh { get { return Open && Last != null && Age < 15; } }
        public Reading DisplayReading { get { return Last ?? LastKnown; } }
    }

    internal sealed class HidReader : IDisposable
    {
        // Silence longer than this rebuilds the interface once as a health check. Reports are
        // passive; an idle device legitimately sends nothing for hours.
        private const double IdleHealthCheckSeconds = 1800;
        private readonly object gate = new object();
        private readonly ManualResetEvent stop = new ManualResetEvent(false);
        private readonly AutoResetEvent reconnect = new AutoResetEvent(false);
        private int requestedGeneration;
        private bool checking;
        private long checkStarted;
        private string checkStatus = "点击手动检查，重新连接并等待新报告。";
        private readonly Thread worker;
        private Reading last;
        private Reading lastKnown;
        private bool opened;
        private string state = "正在查找接收器";
        private long packets, receivedTick;
        private int connections;
        private readonly Action<string> log;
        internal event Action Changed;

        public HidReader(Action<string> logger)
        {
            log = logger;
            worker = new Thread(Run) { IsBackground = true, Name = "VT9 HID reader" };
            worker.Start();
        }

        public DeviceSnapshot Snapshot()
        {
            lock (gate) return new DeviceSnapshot { Last = last, LastKnown = lastKnown, Open = opened, State = state,
                Packets = packets, Connections = connections,
                Checking = checking, CheckStatus = checkStatus,
                CheckElapsed = checkStarted == 0 ? 0 : (Stopwatch.GetTimestamp() - checkStarted) / (double)Stopwatch.Frequency,
                Age = lastKnown == null ? double.PositiveInfinity : (Stopwatch.GetTimestamp() - receivedTick) / (double)Stopwatch.Frequency };
        }

        internal void ReconnectForDeviceChange()
        {
            lock (gate) { requestedGeneration++; reconnect.Set(); }
        }

        internal bool RequestManualCheck()
        {
            lock (gate)
            {
                if (checking || stop.WaitOne(0)) return false;
                checking = true;
                checkStarted = Stopwatch.GetTimestamp();
                checkStatus = "正在重新连接，等待设备的新报告…";
                requestedGeneration++;
                reconnect.Set();
            }
            NotifyChanged();
            return true;
        }

        private bool Superseded(int generation)
        {
            lock (gate) return generation != requestedGeneration;
        }

        // Retry delay after N consecutive "receiver absent" enumerations. Split out so the
        // back-off is covered by the self-test instead of only by field observation.
        internal static int BackoffMilliseconds(int missingStreak)
        {
            if (missingStreak < 1) return 3000;
            return Math.Min(30000, 3000 * (1 << Math.Min(missingStreak - 1, 4)));
        }

        private void CheckTimeout()
        {
            bool expired = false;
            lock (gate)
            {
                if (checking && (Stopwatch.GetTimestamp() - checkStarted) / (double)Stopwatch.Frequency >= 30)
                {
                    checking = false;
                    checkStatus = "检查超时：30秒内未收到新报告，请唤醒鼠标或检查接收器。";
                    expired = true;
                }
            }
            if (expired) NotifyChanged();
        }

        private void NotifyChanged()
        {
            Action handler = Changed;
            if (handler != null) handler();
        }

        private void SetState(bool isOpen, string message)
        {
            bool changed;
            lock (gate) { changed = state != message; opened = isOpen; state = message; }
            if (changed) { log(message); NotifyChanged(); }
        }

        private void Run()
        {
            int missingStreak = 0;
            while (!stop.WaitOne(0))
            {
                CheckTimeout();
                int generation, waitMs = 3000;
                lock (gate) { reconnect.Reset(); generation = requestedGeneration; }
                try
                {
                    ushort productId;
                    string path = Native.FindStatusCollection(out productId);
                    if (path == null)
                    {
                        // Re-enumerating all HID interfaces every 3 s while the receiver is absent
                        // only burns CPU; back off. An arrival notification still wakes us at once.
                        missingStreak++;
                        waitMs = BackoffMilliseconds(missingStreak);
                        SetState(false, "未找到接收器，正在自动重试");
                    }
                    else
                    {
                        missingStreak = 0;
                        using (SafeFileHandle handle = Native.CreateFile(path, 0x80000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero))
                        {
                            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法打开状态接口");
                            lock (gate) { connections++; last = null; }
                            SetState(true, "接口已连接（" + (productId == 0x4191 ? "USB 有线" : "2.4G 接收器") + "）");
                            Receive(handle, generation, productId);
                        }
                    }
                }
                catch (Exception ex) { SetState(false, "读取中断，将自动重连：" + ex.Message); }
                CheckTimeout();
                if (Superseded(generation)) continue;
                if (WaitHandle.WaitAny(new WaitHandle[] { stop, reconnect }, waitMs) == 0) break;
            }
        }

        private void Receive(SafeFileHandle handle, int generation, ushort productId)
        {
            IntPtr buffer = Marshal.AllocHGlobal(7);
            IntPtr overlap = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Native.Overlapped)));
            IntPtr done = Native.CreateEvent(IntPtr.Zero, true, false, null);
            bool pending = false;
            long lastReportTick = Stopwatch.GetTimestamp();
            try
            {
                if (done == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                // Flush once at connection so queued reports are not presented as fresh.
                if (!Native.HidD_FlushQueue(handle)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法清空旧报告");
                while (!stop.WaitOne(0))
                {
                    if (Superseded(generation)) return;
                    Native.ResetEvent(done);
                    Marshal.StructureToPtr(new Native.Overlapped { Event = done }, overlap, false);
                    bool immediate = Native.ReadFile(handle, buffer, 7, IntPtr.Zero, overlap);
                    if (!immediate && Marshal.GetLastWin32Error() != 997) throw new Win32Exception(Marshal.GetLastWin32Error());
                    pending = true;
                    // A pending read stays pending across timeouts; no device polling occurs.
                    while (!immediate)
                    {
                        uint wait = Native.WaitForSingleObject(done, 1000);
                        if (wait == 0) break;
                        if (wait != 258) throw new Win32Exception(Marshal.GetLastWin32Error());
                        if (stop.WaitOne(0)) return;
                        CheckTimeout();
                        if (Superseded(generation)) return;
                        if ((Stopwatch.GetTimestamp() - lastReportTick) / (double)Stopwatch.Frequency >= IdleHealthCheckSeconds)
                        {
                            // An idle mouse stops reporting entirely, and the pending overlapped
                            // read resumes by itself when it wakes, so reconnecting after a short
                            // silence only churns the interface (measured ~1.35 reconnects/min
                            // while idle). Keep one long health check for a wedged driver.
                            SetState(false, "空闲超过 " + (int)(IdleHealthCheckSeconds / 60) + " 分钟，重建状态接口");
                            return;
                        }
                    }
                    uint count;
                    bool success = Native.GetOverlappedResult(handle, overlap, out count, false);
                    int error = Marshal.GetLastWin32Error();
                    pending = false; // completion was observed, including removal errors
                    if (!success) throw new Win32Exception(error);
                    CheckTimeout();
                    byte[] bytes = new byte[count];
                    Marshal.Copy(buffer, bytes, 0, bytes.Length);
                    Reading reading;
                    if (!Reading.TryParse(bytes, out reading, productId)) continue;
                    lastReportTick = Stopwatch.GetTimestamp();
                    lock (gate)
                    {
                        if (generation != requestedGeneration) return;
                        long now = Stopwatch.GetTimestamp();
                        reading.IntervalMilliseconds = last == null ? (double?)null : (now - receivedTick) * 1000.0 / Stopwatch.Frequency;
                        reading.Sequence = ++packets;
                        last = reading;
                        lastKnown = reading;
                        receivedTick = now;
                        if (checking)
                        {
                            checking = false;
                            checkStatus = "检查成功：" + reading.ReceivedUtc.ToLocalTime().ToString("HH:mm:ss") + " · DPI " + reading.Dpi + " · 电量 " + reading.Battery + "%";
                        }
                    }
                    SetState(true, "正在接收设备状态");
                    NotifyChanged();
                }
            }
            finally
            {
                // Do not free the OVERLAPPED/buffer before cancelled I/O actually completes.
                if (pending)
                {
                    Native.CancelIoEx(handle, overlap);
                    uint ignored;
                    Native.GetOverlappedResult(handle, overlap, out ignored, true);
                }
                if (done != IntPtr.Zero) Native.CloseHandle(done);
                Marshal.FreeHGlobal(overlap);
                Marshal.FreeHGlobal(buffer);
            }
        }

        public void Dispose()
        {
            stop.Set();
            // Worker owns native buffers and releases them after cancellation completes.
            if (worker.Join(4000)) { stop.Dispose(); reconnect.Dispose(); }
            else if (log != null) log("状态读取线程未在 4 秒内退出，句柄留待进程结束时释放。");
        }
    }

    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct InterfaceData
        { public int Size; public Guid ClassGuid; public int Flags; public IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)] internal struct Attributes
        { public int Size; public ushort Vendor, Product, Version; }
        [StructLayout(LayoutKind.Sequential)] internal struct Caps
        {
            public ushort Usage, UsagePage, InputLength, OutputLength, FeatureLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort Links, InputButtons, InputValues, InputIndices, OutputButtons, OutputValues,
                OutputIndices, FeatureButtons, FeatureValues, FeatureIndices;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct Overlapped
        { public UIntPtr Internal, InternalHigh; public uint Offset, OffsetHigh; public IntPtr Event; }

        internal static string FindStatusCollection(out ushort selectedProduct)
        { return FindCollection(2, 7, out selectedProduct); }

        internal static string FindChargeCollection(out ushort selectedProduct)
        { return FindCollection(14, 32, out selectedProduct); }

        private static string FindCollection(ushort usage, ushort inputLength, out ushort selectedProduct)
        {
            selectedProduct = 0;
            string wireless = null;
            Guid guid;
            HidD_GetHidGuid(out guid);
            IntPtr list = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, 0x12);
            if (list == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                for (uint index = 0; ; index++)
                {
                    InterfaceData item = new InterfaceData { Size = Marshal.SizeOf(typeof(InterfaceData)) };
                    if (!SetupDiEnumDeviceInterfaces(list, IntPtr.Zero, ref guid, index, ref item))
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error == 259) break;
                        throw new Win32Exception(error);
                    }
                    uint size;
                    SetupDiGetDeviceInterfaceDetail(list, ref item, IntPtr.Zero, 0, out size, IntPtr.Zero);
                    if (size < 6 || size > 65536) continue;
                    IntPtr detail = Marshal.AllocHGlobal((int)size);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(list, ref item, detail, size, out size, IntPtr.Zero)) continue;
                        string path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                        using (SafeFileHandle h = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
                        {
                            if (h.IsInvalid) continue;
                            Attributes attributes = new Attributes { Size = Marshal.SizeOf(typeof(Attributes)) };
                            if (!HidD_GetAttributes(h, ref attributes) || attributes.Vendor != 0x24AE || (attributes.Product != 0x1849 && attributes.Product != 0x4191)) continue;
                            IntPtr preparsed;
                            if (!HidD_GetPreparsedData(h, out preparsed)) continue;
                            try
                            {
                                Caps caps;
                                if (HidP_GetCaps(preparsed, out caps) == 0x00110000 && caps.UsagePage == 0xFF00 && caps.Usage == usage && caps.InputLength == inputLength)
                                {
                                    if (attributes.Product == 0x4191) { selectedProduct = 0x4191; return path; }
                                    wireless = path;
                                }
                            }
                            finally { HidD_FreePreparsedData(preparsed); }
                        }
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                }
                if (wireless != null) selectedProduct = 0x1849;
                return wireless;
            }
            finally { SetupDiDestroyDeviceInfoList(list); }
        }

        [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetAttributes(SafeFileHandle h, ref Attributes attrs);
        [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
        [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, out Caps caps);
        [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_FlushQueue(SafeFileHandle h);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr list, IntPtr device, ref Guid guid, uint index, ref InterfaceData data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr list, ref InterfaceData data, IntPtr detail, uint size, out uint needed, IntPtr device);
        [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr list);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool ReadFile(SafeFileHandle h, IntPtr buffer, uint count, IntPtr read, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetOverlappedResult(SafeFileHandle h, IntPtr overlapped, out uint count, bool wait);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool CancelIoEx(SafeFileHandle h, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] internal static extern IntPtr CreateEvent(IntPtr security, bool manual, bool initial, string name);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool ResetEvent(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(IntPtr h, uint milliseconds);
        [DllImport("kernel32.dll")] internal static extern bool CloseHandle(IntPtr h);
        [DllImport("user32.dll")] internal static extern bool DestroyIcon(IntPtr h);
        [DllImport("user32.dll")] internal static extern bool SetProcessDPIAware();
    }
}

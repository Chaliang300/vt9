using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace VT9Status
{
    internal static class ConfigChannelLock { internal static readonly object Gate = new object(); }

    internal interface IDpiChannel
    {
        byte[] Read(uint address, int length);
        void Write(uint address, byte[] bytes);
    }

    internal sealed class DpiProfile
    {
        internal byte[] Table;
        internal byte[] Active;
        internal int[] Values;
        internal bool[] Enabled;
        // 本机实测：同一块表在不同连接/写入路径下分别是「50 DPI 单位」和「真实 DPI」，
        // 所以单位必须每次读出来判定，不能写死。
        internal int Unit;
        internal DateTime VerifiedUtc;
        internal int Current { get { return Values[Active[0]]; } }
        internal int ActiveIndex { get { return Active[0]; } }
        internal int EnabledCount
        {
            get { int n = 0; for (int i = 0; i < Enabled.Length; i++) if (Enabled[i]) n++; return n; }
        }
    }

    internal static class DpiController
    {
        internal const uint TableAddress = 0x888, ActiveAddress = 0x898;
        internal static DpiProfile Load(IDpiChannel channel)
        {
            // 交替读取活动块与表块，两轮一致才采用。这条通道有丢帧，一次不成不代表设备没数据，
            // 因此最多交替三轮再判失败（1.7 时只给一轮，实测偶发误报）。
            byte[] active = null;
            byte[] table = null;
            bool stable = false;
            for (int round = 0; round < 3 && !stable; round++)
            {
                byte[] activeFirst = channel.Read(ActiveAddress, 4);
                byte[] tableFirst = channel.Read(TableAddress, 16);
                byte[] activeAgain = channel.Read(ActiveAddress, 4);
                byte[] tableAgain = channel.Read(TableAddress, 16);
                if (activeFirst.SequenceEqual(activeAgain) && tableFirst.SequenceEqual(tableAgain))
                {
                    active = activeAgain;
                    table = tableAgain;
                    stable = true;
                }
            }
            if (!stable) throw new InvalidOperationException("档位读取不稳定，请唤醒鼠标后重试。");
            if (active.Length != 4 || table.Length != 16 || table[14] > 6) throw new InvalidOperationException("不支持的 DPI 配置格式。");
            int count = table[14] + 1;
            if (active[0] >= count) throw new InvalidOperationException("当前档位索引无效。");
            // 七档槽位全部读出来：未启用的槽位也要显示，但只有启用的档位参与校验。
            int[] raw = new int[7];
            for (int i = 0; i < raw.Length; i++) raw[i] = table[i * 2] | table[i * 2 + 1] << 8;

            // 单位判定：先按「真实 DPI」校验，再按「50 DPI 单位」校验。两条都失败才拒绝，
            // 避免把单位不同当成格式错误（1.7 记录过 50 单位，本机当前是真实 DPI）。
            int unit = 0;
            int[] candidates = { 1, 50 };
            for (int c = 0; c < candidates.Length && unit == 0; c++)
            {
                bool ok = true;
                for (int i = 0; i < count; i++)
                {
                    int dpi = raw[i] * candidates[c];
                    if (dpi < 50 || dpi > 26000) { ok = false; break; }
                }
                if (ok) unit = candidates[c];
            }
            if (unit == 0) throw new InvalidOperationException("DPI 档位单位或范围不匹配，未允许写入。");

            int[] values = new int[7];
            for (int i = 0; i < values.Length; i++) values[i] = raw[i] * unit;
            return new DpiProfile { Active = active, Table = table, Values = values, Unit = unit,
                Enabled = BuildEnabled(count), VerifiedUtc = DateTime.UtcNow };
        }

        private static bool[] BuildEnabled(int count)
        {
            bool[] flags = new bool[7];
            for (int i = 0; i < flags.Length; i++) flags[i] = i < count;
            return flags;
        }

        internal static DpiProfile Select(IDpiChannel channel, DpiProfile expected, int index)
        {
            if (expected == null || index < 0 || index >= expected.Values.Length || !expected.Enabled[index])
                throw new ArgumentOutOfRangeException("index");
            DpiProfile before = Load(channel);
            if (!before.Table.SequenceEqual(expected.Table)) throw new InvalidOperationException("设备档位配置已变化，请重新读取后选择。");
            if (before.Active[0] == index) return before;
            byte[] target = (byte[])before.Active.Clone();
            target[0] = (byte)index;
            // Preserve the other three bytes. Never rewrite the table or other settings.
            channel.Write(ActiveAddress, target);
            DpiProfile after;
            try { after = Load(channel); }
            catch (Exception ex) { throw new InvalidOperationException("写入已发送，但回读失败；请重新读取核对当前 DPI。" + ex.Message); }
            if (!after.Active.SequenceEqual(target) || !after.Table.SequenceEqual(before.Table))
                throw new InvalidOperationException("未确认设置生效，请重新读取核对当前 DPI。");
            return after;
        }

        // 整表应用：档位数值、每档启用状态、活动档一次处理。
        // 设备用表内 [14] 表达启用档数，因此启用集合按「前缀压缩」写入，活动索引随之重映射。
        // 若实测证明每档开关是 [15] 的位掩码，只需改这里的压缩方式，回读与索引联动不变。
        internal static DpiProfile Apply(IDpiChannel channel, DpiProfile expected, int[] values, bool[] enabled, int activeIndex)
        {
            if (values == null || enabled == null || values.Length != 7 || enabled.Length != 7)
                throw new ArgumentException("档位数据不完整");
            int enabledCount = 0;
            for (int i = 0; i < enabled.Length; i++) if (enabled[i]) enabledCount++;
            if (enabledCount < 1) throw new InvalidOperationException("至少要启用一档。");
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] < 50 || values[i] > 26000)
                    throw new InvalidOperationException("DPI 必须在 50–26000 之间。");
            }
            if (activeIndex < 0 || activeIndex >= 7 || !enabled[activeIndex])
                throw new InvalidOperationException("待应用的当前档位无效。");

            DpiProfile before = Load(channel);
            if (expected != null && !before.Table.SequenceEqual(expected.Table))
                throw new InvalidOperationException("设备档位配置已变化，请重新读取后再应用。");

            byte[] table = (byte[])before.Table.Clone();
            int unit = before.Unit <= 0 ? 1 : before.Unit;
            int[] newIndex = new int[7];
            for (int i = 0; i < newIndex.Length; i++) newIndex[i] = -1;
            int count = 0;
            for (int i = 0; i < 7; i++)
            {
                if (!enabled[i]) continue;
                if (values[i] % unit != 0) throw new InvalidOperationException("DPI 与设备单位（" + unit + "）不匹配。");
                int raw = values[i] / unit;
                if (raw > 0xFFFF) throw new InvalidOperationException("DPI 超出设备表可表示范围。");
                table[count * 2] = (byte)(raw & 0xFF);
                table[count * 2 + 1] = (byte)(raw >> 8);
                newIndex[i] = count;
                count++;
            }
            table[14] = (byte)(count - 1);

            if (!table.SequenceEqual(before.Table))
            {
                channel.Write(TableAddress, table);
                byte[] readBack;
                try { readBack = ConfigReader.ReadStable(channel, TableAddress, 16); }
                catch (Exception ex) { throw new InvalidOperationException("表已写入但回读失败，请重新读取核对。" + ex.Message); }
                if (!readBack.SequenceEqual(table)) throw new InvalidOperationException("DPI 表回读不一致，已停止后续写入。");
            }

            int target = newIndex[activeIndex] < 0 ? 0 : newIndex[activeIndex];
            byte[] active = ConfigReader.ReadStable(channel, ActiveAddress, 4);
            if (active[0] != target)
            {
                byte[] block = (byte[])active.Clone();
                block[0] = (byte)target;
                channel.Write(ActiveAddress, block);
                byte[] back = ConfigReader.ReadStable(channel, ActiveAddress, 4);
                if (!back.SequenceEqual(block)) throw new InvalidOperationException("表已写入，但活动档位回读不一致。");
            }
            return Load(channel);
        }
    }

    internal sealed class NativeDpiChannel : IDpiChannel, IDisposable
    {
        private readonly SafeFileHandle handle;
        private readonly byte connection;
        internal NativeDpiChannel()
        {
            ushort product;
            string path = Native.FindChargeCollection(out product);
            if (path == null) throw new InvalidOperationException("未找到鼠标配置接口。");
            connection = product == 0x4191 ? (byte)0xFF : (byte)0xA5;
            handle = Native.CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); throw new InvalidOperationException("无法打开配置接口，请关闭其他鼠标配置软件后重试。"); }
        }

        // 有线接口优先：0xFF 表示 USB，0xA5 表示 2.4G 接收器。参数页要用它决定写回报率的哪个字段。
        internal bool Wired { get { return connection == 0xFF; } }
        internal static byte[] Frame(byte connection, byte command, uint address, int length, byte[] payload)
        {
            if ((command != 0xA4 && command != 0xA5) || length < 1 || length > 16) throw new ArgumentException("Invalid DPI command");
            // 写入只允许白名单里的块：活动档 4 字节、档位表 16 字节、三个参数块各 4 字节。
            if (command == 0xA5 && (payload == null || payload.Length != length || !IsWritable(address, length)))
                throw new ArgumentException("Only whitelisted configuration blocks may be written");
            byte[] frame = new byte[32];
            frame[0] = 0xBA; frame[1] = connection; frame[2] = command; frame[3] = (byte)length;
            Array.Copy(BitConverter.GetBytes(address), 0, frame, 4, 4);
            if (payload != null) Array.Copy(payload, 0, frame, 8, payload.Length);
            return frame;
        }

        internal static bool IsWritable(uint address, int length)
        {
            if (address == DpiController.ActiveAddress) return length == 4;
            if (address == DpiController.TableAddress) return length == 16;
            if (length != 4) return false;
            return address == SettingsController.TimingAddress || address == SettingsController.PerformanceAddress
                || address == SettingsController.SensorAddress;
        }
        private void Send(byte[] frame)
        {
            uint written;
            if (!WriteFile(handle, frame, 32, out written, IntPtr.Zero) || written != 32) throw new InvalidOperationException("设备未接受配置请求。");
        }
        public byte[] Read(uint address, int length)
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                Send(Frame(connection, 0xA4, address, length, null));
                for (int poll = 0; poll < 4; poll++)
                {
                    Thread.Sleep(25);
                    byte[] answer = new byte[32]; answer[0] = 0xBA;
                    if (!GetInput(handle, answer, answer.Length)) continue;
                    int offset = answer[0] == 0xBA ? 1 : 0;
                    // This device retains charge metadata in bytes 1..3 even for
                    // block replies; the actual register payload starts at byte 4.
                    if (answer[offset] != 1) continue;
                    byte[] result = new byte[length];
                    Array.Copy(answer, offset + 4, result, 0, length);
                    return result;
                }
            }
            throw new InvalidOperationException("未收到有效配置回复，请轻动鼠标后重试。");
        }
        public void Write(uint address, byte[] bytes)
        {
            Send(Frame(connection, 0xA5, address, bytes.Length, bytes));
            Thread.Sleep(80);
        }
        public void Dispose() { handle.Dispose(); }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteFile(SafeFileHandle h, byte[] bytes, uint length, out uint written, IntPtr overlap);
        [DllImport("hid.dll", EntryPoint = "HidD_GetInputReport", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool GetInput(SafeFileHandle h, [In, Out] byte[] report, int length);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace VT9Status
{
    // 参数设置（0x880 / 0x884 / 0x8C0）与 DPI 档位表的读写。
    // 纪律与 1.7 的 DPI 切换一致：读整块 → 只改目标字节 → 写整块 → 整块回读比对；
    // 目标值与现值相同则不发写；任何回读不一致都抛异常，由界面回显设备真实值。

    internal sealed class TimingBlock
    {
        internal byte[] Raw;
        // 本机实测：这两个字节直接是毫秒（读到 08 08 = 按下/抬起各 8ms），
        // 不是公开实现里的「档位索引」。界面仍只提供官方那七档离散值。
        internal int PressRaw, ReleaseRaw;
        internal int SleepMinutes, Flags;

        internal static readonly int[] DebounceMs = { 1, 2, 4, 8, 16, 24, 32 };

        internal int? PressMs { get { return Valid(PressRaw) ? (int?)PressRaw : null; } }
        internal int? ReleaseMs { get { return Valid(ReleaseRaw) ? (int?)ReleaseRaw : null; } }
        internal int PressIndex { get { return IndexOf(PressRaw); } }
        internal int ReleaseIndex { get { return IndexOf(ReleaseRaw); } }
        internal bool AngleSnapOff { get { return (Flags & 0x01) != 0; } }
        internal bool RippleOff { get { return (Flags & 0x02) != 0; } }

        private static bool Valid(int ms) { return ms >= 1 && ms <= 32; }

        internal static int IndexOf(int ms)
        {
            for (int i = 0; i < DebounceMs.Length; i++) if (DebounceMs[i] == ms) return i;
            return -1;
        }

        internal static TimingBlock Parse(byte[] raw)
        {
            if (raw == null || raw.Length < 4) throw new InvalidOperationException("消抖配置块长度不足。");
            return new TimingBlock
            {
                Raw = raw, PressRaw = raw[0], ReleaseRaw = raw[1],
                SleepMinutes = raw[2], Flags = raw[3]
            };
        }
    }

    internal sealed class PerformanceBlock
    {
        internal byte[] Raw;
        internal int WirelessCode, WiredCode;
        internal bool OnWired;

        internal static readonly int[] Codes = { 0x08, 0x04, 0x02, 0x01, 0x84, 0x82 };
        internal static readonly int[] Rates = { 125, 250, 500, 1000, 2000, 4000 };

        internal int CurrentCode
        {
            get
            {
                // 无线连接时 [2] 可能是 0（该路径未使用），此时退回另一个字段，避免显示「—」。
                int code = OnWired ? WiredCode : WirelessCode;
                if (HzOf(code) == null) code = OnWired ? WirelessCode : WiredCode;
                return code;
            }
        }
        internal int? CurrentHz { get { return HzOf(CurrentCode); } }
        internal int? WirelessHz { get { return HzOf(WirelessCode); } }
        internal int? WiredHz { get { return HzOf(WiredCode); } }

        internal static int? HzOf(int code)
        {
            for (int i = 0; i < Codes.Length; i++) if (Codes[i] == code) return Rates[i];
            return null;
        }

        internal static int? CodeOf(int hz)
        {
            for (int i = 0; i < Rates.Length; i++) if (Rates[i] == hz) return Codes[i];
            return null;
        }

        internal static PerformanceBlock Parse(byte[] raw, bool onWired)
        {
            if (raw == null || raw.Length < 3) throw new InvalidOperationException("回报率配置块长度不足。");
            return new PerformanceBlock { Raw = raw, WirelessCode = raw[0], WiredCode = raw[2], OnWired = onWired };
        }
    }

    internal sealed class SensorBlock
    {
        internal byte[] Raw;
        internal int LiftOffIndex;
        internal bool MotionSync;

        // 本机实测：设备存 1，官方界面显示 1mm；按 0 基解释该是 2mm，与官方矛盾，
        // 所以这里是 1 基（1/2/3 → 1/2/3mm）。0 或更大的值不猜，界面显示未获取。
        internal int? LiftOffMm
        {
            get { return LiftOffIndex >= 1 && LiftOffIndex <= 3 ? (int?)LiftOffIndex : null; }
        }

        internal static SensorBlock Parse(byte[] raw)
        {
            if (raw == null || raw.Length < 2) throw new InvalidOperationException("引擎配置块长度不足。");
            return new SensorBlock { Raw = raw, LiftOffIndex = raw[0], MotionSync = raw[1] == 1 };
        }
    }

    internal sealed class ConfigSnapshot
    {
        internal DpiProfile Dpi;
        internal TimingBlock Timing;
        internal PerformanceBlock Performance;
        internal SensorBlock Sensor;
        internal bool Wired;
        internal string Error;
        internal DateTime ReadUtc;

        internal bool Complete { get { return Dpi != null && Timing != null && Performance != null && Sensor != null; } }

        // 一次打开通道读四个块；某一项失败不影响其它项，错误汇总后交给界面提示。
        internal static ConfigSnapshot Read(IDpiChannel channel, bool wired)
        {
            ConfigSnapshot result = new ConfigSnapshot { Wired = wired, ReadUtc = DateTime.UtcNow };
            List<string> problems = new List<string>();
            try { result.Dpi = DpiController.Load(channel); }
            catch (Exception ex) { problems.Add("档位表：" + ex.Message); }
            try
            {
                TimingBlock block = SettingsController.ReadTiming(channel);
                if (block.SleepMinutes > 240) throw new InvalidOperationException("休眠时长不合理：" + block.SleepMinutes);
                if (block.PressRaw > 32 || block.ReleaseRaw > 32) throw new InvalidOperationException("消抖值不合理：" + block.PressRaw + "/" + block.ReleaseRaw);
                result.Timing = block;
            }
            catch (Exception ex) { problems.Add("消抖/休眠：" + ex.Message); }
            try
            {
                PerformanceBlock block = SettingsController.ReadPerformance(channel, wired);
                if (block.CurrentHz == null)
                    throw new InvalidOperationException("回报率字段无法识别：" + BitConverter.ToString(block.Raw).Replace("-", " "));
                result.Performance = block;
            }
            catch (Exception ex) { problems.Add("回报率：" + ex.Message); }
            try
            {
                SensorBlock block = SettingsController.ReadSensor(channel);
                if (block.LiftOffIndex > 3) throw new InvalidOperationException("静默高度索引不合理：" + block.LiftOffIndex);
                result.Sensor = block;
            }
            catch (Exception ex) { problems.Add("引擎：" + ex.Message); }
            if (result.Dpi == null && result.Timing == null && result.Performance == null && result.Sensor == null && problems.Count > 0)
                throw new InvalidOperationException(problems[0]);
            result.Error = problems.Count == 0 ? null : String.Join("；", problems.ToArray());
            return result;
        }

        // 某一项读失败时保留上一次成功的值，界面同时显示「部分读取失败」，
        // 不把整页清成横线，也不把旧值当成新读数。
        internal ConfigSnapshot MergeWith(ConfigSnapshot previous)
        {
            if (previous == null) return this;
            return new ConfigSnapshot
            {
                Dpi = Dpi ?? previous.Dpi,
                Timing = Timing ?? previous.Timing,
                Performance = Performance ?? previous.Performance,
                Sensor = Sensor ?? previous.Sensor,
                Wired = Wired,
                Error = Error,
                ReadUtc = DateTime.UtcNow
            };
        }

        internal string Summary()
        {
            return "当前 " + (Dpi == null ? "-" : Dpi.Current + " DPI") +
                "，" + (Performance == null ? "回报率 -" : "回报率 " + (Performance.CurrentHz.HasValue ? Performance.CurrentHz.Value + "Hz" : "-")) +
                "，" + (Timing == null ? "休眠 -" : "休眠 " + Timing.SleepMinutes + " 分") +
                "，" + (Timing == null ? "消抖 -" : "消抖 " + (Timing.PressMs.HasValue ? Timing.PressMs.Value.ToString() : "-") + "/" + (Timing.ReleaseMs.HasValue ? Timing.ReleaseMs.Value.ToString() : "-") + "ms");
        }
    }

    internal static class ConfigReader
    {
        // 这条通道的应答里不带地址，无法自证「这条数据是谁的应答」。官方驱动同时访问时，
        // 双方可能互相取走应答；两次一致挡不住这种错配（错得稳定时也会通过），
        // 所以改成三次多数表决，并配合调用方的值域校验。
        internal static byte[] ReadStable(IDpiChannel channel, uint address, int length)
        {
            byte[] first = channel.Read(address, length);
            byte[] second = channel.Read(address, length);
            if (first.SequenceEqual(second)) return first;
            byte[] third = channel.Read(address, length);
            if (first.SequenceEqual(third)) return first;
            if (second.SequenceEqual(third)) return second;
            throw new InvalidOperationException("配置读取不稳定（三次读数互不相同），请关闭官方驱动后重试。");
        }
    }

    internal static class SettingsController
    {
        internal const uint PerformanceAddress = 0x880, SensorAddress = 0x884, TimingAddress = 0x8C0;

        internal static TimingBlock ReadTiming(IDpiChannel channel)
        {
            return TimingBlock.Parse(ConfigReader.ReadStable(channel, TimingAddress, 4));
        }

        internal static PerformanceBlock ReadPerformance(IDpiChannel channel, bool wired)
        {
            return PerformanceBlock.Parse(ConfigReader.ReadStable(channel, PerformanceAddress, 4), wired);
        }

        internal static SensorBlock ReadSensor(IDpiChannel channel)
        {
            return SensorBlock.Parse(ConfigReader.ReadStable(channel, SensorAddress, 4));
        }

        // 单字段写入：只替换目标字节，其余三个字节原样保留。
        internal static byte[] WriteField(IDpiChannel channel, uint address, int length, int offset, byte value)
        {
            byte[] before = ConfigReader.ReadStable(channel, address, length);
            byte[] target = (byte[])before.Clone();
            target[offset] = value;
            if (before.SequenceEqual(target)) return before;
            channel.Write(address, target);
            byte[] after = ConfigReader.ReadStable(channel, address, length);
            if (!after.SequenceEqual(target)) throw new InvalidOperationException("写入未确认：回读与目标不一致。");
            return after;
        }

        internal static TimingBlock SetSleepMinutes(IDpiChannel channel, int minutes)
        {
            if (minutes < 1 || minutes > 255) throw new ArgumentOutOfRangeException("minutes");
            WriteField(channel, TimingAddress, 4, 2, (byte)minutes);
            return ReadTiming(channel);
        }

        internal static TimingBlock SetDebounce(IDpiChannel channel, bool press, int milliseconds)
        {
            // 只允许官方那七档，避免写进设备不认的值。
            if (TimingBlock.IndexOf(milliseconds) < 0) throw new ArgumentOutOfRangeException("milliseconds");
            WriteField(channel, TimingAddress, 4, press ? 0 : 1, (byte)milliseconds);
            return ReadTiming(channel);
        }

        internal static PerformanceBlock SetPollingRate(IDpiChannel channel, bool wired, int hz)
        {
            int? code = PerformanceBlock.CodeOf(hz);
            if (!code.HasValue) throw new ArgumentException("不支持的回报率：" + hz + " Hz");
            // 有线改 [2]，无线改 [0]，另一个字段保持设备现值。
            WriteField(channel, PerformanceAddress, 4, wired ? 2 : 0, (byte)code.Value);
            return ReadPerformance(channel, wired);
        }

        internal static SensorBlock SetLiftOffMm(IDpiChannel channel, int millimeters)
        {
            if (millimeters < 1 || millimeters > 3) throw new ArgumentOutOfRangeException("millimeters");
            WriteField(channel, SensorAddress, 4, 0, (byte)millimeters);
            return ReadSensor(channel);
        }

        internal static SensorBlock SetMotionSync(IDpiChannel channel, bool enabled)
        {
            WriteField(channel, SensorAddress, 4, 1, (byte)(enabled ? 1 : 0));
            return ReadSensor(channel);
        }
    }
}

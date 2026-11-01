using System;

namespace VT9Status
{
    // 参数块与 DPI 整表写入的自检：用模拟通道验证「只改目标字节、相同值不写、回读不一致要报错」。
    internal static class SettingsTests
    {
        private sealed class Channel : IDpiChannel
        {
            // 本机实测值：按下/抬起 8ms、休眠 30 分、标志 0x03
            internal byte[] Timing = { 8, 8, 0x1E, 3 };
            internal byte[] Performance = { 1, 0x82, 0, 0 };
            internal byte[] Sensor = { 1, 1, 0, 3 };
            // 1.7 记录的 50 DPI 单位表：800/1200/1600/2400/3200/6400/20000
            internal byte[] Table = { 16, 0, 24, 0, 32, 0, 48, 0, 64, 0, 128, 0, 144, 1, 6, 0 };
            internal byte[] Active = { 2, 0, 2, 3 };
            internal int Writes;
            internal bool IgnoreWrite;

            public byte[] Read(uint address, int length)
            {
                byte[] source;
                if (address == SettingsController.TimingAddress) source = Timing;
                else if (address == SettingsController.PerformanceAddress) source = Performance;
                else if (address == SettingsController.SensorAddress) source = Sensor;
                else if (address == DpiController.TableAddress) source = Table;
                else if (address == DpiController.ActiveAddress) source = Active;
                else throw new Exception("Unexpected read 0x" + address.ToString("X3"));
                byte[] copy = new byte[length];
                Array.Copy(source, copy, Math.Min(length, source.Length));
                return copy;
            }

            public void Write(uint address, byte[] bytes)
            {
                Writes++;
                if (IgnoreWrite) return;
                if (address == SettingsController.TimingAddress && bytes.Length == 4) Timing = (byte[])bytes.Clone();
                else if (address == SettingsController.PerformanceAddress && bytes.Length == 4) Performance = (byte[])bytes.Clone();
                else if (address == SettingsController.SensorAddress && bytes.Length == 4) Sensor = (byte[])bytes.Clone();
                else if (address == DpiController.TableAddress && bytes.Length == 16) Table = (byte[])bytes.Clone();
                else if (address == DpiController.ActiveAddress && bytes.Length == 4) Active = (byte[])bytes.Clone();
                else throw new Exception("Unexpected write 0x" + address.ToString("X3"));
            }
        }

        internal static int Run()
        {
            int count = 0;
            Action<bool> check = delegate(bool ok)
            {
                if (!ok) throw new Exception("Settings controller test failed at #" + (count + 1));
                count++;
            };

            // 消抖字节是毫秒：08 08 = 按下/抬起各 8ms（对应官方七档里的第 4 档，索引 3）
            Channel fake = new Channel();
            TimingBlock timing = SettingsController.ReadTiming(fake);
            check(timing.PressMs == 8 && timing.ReleaseMs == 8 && timing.SleepMinutes == 30);
            check(timing.PressIndex == 3 && timing.ReleaseIndex == 3);
            check(timing.AngleSnapOff && timing.RippleOff);

            // 写休眠：只改 [2]，其余三个字节保留；相同值不发第二次写
            TimingBlock sleep = SettingsController.SetSleepMinutes(fake, 45);
            check(sleep.SleepMinutes == 45 && fake.Writes == 1);
            check(fake.Timing[0] == 8 && fake.Timing[1] == 8 && fake.Timing[3] == 3);
            SettingsController.SetSleepMinutes(fake, 45);
            check(fake.Writes == 1);

            // 回报率：有线写 [2]，无线写 [0]，互不影响
            fake = new Channel();
            PerformanceBlock perf = SettingsController.ReadPerformance(fake, false);
            check(perf.CurrentHz == 1000 && perf.WirelessHz == 1000);
            SettingsController.SetPollingRate(fake, true, 500);
            check(fake.Performance[2] == 0x02 && fake.Performance[0] == 0x01 && fake.Performance[1] == 0x82 && fake.Performance[3] == 0);
            SettingsController.SetPollingRate(fake, false, 250);
            check(fake.Performance[0] == 0x04 && fake.Performance[2] == 0x02);

            bool rejected = false;
            try { SettingsController.SetPollingRate(fake, true, 8000); } catch (ArgumentException) { rejected = true; }
            check(rejected);

            // 9ms 不在官方七档里，必须拒绝
            rejected = false;
            try { SettingsController.SetDebounce(fake, true, 9); } catch (ArgumentOutOfRangeException) { rejected = true; }
            check(rejected);

            // 引擎块：改 LOD 不能碰运动同步位，反之亦然；1/2/3 对应 1/2/3mm
            SensorBlock lod = SettingsController.SetLiftOffMm(fake, 2);
            check(lod.LiftOffIndex == 2 && lod.LiftOffMm == 2 && fake.Sensor[1] == 1);
            SensorBlock sync = SettingsController.SetMotionSync(fake, false);
            check(!sync.MotionSync && fake.Sensor[0] == 2);
            check(SensorBlock.Parse(new byte[] { 0, 1, 0, 3 }).LiftOffMm == null);

            // 写失败（回读不一致）必须抛异常，界面据此回显设备真实值
            Channel stubborn = new Channel { IgnoreWrite = true };
            rejected = false;
            try { SettingsController.SetSleepMinutes(stubborn, 20); } catch (InvalidOperationException) { rejected = true; }
            check(rejected);

            // 单位判定：50 单位表 → Unit 50；真实 DPI 表（本机当前编码）→ Unit 1
            Channel fifty = new Channel();
            check(DpiController.Load(fifty).Unit == 50);
            Channel direct = new Channel();
            direct.Table = new byte[] { 0x20, 0x03, 0xB0, 0x04, 0x40, 0x06, 0x60, 0x09, 0x80, 0x0C, 0x00, 0x19, 0x20, 0x4E, 6, 0 };
            DpiProfile directProfile = DpiController.Load(direct);
            check(directProfile.Unit == 1 && directProfile.Values[6] == 20000 && directProfile.ActiveIndex == 2);
            DpiProfile directSame = DpiController.Apply(direct, directProfile, directProfile.Values, directProfile.Enabled, 2);
            check(direct.Writes == 0 && directSame.Current == 1600);

            // 值不变时只写活动档
            Channel dpi = new Channel();
            DpiProfile before = DpiController.Load(dpi);
            DpiProfile applied = DpiController.Apply(dpi, before, before.Values, before.Enabled, 3);
            check(applied.ActiveIndex == 3 && applied.Current == 2400 && dpi.Writes == 1);

            // 关闭中间一档：表按前缀压缩，档数减一，尾部槽位保持原值且标记未启用
            dpi = new Channel();
            before = DpiController.Load(dpi);
            bool[] enabled = (bool[])before.Enabled.Clone();
            enabled[2] = false;
            applied = DpiController.Apply(dpi, before, before.Values, enabled, 0);
            check(applied.EnabledCount == 6 && applied.Values[2] == 2400 && applied.Values[5] == 20000);
            check(!applied.Enabled[6] && dpi.Table[14] == 5);

            // 活动档被压缩前移时，索引要跟着重映射：关掉第 1 档，原第 2 档变成索引 0
            dpi = new Channel();
            before = DpiController.Load(dpi);
            enabled = (bool[])before.Enabled.Clone();
            enabled[0] = false;
            applied = DpiController.Apply(dpi, before, before.Values, enabled, 1);
            check(applied.ActiveIndex == 0 && applied.Current == 1200);

            // 全关不允许、超出范围不允许
            dpi = new Channel();
            before = DpiController.Load(dpi);
            rejected = false;
            try { DpiController.Apply(dpi, before, before.Values, new bool[7], 0); } catch (InvalidOperationException) { rejected = true; }
            check(rejected);
            rejected = false;
            int[] bad = (int[])before.Values.Clone();
            bad[0] = 26050;
            try { DpiController.Apply(dpi, before, bad, before.Enabled, 0); } catch (InvalidOperationException) { rejected = true; }
            check(rejected);

            // 写地址白名单：角度块不可写，参数块与档位表可写
            rejected = false;
            try { NativeDpiChannel.Frame(0xFF, 0xA5, 0x8C4, 4, new byte[4]); } catch (ArgumentException) { rejected = true; }
            check(rejected);
            byte[] frame = NativeDpiChannel.Frame(0xFF, 0xA5, SettingsController.TimingAddress, 4, new byte[] { 8, 8, 30, 0 });
            check(frame[2] == 0xA5 && frame[3] == 4 && frame[4] == 0xC0 && frame[5] == 0x08);
            byte[] dpiFrame = NativeDpiChannel.Frame(0xFF, 0xA5, DpiController.TableAddress, 16, new byte[16]);
            check(dpiFrame[3] == 16 && dpiFrame[4] == 0x88 && dpiFrame[5] == 0x08);

            // 自启动兼容性与降级机制自检：
            // 1. IsAutoStart 安全读取不抛异常
            bool currentAutoStart = MainForm.IsAutoStart();
            check(true);

            // 2. 快捷方式创建与解析能力验证（在 data 目录下测试，避免触碰系统启动文件夹）
            string testDir = Program.DataDir;
            string testLnk = System.IO.Path.Combine(testDir, "test-shortcut.lnk");
            string selfPath = typeof(Program).Assembly.Location;
            try
            {
                if (System.IO.File.Exists(testLnk)) System.IO.File.Delete(testLnk);
                bool ok = MainForm.CreateShortcut(testLnk, selfPath, "--tray", testDir, "Test Shortcut");
                check(ok && System.IO.File.Exists(testLnk));
                string target = MainForm.ReadShortcutTarget(testLnk);
                check(String.Equals(target, selfPath, StringComparison.OrdinalIgnoreCase));
                System.IO.File.Delete(testLnk);
                check(!System.IO.File.Exists(testLnk));
            }
            finally
            {
                if (System.IO.File.Exists(testLnk)) System.IO.File.Delete(testLnk);
            }

            return count;
        }
    }
}

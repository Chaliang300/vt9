using System;
using System.Text;
using System.Threading.Tasks;

namespace VT9Status
{
    // 配置页的行为层：三页共用一条配置通道，读取只读，写入一律「读-改-写-回读」。
    internal sealed partial class MainForm
    {
        private ConfigSnapshot config;
        private int[] draftValues = { 800, 1200, 1600, 2400, 3200, 6400, 20000 };
        private bool[] draftEnabled = { true, true, true, false, false, false, false };
        private int draftActive = 2;
        private bool configBusy, configAutoTried;
        private string dpiMessage = "尚未读取档位。点击“读取档位”获取设备现有设置。";
        private string overviewMessage = "尚未读取设备配置。";
        private string paramsMessage = "";

        // ── 状态判断 ──

        private bool DraftsDirty
        {
            get
            {
                if (config == null || config.Dpi == null) return false;
                if (draftActive != config.Dpi.ActiveIndex) return true;
                for (int i = 0; i < 7; i++)
                {
                    if (draftEnabled[i] != config.Dpi.Enabled[i]) return true;
                    if (draftValues[i] != config.Dpi.Values[i]) return true;
                }
                return false;
            }
        }

        private bool CanApply()
        {
            if (config == null || config.Dpi == null || configBusy) return false;
            if (draftActive < 0 || draftActive >= 7 || !draftEnabled[draftActive]) return false;
            int enabled = 0;
            for (int i = 0; i < 7; i++) if (draftEnabled[i]) enabled++;
            return enabled > 0 && DraftsDirty;
        }

        // 除活动档以外是否还有草稿改动：只影响快捷切换的提示语，不影响写什么。
        private bool StageValuesDirty
        {
            get
            {
                if (config == null || config.Dpi == null) return false;
                for (int i = 0; i < 7; i++)
                {
                    if (draftEnabled[i] != config.Dpi.Enabled[i]) return true;
                    if (draftValues[i] != config.Dpi.Values[i]) return true;
                }
                return false;
            }
        }

        private void ResetDraft()
        {
            if (config == null || config.Dpi == null) return;
            draftValues = (int[])config.Dpi.Values.Clone();
            draftEnabled = (bool[])config.Dpi.Enabled.Clone();
            draftActive = config.Dpi.ActiveIndex;
        }

        // ── 读取 ──

        private static ConfigSnapshot ReadConfigFromDevice()
        {
            lock (ConfigChannelLock.Gate)
            using (NativeDpiChannel channel = new NativeDpiChannel())
            {
                return ConfigSnapshot.Read(channel, channel.Wired);
            }
        }

        // 官方驱动也在这条通道上收发，双方会互相取走应答。检测到它在运行就明确提示，
        // 而不是悄悄显示一个可能是别人应答的数字。
        private static bool OfficialDriverRunning()
        {
            try { return System.Diagnostics.Process.GetProcessesByName("RapooGameDev").Length > 0; }
            catch { return false; }
        }

        private static string DriverHint()
        {
            return OfficialDriverRunning() ? "（官方驱动在运行，读数可能不准）" : "";
        }

        private void EnsureConfigLoaded()
        {
            if (configAutoTried || configBusy) return;
            configAutoTried = true;
            RunReadConfig();
        }

        private async void RunReadConfig()
        {
            await ReadConfigAsync();
        }

        private async Task ReadConfigAsync()
        {
            if (configBusy) return;
            configBusy = true;
            dpiMessage = "正在读取设备配置…";
            Invalidate();
            try
            {
                ConfigSnapshot loaded = await Task.Run(delegate { return ReadConfigFromDevice(); });
                if (IsDisposed) return;
                config = loaded.MergeWith(config);
                ResetDraft();
                string text = loaded.Error == null
                    ? "已读取：" + loaded.Summary()
                    : "部分读取失败：" + loaded.Error;
                dpiMessage = text + DriverHint();
                overviewMessage = dpiMessage;
                Program.Log("配置读取：" + loaded.Summary() + (loaded.Error == null ? "" : "；" + loaded.Error));
            }
            catch (Exception ex)
            {
                if (!IsDisposed)
                {
                    dpiMessage = ex.Message;
                    overviewMessage = ex.Message;
                    Program.Log("配置读取失败：" + ex.Message);
                }
            }
            finally
            {
                if (!IsDisposed) { configBusy = false; Invalidate(); }
            }
        }

        // 写入失败后回读一次，让界面显示设备真实值，而不是写入前的界面值。
        private async Task RefreshAfterFailure()
        {
            try
            {
                ConfigSnapshot fresh = await Task.Run(delegate { return ReadConfigFromDevice(); });
                if (IsDisposed) return;
                config = fresh;
                ResetDraft();
            }
            catch (Exception ex) { Program.Log("失败后回读未成功：" + ex.Message); }
        }

        // ── DPI 草稿编辑 ──

        private void OnStageEnabledChanged(int index)
        {
            if (syncing || config == null || config.Dpi == null) return;
            draftEnabled[index] = stageSwitches[index].IsChecked == true;
            if (!draftEnabled[index] && draftActive == index)
            {
                int next = -1;
                for (int i = 0; i < 7; i++) if (draftEnabled[i]) { next = i; break; }
                if (next < 0)
                {
                    // 至少留一档，避免出现没有任何可用档位的状态。
                    draftEnabled[index] = true;
                    syncing = true;
                    stageSwitches[index].IsChecked = true;
                    syncing = false;
                    dpiMessage = "至少要启用一档。";
                    Invalidate();
                    return;
                }
                draftActive = next;
            }
            dpiMessage = DraftsDirty ? "已修改，点击“应用到设备”写入。" : "与设备当前值一致，无需写入。";
            Invalidate();
        }

        private void OnStageSliderChanged(int index)
        {
            if (syncing || config == null || config.Dpi == null) return;
            int value = (int)Math.Round(stageSliders[index].Value / 50.0) * 50;
            if (value < 50) value = 50;
            if (value > 20000) value = 20000;
            draftValues[index] = value;
            SetText(stageValueTexts[index], value.ToString());
            if (index == draftActive) SetText(dpiValueText, value.ToString());
            dpiMessage = "已修改，点击“应用到设备”写入。";
            if (applyButton != null)
            {
                bool can = CanApply();
                applyButton.IsEnabled = can;
                applyButton.Opacity = can ? 1 : 0.4;
            }
            if (revertButton != null) revertButton.IsEnabled = DraftsDirty;
        }

        private void StepDraft(int index, int delta)
        {
            if (config == null || config.Dpi == null || configBusy) return;
            int value = draftValues[index] + delta * 50;
            if (value < 50) value = 50;
            if (value > 20000) value = 20000;
            draftValues[index] = value;
            dpiMessage = "已修改，点击“应用到设备”写入。";
            Invalidate();
        }

        private void RevertDraft()
        {
            ResetDraft();
            dpiMessage = "已恢复为设备当前值。";
            Invalidate();
        }

        // 快捷切换：点一下某一档就把它设为当前档。只改活动档块（0x898），各档的值与启用
        // 状态不动；草稿里若还有别的改动，会跟「应用到设备」一样一起写下去。
        private async void QuickSwitchStage(int index)
        {
            if (config == null || config.Dpi == null)
            {
                dpiMessage = "尚未读取设备档位，先点“读取档位”。";
                Invalidate();
                return;
            }
            if (configBusy) return;
            if (!draftEnabled[index])
            {
                dpiMessage = "第 " + (index + 1) + " 档未启用，先打开它的开关。";
                Invalidate();
                return;
            }
            if (index == draftActive && !StageValuesDirty)
            {
                dpiMessage = "已经是第 " + (index + 1) + " 档。";
                Invalidate();
                return;
            }
            bool switchOnly = !StageValuesDirty;
            draftActive = index;
            await ApplyDpiAsync(switchOnly);
        }

        // ── DPI 应用 ──

        private async void RunApplyDpi()
        {
            await ApplyDpiAsync();
        }

        private async Task ApplyDpiAsync(bool switchOnly = false)
        {
            if (!CanApply()) return;
            DpiProfile expected = config.Dpi;
            int[] values = (int[])draftValues.Clone();
            bool[] enabled = (bool[])draftEnabled.Clone();
            int active = draftActive;
            configBusy = true;
            dpiMessage = switchOnly ? "正在切换到第 " + (active + 1) + " 档…" : "正在写入并回读核验…";
            Invalidate();
            bool refresh = false;
            try
            {
                DpiProfile updated = await Task.Run(delegate
                {
                    lock (ConfigChannelLock.Gate)
                    using (NativeDpiChannel channel = new NativeDpiChannel())
                        return DpiController.Apply(channel, expected, values, enabled, active);
                });
                if (IsDisposed) return;
                config.Dpi = updated;
                ResetDraft();
                dpiMessage = (switchOnly ? "已切换到第 " : "已写入并回读：第 ")
                    + (updated.ActiveIndex + 1) + " 档 " + updated.Current.ToString() + " DPI。";
                Program.Log("DPI 档位已应用并回读：" + updated.Current);
                RefreshTrayAfterWrite();
            }
            catch (Exception ex)
            {
                if (!IsDisposed)
                {
                    dpiMessage = ex.Message;
                    Program.Log("DPI 设置：" + ex.Message);
                    refresh = true;
                }
            }
            finally
            {
                if (!IsDisposed) configBusy = false;
            }
            // C# 5 不允许在 catch 里等待：回读放在 try 之外。
            if (refresh) await RefreshAfterFailure();
            if (!IsDisposed) Invalidate();
        }

        // ── 参数写入（控件改动立即写 + 回读） ──

        private async void RunWrite(string label, Func<IDpiChannel, string> operation)
        {
            await WriteConfigAsync(label, operation);
        }

        private async Task WriteConfigAsync(string label, Func<IDpiChannel, string> operation)
        {
            if (configBusy || config == null) return;
            configBusy = true;
            paramsMessage = label;
            Invalidate();
            string message = null;
            ConfigSnapshot fresh = null;
            bool refresh = false;
            try
            {
                await Task.Run(delegate
                {
                    lock (ConfigChannelLock.Gate)
                    using (NativeDpiChannel channel = new NativeDpiChannel())
                    {
                        message = operation(channel);
                        fresh = ConfigSnapshot.Read(channel, channel.Wired);
                    }
                });
                if (IsDisposed) return;
                if (fresh != null && fresh.Dpi != null) { config = fresh; ResetDraft(); }
                paramsMessage = message;
                Program.Log(label + "：" + message);
                RefreshTrayAfterWrite();
            }
            catch (Exception ex)
            {
                if (!IsDisposed)
                {
                    paramsMessage = ex.Message;
                    Program.Log(label + "失败：" + ex.Message);
                    refresh = true;
                }
            }
            finally
            {
                if (!IsDisposed) configBusy = false;
            }
            if (refresh) await RefreshAfterFailure();
            if (!IsDisposed) Invalidate();
        }

        private void PickPolling(int index)
        {
            int[] rates = { 125, 250, 500, 1000 };
            if (index < 0 || index >= rates.Length) return;
            bool wired = config != null && config.Wired;
            int hz = rates[index];
            RunWrite("正在写入回报率", delegate(IDpiChannel channel)
            {
                PerformanceBlock block = SettingsController.SetPollingRate(channel, wired, hz);
                return "回报率已写入并回读：" + RateText(block.CurrentHz);
            });
        }

        private void PickLod(int index)
        {
            int millimeters = index + 1;
            RunWrite("正在写入静默高度", delegate(IDpiChannel channel)
            {
                SensorBlock block = SettingsController.SetLiftOffMm(channel, millimeters);
                return "静默高度已写入并回读："
                    + (block.LiftOffMm.HasValue ? block.LiftOffMm.Value + " mm" : "未识别（" + block.LiftOffIndex + "）");
            });
        }

        private void PickMotion(bool enabled)
        {
            RunWrite("正在写入运动同步", delegate(IDpiChannel channel)
            {
                SensorBlock block = SettingsController.SetMotionSync(channel, enabled);
                return "运动同步已写入并回读：" + (block.MotionSync ? "开" : "关");
            });
        }

        private void PickSleep(int index)
        {
            int[] minutes = { 5, 10, 30, 60, 120 };
            if (index < 0 || index >= minutes.Length) return;
            CommitSleep(minutes[index]);
        }

        private void CommitSleep(int minutes)
        {
            RunWrite("正在写入休眠时长", delegate(IDpiChannel channel)
            {
                TimingBlock block = SettingsController.SetSleepMinutes(channel, minutes);
                return "休眠时长已写入并回读：" + block.SleepMinutes + " 分钟";
            });
        }

        private void OnSleepSliderChanged()
        {
            if (syncing || sleepSlider == null) return;
            SetText(sleepValueText, ((int)Math.Round(sleepSlider.Value)) + " 分钟");
        }

        // 松手才写设备：拖动过程中不应产生几十次 EEPROM 写入。
        private void OnSleepCommitted()
        {
            if (syncing || sleepSlider == null || config == null) return;
            int minutes = (int)Math.Round(sleepSlider.Value);
            if (minutes < 5) minutes = 5;
            if (minutes > 120) minutes = 120;
            if (config.Timing != null && config.Timing.SleepMinutes == minutes) return;
            CommitSleep(minutes);
        }

        private void SelectDebounceTab(bool press)
        {
            if (editingPress == press) return;
            editingPress = press;
            Invalidate();
        }

        private void PickDebounce(bool press, int index)
        {
            if (index < 0 || index >= TimingBlock.DebounceMs.Length) return;
            int milliseconds = TimingBlock.DebounceMs[index];
            RunWrite(press ? "正在写入按下消抖" : "正在写入抬起消抖", delegate(IDpiChannel channel)
            {
                TimingBlock block = SettingsController.SetDebounce(channel, press, milliseconds);
                return "消抖已写入并回读：按下 " + (block.PressMs.HasValue ? block.PressMs.Value.ToString() : "—") +
                    " ms / 抬起 " + (block.ReleaseMs.HasValue ? block.ReleaseMs.Value.ToString() : "—") + " ms";
            });
        }

        // ── 原始块诊断（只读七个块，用于和官方软件做差分） ──

        private async void RunConfigDump()
        {
            if (configBusy) return;
            configBusy = true;
            overviewMessage = "正在读取原始配置块…";
            Invalidate();
            try
            {
                string text = await Task.Run(delegate
                {
                    lock (ConfigChannelLock.Gate)
                    using (NativeDpiChannel channel = new NativeDpiChannel())
                    {
                        StringBuilder builder = new StringBuilder();
                        uint[] addresses = { 0x880, 0x884, 0x888, 0x898, 0x8C0, 0x8C4, 0x8C8 };
                        string[] names = { "performance", "sensor", "dpiX", "activeStage", "timing", "angle", "dpiY" };
                        int[] lengths = { 4, 4, 16, 4, 4, 4, 16 };
                        for (int i = 0; i < addresses.Length; i++)
                        {
                            try
                            {
                                byte[] block = ConfigReader.ReadStable(channel, addresses[i], lengths[i]);
                                // 名字补到同宽，七行的「0xXXX =」能对齐，复制出去差分时也好读。
                                builder.Append(names[i].PadRight(12)).Append("0x").Append(addresses[i].ToString("X3"))
                                    .Append(" = ").Append(BitConverter.ToString(block).Replace("-", " ")).AppendLine();
                            }
                            catch (Exception ex)
                            {
                                builder.Append(names[i].PadRight(12)).Append("读取失败：").Append(ex.Message).AppendLine();
                            }
                        }
                        return builder.ToString().Trim();
                    }
                });
                if (IsDisposed) return;
                // 状态行只放一句摘要，七行 HEX 交给浮层——44px 高的状态行会把它裁成三行。
                overviewMessage = "已读取 " + text.Split('\n').Length + " 个原始配置块，明细见弹出面板（同一份内容已写入 data/app.log）。";
                Program.Log("配置块读取：" + Environment.NewLine + text);
                ShowConfigDump(text);
            }
            catch (Exception ex)
            {
                if (!IsDisposed) overviewMessage = ex.Message;
            }
            finally
            {
                if (!IsDisposed) { configBusy = false; Invalidate(); }
            }
        }

        // ── 预览：截图诊断时尽量显示真实值，读不到用本机实测表并标注 ──

        internal void LoadProfileForPreview()
        {
            // 预览只读一次：窗口显示后 EnsureConfigLoaded 会再读一遍，重复读会互相干扰。
            configAutoTried = true;
            try
            {
                config = ReadConfigFromDevice();
                ResetDraft();
            }
            catch (Exception ex)
            {
                Program.Log("预览配置读取失败：" + ex.Message);
                config = new ConfigSnapshot
                {
                    Dpi = new DpiProfile
                    {
                        Values = new int[] { 800, 1200, 1600, 2400, 3200, 6400, 20000 },
                        Enabled = new bool[] { true, true, true, true, true, true, true },
                        Active = new byte[] { 2, 0, 2, 3 },
                        Table = new byte[16],
                        Unit = 1,
                        VerifiedUtc = DateTime.UtcNow
                    },
                    Timing = new TimingBlock { Raw = new byte[4], PressRaw = 8, ReleaseRaw = 8, SleepMinutes = 30 },
                    Performance = new PerformanceBlock { Raw = new byte[4], WirelessCode = 0x01, WiredCode = 0x01, OnWired = true },
                    Sensor = new SensorBlock { Raw = new byte[4], LiftOffIndex = 1, MotionSync = true },
                    Wired = true, ReadUtc = DateTime.UtcNow
                };
                ResetDraft();
                dpiMessage = "设备未响应，以上为本机实测档位，仅用于预览。";
            }
            Invalidate();
        }
    }
}

# 1.12 图标与系统托盘（可合并补丁包）

依据 `D:\workbuddyData\2026-10-31-20-57-02\图标系统.svg` 实现，所有坐标直接从该文件取出，
不靠肉眼估。

## 包内文件

| 文件 | 说明 |
| --- | --- |
| `src/IconArtwork.cs` | 新增。应用图标 + 托盘图标绘制、.ico 导出、预览图。放在 `src/` 即参与编译。 |
| `src/TrayMenu.cs` | 新增。托盘右键菜单（配色、圆角、头部状态、自绘勾选）。`MainForm` 的 partial。 |
| `assets/VT9Status.ico` | 新增。由程序自身导出，8 个尺寸（16/20/24/32/48/64/128/256）。 |
| `icon-preview.png` | 验证图：浅/深色桌面下的图标阶梯、6 种托盘状态 × 3 种尺寸、16px 像素放大。 |
| `tray-menu-preview.png` | 验证图：真实托盘菜单截图。 |

## 需要合并进现有源码的接线（共 5 处）

1. `MainForm.cs`
   - 字段：`flashTimer`、`taskbarLight`、`writeFlash`（`trayMenu` 字段改由 `TrayMenu.cs` 持有）。
   - 构造函数：`ApplyWindowIcon(); BuildTrayMenu(); taskbarLight = IconArtwork.LightTaskbar();`
     托盘改为 `new NativeTrayIcon { Text = "...", ContextMenuStrip = trayMenu, Visible = true }`（不再传 `SystemIcons.Application`）。
     新增 400ms `flashTimer`，到点清 `writeFlash` 并刷新图标。
   - `ApplyWindowIcon()`：从 exe 关联图标取 `Window.Icon`。
   - `WindowMessage`：`0x001A` / `0x031A` 时 `RefreshTaskbarTheme()`。
2. `MainForm.State.cs`
   - `RefreshState`：图标 key 改为 `level + ":" + chargingNow + ":" + snapshot.Open + ":" + taskbarLight`。
   - `UpdateTrayIcon(int? level, bool chargingNow, bool open)`：按状态选色，尺寸走 `IconArtwork.TraySize()`。
   - 新增 `FlashTrayWrite()`、`RefreshTaskbarTheme()`、`TryTrayRect()`。
3. `MainForm.Dpi.cs`（现为配置页行为层）：**写入成功处调用 `FlashTrayWrite()`**。
   当前分支已在第 218、265 行接上，若后续重写该文件需要重新接。
4. `Program.cs`：4 个诊断开关
   `--icon-preview`、`--export-icons [路径]`、`--tray-menu-preview`、`--tray-shot`。
5. `NativeTrayIcon.cs`：新增 `TryGetRect(out Rectangle)`（`Shell_NotifyIconGetRect`）。

另外两处随源码一起提交即可：`build.ps1`（`/win32icon` + 首次自动导出 ico）、
`src/AssemblyInfo.cs`（版本号）。

## 几何与规则（便于以后复核）

- 应用图标 256 单位：圆角方形 rx=58（22.5%）、45° 渐变 `#495BEF → #2B30AF`、
  白色鼠标两半（中缝 10.83u）、滚轮圆 (128,83) r=10.4167。
- 托盘 24 单位（导出样例按 16px）：鼠标轮廓为 2.2u 的环（外/内两条路径填充，非描边）、
  水位 `h = 1.8 + 电量% × 0.126`（u）、底部固定 19.2u、宽 8.2u、圆角 `min(3, h/2)`；
  充电水位不低于 60% 并叠闪电；断开整体 40% 不透明度 + 45° 斜线；写入瞬间 `#3B4BD8` 400ms。
- 颜色：正常态随任务栏反色（深色栏 `#FFFFFF`，浅色栏 `#1C2230`）；≤20% 琥珀
  （深 `#E8A33D` / 浅 `#C77C1E`）；充电 `#2FA36B`。
- 尺寸：`AppliedDPI / 96 × 16`，夹在 16–32（100/125/150% → 16/20/24）。

## 合并后必须补的验证

1. 编译通过后重跑 `--self-test`（合并前为 31 项 PASS）与 `--icon-preview`。
2. 实机任务栏像素截图：`--tray-shot` 需要主窗口显示过（隐藏启动时
   `Shell_NotifyIcon` 返回 Win32=5），或手动截屏通知区域。
3. 写入档位成功后的 400ms 靛蓝闪烁（需要连设备实际写入）。
4. 充电态（`#2FA36B` + 闪电）实机确认。

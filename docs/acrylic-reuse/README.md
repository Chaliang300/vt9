# Windows 原生 Acrylic 背景复用指南

本方案整理自 VT9Status 的实际改造与验证，适用于希望实现实时桌面毛玻璃背景的 Windows 桌面程序。本文以 WPF / .NET Framework 为例；不是给不透明的 WinForms 窗口增加一个属性就能完成。

## 下次直接使用

1. 将同目录 `NativeAcrylicBackdrop.cs` 复制到新项目。
2. 主窗口使用 WPF，保持 `AllowsTransparency=false`；在 `Show()` 前创建辅助类并保存为窗口字段。
3. 根布局背景使用透明或半透明颜色，文字和按钮正常绘制。
4. 使用后文的高对比条纹场景检查真实模糊，再检查隐藏、恢复和拖动。

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopBackdrop;

public sealed class MainWindow : Window
{
    private readonly NativeAcrylicBackdrop backdrop;

    public MainWindow()
    {
        Width = 600;
        Height = 440;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = false;
        Background = Brushes.Transparent;

        // 可选：静态白色蒙层，改善深色/彩色背景上的文字对比度。
        // 它与系统背景一起合成，不需要应用逐帧重画。
        Grid root = new Grid {
            Background = new SolidColorBrush(Color.FromArgb(112, 255, 255, 255))
        };
        TextBlock title = new TextBlock {
            Text = "原生 Acrylic", Margin = new Thickness(24),
            Foreground = Brushes.Black, FontSize = 22
        };
        root.Children.Add(title);
        title.MouseLeftButtonDown += delegate { DragMove(); };
        Content = root;

        backdrop = new NativeAcrylicBackdrop(this);
        // Closed 时辅助类会自动释放定时器和窗口事件。
    }
}
```

辅助类是基于本次成功实现整理出的复用版本，已单独编译检查；**未在新项目中执行独立运行回归**。移植后仍需检查本项目的布局、生命周期和目标系统。它只负责背景，不包含托盘、设备通信、DPI 设置或业务事件。

## 为什么这样实现

旧链路为：抓屏 → 缩小 → CPU 模糊 → 放大 → 蒙层 → 整窗重绘。拖动时 140ms 一次，理论上约 7 次/秒，且这些步骤占用 UI 线程。

新链路为：WPF 保留前景内容 → DWM 获取并模糊底层内容 → 系统合成背景与前景。应用只在数据或交互变化时更新前景；拖动不启动抓屏、模糊或刷新循环。

60Hz 的帧预算约为 16.7ms，144Hz 约为 6.9ms。交给系统合成去掉了应用侧低频采样和同步 CPU 处理的限制，但不能据此保证所有硬件和场景都稳定达到这些帧率。

## 已验证有效的初始化顺序

| 阶段 | 操作 | 要点 |
| --- | --- | --- |
| 创建窗口 | WPF Window，`AllowsTransparency=false`，透明背景 | 不使用应用级 `Opacity=0` 或色键来抓取背后内容 |
| HWND 创建后 | 获取 `HwndSource`，设置 `CompositionTarget.BackgroundColor=Transparent` | 客户区必须具备透明合成表面 |
| 准备玻璃区域 | `DwmExtendFrameIntoClientArea`，四个 margin 均为 -1 | 同一 HWND 正常运行期间准备一次；合成环境重建后另行准备 |
| 请求无材质 | 属性 38，值 1 | `DWMSBT_NONE`，是系统背景类型的枚举值 |
| 分开提交 | 等首帧呈现，使用一次性延迟 | 当前项目采用 250ms，不是循环刷新 |
| 激活 Acrylic | 属性 38，值 3 | `DWMSBT_TRANSIENTWINDOW`，Windows 11 对应 Desktop Acrylic |
| 正常运行 | 保留前景，背景交给 DWM | 不在鼠标移动/拖动/每帧回调里重建表面或重设材质 |
| 隐藏后恢复 | 重新绑定材质 | 隐藏时停止尚未触发的初始化回调，关闭时解除事件和 hook |

250ms 是本机 Windows 11 Build 26220 上的经验值；一次性 UI 定时器只提供提交机会，不是精确的垂直同步屏障。移植时应以实际画面为准，不能把这个数字当作 Windows 规范。

## 本次最关键的排障结论

**API 返回成功，不代表已经出现毛玻璃。**

本次直接请求 Acrylic 时曾停留在纯色背景。用户实际确认：点击“无材质”，再点击“Acrylic”后，背景出现模糊。最终实现自动重现这次分步切换，正式预览也确认成功。

初始化排查时需要控制两个变量：透明合成表面的准备和材质属性切换。辅助类在后续激活回调中只设置材质，避免同时重建背景表面；这有利于复现本次成功路径，不意味着已经逐项证明所有失败尝试的单一根因。

另一个检查陷阱是测试背景本身：在 WPF Window 的 `OnRender()` 里画的背景，可能被窗口模板/内容覆盖。推荐像测试程序一样，用实际根 `Canvas` 的子矩形构成条纹，确保画面里真的存在条纹。

## 不要重新引入的路径

- 不把 `140ms` 直接改成 `16ms` 继续同步抓屏和 CPU 模糊。
- 不在正常拖动路径使用 `CopyFromScreen`、`Thread.Sleep` 或 `Application.DoEvents`。
- 不反复切换 `SetWindowDisplayAffinity` 来获得背景。
- 不把不透明 Bitmap、实色根面板或 GDI 全窗绘制覆盖在系统材质上。
- 不将每次 `LocationChanged`、鼠标移动或 `CompositionTarget.Rendering` 都接到材质初始化。
- 不用整窗 `Opacity` 降低文字和按钮透明度来代替毛玻璃。

## 可读性与性能

可使用静态半透明白色蒙层，例如 `ARGB(112,255,255,255)`，并把正文、次要文字和按钮保留为足够深的实色。蒙层数值应结合自己的配色重新检查；不要直接套用到深色主题。

前景采用保留模式：状态改变时更新对应控件，不主动制造持续的整窗刷新。DPI 档位等重复控件可以复用，避免每次数据到达就全部销毁重建。

避免叠加大量独立模糊层。当前方案的主体是一个系统窗口背景，卡片若需要层次，可先用半透明填充承载内容。

## 系统兼容与回退

`DWMWA_SYSTEMBACKDROP_TYPE` 的文档最低系统为 Windows 11 Build 22621。系统不支持、接口失败、合成不可用时，辅助类采用实色背景；高对比度使用系统窗口背景色，应用还应配套使用系统前景色。

透明效果、节能模式等系统策略也可能改变实际材质，即使 API 返回成功。回退情况要观察真实画面。本次没有为了验证回退而改变用户的系统设置。

不要将 Mica 当作相同的桌面模糊替代品：本方案请求的是 Desktop Acrylic。主窗口与辅助窗口的材质选择仍应结合产品设计和目标系统验证。

## 编译与运行

现有 .NET Framework 4.8 工程使用以下系统程序集，无需为本方案安装 NuGet 包：

```text
System.dll
System.Core.dll
System.Xaml.dll
WPF/WindowsBase.dll
WPF/PresentationCore.dll
WPF/PresentationFramework.dll
```

本项目保留托盘等功能，还引用 `System.Drawing.dll` 和 `System.Windows.Forms.dll`。启动入口需要 `[STAThread]`，运行 WPF `Application` 与 Dispatcher。不要在同一线程启动两个独立的 GUI 主消息循环。

随附配置示例：

```xml
<?xml version="1.0" encoding="utf-8" ?>
<configuration>
  <startup>
    <supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.8" />
  </startup>
</configuration>
```

本项目构建命令：

```powershell
.\build.ps1
.\build.ps1 -OutputDirectory "$PWD\artifacts\acrylic-validation"
```

## 每次移植后的验证步骤

| 检查 | 预期结果 |
| --- | --- |
| 首次启动，不手动切换 | 自动出现背景模糊，不能只查 HRESULT |
| 红蓝/绿黄 24px 细条纹放在窗口后 | 窗口外条纹清晰，窗口内条纹被柔化；前景文字仍清晰 |
| 切为无材质作对照 | 同位置条纹变清晰，排除“背景本来就是纯色”的误判 |
| 改变背后内容 | 材质颜色/内容随之变化，排除缓存截图 |
| 拖动 | 背景位置跟随，按钮与文字正常；要声称高帧率需额外连续录制/测量 |
| 隐藏、恢复、最小化、失焦 | 状态正常，无遗留透明窗口或初始化任务；分别核对不同应用失焦情形 |
| 点击与键盘操作 | 按钮可响应，焦点可见；移植不改变原有写入或业务权限边界 |
| 不支持系统/高对比度 | 实色回退且文字可读；未实测时明确记录 |
| 退出 | 停止一次性定时器，解除窗口事件和原生 hook |

本项目可执行：

```powershell
Start-Process .\VT9Status.exe -ArgumentList '--self-test' -Wait
Start-Process .\VT9Status.exe -ArgumentList '--ui-preview-bg' -Wait
Start-Process .\VT9Status.exe -ArgumentList '--ui-preview-live'
```

当前交互预览保留真实主面板，“应用”按钮可以设置真实 DPI；检查材质时只操作诊断背景按钮即可。测试背景后续可能已改为其他配色，严格的细条纹对照可使用 `experiments/acrylic/AcrylicProbe.exe`。

## 本项目中的参考位置

- `src/MainForm.cs`：`OnSourceInitialized`、`OnContentRendered`、`ApplyBackdrop`、窗口恢复与清理。
- `src/MainForm.Layout.cs`：当前前景布局与半透明表面。布局之后有独立调整，不能把旧版尺寸当作材质依赖。
- `src/Program.cs`：WPF 启动入口与预览场景。
- `build.ps1`：本地 Framework 编译与 WPF 引用。
- `experiments/acrylic/`：原始独立对照验证。
- `验证记录-1.10.md`：当时的验证事实和未验证项；不是对后续所有界面改动的重新验收。

## 已确认与未确认

已确认：本机原生 Acrylic 可用；分步切换可以自动激活；前景控件、背景颜色变化、拖动落点、托盘隐藏/唤醒正常；1.10 改造时的 31 项既有自检通过。

未确认：稳定 60Hz/144Hz、逐帧掉帧率、跨 DPI/跨显示器、DWM GPU 总开销，以及所有系统降级策略。静态进程 CPU 采样不能代替这些测试。

## 官方参考

- [系统背景类型与最低系统版本](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwm_systembackdrop_type)
- [WPF 的透明 CompositionTarget 与扩展玻璃表面](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/graphics-multimedia/extend-glass-frame-into-a-wpf-application)

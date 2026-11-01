# 提示词：按 rapoo-vt9-svg（1020×660）重做布局 / 字体 / 页面 / 点击与切换动效

- 日期：2026-11-01
- 适用工程：`C:\Users\26451\Desktop\ces`（VT9Status，当前 1.17.1.0，本次目标 1.18.0）
- 设计源：对话里给出的 `rapoo-vt9-svg`（`viewBox="0 0 1020 660"`，四页 + 纵向玻璃 Dock + 完整交互脚本）

这份是**可直接整段投给编码 agent 的提示词**，覆盖四件事：布局（窗口 / 栅格 / Dock / 四页逐卡坐标）、字体（字号与字重）、页面（概况 / DPI / 参数 / 设置的内容与坐标）、动效（点击动效 + 页面切换动效）。

实现前先读（仓库里已有，不重复它们的内容）：

- `docs\skills\action\SKILL.md` —— 四条动效原则与默认参数表
- `docs\skills\action\references\liquid-lens-dock.md` —— 这份 SVG 的姊妹分析（透镜 Dock 的四层结构、六项可带走的技术、四处必改的坑、WPF 对应表）
- `docs\skills\action\references\wpf-transition-engine.md` —— WPF 续接入口与过渡状态机的骨架
- `docs\滑动切换提示词.md` —— 1.17 那次「导航拖动跟手切换」的提示词（本份在它之上扩面，不推翻它）
- `docs\切换动画建议.md` —— 为什么「旧页立刻隐藏 + 新页入场」比整页交叉淡化更干净

---

```text
任务：把 VT9Status 主界面按设计稿 rapoo-vt9-svg（viewBox 1020×660）重做：
      窗口与栅格、左侧纵向玻璃 Dock、四个页面的卡片布局、字号与字重、点击动效、页面切换动效。
      动效按四条原则做（可打断、退场不飞出、状态不硬切、入场起步快收尾轻），
      不要为了"接近 SVG 的观感"牺牲跟手与可打断。

工作目录：C:\Users\26451\Desktop\ces
先把设计稿存成 docs\design\rapoo-vt9-svg.svg（从对话里原样保存），它是本次唯一的对照物；
所有坐标以它为准，本文里给出的数字已经是"换算到窗口绝对坐标"的结果，可直接用。

════════════════════════════════════════════════════════════════
一、项目事实（动手前必看，全部来自 src 实际代码，勿以旧验证记录为准）
════════════════════════════════════════════════════════════════
1. 没有 NuGet / .sln / .csproj：build.ps1 用系统 .NET Framework 的 csc 全量编译 src\*.cs。
   编译：powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -OutputDirectory artifacts\svg-layout
2. 主界面是 **WPF**（MainForm : Window，纯代码构建，无 XAML 资源字典），
   托盘与图标是 WinForms/GDI。窗口 WindowStyle=None、AllowsTransparency=false、
   ResizeMode=NoResize、UseLayoutRounding=true，背景是 DWM 原生 Acrylic
   （MainForm.cs 的 ApplyBackdrop()：DwmSetWindowAttribute(hwnd,38,…)，另有 250ms
   「无材质 → Acrylic」两帧过渡避黑闪）。
3. 布局全部是手算坐标的 Canvas：surface（根 Canvas，挂在 Content 的圆角 Border 里），
   四个页面 pageOverview / pageDpi / pageParams / pageSettings 由 NewPage() 建（当前 800×614，
   靠 Visibility 切换），左侧 navDockContainer（当前 62×314 @ (7,69)）承载 Dock 手势。
   坐标是设计好的：**只在本次改版里按第一节的表格重设一次，之后一行都不许再动**。
4. 公用设施：Fonts.cs（命名字号）、Theme.cs（Ink/Muted/Accent…）、ColorBrush()、Label()、
   Meta()、SubLabel()、CardTitle()、Card()、AddButtonIn()、LineIcon()、PlaceIn()、
   SetText()。内联 ControlTemplate 用 TryParseStyle() 解析字符串（无 XAML 文件）。
5. 动效引擎已在 src\MainForm.Swipe.cs：Transition.AnimateTo（读当前值 → 清动画 → 固化 →
   from→to，可打断，符合原则 1）、Transition.Duration（clamp(剩余/max(400,速度)×1000, 160, 340)）、
   SlideEase.Shared（1−(1−t)³ + 末段 4.8% 回弹）、FadeText（40ms 淡出 + 100ms 淡入 + 0.96→1）、
   ClampInsideShelf（指数阻尼 damped = allowed·(1−e^(−pull/40))）、导航拖拽状态机
   （OnDockMouseDown/Move/Up、4px→3px 阈值、520px/s 判定、NavStep=82）。
   **本次一律复用这些入口，不要另起一套动画代码。**
6. 警告：`验证记录-1.17-滑动切换.md` 里写的「弹簧积分器 / 胶囊 ScaleY 1.25 形变 / 页面 BlurEffect +
   BitmapCache / 160px 位移上限」在当前 src 里**并不存在**（grep Spring / BlurEffect / BitmapCache
   在 src 下零命中）。以代码为准；这份提示词里对 1.17 现状的描述也是按代码写的。
7. 数据流不许动：config / draft* / syncing 守卫 / RunReadConfig / RunApplyDpi / RunConfigDump /
   Preferences。界面只负责把状态画出来。

════════════════════════════════════════════════════════════════
二、总栅格与窗口（本次最大的结构变化）
════════════════════════════════════════════════════════════════
- 窗口：Width=1020、Height=660（MainForm 构造函数里改）。外壳 Content Border 圆角 20 → 24。
- surface 与四个 NewPage() 的尺寸一律改成 1020×660（页面 Canvas 无 Background，保持可穿透命中）。
- 固定栅格（绝对坐标，全部以窗口左上角为原点）：

  | 区域 | x | y | w | h |
  | --- | --- | --- | --- | --- |
  | 顶栏拖动区（DragMove） | 0 | 0 | 1018 | 66 |
  | 左侧 Dock 容器 navDockContainer | 34 | 76 | 100 | 506 |
  | 页面内容区（卡片左边界 / 右边界） | 150 | 76 | 846（右边界 996） | 506（下边界 582） |
  | 页脚操作条 | 150 | 598 | 846 | 36 |

- 三条硬口径：
  1. 卡片统一 `x=150`、宽 846，右边界 996（窗口右留白 24）；卡与卡纵向间距 16。
  2. 页面 Canvas 覆盖整窗（含 Dock 下方区域），但**必须把 navDockContainer 放到 surface.Children 的最后**
     （保证 Dock 在最上层，命中与视觉都不被卡片压住）。
  3. 顶栏所有右侧元素、页脚状态文本，**统一右对齐到 996**。
     （设计稿里它们停在 x=901 / x=966，比内容右边界短 95px、30px，属于原型目测偏差；以 996 为准。）

════════════════════════════════════════════════════════════════
三、顶栏与页脚（BuildChrome 重写）
════════════════════════════════════════════════════════════════
放置规则（本项目既有写法）：TextBlock 显式给 x / y / 宽 / 高，y = 行中心 − 高/2，
高取 ceil(字号×1.4)；下表给的是**行中心 Y** 与基线量级的字号。

| 元素 | 位置 / 尺寸 | 字号 · 字重 · 颜色 |
| --- | --- | --- |
| 标题「雷柏 VT9 控制台」 | 左 32，行中心 34 | 20 · SemiBold · #111827 |
| 状态行 deviceText | 左 32，行中心 52 | 13.5 · Regular · #64748B |
| 「读取于 09:36:41」 | 右对齐 838，行中心 38 | 13.5 · Regular · #64748B |
| 读取配置按钮 | (854, 21, 94, 34) r17，底 #EDF2F7 | 13.5 · SemiBold · #475569 |
| 关闭按钮 | (960, 20, 36, 36) r10，底透明 | X 图标 16px、stroke 2、#475569 |

- 读取配置按钮：busy 时文案「读取中…」+ Opacity 0.6（沿用现有 Invalidate() 逻辑），
  hover 底色 → #E4EAF2（120ms），pressed → RenderTransform ScaleTransform 0.95（120ms）。
- 关闭按钮：hover 底 #D64A3E（120ms）+ 图标转白；点击仍是「收起至托盘」Hide()，
  ToolTip 与 AutomationProperties.Name 保留现状文案。

页脚（y=598，高 36）：

| 元素 | 位置 / 尺寸 | 字号 · 字重 · 颜色 |
| --- | --- | --- |
| 收起至托盘 | (150, 598, 116, 36) r14，底 #E2E8F0 | 13.5 · SemiBold · #334155 |
| 复制诊断信息 | (276, 598, 134, 36) r14，底 #E2E8F0 | 13.5 · SemiBold · #334155 |
| 状态文本（footerStatusText） | 右对齐 996，行中心 616 | 13.5 · Regular · #64748B |

- 两个页脚按钮改成独立 pill（不再是嵌在一整块页脚卡片底里的 StackPanel）：
  删除原 footerBar 那块 682×44 的卡片底，直接放两个 Border+TextBlock 组合的按钮
  （命中用 Border.MouseLeftButtonUp 或保留 Button+buttonStyle，二者取一，别两套并存）。

════════════════════════════════════════════════════════════════
四、左侧 Dock（本次改版的视觉主角，五层结构）
════════════════════════════════════════════════════════════════
容器：navDockContainer = Canvas，位置 (34, 76)，尺寸 100×506，Background=Transparent（必须可命中），
Cursor=Hand，Focusable=true。容器内部坐标 = 减去 (34,76)。

位置表（i = 0..3：概况 / DPI / 参数 / 设置）：

| 量 | i=0 | i=1 | i=2 | i=3 |
| --- | --- | --- | --- | --- |
| 项中心 Y（容器内 / 绝对） | 47 / 123 | 151 / 227 | 255 / 331 | 359 / 435 |
| 胶囊与透镜视窗 top（容器内） | 9 | 113 | 217 | 321 |
| 位移 offset（px） | 0 | 104 | 208 | 312 |

步长 NavStep = 104（旧值 82，必须改，包括 GetNavIndexFromLocalY 与所有吸附计算）。

第 1 层 · 玻璃底座（Border，100×506，r20，IsHitTestVisible=false）：
  - 填充：横向 LinearGradientBrush，白 A=217 (0.85) → A=128 (0.50) → A=191 (0.75)；
  - 描边：纵向 LinearGradientBrush，白 0.95 → 0.40，Thickness 1.2；
  - 阴影：**只挂一层** DropShadowEffect（Direction=270、ShadowDepth=8、BlurRadius=14、
    Color=#7E8DA3、Opacity=0.16）。两层叠加会让这块 100×506 的大面在胶囊移动时重复离屏渲染，
    不值得（设计稿的双 feDropShadow 在这里降级成一层，写进验证记录）。

第 2 层 · 常态标签（四个 TextBlock，IsHitTestVisible=false）：
  - 18px · Regular · #4B525D；水平居中于容器 x=50（绝对 84）；垂直中心 = 上表「项中心 Y」。
  - 文本框宽 100（x=0..100），TextAlignment=Center，行高 = ceil(18×1.4)=26 → y = 中心 − 13。

第 3 层 · 胶囊滑块 navPill（Border 80×76 @ 容器 (10, top)，r20，IsHitTestVisible=false）：
  - 填充：对角 LinearGradientBrush #CFD8F9 (A=230) → #BBC8F5 (A=204)；
  - 描边：对角 #FFFFFF → #9EB1F2，Thickness 1.2；
  - 顶部镜面高光：一条 Line（容器内 (15, top+1.5) → (65, top+1.5)），白、Thickness 1.2、
    StrokeStartLineCap=Round，Opacity 0.9；**该 Line 必须和胶囊一起移动**（放进同一容器子树）；
  - 阴影：一层 DropShadowEffect（ShadowDepth=5、BlurRadius=7、Color=#3C52A3、Opacity=0.22）；
  - RenderTransformOrigin = (0.5, 0.5)，RenderTransform = TransformGroup(ScaleTransform → TranslateTransform)。
  - 删除设计稿里没有的旧装饰：旧的 `#223B4BD8` 底 + `#383B4BD8` 描边的半透明蓝块外观整体替换掉。

第 4 层 · 透镜视窗（Lens Clip，本次新增）：
  - 一个 Canvas（视窗）尺寸 80×76，位置与胶囊**完全相同**（容器内 (10, top)），
    `Clip = new RectangleGeometry(new Rect(0, 0, 80, 76), 20, 20)`；
  - 视窗内容 = 同样的四个标签，但 **18px · SemiBold · #22379B**，垂直中心相对视窗顶端 = 38 + i×104
    （即 38 / 142 / 246 / 350），水平居中于 40；
  - 视窗与胶囊**共享同一个 Y 源**：把胶囊的 TranslateTransform.Y 当作唯一的动画值，
    视窗自身的 TranslateTransform.Y 与它同值（视窗加胶囊各一份 TranslateTransform，
    两处都从同一个 AnimateTo 目标写），视窗内部再放一个 TranslateTransform(0, −offset) 抵消，
    用它上面的 `Binding`（绑定到胶囊 translate 的 Y + 取反转换器）保持逐帧一致。
    —— 一句话口径：**胶囊和裁剪窗口的位移必须永远相等，否则折射幻觉立刻塌**。
    实现二选一，都要保证"只有一个动画值"：
      a) `BindingOperations.SetBinding(contentTranslate, TranslateTransform.YProperty,
         new Binding("Y"){ Source = pillTranslate, Converter = NegateConverter })`；
      b) 视窗与胶囊共用一个 TranslateTransform 实例（Freezable 可共享），
         视窗内容再用绑定取反。
  - 本设计稿的透镜层**不放大**（与常态层同为 18px，只换字重与颜色）；
    不要照搬 liquid-lens-dock.md 里 340×520 那版的 1.1x 放大——放大必须同时改视窗与内容偏移，
    极易脱同步，收益不抵风险。

第 5 层 · 手势（容器独占，沿用 1.17 的做法）：
  - 按下：记录起点、把胶囊当前 Y 从动画里取出固化（BeginAnimation(null) 后写回当前值）；
  - 移动：阈值 4px 后才算拖拽（旧值 3px，改到 4px）；位移 1:1 跟手、**不加缓动**；
    越界用指数阻尼 `damped = allowed·(1−e^(−pull/40))`，allowed = 14px（正常区间 0..312）；
    贴壁时把胶囊 ScaleY 压到 0.96、ScaleX 取 1/√0.96 ≈ 1.02（"被挤在壁上"的暗示）；
  - 松手：|速度| ≥ 520px/s → 前进/后退一项；否则最近中心磁吸；
    时长 = clamp(剩余距离 / max(400, 速度) × 1000, 160, 340)，曲线 SlideEase.Shared；
  - 拖动过就吞掉本次 click（保留 isSwiping 标志，不要只靠时间戳）；
  - 点击：直接 SelectPage(i)，胶囊位移 320ms（设计稿 0.32s，本项目基准 320ms 用于指示器，见 §七）；
  - 键盘与语义一并补：↑/↓ 切换页、Home/End 首末页、Enter/Space 选中；
    给容器与四个标签设 AutomationProperties.Name（如「概况，已选中」）。

════════════════════════════════════════════════════════════════
五、四个页面逐卡坐标（全部绝对坐标；卡片圆角 Card() 改 20）
════════════════════════════════════════════════════════════════
所有卡片 x=150 / 宽 846，除非表格另注；卡片标题 18px SemiBold #111827；
行标签 14.5px #64748B；行读数 15px SemiBold #111827 且右对齐。

── 5.1 概况页 ──
设备卡 (150, 76, 360, 336)：
  - 标题「设备」基线 (174, 110)；右侧「2.4G 无线」右对齐 486，同行，13.5px #8A94A6；
  - 产品图区 (174, 128, 312, 175) r14、底 #EEF2FB，鼠标矢量图形居中等比缩放；
    **沿用现有 Path 画法**（比设计稿的圆角矩形占位形状好看得多）；
  - 三行（label 左 174 / value 右对齐 486）：电量 基线 330、充电状态 基线 362、连接模式 基线 394；
    电量用 18px SemiBold（未获取时用 #C8643A 低电量色），另两行 15px SemiBold。
参数卡 (526, 76, 470, 336)：
  - 标题「参数」基线 (550, 110)；
  - 六行（label 左 550 / value 右对齐 972）；基线 156 / 202 / 248 / 294 / 340 / 386：
    回报率、静默高度、进入休眠、按键消抖、运动同步、当前档位。
启动与提醒卡 (150, 428, 846, 152)：
  - 标题「启动与提醒」基线 (174, 462)；
  - 标题行右侧（设计稿没有、功能必需，按此并入）：
    「读取原始配置块」pill (846, 444, 126, 30) r12，底 #EDF2F7、字 13px SemiBold #475569；
    其左侧「固件 未知 · 程序 1.18.0」右对齐 830，13.5px #8A94A6（版本号从程序集读，不要手写）；
  - 行 1（中心 493）：Switch (174, 480, 48, 26)；标签「自动启动」左 232、15px SemiBold；
    描述「开机自动启动服务并收起至托盘」左 310、13.5px #64748B；
    状态读数右对齐 970、14.5px SemiBold（未启用 #8A94A6 / 已启用 #111827）；
  - 行 2（中心 539）：同上，「低电量提醒 / 鼠标电池电量低于 20% 时弹出通知气泡预警 / 已开启 · 阈值 20%」；
  - 卡片底行：dump 摘要（RunConfigDump 的输出）左 174、基线 570、13.5px #55605A、
    单行 CharacterEllipsis + ToolTip 全文。

── 5.2 DPI 页 ──
当前档位卡 (150, 76, 846, 106)：
  - 「当前档位」基线 (174, 106)，14px SemiBold #1E293B；
  - 大数字 dpiValueText 基线 (174, 150)，40px SemiBold #111827（雅黑无 800，用 SemiBold）；
  - 右侧「共 N 档 · 首档 – 最大档」右对齐 972，基线 106，13.5px #64748B；
  - 「快捷切换」右对齐 528，行中心 144，14px #64748B；
  - 七个档位 pill：56×32、r10、间距 6、**右对齐到 972**（即起点 x=544），中心 y=144；
    未选中底 #EDF2F7/字 #475569，选中底 #304FFE/字 #FFFFFF（**内容必须是设备实际档位值**，
    不要写死设计稿的 800/1200/1600/2000/3200/6400/20000）。
7 档调节卡 (150, 194, 846, 388)：
  - 行 i（i=0..6）行中心 Y = 225 + i×44 → 225 / 269 / 313 / 357 / 401 / 445 / 489；
  - 每行（绝对坐标）：
    · Switch 48×26 @ (174, 中心−13)；
    · 档位序号（1..7）15px、中心 (242, 中心)，激活档 #304FFE SemiBold、其余 #64748B；
    · 滑轨：起点 x=268、长 480、行中心线上；未填充段 4px 圆头 #E2E8F0、已填充段 #304FFE、
      Thumb 15px 白底 + #304FFE 描边 3px；未启用档整体 Opacity 0.45 且轨色退成 #A5B4FC；
      **继续用 WPF Slider 控件**（保留 Minimum=50 / Maximum=20000 / TickFrequency=50 /
      IsSnapToTickEnabled / IsMoveToPointEnabled 与键盘可达），只改宽 340→480 与模板视觉；
      不要照搬设计稿的 px↔DPI 公式（x/0.06、clamp 100..8000——它跟真实范围不符，见 §八）；
    · 数值 15px SemiBold，**右对齐 890**（未启用显示「未启用」#8A94A6）；
    · 「−」按钮 (906, 中心−13, 30, 26) r8、「+」按钮 (942, 中心−13, 30, 26) r8；
      （设计稿把它们放在 824/842/878、右侧空 88px；这里按"右对齐到 972"的口径收齐）
  - 底部按钮：应用到设备 (174, 522, 180, 40) r12 底 #304FFE 白字 14.5px SemiBold；
    恢复设备值 (370, 522, 180, 40) r12 底 #EDF2F7 字 #475569。
  - 本卡**不画刻度**（设计稿里也没有）；刻度语义由顶部快捷 pill 承担。

── 5.3 参数页 ──
回报率卡 (150, 76, 846, 112)：标题基线 (174, 110)；
  四个 pill (174 + i×98, 130, 86, 38) r12、中心 y 149：125 / 250 / 500 / 1000 Hz。
静默高度卡 (150, 200, 846, 112)：标题基线 (174, 234)；
  三个 pill (174 + i×98, 254, 86, 38)：1 / 2 / 3 mm；
  运动同步 Switch (835, 261, 48, 26)，标签左 894、15px SemiBold、中心 y 274。
进入休眠等待时长卡 (150, 324, 846, 126)：
  - 标题基线 (174, 358)；副说明「有线连接时鼠标不休眠」左 710、基线 358、13.5px #8A94A6；
    当前值右对齐 972、基线 358、16px SemiBold #111827；
  - 滑轨：起点 x=174、长 680、行中心 386（Slider 保持 5..120）；
  - 档位 pill (174 + offset, 402, w, 32) r10：offset 0/70/140/216/286，宽 60/60/66/60/60，
    文案 5 / 10 / 30 / 60 / 120（单位由卡片标题与右侧读数承担）；
    **删掉原来 AddSliderTicks 画在 y≈318/324 的两排刻度**（设计稿没有，且会与 pill 行叠字）。
按键防抖延迟卡 (150, 462, 846, 120)：
  - 标题基线 (174, 494)；
  - 模式 pill「按下」(820, 470, 70, 30) r10、「抬起」(896, 470, 70, 30) r10（选中底 #304FFE 白字）；
  - 毫秒 pill (174 + offset, 510, w, 36) r12：offset 0/80/160/240/324/408/492，
    宽 72/72/72/76/76/76/76，文案 1/2/4/8/16/24/32 ms；
  - 提示行 paramMessage：左 174、基线 570、13.5px #2E3AB0、单行省略 + ToolTip
    （设计稿无此行；原来它单独占 y=492 一行，现在收进卡内底部）。

── 5.4 设置页 ──
外观与材质卡 (150, 76, 846, 236)：
  - 标题基线 (174, 110)；说明「调节毛玻璃 Acrylic 材质与卡片半透明度」左 292、基线 110、13.5px #8A94A6；
  - 「恢复默认」pill (870, 92, 102, 34) r12；
  - 分组 1 (174, 138, 798, 62) r20 底 #F8FAFC：
    标题「卡片背景透明度」14.5px SemiBold #1E293B 左 192、基线 169；
    读数右对齐 972、基线 169、15px SemiBold #304FFE；滑轨 起点 192、长 760、行中心 186；
  - 分组 2 (174, 214, 798, 62)：标题「整窗背景透明度」基线 (192, 245)、读数 (972, 245)、滑轨中心 262；
  - 组内滑轨继续用现成的 cardTransparencySlider / windowTransparencySlider 与实时保存逻辑；
    设计稿没有的「0% 不透明 / 100% 完全透明」两行小字删掉，语义改放分组的 ToolTip；
  - 保存提示行 appearanceSaveStatus：左 174、基线 292、13.5px #55605A（设计稿无，功能必需）。
程序与运行环境卡 (150, 326, 846, 256)：
  - 标题基线 (174, 360)；版本「VT9 控制台 1.18.0」右对齐 972、基线 360、13.5px #8A94A6（程序集读）；
  - 四行：label 14.5px #64748B 左 174，值 14.5px SemiBold #111827 左 274；基线 396 / 436 / 476 / 516：
    适配设备 / 通信通道 / 背景材质 / 界面系统（文案沿用现状）。

════════════════════════════════════════════════════════════════
六、字体（Fonts.cs 是唯一来源，改这一处）
════════════════════════════════════════════════════════════════
字体族**不动**：仍然只有 `Microsoft YaHei UI`（WPF 与 GDI 同一份定义，见 1.14 的收敛结论）。

| 常量 | 现状 | 新版 | 用途 |
| --- | --- | --- | --- |
| Meta | 12 | 12 | 滑块刻度（本次只剩参数页刻度，DPI/休眠不再画） |
| Sub | 13 | 13.5 | 卡片说明、页脚、状态、右侧 meta |
| Chip | — | 13.5 | 新增：pill / chip 文字 |
| Body | 14 | 14.5 | 行标签、开关标签、大按钮文字 |
| Step | — | 16 | 新增：± 微调按钮 |
| Nav | 15 | 18 | Dock 标签（本次最显眼的一处放大） |
| Label | 16 | 18 | 卡片标题 |
| Heading | 18 | 18 | 沿用 |
| Value | 15 | 15 | 行读数 |
| ValueHero | 18 | 18 | 电量 |
| Display | 38 | 40 | DPI 大数字 |
| Title | 20 | 20 | 窗口标题 |

字重口径（重要，别在验证记录里写"实现了 600/700/800 三档"）：
- 微软雅黑 UI 只有 Regular 与 Bold 两个真实字面，WPF 的 SemiBold/Bold/ExtraBold 最终都落到
  Bold 面（或合成），**视觉上分不出来**。所以：
  · 层级靠**字号 + 颜色**表达，不靠字重档位；
  · 全站只保留 Regular / SemiBold 两个取值，Dock 激活项用「#22379B + 1.08 倍缩放」而不是加粗；
  · pill 选中/未选中**都固定 SemiBold**（WPF 不能动画 FontWeight，硬切字重会有"跳一下"的观感）。

════════════════════════════════════════════════════════════════
七、颜色与材质（先收敛，再改版）
════════════════════════════════════════════════════════════════
第 0 步：把散落在 MainForm.Layout.cs / MainForm.Appearance.cs / MainForm.Swipe.cs 里的颜色字面量
（#1F2724、#45514D、#55605A、#697570、#8A9791、#141F2724、#123B4BD8、#223B4BD8、#383B4BD8、
#B6BEBD、#D8E0DC、#2E3AB0、#F5F7FC、#E3E7F7、#C9CFF7、#A6FFFFFF、#99FFFFFF …）
全部换成 Theme 常量（自检命令：rg -n '#[0-9A-Fa-f]{6,8}' src）。不收敛的话，这次换色必漏。

Theme 新调色板：

| 常量 | 值 | 用途 |
| --- | --- | --- |
| Ink | #111827 | 标题、读数 |
| InkSoft | #4B525D | Dock 常态标签、次级正文 |
| Muted | #64748B | 行标签、说明、页脚 |
| Faint | #8A94A6 | 右侧 meta、未启用文字 |
| Accent | #304FFE | 选中底色、滑轨已填充段 |
| AccentDeep | #22379B | Dock 激活文字、透镜层文字、链接/提示 |
| AccentPressed | #2A45E0 | 按下态底色 |
| Surface | #FFFFFF | 卡片底 |
| ChipBg | #EDF2F7 | 未选中 pill |
| ChipHover | #E4EAF2 | pill / 按钮 hover |
| GroupBg | #F8FAFC | 设置页分组块 |
| Track | #E2E8F0 | 滑轨底 |
| SwitchOff | #CBD5E1 | 开关关闭 |
| FooterBtn | #E2E8F0 / FooterInk #334155 | 页脚按钮 |
| CloseHover | #D64A3E | 关闭按钮 hover |
| Hairline | #E2E8F0 | 备用细线（本版基本不用） |

材质口径：
- 卡片圆角 18 → **20**；分组 20；pill 10（小）/12（中）；页脚按钮 14；底座 20；胶囊 20；
  外壳 24；产品图区 14。
- 卡片底沿用 `CardBackgroundBrush`（受设置页「卡片背景透明度」控制，默认 45%）；
  描边保持 1px 高光白（收敛成 Theme 常量）。
- **不叠加设计稿的整窗渐变 win-bg-grad**：窗口背景归 DWM 原生 Acrylic 所有，
  叠加一层渐变会把材质盖掉。该渐变只作为「Acrylic 不可用」时的实色回退
  （ApplyBackdrop 里的 #F6F8F7 改成 #EDF1F6，贴近设计稿中段）。
- **不给卡片挂 DropShadowEffect**：一页 3–4 张、四页十几张的真实矢量面，静态重绘代价高、
  小字边缘还会发虚。设计稿的 card-soft-shadow 用"1px 高光描边 + 共享白底画刷"降级表达。
  只有两块玻璃（底座、胶囊）各挂一层阴影。

════════════════════════════════════════════════════════════════
八、点击动效（逐控件）
════════════════════════════════════════════════════════════════
统一入口：一律走 `Transition.AnimateTo`（读当前值续接、可打断、系统关动画时一步到位）；
曲线一律 `SlideEase.Shared`；**禁止** KeyFrames / Storyboard / 事件里直接 Emphasized 动画。

1. 开关 Switch（CheckBox 模板重写，switchStyle）：
   - 尺寸 40×22 r11 → **48×26 r13**，knob 16 → 18、圆角/边距 4；
   - 关闭底 #CBD5E1 → 开启底 #304FFE：200ms **ColorAnimation**；
     Track 的 Background 必须是**每个控件独立的 SolidColorBrush**（不能用共享/冻结画刷，
     否则整页开关一起变色——现有 chip 共享画刷的坑不要重犯）；
   - knob 位移：`TranslateTransform.X` 0 → 22，220ms SlideEase（末段回弹约 4.8%，
     近似设计稿的 cubic-bezier(0.34, 1.56, 0.64, 1)）。**不要**用 HorizontalAlignment 切换
     （那是布局属性，会触发重新测量，且不可动画、不可打断）；
   - 程序化改值（syncing 路径）照常播动画，但**不得触发写设备**（syncing 守卫原样保留）；
   - 禁用态保持 Opacity 0.4。
2. 选项 pill / chip（chipStyle + 页脚/动作用 buttonStyle）：
   - 尺寸按 §五 的表；圆角 10 或 12；
   - 选中：底 #EDF2F7 → #304FFE、字 #475569 → #FFFFFF，180ms ColorAnimation；
   - hover：底 → #E4EAF2，120ms；pressed：`ScaleTransform 0.95`（± 按钮 0.92），120ms，
     松手回 1.0 用 SlideEase（轻微回弹）。实现上给按钮模板外面套一层（外层定位、内层动画），
     保证 RenderTransform 不与布局打架；
   - 每个 chip 必须持有自己的画刷实例；`SyncChips` 里改 `.Color` 而不是换画刷对象。
3. ± 微调按钮：30×26 r8，hover #E4EAF2、pressed 0.92；图标沿用 LineIcon。
4. 关闭按钮：hover 底 #D64A3E + 图标白，120ms。
5. 状态 / 文案交叉淡化（原则 3）：
   - 触发点：DPI 大数字换档、快捷 pill 切换档位、读取按钮「读取 ↔ 读取中…」、
     概览读数在「读取完成 / 充电状态变化」时、页脚状态行状态变化；
   - 做法沿用现有 FadeText：Opacity 1→0.3（40ms）→ 换文本 → Opacity 0.3→1 与 0.96→1（100ms），
     合计 140ms；
   - **不做**淡化的场景：拖动滑块时跟着走的数值、每秒心跳刷新的时间戳/秒数
     （逐帧淡入淡出会抖）。
6. 不做的事：hover 位移、图标旋转、环形涟漪、透明度过冲。这套界面的气质是克制，
   设计稿里也没有这些。

════════════════════════════════════════════════════════════════
九、切换动效（页面切换 / 卡片入场 / 打断）
════════════════════════════════════════════════════════════════
两条同时进行、各自负责一件事：
  A. **Dock 指示**：胶囊 + 透镜视窗一起位移（320ms，SlideEase；拖动松手那条走 §四的
     速度判定与 clamp(…,160,340)）；导航标签颜色与 1.08 缩放 200ms 交叉过渡。
  B. **内容入场**：旧页同帧隐藏（Visibility.Collapsed），新页的卡片错峰从右侧滑入。

为什么 B 不做整页交叉淡化：两页卡片占同一批坐标（x=150 / 846 宽），重叠那一两百毫秒会看到
两层半透明卡片糊在一起。旧页直接撤、新页错峰入场最干净（与 docs\切换动画建议.md 的结论一致；
原则 2 的"退场不飞出"在这里体现为"不飞、直接撤"，而不是"滑出屏幕"）。

卡片入场的实现：
1. 构建期登记：每个页面构建时把该页所有卡片元素记进
   `List<(FrameworkElement el, double baseX, double baseY)>`（在 Card() 里按页面收集即可，
   baseX/baseY 就是 Canvas.Left/Top，构建后不再变）。
2. 播放（PlayPageCards(pageEl)）：
   - 先按 (中心 y, x) 排序（同一行内从左到右）；
   - 每张卡：`RenderTransform = TranslateTransform(72, 0)`、`Opacity = 0`；
   - 动画：X 72 → 0、Opacity 0 → 1，时长 320ms（卡片多或面积大时可 340ms），SlideEase；
   - 错峰：第 i 张的 BeginTime = i×45ms（设计稿 0.42s / 55ms；本程序取 320ms / 45ms，
     四页最多 4 张卡，入场总时长 ≈ 455ms）；
   - 完成后：清 Effect / CacheMode（本次不用模糊，所以只剩"确保为 null"）、
     属性回到精确终值（X=0、Opacity=1）、`Program.Log("卡片入场完成：" + 页名 + "（" + n + " 张，" + ms + " ms）")`。
3. 打断（原则 1）：
   - 新目标到来 → 先对**上一页**所有卡片 `BeginAnimation(prop, null)` 并落定终值
     （旧页已隐藏，不需要续接）；
   - 若新目标就是当前正在入场的页（连点同一个 tab），从**当前值**续接到终值，
     时长按剩余距离重算：clamp(剩余/ max(400, 0) ×1000, 160, 340)，绝不闪回 72px 重放。
4. 首屏与降级：窗口 IsVisible == false（构建期那次 NavigateToPage）或
   `SystemParameters.ClientAreaAnimation == false` / `HighContrast` 为真 → 直接落位，不播动画。
5. 时长与曲线总表：

  | 动作 | 时长 | 曲线 |
  | --- | --- | --- |
  | 胶囊/透镜位移（点击） | 320ms | SlideEase |
  | 胶囊位移（拖动松手） | clamp(剩余/max(400,v)×1000, 160, 340) | SlideEase |
  | Dock 标签颜色 + 1.08 缩放 | 200ms | SlideEase |
  | 卡片入场（每张） | 320ms，错峰 45ms | SlideEase |
  | Switch 底色 / knob | 200ms / 220ms | 线性颜色 / SlideEase |
  | Pill 颜色 / pressed | 180ms / 120ms | SlideEase |
  | 文字交叉淡化 | 40ms 出 + 100ms 入 | SlideEase |

════════════════════════════════════════════════════════════════
十、硬约束（违反就算没做完）
════════════════════════════════════════════════════════════════
1. 动画只碰 `RenderTransform` / `Opacity` / 颜色画刷；**绝不**动 Canvas.Left/Top/Width/Height/
   Margin（手算坐标的界面动布局属性会牵动测量、描边与命中区）。
2. 不给窗口本体做 Opacity / Scale 动画；不给 surface 加整窗渐变或 ClipToBounds；
   Acrylic 路径（ApplyBackdrop / DwmSetWindowAttribute 38 / 250ms 两帧过渡）原样保留。
3. 静止态必须干净：动画结束后所有 `Effect == null`、`CacheMode == null`、属性是精确终值。
   过渡期若用到 BitmapCache，结束回调里立刻清掉（12–15px 小字禁不起位图缓存）。
4. 尊重系统开关：`SystemParameters.ClientAreaAnimation == false` 或 `HighContrast` → 瞬时到位，
   不报错、不半播。
5. 拖动必须抢鼠标捕获（CaptureMouse），拖动过就吞掉 click（保留 isSwiping 而非时间戳判断）。
6. 打断要"就地结算"：只允许用 `BeginAnimation(prop, null)` 把当前值取出来当起点，
   不许把元素弹回旧位置再重播。
7. 不引入 XAML 文件 / 资源字典 / NuGet 依赖；继续用 TryParseStyle 的内联模板字符串。
8. 不动数据层：HidReader / ChargeMonitor / DpiController / SettingsController / ConfigBlocks /
   Program 的读写与诊断路径；不动 TrayMenu.cs 的交互（只允许版本号与文案随之更新）。
9. 功能不许丢：读取原始配置块、dump 摘要、paramMessage、外观保存提示、固件/程序信息、
   「收起至托盘 / 复制诊断信息 / 关闭」全部保留，按 §五 给的位置并入新栅格。
10. 中文注释，注释里写"为什么"（沿用本仓库的注释风格）。

════════════════════════════════════════════════════════════════
十一、落点（文件 / 函数级）
════════════════════════════════════════════════════════════════
- src\MainForm.cs：Width/Height 1020×660；`PreviewStage` 相关不动。
- src\Fonts.cs：§六 的字号表（新增 Chip / Step；Nav 15→18、Label 16→18、Body 14→14.5、Sub 13→13.5）。
- src\Theme.cs：§七 的调色板 + 新常量；被替换的字面量全部删掉。
- src\MainForm.Layout.cs：
  · BuildSurface：外壳圆角 24；NewPage 尺寸 1020×660；把 navDockContainer 移到子元素最后。
  · BuildChrome：§三 的顶栏与页脚重写（删掉整块 footerBar 卡片底）。
  · BuildNav：§四 的五层结构（底座 / 常态标签 / 胶囊 / 透镜视窗 / 热区），NavStep 82 → 104。
  · BuildOverviewPage / BuildDpiPage / BuildParamsPage：§五 的全部坐标与尺寸。
  · Card()：圆角 18 → 20，并登记卡片的 (x, y) 供入场排序。
  · Switch 模板（switchStyle）：48×26 r13、knob 18（在 switchStyle 里改，别每处手写）。
- src\MainForm.Appearance.cs：BuildSettingsPage 按 §五 5.4 重排；
  BuildTransparencyGroup 的分组外观改成 798×62 r20 #F8FAFC；版本号改从程序集读。
- src\MainForm.Swipe.cs：
  · NavStep / GetNavIndexFromLocalY / ClampInsideShelf / 吸附与速度判定按 §四 更新；
  · 胶囊 + 透镜视窗的联动（单一动画值）、贴壁压缩 ScaleY 0.96；
  · PlayPageCards / 卡片登记 / 入场时长与错峰 / 打断处理；
  · FadeText 扩展到 §八 第 5 条的触发点；
  · 新日志「卡片入场完成：…」，保留「页面切换动画完成：…」。
- src\Program.cs：
  · PreviewStage 尺寸 1120×760 → 1280×860（或把预览窗口挪到 Left=140, Top=110），
    保证 1020×660 的主窗完全落在彩色背景内，否则 --ui-preview-bg 右侧会缺 20px 背景、
    像素探针采到桌面。
  · 可选但推荐：预览路径额外写一份 data\layout-report.json，
    记录卡片 / 底座 / 胶囊 / 页脚的矩形，便于与设计表逐项 diff。
- src\AssemblyInfo.cs：1.18.0.0。

════════════════════════════════════════════════════════════════
十二、验收（可执行、可判定的清单）
════════════════════════════════════════════════════════════════
1. 编译：`powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -OutputDirectory artifacts\svg-layout`
   通过；产物 FileVersion = 1.18.0.0。
2. 自检：先删 data\self-test.json（SaveJson 用 File.Replace，目标已存在时在受限 ACL 环境会失败），
   再跑 `artifacts\svg-layout\VT9Status.exe --self-test`，输出含 "result":"PASS"；
   原有 58 项一项都不许少。
3. 四页截图：
   `--ui-preview-bg --page=overview | dpi | params | settings`
   截图尺寸应为 **1275×825**（1020×660 × 1.25 DPI）；把四张图与设计稿逐项对照：
   底座 100×506 / 胶囊 80×76 / 标签 18px / 卡片 x=150 宽 846 / 顶部六行基线 /
   页脚 116+134×36 / 卡片圆角 20。
4. 日志证明动画播过：data\app.log 里能看到
   「页面切换动画完成：概况」与本次新增的「卡片入场完成：概况（3 张，455 ms）」
   （"丝滑"是时间维度的，截图证明不了）。
5. 透镜与胶囊同步：静止时采样像素确认「胶囊左边缘」与「透镜裁剪左边缘」都在
   逻辑 x=44（截图 x=55），误差 ≤1px；移动过程中（动画中段截图）两者依然重合。
6. 透镜层语义：静止在「概况」时，采样逻辑 (84, 123) 附近应能取到 #22379B 的笔画像素，
   而 (84, 227)（第二项中心）**不应**出现该深紫蓝（被裁剪掉，只有常态 #4B525D）。
   采样按 1.25 倍换算坐标，用 System.Drawing.Bitmap.GetPixel。
7. 打断测试（原则 1 的关键验收）：切页/入场进行到一半时立刻点另一个 tab 或拖动 Dock，
   画面必须从当前位置续接，不能闪回起点；连续快速点四个 tab 不出现空白页、
   胶囊不停在半路、卡片不留半透明残影。
8. 拖动跟手：按住 Dock 上下拖动，胶囊 1:1 跟手（无缓动滞后），到两端位移渐变式贴壁
   （永不越过 ±14px），松手按 520px/s 或最近中心吸附。
9. 静止态检查：入场结束后遍历所有页面元素，Effect / CacheMode 全为 null，
   RenderTransform 位移回到 0、Opacity 为 1。
10. 降级路径：`SystemParameters.ClientAreaAnimation = false`（关闭系统动画）与高分对照下，
    一切瞬时到位、无异常、无半播。
11. 状态不硬切：DPI 换档、读取按钮文案、页脚状态行肉眼可辨的交叉淡化；拖动滑块时数值不闪。
12. 打包与文档：
    `powershell -NoProfile -ExecutionPolicy Bypass -File .\pack.ps1 -Version 1.18.0 -Preview -Zip -Archive`；
    新写 `验证记录-1.18-SVG改版.md`（原则落地、坐标表、曲线与参数、透镜同步的测量结果、
    §十三 里每一条偏离的理由）；
    更新 `使用说明.md` 里关于窗口尺寸/卡片坐标/字号档位/页脚结构的描述（现在还写着 800×614、
    x=96/412、682 宽、页脚卡片底）。

════════════════════════════════════════════════════════════════
十三、设计稿里"不要照抄"的地方（逐条，照抄即错）
════════════════════════════════════════════════════════════════
1. 整窗渐变 win-bg-grad → 不叠加，窗口背景归 DWM Acrylic（只做回退实色 #EDF1F6）。
2. 卡片投影 card-soft-shadow → 不给每张卡挂 DropShadowEffect（降级为 1px 高光描边）。
3. DPI 快捷 pill 的固定值（800…20000） → 必须显示设备实际档位值（draftValues）。
4. DPI 滑轨的 px↔DPI 公式（x/0.06、clamp 100..8000） → 与真实档位表矛盾；
   保留 WPF Slider 的 50..20000，只取 480px 的视觉长度。
5. 设计稿脚本里的 state / switches.dpi 第二份状态机 → 不要引入；一律接现有
   CheckBox / Slider / chip / config / draft / syncing。
6. 透镜层 1.1x 放大（姊妹版 340×520 的写法） → 本设计稿不放大，只换字重与颜色。
7. will-change / pointer-events / user-select / touch-action / dominant-baseline → WPF 无对应，
   不写；文本垂直居中用"显式高度 + 行中心"的既有约定。
8. text-anchor:end 的散点 → 一律右对齐到 996（顶栏右侧簇、页脚状态、行读数统一口径）。
9. ± 按钮的 824/842/878 与设计稿右侧 88px 空档 → 按右对齐 972 收齐（890/906/942）。
10. 底座 506 高、内容只占容器内 9..397（底部空 94px） → **默认照抄**（它与右侧内容区
    76..582 和页脚 598 对齐）；若实机观感底部空档明显，允许改成自适应高度 434 并在
    内容区里垂直居中（绝对 112..546），但必须在验证记录里写明这是有意的偏离。
11. 设计稿的 40px/800 字重、18px/700 卡片标题等重字重 → 雅黑只有 Regular/Bold，
    统一落到 SemiBold，层级靠字号与颜色（见 §六，别在文档里吹"实现了 800 字重"）。
12. 顶栏「读取于」/页脚状态文本在 1020 宽窗口里的绝对 x（735 / 966）→ 按右对齐重算
    （838 / 996），避免右侧出现 30–95px 的错位空档。

════════════════════════════════════════════════════════════════
十四、备注
════════════════════════════════════════════════════════════════
- 1020×660 在 1366×768 上仍有余量（窗口居中，左右各留 173px）；不需要为小屏做降级方案。
- 根目录的 VT9Status.exe 常被正在运行的程序独占锁定；装机用 pack.ps1 -Install（会等旧实例退出）。
- 若改动中途发现某处坐标与设备语义冲突（例如档位值位数过多撑破 pill），
  优先保证"数据正确 + 不重叠"，把视觉微调写进验证记录，不要为了对齐设计稿把功能改坏。
```

---

## 这份提示词做了什么（速览）

| 面 | 现状（1.17.1 实际代码） | 目标（按 SVG） |
| --- | --- | --- |
| 窗口 | 800×614，外壳圆角 20 | 1020×660，外壳圆角 24 |
| 内容栅格 | 卡片 x=96..778（宽 682），间距 12 | 卡片 x=150..996（宽 846），间距 16 |
| Dock | 62×314 @ (7,69)，项 56×56、步长 82，只有「底座 + 半透明蓝块」 | 100×506 @ (34,76)，项 80×76、步长 104，五层结构：底座 / 常态标签 / 渐变胶囊 + 顶部高光 / **透镜裁剪层（#22379B）** / 热区 |
| 字体 | Meta 12 / Sub 13 / Body 14 / Nav 15 / Label 16 / Display 38 | 12 / 13.5 / 14.5 / **18** / **18** / 40，新增 Chip 13.5、Step 16 |
| 配色 | Accent #3B4BD8，颜色字面量散落 6 个文件 | Accent #304FFE、AccentDeep #22379B 等，全部收敛进 `Theme.cs` |
| 点击动效 | 只有 Hover/Pressed 改 Opacity，开关靠 `HorizontalAlignment` 硬跳 | 开关 48×26 + knob 位移 220ms 回弹、pill 颜色 180ms + 按下 0.95 缩放、文本 140ms 交叉淡化 |
| 切换动效 | 只有胶囊 220ms 位移，页面同帧 `Visibility` 切换 | 胶囊 + 透镜视窗同源位移 320ms，新页卡片 320ms 错峰滑入（72px→0，45ms 步进），可打断、旧页直接撤 |
| 明确降级 | — | 卡片阴影、整窗渐变、1.1x 透镜放大、800 字重、px↔DPI 公式 共 12 条「不要照抄」清单 |

需要我直接按这份提示词动手改代码（含 `build.ps1` 编译与四页 `--ui-preview` 对照），说一声即可。

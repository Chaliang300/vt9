using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Markup;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace VT9Status
{
    internal enum PanelPage { Overview, Dpi, Params, Settings }

    internal sealed class NegateConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value is double) return -(double)value;
            return 0.0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value is double) return -(double)value;
            return 0.0;
        }
    }

    internal sealed class ReciprocalConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        { return value is double && (double)value != 0 ? 1.0 / (double)value : 1.0; }
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        { throw new NotSupportedException(); }
    }

    internal sealed class CardHostItem
    {
        public FrameworkElement Element;
        public double X;
        public double Y;
    }

    internal sealed partial class MainForm
    {
        // ── 页面容器与导航 ──
        private Canvas pageOverview, pageDpi, pageParams, pageSettings;
        private Canvas navDockContainer;
        private Canvas navBaseLabels;
        private Border navShelf;
        private TextBlock[] navLabels;
        private Canvas lensWindow;
        private TranslateTransform lensContentTranslate;

        // ── 顶栏与页脚 ──
        private Button globalReadBtn;
        private TextBlock globalReadBtnText;
        private TextBlock globalReadTimeText;

        // ── 概览页 ──
        private TextBlock batteryText, chargeText, ovMode;
        private TextBlock ovPolling, ovLod, ovSleep, ovDebounce, ovMotion, ovStage;

        // ── DPI 页 ──
        private TextBlock dpiValueText, dpiRangeText;
        private CheckBox[] stageSwitches;
        private Slider[] stageSliders;
        private TextBlock[] stageValueTexts, stageIndexTexts;
        private Button[] stageMinus, stagePlus;
        private Button[] dpiStageChips;
        private Button applyButton, revertButton;

        // ── 参数页 ──
        private Button[] pollingChips, lodChips, sleepChips, debounceChips;
        private Button debounceTabPress, debounceTabRelease;
        private bool editingPress = true;
        private Slider sleepSlider;
        private TextBlock sleepValueText, paramMessage;
        private CheckBox motionCheck;

        private Style switchStyle, sliderStyle, chipStyle, chipStyle8, chipStyle12, readStyle;
        private bool syncing;

        // 卡片登记表：供页面错峰入场动画使用
        private readonly System.Collections.Generic.Dictionary<Canvas, System.Collections.Generic.List<CardHostItem>> pageCards =
            new System.Collections.Generic.Dictionary<Canvas, System.Collections.Generic.List<CardHostItem>>();

        // ─────────────────────────────── 基础绘制 ───────────────────────────────

        private static SolidColorBrush ColorBrush(string value)
        {
            return (SolidColorBrush)new BrushConverter().ConvertFromString(value);
        }

        private static Style TryParseStyle(string xaml, string name)
        {
            try { return (Style)XamlReader.Parse(xaml); }
            catch (Exception ex) { Program.Log("界面样式解析失败（" + name + "）：" + ex.Message); return null; }
        }

        // Pill 模板只有一份，圆角是唯一变量。设计稿里 8/10/12/14/17 五档都要用，
        // 与其抄五份 XAML 让它们慢慢漂移，不如按半径生成。
        private static string PillStyleXaml(double radius)
        {
            return @"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'>
 <Setter Property='Cursor' Value='Hand'/>
 <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'>
  <Border Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='" + radius.ToString(System.Globalization.CultureInfo.InvariantCulture) + @"'>
   <ContentPresenter Margin='{TemplateBinding Padding}' HorizontalAlignment='Center' VerticalAlignment='Center'/>
  </Border>
 </ControlTemplate></Setter.Value></Setter>
 <Style.Triggers>
  <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.45'/></Trigger>
 </Style.Triggers>
</Style>";
        }

        private void PlaceIn(Canvas host, FrameworkElement element, double x, double y, double width, double height)
        {
            Canvas.SetLeft(element, x); Canvas.SetTop(element, y);
            element.Width = width; element.Height = height;
            host.Children.Add(element);
        }

        private TextBlock Label(Canvas host, string text, double size, double x, double y, double width, double height)
        {
            TextBlock label = new TextBlock
            {
                Text = text, FontSize = size,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = Theme.Brush(Theme.Muted)
            };
            PlaceIn(host, label, x, y, width, height);
            return label;
        }

        private TextBlock Meta(Canvas host, string text, double x, double y, double width)
        {
            TextBlock label = Label(host, text, Fonts.Sub, x, y, width, 22);
            label.Foreground = Theme.Brush(Theme.Faint);
            return label;
        }

        private TextBlock SubLabel(Canvas host, string text, double x, double y, double width)
        {
            TextBlock label = Label(host, text, Fonts.Body, x, y, width, 22);
            label.Foreground = Theme.Brush(Theme.Muted);
            return label;
        }

        private TextBlock CardTitle(Canvas host, string text, double x, double y, double width, double height)
        {
            TextBlock title = Label(host, text, Fonts.Label, x, y, width, height);
            title.FontWeight = FontWeights.Bold;
            title.Foreground = Theme.Brush(Theme.Ink);
            return title;
        }

        // 构建独立卡片容器 Canvas，并在构建期登记，确保背景与所有控件作为一个整体错峰滑入
        private Canvas Card(Canvas host, double x, double y, double width, double height)
        {
            Canvas card = new Canvas { Width = width, Height = height };
            Border bg = new Border
            {
                Width = width, Height = height,
                Background = CardBackgroundBrush,
                BorderBrush = Theme.Brush(Theme.CardStroke), // 1px 高光描边（设计稿投影的降级表达）
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(20)
            };
            Canvas.SetLeft(bg, 0); Canvas.SetTop(bg, 0);
            card.Children.Add(bg);
            PlaceIn(host, card, x, y, width, height);

            System.Collections.Generic.List<CardHostItem> list;
            if (!pageCards.TryGetValue(host, out list))
            {
                list = new System.Collections.Generic.List<CardHostItem>();
                pageCards[host] = list;
            }
            list.Add(new CardHostItem { Element = card, X = x, Y = y });
            return card;
        }

        private static Path LineIcon(string data, double size, string color)
        {
            return new Path
            {
                Data = Geometry.Parse(data), Stroke = ColorBrush(color), StrokeThickness = 1.7,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Width = size, Height = size, Stretch = Stretch.Uniform
            };
        }

        private void SetText(TextBlock block, string text, bool fade = false)
        {
            if (block == null) return;
            if (fade) FadeText(block, text);
            else block.Text = text;
        }

        // 读数颜色要是"可动画"的：Theme.Brush 返回的是冻结画刷，直接赋值既不能过渡，
        // 也会在每秒一次的心跳刷新里反复分配新对象。这里保证每块文本只持有一份画刷。
        private static SolidColorBrush MutableForeground(TextBlock block, System.Drawing.Color initial)
        {
            if (block == null) return null;
            SolidColorBrush brush = block.Foreground as SolidColorBrush;
            if (brush == null || brush.IsFrozen)
            {
                brush = Theme.NewBrush(initial);
                block.Foreground = brush;
            }
            return brush;
        }

        private Canvas NewPage()
        {
            Canvas page = new Canvas { Width = 1020, Height = 606, Visibility = Visibility.Collapsed };
            surface.Children.Add(page);
            return page;
        }

        // 统一 Pill 选项与按钮交互反馈：hover 微调底色，pressed 缩放 0.95/0.92
        private void AttachPillFeedback(Button btn, double pressedScale = 0.95, System.Drawing.Color? hoverColor = null, System.Drawing.Color? normalColor = null)
        {
            ScaleTransform st = new ScaleTransform(1.0, 1.0);
            btn.RenderTransform = st;
            btn.RenderTransformOrigin = new Point(0.5, 0.5);

            // 静止色（“平时该是什么颜色”）统一放在 Tag 里，由 SyncChips / SyncTab 在设置选中态时写入。
            // 以前鼠标离开时硬写回 normalColor（未选中色），于是滑动切页时鼠标扫过选中的 pill，
            // 它就被刷成浅色、且选中态要等下一次配置刷新才回来——表现为“几次滑动之后蓝色没了”。
            if (normalColor.HasValue) btn.Tag = Theme.Media(normalColor.Value);

            btn.PreviewMouseDown += delegate
            {
                Transition.AnimateTo(st, ScaleTransform.ScaleXProperty, pressedScale, 120, ReferenceEase.Feedback);
                Transition.AnimateTo(st, ScaleTransform.ScaleYProperty, pressedScale, 120, ReferenceEase.Feedback);
            };
            btn.PreviewMouseUp += delegate
            {
                Transition.AnimateTo(st, ScaleTransform.ScaleXProperty, 1.0, 140, ReferenceEase.Feedback);
                Transition.AnimateTo(st, ScaleTransform.ScaleYProperty, 1.0, 140, ReferenceEase.Feedback);
            };
            btn.MouseLeave += delegate
            {
                Transition.AnimateTo(st, ScaleTransform.ScaleXProperty, 1.0, 140, ReferenceEase.Feedback);
                Transition.AnimateTo(st, ScaleTransform.ScaleYProperty, 1.0, 140, ReferenceEase.Feedback);
                SolidColorBrush scb = btn.Background as SolidColorBrush;
                if (scb == null || scb.IsFrozen) return;
                // 回到“静止色”，而不是无条件的未选中色：选中态在鼠标划过之后必须原样保留。
                Color back = btn.Tag is Color ? (Color)btn.Tag : scb.Color;
                Transition.AnimateColorTo(scb, back, 120);
            };
            if (hoverColor.HasValue)
            {
                btn.MouseEnter += delegate
                {
                    SolidColorBrush scb = btn.Background as SolidColorBrush;
                    if (scb == null || scb.IsFrozen) return;
                    // 选中态本身就是强调色实底，悬停再压一层灰会被读成“被取消选中”；
                    // 所以只对未选中的 pill 做 hover 变色，选中的保持蓝色不动（按下仍有缩放反馈）。
                    if (btn.Tag is Color && (Color)btn.Tag == Theme.Media(Theme.Accent)) return;
                    Transition.AnimateColorTo(scb, Theme.Media(hoverColor.Value), 120);
                };
            }
        }

        // ── 开关：每只开关一份画刷 + 一个可续接的位移动画 ──
        // 模板里不再放 IsChecked 触发器。原因：Storyboard 的 To 动画在连续点击时会从
        // 底色重新起跑（观感是"跳一下"），而 Transition 读当前值续接，且能一眼看清
        // "哪个开关在动"——画刷是每只开关自己的，不会整页一起变色。
        private void WireSwitch(CheckBox box)
        {
            if (box == null) return;
            box.Background = Theme.NewBrush(Theme.SwitchOff);
            box.Checked += delegate { AnimateSwitch(box, true); };
            box.Unchecked += delegate { AnimateSwitch(box, false); };
            box.Loaded += delegate { SetSwitchImmediate(box, box.IsChecked == true); };
            if (box.IsLoaded) SetSwitchImmediate(box, box.IsChecked == true);
        }

        private static TranslateTransform SwitchKnob(CheckBox box)
        {
            if (box == null) return null;
            box.ApplyTemplate();
            return box.Template == null ? null : box.Template.FindName("KnobTrans", box) as TranslateTransform;
        }

        private void AnimateSwitch(CheckBox box, bool on)
        {
            if (box == null) return;
            TranslateTransform knob = SwitchKnob(box);
            // 尚未进入可视树（构建期 Invalidate 里的首次赋值）时直接落位，
            // 否则会在窗口还没出现时先播一遍动画。
            if (!box.IsLoaded || knob == null)
            {
                SetSwitchImmediate(box, on);
                return;
            }
            SolidColorBrush track = box.Background as SolidColorBrush;
            if (track != null) Transition.AnimateColorTo(track, Theme.Media(on ? Theme.Accent : Theme.SwitchOff), 200);
            // 0 → 22px, 220ms, reference cubic-bezier; interruptions retain the current value.
            Transition.AnimateTo(knob, TranslateTransform.XProperty, on ? 22.0 : 0.0, 220, ReferenceEase.Switch);
        }

        private void SetSwitchImmediate(CheckBox box, bool on)
        {
            if (box == null) return;
            TranslateTransform knob = SwitchKnob(box);
            SolidColorBrush track = box.Background as SolidColorBrush;
            if (track != null)
            {
                track.BeginAnimation(SolidColorBrush.ColorProperty, null);
                track.Color = Theme.Media(on ? Theme.Accent : Theme.SwitchOff);
            }
            if (knob != null)
            {
                knob.BeginAnimation(TranslateTransform.XProperty, null);
                knob.X = on ? 22.0 : 0.0;
            }
        }

        // ─────────────────────────────── 构建 ───────────────────────────────

        private void BuildSurface()
        {
            surface.Width = 1020; surface.Height = 606;
            surface.Background = WindowOverlayBrush;
            surface.LayoutUpdated += delegate
            {
                if (panelPages != null && (int)activePage >= 0)
                    AlignDockToPage(panelPages[(int)activePage]);
            };
            Content = new Border
            {
                CornerRadius = new CornerRadius(24), BorderThickness = new Thickness(1),
                BorderBrush = Theme.Brush(Theme.WindowStroke), Child = surface
            };

            // 标准模型 1：统一开关模型（尺寸 48×26 r13，knob 18，位移 TranslateTransform 0→22，无布局重测）
            // 底色与旋钮位移一律由 WireSwitch 用 Transition 驱动：模板触发器那套 Storyboard 在
            // 连续点击时会从头重放，而这里的每一下都从当前值续接（原则 1）。
            switchStyle = TryParseStyle(@"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='CheckBox'>
 <Setter Property='Cursor' Value='Hand'/>
 <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='CheckBox'>
  <Grid Background='Transparent'>
   <Border x:Name='Track' Width='48' Height='26' CornerRadius='13' Background='{TemplateBinding Background}' BorderThickness='0'
           HorizontalAlignment='Left' VerticalAlignment='Center'>
    <Ellipse x:Name='Knob' Width='18' Height='18' Fill='White' HorizontalAlignment='Left' Margin='4,0,0,0'>
     <Ellipse.RenderTransform>
      <TranslateTransform x:Name='KnobTrans' X='0' Y='0'/>
     </Ellipse.RenderTransform>
     <Ellipse.Effect><DropShadowEffect Color='#000000' ShadowDepth='2' BlurRadius='6' Opacity='0.18'/></Ellipse.Effect>
    </Ellipse>
   </Border>
   <ContentPresenter Margin='58,0,0,0' VerticalAlignment='Center' HorizontalAlignment='Left'/>
  </Grid>
  <ControlTemplate.Triggers>
   <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.4'/></Trigger>
  </ControlTemplate.Triggers>
 </ControlTemplate></Setter.Value></Setter>
</Style>", "switch");

            // 标准模型 2：统一 Pill 点击选项框。圆角按设计稿分档：
            // 小 pill 10 / 中 pill 12 / ± 微调 8 / 读取配置 17 / 页脚 14。
            // 模板只有一份，圆角通过字符串参数化，避免五份 XAML 互相漂移。
            chipStyle = TryParseStyle(PillStyleXaml(10), "chip-r10");
            chipStyle8 = TryParseStyle(PillStyleXaml(8), "chip-r8");
            chipStyle12 = TryParseStyle(PillStyleXaml(12), "chip-r12");
            readStyle = TryParseStyle(PillStyleXaml(17), "chip-r17");

            // 标准模型 3：统一动作按钮样式（圆角 12px）
            buttonStyle = TryParseStyle(@"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'>
 <Setter Property='Cursor' Value='Hand'/>
 <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'>
  <Border Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='12'>
   <ContentPresenter Margin='{TemplateBinding Padding}' HorizontalAlignment='Center' VerticalAlignment='Center'/>
  </Border>
 </ControlTemplate></Setter.Value></Setter>
 <Style.Triggers>
  <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.45'/></Trigger>
 </Style.Triggers>
</Style>", "button");

            // 14px blue round track. Its 7px caps extend beyond the logical travel endpoints.
            sliderStyle = TryParseStyle(@"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Slider'>
 <Setter Property='Height' Value='28'/>
 <Setter Property='UseLayoutRounding' Value='False'/>
 <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Slider'>
  <Grid Background='Transparent'>
   <Border Height='14' CornerRadius='7' Background='#E2E8F0' VerticalAlignment='Center'/>
   <Track x:Name='PART_Track'>
    <Track.DecreaseRepeatButton>
     <RepeatButton Command='Slider.DecreaseLarge'>
      <RepeatButton.Template><ControlTemplate TargetType='RepeatButton'>
       <Border Background='{SLIDER_ACCENT}' Height='14' CornerRadius='7' Margin='0,0,-14,0' VerticalAlignment='Center'/>
      </ControlTemplate></RepeatButton.Template>
     </RepeatButton>
    </Track.DecreaseRepeatButton>
    <Track.Thumb>
     <Thumb Width='14' Height='14' Cursor='Hand'>
      <Thumb.Template><ControlTemplate TargetType='Thumb'>
       <Ellipse Width='14' Height='14' Fill='{SLIDER_ACCENT}'/>
      </ControlTemplate></Thumb.Template>
     </Thumb>
    </Track.Thumb>
    <Track.IncreaseRepeatButton>
     <RepeatButton Command='Slider.IncreaseLarge'>
      <RepeatButton.Template><ControlTemplate TargetType='RepeatButton'><Border Background='Transparent'/></ControlTemplate></RepeatButton.Template>
     </RepeatButton>
    </Track.IncreaseRepeatButton>
   </Track>
  </Grid>
  <ControlTemplate.Triggers>
   <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='1'/></Trigger>
  </ControlTemplate.Triggers>
 </ControlTemplate></Setter.Value></Setter>
</Style>".Replace("{SLIDER_ACCENT}", Theme.HexSliderAccent), "slider");

            BuildChrome();

            // 页面优先挂入 surface
            pageOverview = NewPage(); BuildOverviewPage();
            pageDpi = NewPage(); BuildDpiPage();
            pageParams = NewPage(); BuildParamsPage();
            pageSettings = NewPage(); BuildSettingsPage();

            // 硬口径：把 navDockContainer 放到 surface.Children 最后，确保 Dock 处于最顶层
            BuildNav();

            InitSwipeEngine();
            NavigateToPage(PanelPage.Overview, false);
        }

        // ─────────────────────────────── 顶栏与页脚重写 ───────────────────────────────

        private void BuildChrome()
        {
            // 顶栏拖动区 (0, 0, 1018, 66)
            Border drag = new Border { Background = Brushes.Transparent };
            PlaceIn(surface, drag, 0, 0, 1018, 66);
            drag.MouseLeftButtonDown += delegate { DragMove(); };

            // 标题「雷柏 VT9 控制台」: 左 32, 行中心 34, 20px SemiBold #111827
            // 标题下方的连接状态行（“接口已连接（2.4G 接收器）”）已移除：连接方式在概况页
            // 与设备卡里都有，顶栏只留标题，视觉更干净。deviceLine 本身仍参与状态判定与摘要。
            TextBlock title = Label(surface, "雷柏 VT9 控制台", Fonts.Title, 32, 20, 320, 28);
            title.FontWeight = FontWeights.Bold;
            title.Foreground = Theme.Brush(Theme.Ink);
            title.IsHitTestVisible = false;

            // 「读取于 09:36:41」: 右对齐 838, 行中心 38, 13.5px Regular #64748B
            globalReadTimeText = Label(surface, "尚未读取", Fonts.Sub, 495, 34.5, 240, 19);
            globalReadTimeText.TextAlignment = TextAlignment.Right;
            globalReadTimeText.Foreground = Theme.Brush(Theme.Muted);
            globalReadTimeText.IsHitTestVisible = false;

            // 读取配置按钮: (750, 26, 94, 34) r17, 底 #EDF2F7, 字 13.5px SemiBold #475569
            globalReadBtn = new Button
            {
                Style = readStyle, Width = 94, Height = 34,
                Background = Theme.NewBrush(Theme.ChipBg),
                BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = "读取设备全部配置（DPI/回报率/LOD/消抖/休眠）"
            };
            globalReadBtnText = new TextBlock
            {
                Text = "读取配置", FontSize = Fonts.Chip, FontWeight = FontWeights.SemiBold,
                Foreground = Theme.NewBrush(Theme.FooterInk)
            };
            globalReadBtn.Content = globalReadBtnText;
            AttachPillFeedback(globalReadBtn, 0.95, Theme.ChipHover, Theme.ChipBg);
            globalReadBtn.Click += delegate { RunReadConfig(); };
            PlaceIn(surface, globalReadBtn, 750, 26, 94, 34);

            // 关闭按钮: (865, 25, 36, 36) r10, 底透明, X 图标 16px, stroke 2, #475569
            Button close = new Button
            {
                Style = chipStyle, Width = 36, Height = 36,
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand, ToolTip = "收起至托盘"
            };
            System.Windows.Automation.AutomationProperties.SetName(close, "收起至托盘");
            Path closeIcon = LineIcon("M 2,2 L 14,14 M 14,2 L 2,14", 16, Theme.HexInkSoft);
            closeIcon.StrokeThickness = 2;
            close.Content = closeIcon;
            // 透明也算一种颜色：给按钮和图标各一份可变画刷，hover 时 120ms 过渡到
            // #D64A3E + 白图标，而不是一帧换掉。
            SolidColorBrush closeBg = Theme.NewBrush(System.Drawing.Color.FromArgb(0, 0, 0, 0));
            SolidColorBrush closeInk = Theme.NewBrush(Theme.InkSoft);
            close.Background = closeBg;
            closeIcon.Stroke = closeInk;
            close.MouseEnter += delegate
            {
                Transition.AnimateColorTo(closeBg, Theme.Media(Theme.CloseHover), 120);
                Transition.AnimateColorTo(closeInk, Colors.White, 120);
            };
            close.MouseLeave += delegate
            {
                Transition.AnimateColorTo(closeBg, System.Windows.Media.Color.FromArgb(0, 0, 0, 0), 120);
                Transition.AnimateColorTo(closeInk, Theme.Media(Theme.InkSoft), 120);
            };
            // 走 Close() 而不是直接 HideToTray()：托盘可用时 OnClosing 会取消关闭并收起到托盘，
            // 托盘不可用时（例如进程被限制、Shell_NotifyIcon 被拒）就真的退出。
            // 直接调 HideToTray() 会让“托盘注册失败”这种情形下窗口既关不掉、也收不进托盘，成为死路。
            close.Click += delegate { Close(); };
            PlaceIn(surface, close, 865, 25, 36, 36);


        }

        // ─────────────────────────────── 左侧五层 Dock ───────────────────────────────

        private static void ApplyNavTracking(TextBlock label)
        {
            TextEffectCollection effects = new TextEffectCollection();
            for (int i = 0; i < label.Text.Length; i++)
                effects.Add(new TextEffect(new TranslateTransform((i - (label.Text.Length - 1) / 2.0) * 0.5, 0),
                    null, null, i, 1));
            label.TextEffects = effects;
        }

        private void BuildNav()
        {
            // 容器：navDockContainer (34, 76, 100, 506)
            navDockContainer = new Canvas
            {
                Width = 100, Height = 506,
                UseLayoutRounding = false, // Keep the lens and underlying glyphs in one subpixel coordinate space.
                Background = Brushes.Transparent, // 必须可参与命中
                Cursor = Cursors.Hand, Focusable = true
            };
            System.Windows.Automation.AutomationProperties.SetName(navDockContainer, "导航栏");
            PlaceIn(surface, navDockContainer, 34, 76, 100, 506);

            // ── 第 1 层：玻璃底座（Border 100×506，r20，单层 DropShadowEffect）──
            LinearGradientBrush shelfFill = new LinearGradientBrush();
            shelfFill.StartPoint = new Point(0, 0); shelfFill.EndPoint = new Point(1, 0);
            shelfFill.GradientStops.Add(new GradientStop(Theme.Media(Theme.GlassFillTop), 0.0));
            shelfFill.GradientStops.Add(new GradientStop(Theme.Media(Theme.GlassFillMid), 0.5));
            shelfFill.GradientStops.Add(new GradientStop(Theme.Media(Theme.GlassFillBottom), 1.0));

            LinearGradientBrush shelfStroke = new LinearGradientBrush();
            shelfStroke.StartPoint = new Point(0, 0); shelfStroke.EndPoint = new Point(0, 1);
            shelfStroke.GradientStops.Add(new GradientStop(Theme.Media(Theme.GlassStrokeTop), 0.0));
            shelfStroke.GradientStops.Add(new GradientStop(Theme.Media(Theme.GlassStrokeBottom), 1.0));

            navShelf = new Border
            {
                Width = 100, Height = 506, CornerRadius = new CornerRadius(20),
                Background = shelfFill, BorderBrush = shelfStroke, BorderThickness = new Thickness(1.2),
                IsHitTestVisible = false,
                Effect = new DropShadowEffect
                {
                    Direction = 270, ShadowDepth = 8, BlurRadius = 14,
                    Color = Theme.Media(Theme.DockShadow), Opacity = 0.16
                }
            };
            Canvas.SetLeft(navShelf, 0); Canvas.SetTop(navShelf, 0);
            navDockContainer.Children.Add(navShelf);

            // ── 第 2 层：常态标签（四个 TextBlock，18px Regular #4B525D）──
            string[] captions = { "概况", "DPI", "参数", "设置" };
            double[] itemCentersY = { 47, 151, 255, 359 };
            navLabels = new TextBlock[captions.Length];
            navBaseLabels = new Canvas { Width = 100, Height = 506, IsHitTestVisible = false };
            navDockContainer.Children.Add(navBaseLabels);
            for (int i = 0; i < captions.Length; i++)
            {
                TextBlock label = new TextBlock
                {
                    Width = 100, Height = 26,
                    Text = captions[i], FontSize = Fonts.Nav,
                    Foreground = Theme.Brush(Theme.InkSoft),
                    FontWeight = FontWeights.Medium,
                    TextAlignment = TextAlignment.Center,
                    IsHitTestVisible = false,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = new ScaleTransform(1.0, 1.0)
                };
                ApplyNavTracking(label);
                System.Windows.Automation.AutomationProperties.SetName(label, captions[i]);
                Canvas.SetLeft(label, 0);
                Canvas.SetTop(label, itemCentersY[i] - 13);
                navBaseLabels.Children.Add(label);
                navLabels[i] = label;
            }

            // ── 第 3 层：胶囊滑块 navPill（Border 80×76 @ 容器 (10, 9)，r20，IsHitTestVisible=false）──
            navPillTranslate = new TranslateTransform(0, 0);
            navPillScale = new ScaleTransform(1.0, 1.0);
            System.Windows.Data.BindingOperations.SetBinding(navPillScale, ScaleTransform.ScaleXProperty,
                new System.Windows.Data.Binding("ScaleY") { Source = navPillScale, Converter = new ReciprocalConverter() });
            TransformGroup pillGroup = new TransformGroup();
            pillGroup.Children.Add(navPillScale);
            pillGroup.Children.Add(navPillTranslate);

            // Cut the original glyphs out only under the capsule. Otherwise the translucent
            // glass lets their smaller strokes show through the larger lens text as ghosting.
            TransformGroup lensToDock = new TransformGroup();
            lensToDock.Children.Add(new TranslateTransform(-40, -38));
            lensToDock.Children.Add(navPillScale);
            lensToDock.Children.Add(new TranslateTransform(50, 47));
            lensToDock.Children.Add(navPillTranslate);
            RectangleGeometry coveredGlyphs = new RectangleGeometry(new Rect(0, 0, 80, 76), 20, 20);
            coveredGlyphs.Transform = lensToDock;
            navBaseLabels.Clip = new CombinedGeometry(GeometryCombineMode.Exclude,
                new RectangleGeometry(new Rect(0, 0, 100, 506)), coveredGlyphs);

            LinearGradientBrush pillFill = new LinearGradientBrush();
            pillFill.StartPoint = new Point(0, 0); pillFill.EndPoint = new Point(1, 1);
            pillFill.GradientStops.Add(new GradientStop(Theme.Media(Theme.CapsuleFillTop), 0.0));
            pillFill.GradientStops.Add(new GradientStop(Theme.Media(Theme.CapsuleFillBottom), 1.0));

            LinearGradientBrush pillStroke = new LinearGradientBrush();
            pillStroke.StartPoint = new Point(0, 0); pillStroke.EndPoint = new Point(1, 1);
            pillStroke.GradientStops.Add(new GradientStop(Colors.White, 0.0));
            pillStroke.GradientStops.Add(new GradientStop(Theme.Media(Theme.CapsuleEdge), 1.0));

            Canvas pillCanvas = new Canvas
            {
                Width = 80, Height = 76,
                IsHitTestVisible = false,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = pillGroup
            };

            Border pillBorder = new Border
            {
                Width = 80, Height = 76, CornerRadius = new CornerRadius(20),
                Background = pillFill, BorderBrush = pillStroke, BorderThickness = new Thickness(1.2),
                IsHitTestVisible = false,
                Effect = new DropShadowEffect
                {
                    Direction = 270, ShadowDepth = 5, BlurRadius = 7,
                    Color = Theme.Media(Theme.CapsuleShadow), Opacity = 0.22
                }
            };
            Canvas.SetLeft(pillBorder, 0); Canvas.SetTop(pillBorder, 0);
            pillCanvas.Children.Add(pillBorder);

            Canvas.SetLeft(pillCanvas, 10); Canvas.SetTop(pillCanvas, 9);
            navDockContainer.Children.Add(pillCanvas);
            navPill = pillBorder;

            // ── 第 4 层：透镜视窗（Lens Clip 80×76 @ (10, 9)，与胶囊共享同一个 Y 动画值）──
            lensWindow = new Canvas
            {
                Width = 80, Height = 76,
                Clip = new RectangleGeometry(new Rect(0, 0, 80, 76), 20, 20),
                IsHitTestVisible = false,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = pillGroup // Shared displacement AND deformation with the capsule.
            };

            Canvas lensContent = new Canvas { Width = 80, Height = 506, IsHitTestVisible = false };
            lensContentTranslate = new TranslateTransform(0, 0);
            System.Windows.Data.BindingOperations.SetBinding(lensContentTranslate, TranslateTransform.YProperty,
                new System.Windows.Data.Binding("Y") { Source = navPillTranslate, Converter = new NegateConverter() });
            ScaleTransform inverseShape = new ScaleTransform(1, 1, 40, 38);
            System.Windows.Data.BindingOperations.SetBinding(inverseShape, ScaleTransform.ScaleXProperty,
                new System.Windows.Data.Binding("ScaleX") { Source = navPillScale, Converter = new ReciprocalConverter() });
            System.Windows.Data.BindingOperations.SetBinding(inverseShape, ScaleTransform.ScaleYProperty,
                new System.Windows.Data.Binding("ScaleY") { Source = navPillScale, Converter = new ReciprocalConverter() });
            TransformGroup lensCompensation = new TransformGroup();
            lensCompensation.Children.Add(lensContentTranslate);
            lensCompensation.Children.Add(inverseShape);
            lensContent.RenderTransform = lensCompensation;

            for (int i = 0; i < captions.Length; i++)
            {
                TextBlock lensLabel = new TextBlock
                {
                    Width = 80, Height = 26,
                    Text = captions[i], FontSize = Fonts.Nav,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = new ScaleTransform(Fonts.NavLensScale, Fonts.NavLensScale),
                    FontWeight = FontWeights.Bold,
                    Foreground = Theme.Brush(Theme.AccentDeep), // #22379B
                    TextAlignment = TextAlignment.Center,
                    IsHitTestVisible = false
                };
                ApplyNavTracking(lensLabel);
                Canvas.SetLeft(lensLabel, 0);
                Canvas.SetTop(lensLabel, (38 + i * 104) - 13);
                lensContent.Children.Add(lensLabel);
            }
            lensWindow.Children.Add(lensContent);
            Canvas.SetLeft(lensWindow, 10); Canvas.SetTop(lensWindow, 9);
            navDockContainer.Children.Add(lensWindow);

            // ── 第 5 层：手势交互与键盘导航 ──
            navDockContainer.MouseLeftButtonDown += OnDockMouseDown;
            navDockContainer.MouseMove += OnDockMouseMove;
            navDockContainer.PreviewMouseLeftButtonUp += OnDockMouseUp;
            navDockContainer.LostMouseCapture += OnDockLostCapture;
            navDockContainer.KeyDown += OnDockKeyDown;
            Deactivated += delegate { CancelDockGesture(); };
        }

        // Use the same card edges as the current page, independent of entrance transforms.
        private void AlignDockToPage(Canvas page)
        {
            if (navDockContainer == null || navShelf == null || page == null) return;
            System.Collections.Generic.List<CardHostItem> cards;
            if (!pageCards.TryGetValue(page, out cards) || cards.Count == 0) return;
            double top = double.MaxValue, bottom = double.MinValue;
            foreach (CardHostItem card in cards)
            {
                // At fractional DPI, rounded Y + rounded height may differ from
                // rounding the combined bounds. Follow the arranged card edges.
                bool arranged = card.Element.IsArrangeValid && card.Element.ActualHeight > 0;
                double y = arranged ? VisualTreeHelper.GetOffset(card.Element).Y : card.Y;
                double height = arranged ? card.Element.ActualHeight : card.Element.Height;
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y + height);
            }
            Canvas.SetTop(navDockContainer, top);
            navDockContainer.Height = bottom - top;
            navShelf.Height = bottom - top;
        }

        private void OnDockKeyDown(object sender, KeyEventArgs e)
        {
            int cur = Math.Max(0, (int)activePage);
            if (e.Key == Key.Up)
            {
                if (cur > 0) SelectPage((PanelPage)(cur - 1));
                e.Handled = true;
            }
            else if (e.Key == Key.Down)
            {
                if (cur < 3) SelectPage((PanelPage)(cur + 1));
                e.Handled = true;
            }
            else if (e.Key == Key.Home)
            {
                SelectPage(PanelPage.Overview);
                e.Handled = true;
            }
            else if (e.Key == Key.End)
            {
                SelectPage(PanelPage.Settings);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter || e.Key == Key.Space)
            {
                SelectPage(activePage);
                e.Handled = true;
            }
        }

        private void SelectPage(PanelPage page)
        {
            NavigateToPage(page, IsVisible);
        }

        internal void SelectPageForPreview(string name)
        {
            if (name == "dpi") SelectPage(PanelPage.Dpi);
            else if (name == "params") SelectPage(PanelPage.Params);
            else if (name == "settings") SelectPage(PanelPage.Settings);
            else if (name == "dump")
            {
                SelectPage(PanelPage.Overview);
                System.Windows.Threading.DispatcherTimer dump = new System.Windows.Threading.DispatcherTimer
                { Interval = TimeSpan.FromMilliseconds(400) };
                dump.Tick += delegate
                {
                    if (configBusy) return;
                    dump.Stop();
                    RunConfigDump();
                };
                dump.Start();
            }
            else SelectPage(PanelPage.Overview);
        }

        // ─────────────────────────────── 概览页 ───────────────────────────────

        private void BuildOverviewPage()
        {
            Canvas host = pageOverview;

            // 1. 设备卡 (150, 76, 360, 344)
            Canvas cardDev = Card(host, 150, 76, 360, 344);
            CardTitle(cardDev, "设备", 24, 18, 80, 26);
            ovMode = Meta(cardDev, "", 180, 22, 156);
            ovMode.TextAlignment = TextAlignment.Right;
            ovMode.Foreground = Theme.Brush(Theme.Faint);

            Border product = new Border
            {
                CornerRadius = new CornerRadius(14),
                Background = Theme.Brush(Theme.ProductBg)
            };
            Canvas mouse = new Canvas { Width = 60, Height = 100 };
            mouse.Children.Add(new Border
            {
                Width = 60, Height = 100, CornerRadius = new CornerRadius(28),
                Background = Theme.Brush(Theme.Accent)
            });
            mouse.Children.Add(new Path
            {
                Data = Geometry.Parse("M30,0 L30,45"),
                Stroke = Theme.Brush(Theme.ProductLine), StrokeThickness = 1.8
            });
            Border wheel = new Border
            {
                Width = 5, Height = 18, CornerRadius = new CornerRadius(2.5),
                Background = Brushes.White
            };
            Canvas.SetLeft(wheel, 27.5); Canvas.SetTop(wheel, 18);
            mouse.Children.Add(wheel);
            Canvas mouseCenter = new Canvas { Width = 312, Height = 175 };
            Canvas.SetLeft(mouse, 126); Canvas.SetTop(mouse, 20);
            mouseCenter.Children.Add(mouse);
            product.Child = mouseCenter;
            PlaceIn(cardDev, product, 24, 52, 312, 175);

            SubLabel(cardDev, "电量", 24, 237.5, 80);
            batteryText = Label(cardDev, "未获取", Fonts.ValueHero, 104, 237.5, 232, 26);
            batteryText.FontWeight = FontWeights.Bold;
            batteryText.Foreground = Theme.Brush(Theme.Ink);
            batteryText.TextAlignment = TextAlignment.Right;

            SubLabel(cardDev, "充电状态", 24, 269.5, 80);
            chargeText = Label(cardDev, "—", Fonts.Value, 104, 270.5, 232, 24);
            chargeText.FontWeight = FontWeights.Bold;
            chargeText.Foreground = Theme.Brush(Theme.Ink);
            chargeText.TextAlignment = TextAlignment.Right;

            SubLabel(cardDev, "连接模式", 24, 301.5, 80);
            TextBlock mode = Label(cardDev, "", Fonts.Value, 104, 302.5, 232, 24);
            mode.FontWeight = FontWeights.Bold;
            mode.Foreground = Theme.Brush(Theme.Ink);
            mode.TextAlignment = TextAlignment.Right;
            PageModeText = mode;

            // 2. 参数卡 (526, 76, 470, 344)
            Canvas cardParam = Card(host, 526, 76, 470, 344);
            CardTitle(cardParam, "参数", 24, 18, 100, 26);

            string[] names = { "回报率", "静默高度", "进入休眠", "按键消抖", "运动同步", "当前档位" };
            double[] paramBaselines = { 80, 126, 172, 218, 264, 310 };
            TextBlock[] values = new TextBlock[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                double topY = paramBaselines[i] - 15;
                SubLabel(cardParam, names[i], 24, topY, 120);
                TextBlock value = Label(cardParam, "—", Fonts.Value, 150, topY, 296, 24);
                value.FontWeight = FontWeights.Bold;
                value.Foreground = Theme.Brush(Theme.Ink);
                value.TextAlignment = TextAlignment.Right;
                values[i] = value;
            }
            ovPolling = values[0]; ovLod = values[1]; ovSleep = values[2];
            ovDebounce = values[3]; ovMotion = values[4]; ovStage = values[5];

            // 3. 启动与提醒卡 (150, 436, 846, 146)
            Canvas cardStartup = Card(host, 150, 436, 846, 146);
            CardTitle(cardStartup, "启动与提醒", 24, 18, 140, 26);

            // 行 1 (中心 493, relative 65): Switch (24, 52, 48, 26)
            autoCheck = new CheckBox { Content = "自动启动", Style = switchStyle, FontSize = Fonts.SwitchLabel, FontWeight = FontWeights.SemiBold, Foreground = Theme.Brush(Theme.Ink) };
            WireSwitch(autoCheck);
            PlaceIn(cardStartup, autoCheck, 24, 52, 150, 26);
            autoCheck.Click += delegate
            {
                autoStartOn = autoCheck.IsChecked == true;
                ApplyAutoStart(autoStartOn);
                Invalidate();
            };
            TextBlock autoDesc = Label(cardStartup, "开机自动启动服务并收起至托盘", Fonts.Sub, 180, 54, 320, 22);
            autoDesc.Foreground = Theme.Brush(Theme.Muted);
            autoStatusText = Label(cardStartup, "未启用", Fonts.Body, 580, 54, 240, 22);
            autoStatusText.FontWeight = FontWeights.SemiBold;
            autoStatusText.TextAlignment = TextAlignment.Right;

            // 行 2 (中心 539, relative 111): Switch (24, 98, 48, 26)
            alertsCheck = new CheckBox { Content = "低电量提醒", Style = switchStyle, FontSize = Fonts.SwitchLabel, FontWeight = FontWeights.SemiBold, Foreground = Theme.Brush(Theme.Ink) };
            WireSwitch(alertsCheck);
            PlaceIn(cardStartup, alertsCheck, 24, 98, 150, 26);
            alertsCheck.Click += delegate
            {
                preferences.LowBatteryNotification = alertsCheck.IsChecked == true;
                preferences.Save();
                Invalidate();
            };
            TextBlock alertsDesc = Label(cardStartup, "鼠标电池电量低于 20% 时弹出通知气泡预警", Fonts.Sub, 180, 100, 370, 22);
            alertsDesc.Foreground = Theme.Brush(Theme.Muted);
            alertsStatusText = Label(cardStartup, "未开启", Fonts.Body, 580, 100, 240, 22);
            alertsStatusText.FontWeight = FontWeights.SemiBold;
            alertsStatusText.TextAlignment = TextAlignment.Right;


        }

        private TextBlock PageModeText;

        // ─────────────────────────────── DPI 页 ───────────────────────────────

        private void BuildDpiPage()
        {
            Canvas host = pageDpi;

            // 1. 当前档位卡 (150, 76, 846, 106)
            Canvas cardHero = Card(host, 150, 76, 846, 106);
            TextBlock currentLabel = CardTitle(cardHero, "当前档位", 24, 14, 100, 20);
            currentLabel.FontSize = 14;
            currentLabel.FontWeight = FontWeights.SemiBold;
            dpiValueText = Label(cardHero, "—", Fonts.Display, 24, 38, 200, 52);
            dpiValueText.FontFamily = Fonts.Wpf;
            dpiValueText.FontWeight = FontWeights.ExtraBold;
            dpiValueText.Foreground = Theme.Brush(Theme.Ink);

            dpiRangeText = Meta(cardHero, "未读取", 522, 14, 300);
            dpiRangeText.TextAlignment = TextAlignment.Right;

            TextBlock switchLabel = Meta(cardHero, "快捷切换", 352, 58, 88);
            switchLabel.TextAlignment = TextAlignment.Right;

            // Reference variable widths, fitted within the hero card boundary.
            dpiStageChips = new Button[7];
            double[] quickWidths = { 46, 50, 52, 52, 52, 54, 56 };
            double quickX = 448;
            for (int i = 0; i < 7; i++)
            {
                int index = i;
                Button chip = new Button
                {
                    Content = "—", Style = chipStyle, Width = quickWidths[i], Height = 32,
                    FontSize = Fonts.Chip, FontWeight = FontWeights.SemiBold,
                    Background = Theme.NewBrush(Theme.ChipBg),
                    Foreground = Theme.Brush(Theme.FooterInk),
                    BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand
                };
                AttachPillFeedback(chip, 0.95, Theme.ChipHover, Theme.ChipBg);
                chip.Click += delegate { QuickSwitchStage(index); };
                PlaceIn(cardHero, chip, quickX, 52, quickWidths[i], 32);
                quickX += quickWidths[i] + 6;
                dpiStageChips[i] = chip;
            }

            // 2. 7 档调节卡 (150, 194, 846, 388)
            Canvas cardSliders = Card(host, 150, 194, 846, 388);
            stageSwitches = new CheckBox[7];
            stageSliders = new Slider[7];
            stageValueTexts = new TextBlock[7];
            stageIndexTexts = new TextBlock[7];
            stageMinus = new Button[7];
            stagePlus = new Button[7];

            for (int i = 0; i < 7; i++)
            {
                int index = i;
                double center = 31 + i * 44;

                CheckBox toggle = new CheckBox { Style = switchStyle };
                PlaceIn(cardSliders, toggle, 24, center - 13, 48, 26);
                toggle.Click += delegate { OnStageEnabledChanged(index); };
                WireSwitch(toggle);
                stageSwitches[i] = toggle;

                TextBlock caption = Label(cardSliders, (i + 1).ToString(), Fonts.Value, 79, center - 13, 26, 26);
                caption.TextAlignment = TextAlignment.Center;
                stageIndexTexts[i] = caption;

                Slider slider = new Slider
                {
                    Minimum = 50, Maximum = 20000, SmallChange = 50, LargeChange = 500,
                    TickFrequency = 50, IsSnapToTickEnabled = true, Style = sliderStyle,
                    IsMoveToPointEnabled = true, Height = 28
                };
                PlaceIn(cardSliders, slider, 111, center - 14, 494, 28);
                slider.ValueChanged += delegate { OnStageSliderChanged(index); };
                stageSliders[i] = slider;

                TextBlock value = Label(cardSliders, "—", Fonts.Value, 550, center - 13, 124, 26);
                value.TextAlignment = TextAlignment.Right;
                value.FontFamily = Fonts.Wpf;
                stageValueTexts[i] = value;

                Button minus = new Button
                {
                    Style = chipStyle8, Width = 30, Height = 26,
                    Background = Theme.NewBrush(Theme.ChipBg),
                    BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand,
                    Content = LineIcon("M0,6 L12,6", 11, Theme.HexInkSoft)
                };
                AttachPillFeedback(minus, 0.92, Theme.ChipHover, Theme.ChipBg);
                minus.Click += delegate { StepDraft(index, -1); };
                PlaceIn(cardSliders, minus, 692, center - 13, 30, 26);
                stageMinus[i] = minus;

                Button plus = new Button
                {
                    Style = chipStyle8, Width = 30, Height = 26,
                    Background = Theme.NewBrush(Theme.ChipBg),
                    BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand,
                    Content = LineIcon("M0,6 L12,6 M6,0 L6,12", 11, Theme.HexInkSoft)
                };
                AttachPillFeedback(plus, 0.92, Theme.ChipHover, Theme.ChipBg);
                plus.Click += delegate { StepDraft(index, 1); };
                PlaceIn(cardSliders, plus, 728, center - 13, 30, 26);
                stagePlus[i] = plus;
            }

            // 底部应用与恢复按钮: 应用 (174, 522, 180, 40) r12 底 #304FFE, 恢复 (370, 522, 180, 40) r12 底 #EDF2F7
            applyButton = new Button
            {
                Content = "应用到设备", Style = buttonStyle, Width = 180, Height = 40,
                FontSize = Fonts.Body, FontWeight = FontWeights.SemiBold,
                Background = Theme.NewBrush(Theme.Accent),
                Foreground = Brushes.White,
                BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            AttachPillFeedback(applyButton, 0.95, Theme.AccentPressed, Theme.Accent);
            applyButton.Click += delegate { RunApplyDpi(); };
            PlaceIn(cardSliders, applyButton, 24, 328, 180, 40);

            revertButton = new Button
            {
                Content = "恢复设备值", Style = buttonStyle, Width = 180, Height = 40,
                FontSize = Fonts.Body, FontWeight = FontWeights.SemiBold,
                Background = Theme.NewBrush(Theme.ChipBg),
                Foreground = Theme.Brush(Theme.FooterInk),
                BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            AttachPillFeedback(revertButton, 0.95, Theme.ChipHover, Theme.ChipBg);
            revertButton.Click += delegate { RevertDraft(); };
            PlaceIn(cardSliders, revertButton, 220, 328, 180, 40);
        }

        // ─────────────────────────────── 参数页 ───────────────────────────────

        private Button[] BuildChipRow(Canvas host, double x, double y, double height, double chipWidth, double gap,
            string[] captions, Action<int> pick)
        {
            Button[] chips = new Button[captions.Length];
            for (int i = 0; i < captions.Length; i++)
            {
                int index = i;
                Button chip = new Button
                {
                    Content = captions[i], Style = chipStyle12, Width = chipWidth, Height = height,
                    FontSize = Fonts.Chip, FontWeight = FontWeights.SemiBold,
                    Background = Theme.NewBrush(Theme.ChipBg),
                    Foreground = Theme.Brush(Theme.FooterInk),
                    BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand
                };
                AttachPillFeedback(chip, 0.95, Theme.ChipHover, Theme.ChipBg);
                chip.Click += delegate { if (!syncing) pick(index); };
                PlaceIn(host, chip, x + i * (chipWidth + gap), y, chipWidth, height);
                chips[i] = chip;
            }
            return chips;
        }

        private void SyncTab(Button tab, bool selected, string caption)
        {
            if (tab == null) return;
            tab.Content = caption;
            System.Drawing.Color pickTab = selected ? Theme.Accent : Theme.ChipBg;
            Color restingTab = Theme.Media(pickTab);
            tab.Tag = restingTab;
            SolidColorBrush bg = tab.Background as SolidColorBrush;
            if (bg == null || bg.IsFrozen)
            {
                bg = Theme.NewBrush(pickTab);
                tab.Background = bg;
            }
            else
            {
                Transition.AnimateColorTo(bg, restingTab, 180);
            }

            SolidColorBrush fg = tab.Foreground as SolidColorBrush;
            if (fg == null || fg.IsFrozen)
            {
                fg = Theme.NewBrush(selected ? Theme.Surface : Theme.FooterInk);
                tab.Foreground = fg;
            }
            else
            {
                Transition.AnimateColorTo(fg, Theme.Media(selected ? Theme.Surface : Theme.FooterInk), 180);
            }
            tab.FontWeight = FontWeights.SemiBold;
            tab.BorderBrush = Brushes.Transparent;
            tab.BorderThickness = new Thickness(0);
        }

        private void SyncChips(Button[] chips, int selected)
        {
            if (chips == null) return;
            for (int i = 0; i < chips.Length; i++)
            {
                Button chip = chips[i];
                if (chip == null) continue;
                bool on = i == selected;
                // 静止色同时写进 Tag，悬停反馈离开时按它回位（见 AttachPillFeedback）。
                System.Drawing.Color pick = on ? Theme.Accent : Theme.ChipBg;
                Color resting = Theme.Media(pick);
                chip.Tag = resting;
                SolidColorBrush bg = chip.Background as SolidColorBrush;
                if (bg == null || bg.IsFrozen)
                {
                    bg = Theme.NewBrush(pick);
                    chip.Background = bg;
                }
                else
                {
                    // 180ms 交叉过渡：选中态不再"一帧换色"（原则 3）
                    Transition.AnimateColorTo(bg, resting, 180);
                }

                SolidColorBrush fg = chip.Foreground as SolidColorBrush;
                if (fg == null || fg.IsFrozen)
                {
                    fg = Theme.NewBrush(on ? Theme.Surface : Theme.FooterInk);
                    chip.Foreground = fg;
                }
                else
                {
                    Transition.AnimateColorTo(fg, Theme.Media(on ? Theme.Surface : Theme.FooterInk), 180);
                }

                chip.FontWeight = FontWeights.SemiBold;
                chip.BorderBrush = Brushes.Transparent;
                chip.BorderThickness = new Thickness(0);
            }
        }

        private void BuildParamsPage()
        {
            Canvas host = pageParams;

            // 1. 回报率卡片 (150, 76, 846, 112)
            Canvas cardPoll = Card(host, 150, 76, 846, 112);
            CardTitle(cardPoll, "回报率", 24, 18, 120, 26);
            pollingChips = BuildChipRow(cardPoll, 24, 54, 38, 86, 12,
                new[] { "125 Hz", "250 Hz", "500 Hz", "1000 Hz" }, delegate(int i) { PickPolling(i); });

            // 2. 静默高度卡片 (150, 200, 846, 112)
            Canvas cardLod = Card(host, 150, 200, 846, 112);
            CardTitle(cardLod, "静默高度", 24, 18, 120, 26);
            lodChips = BuildChipRow(cardLod, 24, 54, 38, 86, 12,
                new[] { "1 mm", "2 mm", "3 mm" }, delegate(int i) { PickLod(i); });

            motionCheck = new CheckBox { Content = "运动同步", Style = switchStyle, FontSize = Fonts.SwitchLabel, FontWeight = FontWeights.SemiBold, Foreground = Theme.Brush(Theme.Ink) };
            WireSwitch(motionCheck);
            PlaceIn(cardLod, motionCheck, 685, 61, 140, 26);
            motionCheck.Click += delegate { if (!syncing) PickMotion(motionCheck.IsChecked == true); };

            // 3. 进入休眠等待时长卡片 (150, 324, 846, 126)
            Canvas cardSleep = Card(host, 150, 324, 846, 126);
            CardTitle(cardSleep, "进入休眠等待时长", 24, 18, 180, 26);
            Meta(cardSleep, "有线连接时鼠标不休眠", 560, 20, 180);

            sleepValueText = Label(cardSleep, "—", Fonts.Heading, 722, 18, 100, 26);
            sleepValueText.FontWeight = FontWeights.SemiBold;
            sleepValueText.Foreground = Theme.Brush(Theme.Ink);
            sleepValueText.TextAlignment = TextAlignment.Right;

            sleepSlider = new Slider
            {
                Minimum = 5, Maximum = 120, SmallChange = 1, LargeChange = 5,
                TickFrequency = 1, IsSnapToTickEnabled = true, Style = sliderStyle,
                IsMoveToPointEnabled = true, Height = 28
            };
            PlaceIn(cardSleep, sleepSlider, 17, 48, 694, 28);
            sleepSlider.ValueChanged += delegate { OnSleepSliderChanged(); };
            sleepSlider.PreviewMouseLeftButtonUp += delegate { OnSleepCommitted(); };

            // 档位 pills (文案 5 / 10 / 30 / 60 / 120，删掉 AddSliderTicks)
            double[] sleepOffsets = { 0, 70, 140, 216, 286 };
            double[] sleepWidths = { 60, 60, 66, 60, 60 };
            string[] sleepCaps = { "5", "10", "30", "60", "120" };
            sleepChips = new Button[5];
            for (int i = 0; i < 5; i++)
            {
                int index = i;
                Button chip = new Button
                {
                    Content = sleepCaps[i], Style = chipStyle, Width = sleepWidths[i], Height = 32,
                    FontSize = Fonts.Chip, FontWeight = FontWeights.SemiBold,
                    Background = Theme.NewBrush(Theme.ChipBg),
                    Foreground = Theme.Brush(Theme.FooterInk),
                    BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand
                };
                AttachPillFeedback(chip, 0.95, Theme.ChipHover, Theme.ChipBg);
                chip.Click += delegate { if (!syncing) PickSleep(index); };
                PlaceIn(cardSleep, chip, 24 + sleepOffsets[i], 78, sleepWidths[i], 32);
                sleepChips[i] = chip;
            }

            // 4. 按键防抖延迟卡片 (150, 462, 846, 120)
            Canvas cardDebounce = Card(host, 150, 462, 846, 120);
            CardTitle(cardDebounce, "按键防抖延迟", 24, 16, 180, 26);

            debounceTabPress = new Button
            {
                Content = "按下", Style = chipStyle, Width = 70, Height = 30,
                FontSize = Fonts.Chip, FontWeight = FontWeights.SemiBold,
                Background = Theme.NewBrush(Theme.Accent),
                Foreground = Brushes.White,
                BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            AttachPillFeedback(debounceTabPress, 0.95, Theme.ChipHover, Theme.ChipBg);
            debounceTabPress.Click += delegate { SelectDebounceTab(true); };
            PlaceIn(cardDebounce, debounceTabPress, 670, 8, 70, 30);

            debounceTabRelease = new Button
            {
                Content = "抬起", Style = chipStyle, Width = 70, Height = 30,
                FontSize = Fonts.Chip, FontWeight = FontWeights.SemiBold,
                Background = Theme.NewBrush(Theme.ChipBg),
                Foreground = Theme.Brush(Theme.FooterInk),
                BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            AttachPillFeedback(debounceTabRelease, 0.95, Theme.ChipHover, Theme.ChipBg);
            debounceTabRelease.Click += delegate { SelectDebounceTab(false); };
            PlaceIn(cardDebounce, debounceTabRelease, 746, 8, 70, 30);

            double[] dbOffsets = { 0, 80, 160, 240, 324, 408, 492 };
            double[] dbWidths = { 72, 72, 72, 76, 76, 76, 76 };
            string[] dbCaps = { "1 ms", "2 ms", "4 ms", "8 ms", "16 ms", "24 ms", "32 ms" };
            debounceChips = new Button[7];
            for (int i = 0; i < 7; i++)
            {
                int index = i;
                Button chip = new Button
                {
                    Content = dbCaps[i], Style = chipStyle12, Width = dbWidths[i], Height = 36,
                    FontSize = Fonts.Chip, FontWeight = FontWeights.SemiBold,
                    Background = Theme.NewBrush(Theme.ChipBg),
                    Foreground = Theme.Brush(Theme.FooterInk),
                    BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand
                };
                AttachPillFeedback(chip, 0.95, Theme.ChipHover, Theme.ChipBg);
                chip.Click += delegate { if (!syncing) PickDebounce(editingPress, index); };
                PlaceIn(cardDebounce, chip, 24 + dbOffsets[i], 48, dbWidths[i], 36);
                debounceChips[i] = chip;
            }

            // 提示行 paramMessage
            paramMessage = Label(cardDebounce, "", Fonts.Sub, 24, 94, 798, 22);
            paramMessage.TextWrapping = TextWrapping.NoWrap;
            paramMessage.TextTrimming = TextTrimming.CharacterEllipsis;
            paramMessage.Foreground = Theme.Brush(Theme.AccentDeep);
        }

        // ─────────────────────────────── 刷新与数据驱动 ───────────────────────────────

        private static string RateText(int? hz)
        {
            return hz.HasValue ? hz.Value + " Hz" : "—";
        }

        private void Invalidate()
        {
            if (IsDisposed) return;

            SetText(PageModeText, snapshot.Open ? (deviceLine.IndexOf("有线") >= 0 ? "已连接 · USB" : "已连接 · 2.4G") : "未连接");

            ConfigSnapshot currentConfig = config;
            string timeStr = currentConfig == null ? "尚未读取" : "读取于 " + currentConfig.ReadUtc.ToLocalTime().ToString("HH:mm:ss");
            SetText(globalReadTimeText, timeStr);

            if (globalReadBtn != null)
            {
                globalReadBtn.IsEnabled = !configBusy;
                globalReadBtn.ToolTip = configBusy ? "正在读取设备配置…" : overviewMessage;
                SetText(globalReadBtnText, configBusy ? "读取中…" : "读取配置", true);
                globalReadBtn.Opacity = configBusy ? 0.6 : 1.0;
            }

            if (autoCheck != null) autoCheck.IsChecked = autoStartOn;
            if (alertsCheck != null) alertsCheck.IsChecked = preferences.LowBatteryNotification;

            UpdateOverview();
            UpdateDpiPage();
            UpdateParamsPage();
        }

        private void UpdateOverview()
        {
            if (ovStage == null) return;
            SetText(batteryText, level.HasValue ? level.Value + "%" + (batteryFresh ? "" : " · 上次") : "未获取", true);
            bool batteryLow = !level.HasValue || !batteryFresh;
            SolidColorBrush batteryInk = MutableForeground(batteryText, batteryLow ? Theme.Low : Theme.Ink);
            if (batteryInk != null) Transition.AnimateColorTo(batteryInk, Theme.Media(batteryLow ? Theme.Low : Theme.Ink), 180);
            SetText(chargeText, chargeStatus.Length == 0 ? "—" : chargeStatus, true);
            SetText(ovMode, snapshot.Open ? (deviceLine.IndexOf("有线") >= 0 ? "USB 有线" : "2.4G 无线") : "未连接");

            ConfigSnapshot current = config;
            bool has = current != null;
            SetText(ovPolling, has && current.Performance != null ? RateText(current.Performance.CurrentHz) : "—");
            SetText(ovLod, has && current.Sensor != null
                ? (current.Sensor.LiftOffMm.HasValue ? current.Sensor.LiftOffMm.Value + " mm" : "未获取")
                : "—");
            SetText(ovSleep, has && current.Timing != null ? current.Timing.SleepMinutes + " 分钟" : "—");
            int? debounce = has && current.Timing != null
                ? (editingPress ? current.Timing.PressMs : current.Timing.ReleaseMs) : null;
            SetText(ovDebounce, debounce.HasValue ? debounce.Value + " ms · " + (editingPress ? "按下" : "抬起") : "—");
            SetText(ovMotion, has && current.Sensor != null ? (current.Sensor.MotionSync ? "开" : "关") : "—");
            SetText(ovStage, has && current.Dpi != null
                ? "第 " + (current.Dpi.ActiveIndex + 1) + " 档 · " + current.Dpi.Current.ToString() + " DPI"
                : "—");

            if (autoStatusText != null)
            {
                autoStatusText.Text = autoStartOn ? "已启用 · 开机自启" : "未启用";
                SolidColorBrush autoInk = MutableForeground(autoStatusText, autoStartOn ? Theme.Ink : Theme.Faint);
                if (autoInk != null) Transition.AnimateColorTo(autoInk, Theme.Media(autoStartOn ? Theme.Ink : Theme.Faint), 180);
            }
            if (alertsStatusText != null)
            {
                alertsStatusText.Text = preferences.LowBatteryNotification ? "已开启 · 阈值 20%" : "未开启";
                SolidColorBrush alertsInk = MutableForeground(alertsStatusText, preferences.LowBatteryNotification ? Theme.Ink : Theme.Faint);
                if (alertsInk != null) Transition.AnimateColorTo(alertsInk, Theme.Media(preferences.LowBatteryNotification ? Theme.Ink : Theme.Faint), 180);
            }

        }

        private void UpdateDpiPage()
        {
            if (stageSliders == null) return;
            ConfigSnapshot current = config;
            bool has = current != null && current.Dpi != null;

            SetText(dpiValueText, has ? draftValues[draftActive].ToString() : "—", true);
            SetText(dpiRangeText, has
                ? "共 " + current.Dpi.EnabledCount + " 档 · " + FirstEnabledText(current.Dpi) + " – " + MaxEnabledText(current.Dpi)
                : "未读取");

            syncing = true;
            try
            {
                for (int i = 0; i < 7; i++)
                {
                    bool enabled = has && draftEnabled[i];
                    stageSwitches[i].IsChecked = enabled;
                    stageSwitches[i].IsEnabled = has && !configBusy;
                    stageSliders[i].IsEnabled = has && enabled && !configBusy;
                    stageSliders[i].Value = has ? draftValues[i] : 800;
                    stageMinus[i].IsEnabled = has && enabled && !configBusy;
                    stagePlus[i].IsEnabled = has && enabled && !configBusy;
                    SetText(stageValueTexts[i], has ? (enabled ? draftValues[i].ToString() : "未启用") : "—");
                    SolidColorBrush valueInk = MutableForeground(stageValueTexts[i], enabled ? Theme.Ink : Theme.Faint);
                    if (valueInk != null) Transition.AnimateColorTo(valueInk, Theme.Media(enabled ? Theme.Ink : Theme.DisabledInk), 180);
                    // The active DPI value carries the emphasis; inactive enabled values remain regular.
                    stageValueTexts[i].FontWeight = enabled ? (i == draftActive ? FontWeights.Bold : FontWeights.Normal) : FontWeights.SemiBold;
                    SolidColorBrush indexInk = MutableForeground(stageIndexTexts[i], Theme.Muted);
                    if (indexInk != null) Transition.AnimateColorTo(indexInk, Theme.Media(enabled ? (i == draftActive ? Theme.Accent : Theme.FooterInk) : Theme.DisabledInk), 180);
                    stageIndexTexts[i].FontWeight = has && i == draftActive ? FontWeights.Bold : FontWeights.Medium;
                    // Keep the disabled track blue as in the reference; IsEnabled still blocks interaction.

                    if (dpiStageChips != null)
                    {
                        Button chip = dpiStageChips[i];
                        bool active = has && i == draftActive;
                        chip.Content = has ? draftValues[i].ToString() : "—";
                        chip.IsEnabled = enabled && !configBusy;
                        SolidColorBrush bg = chip.Background as SolidColorBrush;
                        if (bg == null || bg.IsFrozen)
                        {
                            bg = Theme.NewBrush(active ? Theme.Accent : Theme.ChipBg);
                            chip.Background = bg;
                        }
                        else
                        {
                            Transition.AnimateColorTo(bg, Theme.Media(active ? Theme.Accent : Theme.ChipBg), 180);
                        }
                        SolidColorBrush fg = chip.Foreground as SolidColorBrush;
                        if (fg == null || fg.IsFrozen)
                        {
                            fg = Theme.NewBrush(active ? Theme.Surface : Theme.FooterInk);
                            chip.Foreground = fg;
                        }
                        else
                        {
                            Transition.AnimateColorTo(fg, Theme.Media(active ? Theme.Surface : Theme.FooterInk), 180);
                        }
                        chip.FontWeight = active ? FontWeights.Bold : FontWeights.SemiBold;
                        chip.ToolTip = !has ? "尚未读取设备档位"
                            : enabled ? "切换到第 " + (i + 1) + " 档 · " + draftValues[i] + " DPI"
                            : "第 " + (i + 1) + " 档未启用";
                    }
                }
            }
            finally { syncing = false; }

            if (applyButton != null)
            {
                bool can = CanApply();
                applyButton.ToolTip = dpiMessage;
                applyButton.IsEnabled = can;
                applyButton.Opacity = can ? 1 : 0.4;
            }
            if (revertButton != null) revertButton.IsEnabled = has && !configBusy && DraftsDirty;
        }

        private static string FirstEnabledText(DpiProfile profile)
        {
            for (int i = 0; i < profile.Values.Length; i++)
                if (profile.Enabled[i]) return profile.Values[i].ToString();
            return "—";
        }

        private static string MaxEnabledText(DpiProfile profile)
        {
            int max = 0;
            for (int i = 0; i < profile.Values.Length; i++)
                if (profile.Enabled[i] && profile.Values[i] > max) max = profile.Values[i];
            return max > 0 ? max.ToString() : "—";
        }

        private void UpdateParamsPage()
        {
            if (pollingChips == null) return;
            ConfigSnapshot current = config;
            syncing = true;
            try
            {
                int pollingIndex = -1;
                if (current != null && current.Performance != null && current.Performance.CurrentHz.HasValue)
                {
                    int[] rates = { 125, 250, 500, 1000 };
                    for (int i = 0; i < rates.Length; i++)
                        if (rates[i] == current.Performance.CurrentHz.Value) pollingIndex = i;
                }
                SyncChips(pollingChips, pollingIndex);
                int lodIndex = -1;
                if (current != null && current.Sensor != null && current.Sensor.LiftOffMm.HasValue)
                    lodIndex = current.Sensor.LiftOffMm.Value - 1;
                SyncChips(lodChips, lodIndex);
                if (motionCheck != null && current != null && current.Sensor != null)
                    motionCheck.IsChecked = current.Sensor.MotionSync;

                TimingBlock timing = current == null ? null : current.Timing;
                int[] sleepValues = { 5, 10, 30, 60, 120 };
                int sleepIndex = -1;
                if (timing != null)
                    for (int i = 0; i < sleepValues.Length; i++)
                        if (sleepValues[i] == timing.SleepMinutes) sleepIndex = i;
                SyncChips(sleepChips, sleepIndex);
                if (timing != null) sleepSlider.Value = Math.Max(5, Math.Min(120, timing.SleepMinutes));
                SetText(sleepValueText, timing == null ? "—" : timing.SleepMinutes + " 分钟");

                bool tabPress = editingPress;
                SyncChips(debounceChips, timing == null ? -1 : (tabPress ? timing.PressIndex : timing.ReleaseIndex));
                SyncTab(debounceTabPress, tabPress, "按下");
                SyncTab(debounceTabRelease, !tabPress, "抬起");

                bool enabled = current != null && !configBusy;
                SetEnabled(pollingChips, enabled);
                SetEnabled(lodChips, enabled);
                SetEnabled(sleepChips, enabled);
                SetEnabled(debounceChips, enabled);
                debounceTabPress.IsEnabled = enabled;
                debounceTabRelease.IsEnabled = enabled;
                if (sleepSlider != null) sleepSlider.IsEnabled = enabled;
                if (motionCheck != null) motionCheck.IsEnabled = enabled;
            }
            finally { syncing = false; }

            SetText(paramMessage, current == null && paramsMessage.Length == 0
                ? "尚未读取参数，点右上角「读取配置」从设备获取。"
                : paramsMessage);
        }

        private static void SetEnabled(Button[] chips, bool enabled)
        {
            if (chips == null) return;
            for (int i = 0; i < chips.Length; i++) chips[i].IsEnabled = enabled;
        }
    }
}

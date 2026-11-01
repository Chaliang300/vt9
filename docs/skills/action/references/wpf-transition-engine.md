# WPF 参考实现：可打断的过渡动画

这是 `action` 的参考骨架。摘出来就能用，但每一处"为什么这么写"都注明了——
照抄时最容易丢掉的正是那些说明里的约束。

## 1. 缓动：起步快收尾轻 + 末段 2% 回弹

```csharp
internal sealed class SettleEase : IEasingFunction
{
    internal static readonly SettleEase Shared = new SettleEase();

    public double Ease(double t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        double main = 1 - Math.Pow(1 - t, 3);                                    // ease-out：t=0 处斜率最大
        double settle = t > 0.75 ? 0.02 * Math.Sin(Math.PI * (t - 0.75) / 0.25) : 0;  // 末段过冲再收回
        return main + settle;
    }
}
```

要"先拉长→加速→减速"的手感时，**不要**改成慢起曲线：把慢起交给手指拖拽（拖动期 1:1、不加缓动），
松手后的动画必须一上来就有速度。

## 2. 续接入口：从"当前值"到新目标

```csharp
internal static class Transition
{
    // target：UIElement / TranslateTransform / BlurEffect 都行（DependencyObject）
    internal static void AnimateTo(DependencyObject target, DependencyProperty property, double to,
        double milliseconds, EventHandler done)
    {
        // 系统关了动画（或高对比度）就一步到位
        if (!SystemParameters.ClientAreaAnimation || SystemParameters.HighContrast)
        {
            target.SetValue(property, to);
            if (done != null) done(null, EventArgs.Empty);
            return;
        }

        // 读属性拿到的是**当前插值**（动画进行中也是），正好当起点
        double from = (double)target.GetValue(property);
        target.BeginAnimation(property, null);   // 只清动画，不动值
        target.SetValue(property, from);         // 当前值固定成新基准

        if (milliseconds <= 0 || Math.Abs(to - from) < 0.5)
        {
            target.SetValue(property, to);
            if (done != null) done(null, EventArgs.Empty);
            return;
        }

        DoubleAnimation move = new DoubleAnimation(from, to,
            new Duration(TimeSpan.FromMilliseconds(milliseconds)))
        {
            EasingFunction = SettleEase.Shared,
            FillBehavior = FillBehavior.HoldEnd   // 保持终值，否则结束瞬间会闪回起点
        };
        move.Completed += delegate
        {
            target.BeginAnimation(property, null);   // 摘掉动画
            target.SetValue(property, to);           // 终值落成基准，避免下次续接读到残留
            if (done != null) done(null, EventArgs.Empty);
        };
        target.BeginAnimation(property, move);
    }

    // 打断时的时长：接着手指的速度走完剩余距离，而不是固定重放一整段
    internal static double Duration(double remainingPixels, double releaseSpeed)
    {
        double speed = Math.Max(400, Math.Abs(releaseSpeed));      // 像素/秒
        return Math.Max(140, Math.Min(340, remainingPixels / speed * 1000));
    }
}
```

三个容易写错的地方：

- `BeginAnimation(property, null)` 之后再 `SetValue(property, from)` —— 只清动画、不重置值，
  这样才是"就地续接"。写成"清动画 + 设回旧值 + 重新播放"就是重放。
- `FillBehavior.HoldEnd` + 在 `Completed` 里清动画并写终值：两头都占，才既不闪回也不残留。
- 不要在这里 `new DoubleAnimationUsingKeyFrames(...)` —— 关键帧被打断只能重放。

## 3. 每个过渡元素的状态与清理

```csharp
private sealed class PageTransition
{
    internal readonly UIElement Page;
    internal readonly TranslateTransform Slide = new TranslateTransform();
    internal readonly BlurEffect Blur = new BlurEffect { Radius = 0, KernelType = KernelType.Gaussian };
    private bool active;

    internal PageTransition(UIElement page)
    {
        Page = page;
        Page.RenderTransform = Slide;   // 只动 RenderTransform，不动 Left/Top/Width/Height
        Page.Opacity = 1;
    }

    // 过渡开始：挂上模糊 + 位图缓存。缓存让"模糊一大片"不必每帧做软件模糊。
    internal void Begin()
    {
        if (active) return;
        active = true;
        Page.Effect = Blur;
        Page.CacheMode = new BitmapCache();
    }

    // 过渡结束：立刻摘掉。静止态留着 Effect/CacheMode，小字会发糊而且一直烧 GPU。
    internal void End()
    {
        if (!active) return;
        active = false;
        Page.Effect = null;
        Page.CacheMode = null;
        Blur.Radius = 0;
        Slide.Y = 0;
        Page.Opacity = 1;
    }
}
```

## 4. 拖动状态机

事件挂 `PreviewMouseLeftButtonDown / Move / Up`（挂窗口或根元素），热区命中才启动：

```csharp
// Down：命中热区 → 记录 startY / lastY / lastStamp；这一步**不要**抢捕获
// Move：|dy| > 6px 才算拖动 → Mouse.Capture(this)（随后原按钮的 Click 不会再触发）
//       位移按下面公式 1:1 施加，直接赋值、不加缓动
// Up  ：按 80px / 520px·s⁻¹ 判定提交或回弹；解除捕获
// LostMouseCapture：当作松手处理，避免 Alt+Tab 之类打断后留下半吊子状态

double p = Math.Max(0, Math.Min(1, Math.Abs(offset) / 160));   // 拖动进度
current.Slide.Y      = offset;
current.Page.Opacity = 1 - 0.5 * p;
current.Blur.Radius  = 5 * p;

neighbor.Page.Visibility = Visibility.Visible;
neighbor.Slide.Y      = offset + step * (56 - 40 * p);          // step=+1：目标在后方
neighbor.Page.Opacity = 0.2 + 0.8 * p;
neighbor.Blur.Radius  = 6 * (1 - p);
```

速度用高精度计时器取，别用 `DateTime.UtcNow`（15ms 粒度算出的甩动速度不稳）：

```csharp
double stamp = (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
```

## 5. 提交 / 回弹 / 点击：都是同一条路

```csharp
// 提交：旧元素只移 48px 就淡出 + 模糊（不飞出屏幕）
int pending = 6;                                   // 2 个元素 × 3 条属性
EventHandler settle = delegate { if (--pending > 0) return; Finish(); };

Transition.AnimateTo(outgoing.Slide, TranslateTransform.YProperty, offset - step * 48, ms, settle);
Transition.AnimateTo(outgoing.Page,  UIElement.OpacityProperty, 0, ms, settle);
Transition.AnimateTo(outgoing.Blur,  BlurEffect.RadiusProperty, 8, ms, settle);
Transition.AnimateTo(incoming.Slide, TranslateTransform.YProperty, 0, ms, settle);
Transition.AnimateTo(incoming.Page,  UIElement.OpacityProperty, 1, ms, settle);
Transition.AnimateTo(incoming.Blur,  BlurEffect.RadiusProperty, 0, ms, settle);

// 回弹：把刚才"跟手"的位移与模糊收回到静止态，相邻元素退回 56px 后隐藏
// 点击：incoming 从 step·40px 处淡入 + 去模糊，outgoing 移 -step·40px 后淡出 + 模糊

void Finish()
{
    outgoing.Page.Visibility = Visibility.Collapsed;
    outgoing.End();
    incoming.End();
    current = incoming;                            // 状态切换放这里，动画只负责"接住"
    Program.Log("页面切换动画完成：" + target);      // 时间维度的证据：截图证明不了动画播过
}
```

## 6. 常见错误写法

| 错误 | 后果 |
| --- | --- |
| `page.Opacity = 0;` 然后从 0 播一段 | 起始帧硬闪一下，且没法在中途打断续接 |
| `DoubleAnimationUsingKeyFrames` / Storyboard | 打断即重放，操作稍快就"弹回去重来" |
| 动画 `Canvas.Left/Top/Width/Height` | 每帧重新测量排版；手算坐标界面会带偏描边与命中区 |
| 给 `Window.Opacity` 做动画 | 窗口背景是 DWM 合成的原生材质时会掉材质、闪一帧 |
| 静止态留 `BlurEffect` / `CacheMode` | 文字发糊、持续占用合成资源 |
| 退场元素滑出视口 | "被打发走"的机械感，正是要避免的 AI 感 |
| 拖动期加缓动 | 手指动了画面不动 → 不跟手 |
| 固定时长重放打断 | 快速来回切时会一顿一顿 |
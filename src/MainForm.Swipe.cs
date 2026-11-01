using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace VT9Status
{
    // Liquid drag, shared capsule/lens geometry and one page-switch entry.
    // Cards retain the common 420ms entrance with 55ms stagger.
    internal sealed partial class MainForm
    {
        private Border navPill;
        private TranslateTransform navPillTranslate;
        private ScaleTransform navPillScale;

        // 导航项垂直步长（104px）
        private const double NavStep = 104.0;

        private Canvas[] panelPages;
        private PanelPage activePage = (PanelPage)(-1);
        private Canvas currentlyAnimatingPage = null;
        // 动画代次：被新目标顶掉的那条动画即使收到 Completed（BeginAnimation(null) 也会
        // 触发一次收尾），也不能再写日志、清状态，否则日志会出现"没切过去也报完成"。
        private int navGeneration;
        private int entranceGeneration;

        private bool isSwiping;
        private bool dragDown;
        private Point dragStartPt;
        private double dragStartPillY;
        private readonly DockPhysics dockPhysics = new DockPhysics();
        private System.Windows.Threading.DispatcherTimer dockFollowTimer;
        private double dockFrameTime;
        private int pendingTab;
        private double lastDockDragEndTime = double.NegativeInfinity;

        private static bool IsAnimationEnabled()
        {
            return Transition.IsAnimationEnabled();
        }

        // 统一属性动画与打断工具（原则 1 核心）
        internal static class Transition
        {
            internal static bool IsAnimationEnabled()
            {
                return SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;
            }

            internal static void AnimateTo(IAnimatable target, DependencyProperty property, double to,
                double milliseconds, IEasingFunction easing = null, EventHandler done = null)
            {
                if (target == null) return;
                DependencyObject d = target as DependencyObject;
                if (d == null) return;

                if (!IsAnimationEnabled())
                {
                    d.SetValue(property, to);
                    if (done != null) done(null, EventArgs.Empty);
                    return;
                }

                double from = (double)d.GetValue(property);
                target.BeginAnimation(property, null); // 清除旧动画
                d.SetValue(property, from);            // 固化当前实时值，绝不闪回

                if (milliseconds <= 0 || Math.Abs(to - from) < 0.05)
                {
                    d.SetValue(property, to);
                    if (done != null) done(null, EventArgs.Empty);
                    return;
                }

                DoubleAnimation anim = new DoubleAnimation(from, to, new Duration(TimeSpan.FromMilliseconds(milliseconds)))
                {
                    EasingFunction = easing ?? SlideEase.Shared,
                    FillBehavior = FillBehavior.HoldEnd
                };
                anim.Completed += delegate
                {
                    target.BeginAnimation(property, null);
                    d.SetValue(property, to);
                    if (done != null) done(null, EventArgs.Empty);
                };
                target.BeginAnimation(property, anim);
            }

            // 颜色版续接：读画刷当前的插值颜色 → 清动画 → 固化 → from→to。
            // 与 AnimateTo 同一套口径，只是目标属性是 Color 而不是 double；
            // 选中态、开关底色、悬停底色都走这里，避免"一帧换色"的硬切。
            internal static void AnimateColorTo(SolidColorBrush brush, Color to, double milliseconds,
                IEasingFunction easing = null, EventHandler done = null)
            {
                if (brush == null) return;

                if (!IsAnimationEnabled())
                {
                    brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                    brush.Color = to;
                    if (done != null) done(null, EventArgs.Empty);
                    return;
                }

                Color from = brush.Color; // 动画进行中读到的是当前插值
                brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                brush.Color = from;

                if (milliseconds <= 0 || from == to)
                {
                    brush.Color = to;
                    if (done != null) done(null, EventArgs.Empty);
                    return;
                }

                ColorAnimation anim = new ColorAnimation(from, to, new Duration(TimeSpan.FromMilliseconds(milliseconds)))
                {
                    EasingFunction = easing ?? SlideEase.Shared,
                    FillBehavior = FillBehavior.HoldEnd
                };
                anim.Completed += delegate
                {
                    brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                    brush.Color = to;
                    if (done != null) done(null, EventArgs.Empty);
                };
                brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
            }
        }

        private void InitSwipeEngine()
        {
            panelPages = new Canvas[] { pageOverview, pageDpi, pageParams, pageSettings };
        }

        private static double GetTimeSec()
        {
            return (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
        }

        private static int GetNavIndexFromLocalY(double localY)
        {
            for (int i = 0; i < 4; i++)
            {
                double itemTop = 9.0 + i * NavStep - 14.0;
                double itemBottom = 9.0 + i * NavStep + 76.0 + 14.0;
                if (localY >= itemTop && localY <= itemBottom) return i;
            }
            return Math.Max(0, Math.Min(3, (int)Math.Round((localY - 47.0) / NavStep)));
        }

        // ── navDockContainer 独占手势处理（按住即拖动，单击即切换）──

        private void OnDockMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            dragDown = true;
            isSwiping = false;
            dragStartPt = e.GetPosition(navDockContainer);

            if (navPillTranslate != null)
            {
                double cur = (double)navPillTranslate.GetValue(TranslateTransform.YProperty);
                navPillTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                navPillTranslate.Y = cur;
                dragStartPillY = cur;
            }
            else
            {
                dragStartPillY = Math.Max(0, (int)activePage) * NavStep;
            }

            StopDockFollow();
            double sy = navPillScale == null ? 1 : navPillScale.ScaleY;
            if (navPillScale != null)
            {
                navPillScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                navPillScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                navPillScale.ScaleY = sy;
            }
            dockPhysics.Maximum = Math.Max(312, navShelf.ActualHeight - 94);
            dockPhysics.Reset(dragStartPillY, sy);
            pendingTab = Math.Max(0, (int)activePage);

            if (navDockContainer != null)
            {
                navDockContainer.CaptureMouse();
                navDockContainer.Focus();
            }
        }

        private void OnDockMouseMove(object sender, MouseEventArgs e)
        {
            if (!dragDown) return;
            // Mouse-up can be lost when the pointer leaves the window or focus changes.
            // Returning without clearing capture leaves the next press attached to this drag.
            if (e.LeftButton != MouseButtonState.Pressed || !navDockContainer.IsMouseCaptured)
            {
                CancelDockGesture();
                return;
            }
            Point pt = e.GetPosition(navDockContainer);
            double deltaY = pt.Y - dragStartPt.Y;

            if (!isSwiping)
            {
                if (Math.Abs(deltaY) >= 4.0) // 4px 阈值
                {
                    isSwiping = true;
                }
            }

            if (isSwiping)
            {
                ApplyNavPillFluidDrag(deltaY);
            }
        }

        private void OnDockMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || !dragDown) return;
            dragDown = false;
            Point pt = e.GetPosition(navDockContainer);

            // 先记住"这一轮是不是拖过"，再释放捕获：ReleaseMouseCapture 会同步触发
            // LostMouseCapture，如果在那里清掉 isSwiping，松手就会被误判成一次点击
            // （拖到别处松手会额外切一次页，等于没吞掉 click）。
            bool swiped = isSwiping;
            isSwiping = false;
            e.Handled = true;

            if (navDockContainer != null && navDockContainer.IsMouseCaptured)
            {
                navDockContainer.ReleaseMouseCapture();
            }

            if (swiped)
            {
                FinishNavPillFluidSwipe();
            }
            else
            {
                int clickedIndex = GetNavIndexFromLocalY(pt.Y);
                if (clickedIndex >= 0)
                {
                    ClickDockTab((PanelPage)clickedIndex);
                }
            }
        }

        private void OnDockLostCapture(object sender, MouseEventArgs e)
        {
            CancelDockGesture();
        }

        private void CancelDockGesture()
        {
            if (!dragDown && !isSwiping) return;
            bool swiped = isSwiping;
            StopDockFollow();
            dragDown = false;
            isSwiping = false;
            if (navDockContainer != null && navDockContainer.IsMouseCaptured)
                navDockContainer.ReleaseMouseCapture();
            if (swiped) FinishNavPillFluidSwipe();
            else if ((int)activePage >= 0) NavigateToPage(activePage, true);
        }

        // The target is pointer-driven; a short-lived frame loop supplies weight and shape.
        private void ApplyNavPillFluidDrag(double deltaY)
        {
            if (navPillTranslate == null) return;
            dockPhysics.SetTarget(dragStartPillY + deltaY);
            if (!IsAnimationEnabled())
            {
                navPillTranslate.Y = Math.Max(0, Math.Min(dockPhysics.Maximum, dockPhysics.RawTarget));
                pendingTab = NearestDockTab(navPillTranslate.Y);
                return;
            }
            if (dockFollowTimer == null)
            {
                dockFollowTimer = new System.Windows.Threading.DispatcherTimer(
                    System.Windows.Threading.DispatcherPriority.Render);
                dockFollowTimer.Interval = TimeSpan.FromMilliseconds(16);
                dockFollowTimer.Tick += delegate
                {
                    if (!dragDown || !isSwiping) { StopDockFollow(); return; }
                    double now = GetTimeSec();
                    AdvanceDockPhysics(now - dockFrameTime);
                    dockFrameTime = now;
                };
            }
            if (!dockFollowTimer.IsEnabled)
            {
                dockFrameTime = GetTimeSec();
                dockFollowTimer.Start();
            }
        }

        private static int NearestDockTab(double position)
        {
            return Math.Max(0, Math.Min(3, (int)Math.Floor(position / NavStep + 0.5)));
        }

        private void AdvanceDockPhysics(double seconds)
        {
            dockPhysics.Step(seconds);
            navPillTranslate.Y = dockPhysics.Position;
            navPillScale.ScaleY = dockPhysics.ScaleY;
            pendingTab = NearestDockTab(dockPhysics.Position);
        }

        private void StopDockFollow()
        {
            if (dockFollowTimer != null) dockFollowTimer.Stop();
        }

        private void FinishNavPillFluidSwipe()
        {
            if (navPillTranslate == null) return;
            StopDockFollow();
            pendingTab = NearestDockTab(navPillTranslate.Y);
            lastDockDragEndTime = GetTimeSec();
            NavigateToPage((PanelPage)pendingTab, true, dockPhysics.IsWallPressed);
        }

        private void ClickDockTab(PanelPage target)
        {
            if (GetTimeSec() - lastDockDragEndTime < 0.120) return;
            NavigateToPage(target, IsVisible);
        }

        // Shared snap: distance-based braking, with overshoot only after wall compression.

        private void NavigateToPage(PanelPage target, bool animate = true, bool wallRelease = false)
        {
            if (pageOverview == null) return;
            StopDockFollow();
            int targetIndex = (int)target;
            if (targetIndex < 0 || targetIndex >= panelPages.Length) return;
            bool pageChanged = target != activePage;
            if (pageChanged)
            {
                activePage = target;
                ShowRightPage(target, animate);
            }
            // Same tab: settle the capsule only. Never restart the visible cards.

            double destY = targetIndex * NavStep;

            if (!animate || !IsAnimationEnabled() || !IsVisible)
            {
                navGeneration++;
                if (navPillTranslate != null)
                {
                    navPillTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                    navPillTranslate.Y = destY;
                }
                if (navPillScale != null)
                {
                    navPillScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    navPillScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                    navPillScale.ScaleY = 1.0;
                }
                UpdateNavLabelsVisual(targetIndex);
                Program.Log("页面切换完成：" + PageName(target));
                if (target == PanelPage.Params && config == null && !configBusy)
                {
                    Dispatcher.BeginInvoke(new Action(RunReadConfig), System.Windows.Threading.DispatcherPriority.Background);
                }
                Invalidate();
                return;
            }

            double durationMs = DockPhysics.SnapDuration(destY - navPillTranslate.Y, wallRelease);
            IEasingFunction snapEase = wallRelease ? ReferenceEase.DockBounce : ReferenceEase.Dock;
            int generation = ++navGeneration;

            if (navPillScale != null)
            {
                Transition.AnimateTo(navPillScale, ScaleTransform.ScaleYProperty, 1.0, durationMs, snapEase);
            }

            Transition.AnimateTo(navPillTranslate, TranslateTransform.YProperty, destY, durationMs, snapEase, delegate
            {
                // 被后面的点击顶掉时，这条动画的收尾回调什么都不该做
                if (generation != navGeneration) return;
                Program.Log("页面切换动画完成：" + PageName(target));
                if (target == PanelPage.Params && config == null && !configBusy)
                {
                    Dispatcher.BeginInvoke(new Action(RunReadConfig), System.Windows.Threading.DispatcherPriority.Background);
                }
                Invalidate();
            });

            UpdateNavLabelsVisual(targetIndex);
        }

        // 右侧主页面切换：旧页同帧撤除，新页卡片错峰滑入
        private void ShowRightPage(PanelPage target, bool animate = true)
        {
            if (panelPages == null) return;
            int targetIndex = (int)target;
            AlignDockToPage(panelPages[targetIndex]);
            // 每次换页开一代新的入场动画：上一代还没播完的卡片收尾回调会被这一代作废。
            int generation = ++entranceGeneration;

            for (int i = 0; i < panelPages.Length; i++)
            {
                Canvas page = panelPages[i];
                if (page == null) continue;
                bool isTarget = i == targetIndex;

                if (!isTarget)
                {
                    // 彻底停止上一页卡片动画并落定终值，绝不留半透明残影
                    System.Collections.Generic.List<CardHostItem> oldCards;
                    if (pageCards.TryGetValue(page, out oldCards) && oldCards != null)
                    {
                        foreach (var c in oldCards)
                        {
                            c.Element.BeginAnimation(UIElement.OpacityProperty, null);
                            TranslateTransform t = c.Element.RenderTransform as TranslateTransform;
                            if (t != null)
                            {
                                t.BeginAnimation(TranslateTransform.XProperty, null);
                                t.X = 0;
                            }
                            c.Element.Opacity = 1.0;
                            c.Element.Effect = null;
                            c.Element.CacheMode = null;
                        }
                    }
                    // 入场被切走时清掉"正在入场"的标记：否则下次回到这一页会被误判成
                    // "续接"，卡片直接落位、不播入场动画。
                    if (currentlyAnimatingPage == page) currentlyAnimatingPage = null;
                    page.Visibility = Visibility.Collapsed;
                    page.Opacity = 0.0;
                    page.Effect = null;
                    page.CacheMode = null;
                }
                else
                {
                    page.Visibility = Visibility.Visible;
                    page.Opacity = 1.0;
                    page.Effect = null;
                    page.CacheMode = null;
                    PlayPageCards(page, animate, generation);
                }
            }
        }

        // 卡片错峰滑入与打断处理（原则 1、2、4）
        private void PlayPageCards(Canvas page, bool animate, int generation)
        {
            if (page == null) return;
            System.Collections.Generic.List<CardHostItem> cards;
            if (!pageCards.TryGetValue(page, out cards) || cards == null || cards.Count == 0)
                return;

            // 先按 (中心 y, x) 排序（同一行内从左到右）
            cards.Sort(delegate(CardHostItem a, CardHostItem b)
            {
                int byY = a.Y.CompareTo(b.Y);
                return byY != 0 ? byY : a.X.CompareTo(b.X);
            });

            int n = cards.Count;

            if (!animate || !IsAnimationEnabled() || !IsVisible)
            {
                foreach (var item in cards)
                {
                    item.Element.BeginAnimation(UIElement.OpacityProperty, null);
                    TranslateTransform tt = item.Element.RenderTransform as TranslateTransform;
                    if (tt != null)
                    {
                        tt.BeginAnimation(TranslateTransform.XProperty, null);
                        tt.X = 0;
                    }
                    item.Element.Opacity = 1.0;
                    item.Element.Effect = null;
                    item.Element.CacheMode = null;
                }
                return;
            }

            currentlyAnimatingPage = page;

            double cardDuration = 420.0;
            double staggerDelay = 55.0;

            for (int i = 0; i < n; i++)
            {
                CardHostItem item = cards[i];
                TranslateTransform tt = item.Element.RenderTransform as TranslateTransform;
                if (tt == null)
                {
                    tt = new TranslateTransform(72, 0);
                    item.Element.RenderTransform = tt;
                }

                double startX = 72.0;
                double currentDuration = cardDuration;
                tt.BeginAnimation(TranslateTransform.XProperty, null);
                tt.X = startX;

                double startOpacity = 0.0;
                item.Element.BeginAnimation(UIElement.OpacityProperty, null);
                item.Element.Opacity = startOpacity;

                double beginMs = i * staggerDelay;
                DoubleAnimation animX = new DoubleAnimation(startX, 0.0, new Duration(TimeSpan.FromMilliseconds(currentDuration)))
                {
                    BeginTime = TimeSpan.FromMilliseconds(beginMs),
                    EasingFunction = ReferenceEase.Card,
                    FillBehavior = FillBehavior.HoldEnd
                };

                DoubleAnimation animOp = new DoubleAnimation(item.Element.Opacity, 1.0, new Duration(TimeSpan.FromMilliseconds(currentDuration)))
                {
                    BeginTime = TimeSpan.FromMilliseconds(beginMs),
                    EasingFunction = ReferenceEase.Card,
                    FillBehavior = FillBehavior.HoldEnd
                };

                int cardIndex = i;
                if (cardIndex == n - 1)
                {
                    animX.Completed += delegate
                    {
                        // 这一代入场已被新目标顶掉：不要写日志、不要清别人的状态
                        if (generation != entranceGeneration) return;
                        currentlyAnimatingPage = null;
                        foreach (var c in cards)
                        {
                            c.Element.BeginAnimation(UIElement.OpacityProperty, null);
                            TranslateTransform t = c.Element.RenderTransform as TranslateTransform;
                            if (t != null)
                            {
                                t.BeginAnimation(TranslateTransform.XProperty, null);
                                t.X = 0;
                            }
                            c.Element.Opacity = 1.0;
                            c.Element.Effect = null;
                            c.Element.CacheMode = null;
                        }
                        // Last card settles after (count - 1) delays plus its own duration.
                        int totalMs = (int)Math.Round((n - 1) * staggerDelay + cardDuration);
                        Program.Log("卡片入场完成：" + PageName(activePage) + "（" + n + " 张，" + totalMs + " ms）");
                    };
                }

                tt.BeginAnimation(TranslateTransform.XProperty, animX);
                item.Element.BeginAnimation(UIElement.OpacityProperty, animOp);
            }
        }

        // Keep the base text stable; the moving lens supplies the selected weight.
        private void UpdateNavLabelsVisual(int selectedIndex)
        {
            if (navLabels == null) return;
            string[] captions = { "概况", "DPI", "参数", "设置" };
            for (int i = 0; i < navLabels.Length; i++)
            {
                TextBlock tb = navLabels[i];
                if (tb == null) continue;
                bool selected = i == selectedIndex;

                SolidColorBrush ink = tb.Foreground as SolidColorBrush;
                if (ink == null || ink.IsFrozen)
                {
                    ink = Theme.NewBrush(Theme.InkSoft);
                    tb.Foreground = ink;
                }
                Transition.AnimateColorTo(ink, Theme.Media(Theme.InkSoft), 200);

                System.Windows.Automation.AutomationProperties.SetName(tb, captions[i] + (selected ? "，已选中" : ""));

            }
        }

        // Device readings update immediately, without scale or stale completion callbacks.
        private void FadeText(TextBlock block, string newText)
        {
            if (block == null) return;
            block.BeginAnimation(UIElement.OpacityProperty, null);
            block.Opacity = 1;
            block.RenderTransform = null;
            block.Text = newText;
        }

        private static string PageName(PanelPage page)
        {
            switch (page)
            {
                case PanelPage.Overview: return "概况";
                case PanelPage.Dpi: return "DPI";
                case PanelPage.Params: return "参数";
                case PanelPage.Settings: return "设置";
                default: return page.ToString();
            }
        }

        // ── 动效诊断快照（--ui-motion-test 用）──
        // 读的是"生效值"：动画进行中 GetValue 拿到的就是当前插值，正好能证明
        // 打断时胶囊停在半路、而不是闪回起点；静止时又能证明 Effect/CacheMode 已清空。
        internal object MotionSnapshot()
        {
            double pillY = navPillTranslate == null ? 0.0 : (double)navPillTranslate.GetValue(TranslateTransform.YProperty);
            System.Collections.Generic.List<object> cards = new System.Collections.Generic.List<object>();
            System.Collections.Generic.List<object> pages = new System.Collections.Generic.List<object>();
            for (int i = 0; i < (panelPages == null ? 0 : panelPages.Length); i++)
            {
                Canvas page = panelPages[i];
                if (page == null) continue;
                pages.Add(new { name = PageName((PanelPage)i), visibility = page.Visibility.ToString(), opacity = page.Opacity });
                System.Collections.Generic.List<CardHostItem> list;
                if (!pageCards.TryGetValue(page, out list) || list == null) continue;
                foreach (CardHostItem item in list)
                {
                    TranslateTransform t = item.Element.RenderTransform as TranslateTransform;
                    cards.Add(new
                    {
                        page = PageName((PanelPage)i),
                        x = t == null ? 0.0 : (double)t.GetValue(TranslateTransform.XProperty),
                        opacity = item.Element.Opacity,
                        onScreen = item.Element.IsVisible,
                        effect = item.Element.Effect != null,
                        cache = item.Element.CacheMode != null
                    });
                }
            }
            return new { page = PageName(activePage), pillY = pillY, pages = pages, cards = cards };
        }
    }
}

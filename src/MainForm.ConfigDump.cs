using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace VT9Status
{
    // 原始配置块诊断的显示层。七行 HEX 塞不进「启动与提醒」卡里那条 44px 高的状态行——
    // TextBlock 会把它裁成三行，看起来像读全了，其实后面四块没显示。改成和外观设置同一个
    // 浮层样式，并带一个复制按钮。
    internal sealed partial class MainForm
    {
        private Popup configDumpPopup;
        private TextBlock configDumpText, configDumpStatus;

        private void ShowConfigDump(string text)
        {
            if (configDumpPopup == null) BuildConfigDumpPopup();
            configDumpText.Text = text;
            configDumpStatus.Text = "共 " + text.Split('\n').Length + " 块 · 同一份内容已写入 data/app.log";
            configDumpPopup.IsOpen = true;
        }

        private void BuildConfigDumpPopup()
        {
            StackPanel content = new StackPanel { Width = 424 };

            Grid topRow = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            TextBlock title = new TextBlock
            {
                Text = "原始配置块", FontSize = Fonts.Heading, FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Brush(Theme.Ink), VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(title, 0);

            Button close = new Button
            {
                Width = 24, Height = 24, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand, Style = buttonStyle, ToolTip = "关闭",
                Content = LineIcon("M 1,1 L 9,9 M 9,1 L 1,9", 10, Theme.HexInkSoft)
            };
            close.Click += delegate { configDumpPopup.IsOpen = false; };
            Grid.SetColumn(close, 1);

            topRow.Children.Add(title);
            topRow.Children.Add(close);
            content.Children.Add(topRow);

            configDumpText = new TextBlock
            {
                FontSize = Fonts.Meta, Foreground = Theme.Brush(Theme.Ink),
                LineHeight = 18, TextWrapping = TextWrapping.NoWrap
            };
            content.Children.Add(configDumpText);

            configDumpStatus = new TextBlock
            {
                FontSize = Fonts.Meta, Foreground = Theme.Brush(Theme.Muted),
                Margin = new Thickness(0, 10, 0, 14), TextWrapping = TextWrapping.Wrap
            };
            content.Children.Add(configDumpStatus);

            StackPanel buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            Button copy = new Button
            {
                Content = "复制", Style = buttonStyle, Height = 32, Padding = new Thickness(20, 0, 20, 0),
                Background = Theme.Brush(Theme.Accent), Foreground = Brushes.White, BorderThickness = new Thickness(0),
                FontSize = Fonts.Body, Cursor = System.Windows.Input.Cursors.Hand, Margin = new Thickness(0, 0, 10, 0)
            };
            copy.Click += delegate
            {
                try { Clipboard.SetText(configDumpText.Text); configDumpStatus.Text = "已复制到剪贴板。"; }
                catch (Exception ex) { configDumpStatus.Text = "复制失败：" + ex.Message; }
            };

            Button done = new Button
            {
                Content = "关闭", Style = buttonStyle, Height = 32, Padding = new Thickness(20, 0, 20, 0),
                Background = Theme.Brush(Theme.ChipBg), Foreground = Theme.Brush(Theme.FooterInk),
                BorderThickness = new Thickness(0), FontSize = Fonts.Body,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            done.Click += delegate { configDumpPopup.IsOpen = false; };

            buttons.Children.Add(copy);
            buttons.Children.Add(done);
            content.Children.Add(buttons);

            Border panel = new Border
            {
                Background = Brushes.White, BorderBrush = Theme.Brush(Theme.Track),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16),
                Padding = new Thickness(18, 16, 18, 16),
                Effect = new DropShadowEffect
                {
                    Color = Color.FromArgb(255, 30, 40, 36), Direction = 270, ShadowDepth = 4,
                    BlurRadius = 18, Opacity = 0.16
                },
                Child = content
            };

            Grid root = new Grid { Margin = new Thickness(12) };
            root.Children.Add(panel);

            configDumpPopup = new Popup
            {
                PlacementTarget = surface, Placement = PlacementMode.Center, StaysOpen = false,
                AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade, Child = root
            };
            IsVisibleChanged += delegate { if (!IsVisible && configDumpPopup != null) configDumpPopup.IsOpen = false; };
            Closed += delegate { if (configDumpPopup != null) configDumpPopup.IsOpen = false; };
        }
    }
}

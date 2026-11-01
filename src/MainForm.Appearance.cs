using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace VT9Status
{
    internal sealed partial class MainForm
    {
        private SolidColorBrush cardBackgroundBrush, windowOverlayBrush;
        private Slider cardTransparencySlider, windowTransparencySlider;
        private TextBlock cardTransparencyValue, windowTransparencyValue;

        private static bool TryAppearanceColor(string text, out Color color)
        {
            color = Colors.White;
            string hex = (text ?? "").Trim();
            if (hex.StartsWith("#")) hex = hex.Substring(1);
            uint rgb;
            if (hex.Length != 6 || !UInt32.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out rgb)) return false;
            color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            return true;
        }

        private static Color AppearanceColor(string hex, int transparency)
        {
            Color color;
            if (!TryAppearanceColor(hex, out color)) color = Colors.White;
            color.A = (byte)Math.Round(255 * (100 - Math.Max(0, Math.Min(100, transparency))) / 100.0);
            return color;
        }

        private SolidColorBrush CardBackgroundBrush
        {
            get
            {
                return cardBackgroundBrush ?? (cardBackgroundBrush = new SolidColorBrush(
                    AppearanceColor(preferences.CardBackgroundColor, preferences.CardBackgroundTransparency)));
            }
        }

        private SolidColorBrush WindowOverlayBrush
        {
            get
            {
                return windowOverlayBrush ?? (windowOverlayBrush = new SolidColorBrush(
                    AppearanceColor(preferences.WindowBackgroundColor, preferences.WindowBackgroundTransparency)));
            }
        }

        // 分组外观：798×62 r20 #F8FAFC
        private Border BuildTransparencyGroup(string title, int initialValue, out Slider slider, out TextBlock valueLabel)
        {
            Border card = new Border
            {
                Width = 798,
                Height = 62,
                Background = Theme.Brush(Theme.GroupBg),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(20),
                ToolTip = "调节 " + title + "（0% 不透明 / 100% 完全透明）"
            };

            Canvas groupCanvas = new Canvas { Width = 798, Height = 62 };

            TextBlock titleBlock = new TextBlock
            {
                Text = title,
                FontSize = Fonts.Body,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Brush(Theme.Ink),
                VerticalAlignment = VerticalAlignment.Center
            };
            Canvas.SetLeft(titleBlock, 18);
            Canvas.SetTop(titleBlock, 21);
            groupCanvas.Children.Add(titleBlock);

            valueLabel = new TextBlock
            {
                Width = 100,
                Text = Math.Max(0, Math.Min(100, initialValue)) + "%",
                FontSize = Fonts.Value,
                FontWeight = FontWeights.Bold,
                Foreground = Theme.Brush(Theme.Accent),
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            Canvas.SetLeft(valueLabel, 678); // 20px inset; aligns with the track endpoint at window x=952.
            Canvas.SetTop(valueLabel, 21);
            groupCanvas.Children.Add(valueLabel);

            slider = new Slider
            {
                Style = sliderStyle,
                Width = 774,
                Height = 24,
                Minimum = 0,
                Maximum = 100,
                TickFrequency = 1,
                SmallChange = 1,
                LargeChange = 10,
                IsSnapToTickEnabled = true,
                Value = Math.Max(0, Math.Min(100, initialValue)),
                ToolTip = "0% 不透明 / 100% 完全透明；实时生效并自动保存"
            };
            System.Windows.Automation.AutomationProperties.SetName(slider, title);
            Canvas.SetLeft(slider, 11);
            Canvas.SetTop(slider, 36);
            groupCanvas.Children.Add(slider);

            card.Child = groupCanvas;
            return card;
        }

        // 独立设置界面
        private void BuildSettingsPage()
        {
            Canvas host = pageSettings;

            // ── 卡片 1：外观与材质设置 (150, 76, 846, 236) ──
            Canvas cardApp = Card(host, 150, 76, 846, 236);
            CardTitle(cardApp, "外观与材质", 24, 18, 120, 26);
            Meta(cardApp, "调节毛玻璃 Acrylic 材质与卡片半透明度", 142, 20, 360);

            Button reset = new Button
            {
                Content = "恢复默认",
                Style = chipStyle12,
                Width = 102,
                Height = 34,
                FontSize = Fonts.Chip,
                FontWeight = FontWeights.SemiBold,
                Background = Theme.NewBrush(Theme.ChipBg),
                Foreground = Theme.Brush(Theme.FooterInk),
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            AttachPillFeedback(reset, 0.95, Theme.ChipHover, Theme.ChipBg);
            reset.Click += delegate
            {
                if (cardTransparencySlider != null) cardTransparencySlider.Value = 45;
                if (windowTransparencySlider != null) windowTransparencySlider.Value = 80;
                preferences.CardBackgroundColor = "#FFFFFF";
                preferences.WindowBackgroundColor = "#FFFFFF";
                CardBackgroundBrush.Color = AppearanceColor("#FFFFFF", 45);
                WindowOverlayBrush.Color = AppearanceColor("#FFFFFF", 80);
                preferences.CardBackgroundTransparency = 45;
                preferences.WindowBackgroundTransparency = 80;
                preferences.Save();
            };
            PlaceIn(cardApp, reset, 720, 16, 102, 34);

            Border cardGroup = BuildTransparencyGroup("卡片背景透明度", preferences.CardBackgroundTransparency,
                out cardTransparencySlider, out cardTransparencyValue);
            PlaceIn(cardApp, cardGroup, 24, 62, 798, 62);
            cardTransparencySlider.ValueChanged += delegate
            {
                int next = (int)Math.Round(cardTransparencySlider.Value);
                preferences.CardBackgroundTransparency = next;
                CardBackgroundBrush.Color = AppearanceColor(preferences.CardBackgroundColor, next);
                cardTransparencyValue.Text = next + "%";
                preferences.Save();
            };

            Border windowGroup = BuildTransparencyGroup("整窗背景透明度", preferences.WindowBackgroundTransparency,
                out windowTransparencySlider, out windowTransparencyValue);
            PlaceIn(cardApp, windowGroup, 24, 138, 798, 62);
            windowTransparencySlider.ValueChanged += delegate
            {
                int next = (int)Math.Round(windowTransparencySlider.Value);
                preferences.WindowBackgroundTransparency = next;
                WindowOverlayBrush.Color = AppearanceColor(preferences.WindowBackgroundColor, next);
                windowTransparencyValue.Text = next + "%";
                preferences.Save();
            };


            // ── 卡片 2：程序与系统运行环境 (150, 326, 846, 256) ──
            Canvas cardEnv = Card(host, 150, 326, 846, 256);
            CardTitle(cardEnv, "程序与运行环境", 24, 18, 160, 26);

            string appVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
            TextBlock ver = Label(cardEnv, "VT9 控制台 " + appVersion, Fonts.Sub, 622, 20, 200, 22);
            ver.TextAlignment = TextAlignment.Right;
            ver.Foreground = Theme.Brush(Theme.Faint);

            // 四行：label 14.5px #64748B 左 24，值 14.5px SemiBold #111827 左 124；基线 70 / 110 / 150 / 190
            SubLabel(cardEnv, "适配设备", 24, 55, 90);
            TextBlock t1 = Label(cardEnv, "雷柏 VT9 系列游戏鼠标 (支持 VID 24AE / PID 1849 & 4191)", Fonts.Body, 124, 55, 698, 22);
            t1.FontWeight = FontWeights.SemiBold;
            t1.Foreground = Theme.Brush(Theme.Ink);

            SubLabel(cardEnv, "通信通道", 24, 95, 90);
            TextBlock t2 = Label(cardEnv, "Win32 HID 异步 Overlapped 报文推送与 0xBA 命令管道", Fonts.Body, 124, 95, 698, 22);
            t2.FontWeight = FontWeights.SemiBold;
            t2.Foreground = Theme.Brush(Theme.Ink);

            SubLabel(cardEnv, "背景材质", 24, 135, 90);
            TextBlock t3 = Label(cardEnv, "Windows 11 DWM 原生 Acrylic (亚克力实时硬件合成)", Fonts.Body, 124, 135, 698, 22);
            t3.FontWeight = FontWeights.SemiBold;
            t3.Foreground = Theme.Brush(Theme.Ink);

            SubLabel(cardEnv, "界面系统", 24, 175, 90);
            TextBlock t4 = Label(cardEnv, ".NET Framework 4.8 + WPF 纯代码现代轻量控制台", Fonts.Body, 124, 175, 698, 22);
            t4.FontWeight = FontWeights.SemiBold;
            t4.Foreground = Theme.Brush(Theme.Ink);
        }
    }
}

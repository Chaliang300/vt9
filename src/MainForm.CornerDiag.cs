using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace VT9Status
{
    // 诊断：窗口最外围圆角到底有几层、各自由谁绘制。
    //
    // 同一时刻取三张图对照：
    //   1) 屏幕合成结果（CopyFromScreen）——DWM 圆角 + Acrylic + WPF 图层全叠在一起；
    //   2) WPF 图层（RenderTargetBitmap(form)）——只有 WPF 画的东西，没有 DWM 圆角；
    //   3) 根 Border（RenderTargetBitmap(form.Content)）——看 CornerRadius=24 自己画成什么样。
    // 台座是纯品红（#FF00FF）：界面里不可能出现这个颜色，凡是品红即为「未绘制」。
    internal sealed partial class MainForm
    {
        private sealed class CornerStage : Window
        {
            internal CornerStage()
            {
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;
                ShowInTaskbar = false;
                Left = 10; Top = 10; Width = 1500; Height = 940;
                WindowStartupLocation = WindowStartupLocation.Manual;
                SolidColorBrush magenta = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 0, 255));
                Background = magenta;
                Content = new System.Windows.Controls.Border { Background = magenta };
            }
        }

        internal static void CornerDiagnostic()
        {
            System.Windows.Application app = new System.Windows.Application
            { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
            CornerStage stage = new CornerStage();
            stage.Show();

            MainForm form = new MainForm(false) { Topmost = true };
            form.WindowStartupLocation = WindowStartupLocation.Manual;
            form.Left = 130; form.Top = 130;
            form.Owner = stage;
            form.Show(); form.Activate(); form.LoadProfileForPreview();

            // 四张图：DWM 圆角开 / 关 × 屏幕合成 / WPF 图层。
            // 开 = 当前设置（属性 33 = DWMWCP_ROUND），关 = DWMWCP_DONOTROUND。
            // 两者的差别只可能来自 DWM，因为 WPF 图层是同一份。
            int step = 0;
            DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2600) };
            timer.Tick += delegate
            {
                step++;
                if (step == 1)
                {
                    form.SetCornerRoundForPreview(2);
                    Program.Log("圆角诊断：DWM 圆角开，抓图。");
                }
                else if (step == 2)
                {
                    form.CaptureCornerPair("round");
                    form.SetCornerRoundForPreview(1);
                    Program.Log("圆角诊断：DWM 圆角关，抓图。");
                }
                else
                {
                    timer.Stop();
                    try { form.CaptureCornerPair("noround"); }
                    catch (Exception ex) { Program.Log("圆角诊断失败：" + ex); }
                    stage.Close();
                    form.ShutdownForSession(); form.Close();
                    app.Shutdown();
                }
            };
            timer.Start();
            app.Run();
        }

        // 属性 33：1 = DWMWCP_DONOTROUND，2 = DWMWCP_ROUND，3 = ROUNDSMALL。
        internal void SetCornerRoundForPreview(int mode)
        {
            if (source == null || source.Handle == IntPtr.Zero) return;
            try { DwmSetWindowAttribute(source.Handle, 33, ref mode, 4); }
            catch (Exception ex) { Program.Log("圆角：设置属性 33 失败，" + ex.Message); }
        }

        private void CaptureCornerPair(string tag)
        {
            System.Windows.Point origin = PointToScreen(new System.Windows.Point(0, 0));
            Matrix matrix = PresentationSource.FromVisual(this).CompositionTarget.TransformToDevice;
            int w = (int)Math.Round(ActualWidth * matrix.M11);
            int h = (int)Math.Round(ActualHeight * matrix.M22);

            // 屏幕图向外多取 24px：既要看到窗口外，也要容忍轻微的测量误差。
            int pad = 24;
            Rectangle area = new Rectangle((int)origin.X - pad, (int)origin.Y - pad, w + pad * 2, h + pad * 2);
            using (Bitmap shot = new Bitmap(area.Width, area.Height))
            {
                using (Graphics graphics = Graphics.FromImage(shot))
                    graphics.CopyFromScreen(area.Left, area.Top, 0, 0, area.Size);
                shot.Save(Path.Combine(Program.DataDir, "corner-" + tag + "-screen.png"), ImageFormat.Png);
            }
            SaveRender(this, w, h, matrix, "corner-" + tag + "-wpf.png");

            string[] lines = new[]
            {
                "tag=" + tag,
                "origin=" + (int)origin.X + "," + (int)origin.Y,
                "sizePx=" + w + "x" + h,
                "sizeDip=" + ActualWidth + "x" + ActualHeight,
                "dpi=" + matrix.M11 + "x" + matrix.M22,
                "pad=" + pad,
                "cornerRadiusWpf=24",
                "acrylic=" + BackdropStateForPreview()
            };
            File.WriteAllLines(Path.Combine(Program.DataDir, "corner-" + tag + ".txt"), lines,
                new System.Text.UTF8Encoding(true));
        }

        private static void SaveRender(Visual visual, int w, int h, Matrix matrix, string fileName)
        {
            RenderTargetBitmap bitmap = new RenderTargetBitmap(w, h, 96 * matrix.M11, 96 * matrix.M22, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            PngBitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (FileStream stream = File.Create(Path.Combine(Program.DataDir, fileName))) encoder.Save(stream);
        }

        // 当前 DWM 材质值（38）：1 无 / 2 Mica / 3 Acrylic / 4 MicaAlt。
        internal string BackdropStateForPreview()
        {
            if (source == null || source.Handle == IntPtr.Zero) return "no-hwnd";
            int value = -1;
            try { DwmGetWindowAttribute(source.Handle, 38, out value, 4); } catch { return "error"; }
            return value + " 可用=" + backdropAvailable + " 帧扩展=" + frameExtended;
        }
    }
}

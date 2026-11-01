using System;
using System.Windows.Media.Animation;

namespace VT9Status
{
    // CSS cubic-bezier: solve the horizontal time coordinate before evaluating Y.
    // The three controls deliberately use different motion curves from the reference.
    internal sealed class ReferenceEase : IEasingFunction
    {
        internal static readonly ReferenceEase Card = new ReferenceEase(0.22, 0.9, 0.32, 1);
        internal static readonly ReferenceEase Dock = new ReferenceEase(0.2, 0.9, 0.3, 1);
        internal static readonly ReferenceEase DockBounce = new ReferenceEase(0.2, 1.22, 0.3, 1);
        internal static readonly ReferenceEase Switch = new ReferenceEase(0.34, 1.56, 0.64, 1);
        internal static readonly ReferenceEase Feedback = new ReferenceEase(0.25, 0.1, 0.25, 1);
        private readonly double x1, y1, x2, y2;

        private ReferenceEase(double x1, double y1, double x2, double y2)
        {
            this.x1 = x1; this.y1 = y1; this.x2 = x2; this.y2 = y2;
        }

        private static double Bezier(double t, double a, double b)
        {
            double s = 1 - t;
            return 3 * s * s * t * a + 3 * s * t * t * b + t * t * t;
        }

        public double Ease(double normalizedTime)
        {
            if (normalizedTime <= 0) return 0;
            if (normalizedTime >= 1) return 1;
            double low = 0, high = 1;
            for (int i = 0; i < 28; i++)
            {
                double mid = (low + high) / 2;
                if (Bezier(mid, x1, x2) < normalizedTime) low = mid;
                else high = mid;
            }
            return Bezier((low + high) / 2, y1, y2);
        }
    }

    // 苹果液体玻璃（Liquid Glass）专属缓动：对应 cubic-bezier(0.12, 1.25, 0.32, 1.0)
    // 专为流体指示胶囊流动切换定制：起步平稳迅捷、末段流体碰撞与柔性吸附回弹。
    internal sealed class SlideEase : IEasingFunction
    {
        internal static readonly SlideEase Shared = new SlideEase();

        private const double SettleStart = 0.60;
        private const double Overshoot = 0.048;

        public double Ease(double normalizedTime)
        {
            double t = normalizedTime;
            if (t <= 0) return 0;
            if (t >= 1) return 1;

            double inv = 1.0 - t;
            double main = 1.0 - Math.Pow(inv, 3);
            double settle = t > SettleStart ? Overshoot * Math.Sin(Math.PI * (t - SettleStart) / (1.0 - SettleStart)) * Math.Pow(inv, 0.6) : 0;
            return main + settle;
        }
    }
}

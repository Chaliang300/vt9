using System;

namespace VT9Status
{
    // Frame-rate independent drag follower. It never changes pages or device state.
    internal sealed class DockPhysics
    {
        internal double Maximum = 412;
        internal double Position { get; private set; }
        internal double RawTarget { get; private set; }
        internal double Velocity { get; private set; }
        internal double ScaleY { get; private set; }
        internal double ScaleX { get { return 1 / ScaleY; } }

        internal DockPhysics() { Reset(0, 1); }

        internal void Reset(double position, double scaleY)
        {
            Position = RawTarget = position;
            Velocity = 0;
            ScaleY = scaleY;
        }

        internal void SetTarget(double raw) { RawTarget = raw; }

        internal static double RubberClamp(double raw, double maximum)
        {
            if (raw < 0) return -6 * (1 - Math.Exp(raw / 40));
            if (raw > maximum) return maximum + 6 * (1 - Math.Exp(-(raw - maximum) / 40));
            return raw;
        }

        private double Pressure
        {
            get
            {
                if (RawTarget < 0 && Position < 4) return Math.Min(1, -RawTarget / 60);
                if (RawTarget > Maximum && Position > Maximum - 4)
                    return Math.Min(1, (RawTarget - Maximum) / 60);
                return 0;
            }
        }

        internal bool IsWallPressed { get { return Pressure > 0 && ScaleY < 0.995; } }

        internal void Step(double seconds)
        {
            double dt = Math.Max(0.001, Math.Min(0.05, seconds));
            double before = Position;
            Position += (RubberClamp(RawTarget, Maximum) - Position) * (1 - Math.Exp(-dt / 0.075));
            Velocity += ((Position - before) / dt - Velocity) * (1 - Math.Exp(-dt / 0.045));
            double stretch = Math.Min(0.18, Math.Abs(Velocity) / 2400 * 0.18);
            double pressure = Pressure;
            double shape = pressure > 0 ? 1 - 0.07 * pressure : 1 + stretch;
            ScaleY += (shape - ScaleY) * (1 - Math.Exp(-dt / 0.065));
        }

        internal static double SnapDuration(double distance, bool wall)
        {
            return Math.Max(170, Math.Min(420, 170 + Math.Abs(distance) * 0.55)) + (wall ? 70 : 0);
        }
    }
}

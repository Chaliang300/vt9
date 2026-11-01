using System;

namespace VT9Status
{
    internal static class DockPhysicsTests
    {
        internal static int Run()
        {
            int count = 0;
            Action<bool, string> check = delegate(bool ok, string name)
            {
                if (!ok) throw new Exception("Dock physics: " + name);
                count++;
            };
            check(DockPhysics.RubberClamp(150, 412) == 150, "interior target has no artificial resistance");
            check(DockPhysics.RubberClamp(-100000, 412) >= -6 && DockPhysics.RubberClamp(100000, 412) <= 418,
                "rubber band never exceeds six pixels");
            double near = -DockPhysics.RubberClamp(-10, 412);
            double far = -DockPhysics.RubberClamp(-20, 412);
            check(far > near && far - near < near, "boundary resistance increases with pull");
            DockPhysics fast = new DockPhysics();
            fast.Reset(100, 1); fast.SetTarget(300); fast.Step(1.0 / 60);
            check(fast.Position > 100 && fast.Position < 300, "follower has weight without teleporting");
            for (int i = 0; i < 5; i++) fast.Step(1.0 / 60);
            check(fast.ScaleY > 1 && fast.ScaleX < 1, "speed stretches height and narrows width");
            check(Math.Abs(fast.ScaleX * fast.ScaleY - 1) < 1e-9, "deformation preserves area");
            for (int i = 0; i < 120; i++) fast.Step(1.0 / 60);
            check(Math.Abs(fast.ScaleY - 1) < 0.001 && Math.Abs(fast.Position - 300) < 0.001,
                "stationary pointer restores the capsule");
            DockPhysics sixty = new DockPhysics(), oneTwenty = new DockPhysics();
            sixty.SetTarget(300); oneTwenty.SetTarget(300);
            for (int i = 0; i < 60; i++) sixty.Step(1.0 / 60);
            for (int i = 0; i < 120; i++) oneTwenty.Step(1.0 / 120);
            check(Math.Abs(sixty.Position - oneTwenty.Position) < 1e-6, "follow distance is frame-rate independent");
            DockPhysics wall = new DockPhysics(); wall.Reset(206, 1); wall.SetTarget(800); wall.Step(1.0 / 60);
            check(!wall.IsWallPressed, "a remote pointer is not a wall collision before contact");
            for (int i = 0; i < 90; i++) wall.Step(1.0 / 60);
            check(wall.Position <= 418 && wall.ScaleY < 1 && wall.ScaleX > 1 && wall.IsWallPressed,
                "bottom wall compresses the capsule and marks collision");
            wall.Reset(206, 1); wall.SetTarget(-800);
            for (int i = 0; i < 90; i++) wall.Step(1.0 / 60);
            check(wall.Position >= -6 && wall.ScaleY < 1 && wall.IsWallPressed, "top wall is symmetric");
            check(DockPhysics.SnapDuration(20, false) < DockPhysics.SnapDuration(200, false),
                "farther interior snaps take longer");
            check(DockPhysics.SnapDuration(200, true) > DockPhysics.SnapDuration(200, false),
                "collision has a separate settling phase");
            double previous = 0;
            bool monotone = true, overshoots = false;
            for (int i = 0; i <= 100; i++)
            {
                double t = i / 100.0, smooth = ReferenceEase.Dock.Ease(t);
                monotone &= smooth >= previous && smooth <= 1;
                previous = smooth;
                overshoots |= ReferenceEase.DockBounce.Ease(t) > 1.001;
            }
            check(monotone, "ordinary snap brakes in one direction without overshoot");
            check(overshoots && ReferenceEase.DockBounce.Ease(1) == 1, "wall rebound overshoots gently and settles exactly");
            return count;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Drawing;

namespace VT9Status
{
    internal static class TrayIconTests
    {
        private static ulong Pixels(Bitmap bitmap)
        {
            ulong hash = 14695981039346656037UL;
            unchecked
            {
                for (int y = 0; y < bitmap.Height; y++)
                    for (int x = 0; x < bitmap.Width; x++)
                        hash = (hash ^ (uint)bitmap.GetPixel(x, y).ToArgb()) * 1099511628211UL;
            }
            return hash;
        }

        internal static int Run()
        {
            int count = 0;
            Action<bool, string> check = delegate(bool ok, string name)
            {
                if (!ok) throw new Exception("Tray gauge: " + name);
                count++;
            };
            check(IconArtwork.TraySweep(null) == 0 && IconArtwork.TraySweep(0) == 0, "unknown and empty have no foreground arc");
            check(IconArtwork.TraySweep(50) == 135 && IconArtwork.TraySweep(100) == 270, "arc is proportional to exact percentage");
            check(IconArtwork.TraySweep(-1) == 0 && IconArtwork.TraySweep(101) == 270, "out-of-range values are clamped");
            check(IconArtwork.TrayColor(TrayState.Normal, 19, false).ToArgb() == Color.FromArgb(0xF2,0x68,0x5E).ToArgb(), "dark critical red");
            check(IconArtwork.TrayColor(TrayState.Normal, 19, true).ToArgb() == Color.FromArgb(0xC0,0x39,0x2B).ToArgb(), "light critical red");
            foreach (int level in new[] {20, 35, 40})
            {
                check(IconArtwork.TrayColor(TrayState.Normal, level, false).ToArgb() == Color.FromArgb(0xE8,0xA3,0x3D).ToArgb(), "20..40 inclusive is amber on dark");
                check(IconArtwork.TrayColor(TrayState.Normal, level, true).ToArgb() == Color.FromArgb(0xC7,0x7C,0x1E).ToArgb(), "20..40 inclusive is amber on light");
            }
            check(IconArtwork.TrayColor(TrayState.Normal, 41, false) == Color.White, "above 40 is white on dark");
            check(IconArtwork.TrayColor(TrayState.Normal, 41, true).ToArgb() == Color.FromArgb(0x1C,0x22,0x30).ToArgb(), "above 40 is ink on light");
            check(IconArtwork.TrayColor(TrayState.Charging, 12, false).ToArgb() == Color.FromArgb(0x2F,0xA3,0x6B).ToArgb(), "charging takes green instead of battery band colour");
            check(IconArtwork.ResolveTrayState(false, false, true) == TrayState.Offline, "disconnection overrides retained mode");
            check(IconArtwork.ResolveTrayState(true, true, true) == TrayState.Charging &&
                IconArtwork.ResolveTrayState(true, false, true) == TrayState.Wired &&
                IconArtwork.ResolveTrayState(true, false, false) == TrayState.Normal, "connection and charging select the correct centre glyph");
            HashSet<string> keys = new HashSet<string>();
            HashSet<ulong> frames = new HashSet<ulong>();
            for (int level = 0; level <= 100; level++)
            {
                keys.Add(IconArtwork.TrayKey(16, TrayState.Normal, level, false));
                using (Bitmap icon = IconArtwork.Tray(16, TrayState.Normal, level, false)) frames.Add(Pixels(icon));
            }
            check(keys.Count == 101 && frames.Count == 101, "all 101 percentages produce distinct cache keys and 16px raster frames");
            check(IconArtwork.TrayKey(16, TrayState.Normal, 73, false) != IconArtwork.TrayKey(16, TrayState.Wired, 73, false) &&
                IconArtwork.TrayKey(16, TrayState.Normal, 73, false) != IconArtwork.TrayKey(20, TrayState.Normal, 73, false) &&
                IconArtwork.TrayKey(16, TrayState.Normal, 73, false) != IconArtwork.TrayKey(16, TrayState.Normal, 73, true), "mode, DPI and theme invalidate cache");
            using (Bitmap unknown = IconArtwork.Tray(16, TrayState.Offline, null, false))
            using (Bitmap retained = IconArtwork.Tray(16, TrayState.Offline, 73, false))
            using (Bitmap zero = IconArtwork.Tray(16, TrayState.Normal, 0, false))
                check(Pixels(unknown) == Pixels(retained) && Pixels(unknown) != Pixels(zero), "unknown does not display stale charge; known zero retains the wireless glyph");
            using (Bitmap charging10 = IconArtwork.Tray(24, TrayState.Charging, 10, false))
            using (Bitmap charging60 = IconArtwork.Tray(24, TrayState.Charging, 60, false))
                check(Pixels(charging10) != Pixels(charging60), "charging never substitutes a fake minimum percentage");
            foreach (int size in new[] {16,20,24,32})
            {
                using (Bitmap icon = IconArtwork.Tray(size, TrayState.Normal, 100, false))
                    check(icon.Width == size && icon.Height == size && icon.GetPixel(0,0).A == 0 &&
                        icon.GetPixel(size/2,(int)Math.Round(size*0.84)).A == 0, "native DPI dimensions, transparency and bottom gap");
            }
            return count;
        }
    }
}

using System;
using System.Linq;
namespace VT9Status
{
    internal static class DpiTests
    {
        private sealed class Channel : IDpiChannel
        {
            internal byte[] Active = { 2, 0, 2, 3 };
            internal byte[] Table = { 16, 0, 24, 0, 32, 0, 48, 0, 64, 0, 128, 0, 144, 1, 6, 0 };
            internal int Writes;
            internal bool IgnoreWrite;
            public byte[] Read(uint address, int length) { return (byte[])(address == DpiController.ActiveAddress ? Active : Table).Clone(); }
            public void Write(uint address, byte[] bytes)
            {
                if (address != DpiController.ActiveAddress || bytes.Length != 4) throw new Exception("Unexpected write");
                Writes++; if (!IgnoreWrite) Active = (byte[])bytes.Clone();
            }
        }
        internal static int Run()
        {
            int count = 0;
            Action<bool> check = delegate(bool ok) { if (!ok) throw new Exception("DPI controller test failed"); count++; };
            Channel fake = new Channel(); DpiProfile profile = DpiController.Load(fake);
            check(profile.Current == 1600 && profile.Values.SequenceEqual(new int[] { 800, 1200, 1600, 2400, 3200, 6400, 20000 }) && fake.Writes == 0);
            DpiProfile selected = DpiController.Select(fake, profile, 1);
            check(selected.Current == 1200 && fake.Active.SequenceEqual(new byte[] { 1, 0, 2, 3 }) && fake.Writes == 1);
            DpiController.Select(fake, selected, 1); check(fake.Writes == 1);
            fake.Table[0]++; bool rejected = false;
            try { DpiController.Select(fake, selected, 0); } catch (InvalidOperationException) { rejected = true; }
            check(rejected && fake.Writes == 1);
            fake = new Channel { IgnoreWrite = true }; profile = DpiController.Load(fake); rejected = false;
            try { DpiController.Select(fake, profile, 0); } catch (InvalidOperationException) { rejected = true; }
            check(rejected); rejected = false;
            try { NativeDpiChannel.Frame(0xFF, 0xA5, DpiController.TableAddress, 4, new byte[4]); } catch (ArgumentException) { rejected = true; }
            check(rejected);
            byte[] frame = NativeDpiChannel.Frame(0xFF, 0xA5, DpiController.ActiveAddress, 4, new byte[] { 1, 0, 2, 3 });
            check(frame.Length == 32 && frame.Take(12).SequenceEqual(new byte[] { 0xBA, 0xFF, 0xA5, 4, 0x98, 8, 0, 0, 1, 0, 2, 3 }));
            return count;
        }
    }
}

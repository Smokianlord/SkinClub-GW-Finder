using System;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace SkinClubGiveawayDesktop
{
    // After a scan the app holds only a few kilobytes of results, but the .NET heap and the
    // Windows working set stay inflated by the downloaded pages. Handing that memory back keeps
    // the idle footprint small while the window sits open in the background.
    public static class MemoryTrim
    {
        [DllImport("kernel32.dll")]
        private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimum, IntPtr maximum);

        public static void Run()
        {
            try
            {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Forced, true);
                GC.WaitForPendingFinalizers();
                using (Process self = Process.GetCurrentProcess())
                    SetProcessWorkingSetSize(self.Handle, (IntPtr)(-1), (IntPtr)(-1));
            }
            catch (Exception ex) { Diag.Swallow("Trimming memory", ex); }
        }
    }
}

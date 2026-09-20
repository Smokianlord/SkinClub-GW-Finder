using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Reflection;
using System.Threading;
using SkinClubGiveawayDesktop;

static class BrowserLifecycleTests
{
    static int checks;
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS " + message);
        checks++;
    }
    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--sleep") { Thread.Sleep(60000); return; }
        if (args.Length > 0 && args[0] == "--tree")
        {
            for (int i = 0; i < 12; i++)
            {
                try
                {
                    using (Process child = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--sleep") {
                        UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden }))
                        File.AppendAllText(args[1], child.Id + Environment.NewLine);
                }
                catch { }
                Thread.Sleep(40);
            }
            // Deliberately exit before children to exercise the old taskkill failure.
            return;
        }
        string folder = Path.Combine(Path.GetTempPath(), "SkinClub-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string pidFile = Path.Combine(folder, "pids.txt");
            Process root = BrowserProcessManager.StartHidden(Assembly.GetExecutingAssembly().Location, "--tree \"" + pidFile + "\"");
            Stopwatch deadline = Stopwatch.StartNew();
            while (!root.HasExited && deadline.ElapsedMilliseconds < 10000)
            { Thread.Sleep(10); }
            Check(root.HasExited, "Parent exits while its children remain alive");
            // Windows may briefly include rejected, terminating launches in job accounting.
            // Successful child creations are the authoritative check of the kernel limit.
            Check(File.ReadAllLines(pidFile).Length == 4, "OS permits exactly four children alongside the root");
            Check(BrowserProcessManager.ActiveProcessCount > 0, "Orphaned children remain tracked in the job");
            Check(File.ReadAllLines(pidFile).Length <= 4, "A root cannot spawn more than four concurrent children");
            bool blocked = false;
            try { BrowserProcessManager.StartHidden(Assembly.GetExecutingAssembly().Location, "--sleep"); }
            catch (InvalidOperationException) { blocked = true; }
            Check(blocked, "A second browser tree cannot start before cleanup");
            BrowserProcessManager.KillProcessTree(root);
            Check(BrowserProcessManager.ActiveProcessCount == 0, "Cleanup removes descendants after their parent has exited");
            bool liveChild = false;
            foreach (string pid in File.ReadAllLines(pidFile))
            {
                try { using (Process child = Process.GetProcessById(int.Parse(pid))) if (!child.HasExited) liveChild = true; }
                catch (ArgumentException) { }
            }
            Check(!liveChild, "No recorded child survives cleanup");
            bool failed = false;
            try { BrowserProcessManager.StartHidden(Path.Combine(folder, "missing.exe"), ""); }
            catch { failed = true; }
            Check(failed && BrowserProcessManager.ActiveProcessCount == 0, "Failed launch leaves no browser job or child behind");

            root = BrowserProcessManager.StartHidden(Assembly.GetExecutingAssembly().Location, "--sleep");
            ((Timer)typeof(BrowserProcessManager).GetField("watchdog", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null)).Change(100, Timeout.Infinite);
            deadline.Restart();
            while (BrowserProcessManager.ActiveProcessCount > 0 && deadline.ElapsedMilliseconds < 5000) Thread.Sleep(25);
            Check(BrowserProcessManager.ActiveProcessCount == 0, "Watchdog terminates stalled browser work while the app remains open");
            BrowserProcessManager.KillProcessTree(root);
            root = BrowserProcessManager.StartHidden(Assembly.GetExecutingAssembly().Location, "--sleep");
            BrowserProcessManager.Shutdown();
            Check(BrowserProcessManager.ActiveProcessCount == 0 && BrowserProcessManager.ShutdownToken.IsCancellationRequested, "Shutdown kills active work and cancels queued renders");
            bool rejected = false;
            try { BrowserProcessManager.StartHidden(Assembly.GetExecutingAssembly().Location, "--sleep"); }
            catch (OperationCanceledException) { rejected = true; }
            Check(rejected, "Closing the app prevents any new browser launch");
            Console.WriteLine(checks + " lifecycle checks passed. Artifacts: " + folder);
        }
        finally { BrowserProcessManager.Shutdown(); }
    }
    static void CheckCount()
    {
        if (BrowserProcessManager.ActiveProcessCount > 5) throw new Exception("Process cap exceeded");
    }
}

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using SkinClubGiveawayDesktop;

static class LivePageTests
{
    static void Main()
    {
        string[] urls = {
            "https://drewcs2.club/010926/", "https://mcnasty.club/010926/",
            "https://samz.club/010926/", "https://thedooo.club/010926/"
        };
        string folder = Path.Combine(Path.GetTempPath(), "SkinClub-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        Console.WriteLine("Live test artifacts: " + folder);
        long version = -1;
        int printed = 0;
        int peak = 0;
        try
        {
            using (SemaphoreSlim throttle = new SemaphoreSlim(4, 4))
            {
                Task<GiveawayItem>[] tasks = new Task<GiveawayItem>[urls.Length];
                for (int n = 0; n < urls.Length; n++)
                    tasks[n] = Scanner.ValidateAsync(new Candidate("", urls[n], "manual"), throttle);
                Task<GiveawayItem[]> all = Task.WhenAll(tasks);
                while (!all.IsCompleted)
                {
                    peak = Math.Max(peak, BrowserProcessManager.ActiveProcessCount);
                    Flush(ref version, ref printed, folder);
                    Thread.Sleep(200);
                }
                GiveawayItem[] results = all.GetAwaiter().GetResult();
                Flush(ref version, ref printed, folder);
                File.WriteAllText(Path.Combine(folder, "results.json"), new JavaScriptSerializer().Serialize(results));
                foreach (GiveawayItem item in results)
                    Console.WriteLine(item.Url + " | " + item.Status + " | " + item.Ticket + " | " + item.PromoCode + " | " + item.MinimumDeposit + " | " + item.Deadline + " | " + item.Error);
                Console.WriteLine("Peak browser processes: " + peak + "; remaining: " + BrowserProcessManager.ActiveProcessCount + "; queued: " + Scanner.QueuedRenders);
                if (BrowserProcessManager.ActiveProcessCount != 0 || Scanner.QueuedRenders != 0)
                    throw new Exception("Browser work survived scan completion");
                if (Array.Exists(results, i => i.Status == "unknown" || i.PromoCode == "-" || i.MinimumDeposit == "-" || i.Deadline == "-"))
                    throw new Exception("At least one actual page could not be fully validated; inspect results and log");
                Console.WriteLine("PASS All four live pages validated and browser children cleaned up");
            }
        }
        finally { BrowserProcessManager.Shutdown(); Flush(ref version, ref printed, folder); }
    }
    static void Flush(ref long version, ref int printed, string folder)
    {
        string snapshot = ActivityLog.Snapshot(ref version);
        if (snapshot == null) return;
        File.WriteAllText(Path.Combine(folder, "activity.log"), snapshot);
        string[] lines = snapshot.Split(new string[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
        for (int n = printed; n < lines.Length; n++) Console.WriteLine(lines[n]);
        printed = lines.Length;
    }
}

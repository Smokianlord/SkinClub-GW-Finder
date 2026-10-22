using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace SkinClubGiveawayDesktop
{
    public static partial class Scanner
    {
        private static string BuildProbeUrl(string host, string slug)
        {
            return "https://" + host + "/" + slug + "/";
        }

        private static IEnumerable<string> RecentDateSlugs(int daysBack)
        {
            DateTime today = DateTime.Now.Date;
            for (int offset = 0; offset <= daysBack; offset++)
            {
                DateTime d = today.AddDays(-offset);
                yield return string.Format("{0:00}{1:00}{2:00}", d.Day, d.Month, d.Year % 100);
            }
        }

        private static List<string> FastProbeSlugs()
        {
            // Brute-forcing every creator x every day for two months produced
            // thousands of HTTP requests and could make Deep Search take several
            // minutes. Social/YouTube discovery is the primary discovery path;
            // these probes are only a safety net. Keep the recent week plus the
            // 1st/2nd of the current and previous two months (the common campaign
            // slugs, including older examples such as 010826 and 020826).
            HashSet<string> slugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string slug in RecentDateSlugs(8)) slugs.Add(slug);

            DateTime today = DateTime.Now.Date;
            for (int monthOffset = 0; monthOffset <= 2; monthOffset++)
            {
                DateTime month = new DateTime(today.AddMonths(-monthOffset).Year, today.AddMonths(-monthOffset).Month, 1);
                for (int day = 1; day <= 2; day++)
                {
                    DateTime d = new DateTime(month.Year, month.Month, day);
                    slugs.Add(string.Format("{0:00}{1:00}{2:00}", d.Day, d.Month, d.Year % 100));
                }
            }
            return slugs.ToList();
        }

        private static List<Candidate> GeneratedCandidates()
        {
            List<Candidate> list = new List<Candidate>();
            List<string> slugs = FastProbeSlugs();
            foreach (KeyValuePair<string, string> kv in CreatorDomains)
            {
                foreach (string slug in slugs)
                    list.Add(new Candidate(kv.Key, BuildProbeUrl(kv.Value, slug), "domain-probe"));
            }
            return list;
        }

        private static void AddDiscoveredDomainProbes(List<Candidate> candidates)
        {
            HashSet<string> hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Candidate c in candidates.ToArray())
            {
                try
                {
                    Uri u = new Uri(c.Url);
                    string host = u.Host.ToLowerInvariant();
                    if (host.StartsWith("www.")) host = host.Substring(4);
                    if (host.EndsWith(".club", StringComparison.OrdinalIgnoreCase)) hosts.Add(host);
                }
                catch (Exception swallowed) { Diag.Swallow("Skipping malformed discovered URL", swallowed); }
            }

            foreach (string host in hosts)
            {
                string creator = InferCreator("https://" + host + "/", null);
                foreach (string slug in FastProbeSlugs())
                    candidates.Add(new Candidate(creator, BuildProbeUrl(host, slug), "discovered-domain-probe"));
            }
        }

        // Discovery sources that could not be read (after retries) during the current Deep Search.
        private static int discoveryFailures;
        public static int LastDiscoveryFailures { get { return Volatile.Read(ref discoveryFailures); } }
        // Pages that still could not be reached after every retry in the last Deep Search.
        public static int LastUnreachable { get; private set; }

        private static async Task<string> GetStringSafeAsync(string url)
        {
            try { return await GetStringCoreAsync(url); }
            finally { Interlocked.Increment(ref sourcesRead); }
        }

        private static async Task<string> GetStringCoreAsync(string url)
        {
            ActivityLog.Write("SOURCE", "Fetching " + url);
            try
            {
                PageFetch fetched = await FetchPageAsync(url, MaxDiscoveryBytes, false);
                if (fetched.Status < 200 || fetched.Status > 299)
                {
                    ActivityLog.Write("ERROR", "HTTP " + fetched.Status + " fetching " + url);
                    if (fetched.Status == 429 || fetched.Status >= 500) Interlocked.Increment(ref discoveryFailures);
                    return "";
                }
                string content = fetched.Body ?? "";
                ActivityLog.Write("SOURCE", "Received " + content.Length + " characters from " + url);
                return content;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref discoveryFailures);
                ActivityLog.Write("ERROR", "Source failed after retries: " + url + " — " + ex.Message);
                return "";
            }
        }
        private static async Task<List<Candidate>> DiscoverTelegramAsync()
        {
            List<Candidate> found = new List<Candidate>();
            string[] channels = new string[] { "skinclubcreatorsgiveaway", "skinclubcs2" };
            foreach (string channel in channels)
            {
                string raw = await GetStringSafeAsync("https://t.me/s/" + channel);
                found.AddRange(ExtractDirectUrls(raw, "telegram:" + channel));
            }
            return found;
        }

        private static async Task<List<Candidate>> DiscoverYoutubeAsync()
        {
            List<Candidate> found = new List<Candidate>();
            HashSet<string> videoIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Broad searches catch newly emerging creators. Partner-specific
            // searches stop a creator such as DrewCS2 from being missed simply
            // because their video does not rank for the generic query.
            string[] genericQueries = new string[]
            {
                "skinclub giveaway", "skinclub cs2 giveaway", "skinclub partner giveaway", "skinclub .club giveaway"
            };

            foreach (string q in genericQueries)
            {
                string raw = await GetStringSafeAsync("https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(q));
                found.AddRange(ExtractDirectUrls(raw, "youtube-search"));
                int added = 0;
                foreach (Match m in VideoIdRegex.Matches(raw))
                {
                    if (videoIds.Count >= 120 || added >= 12) break;
                    if (videoIds.Add(m.Groups[1].Value)) added++;
                }
            }

            object youtubeGate = new object();
            SemaphoreSlim partnerSem = new SemaphoreSlim(4, 4);
            List<Task> partnerTasks = new List<Task>();
            foreach (string partnerName in PartnerSearchNames())
            {
                string partner = partnerName;
                partnerTasks.Add(Task.Run(async delegate
                {
                    await partnerSem.WaitAsync();
                    try
                    {
                        string q = partner + " skinclub";
                        string raw = await GetStringSafeAsync("https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(q));
                        List<Candidate> local = ExtractDirectUrls(raw, "youtube-partner:" + partner);
                        List<string> localIds = new List<string>();
                        foreach (Match m in VideoIdRegex.Matches(raw))
                        {
                            if (localIds.Count >= 2) break;
                            localIds.Add(m.Groups[1].Value);
                        }
                        lock (youtubeGate)
                        {
                            found.AddRange(local);
                            foreach (string id in localIds)
                            {
                                if (videoIds.Count >= 120) break;
                                videoIds.Add(id);
                            }
                        }
                    }
                    finally { partnerSem.Release(); }
                }));
            }
            if (partnerTasks.Count > 0) await Task.WhenAll(partnerTasks);

            SemaphoreSlim sem = new SemaphoreSlim(6, 6);
            List<Task<List<Candidate>>> tasks = new List<Task<List<Candidate>>>();
            foreach (string id in videoIds.Take(120))
            {
                tasks.Add(Task.Run(async delegate
                {
                    await sem.WaitAsync();
                    try
                    {
                        string raw = await GetStringSafeAsync("https://www.youtube.com/watch?v=" + id);
                        return ExtractDirectUrls(raw, "youtube-description");
                    }
                    finally { sem.Release(); }
                }));
            }
            if (tasks.Count > 0)
            {
                List<Candidate>[] batches = await Task.WhenAll(tasks);
                foreach (List<Candidate> batch in batches) found.AddRange(batch);
            }
            return found;
        }

        private static async Task<List<Candidate>> DiscoverPartnerSocialsAsync()
        {
            List<Candidate> found = new List<Candidate>();
            HashSet<string> telegramPages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            object gate = new object();
            SemaphoreSlim searchSem = new SemaphoreSlim(4, 4);
            List<Task> searchTasks = new List<Task>();

            foreach (string partnerName in PartnerSearchNames())
            {
                string partner = partnerName;
                searchTasks.Add(Task.Run(async delegate
                {
                    await searchSem.WaitAsync();
                    try
                    {
                        string q = "\"" + partner + "\" SkinClub giveaway (YouTube OR X OR Twitter OR Telegram OR Discord OR Instagram OR TikTok OR Twitch OR Kick OR Facebook)";
                        string bing = "https://www.bing.com/search?q=" + Uri.EscapeDataString(q) + "&count=20";
                        string raw = await GetStringSafeAsync(bing);
                        List<Candidate> local = ExtractDirectUrls(raw, "partner-social-search:" + partner);
                        List<string> socials = ExtractSocialUrls(raw);
                        lock (gate)
                        {
                            found.AddRange(local);
                            foreach (string socialUrl in socials)
                            {
                                if (telegramPages.Count >= 60) break;
                                Uri u;
                                if (!Uri.TryCreate(socialUrl, UriKind.Absolute, out u)) continue;
                                if (!u.Host.Equals("t.me", StringComparison.OrdinalIgnoreCase)) continue;
                                if (u.AbsolutePath.StartsWith("/+", StringComparison.Ordinal)) continue;
                                telegramPages.Add(socialUrl);
                            }
                        }
                    }
                    finally { searchSem.Release(); }
                }));
            }
            if (searchTasks.Count > 0) await Task.WhenAll(searchTasks);

            SemaphoreSlim sem = new SemaphoreSlim(4, 4);
            List<Task<List<Candidate>>> tasks = new List<Task<List<Candidate>>>();
            foreach (string page in telegramPages.Take(60))
            {
                tasks.Add(Task.Run(async delegate
                {
                    await sem.WaitAsync();
                    try
                    {
                        string raw = await GetStringSafeAsync(page);
                        return ExtractDirectUrls(raw, "partner-telegram");
                    }
                    finally { sem.Release(); }
                }));
            }
            if (tasks.Count > 0)
            {
                List<Candidate>[] batches = await Task.WhenAll(tasks);
                foreach (List<Candidate> batch in batches) found.AddRange(batch);
            }

            return found;
        }

        private static async Task<List<Candidate>> DiscoverWebAsync()
        {
            List<Candidate> found = new List<Candidate>();
            List<string> queries = new List<string>();
            queries.Add("SkinClub giveaway .club Tickets left");
            queries.Add("SkinClub Time to completion .club");
            queries.Add("SkinClub partner giveaway .club");
            queries.Add("site:x.com/skinclubmedia SkinClub giveaway .club");
            queries.Add("site:x.com/skinclubpartner SkinClub giveaway .club");
            queries.Add("site:t.me/skinclubcs2 giveaway .club");

            foreach (string slug in FastProbeSlugs())
                queries.Add("\"" + slug + "\" SkinClub giveaway .club");

            object gate = new object();
            SemaphoreSlim sem = new SemaphoreSlim(4, 4);
            List<Task> tasks = new List<Task>();
            foreach (string query in queries)
            {
                string q = query;
                tasks.Add(Task.Run(async delegate
                {
                    await sem.WaitAsync();
                    try
                    {
                        string bing = "https://www.bing.com/search?q=" + Uri.EscapeDataString(q) + "&count=50";
                        string raw = await GetStringSafeAsync(bing);
                        List<Candidate> local = ExtractDirectUrls(raw, "web-search");
                        lock (gate) found.AddRange(local);
                    }
                    finally { sem.Release(); }
                }));
            }
            if (tasks.Count > 0) await Task.WhenAll(tasks);
            return found;
        }

        public static async Task<AppData> DeepScanAsync(AppData data)
        {
            await ScanLock.WaitAsync();
            try
            {
                phase = "Deep Search: reading sources and checking pages";
                Volatile.Write(ref sourcesRead, 0);
                ActivityLog.Write("SCAN", "Deep Search started: Telegram, YouTube, partner socials and web search");
                HashSet<string> known = new HashSet<string>(data.Items.Select(delegate(GiveawayItem x) { return GiveawayKey(x.Url); }), StringComparer.OrdinalIgnoreCase);
                KnownItems = IndexKnown(data);
                ResetChecks(0);
                Volatile.Write(ref discoveryFailures, 0);
                LastUnreachable = 0;

                // Page checks start immediately and overlap with the slower social/search
                // discovery, instead of waiting for every source to finish first. Pages found
                // later are queued as they arrive; a URL is only ever checked once.
                HashSet<string> queued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                List<Task<GiveawayItem>> validations = new List<Task<GiveawayItem>>();
                SemaphoreSlim throttle = new SemaphoreSlim(AppSettings.Current.ProbeConcurrency, AppSettings.Current.ProbeConcurrency);
                Action<IEnumerable<Candidate>> enqueue = delegate(IEnumerable<Candidate> items)
                {
                    foreach (Candidate c in items)
                    {
                        if (string.IsNullOrWhiteSpace(c.Url)) continue;
                        string key = GiveawayKey(c.Url);
                        if (key.Length == 0 || !queued.Add(key)) continue;
                        Interlocked.Increment(ref checksTotal);
                        validations.Add(ValidateAsync(new Candidate(c.Creator, CleanDiscoveredUrl(c.Url), c.Source), throttle));
                    }
                };

                List<Candidate> saved = new List<Candidate>();
                foreach (GiveawayItem i in data.Items)
                    saved.Add(new Candidate(i.Creator, i.Url, string.IsNullOrWhiteSpace(i.Source) ? "saved" : i.Source));

                Task<List<Candidate>> telegram = DiscoverTelegramAsync();
                Task<List<Candidate>> youtube = DiscoverYoutubeAsync();
                Task<List<Candidate>> partnerSocials = DiscoverPartnerSocialsAsync();
                Task<List<Candidate>> web = DiscoverWebAsync();

                enqueue(saved);
                enqueue(GeneratedCandidates());

                List<Candidate> discovered = new List<Candidate>();
                discovered.AddRange(await telegram);
                discovered.AddRange(await youtube);
                discovered.AddRange(await partnerSocials);
                discovered.AddRange(await web);
                discovered.AddRange(saved);

                // Any .club domain discovered from search, Telegram, YouTube, or an
                // existing saved item becomes probeable automatically. This prevents
                // Deep Search from depending only on the hard-coded creator list.
                AddDiscoveredDomainProbes(discovered);
                enqueue(discovered);
                phase = "Deep Search: finishing page checks";
                ActivityLog.Write("SCAN", "Discovery complete; checking " + validations.Count + " unique candidate pages");

                List<GiveawayItem> useful = new List<GiveawayItem>();
                GiveawayItem[] updates = await Task.WhenAll(validations);

                // A page that could not be read at all is not "no giveaway here". Check those again,
                // slowly and a few at a time, before accepting that they are unreachable.
                for (int round = 1; round <= 3; round++)
                {
                    List<int> failed = new List<int>();
                    for (int i = 0; i < updates.Length; i++) if (updates[i].Unreachable) failed.Add(i);
                    if (failed.Count == 0) break;
                    phase = "Deep Search: re-checking " + failed.Count + " unreachable pages (round " + round + " of 3)";
                    ActivityLog.Write("SCAN", failed.Count + " pages could not be read; checking them again (round " + round + " of 3)");
                    Interlocked.Add(ref checksTotal, failed.Count);
                    await Task.Delay(2000 * round);
                    SemaphoreSlim slow = new SemaphoreSlim(4, 4);
                    List<Task<GiveawayItem>> again = new List<Task<GiveawayItem>>();
                    foreach (int index in failed)
                        again.Add(ValidateAsync(new Candidate(updates[index].Creator, updates[index].Url, updates[index].Source), slow));
                    GiveawayItem[] redone = await Task.WhenAll(again);
                    for (int k = 0; k < failed.Count; k++) updates[failed[k]] = redone[k];
                }
                LastUnreachable = updates.Count(delegate(GiveawayItem u) { return u.Unreachable; });
                foreach (GiveawayItem u in updates.Where(delegate(GiveawayItem u) { return u.Unreachable; }))
                    ActivityLog.Write("WARN", "Could not reach after retries: " + u.Url + " (" + u.Error + ")");
                foreach (GiveawayItem u in updates)
                {
                    string key = GiveawayKey(u.Url);
                    if (u.Status == "active" || u.Status == "ended" || known.Contains(key)) useful.Add(u);
                }
                MergeItems(data, useful);
                data.LastScan = DateTime.UtcNow.ToString("o");
                data.LastDeepScan = data.LastScan;
                DataStore.Save(data);
                ActivityLog.Write("SCAN", "Deep Search complete; " + useful.Count + " useful results saved");
                return data;
            }
            finally { KnownItems = null; phase = "Idle"; ScanLock.Release(); }
        }

        public static async Task<GiveawayItem> ValidateOneAsync(string creator, string url)
        {
            if (!DirectUrlRegex.IsMatch(url)) throw new ArgumentException("Use a direct dated creator giveaway URL, for example https://creator.club/010926/");
            SemaphoreSlim throttle = new SemaphoreSlim(1, 1);
            ResetChecks(1);
            phase = "Checking the added link";
            try { return await ValidateAsync(new Candidate(creator, url, "manual"), throttle); }
            finally { phase = "Idle"; }
        }

        public static void MergeOne(AppData data, GiveawayItem item)
        {
            MergeItems(data, new GiveawayItem[] { item });
            DataStore.Save(data);
        }
    }
}

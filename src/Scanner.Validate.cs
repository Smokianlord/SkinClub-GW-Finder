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
        public static async Task<GiveawayItem> ValidateAsync(Candidate c, SemaphoreSlim throttle)
        {
            await throttle.WaitAsync();
            Interlocked.Increment(ref activeChecks);
            try
            {
                ActivityLog.Write("CHECK", c.Url);
                GiveawayItem item = new GiveawayItem
                {
                    Creator = InferCreator(c.Url, c.Creator), Url = CleanDiscoveredUrl(c.Url), Status = "unknown", Ticket = "-", PromoCode = "-", MinimumDeposit = "-", Deadline = "-",
                    LastChecked = DateTime.UtcNow.ToString("o"), Source = c.Source, Error = null
                };
                try
                {
                    string html;
                    // Headers first: a 404 or a redirect to a homepage is rejected without
                    // downloading its body, and accepted pages are read with a size cap.
                    using (HttpResponseMessage r = await Client.GetAsync(c.Url, HttpCompletionOption.ResponseHeadersRead))
                    {
                    if (!r.IsSuccessStatusCode)
                    {
                        item.Error = "HTTP " + (int)r.StatusCode;
                        return item;
                    }
                    // Keep the requested giveaway identity on failures and redirects.
                    // A redirect to a homepage must not orphan the saved dated entry.
                    if (!string.Equals(GiveawayKey(c.Url), GiveawayKey(r.RequestMessage.RequestUri.ToString()), StringComparison.OrdinalIgnoreCase))
                    {
                        item.Error = "Page redirected to a different giveaway or homepage";
                        return item;
                    }
                    html = await ReadBodyCappedAsync(r, MaxPageBytes);
                    }
                    ParseResult p = ParsePage(html);
                    item.Status = p.Status;
                    item.Ticket = p.Ticket;
                    // Only a code ending with the URL's DDMMYY slug is the entry code. Ended pages show
                    // an unrelated code instead; taking it would overwrite the real, saved one.
                    item.PromoCode = GiveawayInfo.EntryCodeOrDash(p.PromoCode, item.Url);
                    item.MinimumDeposit = (p.PromoCode != "-" && item.PromoCode == "-") ? "-" : p.MinimumDeposit;
                    item.Deadline = p.Deadline;

                    // Current SkinClub creator pages can inject the hour countdown only
                    // after JavaScript runs. If the direct fetch has a live giveaway but
                    // no deadline, use the installed Edge/Chrome engine to render the DOM
                    // and convert the resulting remaining-hours value to a calendar date.
                    bool activePage = string.Equals(item.Status, "active", StringComparison.OrdinalIgnoreCase);

                    // Re-use metadata we already resolved on an earlier pass. Launching a
                    // headless browser for every saved page on every refresh was the main
                    // reason refreshes took minutes; the deadline/promo/deposit of a running
                    // giveaway do not change once known.
                    bool recentlyRendered = false;
                    GiveawayItem prior = null;
                    Dictionary<string, GiveawayItem> priorMap = KnownItems;
                    if (priorMap != null) priorMap.TryGetValue(GiveawayKey(c.Url), out prior);
                    if (prior != null)
                    {
                        item.RenderedAt = prior.RenderedAt;
                        DateTime renderedAt;
                        recentlyRendered = DateTime.TryParse(prior.RenderedAt, System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.RoundtripKind, out renderedAt) &&
                            DateTime.UtcNow - renderedAt.ToUniversalTime() < MetadataRetryInterval;
                        if (activePage)
                        {
                            DateTime knownDeadline;
                            if (item.Deadline == "-" && !string.IsNullOrWhiteSpace(prior.Deadline) && prior.Deadline != "-" &&
                                DateTime.TryParse(prior.Deadline, out knownDeadline) && knownDeadline.Date >= DateTime.Now.Date)
                                item.Deadline = prior.Deadline;
                        }
                        if (item.PromoCode == "-" && GiveawayInfo.IsEntryCodeFor(prior.PromoCode, item.Url))
                            item.PromoCode = prior.PromoCode;
                        if (item.MinimumDeposit == "-" && !string.IsNullOrWhiteSpace(prior.MinimumDeposit) && prior.MinimumDeposit != "-")
                            item.MinimumDeposit = prior.MinimumDeposit;
                    }

                    bool missingMetadata = (item.PromoCode == "-" || item.MinimumDeposit == "-") && !recentlyRendered;
                    bool needsRenderedDeadline = activePage && item.Deadline == "-" && !recentlyRendered;

                    // Deep Search generates some guessed date URLs as a safety net. A guessed URL
                    // that merely returns HTTP 200 must not launch a full Chromium/CDP session.
                    // That regression turned a ~1 minute scan into a multi-minute scan. Only use
                    // the expensive rendered fallback when the static response actually looks like
                    // a giveaway, or when the URL came from a real saved/social/manual discovery.
                    bool generatedProbe = string.Equals(c.Source, "domain-probe", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(c.Source, "discovered-domain-probe", StringComparison.OrdinalIgnoreCase);
                    bool staticGiveawaySignal = !string.Equals(item.Status, "unknown", StringComparison.OrdinalIgnoreCase) ||
                        Regex.IsMatch(html, @"tickets?\s*(?:left|remaining)[^0-9]{0,40}[0-9]+\s*/\s*[0-9]+|time\s*to\s*completion", RegexOptions.IgnoreCase);
                    // An ended page no longer shows the entry code or deposit, so rendering it in a
                    // browser can only return unrelated values; keep what was saved while it was live.
                    bool endedPage = string.Equals(item.Status, "ended", StringComparison.OrdinalIgnoreCase);
                    bool allowRenderedMetadata = missingMetadata && !endedPage && (!generatedProbe || staticGiveawaySignal);

                    // Promo/minimum-deposit values may also be client-rendered on already-ended
                    // giveaways. Saved/social links can still use the rendered metadata path.
                    if (allowRenderedMetadata || needsRenderedDeadline)
                    {
                        RenderedFields rendered = await FetchRenderedFieldsAsync(item.Url);
                        item.RenderedAt = DateTime.UtcNow.ToString("o");
                        string renderedCode = GiveawayInfo.EntryCodeOrDash(rendered.PromoCode, item.Url);
                        if (renderedCode != "-")
                        {
                            item.PromoCode = renderedCode;
                            if (rendered.MinimumDeposit != "-") item.MinimumDeposit = rendered.MinimumDeposit;
                        }

                        // Only let rendered timer data alter the deadline/status for a page that
                        // was parsed as active. For ended/unknown pages we are here solely to
                        // backfill metadata, avoiding the old deadline reclassification problem.
                        if (activePage && rendered.Deadline != "-")
                        {
                            item.Deadline = rendered.Deadline;
                            if (string.Equals(rendered.Deadline, "Ended", StringComparison.OrdinalIgnoreCase))
                                item.Status = "ended";
                        }
                    }
                    return item;
                }
                catch (Exception ex)
                {
                    item.Error = ex.Message.Length > 160 ? ex.Message.Substring(0, 160) : ex.Message;
                    return item;
                }
                finally { ActivityLog.Write(item.Error == null ? "RESULT" : "ERROR", c.Url + " — " + item.Status + (item.Error == null ? "" : ": " + item.Error)); }
            }
            finally
            {
                Interlocked.Decrement(ref activeChecks);
                Interlocked.Increment(ref checksDone);
                throttle.Release();
            }
        }

        private static void MergeItems(AppData data, IEnumerable<GiveawayItem> updates)
        {
            Dictionary<string, GiveawayItem> byUrl = new Dictionary<string, GiveawayItem>(StringComparer.OrdinalIgnoreCase);
            foreach (GiveawayItem i in data.Items)
            {
                if (i.Url != null) byUrl[GiveawayKey(i.Url)] = i;
            }
            foreach (GiveawayItem u in updates)
            {
                string key = GiveawayKey(u.Url);
                GiveawayItem existing;
                if (byUrl.TryGetValue(key, out existing))
                {
                    // A failed/ambiguous check must not move an already-known giveaway
                    // out of Active or History. Only a successful active/ended parse may
                    // change its classification.
                    if (u.Status == "unknown" &&
                        (string.Equals(existing.Status, "active", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(existing.Status, "ended", StringComparison.OrdinalIgnoreCase)))
                    {
                        // Keep the known classification, but do not discard metadata that the
                        // rendered-page fallback successfully recovered. This is especially
                        // important for Joined items saved before promocode/deposit support.
                        if (!string.IsNullOrWhiteSpace(u.PromoCode) && u.PromoCode != "-")
                            existing.PromoCode = u.PromoCode;
                        if (!string.IsNullOrWhiteSpace(u.MinimumDeposit) && u.MinimumDeposit != "-")
                            existing.MinimumDeposit = u.MinimumDeposit;
                        if (!string.IsNullOrWhiteSpace(u.Ticket) && u.Ticket != "-")
                            existing.Ticket = u.Ticket;

                        existing.LastChecked = u.LastChecked;
                        existing.Error = u.Error;
                        if (!string.IsNullOrWhiteSpace(u.RenderedAt)) existing.RenderedAt = u.RenderedAt;
                        if (!string.IsNullOrWhiteSpace(u.Source)) existing.Source = u.Source;
                    }
                    else
                    {
                        bool wasEnded = string.Equals(existing.Status, "ended", StringComparison.OrdinalIgnoreCase);
                        bool isEnded = string.Equals(u.Status, "ended", StringComparison.OrdinalIgnoreCase);

                        existing.Creator = u.Creator;
                        existing.Url = CleanDiscoveredUrl(u.Url);
                        existing.Status = u.Status;
                        existing.Ticket = u.Ticket;
                        if (!string.IsNullOrWhiteSpace(u.PromoCode) && u.PromoCode != "-") existing.PromoCode = u.PromoCode;
                        else if (string.IsNullOrWhiteSpace(existing.PromoCode)) existing.PromoCode = "-";
                        if (!string.IsNullOrWhiteSpace(u.MinimumDeposit) && u.MinimumDeposit != "-") existing.MinimumDeposit = u.MinimumDeposit;
                        else if (string.IsNullOrWhiteSpace(existing.MinimumDeposit)) existing.MinimumDeposit = "-";
                        existing.Deadline = u.Deadline; existing.LastChecked = u.LastChecked;
                        existing.Source = u.Source; existing.Error = u.Error;
                        if (!string.IsNullOrWhiteSpace(u.RenderedAt)) existing.RenderedAt = u.RenderedAt;

                        if (isEnded)
                        {
                            if (!wasEnded || string.IsNullOrWhiteSpace(existing.HistorySince))
                                existing.HistorySince = DateTime.UtcNow.ToString("o");
                        }
                        else
                        {
                            existing.HistorySince = null;
                        }
                    }
                }
                else
                {
                    u.Url = CleanDiscoveredUrl(u.Url);
                    if (string.Equals(u.Status, "ended", StringComparison.OrdinalIgnoreCase))
                        u.HistorySince = DateTime.UtcNow.ToString("o");
                    data.Items.Add(u);
                    byUrl[key] = u;
                }
            }
        }

        public static async Task<AppData> RefreshSavedAsync(AppData data)
        {
            await ScanLock.WaitAsync();
            try
            {
                ActivityLog.Write("SCAN", "Refreshing saved giveaways");
                List<Candidate> cs = new List<Candidate>();
                foreach (GiveawayItem i in data.Items)
                {
                    // Ended giveaways are already saved in History. Re-checking
                    // them on every app launch can reclassify or delay History
                    // unnecessarily. Keep them persisted until normal retention
                    // cleanup removes them. Active/unknown items still refresh.
                    if (string.Equals(i.Status, "ended", StringComparison.OrdinalIgnoreCase))
                    {
                        // An ended page no longer shows its entry code or deposit, so there is
                        // nothing left to backfill: keep what was saved while it was live.
                        continue;
                    }
                    cs.Add(new Candidate(i.Creator, i.Url, string.IsNullOrWhiteSpace(i.Source) ? "saved" : i.Source));
                }
                KnownItems = IndexKnown(data);
                SemaphoreSlim throttle = new SemaphoreSlim(AppSettings.Current.ProbeConcurrency, AppSettings.Current.ProbeConcurrency);
                List<Task<GiveawayItem>> tasks = new List<Task<GiveawayItem>>();
                ResetChecks(cs.Count);
                foreach (Candidate c in cs) tasks.Add(ValidateAsync(c, throttle));
                GiveawayItem[] updates = tasks.Count == 0 ? new GiveawayItem[0] : await Task.WhenAll(tasks);
                MergeItems(data, updates);
                data.LastScan = DateTime.UtcNow.ToString("o");
                DataStore.Save(data);
                ActivityLog.Write("SCAN", "Refresh complete; " + updates.Length + " pages checked");
                return data;
            }
            finally { KnownItems = null; ScanLock.Release(); }
        }

    }
}

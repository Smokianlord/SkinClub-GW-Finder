using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace SkinClubGiveawayDesktop
{
    // Facts that can be derived from a giveaway's URL and saved fields.
    //
    // Creator pages end with a DDMMYY slug (https://kryoz.club/010926/) and the real entry code
    // always ends with the same slug (LP-KRYOZ-010926). Once a giveaway has ended its page only
    // shows an unrelated code (for example LP-XTVFAGX7QP9W), so a code that does not end with the
    // URL's slug must never replace or masquerade as the entry code.
    public static class GiveawayInfo
    {
        private static readonly Regex UrlSlugRegex = new Regex(@"/(\d{6})(?:/|$|\?)", RegexOptions.Compiled);
        private static readonly Regex EntryCodeRegex = new Regex(@"^LP-(.+)-(\d{6})$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string UrlSlug(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            Match m = UrlSlugRegex.Match(url);
            return m.Success ? m.Groups[1].Value : null;
        }

        public static bool HasCode(string code)
        {
            return !string.IsNullOrWhiteSpace(code) && code.Trim() != "-";
        }

        // True when the code can be the entry code of the giveaway at this URL.
        public static bool IsEntryCodeFor(string code, string url)
        {
            if (!HasCode(code)) return false;
            string slug = UrlSlug(url);
            return slug == null || code.Trim().EndsWith("-" + slug, StringComparison.OrdinalIgnoreCase);
        }

        public static string EntryCodeOrDash(string code, string url)
        {
            return IsEntryCodeFor(code, url) ? code : "-";
        }

        // DDMMYY slug as a date; DateTime.MinValue when the URL has none.
        public static DateTime GiveawayDate(string url)
        {
            string slug = UrlSlug(url);
            DateTime date;
            if (slug != null && DateTime.TryParseExact(slug, "ddMMyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                return date;
            return DateTime.MinValue;
        }

        public static string HostKey(string url)
        {
            Uri u;
            if (!Uri.TryCreate(url ?? "", UriKind.Absolute, out u)) return (url ?? "").ToLowerInvariant();
            string host = u.Host.ToLowerInvariant();
            return host.StartsWith("www.") ? host.Substring(4) : host;
        }

        public static DateTime Recency(GiveawayItem item)
        {
            DateTime timestamp;
            if (DateTime.TryParse(item.HistorySince, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out timestamp))
                return timestamp.ToUniversalTime();
            if (DateTime.TryParse(item.LastChecked, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out timestamp))
                return timestamp.ToUniversalTime();
            return DateTime.MinValue;
        }

        // The newest giveaway of each creator: highest dated slug, then most recently seen.
        public static List<GiveawayItem> LatestPerCreator(IEnumerable<GiveawayItem> items)
        {
            Dictionary<string, GiveawayItem> best = new Dictionary<string, GiveawayItem>(StringComparer.OrdinalIgnoreCase);
            foreach (GiveawayItem item in items)
            {
                string key = HostKey(item.Url);
                GiveawayItem current;
                if (!best.TryGetValue(key, out current) || IsNewer(item, current)) best[key] = item;
            }
            return best.Values.ToList();
        }

        private static bool IsNewer(GiveawayItem a, GiveawayItem b)
        {
            int byDate = GiveawayDate(a.Url).CompareTo(GiveawayDate(b.Url));
            if (byDate != 0) return byDate > 0;
            return Recency(a) > Recency(b);
        }

        // Fixes saved entries whose promocode was overwritten by an ended page's unrelated code.
        // Where the creator's code pattern is known from another giveaway (LP-<NAME>-<slug>), the
        // missing entry code of an ended giveaway is rebuilt from it. Returns true if anything changed.
        public static bool RepairEntryCodes(AppData data)
        {
            bool changed = false;
            Dictionary<string, string> tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (GiveawayItem item in data.Items)
            {
                if (item == null || !IsEntryCodeFor(item.PromoCode, item.Url) || UrlSlug(item.Url) == null) continue;
                Match m = EntryCodeRegex.Match(item.PromoCode.Trim());
                if (m.Success) tokens[HostKey(item.Url)] = m.Groups[1].Value;
            }

            foreach (GiveawayItem item in data.Items)
            {
                if (item == null) continue;
                string slug = UrlSlug(item.Url);
                if (slug == null) continue;

                if (HasCode(item.PromoCode) && !IsEntryCodeFor(item.PromoCode, item.Url))
                {
                    Match dated = Regex.Match(item.PromoCode.Trim(), @"-(\d{6})$");
                    item.PromoCode = "-";
                    // A code carrying another giveaway's date means the whole record was mixed up.
                    if (dated.Success) item.MinimumDeposit = "-";
                    changed = true;
                }

                string token;
                if (!HasCode(item.PromoCode) && string.Equals(item.Status, "ended", StringComparison.OrdinalIgnoreCase) &&
                    tokens.TryGetValue(HostKey(item.Url), out token))
                {
                    item.PromoCode = "LP-" + token + "-" + slug;
                    changed = true;
                }
            }
            return changed;
        }
    }
}

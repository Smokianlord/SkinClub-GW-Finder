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


[assembly: AssemblyTitle("SkinClub GW Finder")]
[assembly: AssemblyProduct("SkinClub GW Finder")]
[assembly: AssemblyDescription("SkinClub creator giveaway monitor")]
[assembly: AssemblyVersion("1.4.0.0")]
[assembly: AssemblyFileVersion("1.4.0.0")]
[assembly: AssemblyInformationalVersion("1.4.0")]

namespace SkinClubGiveawayDesktop
{
    public class GiveawayItem
    {
        public string Creator { get; set; }
        public string Url { get; set; }
        public string Status { get; set; }
        public string Ticket { get; set; }
        public string PromoCode { get; set; }
        public string MinimumDeposit { get; set; }
        public string Deadline { get; set; }
        public string LastChecked { get; set; }
        public string Source { get; set; }
        public string Error { get; set; }
        public bool Joined { get; set; }
        public string JoinedAt { get; set; }
        public string HistorySince { get; set; }
        public string RenderedAt { get; set; }
    }

    public class AppData
    {
        public List<GiveawayItem> Items { get; set; }
        public string LastScan { get; set; }
        public string LastDeepScan { get; set; }

        public AppData()
        {
            Items = new List<GiveawayItem>();
        }
    }

    public class Candidate
    {
        public string Creator;
        public string Url;
        public string Source;

        public Candidate(string creator, string url, string source)
        {
            Creator = creator;
            Url = url;
            Source = source;
        }
    }

    public class ParseResult
    {
        public string Status;
        public string Ticket;
        public string PromoCode;
        public string MinimumDeposit;
        public string Deadline;
        public int? Remaining;
        public int? Total;
    }

    public class RenderedFields
    {
        public string Deadline = "-";
        public string PromoCode = "-";
        public string MinimumDeposit = "-";
    }

    public static class DataStore
    {
        public static readonly string DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SkinClub GW Finder");
        public static readonly string DataFile = Path.Combine(DataDir, "data.json");
        private static readonly object FileLock = new object();

        public static AppData Load()
        {
            Directory.CreateDirectory(DataDir);
            ImportOldDataIfPresent();
            if (!File.Exists(DataFile))
            {
                AppData initial = DefaultData();
                Save(initial);
                return initial;
            }
            try
            {
                string json = File.ReadAllText(DataFile, Encoding.UTF8);
                JavaScriptSerializer js = new JavaScriptSerializer();
                js.MaxJsonLength = int.MaxValue;
                AppData data = js.Deserialize<AppData>(json);
                if (data == null) data = DefaultData();
                if (data.Items == null) data.Items = new List<GiveawayItem>();
                data.Items.RemoveAll(delegate(GiveawayItem item) { return item == null || string.IsNullOrWhiteSpace(item.Url); });
                foreach (GiveawayItem item in data.Items)
                {
                    if (item == null) continue;
                    string promo = (item.PromoCode ?? "").Trim();
                    string upper = promo.ToUpperInvariant();
                    if (upper == "SKIN" || upper == "CLUB" || upper == "BONUS" ||
                        upper == "PRIZE" || upper == "POOL" || upper == "DISCOUNT" ||
                        upper == "PROMO" || upper == "PROMOCODE" || upper == "CODE")
                        item.PromoCode = "-";

                    // Repair metadata contaminated by the old concurrent Chromium/CDP
                    // race. Participation codes normally carry the same DDMMYY slug as
                    // the giveaway URL. If the saved code points at a different dated
                    // giveaway (for example SAMZ /010926 with ...-010826), discard the
                    // promo/deposit so the normal refresh can repopulate them correctly.
                    if (!string.IsNullOrWhiteSpace(item.PromoCode) && item.PromoCode != "-" &&
                        !string.IsNullOrWhiteSpace(item.Url))
                    {
                        Match promoDate = Regex.Match(item.PromoCode, @"-(\d{6})$", RegexOptions.IgnoreCase);
                        Match urlDate = Regex.Match(item.Url, @"/(\d{6})(?:/|$|\?)", RegexOptions.IgnoreCase);
                        if (promoDate.Success && urlDate.Success &&
                            !string.Equals(promoDate.Groups[1].Value, urlDate.Groups[1].Value, StringComparison.OrdinalIgnoreCase))
                        {
                            item.PromoCode = "-";
                            item.MinimumDeposit = "-";
                        }
                    }
                }
                int previousCount = data.Items.Count;
                PruneOldHistory(data);
                if (data.Items.Count != previousCount) Save(data);
                return data;
            }
            catch
            {
                // Preserve unreadable data for recovery instead of silently destroying it.
                File.Copy(DataFile, DataFile + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"), false);
                AppData fallback = DefaultData();
                Save(fallback);
                return fallback;
            }
        }

        public static void Save(AppData data)
        {
            PruneOldHistory(data);
            lock (FileLock)
            {
                Directory.CreateDirectory(DataDir);
                JavaScriptSerializer js = new JavaScriptSerializer();
                js.MaxJsonLength = int.MaxValue;
                string json = js.Serialize(data);
                string temp = DataFile + ".tmp";
                File.WriteAllText(temp, json, Encoding.UTF8);
                if (File.Exists(DataFile)) File.Replace(temp, DataFile, DataFile + ".bak");
                else File.Move(temp, DataFile);
            }
        }

        private static void PruneOldHistory(AppData data)
        {
            if (data == null || data.Items == null) return;

            // History is capped by recency, independently of the current grid sort.
            // Joined and active entries never count toward this limit.
            HashSet<GiveawayItem> older = new HashSet<GiveawayItem>(data.Items
                .Where(delegate(GiveawayItem item) {
                    return item != null && !item.Joined &&
                        string.Equals(item.Status, "ended", StringComparison.OrdinalIgnoreCase);
                })
                .OrderByDescending(HistoryRecency)
                .ThenBy(delegate(GiveawayItem item) { return item.Url ?? ""; }, StringComparer.OrdinalIgnoreCase)
                .Skip(30));
            data.Items.RemoveAll(delegate(GiveawayItem item) { return older.Contains(item); });
        }

        private static DateTime HistoryRecency(GiveawayItem item)
        {
            DateTime timestamp;
            if (DateTime.TryParse(item.HistorySince, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out timestamp))
                return timestamp.ToUniversalTime();
            if (DateTime.TryParse(item.LastChecked, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out timestamp))
                return timestamp.ToUniversalTime();
            return DateTime.MinValue;
        }
        private static void ImportOldDataIfPresent()
        {
            if (File.Exists(DataFile)) return;
            try
            {
                // Migrate data from older builds after the app was renamed.
                string legacyDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SkinClub Giveaway Dashboard");
                string legacyFile = Path.Combine(legacyDir, "data.json");
                if (File.Exists(legacyFile))
                {
                    File.Copy(legacyFile, DataFile, false);
                    return;
                }

                string old = Path.Combine(Application.StartupPath, "data.json");
                if (File.Exists(old)) File.Copy(old, DataFile, false);
            }
            catch { }
        }

        private static AppData DefaultData()
        {
            AppData d = new AppData();
            AddSeed(d, "Jon Sandman", "https://jonsandman.club/010926/");
            AddSeed(d, "McNasty", "https://mcnasty.club/010826/");
            AddSeed(d, "Blarg", "https://blarg.club/010826/");
            AddSeed(d, "ErycTriceps", "https://eryctriceps.club/010826/");
            AddSeed(d, "Joaco", "https://joaco.club/010826/");
            return d;
        }

        private static void AddSeed(AppData d, string creator, string url)
        {
            d.Items.Add(new GiveawayItem
            {
                Creator = creator,
                Url = url,
                Status = "unknown",
                Ticket = "-",
                PromoCode = "-",
                MinimumDeposit = "-",
                Deadline = "-",
                Source = "seed"
            });
        }
    }

    // Owns every Chromium/Edge process launched by this application.
    // The Windows Job Object has KILL_ON_JOB_CLOSE enabled, so Windows itself
    // terminates all browser child processes if the app closes or crashes.
    // We also keep the root Process handles so a normal FormClosing can clean
    // them up immediately instead of leaving background giveaway tabs behind.
    public static class Scanner
    {
        public static readonly SemaphoreSlim ScanLock = new SemaphoreSlim(1, 1);

        private static readonly Dictionary<string, string> CreatorDomains = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            {"Anomaly", "anomaly.club"}, {"Blarg", "blarg.club"}, {"Bomman", "bomman.club"},
            {"bysTaXx", "bystaxx.club"}, {"DEVNGUYEN", "devnguyen.club"}, {"DrewCS2", "drewcs2.club"}, {"DrewUnboxing", "drewunboxing.club"},
            {"ErycTriceps", "eryctriceps.club"}, {"HaiX", "haix.club"}, {"Jon Sandman", "jonsandman.club"},
            {"McNasty", "mcnasty.club"}, {"MrTweeday", "mrtweeday.club"}, {"NadeKing", "nadeking.club"},
            {"RickyWorld", "rickyworld.club"}, {"SAMZ", "samz.club"}, {"shynW", "shynw.club"},
            {"SwaggerSouls", "swagger.club"}, {"Tarifa", "tarifa.club"}, {"TheSparkles", "thesparkles.club"},
            {"Tuitenbo", "tuitenbo.club"}, {"Viruzz", "viruzz.club"}, {"Znorux", "znorux.club"},
            {"THE ASH", "theash.club"}, {"TheDooo", "thedooo.club"}, {"Yumi", "tooyumi.club"},
            {"Yinger", "yinger.club"}, {"Dona", "thedona.club"}, {"WaffleXD", "wafflecs.club"},
            {"Kryoz", "kryoz.club"}, {"Python", "thepython.club"}, {"JOISPOI24", "joispoi.club"},
            {"Joaco", "joaco.club"}
        };

        private static readonly Dictionary<string, string> DomainCreators = BuildDomainCreators();

        // Skin.Club's public partner page shows a featured subset rather than a
        // complete directory. These names are combined with the creator-domain
        // list below when Deep Search looks through partner social activity.
        private static readonly string[] FeaturedPartnerNames = new string[]
        {
            "ENCE", "G2 Esports", "HLTV", "GamerLegion", "Team Vitality",
            "James BanKs", "apEX", "karrigan", "Mauisnake", "Anders Blume",
            "Astralis", "9INE", "Virre CS2"
        };

        private static readonly Regex SocialUrlRegex = new Regex(
            @"https?://(?:www\.)?(?:(?:youtube\.com|youtu\.be)/[^\s""'<>\\]+|(?:x\.com|twitter\.com)/[^\s""'<>\\]+|t\.me/[^\s""'<>\\]+|(?:discord\.gg|discord\.com)/[^\s""'<>\\]+|instagram\.com/[^\s""'<>\\]+|tiktok\.com/[^\s""'<>\\]+|twitch\.tv/[^\s""'<>\\]+|kick\.com/[^\s""'<>\\]+|facebook\.com/[^\s""'<>\\]+|threads\.net/[^\s""'<>\\]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex DirectUrlRegex = new Regex(@"https?://(?:www\.)?([a-z0-9-]+\.club)/(\d{6})/?(?:\?[^\s""'<>\\]*)?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex VideoIdRegex = new Regex("\"videoId\":\"([A-Za-z0-9_-]{11})\"", RegexOptions.Compiled);
        // Status parsing is anchored to the giveaway's own ticket/deadline fields.
        // Generic END/FIN words elsewhere on the page (FAQ, footer, old results,
        // scripts, etc.) must never move a live giveaway into History.
        private static readonly Regex TicketLineRegex = new Regex(
            @"^[ \t]*(?:tickets?[ \t]*(?:left|remaining|restantes)?|tickets?[ \t]+disponibles|bilhetes?[ \t]+(?:restantes|dispon[ií]veis)|v[eé][ \t]*(?:c[oò]n[ \t]+l[aạ]i|con[ \t]+lai))[ \t]*[:\-]?[ \t]*([\d.,]+)[ \t]*/[ \t]*([\d.,]+)[ \t]*$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex TicketFallbackRegex = new Regex(
            @"(?:tickets?\s*(?:left|remaining|restantes)?|tickets?\s+disponibles|bilhetes?\s+(?:restantes|dispon[ií]veis)|v[eé]\s*(?:c[oò]n\s+l[aạ]i|con\s+lai))\s*[:\-]?\s*([\d.,]+)\s*/\s*([\d.,]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex DeadlineLineRegex = new Regex(
            @"^[ \t]*(?:time[ \t]+to[ \t]+completion|time[ \t]+remaining|tiempo[ \t]+restante|tempo[ \t]+restante|th(?:ờ|o)i[ \t]+gian[ \t]+c(?:ò|o)n[ \t]+l(?:ạ|a)i|deadline|ends?[ \t]+in|ending[ \t]+in)[ \t]*[:\-]?[ \t]*([^
|]{0,80})[ \t]*$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex ExactEndedValueRegex = new Regex(
            @"^\s*(?:end|ended|fin|finished|k[eế]t\s*th[uú]c|ket\s*thuc|cerrado|terminado|finalizado|encerrado|conclu[ií]do)\s*[.!]*\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex CountdownUnitRegex = new Regex(
            @"(\d+(?:[.,]\d+)?)\s*(days?|d[ií]as?|dias?|d|hours?|hrs?|horas?|h|minutes?|mins?|minutos?|m|seconds?|secs?|segundos?|s)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex CountdownClockRegex = new Regex(
            @"^\s*(?:(\d+)\s*[: ]\s*)?(\d{1,2}):(\d{2}):(\d{2})\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex HiddenAbsoluteDeadlineRegex = new Regex(
            @"(?:end(?:[-_ ]?(?:date|time|at))?|endsAt|endDate|deadline|expires?(?:[-_ ]?(?:date|time|at))?|finish(?:[-_ ]?(?:date|time|at))?|data-end(?:[-_](?:date|time|at))?)\s*[^0-9]{0,18}([0-9]{4}-[0-9]{2}-[0-9]{2}(?:[T ][0-9]{2}:[0-9]{2}(?::[0-9]{2})?(?:\.[0-9]+)?(?:Z|[+\-][0-9]{2}:?[0-9]{2})?)?)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex HiddenUnixDeadlineRegex = new Regex(
            @"(?:end(?:[-_ ]?(?:time|at))?|deadline|expires?(?:[-_ ]?(?:time|at))?|finish(?:[-_ ]?(?:time|at))?|data-end(?:[-_](?:time|at))?)\s*[^0-9]{0,18}([0-9]{10,13})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RemainingSecondsRegex = new Regex(
            @"(?:timeRemaining|remainingTime|secondsLeft|seconds_left|time_left|timeLeft)\s*[^0-9]{0,18}([0-9]{1,8})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RemainingHoursRegex = new Regex(
            @"(?:hoursRemaining|remainingHours|hoursLeft|hours_left|remaining_hours|timeToCompletionHours|time_to_completion_hours|hoursToCompletion|completionHours|completion_hours|timeToCompletion|time_to_completion|remainingTimeHours|timeRemainingHours|hoursUntilEnd|hours_until_end|hoursToEnd|hours_to_end|endInHours|end_in_hours)\s*[""']?\s*[:=]\s*[""']?\s*([0-9]+(?:[.,][0-9]+)?)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RemainingHoursNearLabelRegex = new Regex(
            @"(?:time\s*to\s*completion|time\s*remaining|remaining\s*time|ends?\s*in|deadline|completion)[^0-9]{0,120}([0-9]+(?:[.,][0-9]+)?)\s*(?:hours?|hrs?|hr|h)\b",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex RemainingBareHoursNearLabelRegex = new Regex(
            @"(?:time\s*to\s*completion|timeToCompletion|time_to_completion|remainingHours|hoursRemaining|hoursLeft|hours_left|hoursToCompletion|completionHours|hoursUntilEnd|hoursToEnd|endInHours)[^0-9]{0,80}([0-9]+(?:[.,][0-9]+)?)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex DeadlineNextValueRegex = new Regex(
            @"(?:time[ \t]+to[ \t]+completion|time[ \t]+remaining|deadline|ends?[ \t]+in|ending[ \t]+in)[ \t]*[:\-]?[ \t]*(?:\r?\n[ \t]*){0,2}(END|ENDED|[0-9]+(?:[.,][0-9]+)?(?:[ \t]*(?:days?|d|hours?|hrs?|h|minutes?|mins?|m|seconds?|secs?|s))?(?:[ \t]+[0-9]+(?:[.,][0-9]+)?[ \t]*(?:days?|d|hours?|hrs?|h|minutes?|mins?|m|seconds?|secs?|s))*|[0-9]{1,3}:[0-9]{2}(?::[0-9]{2})?)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // Current SkinClub pages render the countdown as, for example:
        // "TIME TO COMPLETION: 161H 59M 2S". Keep this parser deliberately
        // anchored to that visible label so unrelated hidden timestamps cannot
        // be mistaken for the giveaway deadline.
        private static readonly Regex VisibleCompletionTimerRegex = new Regex(
            @"time\s*to\s*completion\s*:?\s*([0-9]+(?:[.,][0-9]+)?)\s*(?:hours?|hrs?|hr|h)\s*(?:([0-9]+(?:[.,][0-9]+)?)\s*(?:minutes?|mins?|min|m)\s*)?(?:([0-9]+(?:[.,][0-9]+)?)\s*(?:seconds?|secs?|sec|s))?",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly HttpClient Client = MakeClient();
        // HTTP discovery stays concurrent. Render one page at a time, with Windows
        // enforcing five browser processes total INCLUDING the root and all children.
        private static readonly SemaphoreSlim RenderFallbackThrottle = new SemaphoreSlim(1, 1);
        private static int queuedRenders;
        public static int QueuedRenders { get { return Volatile.Read(ref queuedRenders); } }
        private static readonly object RenderCacheGate = new object();
        private static readonly Dictionary<string, Tuple<DateTime, RenderedFields>> RenderCache =
            new Dictionary<string, Tuple<DateTime, RenderedFields>>(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan RenderCacheLifetime = TimeSpan.FromMinutes(10);
        // How long to wait before launching the browser again for a page whose
        // promocode / deposit / deadline could not be found on the last attempt.
        private static readonly TimeSpan MetadataRetryInterval = TimeSpan.FromHours(12);
        // Snapshot of already-saved items, keyed by GiveawayKey, for the scan in progress.
        private static volatile Dictionary<string, GiveawayItem> KnownItems;

        private static Dictionary<string, GiveawayItem> IndexKnown(AppData data)
        {
            Dictionary<string, GiveawayItem> map = new Dictionary<string, GiveawayItem>(StringComparer.OrdinalIgnoreCase);
            foreach (GiveawayItem i in data.Items)
                if (i != null && !string.IsNullOrWhiteSpace(i.Url)) map[GiveawayKey(i.Url)] = i;
            return map;
        }

        private static Dictionary<string, string> BuildDomainCreators()
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> kv in CreatorDomains) map[kv.Value] = kv.Key;
            return map;
        }

        private static List<string> PartnerSearchNames()
        {
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in CreatorDomains.Keys) names.Add(name);
            foreach (string name in FeaturedPartnerNames) names.Add(name);
            return names.OrderBy(delegate(string x) { return x; }, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static List<string> ExtractSocialUrls(string raw)
        {
            List<string> urls = new List<string>();
            string normalized = NormalizeDiscoveryHtml(raw);
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in SocialUrlRegex.Matches(normalized))
            {
                string url = WebUtility.HtmlDecode(m.Value).Trim().TrimEnd('.', ',', ';', ')', ']', '}');
                if (url.Length == 0 || !seen.Add(url)) continue;
                urls.Add(url);
            }
            return urls;
        }

        private static HttpClient MakeClient()
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
            ServicePointManager.DefaultConnectionLimit = 20;
            HttpClientHandler h = new HttpClientHandler();
            h.AllowAutoRedirect = true;
            h.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            HttpClient c = new HttpClient(h);
            c.Timeout = TimeSpan.FromSeconds(8);
            c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/152 Safari/537.36");
            c.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            return c;
        }

        public static string InferCreator(string url, string fallback)
        {
            try
            {
                Uri u = new Uri(url);
                string host = u.Host.ToLowerInvariant();
                if (host.StartsWith("www.")) host = host.Substring(4);
                string creator;
                if (DomainCreators.TryGetValue(host, out creator)) return creator;
                if (!string.IsNullOrWhiteSpace(fallback)) return fallback;
                string stem = host.Split('.')[0].Replace('-', ' ').Replace('_', ' ');
                if (stem.Length == 0) return "Unknown";
                return System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(stem);
            }
            catch { return string.IsNullOrWhiteSpace(fallback) ? "Unknown" : fallback; }
        }

        private static string NormalizeDiscoveryHtml(string s)
        {
            if (s == null) return "";
            string normalized = s.Replace("\\u002F", "/").Replace("\\u002f", "/")
                    .Replace("\\u003A", ":").Replace("\\u003a", ":")
                    .Replace("\\u0026", "&")
                    .Replace("\\/", "/");
            normalized = WebUtility.HtmlDecode(normalized);
            try { normalized = Uri.UnescapeDataString(normalized); } catch { }
            return normalized;
        }

        private static string GiveawayKey(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "";
            try
            {
                Uri u = new Uri(url);
                string host = u.Host.ToLowerInvariant();
                if (host.StartsWith("www.")) host = host.Substring(4);
                string path = u.AbsolutePath.TrimEnd('/').ToLowerInvariant();
                return host + path;
            }
            catch
            {
                string value = url.Trim();
                int q = value.IndexOf('?');
                if (q >= 0) value = value.Substring(0, q);
                return value.TrimEnd('/').ToLowerInvariant();
            }
        }

        private static string CleanDiscoveredUrl(string rawUrl)
        {
            if (string.IsNullOrWhiteSpace(rawUrl)) return rawUrl;
            string url = WebUtility.HtmlDecode(rawUrl).Trim().TrimEnd('.', ',', ';', ')', ']', '}');
            try
            {
                Uri u = new Uri(url);
                string path = u.GetLeftPart(UriPartial.Path);
                if (!path.EndsWith("/")) path += "/";
                return path;
            }
            catch
            {
                return url;
            }
        }

        private static List<Candidate> ExtractDirectUrls(string raw, string source)
        {
            List<Candidate> list = new List<Candidate>();
            string normalized = NormalizeDiscoveryHtml(raw);
            MatchCollection matches = DirectUrlRegex.Matches(normalized);
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in matches)
            {
                string url = CleanDiscoveredUrl(m.Value);
                string key = GiveawayKey(url);
                if (key.Length == 0 || !seen.Add(key)) continue;
                list.Add(new Candidate(InferCreator(url, null), url, source));
            }
            return list;
        }

        private static string HtmlToText(string html)
        {
            if (string.IsNullOrEmpty(html)) return "";
            string s = Regex.Replace(html, @"<(?:br|/p|/div|/li|/tr|/h[1-6])\b[^>]*>", "\n", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<script\b[^>]*>.*?</script>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            s = Regex.Replace(s, @"<style\b[^>]*>.*?</style>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            s = Regex.Replace(s, @"<[^>]+>", " ");
            s = WebUtility.HtmlDecode(s);
            s = s.Replace("\r", "");
            s = Regex.Replace(s, @"[ \t]+", " ");
            s = Regex.Replace(s, @"\n\s*\n+", "\n");
            return s.Trim();
        }

        // Participation details must come from the actual "Take part / Step 2"
        // instructions or the Skin.Club promo link.  The page also contains a
        // decorative "use promocode" special-prize CTA; treating the next word
        // after that CTA as a code is what produced bogus values such as SKIN.
        private static readonly Regex PromoQueryRegex = new Regex(
            @"(?:utm_promo|promo_code|promocode)\s*=\s*([A-Za-z0-9][A-Za-z0-9_\-]{2,63})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ParticipationStepRegex = new Regex(
            @"(?:replenish|top[ \t-]*up|deposit|recharge)[\s\S]{0,120}?(?:balance|account)?[\s\S]{0,60}?(?:from|minimum(?:\s+deposit)?(?:\s+of)?|at\s+least)\s*((?:[$€£]\s*)?[0-9]+(?:[.,][0-9]{1,2})?\s*(?:USD|EUR|GBP|USDT|RUB|PLN|BRL|ARS|UAH|BDT)?|[0-9]+(?:[.,][0-9]{1,2})?\s*[$€£])[\s\S]{0,100}?(?:promo(?:tional)?[\s_\-]*code|promocode|c[oó]digo\s*(?:promocional|promo)?)[ \t
:=-]*([A-Za-z0-9][A-Za-z0-9_\-]{2,63})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex PromoCodeRegex = new Regex(
            @"(?:promo(?:tional)?[\s_\-]*code|promocode|special[\s_\-]*code|code[\s_\-]*promo|c[oó]digo\s*(?:promocional|promo)?|use\s+(?:promo[\s_\-]*)?code|enter\s+(?:promo[\s_\-]*)?code)\s*(?:(?:for\s+participation|to\s+participate|for\s+the\s+giveaway)\s*)?(?:[:#=\-]|\bis\b)?\s*[""']?([A-Za-z0-9][A-Za-z0-9_\-]{1,63})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex MinimumDepositRegex = new Regex(
            @"(?:(?:minimum|min\.?)[\s_\-]*(?:deposit(?:[\s_\-]+amount)?|top[\s_\-]*up(?:[\s_\-]+amount)?|recharge(?:[\s_\-]+amount)?|amount)|deposit[\s_\-]+(?:minimum|min\.?))\s*[:=\-]?\s*((?:[$€£]\s*)?[0-9]+(?:[.,][0-9]{1,2})?\s*(?:USD|EUR|GBP|USDT|RUB|PLN|BRL|ARS|UAH|BDT)?|[0-9]+(?:[.,][0-9]{1,2})?\s*[$€£])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ParticipationDepositRegex = new Regex(
            @"(?:from|starting\s+from)\s*((?:[$€£]\s*)?[0-9]+(?:[.,][0-9]{1,2})?\s*(?:USD|EUR|GBP|USDT|RUB|PLN|BRL|ARS|UAH|BDT)?|[0-9]+(?:[.,][0-9]{1,2})?\s*[$€£])\s*(?:with|using)\s+(?:a\s+)?promo\s*code",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex JsonMinimumDepositRegex = new Regex(
            @"[""']?(?:minimumDeposit|minimum_deposit|minDeposit|min_deposit|depositMinimum|deposit_minimum|minimumTopup|minimum_topup|minTopup|min_topup|minRecharge|minimumRecharge)[""']?\s*[:=]\s*[""']?\s*((?:[$€£]\s*)?[0-9]+(?:[.,][0-9]{1,2})?\s*(?:USD|EUR|GBP|USDT|RUB|PLN|BRL|ARS|UAH|BDT)?|[0-9]+(?:[.,][0-9]{1,2})?\s*[$€£])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex FlexibleDepositBeforePromoRegex = new Regex(
            @"(?:replenish|top[ \t-]*up|deposit|recharge|add\s+funds|fund\s+(?:your\s+)?balance)[\s\S]{0,180}?(?:from|starting\s+from|at\s+least|minimum(?:\s+of)?|more\s+than|over)?\s*((?:[$€£]\s*)?[0-9]+(?:[.,][0-9]{1,2})?\s*(?:USD|EUR|GBP|USDT|RUB|PLN|BRL|ARS|UAH|BDT)?|[0-9]+(?:[.,][0-9]{1,2})?\s*[$€£])[\s\S]{0,180}?(?:promo(?:tional)?[\s_\-]*code|promocode|c[oó]digo)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex FlexiblePromoBeforeDepositRegex = new Regex(
            @"(?:promo(?:tional)?[\s_\-]*code|promocode|c[oó]digo)[\s\S]{0,180}?(?:replenish|top[ \t-]*up|deposit|recharge|add\s+funds|fund\s+(?:your\s+)?balance)[\s\S]{0,120}?(?:from|starting\s+from|at\s+least|minimum(?:\s+of)?|more\s+than|over)?\s*((?:[$€£]\s*)?[0-9]+(?:[.,][0-9]{1,2})?\s*(?:USD|EUR|GBP|USDT|RUB|PLN|BRL|ARS|UAH|BDT)?|[0-9]+(?:[.,][0-9]{1,2})?\s*[$€£])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // Creator templates are not identical. Some pages use wording such as
        // "refill your balance with $5" or translated labels instead of the
        // exact "minimum deposit / from $5" phrasing. These helpers are only
        // used near a verified participation promocode so prize values elsewhere
        // on the giveaway page are not mistaken for the deposit requirement.
        private static readonly Regex DepositContextRegex = new Regex(
            @"replenish|refill|top[ \t-]*up|deposit|recharge|balance|add\s+(?:funds|money)|fund\s+(?:your\s+)?balance|load\s+(?:your\s+)?balance|recarg(?:a|ar|ue)|saldo|dep[oó]sito|depositar|ingres(?:a|ar)|recarregar|carregar\s+saldo|recharg(?:e|er)|solde|einzahl(?:en|ung)|guthaben",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex MoneyAmountRegex = new Regex(
            @"(?:(?:US|CA|AU)?[$€£]\s*[0-9]+(?:[.,][0-9]{1,2})?|[0-9]+(?:[.,][0-9]{1,2})?\s*(?:US[$]|CA[$]|AU[$]|[$€£]|USD|EUR|GBP|USDT|RUB|PLN|BRL|ARS|UAH|BDT))",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static int? ParseInt(string s)
        {
            if (s == null) return null;
            string digits = Regex.Replace(s, "[^0-9]", "");
            int v;
            if (int.TryParse(digits, out v)) return v;
            return null;
        }

        private static bool TryParseCountdown(string raw, out TimeSpan span)
        {
            span = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(raw)) return false;

            string s = raw.Trim().ToLowerInvariant();
            double totalSeconds = 0;
            bool found = false;

            MatchCollection units = CountdownUnitRegex.Matches(s);
            foreach (Match m in units)
            {
                double value;
                string number = (m.Groups[1].Value ?? "").Replace(',', '.');
                if (!double.TryParse(number, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value)) continue;
                string unit = (m.Groups[2].Value ?? "").ToLowerInvariant();

                if (unit.StartsWith("d") && unit != "dias" && unit != "dia" && unit != "días" && unit != "día")
                    totalSeconds += value * 86400.0;
                else if (unit.StartsWith("day") || unit == "dia" || unit == "dias" || unit == "día" || unit == "días" || unit == "d")
                    totalSeconds += value * 86400.0;
                else if (unit.StartsWith("h") || unit.StartsWith("hora"))
                    totalSeconds += value * 3600.0;
                else if (unit.StartsWith("m") || unit.StartsWith("min"))
                    totalSeconds += value * 60.0;
                else if (unit.StartsWith("s") || unit.StartsWith("seg"))
                    totalSeconds += value;
                found = true;
            }

            // Common countdown forms: HH:MM:SS or DD:HH:MM:SS.
            string withoutUnits = CountdownUnitRegex.Replace(s, " ").Trim();
            Match clock = CountdownClockRegex.Match(withoutUnits);
            if (clock.Success)
            {
                int a = 0, b = 0, c = 0, d = 0;
                if (clock.Groups[1].Success)
                {
                    int.TryParse(clock.Groups[1].Value, out a);
                    int.TryParse(clock.Groups[2].Value, out b);
                    int.TryParse(clock.Groups[3].Value, out c);
                    int.TryParse(clock.Groups[4].Value, out d);
                    totalSeconds += a * 86400.0 + b * 3600.0 + c * 60.0 + d;
                }
                else
                {
                    int.TryParse(clock.Groups[2].Value, out b);
                    int.TryParse(clock.Groups[3].Value, out c);
                    int.TryParse(clock.Groups[4].Value, out d);
                    totalSeconds += b * 3600.0 + c * 60.0 + d;
                }
                found = true;
            }

            if (!found || totalSeconds < 0) return false;
            span = TimeSpan.FromSeconds(totalSeconds);
            return true;
        }

        private static bool TryParseHoursNumber(string raw, out double hours)
        {
            hours = 0;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            string number = raw.Trim().Replace(',', '.');
            double parsed;
            if (!double.TryParse(number, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out parsed)) return false;
            // Sanity limit: a creator giveaway should not run for years. This also
            // prevents Unix timestamps or unrelated large numbers being treated as hours.
            if (parsed < 0 || parsed > 17568) return false;
            hours = parsed;
            return true;
        }

        private static string DeadlineFromHours(double hours)
        {
            DateTime end = DateTime.Now.AddHours(hours);
            return end.ToString("MMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture).ToUpperInvariant();
        }

        private static string DeadlineFromRenderedCompletionText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "-";

            string decoded = WebUtility.HtmlDecode(text).Replace('\u00A0', ' ');
            Match match = VisibleCompletionTimerRegex.Match(decoded);
            if (!match.Success) return "-";

            double hours = 0, minutes = 0, seconds = 0;
            string h = (match.Groups[1].Value ?? "").Replace(',', '.');
            string m = match.Groups[2].Success ? (match.Groups[2].Value ?? "0").Replace(',', '.') : "0";
            string sec = match.Groups[3].Success ? (match.Groups[3].Value ?? "0").Replace(',', '.') : "0";

            if (!double.TryParse(h, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out hours)) return "-";
            if (!double.TryParse(m, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out minutes)) minutes = 0;
            if (!double.TryParse(sec, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out seconds)) seconds = 0;

            if (hours < 0 || hours > 17568 || minutes < 0 || minutes >= 60 || seconds < 0 || seconds >= 60) return "-";

            DateTime end = DateTime.Now.AddSeconds(hours * 3600.0 + minutes * 60.0 + seconds);
            return end.ToString("MMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture).ToUpperInvariant();
        }

        private static string DeadlineFromRawHtml(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return "-";

            string decoded = WebUtility.HtmlDecode(html);
            Match hoursMatch = RemainingHoursRegex.Match(decoded);
            if (!hoursMatch.Success) hoursMatch = RemainingHoursNearLabelRegex.Match(decoded);
            if (!hoursMatch.Success) hoursMatch = RemainingBareHoursNearLabelRegex.Match(decoded);
            if (hoursMatch.Success)
            {
                double hours;
                if (TryParseHoursNumber(hoursMatch.Groups[1].Value, out hours))
                    return DeadlineFromHours(hours);
            }

            return "-";
        }

        private static string DeadlineFromVisibleText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "-";

            // First handle the normal same-line form.
            MatchCollection matches = DeadlineLineRegex.Matches(text);
            foreach (Match match in matches)
            {
                string raw = Regex.Replace(match.Groups[1].Value ?? "", @"\s+", " ").Trim().Trim(' ', ':', '|');
                if (raw == "" || raw == "-") continue;
                if (ExactEndedValueRegex.IsMatch(raw)) return "Ended";
                string converted = DeadlineAsDate(raw);
                if (converted != "-") return converted;
            }

            // The new layout can render the label and hour count in separate block
            // elements, e.g. "Time to completion:" on one line and "46" on the next.
            Match next = DeadlineNextValueRegex.Match(text);
            if (next.Success)
            {
                string raw = Regex.Replace(next.Groups[1].Value ?? "", @"\s+", " ").Trim();
                if (ExactEndedValueRegex.IsMatch(raw)) return "Ended";
                string converted = DeadlineAsDate(raw);
                if (converted != "-") return converted;
            }

            return "-";
        }

        private static string FindChromiumBrowser()
        {
            List<string> paths = new List<string>();
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            if (!string.IsNullOrWhiteSpace(pf86))
            {
                paths.Add(Path.Combine(pf86, "Microsoft", "Edge", "Application", "msedge.exe"));
                paths.Add(Path.Combine(pf86, "Google", "Chrome", "Application", "chrome.exe"));
            }
            if (!string.IsNullOrWhiteSpace(pf))
            {
                paths.Add(Path.Combine(pf, "Microsoft", "Edge", "Application", "msedge.exe"));
                paths.Add(Path.Combine(pf, "Google", "Chrome", "Application", "chrome.exe"));
                paths.Add(Path.Combine(pf, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"));
            }
            if (!string.IsNullOrWhiteSpace(local))
            {
                paths.Add(Path.Combine(local, "Microsoft", "Edge", "Application", "msedge.exe"));
                paths.Add(Path.Combine(local, "Google", "Chrome", "Application", "chrome.exe"));
                paths.Add(Path.Combine(local, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"));
            }

            foreach (string path in paths)
                if (File.Exists(path)) return path;
            return null;
        }

        private static int GetFreeTcpPort()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally { listener.Stop(); }
        }

        private static string JsonStringValue(object value)
        {
            return value == null ? "" : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        private static async Task<string> DevToolsEvaluateAsync(string websocketUrl, string expression, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(websocketUrl)) return "";
            using (ClientWebSocket ws = new ClientWebSocket())
            {
                ws.Options.Proxy = null;
                await ws.ConnectAsync(new Uri(websocketUrl), token);

                JavaScriptSerializer js = new JavaScriptSerializer();
                Dictionary<string, object> payload = new Dictionary<string, object>();
                payload["id"] = 1;
                payload["method"] = "Runtime.evaluate";
                Dictionary<string, object> parameters = new Dictionary<string, object>();
                parameters["expression"] = expression;
                parameters["returnByValue"] = true;
                parameters["awaitPromise"] = true;
                payload["params"] = parameters;

                byte[] outgoing = Encoding.UTF8.GetBytes(js.Serialize(payload));
                await ws.SendAsync(new ArraySegment<byte>(outgoing), WebSocketMessageType.Text, true, token);

                using (MemoryStream ms = new MemoryStream())
                {
                    byte[] buffer = new byte[8192];
                    DateTime expires = DateTime.UtcNow.AddSeconds(8);
                    while (DateTime.UtcNow < expires && ws.State == WebSocketState.Open)
                    {
                        WebSocketReceiveResult rr;
                        rr = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);

                        if (rr.MessageType == WebSocketMessageType.Close) break;
                        ms.Write(buffer, 0, rr.Count);
                        if (!rr.EndOfMessage) continue;

                        string message = Encoding.UTF8.GetString(ms.ToArray());
                        ms.SetLength(0);
                        try
                        {
                            Dictionary<string, object> root = js.Deserialize<Dictionary<string, object>>(message);
                            object idObj;
                            if (!root.TryGetValue("id", out idObj) || Convert.ToInt32(idObj) != 1) continue;
                            object resultObj;
                            if (!root.TryGetValue("result", out resultObj)) return "";
                            Dictionary<string, object> result = resultObj as Dictionary<string, object>;
                            if (result == null) return "";
                            object innerObj;
                            if (!result.TryGetValue("result", out innerObj)) return "";
                            Dictionary<string, object> inner = innerObj as Dictionary<string, object>;
                            if (inner == null) return "";
                            object valueObj;
                            if (!inner.TryGetValue("value", out valueObj)) return "";
                            return JsonStringValue(valueObj);
                        }
                        catch { return ""; }
                    }
                }
            }
            return "";
        }

        private static RenderedFields CloneRenderedFields(RenderedFields source)
        {
            RenderedFields copy = new RenderedFields();
            if (source == null) return copy;
            copy.Deadline = source.Deadline;
            copy.PromoCode = source.PromoCode;
            copy.MinimumDeposit = source.MinimumDeposit;
            return copy;
        }

        private static bool TryGetRenderedCache(string url, out RenderedFields fields)
        {
            fields = null;
            if (string.IsNullOrWhiteSpace(url)) return false;
            lock (RenderCacheGate)
            {
                Tuple<DateTime, RenderedFields> cached;
                if (!RenderCache.TryGetValue(url, out cached)) return false;
                if (DateTime.UtcNow - cached.Item1 > RenderCacheLifetime)
                {
                    RenderCache.Remove(url);
                    return false;
                }
                fields = CloneRenderedFields(cached.Item2);
                return true;
            }
        }

        private static void StoreRenderedCache(string url, RenderedFields fields)
        {
            if (string.IsNullOrWhiteSpace(url) || fields == null) return;
            lock (RenderCacheGate)
            {
                RenderCache[url] = Tuple.Create(DateTime.UtcNow, CloneRenderedFields(fields));
            }
        }

        private static void KillProcessTree(Process process)
        {
            BrowserProcessManager.KillProcessTree(process);
        }

        private static void RemoveRenderProfile(string profileDir)
        {
            if (string.IsNullOrWhiteSpace(profileDir)) return;
            string fullPath = Path.GetFullPath(profileDir);
            string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(Path.GetDirectoryName(fullPath), tempRoot, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(fullPath).StartsWith("SkinClubGWFinder_", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to remove a profile outside the app's temporary folder");
            for (int attempt = 0; attempt < 8; attempt++)
            {
                try
                {
                    if (Directory.Exists(fullPath)) Directory.Delete(fullPath, true);
                    return;
                }
                catch (IOException ex)
                {
                    if (attempt == 7) { ActivityLog.Write("CLEANUP", "Temporary profile still locked after browser exit: " + ex.Message); return; }
                    Thread.Sleep(150);
                }
                catch (UnauthorizedAccessException ex)
                { ActivityLog.Write("CLEANUP", "Temporary profile could not be removed: " + ex.Message); return; }
            }
        }

        private static async Task<RenderedFields> FetchRenderedFieldsAsync(string url)
        {
            RenderedFields cachedFields;
            if (TryGetRenderedCache(url, out cachedFields))
            { ActivityLog.Write("CACHE", "Using rendered metadata: " + url); return cachedFields; }

            RenderedFields best = new RenderedFields();
            if (string.IsNullOrWhiteSpace(url)) return best;
            string browser = FindChromiumBrowser();
            if (string.IsNullOrWhiteSpace(browser))
            { ActivityLog.Write("ERROR", "No Edge or Chrome installation found for " + url); return best; }

            Interlocked.Increment(ref queuedRenders);
            ActivityLog.Write("QUEUE", "Waiting to render " + url);
            try { await RenderFallbackThrottle.WaitAsync(BrowserProcessManager.ShutdownToken); }
            finally { Interlocked.Decrement(ref queuedRenders); }
            string profileDir = null;
            Process process = null;
            HttpClient localClient = null;
            CancellationTokenSource renderCts = CancellationTokenSource.CreateLinkedTokenSource(BrowserProcessManager.ShutdownToken);
            renderCts.CancelAfter(TimeSpan.FromSeconds(30));
            CancellationToken token = renderCts.Token;
            try
            {
                token.ThrowIfCancellationRequested();
                if (TryGetRenderedCache(url, out cachedFields)) return cachedFields;
                ActivityLog.Write("RENDER", "Loading " + url);
                // Let Chromium choose its own DevTools port and use an isolated profile.
                profileDir = Path.Combine(Path.GetTempPath(), "SkinClubGWFinder_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(profileDir);

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = browser;
                psi.Arguments = "--headless=new --disable-gpu --in-process-gpu --enable-features=NetworkServiceInProcess2 --disable-extensions --no-first-run --no-default-browser-check " +
                                "--disable-sync --mute-audio --disable-background-networking --disable-component-update " +
                                "--disable-features=MediaRouter,OptimizationHints,Translate,BackForwardCache " +
                                "--renderer-process-limit=1 --disable-background-mode --disable-crash-reporter --blink-settings=imagesEnabled=false " +
                                "--remote-allow-origins=* --remote-debugging-port=0" +
                                " --user-data-dir=\"" + profileDir + "\" \"" + url.Replace("\"", "") + "\"";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;

                process = BrowserProcessManager.StartHidden(psi.FileName, psi.Arguments);
                try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }

                HttpClientHandler localHandler = new HttpClientHandler();
                localHandler.UseProxy = false;
                localClient = new HttpClient(localHandler);
                localClient.Timeout = TimeSpan.FromSeconds(2);
                string websocketUrl = "";
                int port = 0;
                string requestedHost = "";
                try { requestedHost = new Uri(url).Host; } catch { }
                DateTime discoveryDeadline = DateTime.UtcNow.AddSeconds(8);

                // With --remote-debugging-port=0 Chromium writes the actual selected port
                // to DevToolsActivePort in this render's unique profile directory. This is
                // independent of other applications using DevTools.
                while (DateTime.UtcNow < discoveryDeadline && port <= 0)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        string activePortFile = Path.Combine(profileDir, "DevToolsActivePort");
                        if (File.Exists(activePortFile))
                        {
                            string[] lines = File.ReadAllLines(activePortFile);
                            int parsedPort;
                            if (lines.Length > 0 && int.TryParse(lines[0].Trim(), out parsedPort) && parsedPort > 0)
                                port = parsedPort;
                        }
                    }
                    catch { }
                    if (port <= 0) await Task.Delay(100, token);
                }

                while (DateTime.UtcNow < discoveryDeadline && port > 0 && string.IsNullOrWhiteSpace(websocketUrl))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        string tabsJson;
                        using (HttpResponseMessage response = await localClient.GetAsync("http://127.0.0.1:" + port + "/json/list", token))
                        {
                            response.EnsureSuccessStatusCode();
                            tabsJson = await response.Content.ReadAsStringAsync();
                        }
                        JavaScriptSerializer serializer = new JavaScriptSerializer();
                        object[] tabs = serializer.Deserialize<object[]>(tabsJson);
                        foreach (object tabObj in tabs)
                        {
                            Dictionary<string, object> tab = tabObj as Dictionary<string, object>;
                            if (tab == null) continue;
                            string type = tab.ContainsKey("type") ? JsonStringValue(tab["type"]) : "";
                            string tabUrl = tab.ContainsKey("url") ? JsonStringValue(tab["url"]) : "";
                            string ws = tab.ContainsKey("webSocketDebuggerUrl") ? JsonStringValue(tab["webSocketDebuggerUrl"]) : "";
                            if (!string.Equals(type, "page", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(ws))
                                continue;

                            // Never fall back to the first page target. Only attach to the
                            // target whose host matches the giveaway currently being validated.
                            // This is a second guard against cross-page metadata contamination.
                            Uri tabUri;
                            if (!string.IsNullOrWhiteSpace(requestedHost) &&
                                Uri.TryCreate(tabUrl, UriKind.Absolute, out tabUri) &&
                                string.Equals(tabUri.Host, requestedHost, StringComparison.OrdinalIgnoreCase))
                            {
                                websocketUrl = ws;
                                break;
                            }
                        }
                    }
                    catch { }
                    if (string.IsNullOrWhiteSpace(websocketUrl)) await Task.Delay(150, token);
                }

                if (string.IsNullOrWhiteSpace(websocketUrl))
                { ActivityLog.Write("TIMEOUT", "No page target within 8 seconds: " + url); return best; }

                // Read the text the user actually sees. The previous build scanned
                // hidden JS/state too, which could pick an unrelated timer and produce
                // SEP 11/SEP 12 even when the page visibly showed 161H 59M 2S.
                string expression = @"(() => {
                    try {
                        if (!document.body) return '';
                        return document.body.innerText || document.body.textContent || '';
                    } catch (e) {
                        return '';
                    }
                })()";

                // Promo/deposit values are sometimes present in hidden fields or data-*
                // attributes even when the visible page only shows a generic FAQ. Collect
                // only relevant DOM metadata so we can find those values without using
                // hidden timer state for the deadline.
                string metadataExpression = @"(() => {
                    try {
                        const out = [];
                        if (document.documentElement) out.push(document.documentElement.textContent || '');
                        document.querySelectorAll('*').forEach(el => {
                            try {
                                if (typeof el.value === 'string' && el.value) out.push('value=' + el.value);
                                if (el.href && /(?:utm_promo|promo_code|promocode)=/i.test(el.href)) out.push('href=' + el.href);
                                if (!el.attributes) return;
                                for (const a of el.attributes) {
                                    const pair = (a.name || '') + '=' + (a.value || '');
                                    if (/promo|code|deposit|minimum|min[-_ ]?deposit|top[-_ ]?up|recharge/i.test(pair)) out.push(pair);
                                }
                            } catch (_) {}
                        });
                        // Creator templates sometimes keep participation settings only
                        // in JSON/application state. Read promo/deposit-related script
                        // and storage text, but deliberately ignore timer/deadline state.
                        document.querySelectorAll('script').forEach(s => {
                            try {
                                const t = s.textContent || '';
                                if (/promo|promocode|minimumDeposit|minDeposit|minimum_deposit|min_deposit|deposit|topup|top_up|recharge/i.test(t))
                                    out.push(t.slice(0, 60000));
                            } catch (_) {}
                        });
                        try {
                            for (let i = 0; i < localStorage.length; i++) {
                                const k = localStorage.key(i) || '';
                                const v = localStorage.getItem(k) || '';
                                if (/promo|deposit|minimum|minDeposit|topup|recharge/i.test(k + ' ' + v)) out.push(k + '=' + v);
                            }
                        } catch (_) {}
                        try {
                            for (let i = 0; i < sessionStorage.length; i++) {
                                const k = sessionStorage.key(i) || '';
                                const v = sessionStorage.getItem(k) || '';
                                if (/promo|deposit|minimum|minDeposit|topup|recharge/i.test(k + ' ' + v)) out.push(k + '=' + v);
                            }
                        } catch (_) {}
                        return out.join('\n').slice(0, 300000);
                    } catch (e) {
                        return '';
                    }
                })()";

                // The countdown is populated asynchronously. Poll the live DOM instead of
                // taking one snapshot too early. Keep polling after the deadline appears so
                // promocode/minimum-deposit fields that render slightly later are not missed.
                DateTime renderDeadline = DateTime.UtcNow.AddSeconds(10);
                while (DateTime.UtcNow < renderDeadline)
                {
                    string renderedData = await DevToolsEvaluateAsync(websocketUrl, expression, token);
                    string metadata = await DevToolsEvaluateAsync(websocketUrl, metadataExpression, token);

                    if (!string.IsNullOrWhiteSpace(renderedData) || !string.IsNullOrWhiteSpace(metadata))
                    {
                        string promo = PromoCodeFromText(renderedData);
                        if (promo == "-") promo = PromoCodeFromText(metadata);
                        if (promo != "-") best.PromoCode = promo;

                        string promoForDeposit = promo != "-" ? promo : best.PromoCode;
                        string minDeposit = MinimumDepositFromText(renderedData, promoForDeposit);
                        if (minDeposit == "-") minDeposit = MinimumDepositFromText(metadata, promoForDeposit);
                        if (minDeposit != "-") best.MinimumDeposit = minDeposit;

                        // The visible TIME TO COMPLETION timer is authoritative. Never use
                        // hidden DOM state for the deadline because it previously caused
                        // unrelated timestamps to be displayed as giveaway deadlines.
                        string deadline = DeadlineFromRenderedCompletionText(renderedData);
                        if (deadline == "-") deadline = DeadlineFromVisibleText(renderedData);
                        if (deadline != "-") best.Deadline = deadline;

                        if (best.Deadline != "-" && best.PromoCode != "-" && best.MinimumDeposit != "-")
                        {
                            StoreRenderedCache(url, best);
                            ActivityLog.Write("RENDER", "Metadata complete: " + url);
                            return CloneRenderedFields(best);
                        }
                    }
                    await Task.Delay(500, token);
                }
                StoreRenderedCache(url, best);
                ActivityLog.Write("RENDER", "Finished with partial metadata: " + url);
                return CloneRenderedFields(best);
            }
            catch (Exception ex)
            {
                ActivityLog.Write(ex is OperationCanceledException ? "TIMEOUT" : "ERROR", "Render stopped: " + url + " — " + ex.Message);
                return CloneRenderedFields(best);
            }
            finally
            {
                try
                {
                    KillProcessTree(process);
                    RemoveRenderProfile(profileDir);
                }
                catch (Exception ex) { ActivityLog.Write("ERROR", "Browser cleanup failed; further launches blocked: " + ex.Message); }
                finally
                {
                    if (localClient != null) localClient.Dispose();
                    renderCts.Dispose();
                    RenderFallbackThrottle.Release();
                }
            }
        }

        private static string AbsoluteDeadlineFromHtml(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return "-";

            Match iso = HiddenAbsoluteDeadlineRegex.Match(html);
            if (iso.Success)
            {
                DateTime dt;
                string raw = WebUtility.HtmlDecode(iso.Groups[1].Value ?? "");
                if (DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AllowWhiteSpaces | System.Globalization.DateTimeStyles.AssumeLocal, out dt) ||
                    DateTime.TryParse(raw, out dt))
                {
                    if (dt.Date >= DateTime.Now.Date.AddDays(-1))
                        return dt.ToString("MMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture).ToUpperInvariant();
                }
            }

            Match unix = HiddenUnixDeadlineRegex.Match(html);
            if (unix.Success)
            {
                long stamp;
                if (long.TryParse(unix.Groups[1].Value, out stamp))
                {
                    if (stamp > 9999999999L) stamp /= 1000L;
                    try
                    {
                        DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                        DateTime dt = epoch.AddSeconds(stamp).ToLocalTime();
                        if (dt.Year >= DateTime.Now.Year - 1 && dt.Year <= DateTime.Now.Year + 5 && dt.Date >= DateTime.Now.Date.AddDays(-1))
                            return dt.ToString("MMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture).ToUpperInvariant();
                    }
                    catch { }
                }
            }

            Match seconds = RemainingSecondsRegex.Match(html);
            if (seconds.Success)
            {
                int sec;
                if (int.TryParse(seconds.Groups[1].Value, out sec) && sec > 0)
                {
                    DateTime dt = DateTime.Now.AddSeconds(sec);
                    return dt.ToString("MMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture).ToUpperInvariant();
                }
            }

            string hoursDeadline = DeadlineFromRawHtml(html);
            if (hoursDeadline != "-") return hoursDeadline;

            return "-";
        }

        private static string DeadlineAsDate(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "-";
            string value = Regex.Replace(raw, @"\s+", " ").Trim().Trim(' ', ':', '|');
            if (value == "" || value == "-") return "-";

            double bareHours;
            if (Regex.IsMatch(value.Replace(',', '.'), @"^\d+(?:\.\d+)?$") &&
                TryParseHoursNumber(value, out bareHours))
            {
                return DeadlineFromHours(bareHours);
            }

            TimeSpan countdown;
            if (TryParseCountdown(value, out countdown))
            {
                DateTime end = DateTime.Now.Add(countdown);
                return end.ToString("MMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture).ToUpperInvariant();
            }

            DateTime absolute;
            if (DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AllowWhiteSpaces | System.Globalization.DateTimeStyles.AssumeLocal, out absolute) ||
                DateTime.TryParse(value, out absolute))
            {
                return absolute.ToString("MMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture).ToUpperInvariant();
            }

            // The visible field can be a live timer format we do not recognize yet.
            // Keep the giveaway Active, but do not show the raw timer in the Deadline column.
            return "-";
        }

        private static bool IsClearlyInvalidPromoCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return true;
            string upper = code.Trim().ToUpperInvariant();
            return upper == "GET" || upper == "CODE" || upper == "PROMO" || upper == "PROMOCODE" ||
                   upper == "SPECIAL" || upper == "COPY" || upper == "HERE" || upper == "USE" ||
                   upper == "ENTER" || upper == "FOR" || upper == "WITH" || upper == "USING" ||
                   upper == "THE" || upper == "EVERY" || upper == "SKIN" || upper == "CLUB" ||
                   upper == "BONUS" || upper == "PRIZE" || upper == "POOL" || upper == "DISCOUNT";
        }

        private static string NormalizePromoCode(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "-";
            string code = WebUtility.UrlDecode(WebUtility.HtmlDecode(raw)).Trim().Trim('"', '\'', ' ', '\t', '\r', '\n');
            if (code.Length < 3 || code.Length > 64 || IsClearlyInvalidPromoCode(code)) return "-";
            if (!Regex.IsMatch(code, @"^[A-Za-z0-9][A-Za-z0-9_\-]{2,63}$")) return "-";
            return code;
        }

        private static string PromoCodeFromText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "-";
            string decoded = WebUtility.HtmlDecode(text);

            // Most reliable source: the actual participation instruction:
            // "Replenish your balance from $8 with promo code LP-..."
            // This deliberately comes before generic Skin.Club promo links because
            // the page can also contain a separate "Special Prize" discount code.
            // Raw HTML puts tags between the words and the code ("promo code <br> <b>LP-X</b>"),
            // so also try the text with tags removed.
            string flat = Regex.Replace(decoded, @"<[^>]+>", " ");
            foreach (string source in new string[] { flat, decoded })
            {
                Match step = ParticipationStepRegex.Match(source);
                if (!step.Success) continue;
                string code = NormalizePromoCode(step.Groups[2].Value);
                if (code != "-") return code;
            }

            // Fallback to Skin.Club promo links, but prefer the giveaway
            // participation code (normally LP-...) if multiple promo links exist.
            string firstQueryCode = "-";
            foreach (Match query in PromoQueryRegex.Matches(decoded))
            {
                string code = NormalizePromoCode(query.Groups[1].Value);
                if (code == "-") continue;
                // Skip the separate "discount promocode / special bonus" prize code; only the
                // STEP 2 entry code counts as the giveaway promocode.
                string before = decoded.Substring(Math.Max(0, query.Index - 500), Math.Min(500, query.Index));
                if (Regex.IsMatch(before, @"discount\s+promo|special\s+(?:bonus|prize)", RegexOptions.IgnoreCase)) continue;
                if (code.StartsWith("LP-", StringComparison.OrdinalIgnoreCase)) return code;
                if (firstQueryCode == "-") firstQueryCode = code;
            }
            if (firstQueryCode != "-") return firstQueryCode;

            // Legacy fallback.  Require a structured value; plain UI words such
            // as SKIN must never be accepted as promocodes.
            foreach (Match m in PromoCodeRegex.Matches(decoded))
            {
                string code = NormalizePromoCode(m.Groups[1].Value);
                if (code == "-") continue;
                bool looksStructured = code.StartsWith("LP-", StringComparison.OrdinalIgnoreCase) ||
                    Regex.IsMatch(code, @"[0-9_\-]");
                if (!looksStructured) continue;
                return code;
            }
            return "-";
        }

        private static string NormalizeDepositAmount(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "-";
            string value = Regex.Replace(WebUtility.HtmlDecode(raw), @"\s+", " ").Trim();
            if (string.IsNullOrWhiteSpace(value)) return "-";
            return value.ToUpperInvariant();
        }

        private static string MinimumDepositFromText(string text)
        {
            return MinimumDepositFromText(text, null);
        }

        private static string MinimumDepositFromText(string text, string knownPromoCode)
        {
            if (string.IsNullOrWhiteSpace(text)) return "-";
            string decoded = WebUtility.HtmlDecode(text);

            // Most reliable source: the same Step 2 instruction that contains the
            // participation promocode. This keeps prize-pool dollar values out.
            Match step = ParticipationStepRegex.Match(decoded);
            if (step.Success)
            {
                string amount = NormalizeDepositAmount(step.Groups[1].Value);
                if (amount != "-") return amount;
            }

            Match m = MinimumDepositRegex.Match(decoded);
            if (!m.Success) m = ParticipationDepositRegex.Match(decoded);
            if (!m.Success) m = JsonMinimumDepositRegex.Match(decoded);
            if (!m.Success) m = FlexibleDepositBeforePromoRegex.Match(decoded);
            if (!m.Success) m = FlexiblePromoBeforeDepositRegex.Match(decoded);
            if (m.Success)
            {
                string value = NormalizeDepositAmount(m.Groups[1].Value);
                if (value != "-") return value;
            }

            // Some creator templates use different wording but still render the
            // promocode and minimum deposit beside one another. Restrict this
            // fallback to a window around the already-verified participation code.
            if (!string.IsNullOrWhiteSpace(knownPromoCode) && knownPromoCode != "-")
            {
                int promoIndex = decoded.IndexOf(knownPromoCode, StringComparison.OrdinalIgnoreCase);
                if (promoIndex >= 0)
                {
                    int start = Math.Max(0, promoIndex - 700);
                    int length = Math.Min(decoded.Length - start, 1400);
                    string nearby = decoded.Substring(start, length);

                    // Keep the older explicit phrasing first.
                    Match amountNearPromo = Regex.Match(nearby,
                        @"(?:from|starting\s+from|at\s+least|minimum(?:\s+deposit)?(?:\s+of)?|more\s+than|over|for|by)\s*((?:US[$]|CA[$]|AU[$]|[$€£])?\s*[0-9]+(?:[.,][0-9]{1,2})?\s*(?:USD|EUR|GBP|USDT|RUB|PLN|BRL|ARS|UAH|BDT|[$€£])?)",
                        RegexOptions.IgnoreCase);
                    if (amountNearPromo.Success && DepositContextRegex.IsMatch(nearby))
                    {
                        string value = NormalizeDepositAmount(amountNearPromo.Groups[1].Value);
                        if (value != "-") return value;
                    }

                    // Final safe fallback: choose the monetary amount whose local
                    // context looks like a deposit/balance instruction and that is
                    // closest to the verified promo code. This covers templates such
                    // as "refill balance $5", "deposit 10$", and translated labels.
                    int promoLocal = promoIndex - start;
                    Match bestMoney = null;
                    int bestDistance = int.MaxValue;
                    foreach (Match money in MoneyAmountRegex.Matches(nearby))
                    {
                        int ctxStart = Math.Max(0, money.Index - 180);
                        int ctxLength = Math.Min(nearby.Length - ctxStart, money.Length + 360);
                        string context = nearby.Substring(ctxStart, ctxLength);
                        if (!DepositContextRegex.IsMatch(context)) continue;

                        int distance = Math.Abs((money.Index + money.Length / 2) - promoLocal);
                        if (distance < bestDistance)
                        {
                            bestDistance = distance;
                            bestMoney = money;
                        }
                    }
                    if (bestMoney != null)
                    {
                        string value = NormalizeDepositAmount(bestMoney.Value);
                        if (value != "-") return value;
                    }
                }
            }
            return "-";
        }

        public static ParseResult ParsePage(string html)
        {
            string text = HtmlToText(html);

            int? remaining = null;
            int? total = null;
            Match tm = TicketLineRegex.Match(text);
            if (!tm.Success) tm = TicketFallbackRegex.Match(text);
            if (tm.Success)
            {
                remaining = ParseInt(tm.Groups[1].Value);
                total = ParseInt(tm.Groups[2].Value);
            }
            string ticket = (remaining.HasValue && total.HasValue)
                ? string.Format("{0} / {1}", remaining.Value, total.Value)
                : "-";
            string decodedHtml = WebUtility.HtmlDecode(html);
            string promoCode = PromoCodeFromText(decodedHtml);
            if (promoCode == "-") promoCode = PromoCodeFromText(text);
            string minimumDeposit = MinimumDepositFromText(text, promoCode);
            if (minimumDeposit == "-") minimumDeposit = MinimumDepositFromText(decodedHtml, promoCode);

            string deadline = "-";
            bool explicitEnded = false;
            bool hasLiveDeadlineSignal = false;

            // Prefer the exact current visible timer format before any legacy parser.
            string visibleDeadline = DeadlineFromRenderedCompletionText(text);
            if (visibleDeadline == "-") visibleDeadline = DeadlineFromVisibleText(text);
            if (string.Equals(visibleDeadline, "Ended", StringComparison.OrdinalIgnoreCase))
            {
                explicitEnded = true;
                deadline = "Ended";
            }
            else if (visibleDeadline != "-")
            {
                hasLiveDeadlineSignal = true;
                deadline = visibleDeadline;
            }
            else
            {
                // On the current SkinClub layout the visible completion value is injected
                // by JavaScript. If the static page already contains the timer label, do
                // NOT trust hidden endTime/deadline/remainingTime values: some of those
                // belong to unrelated page state and were the source of the wrong dates.
                bool currentCompletionLayout = Regex.IsMatch(text, @"time\s*to\s*completion", RegexOptions.IgnoreCase);
                if (currentCompletionLayout)
                {
                    hasLiveDeadlineSignal = true;
                }
                else
                {
                    // Legacy creator pages may still expose a real absolute deadline.
                    deadline = AbsoluteDeadlineFromHtml(html);
                    if (deadline != "-") hasLiveDeadlineSignal = true;
                    if (!hasLiveDeadlineSignal && DeadlineLineRegex.IsMatch(text))
                        hasLiveDeadlineSignal = true;
                }
            }

            bool soldOut = remaining.HasValue && remaining.Value <= 0;
            bool hasTickets = remaining.HasValue && remaining.Value > 0;

            string status;
            if (explicitEnded || soldOut) status = "ended";
            else if (hasTickets || hasLiveDeadlineSignal) status = "active";
            else status = "unknown";

            return new ParseResult
            {
                Status = status,
                Ticket = ticket,
                PromoCode = promoCode,
                MinimumDeposit = minimumDeposit,
                Deadline = explicitEnded ? "Ended" : deadline,
                Remaining = remaining,
                Total = total
            };
        }

        public static async Task<GiveawayItem> ValidateAsync(Candidate c, SemaphoreSlim throttle)
        {
            await throttle.WaitAsync();
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
                    using (HttpResponseMessage r = await Client.GetAsync(c.Url))
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
                    html = await r.Content.ReadAsStringAsync();
                    }
                    ParseResult p = ParsePage(html);
                    item.Status = p.Status;
                    item.Ticket = p.Ticket;
                    item.PromoCode = p.PromoCode;
                    item.MinimumDeposit = p.MinimumDeposit;
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
                        if (item.PromoCode == "-" && !string.IsNullOrWhiteSpace(prior.PromoCode) && prior.PromoCode != "-")
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
                    bool allowRenderedMetadata = missingMetadata && (!generatedProbe || staticGiveawaySignal);

                    // Promo/minimum-deposit values may also be client-rendered on already-ended
                    // giveaways. Saved/social links can still use the rendered metadata path.
                    if (allowRenderedMetadata || needsRenderedDeadline)
                    {
                        RenderedFields rendered = await FetchRenderedFieldsAsync(item.Url);
                        item.RenderedAt = DateTime.UtcNow.ToString("o");
                        if (rendered.PromoCode != "-") item.PromoCode = rendered.PromoCode;
                        if (rendered.MinimumDeposit != "-") item.MinimumDeposit = rendered.MinimumDeposit;

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
            finally { throttle.Release(); }
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
                        // Normal History entries stay persisted and are not revalidated on
                        // every launch. Joined entries are different: older saved items may
                        // predate the promocode/minimum-deposit fields, so allow a metadata
                        // backfill only while one of those fields is still missing.
                        bool missingJoinedMetadata = i.Joined &&
                            (string.IsNullOrWhiteSpace(i.PromoCode) || i.PromoCode == "-" ||
                             string.IsNullOrWhiteSpace(i.MinimumDeposit) || i.MinimumDeposit == "-");
                        if (!missingJoinedMetadata) continue;
                    }
                    cs.Add(new Candidate(i.Creator, i.Url, string.IsNullOrWhiteSpace(i.Source) ? "saved" : i.Source));
                }
                KnownItems = IndexKnown(data);
                SemaphoreSlim throttle = new SemaphoreSlim(16, 16);
                List<Task<GiveawayItem>> tasks = new List<Task<GiveawayItem>>();
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
                catch { }
            }

            foreach (string host in hosts)
            {
                string creator = InferCreator("https://" + host + "/", null);
                foreach (string slug in FastProbeSlugs())
                    candidates.Add(new Candidate(creator, BuildProbeUrl(host, slug), "discovered-domain-probe"));
            }
        }

        private static async Task<string> GetStringSafeAsync(string url)
        {
            ActivityLog.Write("SOURCE", "Fetching " + url);
            try
            {
                using (HttpResponseMessage r = await Client.GetAsync(url))
                {
                    if (!r.IsSuccessStatusCode)
                    { ActivityLog.Write("ERROR", "HTTP " + (int)r.StatusCode + " fetching " + url); return ""; }
                    string content = await r.Content.ReadAsStringAsync();
                    ActivityLog.Write("SOURCE", "Received " + content.Length + " characters from " + url);
                    return content;
                }
            }
            catch (Exception ex) { ActivityLog.Write("ERROR", "Source failed: " + url + " — " + ex.Message); return ""; }
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
                ActivityLog.Write("SCAN", "Deep Search started: Telegram, YouTube, partner socials and web search");
                Task<List<Candidate>> telegram = DiscoverTelegramAsync();
                Task<List<Candidate>> youtube = DiscoverYoutubeAsync();
                Task<List<Candidate>> partnerSocials = DiscoverPartnerSocialsAsync();
                Task<List<Candidate>> web = DiscoverWebAsync();

                List<Candidate> candidates = GeneratedCandidates();
                candidates.AddRange(await telegram);
                candidates.AddRange(await youtube);
                candidates.AddRange(await partnerSocials);
                candidates.AddRange(await web);
                foreach (GiveawayItem i in data.Items)
                    candidates.Add(new Candidate(i.Creator, i.Url, string.IsNullOrWhiteSpace(i.Source) ? "saved" : i.Source));

                // Any .club domain discovered from search, Telegram, YouTube, or an
                // existing saved item becomes probeable automatically. This prevents
                // Deep Search from depending only on the hard-coded creator list.
                AddDiscoveredDomainProbes(candidates);

                Dictionary<string, Candidate> dedup = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);
                foreach (Candidate c in candidates)
                {
                    if (string.IsNullOrWhiteSpace(c.Url)) continue;
                    string key = GiveawayKey(c.Url);
                    if (key.Length == 0) continue;
                    if (!dedup.ContainsKey(key))
                    {
                        dedup[key] = new Candidate(c.Creator, CleanDiscoveredUrl(c.Url), c.Source);
                    }
                }
                candidates = dedup.Values.ToList();
                ActivityLog.Write("SCAN", "Discovery complete; validating " + candidates.Count + " unique candidate pages");
                HashSet<string> known = new HashSet<string>(data.Items.Select(delegate(GiveawayItem x) { return GiveawayKey(x.Url); }), StringComparer.OrdinalIgnoreCase);
                KnownItems = IndexKnown(data);

                List<GiveawayItem> useful = new List<GiveawayItem>();
                SemaphoreSlim throttle = new SemaphoreSlim(12, 12);
                const int chunkSize = 180;
                for (int start = 0; start < candidates.Count; start += chunkSize)
                {
                    List<Candidate> batch = candidates.Skip(start).Take(chunkSize).ToList();
                    List<Task<GiveawayItem>> tasks = new List<Task<GiveawayItem>>();
                    foreach (Candidate c in batch) tasks.Add(ValidateAsync(c, throttle));
                    GiveawayItem[] updates = await Task.WhenAll(tasks);
                    foreach (GiveawayItem u in updates)
                    {
                        string key = GiveawayKey(u.Url);
                        if (u.Status == "active" || u.Status == "ended" || known.Contains(key)) useful.Add(u);
                    }
                }
                MergeItems(data, useful);
                data.LastScan = DateTime.UtcNow.ToString("o");
                data.LastDeepScan = data.LastScan;
                DataStore.Save(data);
                ActivityLog.Write("SCAN", "Deep Search complete; " + useful.Count + " useful results saved");
                return data;
            }
            finally { KnownItems = null; ScanLock.Release(); }
        }

        public static async Task<GiveawayItem> ValidateOneAsync(string creator, string url)
        {
            if (!DirectUrlRegex.IsMatch(url)) throw new ArgumentException("Use a direct dated creator giveaway URL, for example https://creator.club/010926/");
            SemaphoreSlim throttle = new SemaphoreSlim(1, 1);
            return await ValidateAsync(new Candidate(creator, url, "manual"), throttle);
        }

        public static void MergeOne(AppData data, GiveawayItem item)
        {
            MergeItems(data, new GiveawayItem[] { item });
            DataStore.Save(data);
        }
    }

    // Shared palette and drawing helpers for the whole interface.
    internal static class Ui
    {
        public static readonly Color Bg = Color.FromArgb(11, 15, 23);
        public static readonly Color Surface = Color.FromArgb(17, 22, 33);
        public static readonly Color Surface2 = Color.FromArgb(22, 29, 43);
        public static readonly Color Surface3 = Color.FromArgb(29, 37, 54);
        public static readonly Color Line = Color.FromArgb(34, 43, 60);
        public static readonly Color LineStrong = Color.FromArgb(52, 64, 88);
        public static readonly Color Text = Color.FromArgb(233, 237, 245);
        public static readonly Color TextSoft = Color.FromArgb(178, 188, 208);
        public static readonly Color Muted = Color.FromArgb(125, 138, 162);
        public static readonly Color Accent = Color.FromArgb(99, 102, 241);
        public static readonly Color AccentHover = Color.FromArgb(129, 140, 248);
        public static readonly Color AccentDark = Color.FromArgb(79, 70, 229);
        public static readonly Color Success = Color.FromArgb(52, 211, 153);
        public static readonly Color Warning = Color.FromArgb(251, 191, 36);
        public static readonly Color Danger = Color.FromArgb(248, 113, 113);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hwnd, string subAppName, string subIdList);

        // Dark scroll bars (Windows 10 1809+); harmless no-op on older systems.
        public static void DarkScrollBars(Control control)
        {
            EventHandler apply = delegate { try { SetWindowTheme(control.Handle, "DarkMode_Explorer", null); } catch { } };
            if (control.IsHandleCreated) apply(control, EventArgs.Empty);
            control.HandleCreated += apply;
        }

        public static void DarkTitleBar(IntPtr handle)
        {
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(handle, 19, ref on, 4);
            }
            catch { }
        }

        public static GraphicsPath RoundPath(Rectangle r, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = Math.Max(2, radius * 2);
            d = Math.Min(d, Math.Min(r.Width, r.Height));
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static Color Mix(Color a, Color b, double t)
        {
            if (t < 0) t = 0;
            if (t > 1) t = 1;
            return Color.FromArgb(
                (int)Math.Round(a.R * (1.0 - t) + b.R * t),
                (int)Math.Round(a.G * (1.0 - t) + b.G * t),
                (int)Math.Round(a.B * (1.0 - t) + b.B * t));
        }

        public static Rectangle Draw3D(Graphics g, Size size, Color top, Color bottom, Color border, bool pressed)
        {
            Rectangle shadowRect = new Rectangle(2, 5, Math.Max(1, size.Width - 5), Math.Max(1, size.Height - 7));
            using (GraphicsPath sp = RoundPath(shadowRect, 10))
            using (SolidBrush sb = new SolidBrush(Color.FromArgb(90, 0, 0, 0)))
                g.FillPath(sb, sp);
            Rectangle face = new Rectangle(1, pressed ? 3 : 1, Math.Max(1, size.Width - 4), Math.Max(1, size.Height - 7));
            using (GraphicsPath path = RoundPath(face, 10))
            using (LinearGradientBrush fill = new LinearGradientBrush(face, top, bottom, LinearGradientMode.Vertical))
            using (Pen pen = new Pen(border))
            {
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }
            return face;
        }

        public const TextFormatFlags Single = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
    }

    public enum UiButtonKind { Ghost, Primary, Tab }

    // Raised "3D" button: soft drop shadow, vertical gradient face, and a press-down offset.
    // Used for every action and for the Active / Joined / History / Logs navigation.
    public class UiButton : Button
    {
        private static readonly Font BadgeFont = new Font("Segoe UI Semibold", 8F);
        private string badge;
        private bool selected;
        private bool hover;
        private bool pressed;
        public UiButtonKind Kind { get; private set; }
        public Color AccentColor { get; set; }

        public UiButton(UiButtonKind kind)
        {
            Kind = kind;
            AccentColor = Ui.Accent;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            Font = new Font("Segoe UI Semibold", 9.5F);
            ForeColor = Ui.Text;
            Height = 44;
            Margin = new Padding(0);
        }

        public static UiButton Create(string text, UiButtonKind kind, int width)
        {
            UiButton b = new UiButton(kind);
            b.Text = text;
            if (kind != UiButtonKind.Tab || width > 0) b.Width = width;
            return b;
        }

        public string Badge { get { return badge; } set { badge = value; FitToContent(); Invalidate(); } }
        public bool Selected { get { return selected; } set { if (selected == value) return; selected = value; Invalidate(); } }

        private int BadgeWidth()
        {
            return Math.Max(24, TextRenderer.MeasureText(badge ?? "", BadgeFont, new Size(1000, 40), TextFormatFlags.NoPadding).Width + 14);
        }

        public void FitToContent()
        {
            if (Kind != UiButtonKind.Tab) return;
            int w = 40 + TextRenderer.MeasureText(Text ?? "", Font, new Size(1000, 40), TextFormatFlags.NoPadding).Width;
            if (!string.IsNullOrEmpty(badge)) w += 10 + BadgeWidth();
            Width = w;
        }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); FitToContent(); Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent == null ? Ui.Bg : Parent.BackColor);

            Color top, bottom, border, fore = Ui.Text;
            if (Kind == UiButtonKind.Primary)
            {
                top = Color.FromArgb(126, 112, 255);
                bottom = Color.FromArgb(86, 72, 215);
                border = Color.FromArgb(155, 145, 255);
                fore = Color.White;
            }
            else if (selected)
            {
                top = Ui.Mix(Ui.Surface3, AccentColor, 0.62);
                bottom = Ui.Mix(Ui.Bg, AccentColor, 0.30);
                border = Ui.Mix(AccentColor, Color.White, 0.14);
            }
            else
            {
                top = Color.FromArgb(43, 55, 82);
                bottom = Color.FromArgb(24, 33, 52);
                border = Color.FromArgb(66, 82, 112);
                if (Kind == UiButtonKind.Tab) fore = Ui.TextSoft;
            }
            if (hover && Enabled)
            {
                top = Ui.Mix(top, Color.White, 0.10);
                bottom = Ui.Mix(bottom, Color.White, 0.05);
            }
            if (!Enabled)
            {
                top = Ui.Mix(top, Color.FromArgb(80, 84, 94), 0.55);
                bottom = Ui.Mix(bottom, Color.FromArgb(58, 62, 72), 0.55);
                border = Ui.Mix(border, Color.Gray, 0.55);
                fore = Color.FromArgb(145, 153, 168);
            }

            Rectangle face = Ui.Draw3D(g, new Size(Width, Height), top, bottom, border, pressed);
            if (Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(face, -5, -5), fore, bottom);

            if (Kind == UiButtonKind.Tab && !string.IsNullOrEmpty(badge))
            {
                int textWidth = TextRenderer.MeasureText(Text ?? "", Font, new Size(1000, 40), TextFormatFlags.NoPadding).Width;
                int bw = BadgeWidth();
                int total = textWidth + 10 + bw;
                int x = face.X + (face.Width - total) / 2;
                TextRenderer.DrawText(g, Text, Font, new Rectangle(x, face.Y, textWidth + 2, face.Height), fore,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                Rectangle pill = new Rectangle(x + textWidth + 10, face.Y + (face.Height - 18) / 2, bw, 18);
                using (GraphicsPath path = Ui.RoundPath(pill, 9))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(selected ? 90 : 120, 8, 12, 20)))
                    g.FillPath(b, path);
                TextRenderer.DrawText(g, badge, BadgeFont, pill, selected ? Color.White : Ui.TextSoft,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            }
            else
            {
                TextRenderer.DrawText(g, Text, Font, face, fore,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            }
        }
    }

    // Dark text input with a rounded border, focus ring, native placeholder and optional search icon.
    public class TextField : Panel
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        private bool focused;
        private readonly bool searchIcon;
        private string placeholder = "";
        private Rectangle clearRect = Rectangle.Empty;
        public TextBox Input { get; private set; }
        // When true the field leaves room under it for the 3D buttons' shadow so heights line up.
        public bool ReserveShadow { get; set; }

        public TextField(bool withSearchIcon)
        {
            searchIcon = withSearchIcon;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = 36;
            BackColor = Ui.Bg;
            Input = new TextBox();
            Input.BorderStyle = BorderStyle.None;
            Input.BackColor = Ui.Surface2;
            Input.ForeColor = Ui.Text;
            Input.Font = new Font("Segoe UI", 10F);
            Input.Enter += delegate { focused = true; Invalidate(); };
            Input.Leave += delegate { focused = false; Invalidate(); };
            Input.TextChanged += delegate { Invalidate(); };
            Input.HandleCreated += delegate { ApplyPlaceholder(); };
            Controls.Add(Input);
            Cursor = Cursors.IBeam;
        }

        public string Placeholder { get { return placeholder; } set { placeholder = value ?? ""; ApplyPlaceholder(); } }

        private void ApplyPlaceholder()
        {
            if (Input.IsHandleCreated) SendMessage(Input.Handle, 0x1501, new IntPtr(1), placeholder);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Input == null) return;
            int left = searchIcon ? 38 : 14;
            int right = 34;
            Input.Left = left;
            Input.Width = Math.Max(10, Width - left - right);
            int faceTop = ReserveShadow ? 1 : 0, faceHeight = ReserveShadow ? Height - 7 : Height;
            Input.Top = faceTop + Math.Max(0, (faceHeight - Input.Height) / 2);
            clearRect = new Rectangle(Width - 30, faceTop + (faceHeight - 22) / 2, 22, 22);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (Input.TextLength > 0 && clearRect.Contains(e.Location)) { Input.Clear(); Input.Focus(); return; }
            Input.Focus();
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent == null ? Ui.Bg : Parent.BackColor);
            Rectangle r = ReserveShadow ? new Rectangle(0, 1, Width - 1, Height - 8) : new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Ui.RoundPath(r, 10))
            using (SolidBrush b = new SolidBrush(Ui.Surface2))
            using (Pen p = new Pen(focused ? Ui.Accent : Ui.Line, focused ? 1.5F : 1F))
            {
                g.FillPath(b, path);
                g.DrawPath(p, path);
            }
            Color icon = focused ? Ui.AccentHover : Ui.Muted;
            if (searchIcon)
            {
                int cy = r.Y + r.Height / 2;
                using (Pen p = new Pen(icon, 1.7F))
                {
                    p.StartCap = LineCap.Round;
                    p.EndCap = LineCap.Round;
                    g.DrawEllipse(p, 14, cy - 7, 9, 9);
                    g.DrawLine(p, 21, cy + 1, 25, cy + 5);
                }
            }
            if (Input.TextLength > 0)
            {
                using (SolidBrush b = new SolidBrush(Ui.Surface3))
                    g.FillEllipse(b, clearRect.X + 2, clearRect.Y + 2, 18, 18);
                using (Pen p = new Pen(Ui.TextSoft, 1.5F))
                {
                    p.StartCap = LineCap.Round;
                    p.EndCap = LineCap.Round;
                    g.DrawLine(p, clearRect.X + 8, clearRect.Y + 8, clearRect.X + 14, clearRect.Y + 14);
                    g.DrawLine(p, clearRect.X + 14, clearRect.Y + 8, clearRect.X + 8, clearRect.Y + 14);
                }
            }
        }
    }

    internal class DarkMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Ui.Surface2; } }
        public override Color ImageMarginGradientBegin { get { return Ui.Surface2; } }
        public override Color ImageMarginGradientMiddle { get { return Ui.Surface2; } }
        public override Color ImageMarginGradientEnd { get { return Ui.Surface2; } }
        public override Color MenuBorder { get { return Ui.LineStrong; } }
        public override Color MenuItemBorder { get { return Ui.Surface3; } }
        public override Color MenuItemSelected { get { return Ui.Surface3; } }
        public override Color MenuItemSelectedGradientBegin { get { return Ui.Surface3; } }
        public override Color MenuItemSelectedGradientEnd { get { return Ui.Surface3; } }
        public override Color MenuItemPressedGradientBegin { get { return Ui.Surface3; } }
        public override Color MenuItemPressedGradientEnd { get { return Ui.Surface3; } }
    }

    // Replaces the white system combo box: shows the current value and opens a dark menu.
    public class UiPicker : Control
    {
        private bool hover;
        private int selectedIndex = -1;
        private readonly List<object> items = new List<object>();
        private ContextMenuStrip menu;
        public event EventHandler SelectedIndexChanged;

        public UiPicker()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            Height = 44;
            Font = new Font("Segoe UI", 9.5F);
            TabStop = true;
        }

        public List<object> Items { get { return items; } }
        public object SelectedItem { get { return selectedIndex >= 0 && selectedIndex < items.Count ? items[selectedIndex] : null; } }
        public int SelectedIndex
        {
            get { return selectedIndex; }
            set
            {
                if (value == selectedIndex) return;
                selectedIndex = value;
                Invalidate();
                if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space || e.KeyCode == Keys.Down) { ShowMenu(); e.Handled = true; }
            base.OnKeyDown(e);
        }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowMenu(); base.OnMouseDown(e); }

        private void ShowMenu()
        {
            // The previous menu is released only now: disposing it while its Click handler is still pending crashes.
            if (menu != null) menu.Dispose();
            menu = new ContextMenuStrip();
            menu.Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors());
            menu.BackColor = Ui.Surface2;
            menu.ForeColor = Ui.Text;
            menu.Font = Font;
            menu.ShowImageMargin = false;
            menu.Padding = new Padding(4);
            for (int i = 0; i < items.Count; i++)
            {
                int index = i;
                ToolStripMenuItem item = new ToolStripMenuItem(items[i].ToString());
                item.ForeColor = i == selectedIndex ? Ui.AccentHover : Ui.Text;
                item.Padding = new Padding(6, 5, 6, 5);
                item.Click += delegate { SelectedIndex = index; };
                menu.Items.Add(item);
            }
            menu.Show(this, new Point(0, Height - 2));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent == null ? Ui.Bg : Parent.BackColor);
            Color top = Color.FromArgb(43, 55, 82), bottom = Color.FromArgb(24, 33, 52);
            if (hover) { top = Ui.Mix(top, Color.White, 0.10); bottom = Ui.Mix(bottom, Color.White, 0.05); }
            Rectangle face = Ui.Draw3D(g, new Size(Width, Height), top, bottom, Color.FromArgb(66, 82, 112), false);
            object current = SelectedItem;
            TextRenderer.DrawText(g, current == null ? "" : current.ToString(), Font, new Rectangle(face.X + 14, face.Y, face.Width - 40, face.Height), Ui.Text, Ui.Single);
            using (Pen p = new Pen(Ui.TextSoft, 1.6F))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                int cx = face.Right - 16, cy = face.Y + face.Height / 2;
                g.DrawLine(p, cx - 4, cy - 2, cx, cy + 2);
                g.DrawLine(p, cx, cy + 2, cx + 4, cy - 2);
            }
        }
    }
    // Thin progress strip shown above the table while a scan is running.
    public class ScanStrip : Control
    {
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private double position;
        private bool active;

        public ScanStrip()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Height = 2;
            timer.Interval = 30;
            timer.Tick += delegate { position += 0.018; if (position > 1.0) position = 0; Invalidate(); };
        }

        public bool Active
        {
            get { return active; }
            set
            {
                active = value;
                if (value) timer.Start(); else timer.Stop();
                position = 0;
                Invalidate();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Ui.Line);
            if (!active) return;
            int w = Math.Max(40, Width / 4);
            int x = (int)((Width + w) * position) - w;
            using (LinearGradientBrush b = new LinearGradientBrush(new Rectangle(x - 1, 0, w + 2, Math.Max(1, Height)),
                Color.FromArgb(0, Ui.Accent), Ui.AccentHover, LinearGradientMode.Horizontal))
                e.Graphics.FillRectangle(b, x, 0, w, Height);
        }
    }

    public class AddLinkForm : Form
    {
        public string CreatorName { get { return creatorField.Input.Text.Trim(); } }
        public string GiveawayUrl { get { return urlField.Input.Text.Trim(); } }
        private readonly TextField creatorField;
        private readonly TextField urlField;

        public AddLinkForm()
        {
            Text = "Add giveaway";
            ClientSize = new Size(560, 330);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Ui.Surface;
            ForeColor = Ui.Text;
            Font = new Font("Segoe UI", 9F);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            Label title = new Label();
            title.Text = "Add a giveaway";
            title.SetBounds(28, 24, 504, 32);
            title.Font = new Font("Segoe UI Semibold", 15F);
            title.ForeColor = Ui.Text;
            Controls.Add(title);

            Label sub = new Label();
            sub.Text = "Paste a direct, dated creator giveaway link. The page is checked before it is saved.";
            sub.SetBounds(28, 60, 504, 22);
            sub.ForeColor = Ui.Muted;
            Controls.Add(sub);

            Controls.Add(MakeLabel("CREATOR  (OPTIONAL)", 108));
            creatorField = new TextField(false);
            creatorField.BackColor = Ui.Surface;
            creatorField.SetBounds(28, 130, 504, 38);
            creatorField.Placeholder = "e.g. Jon Sandman";
            Controls.Add(creatorField);

            Controls.Add(MakeLabel("GIVEAWAY LINK", 184));
            urlField = new TextField(false);
            urlField.BackColor = Ui.Surface;
            urlField.SetBounds(28, 206, 504, 38);
            urlField.Placeholder = "https://creator.club/010926/";
            Controls.Add(urlField);

            UiButton cancel = UiButton.Create("Cancel", UiButtonKind.Ghost, 96);
            cancel.Location = new Point(28 + 504 - 96 - 12 - 140, 268);
            cancel.DialogResult = DialogResult.Cancel;
            UiButton add = UiButton.Create("Validate and add", UiButtonKind.Primary, 140);
            add.Location = new Point(28 + 504 - 140, 268);
            add.DialogResult = DialogResult.OK;
            cancel.BackColor = add.BackColor = Ui.Surface;
            Controls.Add(cancel);
            Controls.Add(add);
            AcceptButton = add;
            CancelButton = cancel;
            Shown += delegate { urlField.Input.Focus(); };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Ui.DarkTitleBar(Handle);
        }

        private static Label MakeLabel(string text, int y)
        {
            Label l = new Label();
            l.Text = text;
            l.SetBounds(28, y, 504, 18);
            l.ForeColor = Ui.Muted;
            l.Font = new Font("Segoe UI Semibold", 8F);
            return l;
        }
    }
    public class CopyToastControl : Control
    {
        private readonly System.Windows.Forms.Timer animationTimer;
        private readonly string toastText;
        private Point settledLocation;
        private int elapsedMs;
        private double animationAlpha;

        private static readonly Color ToastTop = Color.FromArgb(19, 78, 74);
        private static readonly Color ToastBottom = Color.FromArgb(10, 45, 52);
        private static readonly Color ToastBorder = Color.FromArgb(45, 212, 191);
        private static readonly Color ToastText = Color.FromArgb(240, 253, 250);

        public event EventHandler Finished;

        public CopyToastControl(string text, Point anchor, Rectangle availableArea)
        {
            toastText = string.IsNullOrWhiteSpace(text) ? "COPIED" : text.ToUpperInvariant();

            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            TabStop = false;
            Enabled = false;
            Font = new Font("Segoe UI Semibold", 8.5F);

            Size measured = TextRenderer.MeasureText(toastText, Font, new Size(400, 40), TextFormatFlags.SingleLine);
            Width = Math.Max(104, measured.Width + 50);
            Height = 38;

            int x = anchor.X - Width / 2;
            int y = anchor.Y - Height - 12;
            if (y < availableArea.Top + 4) y = anchor.Y + 16;
            if (x < availableArea.Left + 4) x = availableArea.Left + 4;
            if (x + Width > availableArea.Right - 4) x = availableArea.Right - Width - 4;
            if (y + Height > availableArea.Bottom - 4) y = availableArea.Bottom - Height - 4;

            settledLocation = new Point(x, y);
            Location = new Point(x, y + 8);
            Visible = false;

            animationTimer = new System.Windows.Forms.Timer();
            animationTimer.Interval = 16;
            animationTimer.Tick += AnimationTick;
        }

        public void Start()
        {
            elapsedMs = 0;
            animationAlpha = 0.0;
            Visible = true;
            BringToFront();
            animationTimer.Start();
        }

        private static double Clamp01(double value)
        {
            if (value < 0.0) return 0.0;
            if (value > 1.0) return 1.0;
            return value;
        }

        private static double EaseOutCubic(double t)
        {
            t = Clamp01(t);
            double inv = 1.0 - t;
            return 1.0 - inv * inv * inv;
        }

        private void AnimationTick(object sender, EventArgs e)
        {
            elapsedMs += animationTimer.Interval;

            if (elapsedMs <= 170)
            {
                double progress = EaseOutCubic(elapsedMs / 170.0);
                animationAlpha = Math.Min(0.96, progress * 0.96);
                Location = new Point(settledLocation.X, settledLocation.Y + (int)Math.Round(8.0 * (1.0 - progress)));
                Invalidate();
                return;
            }

            if (elapsedMs <= 900)
            {
                animationAlpha = 0.96;
                Location = settledLocation;
                Invalidate();
                return;
            }

            double fade = Clamp01((elapsedMs - 900) / 320.0);
            animationAlpha = Math.Max(0.0, 0.96 * (1.0 - fade));
            Location = new Point(settledLocation.X, settledLocation.Y - (int)Math.Round(7.0 * fade));
            Invalidate();

            if (elapsedMs >= 1220)
            {
                animationTimer.Stop();
                Visible = false;
                EventHandler handler = Finished;
                if (handler != null) handler(this, EventArgs.Empty);
                if (Parent != null) Parent.Controls.Remove(this);
                Dispose();
            }
        }

        private static Color WithAlpha(Color color, double alpha)
        {
            int a = (int)Math.Round(255.0 * Clamp01(alpha));
            return Color.FromArgb(a, color.R, color.G, color.B);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (Width <= 0 || Height <= 0) return;
            // Clip the native control too: transparent WinForms controls otherwise
            // paint a rectangular patch of the parent over the grid beneath them.
            using (GraphicsPath outline = RoundedRect(ClientRectangle, Math.Min(Width, Height) / 2))
            {
                Region previous = Region;
                Region = new Region(outline);
                if (previous != null) previous.Dispose();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (animationAlpha <= 0.0) return;

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle pill = new Rectangle(1, 1, Width - 3, Height - 3);
            Color top = WithAlpha(ToastTop, animationAlpha);
            Color bottom = WithAlpha(ToastBottom, animationAlpha);
            Color borderColor = WithAlpha(ToastBorder, animationAlpha);
            Color textColor = WithAlpha(ToastText, animationAlpha);

            using (GraphicsPath path = RoundedRect(pill, pill.Height / 2))
            using (LinearGradientBrush fill = new LinearGradientBrush(pill, top, bottom, LinearGradientMode.Vertical))
            using (Pen border = new Pen(borderColor, 1.2F))
            {
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(border, path);
            }

            Rectangle badge = new Rectangle(10, 9, 20, 20);
            using (SolidBrush badgeFill = new SolidBrush(WithAlpha(Color.FromArgb(45, 212, 191), animationAlpha)))
                e.Graphics.FillEllipse(badgeFill, badge);
            using (Pen check = new Pen(WithAlpha(Color.White, animationAlpha), 2.0F))
            {
                check.StartCap = LineCap.Round;
                check.EndCap = LineCap.Round;
                e.Graphics.DrawLines(check, new Point[]
                {
                    new Point(15, 19),
                    new Point(19, 23),
                    new Point(26, 15)
                });
            }

            Rectangle textRect = new Rectangle(37, 1, Width - 44, Height - 3);
            using (SolidBrush textBrush = new SolidBrush(textColor))
            using (StringFormat format = new StringFormat())
            {
                format.Alignment = StringAlignment.Near;
                format.LineAlignment = StringAlignment.Center;
                format.FormatFlags = StringFormatFlags.NoWrap;
                e.Graphics.DrawString(toastText, Font, textBrush, textRect, format);
            }
        }

        private static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = Math.Max(2, radius * 2);
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && animationTimer != null)
                animationTimer.Dispose();
            base.Dispose(disposing);
        }
    }

    public class BufferedGrid : DataGridView
    {
        public BufferedGrid() { DoubleBuffered = true; }
    }

    public class MainForm : Form
    {
        private AppData data;
        private DataGridView grid;
        private Button historyButton;
        private Button activeButton;
        private Button logsButton;
        private bool showingLogs;
        private TextBox logText;
        private Panel logsPanel;
        private FlowLayoutPanel logActions;
        private readonly System.Windows.Forms.Timer logTimer = new System.Windows.Forms.Timer();
        private long logVersion = -1;
        private Button joinedButton;
        private Button refreshButton;
        private Button deepButton;
        private Button addButton;
        private Label lastCheck;
        private Label viewTitle;
        private Label viewSubtitle;
        private Label scanStatus;
        private TextBox filterBox;
        private UiPicker filterFieldBox;
        private TableLayoutPanel filterPanelRef;
        private bool showingHistory = false;
        private bool showingJoined = false;
        private bool scanning = false;
        private System.Windows.Forms.Timer autoTimer;
        private readonly System.Windows.Forms.Timer filterTimer = new System.Windows.Forms.Timer();
        private readonly Font creatorFont = new Font("Segoe UI Semibold", 9.4F);
        private readonly Font ticketFont = new Font("Segoe UI Semibold", 9.2F);
        private CopyToastControl copyToast;
        private string sortColumn = "Creator";
        private bool sortAscending = true;

        // Restrained product-dashboard palette: neutral ink surfaces with precise accent usage.
        private readonly Color Bg = Ui.Bg;
        private readonly Color Surface = Ui.Surface;
        private readonly Color Surface2 = Ui.Surface2;
        private readonly Color Surface3 = Ui.Surface3;
        private readonly Color Line = Ui.Line;
        private readonly Color TextColor = Ui.Text;
        private readonly Color Muted = Ui.Muted;
        private readonly Color Accent = Ui.Accent;
        private readonly Color Accent2 = Color.FromArgb(56, 189, 248);
        private readonly Color Success = Ui.Success;
        private readonly Color Warning = Ui.Warning;
        private readonly Color Danger = Ui.Danger;
        private readonly Color PriorityGreen = Color.FromArgb(74, 222, 128);
        private readonly Color PriorityYellow = Color.FromArgb(250, 204, 21);
        private readonly Color PriorityRed = Color.FromArgb(248, 113, 113);
        private readonly Color RowHover = Color.FromArgb(21, 28, 41);
        private readonly Color RowSelected = Color.FromArgb(26, 34, 52);
        public MainForm()
            : this(null, true)
        {
        }

        internal MainForm(AppData initialData, bool automaticRefresh)
        {
            Text = Program.DisplayTitle;
            Width = 1240;
            Height = 800;
            MinimumSize = new Size(1180, 720);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Bg;
            ForeColor = TextColor;
            Font = new Font("Segoe UI", 9F);
            AutoScaleMode = AutoScaleMode.Dpi;
            KeyPreview = true;
            KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Control && e.KeyCode == Keys.F && filterBox != null)
                {
                    filterBox.Focus();
                    filterBox.SelectAll();
                    e.SuppressKeyPress = true;
                }
            };
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            // Closing the app must also close every hidden browser render that it owns.
            // This prevents Edge/Chrome giveaway tabs from remaining in Task Manager.
            FormClosing += delegate
            {
                try { if (autoTimer != null) autoTimer.Stop(); } catch { }
                BrowserProcessManager.Shutdown();
            };

            data = initialData ?? DataStore.Load();
            // History is persisted data. Do not reset ended giveaways back to
            // unknown on startup; that made the History tab rebuild itself on
            // every launch instead of loading the saved classification.
            BuildUi();
            ActivityLog.Write("APP", "Ready. Browser process limit: 5 total; one rendered page at a time; 30-second page timeout.");
            logTimer.Interval = 1000;
            logTimer.Tick += delegate { UpdateLogs(); };
            logTimer.Start();
            filterTimer.Interval = 120;
            filterTimer.Tick += delegate { filterTimer.Stop(); RenderView(false); };
            Render();

            if (automaticRefresh) Shown += async delegate { await RefreshAsync(false); };
            autoTimer = new System.Windows.Forms.Timer();
            autoTimer.Interval = 10 * 60 * 1000;
            autoTimer.Tick += async delegate { if (!scanning) await RefreshAsync(false); };
            if (automaticRefresh) autoTimer.Start();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Ui.DarkTitleBar(Handle);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                filterTimer.Dispose();
                logTimer.Dispose();
                if (autoTimer != null) autoTimer.Dispose();
            }
            base.Dispose(disposing);
            if (disposing) { creatorFont.Dispose(); ticketFont.Dispose(); }
        }

        private void RepairLegacyStatuses()
        {
            bool changed = false;
            foreach (GiveawayItem i in data.Items)
            {
                if (!string.Equals(i.Status, "ended", StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals((i.Deadline ?? "").Trim(), "Ended", StringComparison.OrdinalIgnoreCase)) continue;
                Match m = Regex.Match(i.Ticket ?? "", @"^\s*([0-9,]+)\s*/\s*([0-9,]+)\s*$");
                if (!m.Success) continue;
                int remaining;
                if (!int.TryParse(m.Groups[1].Value.Replace(",", ""), out remaining)) continue;
                if (remaining <= 0) continue;
                i.Status = "unknown";
                i.Deadline = "-";
                changed = true;
            }
            if (changed) DataStore.Save(data);
        }

        private static void RoundControl(Control control, int radius)
        {
            EventHandler update = delegate
            {
                if (control.Width < 2 || control.Height < 2) return;
                int diameter = Math.Min(radius * 2, Math.Min(control.Width, control.Height));
                using (GraphicsPath path = new GraphicsPath())
                {
                    path.AddArc(0, 0, diameter, diameter, 180, 90);
                    path.AddArc(control.Width - diameter, 0, diameter, diameter, 270, 90);
                    path.AddArc(control.Width - diameter, control.Height - diameter, diameter, diameter, 0, 90);
                    path.AddArc(0, control.Height - diameter, diameter, diameter, 90, 90);
                    path.CloseFigure();
                    Region previous = control.Region;
                    control.Region = new Region(path);
                    if (previous != null) previous.Dispose();
                }
            };
            control.SizeChanged += update;
            update(control, EventArgs.Empty);
        }

        private readonly Font cellFont = new Font("Segoe UI", 9.5F);
        private readonly Font cellBold = new Font("Segoe UI Semibold", 9.5F);
        private readonly Font monoFont = new Font("Consolas", 9.5F);
        private readonly Font smallFont = new Font("Segoe UI", 8.5F);
        private readonly Font headerFont = new Font("Segoe UI Semibold", 8F);
        private ScanStrip scanStrip;
        private Panel statusDot;
        private Color statusDotColor = Ui.Success;
        private int hoverRow = -1;
        private int hoverColumn = -1;
        private bool hoverOnCopy;

        private void BuildUi()
        {
            SuspendLayout();

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.BackColor = Bg;
            root.Padding = new Padding(28, 18, 28, 0);
            root.ColumnCount = 1;
            root.RowCount = 5;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54F));
            Controls.Add(root);

            // ---- Header: brand on the left, primary actions on the right ----
            TableLayoutPanel header = new TableLayoutPanel();
            header.Dock = DockStyle.Fill;
            header.Margin = new Padding(0);
            header.BackColor = Bg;
            header.ColumnCount = 2;
            header.RowCount = 1;
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.Controls.Add(header, 0, 0);

            FlowLayoutPanel brand = new FlowLayoutPanel();
            brand.Dock = DockStyle.Fill;
            brand.Margin = new Padding(0);
            brand.WrapContents = false;
            brand.FlowDirection = FlowDirection.LeftToRight;
            brand.BackColor = Bg;
            header.Controls.Add(brand, 0, 0);

            PictureBox logo = new PictureBox();
            logo.Size = new Size(36, 36);
            logo.Margin = new Padding(0, 12, 12, 0);
            logo.SizeMode = PictureBoxSizeMode.Zoom;
            try { logo.Image = Icon.ExtractAssociatedIcon(Application.ExecutablePath).ToBitmap(); } catch { }
            brand.Controls.Add(logo);

            Label title = new Label();
            title.Text = "SkinClub GW Finder";
            title.AutoSize = true;
            title.Margin = new Padding(0, 14, 0, 0);
            title.Font = new Font("Segoe UI Semibold", 15F);
            title.ForeColor = TextColor;
            brand.Controls.Add(title);

            Label version = new Label();
            version.Text = "v" + typeof(Program).Assembly.GetName().Version.ToString(3);
            version.AutoSize = true;
            version.Margin = new Padding(10, 21, 0, 0);
            version.Font = new Font("Segoe UI", 9F);
            version.ForeColor = Muted;
            brand.Controls.Add(version);

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.AutoSize = true;
            actions.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            actions.Anchor = AnchorStyles.Right;
            actions.Margin = new Padding(0);
            actions.WrapContents = false;
            actions.FlowDirection = FlowDirection.LeftToRight;
            actions.BackColor = Bg;
            header.Controls.Add(actions, 1, 0);

            addButton = MakeHeaderButton("Add link", 96, false);
            refreshButton = MakeHeaderButton("Refresh", 92, false);
            deepButton = MakeHeaderButton("Deep Search", 124, true);
            addButton.Margin = refreshButton.Margin = deepButton.Margin = new Padding(8, 0, 0, 0);
            actions.Controls.Add(addButton);
            actions.Controls.Add(refreshButton);
            actions.Controls.Add(deepButton);
            addButton.Click += async delegate { await AddLinkAsync(); };
            refreshButton.Click += async delegate { await RefreshAsync(true); };
            deepButton.Click += async delegate { await DeepSearchAsync(); };

            // ---- Navigation: tabs on the left, search on the right ----
            TableLayoutPanel nav = new TableLayoutPanel();
            nav.Dock = DockStyle.Fill;
            nav.Margin = new Padding(0);
            nav.BackColor = Bg;
            nav.ColumnCount = 2;
            nav.RowCount = 1;
            nav.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            nav.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 480F));
            nav.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.Controls.Add(nav, 0, 1);

            FlowLayoutPanel tabs = new FlowLayoutPanel();
            tabs.Dock = DockStyle.Fill;
            tabs.Margin = new Padding(0);
            tabs.WrapContents = false;
            tabs.FlowDirection = FlowDirection.LeftToRight;
            tabs.BackColor = Bg;
            nav.Controls.Add(tabs, 0, 0);

            activeButton = MakeTab("Active");
            joinedButton = MakeTab("Joined");
            historyButton = MakeTab("History");
            logsButton = MakeTab("Logs");
            ((UiButton)activeButton).AccentColor = Color.FromArgb(45, 212, 191);
            ((UiButton)joinedButton).AccentColor = Color.FromArgb(167, 139, 250);
            ((UiButton)historyButton).AccentColor = Color.FromArgb(245, 158, 11);
            ((UiButton)logsButton).AccentColor = Color.FromArgb(56, 189, 248);
            tabs.Controls.Add(activeButton);
            tabs.Controls.Add(joinedButton);
            tabs.Controls.Add(historyButton);
            activeButton.Click += delegate { showingHistory = false; showingJoined = false; showingLogs = false; Render(); };
            joinedButton.Click += delegate { showingJoined = true; showingHistory = false; showingLogs = false; Render(); };
            historyButton.Click += delegate { showingHistory = true; showingJoined = false; showingLogs = false; Render(); };
            logsButton.Click += delegate { showingLogs = true; showingHistory = false; showingJoined = false; Render(); };

            Panel navTools = new Panel();
            navTools.Dock = DockStyle.Fill;
            navTools.Margin = new Padding(0);
            navTools.BackColor = Bg;
            nav.Controls.Add(navTools, 1, 0);

            TableLayoutPanel filterPanel = new TableLayoutPanel();
            filterPanelRef = filterPanel;
            filterPanel.Dock = DockStyle.Fill;
            filterPanel.Padding = new Padding(0);
            filterPanel.BackColor = Bg;
            filterPanel.ColumnCount = 2;
            filterPanel.RowCount = 1;
            filterPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            filterPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 142F));
            filterPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            navTools.Controls.Add(filterPanel);

            filterFieldBox = new UiPicker();
            filterFieldBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            filterFieldBox.Margin = new Padding(0, 0, 10, 0);
            filterFieldBox.Items.AddRange(new object[] { "Creator name", "All fields", "URL", "Ticket", "Promocode", "Minimum deposit", "Deadline" });
            filterFieldBox.SelectedIndex = 0;
            filterFieldBox.SelectedIndexChanged += delegate { if (grid != null) Render(); };
            filterPanel.Controls.Add(filterFieldBox, 0, 0);

            TextField searchField = new TextField(true);
            searchField.ReserveShadow = true;
            searchField.Height = 44;
            searchField.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            searchField.Margin = new Padding(0);
            searchField.Placeholder = "Search giveaways   (Ctrl+F)";
            filterBox = searchField.Input;
            filterBox.AccessibleName = "Search giveaways";
            filterBox.TextChanged += delegate { filterTimer.Stop(); filterTimer.Start(); };
            filterBox.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape && filterBox.TextLength > 0)
                {
                    filterBox.Clear();
                    e.SuppressKeyPress = true;
                }
            };
            filterPanel.Controls.Add(searchField, 1, 0);

            logActions = new FlowLayoutPanel();
            logActions.Dock = DockStyle.Fill;
            logActions.FlowDirection = FlowDirection.RightToLeft;
            logActions.Padding = new Padding(0, 6, 0, 0);
            logActions.BackColor = Bg;
            logActions.Visible = false;
            Button copyLogs = MakeHeaderButton("Copy logs", 100, false);
            copyLogs.Margin = new Padding(8, 0, 0, 0);
            copyLogs.Click += delegate
            {
                try { if (logText.TextLength > 0) Clipboard.SetText(logText.Text); }
                catch { scanStatus.Text = "Clipboard is busy - try again"; }
            };
            Button clearLogs = MakeHeaderButton("Clear", 80, false);
            clearLogs.Margin = new Padding(8, 0, 0, 0);
            clearLogs.Click += delegate { ActivityLog.Clear(); UpdateLogs(); };
            logActions.Controls.Add(copyLogs);
            logActions.Controls.Add(clearLogs);
            navTools.Controls.Add(logActions);

            // ---- Context line: what this view shows ----
            TableLayoutPanel context = new TableLayoutPanel();
            context.Dock = DockStyle.Fill;
            context.Margin = new Padding(0);
            context.BackColor = Bg;
            context.ColumnCount = 2;
            context.RowCount = 1;
            context.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            context.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            context.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.Controls.Add(context, 0, 2);

            viewTitle = new Label();
            viewTitle.Text = "Active giveaways";
            viewTitle.AutoSize = true;
            viewTitle.Anchor = AnchorStyles.Left;
            viewTitle.Margin = new Padding(0, 0, 12, 0);
            viewTitle.Font = new Font("Segoe UI Semibold", 12F);
            viewTitle.ForeColor = TextColor;
            context.Controls.Add(viewTitle, 0, 0);

            viewSubtitle = new Label();
            viewSubtitle.Text = "";
            viewSubtitle.Dock = DockStyle.Fill;
            viewSubtitle.Margin = new Padding(0);
            viewSubtitle.ForeColor = Muted;
            viewSubtitle.Font = new Font("Segoe UI", 9F);
            viewSubtitle.TextAlign = ContentAlignment.MiddleLeft;
            viewSubtitle.AutoEllipsis = true;
            context.Controls.Add(viewSubtitle, 1, 0);

            // ---- Card holding the table / logs ----
            Panel card = new Panel();
            card.Dock = DockStyle.Fill;
            card.Margin = new Padding(0);
            card.Padding = new Padding(1);
            card.BackColor = Line;
            root.Controls.Add(card, 0, 3);

            TableLayoutPanel cardInner = new TableLayoutPanel();
            cardInner.Dock = DockStyle.Fill;
            cardInner.Margin = new Padding(0);
            cardInner.BackColor = Surface;
            cardInner.ColumnCount = 1;
            cardInner.RowCount = 2;
            cardInner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            cardInner.RowStyles.Add(new RowStyle(SizeType.Absolute, 2F));
            cardInner.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            card.Controls.Add(cardInner);

            scanStrip = new ScanStrip();
            scanStrip.Dock = DockStyle.Fill;
            scanStrip.Margin = new Padding(0);
            cardInner.Controls.Add(scanStrip, 0, 0);

            grid = new BufferedGrid();
            grid.Dock = DockStyle.Fill;
            grid.Margin = new Padding(0);
            grid.BackgroundColor = Surface;
            grid.BorderStyle = BorderStyle.None;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.None;
            grid.GridColor = Line;
            grid.RowHeadersVisible = false;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.AllowUserToResizeColumns = false;
            grid.AllowUserToOrderColumns = false;
            grid.MultiSelect = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.ReadOnly = true;
            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersVisible = true;
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.ColumnHeadersHeight = 40;
            grid.ColumnHeadersDefaultCellStyle.BackColor = Surface;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = Muted;
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Surface;
            grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Muted;
            grid.ColumnHeadersDefaultCellStyle.Font = headerFont;
            grid.DefaultCellStyle.BackColor = Surface;
            grid.DefaultCellStyle.ForeColor = TextColor;
            grid.DefaultCellStyle.SelectionBackColor = RowSelected;
            grid.DefaultCellStyle.SelectionForeColor = TextColor;
            grid.DefaultCellStyle.Font = cellFont;
            grid.RowTemplate.Height = 56;
            grid.AutoGenerateColumns = false;
            grid.ShowCellToolTips = false;
            grid.StandardTab = true;

            DataGridViewTextBoxColumn creator = new DataGridViewTextBoxColumn();
            creator.Name = "Creator";
            creator.HeaderText = "CREATOR";
            creator.Width = 160;
            creator.SortMode = DataGridViewColumnSortMode.Programmatic;

            DataGridViewTextBoxColumn link = new DataGridViewTextBoxColumn();
            link.Name = "Link";
            link.HeaderText = "LINK";
            link.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            link.MinimumWidth = 140;
            link.SortMode = DataGridViewColumnSortMode.Programmatic;

            DataGridViewTextBoxColumn ticket = new DataGridViewTextBoxColumn();
            ticket.Name = "Ticket";
            ticket.HeaderText = "TICKETS";
            ticket.Width = 176;
            ticket.SortMode = DataGridViewColumnSortMode.Programmatic;

            DataGridViewTextBoxColumn promo = new DataGridViewTextBoxColumn();
            promo.Name = "PromoCode";
            promo.HeaderText = "PROMOCODE";
            promo.Width = 250;
            promo.SortMode = DataGridViewColumnSortMode.Programmatic;

            DataGridViewTextBoxColumn minimumDeposit = new DataGridViewTextBoxColumn();
            minimumDeposit.Name = "MinimumDeposit";
            minimumDeposit.HeaderText = "MIN DEPOSIT";
            minimumDeposit.Width = 110;
            minimumDeposit.SortMode = DataGridViewColumnSortMode.Programmatic;

            DataGridViewTextBoxColumn deadline = new DataGridViewTextBoxColumn();
            deadline.Name = "Deadline";
            deadline.HeaderText = "DEADLINE";
            deadline.Width = 150;
            deadline.SortMode = DataGridViewColumnSortMode.Programmatic;

            DataGridViewButtonColumn joinedAction = MakeIconColumn("JoinedAction", 56);

            grid.Columns.AddRange(new DataGridViewColumn[] { creator, link, ticket, promo, minimumDeposit, deadline, joinedAction });
            grid.CellMouseClick += GridCellMouseClick;
            grid.CellPainting += GridCellPainting;
            grid.ColumnHeaderMouseClick += GridColumnHeaderMouseClick;
            grid.CellMouseMove += GridCellMouseMove;
            grid.CellMouseLeave += delegate { SetHover(-1, -1, false); };
            grid.MouseLeave += delegate { SetHover(-1, -1, false); };
            grid.Paint += delegate(object sender, PaintEventArgs e)
            {
                if (grid.Rows.Count != 0) return;
                string heading = filterBox.TextLength > 0 ? "No matching giveaways"
                    : showingJoined ? "Nothing joined yet"
                    : showingHistory ? "No history yet"
                    : "No active giveaways";
                string detail = filterBox.TextLength > 0 ? "Try another search, or press Esc to clear it."
                    : showingJoined ? "Use the + button on a giveaway to save it here."
                    : showingHistory ? "Ended giveaways are kept here automatically."
                    : "Press Refresh to re-check saved links, or Deep Search to find new ones.";
                int top = grid.ColumnHeadersHeight + Math.Max(30, (grid.ClientSize.Height - grid.ColumnHeadersHeight) / 2 - 50);
                Rectangle headingRect = new Rectangle(20, top, Math.Max(1, grid.ClientSize.Width - 40), 28);
                Rectangle detailRect = new Rectangle(20, top + 30, Math.Max(1, grid.ClientSize.Width - 40), 24);
                TextRenderer.DrawText(e.Graphics, heading, creatorFont, headingRect, TextColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                TextRenderer.DrawText(e.Graphics, detail, smallFont, detailRect, Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            };

            Panel content = new Panel();
            content.Dock = DockStyle.Fill;
            content.Margin = new Padding(0);
            content.Controls.Add(grid);
            logsPanel = new Panel();
            logsPanel.Dock = DockStyle.Fill;
            logsPanel.Padding = new Padding(18, 14, 18, 14);
            logsPanel.BackColor = Surface;
            logText = new TextBox();
            logText.Multiline = true;
            logText.ScrollBars = ScrollBars.Vertical;
            logText.Dock = DockStyle.Fill;
            logText.ReadOnly = true;
            logText.WordWrap = true;
            logText.BorderStyle = BorderStyle.None;
            logText.BackColor = Surface;
            logText.ForeColor = Color.FromArgb(170, 182, 205);
            logText.Font = new Font("Consolas", 9.5F);
            logText.AccessibleName = "Live activity logs";
            Ui.DarkScrollBars(logText);
            Ui.DarkScrollBars(grid);
            logsPanel.Controls.Add(logText);
            content.Controls.Add(logsPanel);
            cardInner.Controls.Add(content, 0, 1);

            // ---- Status bar ----
            TableLayoutPanel status = new TableLayoutPanel();
            status.Dock = DockStyle.Fill;
            status.Margin = new Padding(0);
            status.BackColor = Bg;
            status.ColumnCount = 4;
            status.RowCount = 1;
            status.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            status.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 26F));
            status.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            status.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            status.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.Controls.Add(status, 0, 4);

            logsButton.Anchor = AnchorStyles.Left;
            logsButton.Margin = new Padding(0, 0, 8, 0);
            status.Controls.Add(logsButton, 0, 0);

            statusDot = new Panel();
            statusDot.Size = new Size(10, 10);
            statusDot.Anchor = AnchorStyles.Right;
            statusDot.Margin = new Padding(0);
            statusDot.Paint += delegate(object s, PaintEventArgs pe)
            {
                pe.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                pe.Graphics.Clear(Bg);
                using (SolidBrush b = new SolidBrush(statusDotColor))
                    pe.Graphics.FillEllipse(b, 1, 1, 8, 8);
            };
            status.Controls.Add(statusDot, 1, 0);

            scanStatus = new Label();
            scanStatus.Dock = DockStyle.Fill;
            scanStatus.Margin = new Padding(0);
            scanStatus.ForeColor = Muted;
            scanStatus.Font = new Font("Segoe UI", 9F);
            scanStatus.TextAlign = ContentAlignment.MiddleLeft;
            scanStatus.AutoEllipsis = true;
            scanStatus.Text = "Ready";
            status.Controls.Add(scanStatus, 2, 0);

            lastCheck = new Label();
            lastCheck.AutoSize = true;
            lastCheck.Anchor = AnchorStyles.Right;
            lastCheck.Margin = new Padding(12, 0, 0, 0);
            lastCheck.ForeColor = Muted;
            lastCheck.Font = new Font("Segoe UI", 9F);
            lastCheck.Text = "Last checked: never";
            status.Controls.Add(lastCheck, 3, 0);

            ResumeLayout(true);
        }

        private DataGridViewButtonColumn MakeIconColumn(string name, int width)
        {
            DataGridViewButtonColumn column = new DataGridViewButtonColumn();
            column.Name = name;
            column.HeaderText = "";
            column.Text = "";
            column.UseColumnTextForButtonValue = true;
            column.Width = width;
            column.FlatStyle = FlatStyle.Flat;
            column.SortMode = DataGridViewColumnSortMode.NotSortable;
            return column;
        }

        private Button MakeHeaderButton(string text, int width, bool primary)
        {
            return UiButton.Create(text, primary ? UiButtonKind.Primary : UiButtonKind.Ghost, width);
        }

        private UiButton MakeTab(string text)
        {
            UiButton tab = UiButton.Create(text, UiButtonKind.Tab, 0);
            tab.Margin = new Padding(0, 0, 8, 0);
            return tab;
        }

        private Color MixColor(Color baseColor, Color accent, double amount)
        {
            return Ui.Mix(baseColor, accent, amount);
        }

        private void GridColumnHeaderMouseClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.ColumnIndex < 0) return;
            DataGridViewColumn c = grid.Columns[e.ColumnIndex];
            if (c.SortMode == DataGridViewColumnSortMode.NotSortable || c.Name == "JoinedAction") return;

            if (string.Equals(sortColumn, c.Name, StringComparison.OrdinalIgnoreCase))
                sortAscending = !sortAscending;
            else
            {
                sortColumn = c.Name;
                sortAscending = true;
            }
            Render();
        }

        private static Rectangle CopyButtonRect(Rectangle cellBounds)
        {
            int size = 28;
            return new Rectangle(cellBounds.Right - size - 12, cellBounds.Y + (cellBounds.Height - size) / 2, size, size);
        }

        private Rectangle InlineCopyButtonRect(int columnIndex, int rowIndex, Rectangle cellBounds)
        {
            return CopyButtonRect(cellBounds);
        }

        private void SetHover(int row, int column, bool onCopy)
        {
            if (row == hoverRow && column == hoverColumn && onCopy == hoverOnCopy) return;
            int previous = hoverRow;
            hoverRow = row;
            hoverColumn = column;
            hoverOnCopy = onCopy;
            if (previous >= 0 && previous < grid.RowCount) grid.InvalidateRow(previous);
            if (row >= 0 && row < grid.RowCount) grid.InvalidateRow(row);
        }

        private void GridCellMouseMove(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) { SetHover(-1, -1, false); grid.Cursor = Cursors.Default; return; }
            string name = grid.Columns[e.ColumnIndex].Name;
            Rectangle cell = grid.GetCellDisplayRectangle(e.ColumnIndex, e.RowIndex, false);
            bool onCopy = (name == "Link" || name == "PromoCode") && CopyButtonRect(cell).Contains(cell.X + e.X, cell.Y + e.Y);
            SetHover(e.RowIndex, e.ColumnIndex, onCopy);

            GiveawayItem item = grid.Rows[e.RowIndex].Tag as GiveawayItem;
            bool clickable = name == "JoinedAction" || name == "Link" ||
                (name == "PromoCode" && item != null && !string.IsNullOrWhiteSpace(item.PromoCode) && item.PromoCode != "-");
            grid.Cursor = clickable ? Cursors.Hand : Cursors.Default;
        }

        private static string ShortUrl(string url)
        {
            string s = (url ?? "").Trim();
            if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) s = s.Substring(8);
            else if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) s = s.Substring(7);
            if (s.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) s = s.Substring(4);
            return s.TrimEnd('/');
        }

        private static readonly Color[] AvatarColors = new Color[]
        {
            Color.FromArgb(99, 102, 241), Color.FromArgb(56, 189, 248), Color.FromArgb(52, 211, 153),
            Color.FromArgb(251, 146, 60), Color.FromArgb(244, 114, 182), Color.FromArgb(167, 139, 250)
        };

        private Color TicketColor(GiveawayItem item)
        {
            long remaining, total;
            if (item != null && string.Equals(item.Status, "active", StringComparison.OrdinalIgnoreCase) &&
                TryTicketNumbers(item.Ticket, out remaining, out total) && total > 0)
                return remaining < 100 ? PriorityRed : (remaining < 250 ? PriorityYellow : PriorityGreen);
            return TextColor;
        }

        private void DrawCopyIcon(Graphics g, Rectangle r, bool hot, bool rowHot)
        {
            if (hot)
                using (GraphicsPath path = Ui.RoundPath(r, 7))
                using (SolidBrush fill = new SolidBrush(Surface3))
                using (Pen border = new Pen(Ui.LineStrong))
                {
                    g.FillPath(fill, path);
                    g.DrawPath(border, path);
                }
            Color stroke = hot ? TextColor : (rowHot ? Muted : Color.FromArgb(78, 90, 113));
            using (Pen pen = new Pen(stroke, 1.4F))
            {
                pen.LineJoin = LineJoin.Round;
                int x = r.X + (r.Width - 14) / 2, y = r.Y + (r.Height - 14) / 2;
                g.DrawRectangle(pen, x + 4, y, 9, 9);
                g.DrawLines(pen, new Point[] { new Point(x + 2, y + 4), new Point(x, y + 4), new Point(x, y + 13), new Point(x + 9, y + 13), new Point(x + 9, y + 11) });
            }
        }

        private void GridCellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.ColumnIndex < 0) return;
            Graphics g = e.Graphics;
            Rectangle cb = e.CellBounds;
            string name = grid.Columns[e.ColumnIndex].Name;

            if (e.RowIndex < 0)
            {
                using (SolidBrush b = new SolidBrush(Surface)) g.FillRectangle(b, cb);
                using (Pen p = new Pen(Line)) g.DrawLine(p, cb.Left, cb.Bottom - 1, cb.Right, cb.Bottom - 1);
                bool sorted = string.Equals(name, sortColumn, StringComparison.OrdinalIgnoreCase);
                string header = Convert.ToString(e.Value) ?? "";
                Rectangle headerRect = new Rectangle(cb.X + 16, cb.Y, Math.Max(1, cb.Width - 34), cb.Height);
                TextRenderer.DrawText(g, header, headerFont, headerRect, sorted ? TextColor : Muted, Ui.Single);
                if (sorted && header.Length > 0)
                {
                    int tw = TextRenderer.MeasureText(header, headerFont, new Size(1000, 40), TextFormatFlags.NoPadding).Width;
                    int ax = Math.Min(cb.Right - 16, cb.X + 16 + tw + 9), ay = cb.Y + cb.Height / 2;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    Point[] tri = sortAscending
                        ? new Point[] { new Point(ax - 4, ay + 2), new Point(ax + 4, ay + 2), new Point(ax, ay - 3) }
                        : new Point[] { new Point(ax - 4, ay - 2), new Point(ax + 4, ay - 2), new Point(ax, ay + 3) };
                    using (SolidBrush b = new SolidBrush(Ui.AccentHover)) g.FillPolygon(b, tri);
                }
                e.Handled = true;
                return;
            }

            GiveawayItem item = grid.Rows[e.RowIndex].Tag as GiveawayItem;
            bool selected = (e.State & DataGridViewElementStates.Selected) != 0;
            bool rowHot = e.RowIndex == hoverRow;
            bool ended = item != null && string.Equals(item.Status, "ended", StringComparison.OrdinalIgnoreCase);
            string value = Convert.ToString(e.Value) ?? "";
            bool missing = value.Length == 0 || value == "-";
            Color fore = ended ? Color.FromArgb(150, 161, 182) : TextColor;

            using (SolidBrush b = new SolidBrush(selected ? RowSelected : (rowHot ? RowHover : Surface))) g.FillRectangle(b, cb);
            using (Pen p = new Pen(Line)) g.DrawLine(p, cb.Left, cb.Bottom - 1, cb.Right, cb.Bottom - 1);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            const int pad = 16;
            Rectangle text = new Rectangle(cb.X + pad, cb.Y, Math.Max(1, cb.Width - pad * 2), cb.Height - 1);

            switch (name)
            {
                case "Creator":
                    {
                        string initial = value.Length > 0 ? value.Substring(0, 1).ToUpperInvariant() : "?";
                        Color tone = AvatarColors[Math.Abs(value.ToLowerInvariant().GetHashCode()) % AvatarColors.Length];
                        Rectangle av = new Rectangle(cb.X + pad, cb.Y + (cb.Height - 1 - 30) / 2, 30, 30);
                        using (SolidBrush b = new SolidBrush(Ui.Mix(Surface, tone, 0.22))) g.FillEllipse(b, av);
                        TextRenderer.DrawText(g, initial, cellBold, av, Ui.Mix(tone, Color.White, 0.35),
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                        Rectangle nameRect = new Rectangle(av.Right + 12, cb.Y, Math.Max(1, cb.Right - av.Right - 12 - 8), cb.Height - 1);
                        TextRenderer.DrawText(g, value, cellBold, nameRect, fore, Ui.Single);
                        break;
                    }
                case "Link":
                    {
                        Rectangle linkRect = new Rectangle(text.X, text.Y, Math.Max(1, text.Width - 40), text.Height);
                        bool onLink = rowHot && hoverColumn == e.ColumnIndex && !hoverOnCopy;
                        TextRenderer.DrawText(g, ShortUrl(value), cellFont, linkRect, onLink ? Color.White : (ended ? Ui.Muted : Ui.AccentHover), Ui.Single);
                        DrawCopyIcon(g, CopyButtonRect(cb), rowHot && hoverColumn == e.ColumnIndex && hoverOnCopy, rowHot);
                        break;
                    }
                case "Ticket":
                    {
                        long remaining, total;
                        if (missing || !TryTicketNumbers(value, out remaining, out total) || total <= 0)
                        {
                            TextRenderer.DrawText(g, missing ? "–" : value, cellFont, text, Ui.Muted, Ui.Single);
                            break;
                        }
                        Color tone = TicketColor(item);
                        Rectangle numbers = new Rectangle(text.X, text.Y, 90, text.Height);
                        TextRenderer.DrawText(g, value, cellBold, numbers, tone, Ui.Single);
                        int barX = text.X + 94, barW = Math.Min(40, text.Right - barX);
                        if (barW > 12)
                        {
                            int cy = cb.Y + (cb.Height - 1) / 2;
                            using (GraphicsPath track = Ui.RoundPath(new Rectangle(barX, cy - 2, barW, 4), 2))
                            using (SolidBrush b = new SolidBrush(Ui.Line)) g.FillPath(b, track);
                            int fillW = (int)Math.Round(barW * Math.Min(1.0, (double)remaining / total));
                            if (fillW > 3)
                                using (GraphicsPath fillPath = Ui.RoundPath(new Rectangle(barX, cy - 2, fillW, 4), 2))
                                using (SolidBrush b = new SolidBrush(tone)) g.FillPath(b, fillPath);
                        }
                        break;
                    }
                case "PromoCode":
                    {
                        if (missing) { TextRenderer.DrawText(g, "–", cellFont, text, Ui.Muted, Ui.Single); break; }
                        int maxPill = Math.Max(40, cb.Width - pad - 52);
                        int pillW = Math.Min(maxPill, TextRenderer.MeasureText(value, monoFont, new Size(1000, 40), TextFormatFlags.NoPadding).Width + 22);
                        Rectangle pill = new Rectangle(cb.X + pad, cb.Y + (cb.Height - 1 - 28) / 2, pillW, 28);
                        using (GraphicsPath path = Ui.RoundPath(pill, 6))
                        using (SolidBrush b = new SolidBrush(rowHot ? Surface3 : Surface2))
                        using (Pen p = new Pen(Ui.Line))
                        {
                            g.FillPath(b, path);
                            g.DrawPath(p, path);
                        }
                        TextRenderer.DrawText(g, value, monoFont, new Rectangle(pill.X + 11, pill.Y, pill.Width - 14, pill.Height), fore, Ui.Single);
                        DrawCopyIcon(g, CopyButtonRect(cb), rowHot && hoverColumn == e.ColumnIndex && hoverOnCopy, rowHot);
                        break;
                    }
                case "MinimumDeposit":
                    TextRenderer.DrawText(g, missing ? "–" : value, cellFont, text, missing ? Ui.Muted : fore, Ui.Single);
                    break;
                case "Deadline":
                    {
                        if (missing) { TextRenderer.DrawText(g, "\u2013", cellFont, text, Ui.Muted, Ui.Single); break; }
                        Color tone = fore;
                        DateTime due;
                        if (!ended && item != null && TryDeadlineDate(item, out due))
                        {
                            double days = (due.Date - DateTime.Now.Date).TotalDays;
                            tone = days <= 2 ? PriorityRed : (days <= 7 ? PriorityYellow : PriorityGreen);
                        }
                        else if (ended) tone = Ui.Muted;
                        TextRenderer.DrawText(g, value, cellFont, text, tone, Ui.Single);
                        break;
                    }                case "JoinedAction":
                    {
                        Color tone = showingJoined ? Danger : Success;
                        bool hot = rowHot && hoverColumn == e.ColumnIndex;
                        int size = 30;
                        Rectangle btn = new Rectangle(cb.X + (cb.Width - size) / 2, cb.Y + (cb.Height - 1 - size) / 2, size, size);
                        using (SolidBrush b = new SolidBrush(Ui.Mix(Surface2, tone, hot ? 0.32 : 0.14)))
                        using (Pen p = new Pen(Ui.Mix(Surface2, tone, hot ? 0.75 : 0.40)))
                        {
                            g.FillEllipse(b, btn);
                            g.DrawEllipse(p, btn);
                        }
                        using (Pen icon = new Pen(hot ? Color.White : Ui.Mix(tone, Color.White, 0.25), 1.8F))
                        {
                            icon.StartCap = LineCap.Round;
                            icon.EndCap = LineCap.Round;
                            int cx = btn.X + size / 2, cy = btn.Y + size / 2;
                            if (showingJoined)
                            {
                                g.DrawLine(icon, cx - 4, cy - 4, cx + 4, cy + 4);
                                g.DrawLine(icon, cx + 4, cy - 4, cx - 4, cy + 4);
                            }
                            else
                            {
                                g.DrawLine(icon, cx - 5, cy, cx + 5, cy);
                                g.DrawLine(icon, cx, cy - 5, cx, cy + 5);
                            }
                        }
                        break;
                    }
            }
            e.Handled = true;
        }

        private GraphicsPath MakeRoundedPath(Rectangle r, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = Math.Max(2, radius * 2);
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private void GridCellMouseClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            GiveawayItem item = grid.Rows[e.RowIndex].Tag as GiveawayItem;
            if (item == null) return;

            string columnName = grid.Columns[e.ColumnIndex].Name;
            string url = item.Url ?? "";

            if (columnName == "JoinedAction")
            {
                // Deep Search/Refresh runs against a snapshot so browsing and copying
                // remain fully usable. Avoid changing Joined state mid-scan because a
                // completed snapshot would otherwise overwrite that edit.
                if (scanning) return;

                if (showingJoined)
                {
                    item.Joined = false;
                    item.JoinedAt = null;
                    DataStore.Save(data);
                    scanStatus.Text = "Removed from Joined";
                    scanStatus.ForeColor = Muted;
                    Render();
                }
                else if (!item.Joined)
                {
                    item.Joined = true;
                    item.JoinedAt = DateTime.UtcNow.ToString("o");
                    DataStore.Save(data);
                    scanStatus.Text = "Saved to Joined";
                    scanStatus.ForeColor = Success;
                    Render();
                }
                else
                {
                    scanStatus.Text = "Already saved to Joined";
                    scanStatus.ForeColor = Muted;
                }
                return;
            }

            if (columnName == "Link" || columnName == "PromoCode")
            {
                Rectangle cellBounds = grid.GetCellDisplayRectangle(e.ColumnIndex, e.RowIndex, false);
                Rectangle copyRect = InlineCopyButtonRect(e.ColumnIndex, e.RowIndex, cellBounds);
                Point clickPoint = new Point(cellBounds.X + e.X, cellBounds.Y + e.Y);
                // A promocode is copied by clicking anywhere in its cell; a link is
                // opened by clicking it and copied with the icon.
                bool copyHit = columnName == "PromoCode" || copyRect.Contains(clickPoint);
                if (copyHit)
                {
                    if (columnName == "Link")
                    {
                        CopyToClipboard(url, "Link copied to clipboard", grid.PointToScreen(clickPoint), "LINK COPIED");
                    }
                    else
                    {
                        string promo = item.PromoCode ?? "";
                        if (!string.IsNullOrWhiteSpace(promo) && promo != "-")
                            CopyToClipboard(promo, "Promocode copied to clipboard", grid.PointToScreen(clickPoint), "CODE COPIED");
                        else
                        {
                            scanStatus.Text = "No promocode available";
                            scanStatus.ForeColor = Muted;
                        }
                    }
                    return;
                }

                if (columnName == "Link" && !string.IsNullOrWhiteSpace(url))
                {
                    try { Process.Start(url); } catch { }
                }
            }
        }

        private void CopyToClipboard(string value, string message, Point screenAnchor, string toastMessage)
        {
            if (string.IsNullOrWhiteSpace(value)) return;

            try
            {
                Clipboard.SetText(value);
            }
            catch
            {
                // The Windows clipboard can occasionally be locked by another app.
                // Never let a clipboard failure interfere with an active scan or the UI.
                if (!scanning)
                {
                    scanStatus.Text = "Clipboard is busy - try again";
                    scanStatus.ForeColor = Warning;
                }
                return;
            }

            ShowCopyToast(screenAnchor, toastMessage);

            // While a Refresh/Deep Search is running, keep the scan progress message
            // visible. The animated confirmation above still provides immediate copy
            // feedback without replacing Deep Search progress.
            if (scanning) return;

            scanStatus.Text = message;
            scanStatus.ForeColor = Success;
            Task.Delay(1400).ContinueWith(delegate
            {
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        if (!scanning)
                        {
                            scanStatus.Text = "Ready";
                            scanStatus.ForeColor = Muted;
                        }
                    }));
                }
                catch { }
            });
        }

        private void ShowCopyToast(Point screenAnchor, string text)
        {
            try
            {
                if (copyToast != null && !copyToast.IsDisposed)
                {
                    if (copyToast.Parent != null) copyToast.Parent.Controls.Remove(copyToast);
                    copyToast.Dispose();
                    copyToast = null;
                }
            }
            catch { }

            try
            {
                Point clientAnchor = PointToClient(screenAnchor);
                CopyToastControl toast = new CopyToastControl(text, clientAnchor, ClientRectangle);
                copyToast = toast;
                toast.Finished += delegate
                {
                    try
                    {
                        if (object.ReferenceEquals(copyToast, toast)) copyToast = null;
                    }
                    catch { }
                };
                Controls.Add(toast);
                toast.BringToFront();
                toast.Start();
            }
            catch { }
        }

        private List<GiveawayItem> CurrentItems()
        {
            List<GiveawayItem> list;
            if (showingJoined)
            {
                list = data.Items.Where(delegate(GiveawayItem i) { return i.Joined; }).ToList();
            }
            else
            {
                string wanted = showingHistory ? "ended" : "active";
                list = data.Items.Where(delegate(GiveawayItem i)
                {
                    // A joined giveaway is exclusive to Joined. Removing it from Joined simply
                    // exposes it again in the tab dictated by its preserved real status.
                    if (i.Joined) return false;
                    return string.Equals(i.Status, wanted, StringComparison.OrdinalIgnoreCase);
                }).ToList();
            }

            string query = filterBox == null ? "" : (filterBox.Text ?? "").Trim();
            if (query.Length > 0)
            {
                string field = filterFieldBox == null || filterFieldBox.SelectedItem == null
                    ? "Creator name"
                    : filterFieldBox.SelectedItem.ToString();

                list = list.Where(delegate(GiveawayItem i)
                {
                    if (string.Equals(field, "Creator name", StringComparison.OrdinalIgnoreCase))
                        return (i.Creator ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (string.Equals(field, "URL", StringComparison.OrdinalIgnoreCase))
                        return (i.Url ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (string.Equals(field, "Ticket", StringComparison.OrdinalIgnoreCase))
                        return (i.Ticket ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (string.Equals(field, "Promocode", StringComparison.OrdinalIgnoreCase))
                        return (i.PromoCode ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (string.Equals(field, "Minimum deposit", StringComparison.OrdinalIgnoreCase))
                        return (i.MinimumDeposit ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (string.Equals(field, "Deadline", StringComparison.OrdinalIgnoreCase))
                        return (i.Deadline ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

                    return (i.Creator ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                           (i.Url ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                           (i.Ticket ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                           (i.PromoCode ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                           (i.MinimumDeposit ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                           (i.Deadline ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
                }).ToList();
            }

            list.Sort(CompareItems);
            return list;
        }

        private int CompareItems(GiveawayItem a, GiveawayItem b)
        {
            int cmp = 0;
            if (string.Equals(sortColumn, "Ticket", StringComparison.OrdinalIgnoreCase))
            {
                long ar, at, br, bt;
                bool ah = TryTicketNumbers(a.Ticket, out ar, out at);
                bool bh = TryTicketNumbers(b.Ticket, out br, out bt);
                if (ah != bh) return ah ? -1 : 1;
                if (ah && bh)
                {
                    cmp = ar.CompareTo(br);
                    if (cmp == 0) cmp = at.CompareTo(bt);
                }
            }
            else if (string.Equals(sortColumn, "Deadline", StringComparison.OrdinalIgnoreCase))
            {
                DateTime ad, bd;
                bool ah = TryDeadlineDate(a, out ad);
                bool bh = TryDeadlineDate(b, out bd);
                if (ah != bh) return ah ? -1 : 1;
                if (ah && bh) cmp = ad.CompareTo(bd);
            }
            else if (string.Equals(sortColumn, "PromoCode", StringComparison.OrdinalIgnoreCase))
            {
                cmp = string.Compare(a.PromoCode ?? "", b.PromoCode ?? "", StringComparison.OrdinalIgnoreCase);
            }
            else if (string.Equals(sortColumn, "MinimumDeposit", StringComparison.OrdinalIgnoreCase))
            {
                decimal av, bv;
                bool ah = TryMinimumDepositValue(a.MinimumDeposit, out av);
                bool bh = TryMinimumDepositValue(b.MinimumDeposit, out bv);

                // Sort deposit amounts numerically instead of alphabetically.
                // Example: $2, $5, $10, $15 (not $10, $15, $2, $5).
                // Missing/unknown values stay at the bottom in either direction.
                if (ah != bh) return ah ? -1 : 1;
                if (ah && bh) cmp = av.CompareTo(bv);
                else cmp = 0;
            }
            else if (string.Equals(sortColumn, "Link", StringComparison.OrdinalIgnoreCase))
            {
                cmp = string.Compare(a.Url ?? "", b.Url ?? "", StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                cmp = string.Compare(a.Creator ?? "", b.Creator ?? "", StringComparison.OrdinalIgnoreCase);
            }

            if (cmp == 0)
                cmp = string.Compare(a.Creator ?? "", b.Creator ?? "", StringComparison.OrdinalIgnoreCase);
            return sortAscending ? cmp : -cmp;
        }

        private static readonly Regex TicketPairRegex = new Regex(@"^\s*([0-9,\.]+)\s*/\s*([0-9,\.]+)\s*$", RegexOptions.Compiled);
        private static readonly Regex DepositNumberRegex = new Regex(@"[0-9]+(?:[.,][0-9]+)?", RegexOptions.Compiled);

        private bool TryTicketNumbers(string value, out long remaining, out long total)
        {
            remaining = 0;
            total = 0;
            if (string.IsNullOrEmpty(value)) return false;
            Match m = TicketPairRegex.Match(value);
            if (!m.Success) return false;
            string a = m.Groups[1].Value.Replace(",", "").Replace(".", "");
            string b = m.Groups[2].Value.Replace(",", "").Replace(".", "");
            return long.TryParse(a, out remaining) && long.TryParse(b, out total);
        }

        private bool TryMinimumDepositValue(string raw, out decimal value)
        {
            value = 0m;
            if (string.IsNullOrWhiteSpace(raw)) return false;

            string text = raw.Trim();
            if (text == "-" || string.Equals(text, "N/A", StringComparison.OrdinalIgnoreCase))
                return false;

            // Minimum deposits are displayed with currency markers (for example
            // "$5", "10 USD", "€2.50").  Extract the numeric amount so the
            // grid sorts by value rather than by the formatted display string.
            Match m = DepositNumberRegex.Match(text);
            if (!m.Success) return false;

            string number = m.Value.Replace(',', '.');
            return decimal.TryParse(
                number,
                System.Globalization.NumberStyles.AllowDecimalPoint,
                System.Globalization.CultureInfo.InvariantCulture,
                out value);
        }

        private bool TryDeadlineDate(GiveawayItem item, out DateTime value)
        {
            value = DateTime.MinValue;
            string raw = item == null ? "" : (item.Deadline ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(raw) && raw != "-" && !string.Equals(raw, "Ended", StringComparison.OrdinalIgnoreCase))
            {
                DateTime parsed;
                if (DateTime.TryParse(raw, out parsed))
                {
                    value = parsed.Date;
                    return true;
                }
            }
            if ((showingHistory || (showingJoined && item != null && string.Equals(item.Status, "ended", StringComparison.OrdinalIgnoreCase))) &&
                item != null && !string.IsNullOrWhiteSpace(item.LastChecked))
            {
                DateTime checkedAt;
                if (DateTime.TryParse(item.LastChecked, out checkedAt))
                {
                    value = checkedAt.ToLocalTime();
                    return true;
                }
            }
            return false;
        }

        private void ApplyViewTheme()
        {
            SetTab(activeButton, !showingHistory && !showingJoined && !showingLogs);
            SetTab(joinedButton, showingJoined);
            SetTab(historyButton, showingHistory);
            SetTab(logsButton, showingLogs);
            viewTitle.ForeColor = TextColor;
        }

        private static void SetTab(Button button, bool selected)
        {
            UiButton tab = button as UiButton;
            if (tab != null) tab.Selected = selected;
        }

        private static void SetTabText(Button button, string text, int count)
        {
            UiButton tab = button as UiButton;
            if (tab == null) { button.Text = text + "  \u00b7  " + count; return; }
            tab.Text = text;
            tab.Badge = count.ToString();
        }
        // Column positions, matching the order columns are added in BuildUi().
        private const int ColTicket = 2, ColPromo = 3, ColDeposit = 4, ColDeadline = 5, ColAction = 6;

        private void Render()
        {
            RenderView(true);
        }

        // full=false is used while typing in the search box: only the table rows change,
        // so the header counts, theme colours and panel visibility are left alone.
        private void RenderView(bool full)
        {
            if (IsDisposed || Disposing) return;
            filterTimer.Stop();
            if (!full && !showingLogs && filterBox.TextLength > 0)
            {
                RebuildRows();
                return;
            }
            logsPanel.Visible = showingLogs;
            grid.Visible = !showingLogs;
            filterPanelRef.Visible = !showingLogs;
            logActions.Visible = showingLogs;
            int ac = data.Items.Count(delegate(GiveawayItem i) { return string.Equals(i.Status, "active", StringComparison.OrdinalIgnoreCase) && !i.Joined; });
            int hc = data.Items.Count(delegate(GiveawayItem i) { return string.Equals(i.Status, "ended", StringComparison.OrdinalIgnoreCase) && !i.Joined; });
            int jc = data.Items.Count(delegate(GiveawayItem i) { return i.Joined; });
            lastCheck.Text = "Last checked: " + FormatTime(data.LastScan);

            SetTabText(activeButton, "Active", ac);
            SetTabText(historyButton, "History", hc);
            SetTabText(joinedButton, "Joined", jc);

            if (showingJoined)
            {
                viewTitle.Text = "Joined giveaways";
                viewSubtitle.Text = "Saved by you  \u00b7  use \u00d7 to move an item back to Active or History";
            }
            else if (showingHistory)
            {
                viewTitle.Text = "Giveaway history";
                viewSubtitle.Text = "The 30 most recent ended giveaways  \u00b7  older ones are removed automatically";
            }
            else
            {
                viewTitle.Text = "Active giveaways";
                viewSubtitle.Text = "Running and not joined yet  \u00b7  click a column header to sort";
            }

            ApplyViewTheme();
            if (showingLogs)
            {
                viewTitle.Text = "Activity logs";
                viewTitle.ForeColor = TextColor;
                logsPanel.BringToFront();
                UpdateLogs();
                return;
            }
            RebuildRows();
        }

        private void RebuildRows()
        {
            GiveawayItem selectedItem = grid.CurrentRow == null ? null : grid.CurrentRow.Tag as GiveawayItem;
            string selectedUrl = selectedItem == null ? null : selectedItem.Url;
            int firstRow = grid.FirstDisplayedScrollingRowIndex;
            List<GiveawayItem> visibleItems = CurrentItems();
            if (filterBox.TextLength > 0) viewSubtitle.Text = visibleItems.Count + (visibleItems.Count == 1 ? " match" : " matches") + "  \u00b7  Esc clears the search";
            List<DataGridViewRow> built = new List<DataGridViewRow>(visibleItems.Count);
            foreach (GiveawayItem i in visibleItems)
            {
                bool ended = string.Equals(i.Status, "ended", StringComparison.OrdinalIgnoreCase);
                string deadline = i.Deadline ?? "-";
                if ((showingHistory || showingJoined) && ended && (string.IsNullOrWhiteSpace(deadline) || deadline == "-")) deadline = "Ended";
                
                DateTime shownDate;
                if (DateTime.TryParse(deadline, out shownDate))
                    deadline = shownDate.ToString("MMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture);
                string promoCode = string.IsNullOrWhiteSpace(i.PromoCode) ? "-" : i.PromoCode;
                string minimumDeposit = string.IsNullOrWhiteSpace(i.MinimumDeposit) ? "-" : i.MinimumDeposit;
                DataGridViewRow gridRow = new DataGridViewRow();
                gridRow.CreateCells(grid, i.Creator ?? "Unknown", i.Url ?? "", i.Ticket ?? "-", promoCode, minimumDeposit, deadline, "");
                gridRow.Tag = i;
                gridRow.Height = 56;

                built.Add(gridRow);
            }

            grid.SuspendLayout();
            grid.Rows.Clear();
            if (built.Count > 0) grid.Rows.AddRange(built.ToArray());
            UpdateSortGlyph();
            if (selectedUrl != null)
            {
                foreach (DataGridViewRow row in grid.Rows)
                    if (string.Equals(((GiveawayItem)row.Tag).Url, selectedUrl, StringComparison.OrdinalIgnoreCase))
                    { grid.CurrentCell = row.Cells[0]; break; }
            }
            if (firstRow >= 0 && grid.Rows.Count > 0)
                grid.FirstDisplayedScrollingRowIndex = Math.Min(firstRow, grid.Rows.Count - 1);
            grid.ResumeLayout();
            grid.Invalidate();
        }

        private void UpdateLogs()
        {
            if (IsDisposed || Disposing || !showingLogs) return;
            string snapshot = ActivityLog.Snapshot(ref logVersion);
            if (snapshot != null)
            {
                int selection = logText.SelectionStart;
                bool follow = selection >= Math.Max(0, logText.TextLength - 1);
                logText.Text = snapshot;
                logText.SelectionStart = follow ? logText.TextLength : Math.Min(selection, logText.TextLength);
                if (follow) logText.ScrollToCaret();
            }
            try
            {
                viewSubtitle.Text = "Browser processes " + BrowserProcessManager.ActiveProcessCount + "/5  \u00b7  Queued pages " + Scanner.QueuedRenders + "  \u00b7  Latest 2,000 events";
            }
            catch { viewSubtitle.Text = "Browser count unavailable — check cleanup errors below"; }
        }

        private void UpdateSortGlyph()
        {
            foreach (DataGridViewColumn c in grid.Columns)
                c.HeaderCell.SortGlyphDirection = SortOrder.None;
            if (grid.Columns.Contains(sortColumn))
                grid.Columns[sortColumn].HeaderCell.SortGlyphDirection = sortAscending ? SortOrder.Ascending : SortOrder.Descending;
        }

        private string FormatTime(string iso)
        {
            if (string.IsNullOrWhiteSpace(iso)) return "never";
            DateTime dt;
            if (DateTime.TryParse(iso, out dt))
                return dt.ToLocalTime().ToString("MMM d, h:mm tt", System.Globalization.CultureInfo.InvariantCulture);
            return "never";
        }

        private AppData CloneDataForScan(AppData source)
        {
            // Scanner methods merge results into the AppData instance they receive.
            // Give the worker its own copy so the UI can safely switch tabs, sort,
            // search and copy values while a long scan is running.
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            string json = js.Serialize(source ?? new AppData());
            AppData clone = js.Deserialize<AppData>(json);
            if (clone == null) clone = new AppData();
            if (clone.Items == null) clone.Items = new List<GiveawayItem>();
            return clone;
        }

        private void SetScanning(bool value, string message)
        {
            if (IsDisposed || Disposing) return;
            scanning = value;
            refreshButton.Enabled = !value;
            deepButton.Enabled = !value;
            addButton.Enabled = !value;

            // View navigation must stay usable during long scans. Deep Search and
            // Refresh now work on a cloned data snapshot, so switching between
            // Active / History / Joined cannot race with the scanner.
            historyButton.Enabled = true;
            joinedButton.Enabled = true;

            scanStatus.Text = value ? message : "Ready";
            ActivityLog.Write("APP", value ? message : "Operation finished");
            scanStatus.ForeColor = value ? Warning : Muted;
            if (scanStrip != null) scanStrip.Active = value;
            statusDotColor = value ? Warning : Success;
            if (statusDot != null) statusDot.Invalidate();

            // Never force the Windows busy cursor. Scanning is done on a worker
            // thread, so the UI message pump stays responsive while the status bar
            // reports progress. Keep each control's normal cursor (hand/default).
            UseWaitCursor = false;
            Cursor = Cursors.Default;
        }

        private async Task RefreshAsync(bool manual)
        {
            if (scanning) return;
            SetScanning(true, manual ? "Checking saved giveaway pages..." : "Refreshing live status...");
            try
            {
                // Some giveaway pages require a headless Chromium/CDP fallback.
                // Run the entire scan away from the WinForms UI thread so Chromium
                // startup/parsing can never make Windows show the spinning busy cursor.
                AppData scanData = CloneDataForScan(data);
                AppData refreshed = await Task.Run(async delegate
                {
                    return await Scanner.RefreshSavedAsync(scanData);
                });
                if (IsDisposed || Disposing) return;
                data = refreshed;
                Render();
            }
            catch (Exception ex)
            {
                ActivityLog.Write("ERROR", "Refresh failed: " + ex.Message);
                if (!IsDisposed && !Disposing) MessageBox.Show(this, ex.Message, "Refresh failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { SetScanning(false, ""); }
        }

        private async Task DeepSearchAsync()
        {
            if (scanning) return;
            SetScanning(true, "Deep searching partners, YouTube, Telegram and social sources...");
            try
            {
                AppData scanData = CloneDataForScan(data);
                AppData scanned = await Task.Run(async delegate
                {
                    return await Scanner.DeepScanAsync(scanData);
                });
                if (IsDisposed || Disposing) return;
                data = scanned;
                Render();
                int ac = data.Items.Count(delegate(GiveawayItem i) { return string.Equals(i.Status, "active", StringComparison.OrdinalIgnoreCase) && !i.Joined; });
                scanStatus.Text = "Deep Search complete  •  " + ac + " active giveaway" + (ac == 1 ? "" : "s");
                scanStatus.ForeColor = Success;
                await Task.Delay(2200);
            }
            catch (Exception ex)
            {
                ActivityLog.Write("ERROR", "Deep Search failed: " + ex.Message);
                if (!IsDisposed && !Disposing) MessageBox.Show(this, ex.Message, "Deep Search failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { SetScanning(false, ""); }
        }

        private async Task AddLinkAsync()
        {
            if (scanning) return;
            using (AddLinkForm f = new AddLinkForm())
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                if (scanning) return;
                if (string.IsNullOrWhiteSpace(f.GiveawayUrl)) return;
                SetScanning(true, "Validating giveaway page...");
                try
                {
                    string creatorName = f.CreatorName;
                    string giveawayUrl = f.GiveawayUrl;
                    GiveawayItem item = await Task.Run(async delegate
                    {
                        return await Scanner.ValidateOneAsync(creatorName, giveawayUrl);
                    });
                    if (IsDisposed || Disposing) return;
                    Scanner.MergeOne(data, item);
                    Render();
                    if (item.Status == "active")
                        MessageBox.Show(this, "Running giveaway added to Active.", "Added", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    else if (item.Status == "ended")
                        MessageBox.Show(this, "This giveaway is ended, so it was saved in History.", "Saved to History", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    else
                        MessageBox.Show(this, "The page was saved, but it could not be verified as active or ended yet.", "Unverified", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    ActivityLog.Write("ERROR", "Add link failed: " + ex.Message);
                    if (!IsDisposed && !Disposing) MessageBox.Show(this, ex.Message, "Invalid link", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                finally { SetScanning(false, ""); }
            }
        }
    }

    static class Program
    {
        internal static readonly string DisplayTitle = "SkinClub GW Finder v" + typeof(Program).Assembly.GetName().Version.ToString(3);

        [STAThread]
        static void Main()
        {
            // Two independent shutdown hooks plus the Job Object itself make browser
            // cleanup reliable for normal close, managed shutdown, and most crashes.
            Application.ApplicationExit += delegate { BrowserProcessManager.Shutdown(); };
            AppDomain.CurrentDomain.ProcessExit += delegate { BrowserProcessManager.Shutdown(); };
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                Application.Run(new MainForm());
            }
            finally
            {
                BrowserProcessManager.Shutdown();
            }
        }
    }
}

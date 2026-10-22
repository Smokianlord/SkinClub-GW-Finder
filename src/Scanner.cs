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
        public static readonly SemaphoreSlim ScanLock = new SemaphoreSlim(1, 1);

        // Built-in creators. More can be added without recompiling through ExtraCreators in settings.json.
        private static readonly Dictionary<string, string> BuiltInCreators = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
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
            {"Joaco", "joaco.club"}, {"iNoToRiOuS", "inotorious.club"}, {"TCK10", "tck10.club"},
            {"Karrigan", "karrigan.club"}, {"Theasylum", "theasylum.club"}
        };

        private static readonly Dictionary<string, string> CreatorDomains = MergeCreators();

        private static Dictionary<string, string> MergeCreators()
        {
            Dictionary<string, string> all = new Dictionary<string, string>(BuiltInCreators, StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> extra in AppSettings.Current.ExtraCreators) all[extra.Key] = extra.Value;
            return all;
        }

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
        // HTTP discovery stays concurrent. Rendered pages run in parallel up to BrowserConcurrency,
        // each in its own Windows job that caps that browser's processes and memory.
        private static readonly SemaphoreSlim RenderFallbackThrottle =
            new SemaphoreSlim(AppSettings.Current.BrowserConcurrency, AppSettings.Current.BrowserConcurrency);
        // Live progress for the Logs tab: page checks in flight, finished, and expected this scan.
        private static int activeChecks, checksDone, checksTotal;
        public static int ActiveChecks { get { return Volatile.Read(ref activeChecks); } }
        public static int ChecksDone { get { return Volatile.Read(ref checksDone); } }
        public static int ChecksTotal { get { return Volatile.Read(ref checksTotal); } }
        private static void ResetChecks(int total)
        {
            Volatile.Write(ref checksDone, 0);
            Volatile.Write(ref checksTotal, total);
        }

        private static int queuedRenders;
        public static int QueuedRenders { get { return Volatile.Read(ref queuedRenders); } }
        private static readonly object RenderCacheGate = new object();
        private static readonly Dictionary<string, Tuple<DateTime, RenderedFields>> RenderCache =
            new Dictionary<string, Tuple<DateTime, RenderedFields>>(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan RenderCacheLifetime = TimeSpan.FromMinutes(10);
        // How long to wait before launching the browser again for a page whose
        // promocode / deposit / deadline could not be found on the last attempt.
        private static readonly TimeSpan MetadataRetryInterval = TimeSpan.FromHours(AppSettings.Current.MetadataRetryHours);
        // Snapshot of already-saved items, keyed by GiveawayKey, for the scan in progress.
        private static volatile Dictionary<string, GiveawayItem> KnownItems;

        // Upper bounds on how much of a response is ever held in memory. Giveaway pages are small;
        // YouTube watch pages are large, but the description sits near the top of the document.
        private const int MaxPageBytes = 1024 * 1024;
        private const int MaxDiscoveryBytes = 2 * 1024 * 1024;

        private static async Task<string> ReadBodyCappedAsync(HttpResponseMessage response, int maxBytes)
        {
            using (Stream stream = await response.Content.ReadAsStreamAsync())
            using (MemoryStream buffer = new MemoryStream())
            {
                byte[] chunk = new byte[16 * 1024];
                int total = 0, read;
                while (total < maxBytes && (read = await stream.ReadAsync(chunk, 0, Math.Min(chunk.Length, maxBytes - total))) > 0)
                {
                    buffer.Write(chunk, 0, read);
                    total += read;
                }
                Encoding encoding = Encoding.UTF8;
                string charset = response.Content.Headers.ContentType == null ? null : response.Content.Headers.ContentType.CharSet;
                if (!string.IsNullOrWhiteSpace(charset))
                {
                    try { encoding = Encoding.GetEncoding(charset.Trim('"', '\'')); }
                    catch (ArgumentException) { }
                }
                return encoding.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            }
        }

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
            ServicePointManager.DefaultConnectionLimit = 128;
            HttpClientHandler h = new HttpClientHandler();
            h.AllowAutoRedirect = true;
            h.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            HttpClient c = new HttpClient(h);
            c.Timeout = TimeSpan.FromSeconds(AppSettings.Current.HttpTimeoutSeconds);
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
            // Most fetched pages (search results, social posts) contain no giveaway domain at all;
            // skip the decode/regex work, and its temporary string copies, for those.
            if (string.IsNullOrEmpty(raw) || raw.IndexOf(".club", StringComparison.OrdinalIgnoreCase) < 0) return list;
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

    }
}

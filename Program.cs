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
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;


[assembly: AssemblyTitle("SkinClub GW Finder")]
[assembly: AssemblyProduct("SkinClub GW Finder")]
[assembly: AssemblyDescription("SkinClub creator giveaway monitor")]
[assembly: AssemblyVersion("1.2.2.0")]
[assembly: AssemblyFileVersion("1.2.2.0")]
[assembly: AssemblyInformationalVersion("1.2.2")]

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
                foreach (GiveawayItem item in data.Items)
                {
                    if (item == null) continue;
                    string promo = (item.PromoCode ?? "").Trim();
                    string upper = promo.ToUpperInvariant();
                    if (upper == "SKIN" || upper == "CLUB" || upper == "BONUS" ||
                        upper == "PRIZE" || upper == "POOL" || upper == "DISCOUNT" ||
                        upper == "PROMO" || upper == "PROMOCODE" || upper == "CODE")
                        item.PromoCode = "-";
                }
                PruneOldHistory(data);
                return data;
            }
            catch
            {
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
                if (File.Exists(DataFile)) File.Delete(DataFile);
                File.Move(temp, DataFile);
            }
        }

        private static void PruneOldHistory(AppData data)
        {
            if (data == null || data.Items == null) return;

            // Keep History for one full calendar month from the moment an item
            // actually enters History. Do NOT infer retention from the DDMMYY URL:
            // that was destructive for existing data and could wipe the whole
            // History list immediately after an upgrade. Joined items are always
            // preserved.
            DateTime now = DateTime.UtcNow;
            DateTime cutoff = now.AddMonths(-1);

            data.Items.RemoveAll(delegate(GiveawayItem item)
            {
                if (item == null || item.Joined) return false;
                if (!string.Equals(item.Status, "ended", StringComparison.OrdinalIgnoreCase))
                {
                    item.HistorySince = null;
                    return false;
                }

                DateTime historySince;
                if (string.IsNullOrWhiteSpace(item.HistorySince) ||
                    !DateTime.TryParse(item.HistorySince, null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out historySince))
                {
                    // Existing History from older versions gets a fresh retention
                    // clock instead of being deleted on first launch.
                    item.HistorySince = now.ToString("o");
                    return false;
                }

                return historySince.ToUniversalTime() <= cutoff;
            });
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
            @"^[ \t]*(?:time[ \t]+to[ \t]+completion|time[ \t]+remaining|tiempo[ \t]+restante|tempo[ \t]+restante|th(?:ờ|o)i[ \t]+gian[ \t]+c(?:ò|o)n[ \t]+l(?:ạ|a)i|deadline|ends?[ \t]+in|ending[ \t]+in)[ \t]*[:\-]?[ \t]*([^\r\n|]{0,80})[ \t]*$",
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
        private static readonly SemaphoreSlim RenderFallbackThrottle = new SemaphoreSlim(3, 3);

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
            ServicePointManager.DefaultConnectionLimit = 40;
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
            @"(?:replenish|top[ \t-]*up|deposit|recharge)[\s\S]{0,120}?(?:balance|account)?[\s\S]{0,60}?(?:from|minimum(?:\s+deposit)?(?:\s+of)?|at\s+least)\s*((?:[$€£]\s*)?[0-9]+(?:[.,][0-9]{1,2})?\s*(?:USD|EUR|GBP|USDT|RUB|PLN|BRL|ARS|UAH|BDT)?|[0-9]+(?:[.,][0-9]{1,2})?\s*[$€£])[\s\S]{0,100}?(?:promo(?:tional)?[\s_\-]*code|promocode|c[oó]digo\s*(?:promocional|promo)?)[ \t\r\n:=-]*([A-Za-z0-9][A-Za-z0-9_\-]{2,63})",
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

        private static async Task<string> DevToolsEvaluateAsync(string websocketUrl, string expression)
        {
            if (string.IsNullOrWhiteSpace(websocketUrl)) return "";
            using (ClientWebSocket ws = new ClientWebSocket())
            {
                CancellationTokenSource connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await ws.ConnectAsync(new Uri(websocketUrl), connectCts.Token);

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
                await ws.SendAsync(new ArraySegment<byte>(outgoing), WebSocketMessageType.Text, true, CancellationToken.None);

                using (MemoryStream ms = new MemoryStream())
                {
                    byte[] buffer = new byte[8192];
                    DateTime expires = DateTime.UtcNow.AddSeconds(8);
                    while (DateTime.UtcNow < expires && ws.State == WebSocketState.Open)
                    {
                        CancellationTokenSource receiveCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        WebSocketReceiveResult rr;
                        try
                        {
                            rr = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), receiveCts.Token);
                        }
                        catch (OperationCanceledException) { continue; }

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

        private static async Task<RenderedFields> FetchRenderedFieldsAsync(string url)
        {
            RenderedFields best = new RenderedFields();
            if (string.IsNullOrWhiteSpace(url)) return best;
            string browser = FindChromiumBrowser();
            if (string.IsNullOrWhiteSpace(browser)) return best;

            await RenderFallbackThrottle.WaitAsync();
            string profileDir = null;
            Process process = null;
            try
            {
                int port = GetFreeTcpPort();
                profileDir = Path.Combine(Path.GetTempPath(), "SkinClubGWFinder_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(profileDir);

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = browser;
                psi.Arguments = "--headless=new --disable-gpu --disable-extensions --no-first-run --no-default-browser-check " +
                                "--disable-sync --mute-audio --disable-background-networking --remote-allow-origins=* --remote-debugging-port=" + port +
                                " --user-data-dir=\"" + profileDir + "\" \"" + url.Replace("\"", "") + "\"";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;

                process = new Process();
                process.StartInfo = psi;
                process.Start();
                // Drain pipes immediately so Chromium can never block on logging.
                Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                Task<string> stderrTask = process.StandardError.ReadToEndAsync();

                HttpClientHandler localHandler = new HttpClientHandler();
                localHandler.UseProxy = false;
                HttpClient localClient = new HttpClient(localHandler);
                localClient.Timeout = TimeSpan.FromSeconds(2);
                string websocketUrl = "";
                DateTime discoveryDeadline = DateTime.UtcNow.AddSeconds(8);
                while (DateTime.UtcNow < discoveryDeadline && string.IsNullOrWhiteSpace(websocketUrl))
                {
                    try
                    {
                        string tabsJson = await localClient.GetStringAsync("http://127.0.0.1:" + port + "/json/list");
                        JavaScriptSerializer serializer = new JavaScriptSerializer();
                        object[] tabs = serializer.Deserialize<object[]>(tabsJson);
                        foreach (object tabObj in tabs)
                        {
                            Dictionary<string, object> tab = tabObj as Dictionary<string, object>;
                            if (tab == null) continue;
                            string type = tab.ContainsKey("type") ? JsonStringValue(tab["type"]) : "";
                            string tabUrl = tab.ContainsKey("url") ? JsonStringValue(tab["url"]) : "";
                            string ws = tab.ContainsKey("webSocketDebuggerUrl") ? JsonStringValue(tab["webSocketDebuggerUrl"]) : "";
                            if (type == "page" && !string.IsNullOrWhiteSpace(ws))
                            {
                                websocketUrl = ws;
                                if (!string.IsNullOrWhiteSpace(tabUrl) && tabUrl.IndexOf(new Uri(url).Host, StringComparison.OrdinalIgnoreCase) >= 0)
                                    break;
                            }
                        }
                    }
                    catch { }
                    if (string.IsNullOrWhiteSpace(websocketUrl)) await Task.Delay(250);
                }

                if (string.IsNullOrWhiteSpace(websocketUrl)) return best;

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
                    string renderedData = await DevToolsEvaluateAsync(websocketUrl, expression);
                    string metadata = await DevToolsEvaluateAsync(websocketUrl, metadataExpression);

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
                            return best;
                    }
                    await Task.Delay(500);
                }
                return best;
            }
            catch { return best; }
            finally
            {
                if (process != null)
                {
                    try { if (!process.HasExited) process.Kill(); } catch { }
                    try { process.Dispose(); } catch { }
                }
                if (!string.IsNullOrWhiteSpace(profileDir))
                {
                    try { Directory.Delete(profileDir, true); } catch { }
                }
                RenderFallbackThrottle.Release();
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
            Match step = ParticipationStepRegex.Match(decoded);
            if (step.Success)
            {
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
                GiveawayItem item = new GiveawayItem
                {
                    Creator = InferCreator(c.Url, c.Creator), Url = CleanDiscoveredUrl(c.Url), Status = "unknown", Ticket = "-", PromoCode = "-", MinimumDeposit = "-", Deadline = "-",
                    LastChecked = DateTime.UtcNow.ToString("o"), Source = c.Source, Error = null
                };
                try
                {
                    HttpResponseMessage r = await Client.GetAsync(c.Url);
                    item.Url = CleanDiscoveredUrl(r.RequestMessage.RequestUri.ToString());
                    if (!r.IsSuccessStatusCode)
                    {
                        item.Error = "HTTP " + (int)r.StatusCode;
                        return item;
                    }
                    string html = await r.Content.ReadAsStringAsync();
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
                    bool missingMetadata = item.PromoCode == "-" || item.MinimumDeposit == "-";
                    bool needsRenderedDeadline = activePage &&
                        (item.Deadline == "-" || Regex.IsMatch(html, @"time\s*to\s*completion", RegexOptions.IgnoreCase));

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
                SemaphoreSlim throttle = new SemaphoreSlim(20, 20);
                List<Task<GiveawayItem>> tasks = new List<Task<GiveawayItem>>();
                foreach (Candidate c in cs) tasks.Add(ValidateAsync(c, throttle));
                GiveawayItem[] updates = tasks.Count == 0 ? new GiveawayItem[0] : await Task.WhenAll(tasks);
                MergeItems(data, updates);
                data.LastScan = DateTime.UtcNow.ToString("o");
                DataStore.Save(data);
                return data;
            }
            finally { ScanLock.Release(); }
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
            try
            {
                HttpResponseMessage r = await Client.GetAsync(url);
                if (!r.IsSuccessStatusCode) return "";
                return await r.Content.ReadAsStringAsync();
            }
            catch { return ""; }
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
            SemaphoreSlim partnerSem = new SemaphoreSlim(8, 8);
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

            SemaphoreSlim sem = new SemaphoreSlim(10, 10);
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
            SemaphoreSlim searchSem = new SemaphoreSlim(8, 8);
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

            SemaphoreSlim sem = new SemaphoreSlim(8, 8);
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
            SemaphoreSlim sem = new SemaphoreSlim(8, 8);
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
                HashSet<string> known = new HashSet<string>(data.Items.Select(delegate(GiveawayItem x) { return GiveawayKey(x.Url); }), StringComparer.OrdinalIgnoreCase);

                List<GiveawayItem> useful = new List<GiveawayItem>();
                SemaphoreSlim throttle = new SemaphoreSlim(28, 28);
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
                return data;
            }
            finally { ScanLock.Release(); }
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

    public class AddLinkForm : Form
    {
        public string CreatorName { get { return creatorBox.Text.Trim(); } }
        public string GiveawayUrl { get { return urlBox.Text.Trim(); } }
        private TextBox creatorBox;
        private TextBox urlBox;

        private readonly Color Bg = Color.FromArgb(20, 28, 45);
        private readonly Color Card = Color.FromArgb(34, 45, 68);
        private readonly Color Line = Color.FromArgb(75, 91, 124);
        private readonly Color TextColor = Color.FromArgb(246, 249, 255);
        private readonly Color Muted = Color.FromArgb(174, 190, 216);
        private readonly Color Accent = Color.FromArgb(132, 102, 255);

        public AddLinkForm()
        {
            Text = "Add giveaway";
            Width = 620;
            Height = 300;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Bg;
            ForeColor = TextColor;
            Font = new Font("Segoe UI", 9F);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            Panel accent = new Panel();
            accent.Dock = DockStyle.Top;
            accent.Height = 4;
            accent.BackColor = Accent;
            Controls.Add(accent);

            Label title = new Label();
            title.Text = "Add giveaway link";
            title.Left = 24;
            title.Top = 24;
            title.Width = 430;
            title.Height = 32;
            title.Font = new Font("Segoe UI Semibold", 16F);
            title.ForeColor = TextColor;
            Controls.Add(title);

            Label sub = new Label();
            sub.Text = "Paste a direct dated creator giveaway URL. The page will be validated before it is saved.";
            sub.Left = 25;
            sub.Top = 59;
            sub.Width = 550;
            sub.Height = 34;
            sub.ForeColor = Muted;
            Controls.Add(sub);

            Label l1 = MakeLabel("Creator (optional)", 25, 102, 200);
            creatorBox = MakeTextBox(25, 124, 550);
            Label l2 = MakeLabel("Direct giveaway URL", 25, 158, 200);
            urlBox = MakeTextBox(25, 180, 550);
            urlBox.Text = "https://creator.club/010926/";

            Button cancel = MakeButton("Cancel", 375, 222, 92, false);
            cancel.DialogResult = DialogResult.Cancel;
            Button add = MakeButton("Validate + Add", 475, 222, 100, true);
            add.DialogResult = DialogResult.OK;

            Controls.Add(l1);
            Controls.Add(creatorBox);
            Controls.Add(l2);
            Controls.Add(urlBox);
            Controls.Add(cancel);
            Controls.Add(add);
            AcceptButton = add;
            CancelButton = cancel;
        }

        private Label MakeLabel(string text, int x, int y, int w)
        {
            Label l = new Label();
            l.Text = text;
            l.Left = x;
            l.Top = y;
            l.Width = w;
            l.Height = 20;
            l.ForeColor = Muted;
            l.Font = new Font("Segoe UI Semibold", 8.5F);
            return l;
        }

        private TextBox MakeTextBox(int x, int y, int w)
        {
            TextBox t = new TextBox();
            t.Left = x;
            t.Top = y;
            t.Width = w;
            t.Height = 28;
            t.BackColor = Card;
            t.ForeColor = TextColor;
            t.BorderStyle = BorderStyle.FixedSingle;
            return t;
        }

        private Button MakeButton(string text, int x, int y, int w, bool primary)
        {
            Button b = new Button();
            b.Text = text;
            b.Left = x;
            b.Top = y;
            b.Width = w;
            b.Height = 36;
            b.FlatStyle = FlatStyle.Popup;
            b.UseVisualStyleBackColor = false;
            b.BackColor = primary ? Accent : Card;
            b.ForeColor = TextColor;
            b.Font = new Font("Segoe UI Semibold", 9F);
            b.FlatAppearance.BorderColor = primary ? Accent : Line;
            b.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(126, 110, 255) : Color.FromArgb(25, 32, 46);
            b.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(96, 78, 235) : Color.FromArgb(20, 26, 38);
            return b;
        }
    }

    public class DepthButton : Button
    {
        public Color TopColor { get; set; }
        public Color BottomColor { get; set; }
        public Color BorderColor3D { get; set; }
        private bool hover;
        private bool pressed;

        public DepthButton()
        {
            TopColor = Color.FromArgb(42, 55, 82);
            BottomColor = Color.FromArgb(25, 34, 54);
            BorderColor3D = Color.FromArgb(71, 88, 123);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs mevent) { if (mevent.Button == MouseButtons.Left) pressed = true; Invalidate(); base.OnMouseDown(mevent); }
        protected override void OnMouseUp(MouseEventArgs mevent) { pressed = false; Invalidate(); base.OnMouseUp(mevent); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        private static Color Blend(Color a, Color b, double amount)
        {
            if (amount < 0) amount = 0;
            if (amount > 1) amount = 1;
            return Color.FromArgb(
                (int)Math.Round(a.R * (1.0 - amount) + b.R * amount),
                (int)Math.Round(a.G * (1.0 - amount) + b.G * amount),
                (int)Math.Round(a.B * (1.0 - amount) + b.B * amount));
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

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent == null ? BackColor : Parent.BackColor);

            Rectangle shadowRect = new Rectangle(2, 5, Math.Max(1, Width - 5), Math.Max(1, Height - 7));
            using (GraphicsPath shadowPath = RoundedRect(shadowRect, 6))
            using (SolidBrush shadow = new SolidBrush(Color.FromArgb(95, 0, 0, 0)))
                g.FillPath(shadow, shadowPath);

            int y = pressed ? 3 : 1;
            Rectangle face = new Rectangle(1, y, Math.Max(1, Width - 4), Math.Max(1, Height - 7));
            Color top = TopColor;
            Color bottom = BottomColor;
            if (hover && Enabled)
            {
                top = Blend(top, Color.White, 0.10);
                bottom = Blend(bottom, Color.White, 0.05);
            }
            if (!Enabled)
            {
                top = Blend(top, Color.FromArgb(80, 84, 94), 0.55);
                bottom = Blend(bottom, Color.FromArgb(58, 62, 72), 0.55);
            }

            using (GraphicsPath facePath = RoundedRect(face, 6))
            using (LinearGradientBrush fill = new LinearGradientBrush(face, top, bottom, LinearGradientMode.Vertical))
            using (Pen border = new Pen(Enabled ? BorderColor3D : Blend(BorderColor3D, Color.Gray, 0.55)))
            {
                g.FillPath(fill, facePath);
                g.DrawPath(border, facePath);
            }

            // Thin top highlight + lower edge create real depth without looking glossy/toy-like.
            using (Pen hi = new Pen(Color.FromArgb(pressed ? 35 : 85, 255, 255, 255)))
                g.DrawLine(hi, 8, face.Top + 1, Math.Max(8, Width - 11), face.Top + 1);
            using (Pen low = new Pen(Color.FromArgb(70, 0, 0, 0)))
                g.DrawLine(low, 8, face.Bottom - 1, Math.Max(8, Width - 11), face.Bottom - 1);

            Color text = Enabled ? ForeColor : Color.FromArgb(145, 153, 168);
            TextRenderer.DrawText(g, Text, Font, face, text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
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

            using (GraphicsPath path = RoundedRect(pill, 10))
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

    public class MainForm : Form
    {
        private AppData data;
        private DataGridView grid;
        private Button historyButton;
        private Button joinedButton;
        private Button refreshButton;
        private Button deepButton;
        private Button addButton;
        private Label activeCount;
        private Label historyCount;
        private Label joinedCount;
        private Label lastCheck;
        private Label viewTitle;
        private Label viewSubtitle;
        private Label scanStatus;
        private TextBox filterBox;
        private ComboBox filterFieldBox;
        private TableLayoutPanel cardPanelRef;
        private TableLayoutPanel cardHeaderPanel;
        private TableLayoutPanel titlePanelRef;
        private TableLayoutPanel filterPanelRef;
        private Panel activityBarRef;
        private Panel viewAccentRef;
        private bool showingHistory = false;
        private bool showingJoined = false;
        private bool scanning = false;
        private System.Windows.Forms.Timer autoTimer;
        private CopyToastControl copyToast;
        private string sortColumn = "Creator";
        private bool sortAscending = true;

        // Restrained product-dashboard palette: neutral ink surfaces with precise accent usage.
        private readonly Color Bg = Color.FromArgb(9, 14, 25);
        private readonly Color Surface = Color.FromArgb(17, 24, 39);
        private readonly Color Surface2 = Color.FromArgb(13, 20, 34);
        private readonly Color Surface3 = Color.FromArgb(27, 37, 56);
        private readonly Color Line = Color.FromArgb(43, 55, 78);
        private readonly Color TextColor = Color.FromArgb(244, 247, 252);
        private readonly Color Muted = Color.FromArgb(145, 158, 181);
        private readonly Color Accent = Color.FromArgb(112, 104, 255);
        private readonly Color Accent2 = Color.FromArgb(56, 189, 248);
        private readonly Color Success = Color.FromArgb(45, 212, 191);
        private readonly Color Warning = Color.FromArgb(251, 191, 36);
        private readonly Color Danger = Color.FromArgb(251, 113, 133);
        private readonly Color PriorityGreen = Color.FromArgb(74, 222, 128);
        private readonly Color PriorityYellow = Color.FromArgb(250, 204, 21);
        private readonly Color PriorityRed = Color.FromArgb(248, 113, 113);
        private readonly Color ActiveView = Color.FromArgb(45, 212, 191);
        private readonly Color HistoryView = Color.FromArgb(245, 158, 11);
        private readonly Color JoinedView = Color.FromArgb(167, 139, 250);
        private readonly Color ButtonTop = Color.FromArgb(43, 55, 82);
        private readonly Color ButtonBottom = Color.FromArgb(24, 33, 52);
        private readonly Color ButtonBorder = Color.FromArgb(66, 82, 112);

        public MainForm()
        {
            Text = "SkinClub GW Finder v1.2.2";
            Width = 1240;
            Height = 800;
            MinimumSize = new Size(1040, 680);
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

            data = DataStore.Load();
            // History is persisted data. Do not reset ended giveaways back to
            // unknown on startup; that made the History tab rebuild itself on
            // every launch instead of loading the saved classification.
            BuildUi();
            Render();

            Shown += async delegate { await RefreshAsync(false); };
            autoTimer = new System.Windows.Forms.Timer();
            autoTimer.Interval = 10 * 60 * 1000;
            autoTimer.Tick += async delegate { if (!scanning) await RefreshAsync(false); };
            autoTimer.Start();
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

        private void BuildUi()
        {
            SuspendLayout();

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.BackColor = Bg;
            root.Padding = new Padding(22, 18, 22, 22);
            root.ColumnCount = 1;
            root.RowCount = 4;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 108F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            Controls.Add(root);

            // Brand header
            TableLayoutPanel header = new TableLayoutPanel();
            header.Dock = DockStyle.Fill;
            header.Margin = new Padding(0, 0, 0, 12);
            header.BackColor = Color.FromArgb(14, 21, 36);
            header.ColumnCount = 2;
            header.RowCount = 1;
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48F));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52F));
            header.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.Controls.Add(header, 0, 0);

            Panel brand = new Panel();
            brand.Dock = DockStyle.Fill;
            brand.BackColor = Color.FromArgb(14, 21, 36);
            brand.Padding = new Padding(18, 15, 8, 12);
            header.Controls.Add(brand, 0, 0);

            PictureBox logo = new PictureBox();
            logo.Left = 18;
            logo.Top = 17;
            logo.Width = 54;
            logo.Height = 54;
            logo.SizeMode = PictureBoxSizeMode.Zoom;
            try { logo.Image = Icon.ExtractAssociatedIcon(Application.ExecutablePath).ToBitmap(); } catch { }
            brand.Controls.Add(logo);

            Label title = new Label();
            title.Text = "SkinClub GW Finder v1.2.2";
            title.Left = 86;
            title.Top = 17;
            title.Width = 470;
            title.Height = 34;
            title.Font = new Font("Segoe UI Semibold", 20F);
            title.ForeColor = TextColor;
            brand.Controls.Add(title);

            Label sub = new Label();
            sub.Text = "Live creator giveaway monitor  •  Active pages are verified automatically";
            sub.Left = 88;
            sub.Top = 56;
            sub.Width = 520;
            sub.Height = 24;
            sub.ForeColor = Muted;
            sub.Font = new Font("Segoe UI", 9F);
            brand.Controls.Add(sub);

            Panel brandAccent = new Panel();
            brandAccent.Dock = DockStyle.Left;
            brandAccent.Width = 4;
            brandAccent.BackColor = Accent;
            brand.Controls.Add(brandAccent);
            brandAccent.BringToFront();

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.Dock = DockStyle.Fill;
            actions.Padding = new Padding(8, 26, 16, 0);
            actions.FlowDirection = FlowDirection.RightToLeft;
            actions.WrapContents = false;
            actions.BackColor = Color.FromArgb(14, 21, 36);
            header.Controls.Add(actions, 1, 0);

            deepButton = MakeHeaderButton("Deep Search", 116, true);
            refreshButton = MakeHeaderButton("Refresh", 84, false);
            historyButton = MakeHeaderButton("History", 84, false);
            joinedButton = MakeHeaderButton("Joined", 82, false);
            addButton = MakeHeaderButton("+ Add link", 92, false);
            actions.Controls.Add(deepButton);
            actions.Controls.Add(refreshButton);
            actions.Controls.Add(historyButton);
            actions.Controls.Add(joinedButton);
            actions.Controls.Add(addButton);

            addButton.Click += async delegate { await AddLinkAsync(); };
            historyButton.Click += delegate
            {
                if (showingHistory) showingHistory = false;
                else { showingHistory = true; showingJoined = false; }
                Render();
            };
            joinedButton.Click += delegate
            {
                if (showingJoined) showingJoined = false;
                else { showingJoined = true; showingHistory = false; }
                Render();
            };
            refreshButton.Click += async delegate { await RefreshAsync(true); };
            deepButton.Click += async delegate { await DeepSearchAsync(); };

            // Status / stats row
            TableLayoutPanel toolbar = new TableLayoutPanel();
            toolbar.Dock = DockStyle.Fill;
            toolbar.Margin = new Padding(0, 0, 0, 12);
            toolbar.BackColor = Bg;
            toolbar.ColumnCount = 1;
            toolbar.RowCount = 1;
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.Controls.Add(toolbar, 0, 1);

            FlowLayoutPanel stats = new FlowLayoutPanel();
            stats.Dock = DockStyle.Fill;
            stats.FlowDirection = FlowDirection.LeftToRight;
            stats.WrapContents = false;
            stats.BackColor = Bg;
            toolbar.Controls.Add(stats, 0, 0);

            activeCount = MakeStatLabel("ACTIVE  0", 118, Success);
            historyCount = MakeStatLabel("HISTORY  0", 126, Muted);
            joinedCount = MakeStatLabel("JOINED  0", 118, Accent);
            lastCheck = MakeStatLabel("LAST CHECK  NEVER", 222, Accent2);
            stats.Controls.Add(activeCount);
            stats.Controls.Add(historyCount);
            stats.Controls.Add(joinedCount);
            stats.Controls.Add(lastCheck);

            // Dedicated activity/search-status row. Keeping this outside the table/card
            // prevents DPI scaling or docking from clipping long search messages.
            Panel activityBar = new Panel();
            activityBarRef = activityBar;
            activityBar.Dock = DockStyle.Fill;
            activityBar.Margin = new Padding(0, 0, 0, 8);
            activityBar.Padding = new Padding(12, 0, 12, 0);
            activityBar.BackColor = Color.FromArgb(12, 19, 32);
            root.Controls.Add(activityBar, 0, 2);

            scanStatus = new Label();
            scanStatus.Dock = DockStyle.Fill;
            scanStatus.ForeColor = Warning;
            scanStatus.Font = new Font("Segoe UI Semibold", 9F);
            scanStatus.TextAlign = ContentAlignment.MiddleLeft;
            scanStatus.AutoEllipsis = true;
            scanStatus.Text = "Ready";
            activityBar.Controls.Add(scanStatus);

            // Main card
            TableLayoutPanel card = new TableLayoutPanel();
            cardPanelRef = card;
            card.Dock = DockStyle.Fill;
            card.Margin = new Padding(0);
            card.Padding = new Padding(1);
            card.BackColor = Line;
            card.ColumnCount = 1;
            card.RowCount = 3;
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            // Give the section header enough room at common Windows DPI scales.
            // The labels inside it use a layout panel below instead of absolute Y positions,
            // so helper text can never slide underneath the grid.
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 68F));
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 3F));
            card.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.Controls.Add(card, 0, 3);

            TableLayoutPanel cardHeader = new TableLayoutPanel();
            cardHeaderPanel = cardHeader;
            cardHeader.Dock = DockStyle.Fill;
            cardHeader.BackColor = Surface;
            cardHeader.ColumnCount = 2;
            cardHeader.RowCount = 1;
            cardHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58F));
            cardHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42F));
            card.Controls.Add(cardHeader, 0, 0);

            TableLayoutPanel titlePanel = new TableLayoutPanel();
            titlePanelRef = titlePanel;
            titlePanel.Dock = DockStyle.Fill;
            titlePanel.Margin = new Padding(0);
            titlePanel.Padding = new Padding(16, 7, 8, 6);
            titlePanel.BackColor = Surface;
            titlePanel.ColumnCount = 1;
            titlePanel.RowCount = 2;
            titlePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            titlePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 55F));
            titlePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 45F));
            cardHeader.Controls.Add(titlePanel, 0, 0);

            viewTitle = new Label();
            viewTitle.Text = "Active giveaways";
            viewTitle.Dock = DockStyle.Fill;
            viewTitle.Margin = new Padding(0);
            viewTitle.Font = new Font("Segoe UI Semibold", 11.5F);
            viewTitle.ForeColor = TextColor;
            viewTitle.TextAlign = ContentAlignment.MiddleLeft;
            viewTitle.AutoEllipsis = true;
            titlePanel.Controls.Add(viewTitle, 0, 0);

            viewSubtitle = new Label();
            viewSubtitle.Text = "Click a column header to sort";
            viewSubtitle.Dock = DockStyle.Fill;
            viewSubtitle.Margin = new Padding(0);
            viewSubtitle.ForeColor = Muted;
            viewSubtitle.Font = new Font("Segoe UI", 8.5F);
            viewSubtitle.TextAlign = ContentAlignment.MiddleLeft;
            viewSubtitle.AutoEllipsis = true;
            titlePanel.Controls.Add(viewSubtitle, 0, 1);

            TableLayoutPanel filterPanel = new TableLayoutPanel();
            filterPanelRef = filterPanel;
            filterPanel.Dock = DockStyle.Fill;
            filterPanel.Margin = new Padding(0);
            filterPanel.Padding = new Padding(8, 15, 16, 14);
            filterPanel.BackColor = Surface;
            filterPanel.ColumnCount = 3;
            filterPanel.RowCount = 1;
            filterPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 54F));
            filterPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 122F));
            filterPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            cardHeader.Controls.Add(filterPanel, 1, 0);

            Panel viewAccent = new Panel();
            viewAccentRef = viewAccent;
            viewAccent.Dock = DockStyle.Fill;
            viewAccent.Margin = new Padding(0);
            viewAccent.BackColor = ActiveView;
            card.Controls.Add(viewAccent, 0, 1);


            Label filterLabel = new Label();
            filterLabel.Text = "SEARCH";
            filterLabel.Dock = DockStyle.Fill;
            filterLabel.Margin = new Padding(0);
            filterLabel.ForeColor = Muted;
            filterLabel.Font = new Font("Segoe UI Semibold", 8F);
            filterLabel.TextAlign = ContentAlignment.MiddleLeft;
            filterPanel.Controls.Add(filterLabel, 0, 0);

            filterFieldBox = new ComboBox();
            filterFieldBox.Dock = DockStyle.Fill;
            filterFieldBox.Margin = new Padding(0, 1, 8, 1);
            filterFieldBox.DropDownStyle = ComboBoxStyle.DropDownList;
            filterFieldBox.FlatStyle = FlatStyle.Flat;
            filterFieldBox.BackColor = Surface3;
            filterFieldBox.ForeColor = TextColor;
            filterFieldBox.Font = new Font("Segoe UI", 8.7F);
            filterFieldBox.Items.AddRange(new object[] { "Creator name", "All fields", "URL", "Ticket", "Promocode", "Minimum deposit", "Deadline" });
            filterFieldBox.SelectedIndex = 0;
            filterFieldBox.SelectedIndexChanged += delegate { if (grid != null) Render(); };
            filterPanel.Controls.Add(filterFieldBox, 1, 0);

            filterBox = new TextBox();
            filterBox.Dock = DockStyle.Fill;
            filterBox.Margin = new Padding(0, 2, 0, 2);
            filterBox.BorderStyle = BorderStyle.FixedSingle;
            filterBox.BackColor = Surface3;
            filterBox.ForeColor = TextColor;
            filterBox.Font = new Font("Segoe UI", 9F);
            filterBox.TextChanged += delegate { if (grid != null) Render(); };
            filterBox.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape && filterBox.TextLength > 0)
                {
                    filterBox.Clear();
                    e.SuppressKeyPress = true;
                }
            };
            filterPanel.Controls.Add(filterBox, 2, 0);

            grid = new DataGridView();
            grid.Dock = DockStyle.Fill;
            grid.Margin = new Padding(0);
            grid.BackgroundColor = Surface2;
            grid.BorderStyle = BorderStyle.None;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.GridColor = Line;
            grid.RowHeadersVisible = false;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.AllowUserToOrderColumns = false;
            grid.MultiSelect = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.ReadOnly = true;
            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersVisible = true;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.ColumnHeadersHeight = 46;
            grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(18, 27, 43);
            grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(183, 195, 216);
            grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI Semibold", 8.5F);
            grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
            grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(6, 0, 0, 0);
            grid.DefaultCellStyle.BackColor = Surface2;
            grid.DefaultCellStyle.ForeColor = TextColor;
            grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(34, 47, 72);
            grid.DefaultCellStyle.SelectionForeColor = TextColor;
            grid.DefaultCellStyle.Font = new Font("Segoe UI", 9.5F);
            grid.DefaultCellStyle.Padding = new Padding(8, 0, 8, 0);
            grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(16, 24, 39);
            grid.RowTemplate.Height = 52;
            grid.AutoGenerateColumns = false;
            grid.ShowCellToolTips = false;

            DataGridViewTextBoxColumn creator = new DataGridViewTextBoxColumn();
            creator.Name = "Creator";
            creator.HeaderText = "CREATOR";
            creator.Width = 155;
            creator.MinimumWidth = 145;
            creator.SortMode = DataGridViewColumnSortMode.Programmatic;

            DataGridViewLinkColumn link = new DataGridViewLinkColumn();
            link.Name = "Link";
            link.HeaderText = "LINK";
            link.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            link.MinimumWidth = 210;
            link.LinkColor = Color.FromArgb(125, 211, 252);
            link.ActiveLinkColor = Color.White;
            link.VisitedLinkColor = Color.FromArgb(125, 211, 252);
            link.TrackVisitedState = false;
            link.SortMode = DataGridViewColumnSortMode.Programmatic;

            // Copy icon is painted inside the LINK cell so it sits immediately
            // beside the URL without wasting a separate divider/column.
            link.DefaultCellStyle.Padding = new Padding(8, 0, 38, 0);

            DataGridViewTextBoxColumn ticket = new DataGridViewTextBoxColumn();
            ticket.Name = "Ticket";
            ticket.HeaderText = "TICKET";
            ticket.Width = 110;
            ticket.SortMode = DataGridViewColumnSortMode.Programmatic;
            ticket.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;

            DataGridViewTextBoxColumn promo = new DataGridViewTextBoxColumn();
            promo.Name = "PromoCode";
            promo.HeaderText = "PROMOCODE";
            promo.Width = 220;
            promo.MinimumWidth = 190;
            promo.SortMode = DataGridViewColumnSortMode.Programmatic;
            // Reserve room for the inline copy icon.
            promo.DefaultCellStyle.Padding = new Padding(8, 0, 38, 0);

            DataGridViewTextBoxColumn minimumDeposit = new DataGridViewTextBoxColumn();
            minimumDeposit.Name = "MinimumDeposit";
            minimumDeposit.HeaderText = "MINIMUM DEPOSIT";
            minimumDeposit.Width = 120;
            minimumDeposit.SortMode = DataGridViewColumnSortMode.Programmatic;

            DataGridViewTextBoxColumn deadline = new DataGridViewTextBoxColumn();
            deadline.Name = "Deadline";
            deadline.HeaderText = "DEADLINE";
            deadline.Width = 135;
            deadline.SortMode = DataGridViewColumnSortMode.Programmatic;

            DataGridViewButtonColumn joinedAction = MakeIconColumn("JoinedAction", 48);

            grid.Columns.AddRange(new DataGridViewColumn[] { creator, link, ticket, promo, minimumDeposit, deadline, joinedAction });
            grid.CellMouseClick += GridCellMouseClick;
            grid.CellPainting += GridCellPainting;
            grid.ColumnHeaderMouseClick += GridColumnHeaderMouseClick;
            card.Controls.Add(grid, 0, 2);

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
            column.DefaultCellStyle.BackColor = Surface3;
            column.DefaultCellStyle.ForeColor = TextColor;
            column.DefaultCellStyle.SelectionBackColor = Surface3;
            column.DefaultCellStyle.SelectionForeColor = TextColor;
            column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            return column;
        }

        private Button MakeHeaderButton(string text, int width, bool emphasis)
        {
            DepthButton b = new DepthButton();
            b.Text = text;
            b.Width = width;
            b.Height = 40;
            b.Margin = new Padding(6, 0, 0, 0);
            b.ForeColor = TextColor;
            b.Font = new Font("Segoe UI Semibold", 9F);
            if (emphasis)
            {
                b.TopColor = Color.FromArgb(126, 112, 255);
                b.BottomColor = Color.FromArgb(86, 72, 215);
                b.BorderColor3D = Color.FromArgb(155, 145, 255);
            }
            else
            {
                b.TopColor = ButtonTop;
                b.BottomColor = ButtonBottom;
                b.BorderColor3D = ButtonBorder;
            }
            return b;
        }

        private void SetDepthButtonTheme(Button button, Color accent, bool selected)
        {
            DepthButton b = button as DepthButton;
            if (b == null) return;
            if (selected)
            {
                b.TopColor = MixColor(Surface3, accent, 0.62);
                b.BottomColor = MixColor(Bg, accent, 0.30);
                b.BorderColor3D = MixColor(accent, Color.White, 0.14);
            }
            else
            {
                b.TopColor = ButtonTop;
                b.BottomColor = ButtonBottom;
                b.BorderColor3D = ButtonBorder;
            }
            b.Invalidate();
        }

        private Color MixColor(Color baseColor, Color accent, double amount)
        {
            if (amount < 0) amount = 0;
            if (amount > 1) amount = 1;
            int r = (int)Math.Round(baseColor.R * (1.0 - amount) + accent.R * amount);
            int g = (int)Math.Round(baseColor.G * (1.0 - amount) + accent.G * amount);
            int b = (int)Math.Round(baseColor.B * (1.0 - amount) + accent.B * amount);
            return Color.FromArgb(r, g, b);
        }

        private Label MakeStatLabel(string text, int width, Color accent)
        {
            Label l = new Label();
            l.Width = width;
            l.Height = 38;
            l.Margin = new Padding(0, 7, 10, 0);
            l.Text = text;
            l.ForeColor = Color.White;
            l.BackColor = MixColor(Surface, accent, 0.14);
            l.BorderStyle = BorderStyle.None;
            l.TextAlign = ContentAlignment.MiddleCenter;
            l.Font = new Font("Segoe UI Semibold", 8.5F);
            return l;
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

        private Rectangle InlineCopyButtonRect(int columnIndex, int rowIndex, Rectangle cellBounds)
        {
            int size = Math.Min(28, Math.Max(24, cellBounds.Height - 18));
            int maxX = cellBounds.Right - size - 7;
            int x = maxX;

            try
            {
                DataGridViewCell cell = grid.Rows[rowIndex].Cells[columnIndex];
                string text = Convert.ToString(cell.FormattedValue) ?? "";
                Font font = cell.InheritedStyle.Font ?? grid.Font;
                int textWidth = TextRenderer.MeasureText(text, font, new Size(int.MaxValue, cellBounds.Height),
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
                // Put the icon immediately after the visible value when there is
                // room; clamp it to the right edge for long URLs/codes.
                x = Math.Min(maxX, cellBounds.Left + 8 + textWidth + 5);
            }
            catch { x = maxX; }

            return new Rectangle(
                x,
                cellBounds.Y + (cellBounds.Height - size) / 2,
                size, size);
        }

        private void DrawCopyIconButton(Graphics graphics, Rectangle r)
        {
            Color top = MixColor(Surface3, Accent2, 0.38);
            Color bottom = MixColor(Bg, Accent2, 0.18);
            using (GraphicsPath path = MakeRoundedPath(r, 5))
            using (LinearGradientBrush fill = new LinearGradientBrush(r, top, bottom, LinearGradientMode.Vertical))
            using (Pen border = new Pen(MixColor(Accent2, Color.White, 0.05)))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.FillPath(fill, path);
                graphics.DrawPath(border, path);
            }

            using (Pen iconPen = new Pen(Color.White, 1.6F))
            {
                iconPen.StartCap = LineCap.Round;
                iconPen.EndCap = LineCap.Round;
                Rectangle back = new Rectangle(r.Left + 7, r.Top + 6, 9, 11);
                Rectangle front = new Rectangle(r.Left + 11, r.Top + 10, 9, 11);
                graphics.DrawRectangle(iconPen, back);
                graphics.DrawRectangle(iconPen, front);
            }
        }

        private void GridCellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            string name = grid.Columns[e.ColumnIndex].Name;

            // LINK and PROMOCODE keep their normal text/link cell and get a small
            // copy button painted inside the same cell.  No extra copy column,
            // therefore no extra divider or wasted width.
            if (name == "Link" || name == "PromoCode")
            {
                e.Paint(e.CellBounds, DataGridViewPaintParts.All);
                if (name == "PromoCode")
                {
                    GiveawayItem item = grid.Rows[e.RowIndex].Tag as GiveawayItem;
                    if (item == null || string.IsNullOrWhiteSpace(item.PromoCode) || item.PromoCode == "-")
                    {
                        e.Handled = true;
                        return;
                    }
                }
                DrawCopyIconButton(e.Graphics, InlineCopyButtonRect(e.ColumnIndex, e.RowIndex, e.CellBounds));
                e.Handled = true;
                return;
            }

            if (name != "JoinedAction") return;

            e.PaintBackground(e.CellBounds, true);
            int size = Math.Min(32, Math.Max(22, e.CellBounds.Height - 16));
            Rectangle r = new Rectangle(
                e.CellBounds.X + (e.CellBounds.Width - size) / 2,
                e.CellBounds.Y + (e.CellBounds.Height - size) / 2,
                size, size);

            Color accent = showingJoined ? Danger : Success;
            Color top = MixColor(Surface3, accent, 0.38);
            Color bottom = MixColor(Bg, accent, 0.18);
            using (GraphicsPath path = MakeRoundedPath(r, 5))
            using (LinearGradientBrush fill = new LinearGradientBrush(r, top, bottom, LinearGradientMode.Vertical))
            using (Pen border = new Pen(MixColor(accent, Color.White, 0.05)))
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(border, path);
            }

            using (Pen iconPen = new Pen(Color.White, 1.7F))
            {
                iconPen.StartCap = LineCap.Round;
                iconPen.EndCap = LineCap.Round;
                if (showingJoined)
                {
                    int pad = 9;
                    e.Graphics.DrawLine(iconPen, r.Left + pad, r.Top + pad, r.Right - pad, r.Bottom - pad);
                    e.Graphics.DrawLine(iconPen, r.Right - pad, r.Top + pad, r.Left + pad, r.Bottom - pad);
                }
                else
                {
                    Rectangle frame = new Rectangle(r.Left + 8, r.Top + 6, 13, 18);
                    e.Graphics.DrawRectangle(iconPen, frame);
                    Point[] door = new Point[]
                    {
                        new Point(r.Left + 12, r.Top + 8),
                        new Point(r.Left + 22, r.Top + 11),
                        new Point(r.Left + 22, r.Top + 22),
                        new Point(r.Left + 12, r.Top + 24)
                    };
                    e.Graphics.DrawPolygon(iconPen, door);
                    using (SolidBrush knob = new SolidBrush(Color.White))
                        e.Graphics.FillEllipse(knob, r.Left + 18, r.Top + 15, 2, 2);
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
                bool copyHit = copyRect.Contains(clickPoint);
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

        private bool TryTicketNumbers(string value, out long remaining, out long total)
        {
            remaining = 0;
            total = 0;
            Match m = Regex.Match(value ?? "", @"^\s*([0-9,\.]+)\s*/\s*([0-9,\.]+)\s*$");
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
            Match m = Regex.Match(text, @"[0-9]+(?:[.,][0-9]+)?");
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
            Color accent = showingJoined ? JoinedView : (showingHistory ? HistoryView : ActiveView);
            // Each tab has its own subtle hue, but the saturation is intentionally low so the
            // UI reads like a production desktop dashboard rather than three brightly painted pages.
            Color viewHeader = MixColor(Surface, accent, showingJoined ? 0.105 : 0.085);
            Color viewTable = MixColor(Surface2, accent, showingJoined ? 0.070 : 0.055);
            Color viewAlt = MixColor(Color.FromArgb(16, 24, 39), accent, showingJoined ? 0.085 : 0.070);

            if (cardPanelRef != null) cardPanelRef.BackColor = MixColor(Line, accent, 0.28);
            if (cardHeaderPanel != null) cardHeaderPanel.BackColor = viewHeader;
            if (titlePanelRef != null) titlePanelRef.BackColor = viewHeader;
            if (filterPanelRef != null) filterPanelRef.BackColor = viewHeader;
            if (activityBarRef != null) activityBarRef.BackColor = MixColor(Color.FromArgb(12, 19, 32), accent, 0.035);
            if (viewAccentRef != null) viewAccentRef.BackColor = accent;

            if (grid != null)
            {
                grid.BackgroundColor = viewTable;
                grid.DefaultCellStyle.BackColor = viewTable;
                grid.AlternatingRowsDefaultCellStyle.BackColor = viewAlt;
                grid.DefaultCellStyle.SelectionBackColor = MixColor(Color.FromArgb(31, 43, 65), accent, 0.22);
                grid.ColumnHeadersDefaultCellStyle.BackColor = MixColor(Color.FromArgb(18, 27, 43), accent, 0.075);
            }

            SetDepthButtonTheme(historyButton, HistoryView, showingHistory);
            SetDepthButtonTheme(joinedButton, JoinedView, showingJoined);
            viewTitle.ForeColor = accent;
        }

        private void Render()
        {
            int ac = data.Items.Count(delegate(GiveawayItem i) { return string.Equals(i.Status, "active", StringComparison.OrdinalIgnoreCase) && !i.Joined; });
            int hc = data.Items.Count(delegate(GiveawayItem i) { return string.Equals(i.Status, "ended", StringComparison.OrdinalIgnoreCase) && !i.Joined; });
            int jc = data.Items.Count(delegate(GiveawayItem i) { return i.Joined; });
            activeCount.Text = "ACTIVE  " + ac;
            historyCount.Text = "HISTORY  " + hc;
            joinedCount.Text = "JOINED  " + jc;
            lastCheck.Text = "LAST CHECK  " + FormatTime(data.LastScan);

            historyButton.Text = showingHistory ? "← Active" : "History";
            joinedButton.Text = showingJoined ? "← Active" : "Joined";

            if (showingJoined)
            {
                viewTitle.Text = "Joined giveaways";
                viewSubtitle.Text = "Only joined giveaways appear here • use the × button to return an item to Active or History";
            }
            else if (showingHistory)
            {
                viewTitle.Text = "Giveaway history";
                viewSubtitle.Text = "Ended giveaways are retained here with their original links";
            }
            else
            {
                viewTitle.Text = "Active giveaways";
                viewSubtitle.Text = "Verified running giveaways you have not joined yet • click a column header to sort";
            }

            ApplyViewTheme();

            grid.SuspendLayout();
            grid.Rows.Clear();
            foreach (GiveawayItem i in CurrentItems())
            {
                bool ended = string.Equals(i.Status, "ended", StringComparison.OrdinalIgnoreCase);
                string deadline = i.Deadline ?? "-";
                if ((showingHistory || showingJoined) && ended && (string.IsNullOrWhiteSpace(deadline) || deadline == "-")) deadline = "ENDED";
                if (!string.IsNullOrWhiteSpace(deadline) && deadline != "-") deadline = deadline.ToUpperInvariant();
                string promoCode = string.IsNullOrWhiteSpace(i.PromoCode) ? "-" : i.PromoCode;
                string minimumDeposit = string.IsNullOrWhiteSpace(i.MinimumDeposit) ? "-" : i.MinimumDeposit;
                int row = grid.Rows.Add(i.Creator ?? "Unknown", i.Url ?? "", i.Ticket ?? "-", promoCode, minimumDeposit, deadline, "");
                grid.Rows[row].Tag = i;
                grid.Rows[row].Cells["Creator"].Style.Font = new Font("Segoe UI Semibold", 9.4F);
                grid.Rows[row].Cells["Ticket"].Style.Font = new Font("Segoe UI Semibold", 9.2F);

                bool activeLike = string.Equals(i.Status, "active", StringComparison.OrdinalIgnoreCase);
                grid.Rows[row].Cells["Ticket"].Style.ForeColor = Muted;
                grid.Rows[row].Cells["PromoCode"].Style.ForeColor = promoCode == "-" ? Muted : TextColor;
                grid.Rows[row].Cells["MinimumDeposit"].Style.ForeColor = minimumDeposit == "-" ? Muted : TextColor;
                grid.Rows[row].Cells["Deadline"].Style.ForeColor = Muted;
                if (ended) grid.Rows[row].DefaultCellStyle.ForeColor = Color.FromArgb(183, 193, 208);

                long remaining, total;
                if (activeLike && TryTicketNumbers(i.Ticket, out remaining, out total) && total > 0)
                {
                    if (remaining < 100)
                        grid.Rows[row].Cells["Ticket"].Style.ForeColor = PriorityRed;
                    else if (remaining < 250)
                        grid.Rows[row].Cells["Ticket"].Style.ForeColor = PriorityYellow;
                    else
                        grid.Rows[row].Cells["Ticket"].Style.ForeColor = PriorityGreen;
                }

                DateTime due;
                if (activeLike && TryDeadlineDate(i, out due))
                {
                    double days = (due.Date - DateTime.Now.Date).TotalDays;
                    if (days <= 2)
                        grid.Rows[row].Cells["Deadline"].Style.ForeColor = PriorityRed;
                    else if (days <= 7)
                        grid.Rows[row].Cells["Deadline"].Style.ForeColor = PriorityYellow;
                    else
                        grid.Rows[row].Cells["Deadline"].Style.ForeColor = PriorityGreen;
                }


                if (showingJoined)
                    grid.Rows[row].Cells["JoinedAction"].Style.BackColor = MixColor(Surface3, Danger, 0.08);
                else
                    grid.Rows[row].Cells["JoinedAction"].Style.BackColor = MixColor(Surface3, Success, 0.08);
            }
            UpdateSortGlyph();
            grid.ResumeLayout();
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
            if (string.IsNullOrWhiteSpace(iso)) return "NEVER";
            DateTime dt;
            if (DateTime.TryParse(iso, out dt))
                return dt.ToLocalTime().ToString("MMM d, h:mm tt", System.Globalization.CultureInfo.InvariantCulture).ToUpperInvariant();
            return "NEVER";
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
            scanStatus.ForeColor = value ? Warning : Muted;

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
                data = refreshed;
                Render();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Refresh failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
                data = scanned;
                Render();
                int ac = data.Items.Count(delegate(GiveawayItem i) { return string.Equals(i.Status, "active", StringComparison.OrdinalIgnoreCase) && !i.Joined; });
                scanStatus.Text = "Deep Search complete  •  " + ac + " active giveaway" + (ac == 1 ? "" : "s");
                scanStatus.ForeColor = Success;
                await Task.Delay(2200);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Deep Search failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { SetScanning(false, ""); }
        }

        private async Task AddLinkAsync()
        {
            using (AddLinkForm f = new AddLinkForm())
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
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
                    MessageBox.Show(this, ex.Message, "Invalid link", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                finally { SetScanning(false, ""); }
            }
        }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SkinClubGiveawayDesktop
{
    // SkinClub community quizzes. The list page (and its /page/2, /page/3, ...) is plain HTML: one
    // <article> card per quiz with its link, title, participants, live/closed state and exact end time.
    // A live quiz's own page lists its prizes.
    public static partial class Scanner
    {
        public const string QuizzesUrl = "https://community.skin.club/en/quizzes";
        private const int MaxQuizPages = 6;

        private static readonly Regex QuizCardRegex = new Regex(@"<article\b.*?</article>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex QuizLinkRegex = new Regex(@"href\s*=\s*[""'](https?://community\.skin\.club/[a-z-]+/quizzes/(?!page/)[^""'/?#]+)/?[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // Every link in a card. The title is the card's last text link to the quiz that is not a tag
        // ("live", "Quizzes"); the site wraps it in <h2> on some cards and in a <div> on others.
        private static readonly Regex QuizAnchorRegex = new Regex(@"<a\b([^>]*)>(.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static string QuizTitle(string card, string url)
        {
            string title = "";
            foreach (Match a in QuizAnchorRegex.Matches(card))
            {
                string attributes = a.Groups[1].Value;
                if (attributes.IndexOf(url, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (attributes.IndexOf("club-tag", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                string text = WebUtility.HtmlDecode(Regex.Replace(a.Groups[2].Value, @"<[^>]+>", " "));
                text = Regex.Replace(text, @"\s+", " ").Trim();
                if (text.Length > 0) title = text;
            }
            return title;
        }
        private static readonly Regex QuizPeopleRegex = new Regex(@"club-giveaway-ticket-info__col[^>]*>\s*<div[^>]*>[^<]*</div>\s*<div[^>]*>\s*([0-9][0-9.,\s]*)<", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex QuizEndRegex = new Regex(@"data-countdown-ticket\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex QuizClosedRegex = new Regex(@"club-giveaway-ticket-info--closed", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex QuizFirstPrizeRegex = new Regex(@"1st\s+place\s+(.{3,90}?)\s+(?:\d+(?:st|nd|rd|th)\b|for\s+everyone|take\s+a?\s*part|step\s+1)", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        // Quiz cards on one list page, in page order.
        internal static List<GiveawayItem> ParseQuizList(string html, DateTime nowUtc)
        {
            List<GiveawayItem> quizzes = new List<GiveawayItem>();
            if (string.IsNullOrEmpty(html)) return quizzes;
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match card in QuizCardRegex.Matches(html))
            {
                string block = card.Value;
                if (block.IndexOf("club-giveaway-ticket-info", StringComparison.OrdinalIgnoreCase) < 0) continue;
                Match link = QuizLinkRegex.Match(block);
                if (!link.Success) continue;
                string url = link.Groups[1].Value;
                if (!seen.Add(url)) continue;

                string name = QuizTitle(block, url);
                if (name.Length == 0)
                {
                    // No readable title: fall back to the link's last part ("road-to-singapore-major").
                    string slug = url.Substring(url.LastIndexOf('/') + 1).Replace('-', ' ');
                    name = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(slug);
                }
                Match people = QuizPeopleRegex.Match(block);
                string count = people.Success ? Regex.Replace(people.Groups[1].Value, @"[^0-9]", "") : "";

                bool live = !QuizClosedRegex.IsMatch(block);
                string deadline = "Ended";
                Match end = QuizEndRegex.Match(block);
                DateTime ends;
                if (live && end.Success && DateTime.TryParse(end.Groups[1].Value, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out ends))
                {
                    if (ends <= nowUtc) live = false;
                    else deadline = ends.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.InvariantCulture).ToUpperInvariant();
                }
                else if (live) deadline = "-"; // live, but the page gives no end time

                quizzes.Add(new GiveawayItem
                {
                    Creator = name.Length == 0 ? "Quiz" : name, Url = url, Status = live ? "active" : "ended",
                    Ticket = count.Length == 0 ? "-" : count, PromoCode = "-", MinimumDeposit = "-", PrizePool = "-",
                    Deadline = deadline, Source = "quiz", LastChecked = nowUtc.ToString("o")
                });
            }
            return quizzes;
        }

        // "1st place Huntsman Knife Tiger Tooth Factory New 2nd place ..." on a quiz's own page.
        internal static string QuizFirstPrize(string html)
        {
            if (string.IsNullOrEmpty(html)) return "-";
            Match m = QuizFirstPrizeRegex.Match(HtmlToText(html).Replace('\n', ' '));
            if (!m.Success) return "-";
            string prize = Regex.Replace(m.Groups[1].Value, @"\s+", " ").Trim().TrimStart('★', ' ');
            return prize.Length == 0 ? "-" : prize;
        }

        // Reads every list page and, for live quizzes, the 1st-place prize. On any failure the saved
        // list is kept as it is, so a network hiccup never empties the Quizzes tab.
        internal static async Task RefreshQuizzesAsync(AppData data)
        {
            try
            {
                List<GiveawayItem> found = new List<GiveawayItem>();
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int page = 1; page <= MaxQuizPages; page++)
                {
                    string url = page == 1 ? QuizzesUrl : QuizzesUrl + "/page/" + page;
                    PageFetch fetched = await FetchPageAsync(url, MaxDiscoveryBytes, false);
                    if (fetched.Status < 200 || fetched.Status > 299 || fetched.Body == null)
                    {
                        if (page == 1) { ActivityLog.Write("ERROR", "Quizzes page answered HTTP " + fetched.Status + "; keeping the saved quizzes"); return; }
                        break;
                    }
                    foreach (GiveawayItem quiz in ParseQuizList(fetched.Body, DateTime.UtcNow))
                        if (seen.Add(quiz.Url)) found.Add(quiz);
                    if (fetched.Body.IndexOf("/quizzes/page/" + (page + 1), StringComparison.OrdinalIgnoreCase) < 0) break;
                }
                if (found.Count == 0) { ActivityLog.Write("WARN", "No quizzes found on the quizzes page; keeping the saved quizzes"); return; }

                // Keep prizes already known; read the prize page only for live quizzes that lack one.
                Dictionary<string, GiveawayItem> old = new Dictionary<string, GiveawayItem>(StringComparer.OrdinalIgnoreCase);
                if (data.Quizzes != null) foreach (GiveawayItem q in data.Quizzes) if (q != null && q.Url != null) old[q.Url] = q;
                List<Task> prizeReads = new List<Task>();
                SemaphoreSlim gate = new SemaphoreSlim(3, 3);
                foreach (GiveawayItem quiz in found)
                {
                    GiveawayItem known;
                    if (old.TryGetValue(quiz.Url, out known) && HasValue(known.PrizePool)) { quiz.PrizePool = known.PrizePool; continue; }
                    if (quiz.Status != "active") continue;
                    GiveawayItem target = quiz;
                    prizeReads.Add(Task.Run(async delegate
                    {
                        await gate.WaitAsync();
                        try
                        {
                            PageFetch detail = await FetchPageAsync(target.Url, MaxDiscoveryBytes, false);
                            if (detail.Body != null) target.PrizePool = QuizFirstPrize(detail.Body);
                        }
                        catch (Exception ex) { Diag.Swallow("Reading the prizes of " + target.Url, ex); }
                        finally { gate.Release(); }
                    }));
                }
                await Task.WhenAll(prizeReads);
                data.Quizzes = found;
                ActivityLog.Write("SCAN", "Quizzes: " + found.Count + " listed, " + found.Count(delegate(GiveawayItem q) { return q.Status == "active"; }) + " live");
            }
            catch (Exception ex)
            {
                ActivityLog.Write("ERROR", "Quizzes could not be read (saved list kept): " + ex.Message);
            }
        }
    }
}

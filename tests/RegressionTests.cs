using System;
using System.Collections.Generic;
using System.IO;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using SkinClubGiveawayDesktop;

static class RegressionTests
{
    sealed class RedirectHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://alpha.club/"),
                Content = new StringContent("homepage") });
        }
    }
    sealed class FixedPageHandler : HttpMessageHandler
    {
        readonly string body;
        public FixedPageHandler(string body) { this.body = body; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(body) });
        }
    }
    // Fails the first N requests (by throwing, or with a status code), then serves the page.
    sealed class FlakyHandler : HttpMessageHandler
    {
        readonly string body;
        readonly HttpStatusCode? failureStatus;
        int failuresLeft;
        public int Calls;
        public FlakyHandler(int failures, HttpStatusCode? failureStatus, string body) { failuresLeft = failures; this.failureStatus = failureStatus; this.body = body; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            if (failuresLeft > 0)
            {
                failuresLeft--;
                if (failureStatus.HasValue) return Task.FromResult(new HttpResponseMessage(failureStatus.Value) { RequestMessage = request });
                throw new HttpRequestException("simulated connection reset");
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(body) });
        }
    }
    static int checks;
    static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
        checks++;
        Console.WriteLine("PASS " + message);
    }
    static object Field(object target, string name)
    { return target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target); }
    static object Call(object target, string name, params object[] args)
    { return target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(target, args); }
    // No two controls may overlap, none may leave the window, and wrapped text must fit its label.
    static void CheckDialogLayout(Form form, string name)
    {
        using (form)
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-3000, -3000);
            form.Show();
            Application.DoEvents();
            System.Collections.Generic.List<Control> controls = form.Controls.Cast<Control>().Where(c => c.Visible).ToList();
            string problem = null;
            for (int i = 0; i < controls.Count && problem == null; i++)
            {
                Control a = controls[i];
                if (!form.ClientRectangle.Contains(a.Bounds)) problem = Describe(a) + " leaves the window";
                for (int j = i + 1; j < controls.Count && problem == null; j++)
                    if (a.Bounds.IntersectsWith(controls[j].Bounds)) problem = Describe(a) + " overlaps " + Describe(controls[j]);
                Label label = a as Label;
                if (problem == null && label != null && !label.AutoSize &&
                    TextRenderer.MeasureText(label.Text, label.Font, new Size(label.Width, 0), TextFormatFlags.WordBreak).Height > label.Height)
                    problem = Describe(a) + " is cut off";
            }
            Check(problem == null, name + " has no overlapping, clipped or off-screen controls" + (problem == null ? "" : ": " + problem));
            form.Close();
        }
    }
    static string Describe(Control c) { return c.GetType().Name + " '" + (c.Text.Length > 30 ? c.Text.Substring(0, 30) : c.Text) + "'"; }

    [STAThread]
    static void Main()
    {
        string folder = Path.Combine(Path.GetTempPath(), "SkinClub-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        // Framework test process only: never read or write the user's saved data.
        typeof(DataStore).GetField("DataDir").SetValue(null, folder);
        typeof(DataStore).GetField("DataFile").SetValue(null, Path.Combine(folder, "data.json"));
        AppSettings.Current = new AppSettings();
        AppData data = new AppData();
        data.Items.Add(new GiveawayItem { Creator = "Alpha", Url = "https://alpha.club/010926/", Status = "active", Ticket = "12 / 100", MinimumDeposit = "$10" });
        data.Items.Add(new GiveawayItem { Creator = "Beta", Url = "https://beta.club/010926/", Status = "active", Ticket = "2 / 100", MinimumDeposit = "$2" });
        data.Items.Add(new GiveawayItem { Creator = "Old", Url = "https://old.club/010926/", Status = "ended", Joined = true, HistorySince = DateTime.UtcNow.AddYears(-1).ToString("o") });
        DataStore.Save(data);
        DataStore.Save(data);
        Check(File.Exists(DataStore.DataFile + ".bak"), "Atomic save keeps a backup");
        Check(DataStore.Load().Items.Count == 3, "Saved items and old joined items survive reload");
        Check(DataStore.Load().Items.First(i => i.Creator == "Alpha").MinimumDeposit == "10$", "Saved deposits like $10 are shown as 10$ after loading");
        File.WriteAllText(DataStore.DataFile, "{\"Items\":[null,{\"Url\":null},{\"Url\":\"https://alpha.club/010926/\",\"Status\":\"active\"}]}");
        Check(DataStore.Load().Items.Count == 1, "Invalid saved entries are removed safely");
        File.WriteAllText(DataStore.DataFile, "broken json");
        DataStore.Load();
        Check(Directory.GetFiles(folder, "*.corrupt-*").Length == 1, "Unreadable data is preserved for recovery");
        AppData history = new AppData();
        DateTime historyStart = DateTime.UtcNow.AddYears(-1);
        for (int n = 0; n < 35; n++)
            history.Items.Add(new GiveawayItem { Url = "https://history.club/" + n, Status = "ended", HistorySince = historyStart.AddDays(n).ToString("o") });
        history.Items.Add(new GiveawayItem { Url = "https://legacy.club/", Status = "ended", LastChecked = historyStart.AddDays(36).ToString("o") });
        history.Items.Add(data.Items[0]);
        history.Items.Add(data.Items[2]);
        DataStore.Save(history);
        Check(history.Items.Count == 32, "History cap preserves active and joined entries");
        Check(!history.Items.Any(i => i.Url == "https://history.club/5") && history.Items.Any(i => i.Url == "https://history.club/6"), "Only the 30 most recent history entries survive regardless of age");
        Check(history.Items.Any(i => i.Url == "https://legacy.club/"), "Legacy history falls back to last checked time");
        Check(DataStore.Load().Items.Count == 32, "History cap persists after reload");
        history.Items.Add(new GiveawayItem { Url = "https://newest.club/", Status = "ended", HistorySince = DateTime.UtcNow.ToString("o") });
        File.WriteAllText(DataStore.DataFile, new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(history));
        Check(DataStore.Load().Items.Count == 32 && !File.ReadAllText(DataStore.DataFile).Contains("https://history.club/6\""), "Loading oversized history removes and persists excess entries");
        AppSettings tuned = new AppSettings { RefreshMinutes = 0, HistoryLimit = 100000, HttpTimeoutSeconds = -5 };
        AppSettings.Current = tuned;
        Check(tuned.RefreshMinutes == 1 && tuned.HistoryLimit == 500 && tuned.HttpTimeoutSeconds == 3, "Settings values are clamped to safe ranges");
        tuned.HistoryLimit = 44;
        tuned.Save();
        Check(File.ReadAllText(AppSettings.SettingsFile).Contains("\"RefreshMinutes\": 1"), "Settings are saved as readable JSON");
        typeof(AppSettings).GetField("current", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, null);
        Check(AppSettings.Current.HistoryLimit == 44, "Saved settings load back");
        // A settings.json written by an older version still has BrowserPath and ExtraCreators.
        File.WriteAllText(AppSettings.SettingsFile, "{\"RefreshMinutes\": 7, \"BrowserPath\": \"C:\\\\x\\\\chrome.exe\", \"ExtraCreators\": {\"Someone\": \"someone.club\"}}");
        typeof(AppSettings).GetField("current", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, null);
        Check(AppSettings.Current.RefreshMinutes == 7, "An older settings.json with removed options still loads");
        File.WriteAllText(AppSettings.SettingsFile, "{ not json");
        typeof(AppSettings).GetField("current", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, null);
        Check(AppSettings.Current.HistoryLimit == 30, "Broken settings.json falls back to defaults");
        AppSettings.Current = new AppSettings();

        HttpResponseMessage big = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 3 * 1024 * 1024)) };
        string capped = ((Task<string>)typeof(Scanner).GetMethod("ReadBodyCappedAsync", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { big, 1024 * 1024 })).GetAwaiter().GetResult();
        Check(capped.Length == 1024 * 1024, "Downloaded page bodies are capped in memory");

        Check(GiveawayInfo.IsEntryCodeFor("LP-KRYOZ-010926", "https://kryoz.club/010926/") &&
              !GiveawayInfo.IsEntryCodeFor("LP-XTVFAGX7QP9W", "https://kryoz.club/010926/") &&
              !GiveawayInfo.IsEntryCodeFor("-", "https://kryoz.club/010926/"), "A dated code is an entry code; an undated one is not");
        // Real pages whose code date differs from the link date (November 2026).
        Check(GiveawayInfo.IsEntryCodeFor("LP-JOISPOI24-011126", "https://joispoi.club/021126/") &&
              GiveawayInfo.IsEntryCodeFor("LP-ASYLUM-011226", "https://theasylum.club/011126/"), "An entry code dated differently from its link is still accepted");
        ParseResult otherDate = Scanner.ParsePage("<html><body><p>PASO 2 Recarga tu saldo desde $5 con el código promocional LP-ASYLUM-011226 <a href=\"https://skin.club/en?utm_promo=LP-ASYLUM-011226\">USAR</a> <a href=\"https://skin.club/en?utm_promo=LP-79RGPHDSZ44S\">x</a></p><div>Tickets restantes: 750 / 750</div></body></html>");
        Check(GiveawayInfo.EntryCodeOrDash(otherDate.PromoCode, "https://theasylum.club/011126/") == "LP-ASYLUM-011226" && otherDate.MinimumDeposit == "5$",
            "Code and deposit are read from a page whose code date differs from its link");

        AppData codes = new AppData();
        codes.Items.Add(new GiveawayItem { Creator = "Kryoz", Url = "https://kryoz.club/010926/", Status = "ended", PromoCode = "LP-KRYOZ-010926", MinimumDeposit = "$10" });
        codes.Items.Add(new GiveawayItem { Creator = "Kryoz", Url = "https://kryoz.club/010826/", Status = "ended", PromoCode = "LP-XTVFAGX7QP9W", MinimumDeposit = "$5" });
        codes.Items.Add(new GiveawayItem { Creator = "Yumi", Url = "https://tooyumi.club/010826/", Status = "ended", PromoCode = "LP-ABCDEF123456", MinimumDeposit = "-" });
        codes.Items.Add(new GiveawayItem { Creator = "Kryoz", Url = "https://kryoz.club/020926/", Status = "ended", PromoCode = "LP-KRYOZ-010826", MinimumDeposit = "$3" });
        Check(GiveawayInfo.RepairEntryCodes(codes), "Bad entry codes are detected on load");
        Check(codes.Items[1].PromoCode == "LP-KRYOZ-010826" && codes.Items[1].MinimumDeposit == "$5", "A discount code left by an ended page is rebuilt from the creator's code pattern, keeping the deposit");
        Check(codes.Items[2].PromoCode == "-", "A bad code with no known pattern is cleared instead of guessed");
        Check(codes.Items[3].PromoCode == "LP-KRYOZ-010826" && codes.Items[3].MinimumDeposit == "$3", "A dated code that differs from the link date is kept, with its deposit");
        Check(codes.Items[0].PromoCode == "LP-KRYOZ-010926" && codes.Items[0].MinimumDeposit == "$10", "A correct saved code and deposit are left alone");

        AppData latest = new AppData();
        latest.Items.Add(new GiveawayItem { Creator = "Jon", Url = "https://jonsandman.club/010926/", Status = "ended", HistorySince = DateTime.UtcNow.ToString("o") });
        latest.Items.Add(new GiveawayItem { Creator = "Jon", Url = "https://jonsandman.club/020926/", Status = "ended", HistorySince = DateTime.UtcNow.AddDays(-5).ToString("o") });
        latest.Items.Add(new GiveawayItem { Creator = "Jon", Url = "https://jonsandman.club/010826/", Status = "ended", HistorySince = DateTime.UtcNow.AddDays(-9).ToString("o") });
        latest.Items.Add(new GiveawayItem { Creator = "Blarg", Url = "https://blarg.club/010826/", Status = "ended" });
        System.Collections.Generic.List<GiveawayItem> newest = GiveawayInfo.LatestPerCreator(latest.Items);
        Check(newest.Count == 2 && newest.Any(i => i.Url == "https://jonsandman.club/020926/"), "Latest per creator picks the newest dated link, not the newest saved");

        AppData pruned = new AppData();
        pruned.Items.Add(new GiveawayItem { Creator = "Old", Url = "https://oldcreator.club/010926/", Status = "ended", HistorySince = DateTime.UtcNow.AddYears(-1).ToString("o") });
        for (int n = 0; n < 40; n++)
            pruned.Items.Add(new GiveawayItem { Creator = "Busy", Url = "https://busy.club/01" + (n % 12 + 1).ToString("00") + "26/?n=" + n, Status = "ended", HistorySince = DateTime.UtcNow.AddMinutes(-n).ToString("o") });
        DataStore.Save(pruned);
        Check(pruned.Items.Any(i => i.Url == "https://oldcreator.club/010926/"), "History cap never drops a creator's latest giveaway");

        typeof(Scanner).GetField("Client", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null,
            new HttpClient(new FixedPageHandler("<html><body>Tickets left: 0 / 500 <a href=\"https://skin.club/?promocode=LP-XTVFAGX7QP9W\">go</a></body></html>")));
        GiveawayItem endedPage = Scanner.ValidateAsync(new Candidate("Kryoz", "https://kryoz.club/010826/", "saved"), new SemaphoreSlim(1)).GetAwaiter().GetResult();
        Check(endedPage.Status == "ended" && endedPage.PromoCode == "-", "An ended page's unrelated code is not taken as the entry code");

        // "Time remaining: END" in other languages must still mean the giveaway is over, even with tickets left.
        foreach (string word in new string[] { "FIM", "FIN", "ENDED", "Encerrado", "Terminé", "Завершено" })
        {
            ParseResult over = Scanner.ParsePage("<html><body><div>Bilhetes restantes: 436 / 625</div><div>Tempo restante: " + word + "</div></body></html>");
            Check(over.Status == "ended" && over.Deadline == "Ended", "A page showing '" + word + "' as the time remaining is classified as ended");
        }
        ParseResult live = Scanner.ParsePage("<html><body><div>Bilhetes restantes: 436 / 625</div><div>Tempo restante: 12h 30m 5s</div></body></html>");
        Check(live.Status == "active", "A running countdown still means active");
        foreach (string word in new string[] { "BİTİŞ", "Bitiş", "النهاية" })
        {
            ParseResult over = Scanner.ParsePage("<html><body><div>Bilhetes restantes: 436 / 625</div><div>Tempo restante: " + word + "</div></body></html>");
            Check(over.Status == "ended", "The Turkish/Arabic ended word '" + word + "' means the giveaway is over");
        }

        // Current creator pages, in any language, carry their end date in the HTML (no browser needed).
        System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
        string soon = DateTime.Now.AddDays(5).ToString("dd.MM.yyyy", inv);
        string soonShown = DateTime.Now.AddDays(5).ToString("MMM d, yyyy", inv).ToUpperInvariant();
        string past = DateTime.Now.AddDays(-3).ToString("dd.MM.yyyy", inv);
        Func<string, string, string, string> page = delegate(string label, string body, string date)
        {
            return "<html><body><div>" + body + "</div><p>" + label + " <b class=\"giveaway-timecomplete\" data-giveaway-complete-date=\"" + date +
                "\" data-giveaway-complete-time=\"23:59\"></b></p><script>var t = { countdownEnd: \"END\" };</script></body></html>";
        };
        ParseResult arabic = Scanner.ParsePage(page("الوقت المتبقي حتى النهاية:", "التذاكر المتبقية: 974 / 1000", soon));
        Check(arabic.Status == "active" && arabic.Ticket == "974 / 1000" && arabic.Deadline == soonShown, "An Arabic page is read: tickets, deadline and active status");
        ParseResult turkish = Scanner.ParsePage(page("Bitişe kalan süre:", "ADIM 2 Bakiyenizi en az 5$ yükleyin ve LP-WTCN-011026 promosyon kodunu kullanın <a href=\"https://skin.club/en?utm_promo=LP-WTCN-011026\">KODU KULLAN</a>", soon));
        Check(turkish.Status == "active" && turkish.Deadline == soonShown, "A Turkish leaderboard page without tickets is active until its end date");
        Check(turkish.PromoCode == "LP-WTCN-011026" && turkish.MinimumDeposit == "5$", "A Turkish page's promocode and minimum deposit are read");
        ParseResult spanishOver = Scanner.ParsePage(page("Tiempo restante:", "Tickets restantes: 461 / 625", past));
        Check(spanishOver.Status == "ended" && spanishOver.Deadline == "Ended", "A page whose end date has passed is ended, even with tickets left");
        ParseResult otherLanguage = Scanner.ParsePage(page("Czas do końca:", "Pozostałe losy: 12 / 50", soon));
        Check(otherLanguage.Ticket == "12 / 50" && otherLanguage.Status == "active", "Tickets are read from a page in a language the app has no words for");
        string todayDeadline; bool todayEnded;
        Check(Scanner.TryCompleteDate("data-giveaway-complete-date=\"" + DateTime.Now.ToString("dd.MM.yyyy", inv) + "\"", DateTime.Now.Date, out todayDeadline, out todayEnded) && !todayEnded,
            "A giveaway ending today is still active today");

        // Prize pools are read from the page in any language and shown as USD with thousands grouped.
        Func<string, string, string> poolPage = delegate(string label, string amount)
        {
            return "<html><body><div class=\"prize\"><p class=\"red-label red-label_small\">" + label + "</p>\n  <p class=\"red-label\">" + amount + "</p></div><p>1st place: AK-47</p></body></html>";
        };
        Check(Scanner.PrizePoolFromHtml(poolPage("total prize pool", "$500")) == "500$" &&
              Scanner.PrizePoolFromHtml(poolPage("pool total de premios", "$1,000")) == "1,000$" &&
              Scanner.PrizePoolFromHtml(poolPage("toplam ödül havuzu", "$1,000")) == "1,000$" &&
              Scanner.PrizePoolFromHtml(poolPage("إجمالي قيمة الجوائز", "$2,000")) == "2,000$" &&
              Scanner.ParsePage(poolPage("total prize pool", "$3,000")).PrizePool == "3,000$",
            "Prize pool is read in English, Spanish, Turkish and Arabic");
        Check(Scanner.PrizePoolFromHtml("<html><body>Total prize pool: $750</body></html>") == "750$" &&
              Scanner.PrizePoolFromHtml("<html><body>Tickets left: 5 / 10</body></html>") == "-", "Prize pool falls back to the page text, and is blank when absent");
        decimal thousand, cents;
        Check(Scanner.TryUsdValue("1,000$", out thousand) && thousand == 1000m && Scanner.TryUsdValue("7.50$", out cents) && cents == 7.5m &&
              !Scanner.TryUsdValue("-", out cents), "Amounts like 1,000$ are read back as numbers for sorting");

        // Quizzes are read from the community quizzes page: one card per quiz.
        DateTime quizNow = new DateTime(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc);
        Func<string, string, string, string, string> quizCard = delegate(string slug, string quizTitle, string peopleCount, string timeCell)
        {
            return "<div class=\"club-col-4\"><article class=\"club-gap-y-2xl\"><header><figure><a class=\"club-hover-connect\" href=\"https://community.skin.club/en/quizzes/" + slug + "\"><picture></picture>" +
                "<div class=\"club-giveaway-ticket-info club-giveaway-ticket-info--" + (timeCell.StartsWith("data") ? "open" : "closed") + " club-eq-height-by-row\"><div class=\"club-giveaway-ticket-info__col\"><div class=\"club-text-14-medium\">People</div> <div class=\"club-text-14-extra\">" + peopleCount + "</div></div>" +
                "<div class=\"club-giveaway-ticket-info__col\"><div class=\"club-text-14-medium\">time</div> <div class=\"club-text-14-extra club-countdown-ticket\"" + (timeCell.StartsWith("data") ? " " + timeCell + "></div>" : ">end</div>") + "</div></div></a></figure></header>" +
                "<ul class=\"club-tag-list\"><li><a class=\"club-tag club-tag--status-live\" href=\"https://community.skin.club/en/quizzes/" + slug + "\">live</a></li><li><a class=\"club-tag\" href=\"https://community.skin.club/en/quizzes\">Quizzes</a></li><li><div class=\"club-tag club-tag--date\">Nov 05</div></li></ul>" +
                // The site wraps the title in <h2> on some cards and in a <div> on others.
                (slug.StartsWith("road") ? "<div class=\"club-h5-title-mod-xs\"> <a class=\"club-link-primary\" href=\"https://community.skin.club/en/quizzes/" + slug + "\">" + quizTitle + "</a> </div>"
                    : "<h2 class=\"club-h5\"> <a class=\"club-hover-connect\" href=\"https://community.skin.club/en/quizzes/" + slug + "\">" + quizTitle + "</a> </h2>") + "</article></div>";
        };
        string quizPage = "<html><body><a href=\"https://community.skin.club/en/quizzes/road-to-singapore-major\">Quiz</a><main>" +
            quizCard("road-to-singapore-major", "Road to Singapore Major Quiz", "126", "data-countdown-ticket=\"2026-12-13T23:59:00Z\"") +
            quizCard("gamerlegion-prove-youre-a-fan", "GamerLegion: Prove You&#8217;re a Fan", "1,492", "data-countdown-ticket=\"2026-11-11T23:59:00Z\"") +
            quizCard("are-you-a-true-shox-fan", "Are You a True SHOX Fan?", "810", "end") +
            "<a href=\"https://community.skin.club/en/quizzes/page/2\">2</a></main></body></html>";
        List<GiveawayItem> parsedQuizzes = Scanner.ParseQuizList(quizPage, quizNow);
        Check(parsedQuizzes.Count == 3 && parsedQuizzes[0].Creator == "Road to Singapore Major Quiz" &&
              parsedQuizzes[0].Url == "https://community.skin.club/en/quizzes/road-to-singapore-major" && parsedQuizzes[0].Ticket == "126" &&
              parsedQuizzes[0].Status == "active" && parsedQuizzes[0].Deadline.StartsWith("DEC 1") && parsedQuizzes[0].Deadline.EndsWith("2026"),
            "A live quiz is read with its link, title, participants and end date");
        Check(parsedQuizzes[1].Status == "ended" && parsedQuizzes[1].Deadline == "Ended" && parsedQuizzes[1].Ticket == "1492" && parsedQuizzes[1].Creator.Contains("You’re"),
            "A quiz whose end time has passed counts as closed");
        Check(parsedQuizzes[2].Status == "ended" && parsedQuizzes[2].Deadline == "Ended" && parsedQuizzes[2].Ticket == "810", "A closed quiz is read as ended");
        Check(Scanner.QuizFirstPrize("<html><body><h2>quiz prizes</h2><div>1st place</div><div>Huntsman Knife Tiger Tooth Factory New</div><div>2nd place</div><div>Driver Gloves</div></body></html>") == "Huntsman Knife Tiger Tooth Factory New" &&
              Scanner.QuizFirstPrize("<html><body>quiz prizes <p>1st place</p><p>AK-47 Redline Field-Tested</p><p>2nd - 4th places</p><p>Glock-18</p></body></html>") == "AK-47 Redline Field-Tested" &&
              Scanner.QuizFirstPrize("<html><body>no prizes here</body></html>") == "-", "A quiz's 1st-place prize is read from its page");

        // Deposits are shown as USD in one form: "2$".
        Check(Scanner.UsdAmount("US$2") == "2$" && Scanner.UsdAmount("US$$2") == "2$" && Scanner.UsdAmount("$10") == "10$" &&
              Scanner.UsdAmount("5 USD") == "5$" && Scanner.UsdAmount("7,50 $") == "7.50$" && Scanner.UsdAmount("15 USDT") == "15$" &&
              Scanner.UsdAmount("-") == "-" && Scanner.UsdAmount(null) == "-", "Deposit amounts are shown as USD like 2$");
        Check(Scanner.UsdAmount("10€") == "10 EUR" && Scanner.UsdAmount("R$ 25") == "25 BRL", "A non-dollar deposit keeps its own currency instead of being shown as dollars");

        // Several links can be added at once; anything that is not one whole giveaway link is skipped.
        List<string> skipped;
        List<string> pasted = Scanner.ParseLinkList("https://peereira.club/011026/\r\n\r\nhttps://neok.club/011026\r\nhttps://PEEREIRA.club/011026/ , notes here\nhttps://evil.club/010926/--user-data-dir=C:\\x", out skipped);
        Check(pasted.Count == 2 && pasted[0] == "https://peereira.club/011026/" && pasted[1] == "https://neok.club/011026/", "Pasted links are split, normalised and de-duplicated");
        Check(skipped.Count == 3 && skipped.Contains("notes") && skipped.Contains("here"), "Pasted text that is not a giveaway link is skipped and reported");

        // New creators are probed by Deep Search.
        Check(Scanner.InferCreator("https://thetaco.club/011026/", null) == "TACO" && Scanner.InferCreator("https://maherco.club/011026/", null) == "Maherco",
            "Creators found in November 2026 are built in");
        List<string> slugs = (List<string>)typeof(Scanner).GetMethod("FastProbeSlugs", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
        DateTime nextMonth = DateTime.Now.AddMonths(1);
        Check(slugs.Contains(string.Format("01{0:00}{1:00}", nextMonth.Month, nextMonth.Year % 100)), "Deep Search also probes next month's giveaway date");

        // The Refresh button never opens a browser.
        typeof(Scanner).GetField("Client", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null,
            new HttpClient(new FixedPageHandler("<html><body>Tickets left: 10 / 500</body></html>")));
        Scanner.QuickRefresh = true;
        GiveawayItem quick = Scanner.ValidateAsync(new Candidate("Quick", "https://quick.club/011126/", "saved"), new SemaphoreSlim(1)).GetAwaiter().GetResult();
        Scanner.QuickRefresh = false;
        Check(quick.Status == "active" && quick.Ticket == "10 / 500" && quick.RenderedAt == null, "Refresh reads tickets from the page without opening a browser");

        // Every 3D icon the interface uses is embedded in the exe.
        string[] iconNames = { Icons.Gift, Icons.Package, Icons.Stopwatch, Icons.Check, Icons.Coin, Icons.MoneyBag, Icons.Banknote, Icons.MoneyWings,
            Icons.Gem, Icons.Dagger, Icons.Trophy, Icons.Crown, Icons.Star, Icons.GlowingStar, Icons.Gamepad, Icons.Magnifier, Icons.Hourglass, Icons.Fire };
        Check(iconNames.All(n => Icons.Get(n, 32) != null), "All 3D icons are embedded in the app");

        // The browser for page details is found automatically, Edge first.
        string standardEdge = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe");
        string chosen = Scanner.FindChromiumBrowser();
        Check(!File.Exists(standardEdge) || (chosen != null && chosen.EndsWith("msedge.exe", StringComparison.OrdinalIgnoreCase)), "Edge is used for page details when it is installed");

        // A saved deadline must follow the live timer (a giveaway can be extended).
        DateTime clock = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
        TimeSpan thirtyMin = TimeSpan.FromMinutes(30);
        Check(Scanner.NeedsDeadlineRender(true, false, true, clock.AddMinutes(-45), true, false, thirtyMin, clock), "A saved deadline older than the recheck time is read again from the live timer");
        Check(!Scanner.NeedsDeadlineRender(true, false, true, clock.AddMinutes(-5), true, false, thirtyMin, clock), "A recently read deadline is not read again on an automatic refresh");
        Check(Scanner.NeedsDeadlineRender(true, false, true, clock.AddMinutes(-1), true, true, thirtyMin, clock), "A Refresh or Deep Search started by the user always re-reads the timer");
        Check(Scanner.NeedsDeadlineRender(true, false, true, null, false, false, thirtyMin, clock), "A saved deadline that was never confirmed against the live timer is read again");
        Check(!Scanner.NeedsDeadlineRender(true, true, true, clock.AddHours(-9), true, true, thirtyMin, clock), "A deadline printed in the page text is used as is, with no browser");
        Check(!Scanner.NeedsDeadlineRender(false, false, true, clock.AddHours(-9), true, true, thirtyMin, clock), "Ended or unknown pages never open a browser for the deadline");
        Check(Scanner.NeedsDeadlineRender(true, false, false, null, false, false, thirtyMin, clock) && !Scanner.NeedsDeadlineRender(true, false, false, clock.AddHours(-1), true, false, thirtyMin, clock), "A missing deadline is read once, then retried only after the metadata retry interval");

        // The automatic timer must also find new giveaways, not only re-check saved ones.
        Check(Scanner.AutoDeepSearchDue(null, null, 60, clock), "An automatic Deep Search runs when none has ever completed");
        Check(Scanner.AutoDeepSearchDue(clock.AddMinutes(-61).ToString("o"), null, 60, clock), "An automatic Deep Search runs once the interval has passed");
        Check(!Scanner.AutoDeepSearchDue(clock.AddMinutes(-20).ToString("o"), null, 60, clock), "A recent Deep Search means the timer only refreshes saved giveaways");
        Check(!Scanner.AutoDeepSearchDue(clock.AddDays(-2).ToString("o"), clock.AddMinutes(-10), 60, clock), "A failed automatic Deep Search is not retried on every timer tick");
        Check(!Scanner.AutoDeepSearchDue(null, null, 0, clock), "Automatic Deep Search can be turned off");
        Check(new AppSettings().AutoDeepSearchMinutes == 60, "Automatic Deep Search is on by default, hourly");
        AppSettings deepTuned = new AppSettings { AutoDeepSearchMinutes = 3 };
        deepTuned.Normalize();
        AppSettings deepOff = new AppSettings { AutoDeepSearchMinutes = -7 };
        deepOff.Normalize();
        Check(deepTuned.AutoDeepSearchMinutes == 15 && deepOff.AutoDeepSearchMinutes == 0, "Automatic Deep Search interval is at least 15 minutes, and 0 or less means off");

        // A check that fails to read a page must be retried, and never mistaken for "no giveaway here".
        string endedHtml = "<html><body>Tickets left: 0 / 500</body></html>";
        FieldInfo clientField = typeof(Scanner).GetField("Client", BindingFlags.NonPublic | BindingFlags.Static);
        FlakyHandler resets = new FlakyHandler(2, null, endedHtml);
        clientField.SetValue(null, new HttpClient(resets));
        GiveawayItem afterResets = Scanner.ValidateAsync(new Candidate("A", "https://flaky-a.club/010926/", "saved"), new SemaphoreSlim(1)).GetAwaiter().GetResult();
        Check(afterResets.Status == "ended" && !afterResets.Unreachable && resets.Calls == 3, "A page is found after two dropped connections");
        FlakyHandler busy = new FlakyHandler(2, HttpStatusCode.ServiceUnavailable, endedHtml);
        clientField.SetValue(null, new HttpClient(busy));
        GiveawayItem afterBusy = Scanner.ValidateAsync(new Candidate("B", "https://flaky-b.club/010926/", "saved"), new SemaphoreSlim(1)).GetAwaiter().GetResult();
        Check(afterBusy.Status == "ended" && !afterBusy.Unreachable, "A page is found after two 503 answers");
        FlakyHandler dead = new FlakyHandler(100, null, endedHtml);
        clientField.SetValue(null, new HttpClient(dead));
        GiveawayItem neverReached = Scanner.ValidateAsync(new Candidate("C", "https://flaky-c.club/010926/", "saved"), new SemaphoreSlim(1)).GetAwaiter().GetResult();
        Check(neverReached.Unreachable && neverReached.Status == "unknown" && dead.Calls == 3, "A page that never answers is flagged unreachable after three tries");
        FlakyHandler missing = new FlakyHandler(100, HttpStatusCode.NotFound, endedHtml);
        clientField.SetValue(null, new HttpClient(missing));
        GiveawayItem absent = Scanner.ValidateAsync(new Candidate("D", "https://flaky-d.club/010926/", "saved"), new SemaphoreSlim(1)).GetAwaiter().GetResult();
        Check(!absent.Unreachable && missing.Calls == 1, "A real 404 is final and is not retried");

        // A scan runs on a copy: a Join click made meanwhile must survive, and new finds must be added.
        AppData liveData = new AppData();
        liveData.Items.Add(new GiveawayItem { Creator = "X", Url = "https://x.club/011026/", Status = "active", Ticket = "5 / 10", Joined = true, JoinedAt = "2026-10-23T00:00:00Z" });
        liveData.Items.Add(new GiveawayItem { Creator = "Added", Url = "https://added-by-user.club/011026/", Status = "active", Ticket = "1 / 2" });
        AppData scannedCopy = new AppData();
        scannedCopy.Items.Add(new GiveawayItem { Creator = "X", Url = "https://x.club/011026/", Status = "ended", Ticket = "0 / 10", Deadline = "Ended", Joined = false });
        scannedCopy.Items.Add(new GiveawayItem { Creator = "Found", Url = "https://found.club/011026/", Status = "active", Ticket = "9 / 9" });
        scannedCopy.LastScan = "2026-10-23T01:00:00Z";
        Scanner.ApplyScanResults(liveData, scannedCopy);
        GiveawayItem mergedX = liveData.Items.First(i => i.Url == "https://x.club/011026/");
        Check(mergedX.Joined && mergedX.JoinedAt != null && mergedX.Status == "ended" && mergedX.Ticket == "0 / 10", "A scan keeps a Join made while it ran and still applies its findings");
        Check(liveData.Items.Any(i => i.Url == "https://added-by-user.club/011026/") && liveData.Items.Any(i => i.Url == "https://found.club/011026/") && liveData.Items.Count == 3 && liveData.LastScan == scannedCopy.LastScan, "A scan keeps links added meanwhile and adds new finds without duplicates");

        // An ended giveaway is only reopened by a real, future deadline.
        AppData reopen = new AppData();
        reopen.Items.Add(new GiveawayItem { Creator = "R", Url = "https://reopen.club/010926/", Status = "ended", Ticket = "436 / 625", Deadline = "Ended" });
        Scanner.MergeOne(reopen, new GiveawayItem { Creator = "R", Url = "https://reopen.club/010926/", Status = "active", Ticket = "436 / 625", Deadline = "-", LastChecked = DateTime.UtcNow.ToString("o") });
        Check(reopen.Items[0].Status == "ended", "An ended giveaway is not reopened by a page that merely lacks an ended signal");
        Scanner.MergeOne(reopen, new GiveawayItem { Creator = "R", Url = "https://reopen.club/010926/", Status = "active", Ticket = "436 / 625", Deadline = DateTime.Now.AddDays(5).ToString("MMM d, yyyy").ToUpperInvariant(), LastChecked = DateTime.UtcNow.ToString("o") });
        Check(reopen.Items[0].Status == "active", "An extended giveaway with a future deadline is reopened");

        // Typed links: the whole text must be one giveaway link.
        bool rejectedExtraText = false;
        try { Scanner.ValidateOneAsync("", "https://evil.club/010926/ --user-data-dir=C:\\temp\\x https://real.club/010926/").GetAwaiter().GetResult(); }
        catch (ArgumentException) { rejectedExtraText = true; }
        Check(rejectedExtraText, "A typed link with extra text around it is rejected");
        clientField.SetValue(null, new HttpClient(new FixedPageHandler(endedHtml)));
        GiveawayItem typedOk = Scanner.ValidateOneAsync("", "  https://typed-ok.club/010926  ").GetAwaiter().GetResult();
        Check(typedOk.Status == "ended" && typedOk.Url == "https://typed-ok.club/010926/", "A plain typed link is accepted and normalised");

        typeof(Scanner).GetField("Client", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, new HttpClient(new RedirectHandler()));
        GiveawayItem redirected = Scanner.ValidateAsync(new Candidate("Alpha", data.Items[0].Url, "saved"), new SemaphoreSlim(1)).GetAwaiter().GetResult();
        Check(redirected.Url == data.Items[0].Url && redirected.Status == "unknown" && redirected.Error != null, "Redirect preserves identity and reports failed validation");
        Scanner.MergeOne(data, redirected);
        Check(data.Items.Count == 3 && data.Items[0].Status == "active", "Failed redirect check does not duplicate or reclassify saved entry");
        Application.EnableVisualStyles();
        using (MainForm form = new MainForm(data, false))
        {
            form.Show();
            Application.DoEvents();
            DataGridView grid = (DataGridView)Field(form, "grid");
            Check(grid.Rows.Count == 2, "Active excludes joined entries");
            int joinCol = grid.Columns["JoinedAction"].Index, ticketCol = grid.Columns["Ticket"].Index, prizeCol = grid.Columns["PrizePool"].Index;
            Call(form, "GridCellMouseClick", grid, new DataGridViewCellMouseEventArgs(joinCol, 0, 4, 4, new MouseEventArgs(MouseButtons.Right, 1, 4, 4, 0)));
            Check(!data.Items[0].Joined, "Right click does not join a giveaway");
            grid.CurrentCell = grid.Rows[1].Cells[0];
            Call(form, "Render");
            Check(((GiveawayItem)grid.CurrentRow.Tag).Creator == "Beta", "Rendering preserves selected giveaway");
            Call(form, "GridColumnHeaderMouseClick", grid, new DataGridViewCellMouseEventArgs(ticketCol, -1, 0, 0, new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0)));
            Check(((GiveawayItem)grid.Rows[0].Tag).Creator == "Beta", "Tickets sort numerically");
            // Prize pools sort by amount: 500$ before 1,000$, unknown last.
            data.Items[0].PrizePool = "1,000$"; data.Items[1].PrizePool = "500$";
            Call(form, "GridColumnHeaderMouseClick", grid, new DataGridViewCellMouseEventArgs(prizeCol, -1, 0, 0, new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0)));
            Check(((GiveawayItem)grid.Rows[0].Tag).Creator == "Beta" && ((GiveawayItem)grid.Rows[1].Tag).Creator == "Alpha" &&
                  Convert.ToString(grid.Rows[0].Cells["PrizePool"].Value) == "500$", "Prize pool column shows and sorts by amount");
            ((TextBox)Field(form, "filterBox")).Text = "missing";
            Call(form, "Render");
            Check(grid.Rows.Count == 0, "Unmatched search produces empty view");
            ((TextBox)Field(form, "filterBox")).Clear();
            ((Button)Field(form, "joinedButton")).PerformClick();
            Check(grid.Rows.Count == 1 && ((GiveawayItem)grid.Rows[0].Tag).Joined, "Joined view is exclusive");
            Call(form, "GridCellMouseClick", grid, new DataGridViewCellMouseEventArgs(joinCol, 0, 4, 4, new MouseEventArgs(MouseButtons.Left, 1, 4, 4, 0)));
            ((Button)Field(form, "historyButton")).PerformClick();
            Check(grid.Rows.Count == 1, "Leaving Joined restores history when below the cap");
            ((Button)Field(form, "historyButton")).PerformClick();
            Check(grid.Rows.Count == 1, "Selected History tab remains on History when clicked again");
            ((Button)Field(form, "activeButton")).PerformClick();
            Check(grid.Rows.Count == 2, "Active tab returns to unjoined active giveaways");
            form.Size = form.MinimumSize;
            Application.DoEvents();
            Button add = (Button)Field(form, "addButton");
            Check(add.Left >= 0 && add.Right <= add.Parent.ClientSize.Width, "All header actions fit at minimum size");
            Check(grid.GetCellDisplayRectangle(joinCol, 0, false).Right <= grid.ClientSize.Width && grid.GetCellDisplayRectangle(joinCol, 0, false).Width > 0,
                "Row actions fit without horizontal scrolling at minimum size");
            // The longest real creator name and entry code fit in full (no "...") at the minimum size.
            int nameRoom = grid.Columns["Creator"].Width - 16 - 32 - 12 - 8;
            int codeRoom = grid.Columns["PromoCode"].Width - 16 - 52 - 22;
            using (Font nameFont = new Font("Segoe UI Semibold", 10F))
            using (Font codeFont = new Font("Consolas", 9F))
                Check(TextRenderer.MeasureText("Paulinhoeletric", nameFont, new Size(4000, 60), TextFormatFlags.NoPadding).Width <= nameRoom &&
                      TextRenderer.MeasureText("LP-PAULINHOELETRIC-021026", codeFont, new Size(4000, 60), TextFormatFlags.NoPadding).Width <= codeRoom,
                      "Long creator names and entry codes are shown in full at minimum size");
            int linkRoom = grid.Columns["Link"].Width - 32 - 30;
            using (Font linkFont = new Font("Segoe UI", 8F))
                Check(TextRenderer.MeasureText("drewunboxing.club/011126", linkFont, new Size(4000, 60), TextFormatFlags.NoPadding).Width <= linkRoom,
                    "Long giveaway links are shown in full at minimum size");
            using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
            { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.Combine(folder, "minimum-window.png")); }
            form.Size = new Size(1240, 800);
            Application.DoEvents();
            using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
            { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.Combine(folder, "default-window.png")); }
            ((Button)Field(form, "logsButton")).PerformClick();
            Task.Run(delegate { ActivityLog.Write("TEST", "Worker-thread activity is visible"); }).Wait();
            Call(form, "UpdateLogs");
            Check(((TextBox)Field(form, "logText")).Text.Contains("Worker-thread activity is visible"), "Logs tab displays background worker activity");
            Check(!grid.Visible && ((Label)Field(form, "viewSubtitle")).Text.Contains("Browsers 0/" + BrowserProcessManager.MaxConcurrent + " (0 processes)"), "Logs tab shows browser process count");
            using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
            { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.Combine(folder, "logs-window.png")); }
            ((Button)Field(form, "activeButton")).PerformClick();
            Check(grid.Visible && grid.Rows.Count == 2, "Leaving Logs restores giveaway navigation");

            // The Quizzes tab lists quizzes with their own columns; Creator (formerly Active) restores the giveaway columns.
            Check(((Button)Field(form, "activeButton")).Text == "Creator", "The first tab is called Creator");
            data.Quizzes.Add(new GiveawayItem { Creator = "Closed Quiz", Url = "https://community.skin.club/en/quizzes/closed", Status = "ended", Ticket = "810", PrizePool = "-", Deadline = "Ended" });
            data.Quizzes.Add(new GiveawayItem { Creator = "Live Quiz", Url = "https://community.skin.club/en/quizzes/live", Status = "active", Ticket = "126", PrizePool = "Huntsman Knife", Deadline = DateTime.Now.AddDays(9).ToString("MMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture).ToUpperInvariant() });
            ((Button)Field(form, "quizzesButton")).PerformClick();
            Check(grid.Rows.Count == 2 && ((GiveawayItem)grid.Rows[0].Tag).Creator == "Live Quiz" && ((Button)Field(form, "quizzesButton")).Text == "Quizzes",
                "Quizzes tab lists the quizzes, live ones first");
            Check(!grid.Columns["PromoCode"].Visible && !grid.Columns["MinimumDeposit"].Visible && !grid.Columns["JoinedAction"].Visible &&
                  grid.Columns["Creator"].HeaderText == "QUIZ" && grid.Columns["Ticket"].HeaderText == "PEOPLE" && grid.Columns["PrizePool"].HeaderText == "1ST PRIZE",
                "Quizzes tab shows quiz columns and hides promocode, deposit and join");
            using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
            { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.Combine(folder, "quizzes-window.png")); }
            ((Button)Field(form, "activeButton")).PerformClick();
            Check(grid.Rows.Count == 2 && grid.Columns["PromoCode"].Visible && grid.Columns["JoinedAction"].Visible && grid.Columns["Creator"].HeaderText == "CREATOR" && grid.Columns["Creator"].Width == 180,
                "Leaving Quizzes restores the giveaway columns");
        }
        for (int n = 0; n < 2100; n++) ActivityLog.Write("TEST", "Bounded entry " + n);
        long logVersion = -1;
        string boundedLog = ActivityLog.Snapshot(ref logVersion);
        Check(boundedLog.Split(new string[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries).Length == 2000, "Session log cannot grow beyond 2000 entries");
        // History opens with the most recently ended giveaway on top, and each tab keeps its own sort.
        AppData endedOrder = new AppData();
        DateTime endedBase = DateTime.UtcNow;
        endedOrder.Items.Add(new GiveawayItem { Creator = "Zed", Url = "https://zed.club/010826/", Status = "ended", HistorySince = endedBase.AddDays(-1).ToString("o") });
        endedOrder.Items.Add(new GiveawayItem { Creator = "Amy", Url = "https://amy.club/010826/", Status = "ended", HistorySince = endedBase.AddDays(-9).ToString("o") });
        endedOrder.Items.Add(new GiveawayItem { Creator = "Max", Url = "https://max.club/010926/", Status = "ended", HistorySince = endedBase.AddDays(-3).ToString("o") });
        endedOrder.Items.Add(new GiveawayItem { Creator = "Live", Url = "https://live.club/011026/", Status = "active", Ticket = "1 / 2" });
        endedOrder.Items.Add(new GiveawayItem { Creator = "Abe", Url = "https://abe.club/011026/", Status = "active", Ticket = "5 / 9" });
        using (MainForm orderForm = new MainForm(endedOrder, false))
        {
            orderForm.Show();
            Application.DoEvents();
            DataGridView orderGrid = (DataGridView)Field(orderForm, "grid");
            Func<string> names = delegate { return string.Join(",", orderGrid.Rows.Cast<DataGridViewRow>().Select(r => ((GiveawayItem)r.Tag).Creator).ToArray()); };
            Check(names() == "Abe,Live", "Active still lists creators alphabetically");
            ((Button)Field(orderForm, "historyButton")).PerformClick();
            Check(names() == "Zed,Max,Amy", "History lists the most recently ended giveaway first");
            ((Button)Field(orderForm, "activeButton")).PerformClick();
            Check(names() == "Abe,Live", "Leaving History restores the Active sort");
            ((Button)Field(orderForm, "historyButton")).PerformClick();
            Check(names() == "Zed,Max,Amy", "Returning to History keeps newest-ended first");
            Call(orderForm, "GridColumnHeaderMouseClick", orderGrid, new DataGridViewCellMouseEventArgs(orderGrid.Columns["Deadline"].Index, -1, 0, 0, new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0)));
            Check(names() == "Amy,Max,Zed", "Clicking the History deadline header reverses the order");
        }

        CheckDialogLayout(new SettingsForm(), "Settings dialog");
        CheckDialogLayout(new AddLinkForm(), "Add link dialog");

        // Closing the window keeps the app running in the tray; Exit in the tray menu really closes it.
        // (Last, because exiting shuts the browser manager down for the rest of the process.)
        AppData trayData = new AppData();
        MainForm trayForm = new MainForm(trayData, false);
        trayForm.Show();
        Application.DoEvents();
        Call(trayForm, "BuildTray");
        NotifyIcon trayIcon = (NotifyIcon)Field(trayForm, "tray");
        ContextMenuStrip trayMenu = trayIcon.ContextMenuStrip;
        string[] trayItems = trayMenu.Items.Cast<ToolStripItem>().Where(i => !(i is ToolStripSeparator)).Select(i => i.Text).ToArray();
        Check(trayIcon.Visible && trayIcon.Icon != null && string.Join("|", trayItems) == "Open SkinClub GW Finder|Deep Search|Refresh|Joined giveaways|Exit",
            "Tray icon offers Open, Deep Search, Refresh, Joined giveaways and Exit");
        trayForm.Close();
        Application.DoEvents();
        Check(!trayForm.IsDisposed && !trayForm.Visible, "Closing the window hides the app to the tray instead of exiting");
        Call(trayForm, "ShowJoined");
        Application.DoEvents();
        Check(trayForm.Visible && ((DataGridView)Field(trayForm, "grid")).Visible && (bool)Field(trayForm, "showingJoined"), "Joined giveaways in the tray menu opens the window on Joined");
        Call(trayForm, "ExitApp");
        Application.DoEvents();
        Check(trayForm.IsDisposed && !trayIcon.Visible, "Exit in the tray menu closes the app and removes the tray icon");
        Check(Ui.AppIcon != null && Ui.AppIcon.Width > 0, "The app icon is embedded in the exe");

        Console.WriteLine(checks + " checks passed. Artifacts: " + folder);
    }
}

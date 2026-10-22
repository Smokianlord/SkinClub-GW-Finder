using System;
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
        tuned.ExtraCreators["Good"] = "WWW.Good.Club";
        tuned.ExtraCreators["Bad"] = "not a domain";
        tuned.ExtraCreators["Path"] = "evil.club/steal";
        AppSettings.Current = tuned;
        Check(tuned.RefreshMinutes == 1 && tuned.HistoryLimit == 500 && tuned.HttpTimeoutSeconds == 3, "Settings values are clamped to safe ranges");
        Check(tuned.ExtraCreators.Count == 1 && tuned.ExtraCreators["Good"] == "good.club", "Only valid extra creator domains are kept");
        tuned.Save();
        Check(File.ReadAllText(AppSettings.SettingsFile).Contains("\"RefreshMinutes\": 1"), "Settings are saved as readable JSON");
        typeof(AppSettings).GetField("current", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, null);
        Check(AppSettings.Current.ExtraCreators.ContainsKey("Good"), "Saved settings load back");
        File.WriteAllText(AppSettings.SettingsFile, "{ not json");
        typeof(AppSettings).GetField("current", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, null);
        Check(AppSettings.Current.HistoryLimit == 30, "Broken settings.json falls back to defaults");
        AppSettings.Current = new AppSettings();

        HttpResponseMessage big = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 3 * 1024 * 1024)) };
        string capped = ((Task<string>)typeof(Scanner).GetMethod("ReadBodyCappedAsync", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { big, 1024 * 1024 })).GetAwaiter().GetResult();
        Check(capped.Length == 1024 * 1024, "Downloaded page bodies are capped in memory");

        Check(GiveawayInfo.IsEntryCodeFor("LP-KRYOZ-010926", "https://kryoz.club/010926/") &&
              !GiveawayInfo.IsEntryCodeFor("LP-XTVFAGX7QP9W", "https://kryoz.club/010926/") &&
              !GiveawayInfo.IsEntryCodeFor("-", "https://kryoz.club/010926/"), "Only a code ending with the URL date slug is an entry code");

        AppData codes = new AppData();
        codes.Items.Add(new GiveawayItem { Creator = "Kryoz", Url = "https://kryoz.club/010926/", Status = "ended", PromoCode = "LP-KRYOZ-010926", MinimumDeposit = "$10" });
        codes.Items.Add(new GiveawayItem { Creator = "Kryoz", Url = "https://kryoz.club/010826/", Status = "ended", PromoCode = "LP-XTVFAGX7QP9W", MinimumDeposit = "$5" });
        codes.Items.Add(new GiveawayItem { Creator = "Yumi", Url = "https://tooyumi.club/010826/", Status = "ended", PromoCode = "LP-ABCDEF123456", MinimumDeposit = "-" });
        codes.Items.Add(new GiveawayItem { Creator = "Kryoz", Url = "https://kryoz.club/020926/", Status = "ended", PromoCode = "LP-KRYOZ-010826", MinimumDeposit = "$3" });
        Check(GiveawayInfo.RepairEntryCodes(codes), "Bad entry codes are detected on load");
        Check(codes.Items[1].PromoCode == "LP-KRYOZ-010826" && codes.Items[1].MinimumDeposit == "$5", "A discount code left by an ended page is rebuilt from the creator's code pattern, keeping the deposit");
        Check(codes.Items[2].PromoCode == "-", "A bad code with no known pattern is cleared instead of guessed");
        Check(codes.Items[3].PromoCode == "LP-KRYOZ-020926" && codes.Items[3].MinimumDeposit == "-", "A code from another giveaway date resets the mixed-up record");
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
            Call(form, "GridCellMouseClick", grid, new DataGridViewCellMouseEventArgs(6, 0, 4, 4, new MouseEventArgs(MouseButtons.Right, 1, 4, 4, 0)));
            Check(!data.Items[0].Joined, "Right click does not join a giveaway");
            grid.CurrentCell = grid.Rows[1].Cells[0];
            Call(form, "Render");
            Check(((GiveawayItem)grid.CurrentRow.Tag).Creator == "Beta", "Rendering preserves selected giveaway");
            Call(form, "GridColumnHeaderMouseClick", grid, new DataGridViewCellMouseEventArgs(2, -1, 0, 0, new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0)));
            Check(((GiveawayItem)grid.Rows[0].Tag).Creator == "Beta", "Tickets sort numerically");
            ((TextBox)Field(form, "filterBox")).Text = "missing";
            Call(form, "Render");
            Check(grid.Rows.Count == 0, "Unmatched search produces empty view");
            ((TextBox)Field(form, "filterBox")).Clear();
            ((Button)Field(form, "joinedButton")).PerformClick();
            Check(grid.Rows.Count == 1 && ((GiveawayItem)grid.Rows[0].Tag).Joined, "Joined view is exclusive");
            Call(form, "GridCellMouseClick", grid, new DataGridViewCellMouseEventArgs(6, 0, 4, 4, new MouseEventArgs(MouseButtons.Left, 1, 4, 4, 0)));
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
            Check(grid.GetCellDisplayRectangle(6, 0, false).Right <= grid.ClientSize.Width, "Row actions fit without horizontal scrolling at minimum size");
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
        }
        for (int n = 0; n < 2100; n++) ActivityLog.Write("TEST", "Bounded entry " + n);
        long logVersion = -1;
        string boundedLog = ActivityLog.Snapshot(ref logVersion);
        Check(boundedLog.Split(new string[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries).Length == 2000, "Session log cannot grow beyond 2000 entries");
        CheckDialogLayout(new SettingsForm(), "Settings dialog");
        CheckDialogLayout(new AddLinkForm(), "Add link dialog");
        Console.WriteLine(checks + " checks passed. Artifacts: " + folder);
    }
}

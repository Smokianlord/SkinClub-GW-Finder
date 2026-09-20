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
    [STAThread]
    static void Main()
    {
        string folder = Path.Combine(Path.GetTempPath(), "SkinClub-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        // Framework test process only: never read or write the user's saved data.
        typeof(DataStore).GetField("DataDir").SetValue(null, folder);
        typeof(DataStore).GetField("DataFile").SetValue(null, Path.Combine(folder, "data.json"));
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
            Check(!grid.Visible && ((Label)Field(form, "viewSubtitle")).Text.Contains("0/5"), "Logs tab shows browser process count");
            using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
            { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.Combine(folder, "logs-window.png")); }
            ((Button)Field(form, "activeButton")).PerformClick();
            Check(grid.Visible && grid.Rows.Count == 2, "Leaving Logs restores giveaway navigation");
        }
        for (int n = 0; n < 2100; n++) ActivityLog.Write("TEST", "Bounded entry " + n);
        long logVersion = -1;
        string boundedLog = ActivityLog.Snapshot(ref logVersion);
        Check(boundedLog.Split(new string[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries).Length == 2000, "Session log cannot grow beyond 2000 entries");
        Console.WriteLine(checks + " checks passed. Artifacts: " + folder);
    }
}

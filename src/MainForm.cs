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
    public class MainForm : ThemedForm
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
        private Button settingsButton;
        private NotifyIcon tray;
        private readonly HashSet<string> endingNotified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
        private readonly Font creatorFont = Ui.DisplayFont(11F);
        private readonly Font ticketFont = Ui.DisplayFont(10.5F);
        private CopyToastControl copyToast;
        private string sortColumn = "Creator";
        private bool sortAscending = true;
        // Each tab remembers its own sort. History opens with the most recently ended giveaway on top
        // (its Deadline column holds "Ended", so that column sorts by when the giveaway ended).
        private string currentSortView = "active";
        private readonly Dictionary<string, KeyValuePair<string, bool>> sortByView = new Dictionary<string, KeyValuePair<string, bool>>
        {
            { "active", new KeyValuePair<string, bool>("Creator", true) },
            { "joined", new KeyValuePair<string, bool>("Creator", true) },
            { "history", new KeyValuePair<string, bool>("Deadline", false) }
        };

        // Restrained product-dashboard palette: neutral ink surfaces with precise accent usage.
        private readonly Color Bg = Ui.Bg;
        private readonly Color Surface = Ui.Surface;
        private readonly Color Surface2 = Ui.Surface2;
        private readonly Color Surface3 = Ui.Surface3;
        private readonly Color Line = Ui.Line;
        private readonly Color TextColor = Ui.Text;
        private readonly Color Muted = Ui.Muted;
        private readonly Color Accent = Ui.Accent;
        private readonly Color Accent2 = Ui.Cyan;
        private readonly Color Success = Ui.Success;
        private readonly Color Warning = Ui.Warning;
        private readonly Color Danger = Ui.Danger;
        private readonly Color PriorityGreen = Ui.Lime;
        private readonly Color PriorityYellow = Ui.Gold;
        private readonly Color PriorityRed = Color.FromArgb(255, 61, 110);
        // Row layers drawn over the backdrop: glass normally, brighter glass under the mouse / selection.
        private readonly Color RowHover = Color.FromArgb(226, 44, 38, 80);
        private readonly Color RowSelected = Color.FromArgb(232, 58, 30, 88);
        private readonly Color LinkColor = Color.FromArgb(255, 130, 222);
        private StatCard activeStat, endingStat, joinedStat, cheapestStat;
        public MainForm()
            : this(null, true)
        {
        }

        internal MainForm(AppData initialData, bool automaticRefresh)
        {
            Text = Program.DisplayTitle;
            Width = 1320;
            Height = 800;
            // Wide enough to show every column, including Prize Pool, with names, links and codes in full.
            MinimumSize = new Size(1290, 720);
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
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception swallowed) { Diag.Swallow("Loading window icon", swallowed); }

            // Closing the app must also close every hidden browser render that it owns.
            // This prevents Edge/Chrome giveaway tabs from remaining in Task Manager.
            FormClosing += delegate
            {
                try { if (autoTimer != null) autoTimer.Stop(); } catch (Exception swallowed) { Diag.Swallow("Stopping auto-refresh timer", swallowed); }
                BrowserProcessManager.Shutdown();
            };

            data = initialData ?? DataStore.Load();
            // History is persisted data. Do not reset ended giveaways back to
            // unknown on startup; that made the History tab rebuild itself on
            // every launch instead of loading the saved classification.
            BuildUi();
            ActivityLog.Write("APP", "Ready. Browsers: up to " + BrowserProcessManager.MaxConcurrent + " at once, " + AppSettings.Current.BrowserMemoryLimitMB + " MB total; " + AppSettings.Current.RenderTimeoutSeconds + "-second page timeout.");
            logTimer.Interval = 500;
            logTimer.Tick += delegate { UpdateLogs(); };
            logTimer.Start();
            filterTimer.Interval = 120;
            filterTimer.Tick += delegate { filterTimer.Stop(); RenderView(false); };
            Render();

            // Start-up only refreshes the saved giveaways (quick). The automatic Deep Search runs from the
            // timer, once its interval has passed.
            if (automaticRefresh) Shown += async delegate { await RefreshAsync(false); };
            autoTimer = new System.Windows.Forms.Timer();
            autoTimer.Interval = AppSettings.Current.RefreshMinutes * 60 * 1000;
            autoTimer.Tick += async delegate { if (!scanning) await AutomaticCheckAsync(); };
            if (automaticRefresh)
            {
                autoTimer.Start();
                BuildTray();
            }
        }

        // When the last automatic Deep Search was started, so a failing one is not retried every tick.
        private DateTime? lastAutoDeepAttemptUtc;

        // Each timer tick re-checks the saved giveaways. When the Deep Search interval has passed it runs a
        // full Deep Search instead, which also re-checks every saved giveaway and is the only way new
        // giveaways are found. Never called at start-up, which only refreshes.
        private async Task AutomaticCheckAsync()
        {
            if (scanning) return;
            DateTime now = DateTime.UtcNow;
            if (Scanner.AutoDeepSearchDue(data.LastDeepScan, lastAutoDeepAttemptUtc, AppSettings.Current.AutoDeepSearchMinutes, now))
            {
                lastAutoDeepAttemptUtc = now;
                await DeepSearchAsync(false);
            }
            else await RefreshAsync(false);
        }

        private void BuildTray()
        {
            tray = new NotifyIcon();
            tray.Icon = Icon ?? SystemIcons.Application;
            tray.Text = "SkinClub GW Finder";
            tray.Visible = AppSettings.Current.Notifications;
            tray.BalloonTipClicked += delegate { BringToFrontFromTray(); };
            tray.MouseClick += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) BringToFrontFromTray(); };
        }

        // Also used when a second copy of the app is started: show this one instead.
        public void BringToFrontFromTray()
        {
            if (IsDisposed) return;
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Show();
            Activate();
        }

        private HashSet<string> ActiveKeys()
        {
            HashSet<string> keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (GiveawayItem i in data.Items)
                if (!i.Joined && string.Equals(i.Status, "active", StringComparison.OrdinalIgnoreCase) && i.Url != null) keys.Add(i.Url);
            return keys;
        }

        // Only automatic refreshes notify: after a manual action the user is already looking at the result.
        private void NotifyChanges(HashSet<string> before)
        {
            if (tray == null || !tray.Visible || !AppSettings.Current.Notifications) return;
            List<GiveawayItem> fresh = data.Items.Where(delegate(GiveawayItem i)
            {
                return !i.Joined && i.Url != null && string.Equals(i.Status, "active", StringComparison.OrdinalIgnoreCase) && !before.Contains(i.Url);
            }).ToList();
            if (fresh.Count > 0)
            {
                string names = string.Join(", ", fresh.Take(4).Select(delegate(GiveawayItem i) { return i.Creator; }).ToArray());
                tray.ShowBalloonTip(8000, fresh.Count == 1 ? "New giveaway" : fresh.Count + " new giveaways",
                    names + (fresh.Count > 4 ? " and more" : ""), ToolTipIcon.Info);
                return;
            }

            DateTime today = DateTime.Now.Date;
            List<GiveawayItem> ending = data.Items.Where(delegate(GiveawayItem i)
            {
                DateTime when;
                return !i.Joined && string.Equals(i.Status, "active", StringComparison.OrdinalIgnoreCase) && i.Url != null &&
                    DateTime.TryParse(i.Deadline, out when) && when.Date == today && !endingNotified.Contains(i.Url);
            }).ToList();
            if (ending.Count == 0) return;
            foreach (GiveawayItem i in ending) endingNotified.Add(i.Url);
            tray.ShowBalloonTip(8000, ending.Count == 1 ? "Giveaway ends today" : ending.Count + " giveaways end today",
                string.Join(", ", ending.Take(4).Select(delegate(GiveawayItem i) { return i.Creator; }).ToArray()), ToolTipIcon.Warning);
        }

        private void OpenSettings()
        {
            using (SettingsForm form = new SettingsForm())
            {
                if (form.ShowDialog(this) != DialogResult.OK) return;
                form.Apply();
            }
            autoTimer.Interval = AppSettings.Current.RefreshMinutes * 60 * 1000;
            if (tray != null) tray.Visible = AppSettings.Current.Notifications;
            DataStore.Save(data);
            Render();
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
                if (tray != null) { tray.Visible = false; tray.Dispose(); }
            }
            base.Dispose(disposing);
            if (disposing) { creatorFont.Dispose(); ticketFont.Dispose(); }
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
        private readonly Font cellFontSmall = new Font("Segoe UI", 8.5F);
        private readonly Font cellFontTiny = new Font("Segoe UI", 8F);
        private readonly Font cellBold = Ui.DisplayFont(10F);
        private readonly Font cellBoldSmall = Ui.DisplayFont(9F);
        private readonly Font numberFont = Ui.DisplayFont(10.5F);
        private readonly Font monoFont = Ui.MonoFont(10F);
        private readonly Font monoFontSmall = Ui.MonoFont(9F);

        // The first font in which the text fits the width (the last one otherwise, with an ellipsis).
        private static Font FitFont(Graphics g, string text, int width, params Font[] fonts)
        {
            foreach (Font f in fonts)
                if (TextRenderer.MeasureText(g, text ?? "", f, new Size(4000, 60), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width <= width) return f;
            return fonts[fonts.Length - 1];
        }
        private readonly Font smallFont = new Font("Segoe UI", 8.5F);
        private readonly Font headerFont = Ui.DisplayFont(8.5F);
        private ScanStrip scanStrip;
        private Panel statusDot;
        private Color statusDotColor = Ui.Success;
        private int hoverRow = -1;
        private int hoverColumn = -1;
        private bool hoverOnCopy;

        private void BuildUi()
        {
            SuspendLayout();

            // Every container is transparent: the window's backdrop (cases, coins, cash, skins and
            // SkinClub's pink / blue glows) shows through, and controls draw on top of it.
            TableLayoutPanel root = new GlassTable();
            root.Dock = DockStyle.Fill;
            root.BackColor = Color.Transparent;
            root.Padding = new Padding(28, 14, 28, 0);
            root.ColumnCount = 1;
            root.RowCount = 6;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 70F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 108F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54F));
            Controls.Add(root);

            // ---- Header: brand on the left, primary actions on the right ----
            TableLayoutPanel header = new GlassTable();
            header.Dock = DockStyle.Fill;
            header.Margin = new Padding(0);
            // The bottom 3 px are kept free of child controls for the divider line drawn below.
            header.Padding = new Padding(0, 0, 0, 3);
            header.BackColor = Color.Transparent;
            header.ColumnCount = 2;
            header.RowCount = 1;
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            // The site's glowing pink divider under the header.
            header.Paint += delegate(object sender, PaintEventArgs pe)
            {
                Rectangle line = new Rectangle(0, header.Height - 2, Math.Max(1, header.Width), 1);
                using (LinearGradientBrush b = new LinearGradientBrush(new Rectangle(0, 0, Math.Max(1, header.Width), 2), Color.Transparent, Color.Transparent, LinearGradientMode.Horizontal))
                {
                    ColorBlend cb = new ColorBlend();
                    cb.Colors = new Color[] { Color.FromArgb(0, Ui.Violet), Color.FromArgb(170, Ui.Hot), Color.FromArgb(170, Ui.Pink), Color.FromArgb(0, Ui.Violet) };
                    cb.Positions = new float[] { 0f, 0.35f, 0.65f, 1f };
                    b.InterpolationColors = cb;
                    pe.Graphics.FillRectangle(b, line);
                }
            };
            root.Controls.Add(header, 0, 0);

            FlowLayoutPanel brand = new GlassFlow();
            brand.Dock = DockStyle.Fill;
            brand.Margin = new Padding(0);
            brand.WrapContents = false;
            brand.FlowDirection = FlowDirection.LeftToRight;
            brand.BackColor = Color.Transparent;
            header.Controls.Add(brand, 0, 0);

            // Logo, title and the action buttons share one centre line: the buttons' faces sit 1 px below
            // their top edge and are 37 px tall, so their centre is 13 + 19 = 32, as is the logo's (10 + 22).
            BrandMark logo = new BrandMark();
            logo.Size = new Size(44, 44);
            logo.Margin = new Padding(0, 10, 12, 0);
            brand.Controls.Add(logo);

            BrandTitle title = new BrandTitle("v" + typeof(Program).Assembly.GetName().Version.ToString(3));
            title.Margin = new Padding(0, 10, 0, 0);
            brand.Controls.Add(title);

            FlowLayoutPanel actions = new GlassFlow();
            actions.AutoSize = true;
            actions.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            actions.Anchor = AnchorStyles.Right;
            actions.Margin = new Padding(0);
            actions.WrapContents = false;
            actions.FlowDirection = FlowDirection.LeftToRight;
            actions.BackColor = Color.Transparent;
            header.Controls.Add(actions, 1, 0);

            settingsButton = MakeHeaderButton("Settings", 100, false);
            addButton = MakeHeaderButton("Add links", 112, false);
            refreshButton = MakeHeaderButton("Refresh", 100, false);
            deepButton = MakeHeaderButton("Deep Search", 138, true);
            settingsButton.Margin = addButton.Margin = refreshButton.Margin = deepButton.Margin = new Padding(8, 0, 0, 0);
            actions.Controls.Add(settingsButton);
            settingsButton.Click += delegate { OpenSettings(); };
            actions.Controls.Add(addButton);
            actions.Controls.Add(refreshButton);
            actions.Controls.Add(deepButton);
            addButton.Click += async delegate { await AddLinkAsync(); };
            refreshButton.Click += async delegate { await RefreshAsync(true); };
            deepButton.Click += async delegate { await DeepSearchAsync(true); };

            // ---- Stats: what is worth opening right now ----
            TableLayoutPanel stats = new GlassTable();
            stats.Dock = DockStyle.Fill;
            stats.Margin = new Padding(0, 12, 0, 6);
            stats.BackColor = Color.Transparent;
            stats.ColumnCount = 4;
            stats.RowCount = 1;
            for (int c = 0; c < 4; c++) stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
            stats.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            activeStat = new StatCard("Active giveaways", Ui.Lime, Icons.Gift);
            endingStat = new StatCard("Ending in 48 hours", Ui.Hot, Icons.Stopwatch);
            joinedStat = new StatCard("Joined", Ui.Cyan, Icons.Check);
            cheapestStat = new StatCard("Cheapest entry", Ui.Gold, Icons.MoneyBag);
            StatCard[] cards = { activeStat, endingStat, joinedStat, cheapestStat };
            for (int c = 0; c < cards.Length; c++)
            {
                cards[c].Dock = DockStyle.Fill;
                cards[c].Margin = new Padding(c == 0 ? 0 : 8, 0, c == cards.Length - 1 ? 0 : 8, 0);
                stats.Controls.Add(cards[c], c, 0);
            }
            root.Controls.Add(stats, 0, 1);

            // ---- Navigation: tabs on the left, search on the right ----
            TableLayoutPanel nav = new GlassTable();
            nav.Dock = DockStyle.Fill;
            nav.Margin = new Padding(0);
            nav.BackColor = Color.Transparent;
            nav.ColumnCount = 2;
            nav.RowCount = 1;
            nav.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            nav.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 480F));
            nav.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.Controls.Add(nav, 0, 2);

            FlowLayoutPanel tabs = new GlassFlow();
            tabs.Dock = DockStyle.Fill;
            tabs.Margin = new Padding(0);
            // Same top as the search box and field picker, which are centred in the 58 px row.
            tabs.Padding = new Padding(0, 7, 0, 0);
            tabs.WrapContents = false;
            tabs.FlowDirection = FlowDirection.LeftToRight;
            tabs.BackColor = Color.Transparent;
            nav.Controls.Add(tabs, 0, 0);

            activeButton = MakeTab("Active");
            joinedButton = MakeTab("Joined");
            historyButton = MakeTab("History");
            logsButton = MakeTab("Logs");
            ((UiButton)activeButton).AccentColor = Ui.Lime;
            ((UiButton)joinedButton).AccentColor = Ui.Cyan;
            ((UiButton)historyButton).AccentColor = Ui.Gold;
            ((UiButton)logsButton).AccentColor = Ui.Pink;
            tabs.Controls.Add(activeButton);
            tabs.Controls.Add(joinedButton);
            tabs.Controls.Add(historyButton);
            activeButton.Click += delegate { showingHistory = false; showingJoined = false; showingLogs = false; Render(); };
            joinedButton.Click += delegate { showingJoined = true; showingHistory = false; showingLogs = false; Render(); };
            historyButton.Click += delegate { showingHistory = true; showingJoined = false; showingLogs = false; Render(); };
            logsButton.Click += delegate { showingLogs = true; showingHistory = false; showingJoined = false; Render(); };

            Panel navTools = new GlassPanel();
            navTools.Dock = DockStyle.Fill;
            navTools.Margin = new Padding(0);
            navTools.BackColor = Color.Transparent;
            nav.Controls.Add(navTools, 1, 0);

            TableLayoutPanel filterPanel = new GlassTable();
            filterPanelRef = filterPanel;
            filterPanel.Dock = DockStyle.Fill;
            filterPanel.Padding = new Padding(0);
            filterPanel.BackColor = Color.Transparent;
            filterPanel.ColumnCount = 2;
            filterPanel.RowCount = 1;
            filterPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            filterPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 142F));
            filterPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            navTools.Controls.Add(filterPanel);

            filterFieldBox = new UiPicker();
            filterFieldBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            filterFieldBox.Margin = new Padding(0, 0, 10, 0);
            filterFieldBox.Items.AddRange(new object[] { "Creator name", "All fields", "URL", "Prize pool", "Ticket", "Promocode", "Minimum deposit", "Deadline" });
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

            logActions = new GlassFlow();
            logActions.Dock = DockStyle.Fill;
            logActions.FlowDirection = FlowDirection.RightToLeft;
            logActions.Padding = new Padding(0, 7, 0, 0);
            logActions.BackColor = Color.Transparent;
            logActions.Visible = false;
            Button copyLogs = MakeHeaderButton("Copy logs", 100, false);
            copyLogs.Margin = new Padding(8, 0, 0, 0);
            copyLogs.Click += delegate
            {
                try { if (logText.TextLength > 0) Clipboard.SetText(logText.Text); }
                catch (Exception swallowed) { Diag.Swallow("Copying logs to the clipboard", swallowed); scanStatus.Text = "Clipboard is busy - try again"; }
            };
            Button clearLogs = MakeHeaderButton("Clear", 80, false);
            clearLogs.Margin = new Padding(8, 0, 0, 0);
            clearLogs.Click += delegate { ActivityLog.Clear(); UpdateLogs(); };
            logActions.Controls.Add(copyLogs);
            logActions.Controls.Add(clearLogs);
            navTools.Controls.Add(logActions);

            // ---- Context line: what this view shows ----
            TableLayoutPanel context = new GlassTable();
            context.Dock = DockStyle.Fill;
            context.Margin = new Padding(0);
            context.BackColor = Color.Transparent;
            context.ColumnCount = 2;
            context.RowCount = 1;
            context.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            context.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            context.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.Controls.Add(context, 0, 3);

            viewTitle = new GlassLabel();
            viewTitle.Text = "ACTIVE GIVEAWAYS";
            viewTitle.AutoSize = true;
            viewTitle.Anchor = AnchorStyles.Left;
            viewTitle.Margin = new Padding(0, 0, 14, 0);
            viewTitle.Font = Ui.DisplayFont(14F);
            viewTitle.ForeColor = TextColor;
            viewTitle.BackColor = Color.Transparent;
            context.Controls.Add(viewTitle, 0, 0);

            viewSubtitle = new GlassLabel();
            viewSubtitle.Text = "";
            viewSubtitle.Dock = DockStyle.Fill;
            viewSubtitle.Margin = new Padding(0);
            viewSubtitle.BackColor = Color.Transparent;
            viewSubtitle.ForeColor = Muted;
            viewSubtitle.Font = new Font("Segoe UI", 9F);
            viewSubtitle.TextAlign = ContentAlignment.MiddleLeft;
            viewSubtitle.AutoEllipsis = true;
            context.Controls.Add(viewSubtitle, 1, 0);

            // ---- Card holding the table / logs ----
            // A neon frame: pink on the left fading to electric blue on the right.
            Panel card = new GlassPanel();
            card.Dock = DockStyle.Fill;
            card.Margin = new Padding(0);
            card.Padding = new Padding(1);
            card.BackColor = Color.Transparent;
            card.Paint += delegate(object sender, PaintEventArgs pe)
            {
                Rectangle frame = new Rectangle(0, 0, Math.Max(2, card.Width) - 1, Math.Max(2, card.Height) - 1);
                using (LinearGradientBrush edge = new LinearGradientBrush(new Rectangle(0, 0, Math.Max(2, card.Width), Math.Max(2, card.Height)),
                    Color.FromArgb(200, Ui.Hot), Color.FromArgb(200, Ui.Violet), 15F))
                using (Pen pen = new Pen(edge))
                    pe.Graphics.DrawRectangle(pen, frame);
            };
            card.Resize += delegate { card.Invalidate(); };
            root.Controls.Add(card, 0, 4);

            TableLayoutPanel cardInner = new GlassTable();
            cardInner.Dock = DockStyle.Fill;
            cardInner.Margin = new Padding(0);
            cardInner.BackColor = Surface;
            cardInner.ColumnCount = 1;
            cardInner.RowCount = 2;
            cardInner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            cardInner.RowStyles.Add(new RowStyle(SizeType.Absolute, 3F));
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
            grid.ColumnHeadersHeight = 42;
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
            // Wide enough for the longest creator name (e.g. Paulinhoeletric) and entry code
            // (e.g. LP-PAULINHOELETRIC-021026) at the minimum window size; the link column takes the rest.
            creator.Width = 180;
            creator.SortMode = DataGridViewColumnSortMode.Programmatic;

            DataGridViewTextBoxColumn link = new DataGridViewTextBoxColumn();
            link.Name = "Link";
            link.HeaderText = "LINK";
            link.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            link.MinimumWidth = 120;
            link.SortMode = DataGridViewColumnSortMode.Programmatic;

            DataGridViewTextBoxColumn prizePool = new DataGridViewTextBoxColumn();
            prizePool.Name = "PrizePool";
            prizePool.HeaderText = "PRIZE POOL";
            prizePool.Width = 116;
            prizePool.SortMode = DataGridViewColumnSortMode.Programmatic;

            DataGridViewTextBoxColumn ticket = new DataGridViewTextBoxColumn();
            ticket.Name = "Ticket";
            ticket.HeaderText = "TICKETS";
            ticket.Width = 128;
            ticket.SortMode = DataGridViewColumnSortMode.Programmatic;

            DataGridViewTextBoxColumn promo = new DataGridViewTextBoxColumn();
            promo.Name = "PromoCode";
            promo.HeaderText = "PROMOCODE";
            promo.Width = 278;
            promo.SortMode = DataGridViewColumnSortMode.Programmatic;

            DataGridViewTextBoxColumn minimumDeposit = new DataGridViewTextBoxColumn();
            minimumDeposit.Name = "MinimumDeposit";
            minimumDeposit.HeaderText = "MIN DEPOSIT";
            minimumDeposit.Width = 100;
            minimumDeposit.SortMode = DataGridViewColumnSortMode.Programmatic;

            DataGridViewTextBoxColumn deadline = new DataGridViewTextBoxColumn();
            deadline.Name = "Deadline";
            deadline.HeaderText = "DEADLINE";
            deadline.Width = 128;
            deadline.SortMode = DataGridViewColumnSortMode.Programmatic;

            DataGridViewButtonColumn joinedAction = MakeIconColumn("JoinedAction", 52);

            grid.Columns.AddRange(new DataGridViewColumn[] { creator, link, prizePool, ticket, promo, minimumDeposit, deadline, joinedAction });
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
                int top = grid.ColumnHeadersHeight + Math.Max(84, (grid.ClientSize.Height - grid.ColumnHeadersHeight) / 2 - 10);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                PointF iconAt = new PointF(grid.ClientSize.Width / 2f, top - 42);
                BackdropArt.Glow(e.Graphics, iconAt, 64, Ui.Pink, 70);
                Icons.DrawCentered(e.Graphics, filterBox.TextLength > 0 ? Icons.Magnifier : (showingHistory ? Icons.Hourglass : (showingJoined ? Icons.Check : Icons.Package)),
                    iconAt.X, iconAt.Y, 64);
                Rectangle headingRect = new Rectangle(20, top, Math.Max(1, grid.ClientSize.Width - 40), 28);
                Rectangle detailRect = new Rectangle(20, top + 30, Math.Max(1, grid.ClientSize.Width - 40), 24);
                TextRenderer.DrawText(e.Graphics, heading.ToUpperInvariant(), creatorFont, headingRect, TextColor,
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
            logsPanel.BackColor = Color.FromArgb(16, 14, 34);
            logText = new TextBox();
            logText.Multiline = true;
            logText.ScrollBars = ScrollBars.Vertical;
            logText.Dock = DockStyle.Fill;
            logText.ReadOnly = true;
            logText.WordWrap = true;
            logText.BorderStyle = BorderStyle.None;
            logText.BackColor = Color.FromArgb(16, 14, 34);
            logText.ForeColor = Color.FromArgb(190, 186, 235);
            logText.Font = Ui.MonoFont(9.5F);
            logText.AccessibleName = "Live activity logs";
            Ui.DarkScrollBars(logText);
            Ui.DarkScrollBars(grid);
            logsPanel.Controls.Add(logText);
            content.Controls.Add(logsPanel);
            cardInner.Controls.Add(content, 0, 1);

            // ---- Status bar ----
            TableLayoutPanel status = new GlassTable();
            status.Dock = DockStyle.Fill;
            status.Margin = new Padding(0);
            status.BackColor = Color.Transparent;
            status.ColumnCount = 4;
            status.RowCount = 1;
            status.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            status.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 26F));
            status.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            status.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            status.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.Controls.Add(status, 0, 5);

            logsButton.Anchor = AnchorStyles.Left;
            logsButton.Margin = new Padding(0, 0, 8, 0);
            status.Controls.Add(logsButton, 0, 0);

            statusDot = new GlassPanel();
            statusDot.Size = new Size(16, 16);
            statusDot.Anchor = AnchorStyles.Right;
            statusDot.Margin = new Padding(0);
            statusDot.Paint += delegate(object s, PaintEventArgs pe)
            {
                Ui.PaintBackdrop(statusDot, pe.Graphics, statusDot.ClientRectangle);
                pe.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                BackdropArt.Glow(pe.Graphics, new PointF(8, 8), 8, statusDotColor, 150);
                using (SolidBrush b = new SolidBrush(statusDotColor))
                    pe.Graphics.FillEllipse(b, 4, 4, 8, 8);
            };
            status.Controls.Add(statusDot, 1, 0);

            scanStatus = new GlassLabel();
            scanStatus.Dock = DockStyle.Fill;
            scanStatus.Margin = new Padding(0);
            scanStatus.BackColor = Color.Transparent;
            scanStatus.ForeColor = Muted;
            scanStatus.Font = new Font("Segoe UI", 9F);
            scanStatus.TextAlign = ContentAlignment.MiddleLeft;
            scanStatus.AutoEllipsis = true;
            scanStatus.Text = "Ready";
            status.Controls.Add(scanStatus, 2, 0);

            lastCheck = new GlassLabel();
            lastCheck.AutoSize = true;
            lastCheck.Anchor = AnchorStyles.Right;
            lastCheck.Margin = new Padding(12, 0, 0, 0);
            lastCheck.BackColor = Color.Transparent;
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
                // In History the Deadline column sorts by when the giveaway ended: newest first.
                sortAscending = !(showingHistory && c.Name == "Deadline");
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
            Ui.Pink, Ui.Cyan, Ui.Lime, Ui.Gold, Ui.Hot, Color.FromArgb(150, 120, 255)
        };

        private Color TicketColor(GiveawayItem item)
        {
            long remaining, total;
            if (item != null && string.Equals(item.Status, "active", StringComparison.OrdinalIgnoreCase) &&
                TryTicketNumbers(item.Ticket, out remaining, out total) && total > 0)
                return remaining < 100 ? PriorityRed : (remaining < 250 ? PriorityYellow : PriorityGreen);
            return item != null && string.Equals(item.Status, "ended", StringComparison.OrdinalIgnoreCase) ? Ui.Muted : TextColor;
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
                Ui.PaintBackdrop(grid, g, cb);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(236, 14, 12, 32))) g.FillRectangle(b, cb);
                bool sorted = string.Equals(name, sortColumn, StringComparison.OrdinalIgnoreCase);
                if (sorted)
                    using (LinearGradientBrush b = new LinearGradientBrush(cb, Color.FromArgb(60, Ui.Hot), Color.FromArgb(0, Ui.Hot), LinearGradientMode.Vertical))
                        g.FillRectangle(b, cb);
                using (Pen p = new Pen(sorted ? Ui.Hot : Ui.Line, sorted ? 2F : 1F)) g.DrawLine(p, cb.Left, cb.Bottom - 1, cb.Right, cb.Bottom - 1);
                string header = Convert.ToString(e.Value) ?? "";
                // Room for the sort arrow only on the sorted column.
                Rectangle headerRect = new Rectangle(cb.X + 16, cb.Y, Math.Max(1, cb.Width - (sorted ? 34 : 20)), cb.Height);
                TextRenderer.DrawText(g, header, headerFont, headerRect, sorted ? Color.White : Muted, Ui.Single);
                if (sorted && header.Length > 0)
                {
                    int tw = TextRenderer.MeasureText(header, headerFont, new Size(1000, 40), TextFormatFlags.NoPadding).Width;
                    int ax = Math.Min(cb.Right - 16, cb.X + 16 + tw + 10), ay = cb.Y + cb.Height / 2;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    Point[] tri = sortAscending
                        ? new Point[] { new Point(ax - 5, ay + 3), new Point(ax + 5, ay + 3), new Point(ax, ay - 3) }
                        : new Point[] { new Point(ax - 5, ay - 3), new Point(ax + 5, ay - 3), new Point(ax, ay + 3) };
                    using (SolidBrush b = new SolidBrush(Ui.Pink)) g.FillPolygon(b, tri);
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
            Color fore = ended ? Color.FromArgb(160, 156, 205) : TextColor;

            // The backdrop art under a dark glass layer, brighter for the hovered or selected row.
            Ui.PaintBackdrop(grid, g, cb);
            using (SolidBrush b = new SolidBrush(selected ? RowSelected : (rowHot ? RowHover : BufferedGrid.Glass))) g.FillRectangle(b, cb);
            if ((selected || rowHot) && e.ColumnIndex == 0)
                using (LinearGradientBrush bar = new LinearGradientBrush(new Rectangle(cb.X, cb.Y, 3, cb.Height), Ui.Pink, Ui.Hot, LinearGradientMode.Vertical))
                    g.FillRectangle(bar, cb.X, cb.Y + 6, 3, cb.Height - 13);
            using (Pen p = new Pen(Color.FromArgb(150, Ui.Line))) g.DrawLine(p, cb.Left, cb.Bottom - 1, cb.Right, cb.Bottom - 1);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            const int pad = 16;
            Rectangle text = new Rectangle(cb.X + pad, cb.Y, Math.Max(1, cb.Width - pad * 2), cb.Height - 1);

            switch (name)
            {
                case "Creator":
                    {
                        string initial = value.Length > 0 ? value.Substring(0, 1).ToUpperInvariant() : "?";
                        Color tone = AvatarColors[Math.Abs(value.ToLowerInvariant().GetHashCode()) % AvatarColors.Length];
                        if (ended) tone = Ui.Mix(tone, Ui.LineStrong, 0.6);
                        Rectangle av = new Rectangle(cb.X + pad, cb.Y + (cb.Height - 1 - 32) / 2, 32, 32);
                        using (GraphicsPath tile = Ui.ChamferPath(av, 8))
                        using (LinearGradientBrush b = new LinearGradientBrush(av, Ui.Mix(Surface2, tone, 0.55), Ui.Mix(Surface, tone, 0.18), 45F))
                        using (Pen p = new Pen(Color.FromArgb(200, tone)))
                        {
                            g.FillPath(b, tile);
                            g.DrawPath(p, tile);
                        }
                        TextRenderer.DrawText(g, initial, cellBold, av, Color.White,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                        Rectangle nameRect = new Rectangle(av.Right + 12, cb.Y, Math.Max(1, cb.Right - av.Right - 12 - 8), cb.Height - 1);
                        TextRenderer.DrawText(g, value, FitFont(g, value, nameRect.Width, cellBold, cellBoldSmall), nameRect, fore, Ui.Single);
                        break;
                    }
                case "Link":
                    {
                        Rectangle linkRect = new Rectangle(text.X, text.Y, Math.Max(1, text.Width - 30), text.Height);
                        bool onLink = rowHot && hoverColumn == e.ColumnIndex && !hoverOnCopy;
                        Font linkFont = FitFont(g, ShortUrl(value), linkRect.Width, cellFont, cellFontSmall, cellFontTiny);
                        TextRenderer.DrawText(g, ShortUrl(value), linkFont, linkRect, onLink ? Color.White : (ended ? Ui.Muted : LinkColor), Ui.Single);
                        if (onLink)
                        {
                            int lw = Math.Min(linkRect.Width, TextRenderer.MeasureText(ShortUrl(value), linkFont, new Size(1000, 40), TextFormatFlags.NoPadding).Width);
                            using (Pen under = new Pen(Color.FromArgb(180, Ui.Pink))) g.DrawLine(under, linkRect.X, cb.Y + cb.Height / 2 + 9, linkRect.X + lw, cb.Y + cb.Height / 2 + 9);
                        }
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
                        // Remaining tickets as a neon bar under the numbers.
                        Color tone = TicketColor(item);
                        Rectangle numbers = new Rectangle(text.X, cb.Y + 8, text.Width, 24);
                        TextRenderer.DrawText(g, value, numberFont, numbers, tone, Ui.Single);
                        int barX = text.X, barW = Math.Max(20, Math.Min(132, text.Width - 4));
                        int barY = cb.Y + 36;
                        using (GraphicsPath track = Ui.ChamferPath(new Rectangle(barX, barY, barW, 6), 3))
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(200, 10, 8, 26))) g.FillPath(b, track);
                        int fillW = (int)Math.Round(barW * Math.Min(1.0, (double)remaining / total));
                        if (fillW > 3)
                        {
                            Rectangle fillRect = new Rectangle(barX, barY, fillW, 6);
                            using (GraphicsPath fillPath = Ui.ChamferPath(fillRect, 3))
                            using (LinearGradientBrush b = new LinearGradientBrush(new Rectangle(barX, barY, Math.Max(2, fillW), 6), Ui.Mix(tone, Ui.Bg, 0.25), Ui.Mix(tone, Color.White, 0.25), LinearGradientMode.Horizontal))
                                g.FillPath(b, fillPath);
                            using (SolidBrush tip = new SolidBrush(Color.FromArgb(120, tone))) g.FillEllipse(tip, barX + fillW - 5, barY - 2, 10, 10);
                        }
                        break;
                    }
                case "PromoCode":
                    {
                        if (missing) { TextRenderer.DrawText(g, "–", cellFont, text, Ui.Muted, Ui.Single); break; }
                        int maxPill = Math.Max(40, cb.Width - pad - 52);
                        Font codeFont = FitFont(g, value, maxPill - 22, monoFont, monoFontSmall);
                        int pillW = Math.Min(maxPill, TextRenderer.MeasureText(g, value, codeFont, new Size(1000, 40), TextFormatFlags.NoPadding).Width + 22);
                        Rectangle pill = new Rectangle(cb.X + pad, cb.Y + (cb.Height - 1 - 28) / 2, pillW, 28);
                        Color chipEdge = ended ? Ui.LineStrong : Ui.Mix(Ui.LineStrong, Ui.Cyan, rowHot ? 0.8 : 0.45);
                        using (GraphicsPath path = Ui.ChamferPath(pill, 7))
                        using (SolidBrush b = new SolidBrush(rowHot ? Color.FromArgb(230, Surface3) : Color.FromArgb(230, 14, 12, 34)))
                        using (Pen p = new Pen(chipEdge))
                        {
                            g.FillPath(b, path);
                            g.DrawPath(p, path);
                        }
                        TextRenderer.DrawText(g, value, codeFont, new Rectangle(pill.X + 11, pill.Y, pill.Width - 14, pill.Height),
                            ended ? fore : Color.FromArgb(170, 245, 255), Ui.Single);
                        DrawCopyIcon(g, CopyButtonRect(cb), rowHot && hoverColumn == e.ColumnIndex && hoverOnCopy, rowHot);
                        break;
                    }
                case "PrizePool":
                    {
                        if (missing) { TextRenderer.DrawText(g, "–", cellFont, text, Ui.Muted, Ui.Single); break; }
                        // A trophy, then the total prize value.
                        Icons.DrawCentered(g, Icons.Trophy, text.X + 11, cb.Y + (cb.Height - 1) / 2f, 22);
                        TextRenderer.DrawText(g, value, numberFont, new Rectangle(text.X + 28, text.Y, Math.Max(1, text.Width - 28), text.Height),
                            ended ? Ui.Muted : Color.FromArgb(255, 150, 226), Ui.Single);
                        break;
                    }
                case "MinimumDeposit":
                    {
                        if (missing) { TextRenderer.DrawText(g, "–", cellFont, text, Ui.Muted, Ui.Single); break; }
                        // A small gold coin, then the amount in gold.
                        Color gold = ended ? Ui.Mix(Ui.Gold, Ui.Muted, 0.6) : Ui.Gold;
                        Icons.DrawCentered(g, Icons.Coin, text.X + 11, cb.Y + (cb.Height - 1) / 2f, 22);
                        TextRenderer.DrawText(g, value, numberFont, new Rectangle(text.X + 28, text.Y, Math.Max(1, text.Width - 28), text.Height), gold, Ui.Single);
                        break;
                    }
                case "Deadline":
                    {
                        if (missing) { TextRenderer.DrawText(g, "\u2013", cellFont, text, Ui.Muted, Ui.Single); break; }
                        Color tone = fore;
                        string left = null;
                        DateTime due;
                        if (!ended && item != null && TryDeadlineDate(item, out due))
                        {
                            double days = (due.Date - DateTime.Now.Date).TotalDays;
                            tone = days <= 2 ? PriorityRed : (days <= 7 ? PriorityYellow : PriorityGreen);
                            left = days < 0 ? "ending" : days == 0 ? "ends today" : days == 1 ? "ends tomorrow" : "in " + days + " days";
                        }
                        else if (ended) tone = Ui.Muted;
                        if (left == null) { TextRenderer.DrawText(g, value.ToUpperInvariant(), numberFont, text, tone, Ui.Single); break; }
                        TextRenderer.DrawText(g, value.ToUpperInvariant(), numberFont, new Rectangle(text.X, cb.Y + 8, text.Width, 22), tone, Ui.Single);
                        TextRenderer.DrawText(g, left, smallFont, new Rectangle(text.X, cb.Y + 30, text.Width, 18), Ui.Mix(tone, Ui.Muted, 0.45), Ui.Single);
                        break;
                    }
                case "JoinedAction":
                    {
                        Color tone = showingJoined ? Danger : Success;
                        bool hot = rowHot && hoverColumn == e.ColumnIndex;
                        int size = 32;
                        Rectangle btn = new Rectangle(cb.X + (cb.Width - size) / 2, cb.Y + (cb.Height - 1 - size) / 2, size, size);
                        if (hot) BackdropArt.Glow(g, new PointF(btn.X + size / 2f, btn.Y + size / 2f), 26, tone, 90);
                        using (GraphicsPath tile = Ui.ChamferPath(btn, 8))
                        using (SolidBrush b = new SolidBrush(Ui.Mix(Surface, tone, hot ? 0.40 : 0.16)))
                        using (Pen p = new Pen(Ui.Mix(Surface2, tone, hot ? 0.95 : 0.55), 1.3F))
                        {
                            g.FillPath(b, tile);
                            g.DrawPath(p, tile);
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
                    // Only ever open web links: a hand-edited data.json must not be able to launch a program.
                    Uri link;
                    if (!Uri.TryCreate(url, UriKind.Absolute, out link) || (link.Scheme != Uri.UriSchemeHttp && link.Scheme != Uri.UriSchemeHttps))
                    { scanStatus.Text = "Not a web link, not opened"; return; }
                    try { Process.Start(link.AbsoluteUri); } catch (Exception swallowed) { Diag.Swallow("Opening " + url, swallowed); scanStatus.Text = "Could not open the link in your browser"; }
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
                catch (Exception swallowed) { Diag.Swallow("Resetting status text", swallowed); }
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
            catch (Exception swallowed) { Diag.Swallow("Removing copy toast", swallowed); }

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
                    catch (Exception swallowed) { Diag.Swallow("Clearing copy toast", swallowed); }
                };
                Controls.Add(toast);
                toast.BringToFront();
                toast.Start();
            }
            catch (Exception swallowed) { Diag.Swallow("Showing copy toast", swallowed); }
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
                if (showingHistory && AppSettings.Current.HistoryLatestOnly) list = GiveawayInfo.LatestPerCreator(list);
            }

            string query = filterBox == null ? "" : (filterBox.Text ?? "").Trim();
            if (query.Length > 0)
            {
                string field = filterFieldBox == null || filterFieldBox.SelectedItem == null
                    ? "Creator name"
                    : filterFieldBox.SelectedItem.ToString();

                // Pick the field once, not once per row.
                Func<GiveawayItem, string> selector = null;
                if (string.Equals(field, "Creator name", StringComparison.OrdinalIgnoreCase)) selector = delegate(GiveawayItem i) { return i.Creator; };
                else if (string.Equals(field, "URL", StringComparison.OrdinalIgnoreCase)) selector = delegate(GiveawayItem i) { return i.Url; };
                else if (string.Equals(field, "Ticket", StringComparison.OrdinalIgnoreCase)) selector = delegate(GiveawayItem i) { return i.Ticket; };
                else if (string.Equals(field, "Promocode", StringComparison.OrdinalIgnoreCase)) selector = delegate(GiveawayItem i) { return i.PromoCode; };
                else if (string.Equals(field, "Minimum deposit", StringComparison.OrdinalIgnoreCase)) selector = delegate(GiveawayItem i) { return i.MinimumDeposit; };
                else if (string.Equals(field, "Prize pool", StringComparison.OrdinalIgnoreCase)) selector = delegate(GiveawayItem i) { return i.PrizePool; };
                else if (string.Equals(field, "Deadline", StringComparison.OrdinalIgnoreCase)) selector = delegate(GiveawayItem i) { return i.Deadline; };

                list.RemoveAll(delegate(GiveawayItem i)
                {
                    if (selector != null) return (selector(i) ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0;
                    return !((i.Creator ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                             (i.Url ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                             (i.Ticket ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                             (i.PromoCode ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                             (i.MinimumDeposit ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                             (i.PrizePool ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                             (i.Deadline ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
                });
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
            else if (string.Equals(sortColumn, "Deadline", StringComparison.OrdinalIgnoreCase) && showingHistory)
            {
                // Every History row says "Ended"; order by when it ended (when the app saw it end),
                // then by the giveaway's own date.
                cmp = GiveawayInfo.Recency(a).CompareTo(GiveawayInfo.Recency(b));
                if (cmp == 0) cmp = GiveawayInfo.GiveawayDate(a.Url).CompareTo(GiveawayInfo.GiveawayDate(b.Url));
            }
            else if (string.Equals(sortColumn, "Deadline", StringComparison.OrdinalIgnoreCase))
            {
                DateTime ad, bd;
                bool ah = TryDeadlineDate(a, out ad);
                bool bh = TryDeadlineDate(b, out bd);
                if (ah != bh) return ah ? -1 : 1;
                if (ah && bh) cmp = ad.CompareTo(bd);
            }
            else if (string.Equals(sortColumn, "PrizePool", StringComparison.OrdinalIgnoreCase))
            {
                // By amount (500$ before 1,000$); giveaways without a known prize pool stay at the bottom.
                decimal av, bv;
                bool ah = Scanner.TryUsdValue(a.PrizePool, out av);
                bool bh = Scanner.TryUsdValue(b.PrizePool, out bv);
                if (ah != bh) return ah ? -1 : 1;
                if (ah && bh) cmp = av.CompareTo(bv);
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
            cmp = sortAscending ? cmp : -cmp;
            // Same value in the sorted column: show a creator's newest giveaway first.
            if (cmp == 0) cmp = GiveawayInfo.GiveawayDate(b.Url).CompareTo(GiveawayInfo.GiveawayDate(a.Url));
            return cmp;
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

        private void Render()
        {
            RenderView(true);
        }

        // Save the sort of the tab being left and restore the sort of the tab being opened.
        private void SwitchSortForView()
        {
            string view = showingHistory ? "history" : (showingJoined ? "joined" : "active");
            if (view == currentSortView) return;
            sortByView[currentSortView] = new KeyValuePair<string, bool>(sortColumn, sortAscending);
            KeyValuePair<string, bool> next = sortByView[view];
            sortColumn = next.Key;
            sortAscending = next.Value;
            currentSortView = view;
        }

        // full=false is used while typing in the search box: only the table rows change,
        // so the header counts, theme colours and panel visibility are left alone.
        private void RenderView(bool full)
        {
            if (IsDisposed || Disposing) return;
            filterTimer.Stop();
            if (!showingLogs) SwitchSortForView();
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
            List<GiveawayItem> endedItems = data.Items.Where(delegate(GiveawayItem i) { return string.Equals(i.Status, "ended", StringComparison.OrdinalIgnoreCase) && !i.Joined; }).ToList();
            int hc = AppSettings.Current.HistoryLatestOnly ? GiveawayInfo.LatestPerCreator(endedItems).Count : endedItems.Count;
            int jc = data.Items.Count(delegate(GiveawayItem i) { return i.Joined; });
            lastCheck.Text = "Last checked: " + FormatTime(data.LastScan);

            SetTabText(activeButton, "Active", ac);
            SetTabText(historyButton, "History", hc);
            SetTabText(joinedButton, "Joined", jc);
            UpdateStats(ac, jc);

            if (showingJoined)
            {
                viewTitle.Text = "JOINED GIVEAWAYS";
                viewSubtitle.Text = "Saved by you  \u00b7  use \u00d7 to move an item back to Active or History";
            }
            else if (showingHistory)
            {
                viewTitle.Text = "GIVEAWAY HISTORY";
                viewSubtitle.Text = AppSettings.Current.HistoryLatestOnly
                    ? "Most recently ended first  \u00b7  each creator's latest giveaway  \u00b7  older ones stay saved (see Settings)"
                    : "The " + AppSettings.Current.HistoryLimit + " most recent ended giveaways  \u00b7  older ones are removed automatically";
            }
            else
            {
                viewTitle.Text = "ACTIVE GIVEAWAYS";
                viewSubtitle.Text = "Running and not joined yet  \u00b7  click a column header to sort";
            }

            ApplyViewTheme();
            if (showingLogs)
            {
                viewTitle.Text = "ACTIVITY LOGS";
                viewTitle.ForeColor = TextColor;
                logsPanel.BringToFront();
                UpdateLogs();
                return;
            }
            RebuildRows();
        }

        // The four tiles above the table: running giveaways, those ending within 48 hours, joined ones,
        // and the lowest minimum deposit among running giveaways.
        private void UpdateStats(int activeCount, int joinedCount)
        {
            if (activeStat == null) return;
            DateTime today = DateTime.Now.Date;
            List<GiveawayItem> running = data.Items.Where(delegate(GiveawayItem i)
            {
                return !i.Joined && string.Equals(i.Status, "active", StringComparison.OrdinalIgnoreCase);
            }).ToList();

            int tickets = 0;
            foreach (GiveawayItem i in running)
            {
                long remaining, total;
                if (TryTicketNumbers(i.Ticket, out remaining, out total)) tickets += (int)Math.Min(int.MaxValue - tickets, remaining);
            }
            activeStat.SetValue(activeCount.ToString(), tickets > 0 ? tickets.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " tickets left to grab" : "Deep Search finds new ones");

            List<GiveawayItem> ending = running.Where(delegate(GiveawayItem i)
            {
                DateTime when;
                return DateTime.TryParse(i.Deadline, out when) && (when.Date - today).TotalDays <= 2;
            }).ToList();
            endingStat.SetValue(ending.Count.ToString(), ending.Count == 0 ? "Nothing about to close"
                : string.Join(", ", ending.Take(3).Select(delegate(GiveawayItem i) { return i.Creator; }).ToArray()) + (ending.Count > 3 ? ", ..." : ""));

            int joinedRunning = data.Items.Count(delegate(GiveawayItem i) { return i.Joined && string.Equals(i.Status, "active", StringComparison.OrdinalIgnoreCase); });
            joinedStat.SetValue(joinedCount.ToString(), joinedCount == 0 ? "Press + on a giveaway to save it" : joinedRunning + " still running");

            decimal best = decimal.MaxValue;
            int atBest = 0;
            foreach (GiveawayItem i in running)
            {
                decimal amount;
                if (!TryMinimumDepositValue(i.MinimumDeposit, out amount)) continue;
                if (amount < best) { best = amount; atBest = 1; }
                else if (amount == best) atBest++;
            }
            if (atBest == 0) cheapestStat.SetValue("–", "No deposit known yet");
            else cheapestStat.SetValue(Scanner.UsdAmount(best.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                atBest == 1 ? "1 giveaway at this deposit" : atBest + " giveaways at this deposit");
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
                string prize = string.IsNullOrWhiteSpace(i.PrizePool) ? "-" : i.PrizePool;
                gridRow.CreateCells(grid, i.Creator ?? "Unknown", i.Url ?? "", prize, i.Ticket ?? "-", promoCode, minimumDeposit, deadline, "");
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
                // Say what is happening, not just a process count: most of a scan is web requests, and a
                // browser only opens for the few pages that need one, so "0 browsers" is normal in between.
                int total = Scanner.ChecksTotal;
                bool working = scanning || Scanner.ActiveChecks > 0;
                List<string> parts = new List<string>();
                parts.Add(working && Scanner.Phase != "Idle" ? Scanner.Phase : (working ? "Working" : "Idle"));
                if (total > 0 && working)
                    parts.Add("Pages " + Scanner.ChecksDone + "/" + total + " (" + Scanner.ActiveChecks + " running)");
                if (working && Scanner.SourcesRead > 0 && Scanner.Phase.StartsWith("Deep Search"))
                    parts.Add("Sources read " + Scanner.SourcesRead);
                parts.Add("Browsers " + BrowserProcessManager.RunningBrowsers + "/" + BrowserProcessManager.MaxConcurrent +
                    " (" + BrowserProcessManager.ActiveProcessCount + " processes)");
                if (Scanner.QueuedRenders > 0) parts.Add("Waiting for a browser " + Scanner.QueuedRenders);
                viewSubtitle.Text = string.Join("  \u00b7  ", parts.ToArray());
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
            // A direct field copy: no JSON round-trip, so no temporary serialized string.
            AppData clone = new AppData();
            if (source == null) return clone;
            clone.LastScan = source.LastScan;
            clone.LastDeepScan = source.LastDeepScan;
            if (source.Items != null)
            {
                foreach (GiveawayItem i in source.Items)
                {
                    if (i == null) continue;
                    clone.Items.Add(new GiveawayItem
                    {
                        Creator = i.Creator, Url = i.Url, Status = i.Status, Ticket = i.Ticket, PromoCode = i.PromoCode,
                        MinimumDeposit = i.MinimumDeposit, PrizePool = i.PrizePool, Deadline = i.Deadline, LastChecked = i.LastChecked, Source = i.Source,
                        Error = i.Error, Joined = i.Joined, JoinedAt = i.JoinedAt, HistorySince = i.HistorySince, RenderedAt = i.RenderedAt
                    });
                }
            }
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
            SetScanning(true, manual ? "Checking tickets and deadlines..." : "Refreshing live status...");
            try
            {
                // Some giveaway pages require a headless Chromium/CDP fallback.
                // Run the entire scan away from the WinForms UI thread so Chromium
                // startup/parsing can never make Windows show the spinning busy cursor.
                HashSet<string> before = ActiveKeys();
                bool firstScan = string.IsNullOrEmpty(data.LastScan);
                // The Refresh button is a quick status check: tickets, deadline and ended-or-not straight
                // from each saved page, without opening a browser. Automatic refreshes keep their schedule.
                Scanner.QuickRefresh = manual;
                AppData scanData = CloneDataForScan(data);
                AppData refreshed = await Task.Run(async delegate
                {
                    return await Scanner.RefreshSavedAsync(scanData);
                });
                if (IsDisposed || Disposing) return;
                // Keep what was changed while the scan ran (a Join click, an added link).
                Scanner.ApplyScanResults(data, refreshed);
                DataStore.Save(data);
                Render();
                if (!manual && !firstScan) NotifyChanges(before);
            }
            catch (Exception ex)
            {
                ActivityLog.Write("ERROR", "Refresh failed: " + ex.Message);
                if (!IsDisposed && !Disposing) MessageBox.Show(this, ex.Message, "Refresh failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { Scanner.QuickRefresh = false; SetScanning(false, ""); ReleaseScanMemory(); }
        }

        // Hand the memory used by downloaded pages back to Windows once a scan is over.
        private static void ReleaseScanMemory()
        {
            Task.Run(new Action(MemoryTrim.Run));
        }

        // manual = the user pressed Deep Search. An automatic one (from the timer) uses at most two
        // browsers, re-reads deadlines only on their normal schedule, shows no error dialogs and sends
        // the tray notification for any new giveaway it finds.
        private async Task DeepSearchAsync(bool manual)
        {
            if (scanning) return;
            SetScanning(true, manual
                ? "Deep searching partners, YouTube, Telegram and social sources..."
                : "Searching for new giveaways (automatic Deep Search)...");
            string doneText = null;
            Color doneColor = Success;
            int doneMs = 2200;
            try
            {
                HashSet<string> before = ActiveKeys();
                bool firstScan = string.IsNullOrEmpty(data.LastScan);
                Scanner.RecheckDeadlines = manual;
                AppData scanData = CloneDataForScan(data);
                AppData scanned = await Task.Run(async delegate
                {
                    return await Scanner.DeepScanAsync(scanData);
                });
                if (IsDisposed || Disposing) return;
                // Keep what was changed while the scan ran (a Join click, an added link).
                Scanner.ApplyScanResults(data, scanned);
                DataStore.Save(data);
                Render();
                if (!manual && !firstScan) NotifyChanges(before);
                int ac =data.Items.Count(delegate(GiveawayItem i) { return string.Equals(i.Status, "active", StringComparison.OrdinalIgnoreCase) && !i.Joined; });
                // Never present a scan as complete if some pages or sources could not be read.
                int unreachable = Scanner.LastUnreachable;
                int sourceFailures = Scanner.LastDiscoveryFailures;
                bool incomplete = unreachable > 0 || sourceFailures > 0;
                doneText = "Deep Search complete  •  " + ac + " active giveaway" + (ac == 1 ? "" : "s") +
                    (unreachable > 0 ? "  •  " + unreachable + " page" + (unreachable == 1 ? "" : "s") + " could not be reached" : "") +
                    (sourceFailures > 0 ? "  •  " + sourceFailures + " source" + (sourceFailures == 1 ? "" : "s") + " unreadable" : "") +
                    (incomplete ? " (see Logs, run again)" : "");
                doneColor = incomplete ? Warning : Success;
                doneMs = incomplete ? 10000 : 2200;
            }
            catch (Exception ex)
            {
                ActivityLog.Write("ERROR", (manual ? "Deep Search" : "Automatic Deep Search") + " failed: " + ex.Message);
                if (manual && !IsDisposed && !Disposing) MessageBox.Show(this, ex.Message, "Deep Search failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { Scanner.RecheckDeadlines = false; SetScanning(false, ""); ReleaseScanMemory(); }

            // Show the result after the buttons are usable again, instead of keeping them disabled for it.
            if (doneText != null && !IsDisposed && !Disposing)
            {
                scanStatus.Text = doneText;
                scanStatus.ForeColor = doneColor;
                await Task.Delay(doneMs);
                if (!scanning && !IsDisposed && !Disposing) { scanStatus.Text = "Ready"; scanStatus.ForeColor = Muted; }
            }
        }

        private async Task AddLinkAsync()
        {
            if (scanning) return;
            using (AddLinkForm f = new AddLinkForm())
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                if (scanning) return;
                List<string> rejected;
                List<string> links = Scanner.ParseLinkList(f.LinksText, out rejected);
                if (links.Count == 0)
                {
                    if (rejected.Count > 0)
                        MessageBox.Show(this, "No giveaway links were found. Use direct dated links, for example https://creator.club/011126/",
                            "Invalid link", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                SetScanning(true, links.Count == 1 ? "Checking 1 giveaway link..." : "Checking " + links.Count + " giveaway links...");
                try
                {
                    GiveawayItem[] items = await Task.Run(async delegate
                    {
                        return await Scanner.ValidateManyAsync(links);
                    });
                    if (IsDisposed || Disposing) return;
                    Scanner.MergeMany(data, items);
                    Render();
                    int active = items.Count(delegate(GiveawayItem i) { return i.Status == "active"; });
                    int ended = items.Count(delegate(GiveawayItem i) { return i.Status == "ended"; });
                    int other = items.Length - active - ended;
                    List<string> lines = new List<string>();
                    if (active > 0) lines.Add(active + " running giveaway" + (active == 1 ? "" : "s") + " added to Active.");
                    if (ended > 0) lines.Add(ended + " ended giveaway" + (ended == 1 ? "" : "s") + " saved in History.");
                    if (other > 0) lines.Add(other + " page" + (other == 1 ? " was" : "s were") + " saved but could not be checked yet: " +
                        string.Join(", ", items.Where(delegate(GiveawayItem i) { return i.Status != "active" && i.Status != "ended"; })
                            .Take(5).Select(delegate(GiveawayItem i) { return ShortUrl(i.Url); }).ToArray()) + (other > 5 ? ", ..." : ""));
                    if (rejected.Count > 0) lines.Add(rejected.Count + " piece" + (rejected.Count == 1 ? " was" : "s were") + " skipped because " +
                        (rejected.Count == 1 ? "it is" : "they are") + " not a giveaway link: " + string.Join(", ", rejected.Take(3).ToArray()) + (rejected.Count > 3 ? ", ..." : ""));
                    MessageBox.Show(this, string.Join(Environment.NewLine + Environment.NewLine, lines.ToArray()),
                        links.Count == 1 ? "Link added" : "Links added", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
}

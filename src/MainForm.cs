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
            ActivityLog.Write("APP", "Ready. Browser limits: " + BrowserProcessManager.MaxProcesses + " processes, " + AppSettings.Current.BrowserMemoryLimitMB + " MB; one rendered page at a time; " + AppSettings.Current.RenderTimeoutSeconds + "-second page timeout.");
            logTimer.Interval = 500;
            logTimer.Tick += delegate { UpdateLogs(); };
            logTimer.Start();
            filterTimer.Interval = 120;
            filterTimer.Tick += delegate { filterTimer.Stop(); RenderView(false); };
            Render();

            if (automaticRefresh) Shown += async delegate { await RefreshAsync(false); };
            autoTimer = new System.Windows.Forms.Timer();
            autoTimer.Interval = AppSettings.Current.RefreshMinutes * 60 * 1000;
            autoTimer.Tick += async delegate { if (!scanning) await RefreshAsync(false); };
            if (automaticRefresh)
            {
                autoTimer.Start();
                BuildTray();
            }
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
            try { logo.Image = Icon.ExtractAssociatedIcon(Application.ExecutablePath).ToBitmap(); } catch (Exception swallowed) { Diag.Swallow("Loading header logo", swallowed); }
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

            settingsButton = MakeHeaderButton("Settings", 92, false);
            addButton = MakeHeaderButton("Add link", 96, false);
            refreshButton = MakeHeaderButton("Refresh", 92, false);
            deepButton = MakeHeaderButton("Deep Search", 124, true);
            settingsButton.Margin = addButton.Margin = refreshButton.Margin = deepButton.Margin = new Padding(8, 0, 0, 0);
            actions.Controls.Add(settingsButton);
            settingsButton.Click += delegate { OpenSettings(); };
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
                catch (Exception swallowed) { Diag.Swallow("Copying logs to the clipboard", swallowed); scanStatus.Text = "Clipboard is busy - try again"; }
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
                    try { Process.Start(url); } catch (Exception swallowed) { Diag.Swallow("Opening " + url, swallowed); scanStatus.Text = "Could not open the link in your browser"; }
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
                else if (string.Equals(field, "Deadline", StringComparison.OrdinalIgnoreCase)) selector = delegate(GiveawayItem i) { return i.Deadline; };

                list.RemoveAll(delegate(GiveawayItem i)
                {
                    if (selector != null) return (selector(i) ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0;
                    return !((i.Creator ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                             (i.Url ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                             (i.Ticket ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                             (i.PromoCode ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                             (i.MinimumDeposit ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
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
            List<GiveawayItem> endedItems = data.Items.Where(delegate(GiveawayItem i) { return string.Equals(i.Status, "ended", StringComparison.OrdinalIgnoreCase) && !i.Joined; }).ToList();
            int hc = AppSettings.Current.HistoryLatestOnly ? GiveawayInfo.LatestPerCreator(endedItems).Count : endedItems.Count;
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
                viewSubtitle.Text = AppSettings.Current.HistoryLatestOnly
                    ? "Each creator's latest ended giveaway  \u00b7  older ones stay saved (see Settings)"
                    : "The " + AppSettings.Current.HistoryLimit + " most recent ended giveaways  \u00b7  older ones are removed automatically";
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
                int total = Scanner.ChecksTotal;
                string checks = total > 0 && (scanning || Scanner.ActiveChecks > 0)
                    ? "Page checks " + Scanner.ChecksDone + "/" + total + " (" + Scanner.ActiveChecks + " running)"
                    : "Page checks idle";
                viewSubtitle.Text = "Browser processes " + BrowserProcessManager.ActiveProcessCount + "/" + BrowserProcessManager.MaxProcesses + "  \u00b7  Queued pages " + Scanner.QueuedRenders +
                    "  \u00b7  " + checks + "  \u00b7  Latest 2,000 events";
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
                        MinimumDeposit = i.MinimumDeposit, Deadline = i.Deadline, LastChecked = i.LastChecked, Source = i.Source,
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
            SetScanning(true, manual ? "Checking saved giveaway pages..." : "Refreshing live status...");
            try
            {
                // Some giveaway pages require a headless Chromium/CDP fallback.
                // Run the entire scan away from the WinForms UI thread so Chromium
                // startup/parsing can never make Windows show the spinning busy cursor.
                HashSet<string> before = ActiveKeys();
                bool firstScan = string.IsNullOrEmpty(data.LastScan);
                AppData scanData = CloneDataForScan(data);
                AppData refreshed = await Task.Run(async delegate
                {
                    return await Scanner.RefreshSavedAsync(scanData);
                });
                if (IsDisposed || Disposing) return;
                data = refreshed;
                Render();
                if (!manual && !firstScan) NotifyChanges(before);
            }
            catch (Exception ex)
            {
                ActivityLog.Write("ERROR", "Refresh failed: " + ex.Message);
                if (!IsDisposed && !Disposing) MessageBox.Show(this, ex.Message, "Refresh failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { SetScanning(false, ""); ReleaseScanMemory(); }
        }

        // Hand the memory used by downloaded pages back to Windows once a scan is over.
        private static void ReleaseScanMemory()
        {
            Task.Run(new Action(MemoryTrim.Run));
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
            finally { SetScanning(false, ""); ReleaseScanMemory(); }
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
}

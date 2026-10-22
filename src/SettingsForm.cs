using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace SkinClubGiveawayDesktop
{
    // Edits the common options. Advanced values (timeouts, extra creators) live in settings.json,
    // which this dialog can open in the default editor.
    public class SettingsForm : Form
    {
        private readonly NumericUpDown refreshMinutes = MakeNumber(1, 1440);
        private readonly NumericUpDown historyLimit = MakeNumber(5, 500);
        private readonly CheckBox notifications = new CheckBox();
        private readonly CheckBox latestOnly = new CheckBox();
        private readonly TextField browserField = new TextField(false);

        public SettingsForm()
        {
            Text = "Settings";
            ClientSize = new Size(560, 494);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Ui.Surface;
            ForeColor = Ui.Text;
            Font = new Font("Segoe UI", 9F);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception swallowed) { Diag.Swallow("Loading window icon", swallowed); }

            AppSettings s = AppSettings.Current;

            Label title = new Label();
            title.Text = "Settings";
            title.SetBounds(28, 24, 504, 32);
            title.Font = new Font("Segoe UI Semibold", 15F);
            Controls.Add(title);

            Controls.Add(MakeLabel("CHECK FOR NEW GIVEAWAYS EVERY (MINUTES)", 72));
            refreshMinutes.Value = s.RefreshMinutes;
            refreshMinutes.SetBounds(28, 94, 120, 28);
            Controls.Add(refreshMinutes);

            Controls.Add(MakeLabel("HISTORY: ENDED GIVEAWAYS TO KEEP", 138));
            historyLimit.Value = s.HistoryLimit;
            historyLimit.SetBounds(28, 160, 120, 28);
            Controls.Add(historyLimit);

            notifications.Text = "Show a Windows notification when a new giveaway appears or one ends today";
            notifications.Checked = s.Notifications;
            notifications.AutoSize = true;
            notifications.Location = new Point(28, 206);
            notifications.ForeColor = Ui.Text;
            Controls.Add(notifications);

            latestOnly.Text = "History shows only each creator's latest giveaway (older ones stay saved)";
            latestOnly.Checked = s.HistoryLatestOnly;
            latestOnly.AutoSize = true;
            latestOnly.Location = new Point(28, 230);
            latestOnly.ForeColor = Ui.Text;
            Controls.Add(latestOnly);

            Controls.Add(MakeLabel("BROWSER USED FOR PAGE DETAILS  (BLANK = AUTOMATIC EDGE / CHROME / BRAVE)", 266));
            browserField.BackColor = Ui.Surface;
            browserField.SetBounds(28, 288, 404, 38);
            browserField.Placeholder = "C:\\Program Files\\...\\msedge.exe";
            browserField.Input.Text = s.BrowserPath ?? "";
            Controls.Add(browserField);

            UiButton browse = UiButton.Create("Browse", UiButtonKind.Ghost, 88);
            browse.Location = new Point(444, 288);
            browse.BackColor = Ui.Surface;
            browse.Click += delegate
            {
                using (OpenFileDialog dialog = new OpenFileDialog())
                {
                    dialog.Filter = "Browser (*.exe)|*.exe";
                    if (dialog.ShowDialog(this) == DialogResult.OK) browserField.Input.Text = dialog.FileName;
                }
            };
            Controls.Add(browse);

            UiButton openFile = UiButton.Create("Open settings.json", UiButtonKind.Ghost, 160);
            openFile.Location = new Point(28, 342);
            openFile.BackColor = Ui.Surface;
            openFile.Click += delegate
            {
                try
                {
                    AppSettings.EnsureFileExists();
                    Process.Start(new ProcessStartInfo(AppSettings.SettingsFile) { UseShellExecute = true });
                }
                catch (Exception ex) { Diag.Swallow("Opening settings.json", ex); MessageBox.Show(this, ex.Message, "Cannot open settings.json"); }
            };
            Controls.Add(openFile);

            // Wraps onto two lines and has its own row, clear of the buttons below.
            Label hint = new Label();
            hint.AutoSize = false;
            hint.Text = "settings.json also lets you add creators (ExtraCreators) and tune timeouts and limits. Restart the app after editing it.";
            hint.SetBounds(28, 390, 504, 38);
            hint.ForeColor = Ui.Muted;
            Controls.Add(hint);

            UiButton cancel = UiButton.Create("Cancel", UiButtonKind.Ghost, 96);
            cancel.Location = new Point(28 + 504 - 96 - 12 - 96, 442);
            cancel.DialogResult = DialogResult.Cancel;
            UiButton save = UiButton.Create("Save", UiButtonKind.Primary, 96);
            save.Location = new Point(28 + 504 - 96, 442);
            save.DialogResult = DialogResult.OK;
            cancel.BackColor = save.BackColor = Ui.Surface;
            Controls.Add(cancel);
            Controls.Add(save);
            AcceptButton = save;
            CancelButton = cancel;
        }

        // Applies and saves the dialog's values. Called only when the user pressed Save.
        public void Apply()
        {
            AppSettings s = AppSettings.Current;
            s.RefreshMinutes = (int)refreshMinutes.Value;
            s.HistoryLimit = (int)historyLimit.Value;
            s.Notifications = notifications.Checked;
            s.HistoryLatestOnly = latestOnly.Checked;
            s.BrowserPath = browserField.Input.Text.Trim();
            s.Save();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Ui.DarkTitleBar(Handle);
        }

        private static NumericUpDown MakeNumber(int min, int max)
        {
            NumericUpDown n = new NumericUpDown();
            n.Minimum = min;
            n.Maximum = max;
            n.BackColor = Ui.Surface2;
            n.ForeColor = Ui.Text;
            n.BorderStyle = BorderStyle.FixedSingle;
            return n;
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
}

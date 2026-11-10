using System;
using System.Drawing;
using System.Windows.Forms;

namespace SkinClubGiveawayDesktop
{
    // Edits the options a user may want to change. The browser used for page details is chosen
    // automatically (Edge first), and new creators are found by Deep Search, so neither is set here.
    public class SettingsForm : ThemedForm
    {
        protected override bool HeroArt { get { return false; } }
        private readonly NumericUpDown refreshMinutes = MakeNumber(1, 1440);
        private readonly NumericUpDown deepSearchMinutes = MakeNumber(0, 1440);
        private readonly NumericUpDown historyLimit = MakeNumber(5, 500);
        private readonly CheckBox notifications = new CheckBox();
        private readonly CheckBox latestOnly = new CheckBox();

        public SettingsForm()
        {
            Text = "Settings";
            ClientSize = new Size(560, 392);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9F);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Icon = Ui.AppIcon;

            AppSettings s = AppSettings.Current;

            Label title = new Label();
            title.Text = "SETTINGS";
            title.SetBounds(28, 22, 504, 34);
            title.Font = Ui.DisplayFont(17F);
            title.ForeColor = Ui.Text;
            title.BackColor = Color.Transparent;
            Controls.Add(title);

            Controls.Add(MakeLabel("RECHECK SAVED GIVEAWAYS EVERY (MINUTES)", 72));
            refreshMinutes.Value = s.RefreshMinutes;
            refreshMinutes.SetBounds(28, 94, 120, 28);
            Controls.Add(refreshMinutes);

            Controls.Add(MakeLabel("SEARCH FOR NEW GIVEAWAYS EVERY (MINUTES, 15 OR MORE, 0 = OFF)", 138));
            deepSearchMinutes.Value = s.AutoDeepSearchMinutes;
            deepSearchMinutes.SetBounds(28, 160, 120, 28);
            Controls.Add(deepSearchMinutes);

            Controls.Add(MakeLabel("HISTORY: ENDED GIVEAWAYS TO KEEP", 204));
            historyLimit.Value = s.HistoryLimit;
            historyLimit.SetBounds(28, 226, 120, 28);
            Controls.Add(historyLimit);

            notifications.Text = "Show a Windows notification when a new giveaway appears or one ends today";
            notifications.Checked = s.Notifications;
            notifications.AutoSize = true;
            notifications.Location = new Point(28, 272);
            notifications.ForeColor = Ui.Text;
            notifications.BackColor = Color.Transparent;
            Controls.Add(notifications);

            latestOnly.Text = "History shows only each creator's latest giveaway (older ones stay saved)";
            latestOnly.Checked = s.HistoryLatestOnly;
            latestOnly.AutoSize = true;
            latestOnly.Location = new Point(28, 296);
            latestOnly.ForeColor = Ui.Text;
            latestOnly.BackColor = Color.Transparent;
            Controls.Add(latestOnly);

            UiButton cancel = UiButton.Create("Cancel", UiButtonKind.Ghost, 96);
            cancel.Location = new Point(28 + 504 - 96 - 12 - 96, 340);
            cancel.DialogResult = DialogResult.Cancel;
            UiButton save = UiButton.Create("Save", UiButtonKind.Primary, 96);
            save.Location = new Point(28 + 504 - 96, 340);
            save.DialogResult = DialogResult.OK;
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
            s.AutoDeepSearchMinutes = (int)deepSearchMinutes.Value;
            s.HistoryLimit = (int)historyLimit.Value;
            s.Notifications = notifications.Checked;
            s.HistoryLatestOnly = latestOnly.Checked;
            s.Save();
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
            return Ui.FieldLabel(text, y);
        }
    }
}

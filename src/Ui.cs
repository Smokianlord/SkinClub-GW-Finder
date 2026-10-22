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
    // Shared palette and drawing helpers for the whole interface.
    internal static class Ui
    {
        public static readonly Color Bg = Color.FromArgb(11, 15, 23);
        public static readonly Color Surface = Color.FromArgb(17, 22, 33);
        public static readonly Color Surface2 = Color.FromArgb(22, 29, 43);
        public static readonly Color Surface3 = Color.FromArgb(29, 37, 54);
        public static readonly Color Line = Color.FromArgb(34, 43, 60);
        public static readonly Color LineStrong = Color.FromArgb(52, 64, 88);
        public static readonly Color Text = Color.FromArgb(233, 237, 245);
        public static readonly Color TextSoft = Color.FromArgb(178, 188, 208);
        public static readonly Color Muted = Color.FromArgb(125, 138, 162);
        public static readonly Color Accent = Color.FromArgb(99, 102, 241);
        public static readonly Color AccentHover = Color.FromArgb(129, 140, 248);
        public static readonly Color AccentDark = Color.FromArgb(79, 70, 229);
        public static readonly Color Success = Color.FromArgb(52, 211, 153);
        public static readonly Color Warning = Color.FromArgb(251, 191, 36);
        public static readonly Color Danger = Color.FromArgb(248, 113, 113);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hwnd, string subAppName, string subIdList);

        // Dark scroll bars (Windows 10 1809+); harmless no-op on older systems.
        public static void DarkScrollBars(Control control)
        {
            EventHandler apply = delegate { try { SetWindowTheme(control.Handle, "DarkMode_Explorer", null); } catch { } };
            if (control.IsHandleCreated) apply(control, EventArgs.Empty);
            control.HandleCreated += apply;
        }

        public static void DarkTitleBar(IntPtr handle)
        {
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(handle, 19, ref on, 4);
            }
            catch { }
        }

        public static GraphicsPath RoundPath(Rectangle r, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = Math.Max(2, radius * 2);
            d = Math.Min(d, Math.Min(r.Width, r.Height));
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static Color Mix(Color a, Color b, double t)
        {
            if (t < 0) t = 0;
            if (t > 1) t = 1;
            return Color.FromArgb(
                (int)Math.Round(a.R * (1.0 - t) + b.R * t),
                (int)Math.Round(a.G * (1.0 - t) + b.G * t),
                (int)Math.Round(a.B * (1.0 - t) + b.B * t));
        }

        public static Rectangle Draw3D(Graphics g, Size size, Color top, Color bottom, Color border, bool pressed)
        {
            Rectangle shadowRect = new Rectangle(2, 5, Math.Max(1, size.Width - 5), Math.Max(1, size.Height - 7));
            using (GraphicsPath sp = RoundPath(shadowRect, 10))
            using (SolidBrush sb = new SolidBrush(Color.FromArgb(90, 0, 0, 0)))
                g.FillPath(sb, sp);
            Rectangle face = new Rectangle(1, pressed ? 3 : 1, Math.Max(1, size.Width - 4), Math.Max(1, size.Height - 7));
            using (GraphicsPath path = RoundPath(face, 10))
            using (LinearGradientBrush fill = new LinearGradientBrush(face, top, bottom, LinearGradientMode.Vertical))
            using (Pen pen = new Pen(border))
            {
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }
            return face;
        }

        public const TextFormatFlags Single = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
    }

    public enum UiButtonKind { Ghost, Primary, Tab }

    // Raised "3D" button: soft drop shadow, vertical gradient face, and a press-down offset.
    // Used for every action and for the Active / Joined / History / Logs navigation.
    public class UiButton : Button
    {
        private static readonly Font BadgeFont = new Font("Segoe UI Semibold", 8F);
        private string badge;
        private bool selected;
        private bool hover;
        private bool pressed;
        public UiButtonKind Kind { get; private set; }
        public Color AccentColor { get; set; }

        public UiButton(UiButtonKind kind)
        {
            Kind = kind;
            AccentColor = Ui.Accent;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            Font = new Font("Segoe UI Semibold", 9.5F);
            ForeColor = Ui.Text;
            Height = 44;
            Margin = new Padding(0);
        }

        public static UiButton Create(string text, UiButtonKind kind, int width)
        {
            UiButton b = new UiButton(kind);
            b.Text = text;
            if (kind != UiButtonKind.Tab || width > 0) b.Width = width;
            return b;
        }

        public string Badge { get { return badge; } set { badge = value; FitToContent(); Invalidate(); } }
        public bool Selected { get { return selected; } set { if (selected == value) return; selected = value; Invalidate(); } }

        private int BadgeWidth()
        {
            return Math.Max(24, TextRenderer.MeasureText(badge ?? "", BadgeFont, new Size(1000, 40), TextFormatFlags.NoPadding).Width + 14);
        }

        public void FitToContent()
        {
            if (Kind != UiButtonKind.Tab) return;
            int w = 40 + TextRenderer.MeasureText(Text ?? "", Font, new Size(1000, 40), TextFormatFlags.NoPadding).Width;
            if (!string.IsNullOrEmpty(badge)) w += 10 + BadgeWidth();
            Width = w;
        }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); FitToContent(); Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent == null ? Ui.Bg : Parent.BackColor);

            Color top, bottom, border, fore = Ui.Text;
            if (Kind == UiButtonKind.Primary)
            {
                top = Color.FromArgb(126, 112, 255);
                bottom = Color.FromArgb(86, 72, 215);
                border = Color.FromArgb(155, 145, 255);
                fore = Color.White;
            }
            else if (selected)
            {
                top = Ui.Mix(Ui.Surface3, AccentColor, 0.62);
                bottom = Ui.Mix(Ui.Bg, AccentColor, 0.30);
                border = Ui.Mix(AccentColor, Color.White, 0.14);
            }
            else
            {
                top = Color.FromArgb(43, 55, 82);
                bottom = Color.FromArgb(24, 33, 52);
                border = Color.FromArgb(66, 82, 112);
                if (Kind == UiButtonKind.Tab) fore = Ui.TextSoft;
            }
            if (hover && Enabled)
            {
                top = Ui.Mix(top, Color.White, 0.10);
                bottom = Ui.Mix(bottom, Color.White, 0.05);
            }
            if (!Enabled)
            {
                top = Ui.Mix(top, Color.FromArgb(80, 84, 94), 0.55);
                bottom = Ui.Mix(bottom, Color.FromArgb(58, 62, 72), 0.55);
                border = Ui.Mix(border, Color.Gray, 0.55);
                fore = Color.FromArgb(145, 153, 168);
            }

            Rectangle face = Ui.Draw3D(g, new Size(Width, Height), top, bottom, border, pressed);
            if (Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(face, -5, -5), fore, bottom);

            if (Kind == UiButtonKind.Tab && !string.IsNullOrEmpty(badge))
            {
                int textWidth = TextRenderer.MeasureText(Text ?? "", Font, new Size(1000, 40), TextFormatFlags.NoPadding).Width;
                int bw = BadgeWidth();
                int total = textWidth + 10 + bw;
                int x = face.X + (face.Width - total) / 2;
                TextRenderer.DrawText(g, Text, Font, new Rectangle(x, face.Y, textWidth + 2, face.Height), fore,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                Rectangle pill = new Rectangle(x + textWidth + 10, face.Y + (face.Height - 18) / 2, bw, 18);
                using (GraphicsPath path = Ui.RoundPath(pill, 9))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(selected ? 90 : 120, 8, 12, 20)))
                    g.FillPath(b, path);
                TextRenderer.DrawText(g, badge, BadgeFont, pill, selected ? Color.White : Ui.TextSoft,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            }
            else
            {
                TextRenderer.DrawText(g, Text, Font, face, fore,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            }
        }
    }

    // Dark text input with a rounded border, focus ring, native placeholder and optional search icon.
    public class TextField : Panel
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        private bool focused;
        private readonly bool searchIcon;
        private string placeholder = "";
        private Rectangle clearRect = Rectangle.Empty;
        public TextBox Input { get; private set; }
        // When true the field leaves room under it for the 3D buttons' shadow so heights line up.
        public bool ReserveShadow { get; set; }

        public TextField(bool withSearchIcon)
        {
            searchIcon = withSearchIcon;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = 36;
            BackColor = Ui.Bg;
            Input = new TextBox();
            Input.BorderStyle = BorderStyle.None;
            Input.BackColor = Ui.Surface2;
            Input.ForeColor = Ui.Text;
            Input.Font = new Font("Segoe UI", 10F);
            Input.Enter += delegate { focused = true; Invalidate(); };
            Input.Leave += delegate { focused = false; Invalidate(); };
            Input.TextChanged += delegate { Invalidate(); };
            Input.HandleCreated += delegate { ApplyPlaceholder(); };
            Controls.Add(Input);
            Cursor = Cursors.IBeam;
        }

        public string Placeholder { get { return placeholder; } set { placeholder = value ?? ""; ApplyPlaceholder(); } }

        private void ApplyPlaceholder()
        {
            if (Input.IsHandleCreated) SendMessage(Input.Handle, 0x1501, new IntPtr(1), placeholder);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Input == null) return;
            int left = searchIcon ? 38 : 14;
            int right = 34;
            Input.Left = left;
            Input.Width = Math.Max(10, Width - left - right);
            int faceTop = ReserveShadow ? 1 : 0, faceHeight = ReserveShadow ? Height - 7 : Height;
            Input.Top = faceTop + Math.Max(0, (faceHeight - Input.Height) / 2);
            clearRect = new Rectangle(Width - 30, faceTop + (faceHeight - 22) / 2, 22, 22);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (Input.TextLength > 0 && clearRect.Contains(e.Location)) { Input.Clear(); Input.Focus(); return; }
            Input.Focus();
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent == null ? Ui.Bg : Parent.BackColor);
            Rectangle r = ReserveShadow ? new Rectangle(0, 1, Width - 1, Height - 8) : new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Ui.RoundPath(r, 10))
            using (SolidBrush b = new SolidBrush(Ui.Surface2))
            using (Pen p = new Pen(focused ? Ui.Accent : Ui.Line, focused ? 1.5F : 1F))
            {
                g.FillPath(b, path);
                g.DrawPath(p, path);
            }
            Color icon = focused ? Ui.AccentHover : Ui.Muted;
            if (searchIcon)
            {
                int cy = r.Y + r.Height / 2;
                using (Pen p = new Pen(icon, 1.7F))
                {
                    p.StartCap = LineCap.Round;
                    p.EndCap = LineCap.Round;
                    g.DrawEllipse(p, 14, cy - 7, 9, 9);
                    g.DrawLine(p, 21, cy + 1, 25, cy + 5);
                }
            }
            if (Input.TextLength > 0)
            {
                using (SolidBrush b = new SolidBrush(Ui.Surface3))
                    g.FillEllipse(b, clearRect.X + 2, clearRect.Y + 2, 18, 18);
                using (Pen p = new Pen(Ui.TextSoft, 1.5F))
                {
                    p.StartCap = LineCap.Round;
                    p.EndCap = LineCap.Round;
                    g.DrawLine(p, clearRect.X + 8, clearRect.Y + 8, clearRect.X + 14, clearRect.Y + 14);
                    g.DrawLine(p, clearRect.X + 14, clearRect.Y + 8, clearRect.X + 8, clearRect.Y + 14);
                }
            }
        }
    }

    internal class DarkMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Ui.Surface2; } }
        public override Color ImageMarginGradientBegin { get { return Ui.Surface2; } }
        public override Color ImageMarginGradientMiddle { get { return Ui.Surface2; } }
        public override Color ImageMarginGradientEnd { get { return Ui.Surface2; } }
        public override Color MenuBorder { get { return Ui.LineStrong; } }
        public override Color MenuItemBorder { get { return Ui.Surface3; } }
        public override Color MenuItemSelected { get { return Ui.Surface3; } }
        public override Color MenuItemSelectedGradientBegin { get { return Ui.Surface3; } }
        public override Color MenuItemSelectedGradientEnd { get { return Ui.Surface3; } }
        public override Color MenuItemPressedGradientBegin { get { return Ui.Surface3; } }
        public override Color MenuItemPressedGradientEnd { get { return Ui.Surface3; } }
    }

    // Replaces the white system combo box: shows the current value and opens a dark menu.
    public class UiPicker : Control
    {
        private bool hover;
        private int selectedIndex = -1;
        private readonly List<object> items = new List<object>();
        private ContextMenuStrip menu;
        public event EventHandler SelectedIndexChanged;

        public UiPicker()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            Height = 44;
            Font = new Font("Segoe UI", 9.5F);
            TabStop = true;
        }

        public List<object> Items { get { return items; } }
        public object SelectedItem { get { return selectedIndex >= 0 && selectedIndex < items.Count ? items[selectedIndex] : null; } }
        public int SelectedIndex
        {
            get { return selectedIndex; }
            set
            {
                if (value == selectedIndex) return;
                selectedIndex = value;
                Invalidate();
                if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space || e.KeyCode == Keys.Down) { ShowMenu(); e.Handled = true; }
            base.OnKeyDown(e);
        }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowMenu(); base.OnMouseDown(e); }

        private void ShowMenu()
        {
            // The previous menu is released only now: disposing it while its Click handler is still pending crashes.
            if (menu != null) menu.Dispose();
            menu = new ContextMenuStrip();
            menu.Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors());
            menu.BackColor = Ui.Surface2;
            menu.ForeColor = Ui.Text;
            menu.Font = Font;
            menu.ShowImageMargin = false;
            menu.Padding = new Padding(4);
            for (int i = 0; i < items.Count; i++)
            {
                int index = i;
                ToolStripMenuItem item = new ToolStripMenuItem(items[i].ToString());
                item.ForeColor = i == selectedIndex ? Ui.AccentHover : Ui.Text;
                item.Padding = new Padding(6, 5, 6, 5);
                item.Click += delegate { SelectedIndex = index; };
                menu.Items.Add(item);
            }
            menu.Show(this, new Point(0, Height - 2));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent == null ? Ui.Bg : Parent.BackColor);
            Color top = Color.FromArgb(43, 55, 82), bottom = Color.FromArgb(24, 33, 52);
            if (hover) { top = Ui.Mix(top, Color.White, 0.10); bottom = Ui.Mix(bottom, Color.White, 0.05); }
            Rectangle face = Ui.Draw3D(g, new Size(Width, Height), top, bottom, Color.FromArgb(66, 82, 112), false);
            object current = SelectedItem;
            TextRenderer.DrawText(g, current == null ? "" : current.ToString(), Font, new Rectangle(face.X + 14, face.Y, face.Width - 40, face.Height), Ui.Text, Ui.Single);
            using (Pen p = new Pen(Ui.TextSoft, 1.6F))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                int cx = face.Right - 16, cy = face.Y + face.Height / 2;
                g.DrawLine(p, cx - 4, cy - 2, cx, cy + 2);
                g.DrawLine(p, cx, cy + 2, cx + 4, cy - 2);
            }
        }
    }
    // Thin progress strip shown above the table while a scan is running.
    public class ScanStrip : Control
    {
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private double position;
        private bool active;

        public ScanStrip()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Height = 2;
            timer.Interval = 30;
            timer.Tick += delegate { position += 0.018; if (position > 1.0) position = 0; Invalidate(); };
        }

        public bool Active
        {
            get { return active; }
            set
            {
                active = value;
                if (value) timer.Start(); else timer.Stop();
                position = 0;
                Invalidate();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Ui.Line);
            if (!active) return;
            int w = Math.Max(40, Width / 4);
            int x = (int)((Width + w) * position) - w;
            using (LinearGradientBrush b = new LinearGradientBrush(new Rectangle(x - 1, 0, w + 2, Math.Max(1, Height)),
                Color.FromArgb(0, Ui.Accent), Ui.AccentHover, LinearGradientMode.Horizontal))
                e.Graphics.FillRectangle(b, x, 0, w, Height);
        }
    }

    public class AddLinkForm : Form
    {
        public string CreatorName { get { return creatorField.Input.Text.Trim(); } }
        public string GiveawayUrl { get { return urlField.Input.Text.Trim(); } }
        private readonly TextField creatorField;
        private readonly TextField urlField;

        public AddLinkForm()
        {
            Text = "Add giveaway";
            ClientSize = new Size(560, 330);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Ui.Surface;
            ForeColor = Ui.Text;
            Font = new Font("Segoe UI", 9F);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            Label title = new Label();
            title.Text = "Add a giveaway";
            title.SetBounds(28, 24, 504, 32);
            title.Font = new Font("Segoe UI Semibold", 15F);
            title.ForeColor = Ui.Text;
            Controls.Add(title);

            Label sub = new Label();
            sub.Text = "Paste a direct, dated creator giveaway link. The page is checked before it is saved.";
            sub.SetBounds(28, 60, 504, 22);
            sub.ForeColor = Ui.Muted;
            Controls.Add(sub);

            Controls.Add(MakeLabel("CREATOR  (OPTIONAL)", 108));
            creatorField = new TextField(false);
            creatorField.BackColor = Ui.Surface;
            creatorField.SetBounds(28, 130, 504, 38);
            creatorField.Placeholder = "e.g. Jon Sandman";
            Controls.Add(creatorField);

            Controls.Add(MakeLabel("GIVEAWAY LINK", 184));
            urlField = new TextField(false);
            urlField.BackColor = Ui.Surface;
            urlField.SetBounds(28, 206, 504, 38);
            urlField.Placeholder = "https://creator.club/010926/";
            Controls.Add(urlField);

            UiButton cancel = UiButton.Create("Cancel", UiButtonKind.Ghost, 96);
            cancel.Location = new Point(28 + 504 - 96 - 12 - 140, 268);
            cancel.DialogResult = DialogResult.Cancel;
            UiButton add = UiButton.Create("Validate and add", UiButtonKind.Primary, 140);
            add.Location = new Point(28 + 504 - 140, 268);
            add.DialogResult = DialogResult.OK;
            cancel.BackColor = add.BackColor = Ui.Surface;
            Controls.Add(cancel);
            Controls.Add(add);
            AcceptButton = add;
            CancelButton = cancel;
            Shown += delegate { urlField.Input.Focus(); };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Ui.DarkTitleBar(Handle);
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

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (Width <= 0 || Height <= 0) return;
            // Clip the native control too: transparent WinForms controls otherwise
            // paint a rectangular patch of the parent over the grid beneath them.
            using (GraphicsPath outline = RoundedRect(ClientRectangle, Math.Min(Width, Height) / 2))
            {
                Region previous = Region;
                Region = new Region(outline);
                if (previous != null) previous.Dispose();
            }
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

            using (GraphicsPath path = RoundedRect(pill, pill.Height / 2))
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

    public class BufferedGrid : DataGridView
    {
        public BufferedGrid() { DoubleBuffered = true; }
    }
}

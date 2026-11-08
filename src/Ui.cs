using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
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
    // Shared palette and drawing helpers for the whole interface. The colours follow the SkinClub
    // creator pages' own stylesheet: deep indigo surfaces, a hot pink-to-red primary gradient,
    // electric blue, cyan, neon green and gold accents.
    internal static class Ui
    {
        public static readonly Color Bg = Color.FromArgb(18, 16, 38);          // #121026
        public static readonly Color Surface = Color.FromArgb(28, 26, 49);     // #1C1A31
        public static readonly Color Surface2 = Color.FromArgb(39, 36, 64);    // #272440
        public static readonly Color Surface3 = Color.FromArgb(52, 47, 86);
        public static readonly Color Line = Color.FromArgb(48, 44, 80);
        public static readonly Color LineStrong = Color.FromArgb(72, 67, 119); // #484377
        public static readonly Color Text = Color.FromArgb(247, 246, 255);
        public static readonly Color TextSoft = Color.FromArgb(222, 223, 247); // #DEDFF7
        public static readonly Color Muted = Color.FromArgb(146, 141, 196);
        public static readonly Color Pink = Color.FromArgb(255, 51, 200);      // #FF33C8
        public static readonly Color Hot = Color.FromArgb(255, 0, 94);         // #FF005E
        public static readonly Color Blue = Color.FromArgb(34, 3, 251);        // #2203FB
        public static readonly Color Violet = Color.FromArgb(84, 38, 255);
        public static readonly Color Cyan = Color.FromArgb(0, 229, 255);
        public static readonly Color Lime = Color.FromArgb(72, 255, 96);
        public static readonly Color Gold = Color.FromArgb(255, 204, 0);
        public static readonly Color Accent = Hot;
        public static readonly Color AccentHover = Color.FromArgb(255, 112, 214);
        public static readonly Color AccentDark = Blue;
        public static readonly Color Success = Lime;
        public static readonly Color Warning = Gold;
        public static readonly Color Danger = Color.FromArgb(255, 61, 110);

        // Only fonts with static, hinted faces: the Windows text engine (GDI) draws them crisply with
        // ClearType. Variable fonts such as Bahnschrift or Cascadia come out with uneven strokes there.
        public const string DisplayName = "Segoe UI Semibold";
        public const string MonoName = "Consolas";
        public static Font DisplayFont(float size) { return new Font(DisplayName, size); }
        public static Font BoldFont(float size) { return new Font("Segoe UI", size, FontStyle.Bold); }
        public static Font NumberFont(float size) { return new Font(DisplayName, size); }
        public static Font MonoFont(float size) { return new Font(MonoName, size); }

        // Angular "cut corner" shape used for buttons, chips and cards (top-left and bottom-right cut).
        public static GraphicsPath ChamferPath(Rectangle r, int cut)
        {
            cut = Math.Max(0, Math.Min(cut, Math.Min(r.Width, r.Height) / 2));
            GraphicsPath path = new GraphicsPath();
            path.AddPolygon(new Point[]
            {
                new Point(r.Left + cut, r.Top), new Point(r.Right, r.Top), new Point(r.Right, r.Bottom - cut),
                new Point(r.Right - cut, r.Bottom), new Point(r.Left, r.Bottom), new Point(r.Left, r.Top + cut)
            });
            return path;
        }

        // Paints the part of the window's backdrop that lies under this area of the control, so custom
        // controls blend into the background art instead of showing a flat rectangle.
        public static void PaintBackdrop(Control control, Graphics g, Rectangle area)
        {
            IBackdropHost host;
            Point origin = OriginInForm(control, out host);
            Bitmap art = host == null ? null : host.Backdrop;
            GraphicsState state = g.Save();
            g.CompositingMode = CompositingMode.SourceCopy;
            Rectangle source = new Rectangle(area.X + origin.X, area.Y + origin.Y, area.Width, area.Height);
            if (art == null || source.Right > art.Width || source.Bottom > art.Height || source.X < 0 || source.Y < 0)
                using (SolidBrush bg = new SolidBrush(Bg)) g.FillRectangle(bg, area);
            if (art != null)
            {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.None;
                g.DrawImage(art, area, source, GraphicsUnit.Pixel);
            }
            g.Restore(state);
        }

        // Where the control's top-left corner sits in its window's client area, and that window.
        public static Point OriginInForm(Control control, out IBackdropHost host)
        {
            Point origin = Point.Empty;
            Control current = control;
            host = null;
            while (current != null)
            {
                Form form = current as Form;
                if (form != null) { host = form as IBackdropHost; break; }
                origin.Offset(current.Left, current.Top);
                current = current.Parent;
            }
            return origin;
        }

        public static Color Alpha(Color c, int alpha) { return Color.FromArgb(Math.Max(0, Math.Min(255, alpha)), c.R, c.G, c.B); }

        // Small capitals label above a dialog field.
        public static Label FieldLabel(string text, int y)
        {
            Label l = new Label();
            l.Text = text;
            l.SetBounds(28, y, 504, 18);
            l.ForeColor = Color.FromArgb(255, 140, 220);
            l.BackColor = Color.Transparent;
            l.Font = DisplayFont(8.5F);
            return l;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hwnd, string subAppName, string subIdList);

        // Dark scroll bars (Windows 10 1809+); harmless no-op on older systems.
        public static void DarkScrollBars(Control control)
        {
            EventHandler apply = delegate { try { SetWindowTheme(control.Handle, "DarkMode_Explorer", null); } catch (Exception swallowed) { Diag.Swallow("Applying dark scrollbars", swallowed); } };
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
            catch (Exception swallowed) { Diag.Swallow("Applying dark title bar", swallowed); }
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

        // Angular raised face: a dark drop shadow, a gradient face with cut corners, a thin top highlight
        // and a press-down offset. Returns the face rectangle.
        public static Rectangle Draw3D(Graphics g, Size size, Color top, Color bottom, Color border, bool pressed)
        {
            return Draw3D(g, size, top, bottom, border, pressed, LinearGradientMode.Vertical);
        }

        public static Rectangle Draw3D(Graphics g, Size size, Color top, Color bottom, Color border, bool pressed, LinearGradientMode direction)
        {
            Rectangle shadowRect = new Rectangle(2, 5, Math.Max(1, size.Width - 5), Math.Max(1, size.Height - 7));
            using (GraphicsPath sp = ChamferPath(shadowRect, 9))
            using (SolidBrush sb = new SolidBrush(Color.FromArgb(140, 4, 2, 14)))
                g.FillPath(sb, sp);
            Rectangle face = new Rectangle(1, pressed ? 3 : 1, Math.Max(1, size.Width - 4), Math.Max(1, size.Height - 7));
            using (GraphicsPath path = ChamferPath(face, 9))
            using (LinearGradientBrush fill = new LinearGradientBrush(face, top, bottom, direction))
            using (Pen pen = new Pen(border, 1.2F))
            {
                g.FillPath(fill, path);
                using (LinearGradientBrush shine = new LinearGradientBrush(face, Color.FromArgb(46, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), LinearGradientMode.Vertical))
                {
                    Rectangle upper = new Rectangle(face.X, face.Y, face.Width, Math.Max(1, face.Height / 2));
                    GraphicsState s = g.Save();
                    g.SetClip(path);
                    g.FillRectangle(shine, upper);
                    g.Restore(s);
                }
                g.DrawPath(pen, path);
            }
            return face;
        }

        public const TextFormatFlags Single = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
    }

    // Keeps a control's finished picture and copies it to the screen on every repaint, drawing it again
    // only when something it depends on changes: its state, size, place in the window, or the backdrop.
    // Gradients, glows and icons are then drawn once instead of on every repaint.
    internal sealed class PaintCache : IDisposable
    {
        private Bitmap image;
        private string key;

        public void Paint(Control control, Graphics g, string state, Action<Graphics> render)
        {
            int w = Math.Max(1, control.Width), h = Math.Max(1, control.Height);
            IBackdropHost host;
            Point origin = Ui.OriginInForm(control, out host);
            string k = state + "|" + w + "x" + h + "|" + origin.X + "," + origin.Y + "|" + (host == null ? -1 : host.BackdropVersion) +
                "|" + (host != null && host.Backdrop != null ? 1 : 0);
            if (image == null || k != key)
            {
                if (image == null || image.Width != w || image.Height != h)
                {
                    if (image != null) image.Dispose();
                    // Opaque format: ClearType text drawn into it stays crisp (no alpha channel to lose).
                    image = new Bitmap(w, h, PixelFormat.Format32bppRgb);
                }
                using (Graphics bg = Graphics.FromImage(image)) render(bg);
                key = k;
            }
            GraphicsState s = g.Save();
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.DrawImage(image, new Rectangle(0, 0, w, h), 0, 0, w, h, GraphicsUnit.Pixel);
            g.Restore(s);
        }

        public void Dispose()
        {
            if (image != null) { image.Dispose(); image = null; }
        }
    }

    public enum UiButtonKind { Ghost, Primary, Tab }

    // Angular raised button in the SkinClub style: the primary action uses the pink-to-red gradient and
    // turns electric blue on hover, as on the site. Used for every action and for the Active / Joined /
    // History / Logs navigation. Labels are drawn in capitals.
    public class UiButton : Button
    {
        private static readonly Font BadgeFont = Ui.DisplayFont(8.5F);
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
            Font = Ui.DisplayFont(10F);
            ForeColor = Ui.Text;
            Height = 44;
            Margin = new Padding(0);
        }

        private string Label { get { return (Text ?? "").ToUpperInvariant(); } }

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
            int w = 40 + TextRenderer.MeasureText(Label, Font, new Size(1000, 40), TextFormatFlags.NoPadding).Width;
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

        private readonly PaintCache cache = new PaintCache();

        protected override void OnPaint(PaintEventArgs e)
        {
            string state = Kind + "|" + Label + "|" + badge + "|" + selected + hover + pressed + Enabled + (Focused && ShowFocusCues) + "|" + AccentColor.ToArgb();
            cache.Paint(this, e.Graphics, state, Render);
        }

        protected override void OnLocationChanged(EventArgs e) { base.OnLocationChanged(e); Invalidate(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) cache.Dispose();
            base.Dispose(disposing);
        }

        private void Render(Graphics g)
        {
            Ui.PaintBackdrop(this, g, ClientRectangle);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Color top, bottom, border, fore = Ui.Text;
            LinearGradientMode direction = LinearGradientMode.Vertical;
            bool glow = false;
            if (Kind == UiButtonKind.Primary)
            {
                // Site primary button: pink-to-red, electric blue while hovered.
                top = hover && Enabled ? Ui.Violet : Ui.Pink;
                bottom = hover && Enabled ? Ui.Blue : Ui.Hot;
                border = hover && Enabled ? Color.FromArgb(150, 130, 255) : Color.FromArgb(255, 140, 220);
                fore = Color.White;
                direction = LinearGradientMode.Horizontal;
                glow = Enabled;
            }
            else if (selected)
            {
                top = Ui.Mix(Ui.Surface2, AccentColor, 0.42);
                bottom = Ui.Mix(Ui.Surface, AccentColor, 0.20);
                border = AccentColor;
                fore = Color.White;
                glow = true;
            }
            else
            {
                top = Ui.Surface3;
                bottom = Ui.Surface;
                border = Ui.LineStrong;
                fore = Kind == UiButtonKind.Tab ? Ui.TextSoft : Ui.Text;
                if (hover && Enabled)
                {
                    top = Ui.Mix(top, Kind == UiButtonKind.Tab ? AccentColor : Ui.Pink, 0.18);
                    border = Ui.Mix(Ui.LineStrong, Kind == UiButtonKind.Tab ? AccentColor : Ui.Pink, 0.75);
                    fore = Color.White;
                }
            }
            if (!Enabled)
            {
                top = Ui.Mix(top, Ui.Surface2, 0.7);
                bottom = Ui.Mix(bottom, Ui.Surface, 0.7);
                border = Ui.Mix(border, Ui.Line, 0.7);
                fore = Ui.Muted;
            }

            if (glow)
            {
                Color halo = Kind == UiButtonKind.Primary ? (hover ? Ui.Blue : Ui.Hot) : AccentColor;
                Rectangle under = new Rectangle(6, Height - 14, Math.Max(1, Width - 12), 10);
                using (GraphicsPath p = Ui.RoundPath(under, 5))
                using (PathGradientBrush b = new PathGradientBrush(p))
                {
                    b.CenterColor = Color.FromArgb(150, halo);
                    b.SurroundColors = new Color[] { Color.FromArgb(0, halo) };
                    g.FillPath(b, p);
                }
            }

            Rectangle face = Ui.Draw3D(g, new Size(Width, Height), top, bottom, border, pressed, direction);
            if (Focused && ShowFocusCues)
                using (GraphicsPath focusPath = Ui.ChamferPath(Rectangle.Inflate(face, -4, -4), 6))
                using (Pen focus = new Pen(Color.FromArgb(160, 255, 255, 255)) { DashStyle = DashStyle.Dot })
                    g.DrawPath(focus, focusPath);

            if (Kind == UiButtonKind.Tab && !string.IsNullOrEmpty(badge))
            {
                int textWidth = TextRenderer.MeasureText(Label, Font, new Size(1000, 40), TextFormatFlags.NoPadding).Width;
                int bw = BadgeWidth();
                int total = textWidth + 10 + bw;
                int x = face.X + (face.Width - total) / 2;
                TextRenderer.DrawText(g, Label, Font, new Rectangle(x, face.Y, textWidth + 2, face.Height), fore,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                Rectangle pill = new Rectangle(x + textWidth + 10, face.Y + (face.Height - 20) / 2, bw, 20);
                using (GraphicsPath path = Ui.ChamferPath(pill, 5))
                using (SolidBrush b = new SolidBrush(selected ? AccentColor : Color.FromArgb(150, 8, 6, 22)))
                    g.FillPath(b, path);
                TextRenderer.DrawText(g, badge, BadgeFont, new Rectangle(pill.X, pill.Y + 1, pill.Width, pill.Height), selected ? Ui.Bg : Ui.TextSoft,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            }
            else
            {
                TextRenderer.DrawText(g, Label, Font, face, fore,
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

        private readonly PaintCache cache = new PaintCache();

        protected override void OnPaint(PaintEventArgs e)
        {
            cache.Paint(this, e.Graphics, focused + "|" + (Input.TextLength > 0) + "|" + ReserveShadow + "|" + searchIcon, Render);
        }

        protected override void OnLocationChanged(EventArgs e) { base.OnLocationChanged(e); Invalidate(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) cache.Dispose();
            base.Dispose(disposing);
        }

        private void Render(Graphics g)
        {
            Ui.PaintBackdrop(this, g, ClientRectangle);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = ReserveShadow ? new Rectangle(0, 1, Width - 1, Height - 8) : new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Ui.ChamferPath(r, 9))
            using (SolidBrush b = new SolidBrush(Ui.Surface2))
            using (Pen p = new Pen(focused ? Ui.Pink : Ui.LineStrong, focused ? 1.6F : 1.1F))
            {
                g.FillPath(b, path);
                g.DrawPath(p, path);
            }
            if (focused)
                using (Pen under = new Pen(Color.FromArgb(120, Ui.Pink), 2F))
                    g.DrawLine(under, r.X + 12, r.Bottom + 2, r.Right - 20, r.Bottom + 2);
            Color icon = focused ? Ui.Pink : Ui.Muted;
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

        private readonly PaintCache cache = new PaintCache();

        protected override void OnPaint(PaintEventArgs e)
        {
            object current = SelectedItem;
            cache.Paint(this, e.Graphics, hover + "|" + (current == null ? "" : current.ToString()), Render);
        }

        protected override void OnLocationChanged(EventArgs e) { base.OnLocationChanged(e); Invalidate(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { cache.Dispose(); if (menu != null) menu.Dispose(); }
            base.Dispose(disposing);
        }

        private void Render(Graphics g)
        {
            Ui.PaintBackdrop(this, g, ClientRectangle);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color top = Ui.Surface3, bottom = Ui.Surface;
            Color border = hover ? Ui.Mix(Ui.LineStrong, Ui.Pink, 0.75) : Ui.LineStrong;
            if (hover) top = Ui.Mix(top, Ui.Pink, 0.16);
            Rectangle face = Ui.Draw3D(g, new Size(Width, Height), top, bottom, border, false);
            object current = SelectedItem;
            TextRenderer.DrawText(g, current == null ? "" : current.ToString(), Font, new Rectangle(face.X + 14, face.Y, face.Width - 40, face.Height), Ui.Text, Ui.Single);
            using (Pen p = new Pen(hover ? Ui.Pink : Ui.TextSoft, 1.6F))
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
            // Idle: the site's pink divider line. Scanning: a pink-to-cyan streak sweeps across it.
            Rectangle all = new Rectangle(0, 0, Math.Max(1, Width), Math.Max(1, Height));
            e.Graphics.Clear(Ui.Surface);
            using (LinearGradientBrush line = new LinearGradientBrush(all, Color.FromArgb(0, Ui.Hot), Color.FromArgb(0, Ui.Hot), LinearGradientMode.Horizontal))
            {
                ColorBlend cb = new ColorBlend();
                cb.Colors = new Color[] { Color.FromArgb(40, Ui.Violet), Color.FromArgb(150, Ui.Hot), Color.FromArgb(150, Ui.Hot), Color.FromArgb(40, Ui.Violet) };
                cb.Positions = new float[] { 0f, 0.4f, 0.6f, 1f };
                line.InterpolationColors = cb;
                e.Graphics.FillRectangle(line, all);
            }
            if (!active) return;
            int w = Math.Max(60, Width / 4);
            int x = (int)((Width + w) * position) - w;
            using (LinearGradientBrush b = new LinearGradientBrush(new Rectangle(x - 1, 0, w + 2, Math.Max(1, Height)),
                Color.FromArgb(0, Ui.Pink), Ui.Cyan, LinearGradientMode.Horizontal))
                e.Graphics.FillRectangle(b, x, 0, w, Height);
        }
    }

    public class AddLinkForm : ThemedForm
    {
        // Everything pasted into the box; Scanner.ParseLinkList picks out the giveaway links.
        public string LinksText { get { return linksBox.Text; } set { linksBox.Text = value ?? ""; } }
        private readonly TextBox linksBox;
        protected override bool HeroArt { get { return false; } }

        public AddLinkForm()
        {
            Text = "Add giveaways";
            ClientSize = new Size(560, 444);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9F);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception swallowed) { Diag.Swallow("Loading window icon", swallowed); }

            Label title = new Label();
            title.Text = "ADD GIVEAWAYS";
            title.SetBounds(28, 22, 504, 34);
            title.Font = Ui.DisplayFont(17F);
            title.ForeColor = Ui.Text;
            title.BackColor = Color.Transparent;
            Controls.Add(title);

            Label sub = new Label();
            sub.Text = "Paste one or more dated giveaway links, one per line. Each page is checked before it is saved.";
            sub.SetBounds(28, 60, 504, 38);
            sub.ForeColor = Ui.TextSoft;
            sub.BackColor = Color.Transparent;
            Controls.Add(sub);

            Controls.Add(MakeLabel("GIVEAWAY LINKS", 108));
            linksBox = new TextBox();
            linksBox.Multiline = true;
            linksBox.AcceptsReturn = true;
            linksBox.WordWrap = false;
            linksBox.ScrollBars = ScrollBars.Both;
            linksBox.BorderStyle = BorderStyle.FixedSingle;
            linksBox.BackColor = Ui.Surface2;
            linksBox.ForeColor = Ui.Text;
            linksBox.Font = Ui.MonoFont(10F);
            linksBox.SetBounds(28, 130, 504, 236);
            Controls.Add(linksBox);

            Label hint = new Label();
            hint.Text = "For example https://creator.club/011126/";
            hint.SetBounds(28, 372, 290, 18);
            hint.ForeColor = Ui.Muted;
            hint.BackColor = Color.Transparent;
            Controls.Add(hint);

            UiButton cancel = UiButton.Create("Cancel", UiButtonKind.Ghost, 96);
            cancel.Location = new Point(28 + 504 - 96 - 12 - 140, 392);
            cancel.DialogResult = DialogResult.Cancel;
            UiButton add = UiButton.Create("Check and add", UiButtonKind.Primary, 140);
            add.Location = new Point(28 + 504 - 140, 392);
            add.DialogResult = DialogResult.OK;
            Controls.Add(cancel);
            Controls.Add(add);
            // No AcceptButton: Enter starts a new line in the links box.
            CancelButton = cancel;
            Shown += delegate { linksBox.Focus(); };
        }

        private static Label MakeLabel(string text, int y)
        {
            return Ui.FieldLabel(text, y);
        }
    }
    public class CopyToastControl : Control
    {
        private readonly System.Windows.Forms.Timer animationTimer;
        private readonly string toastText;
        private Point settledLocation;
        private int elapsedMs;
        private double animationAlpha;

        private static readonly Color ToastTop = Color.FromArgb(255, 51, 200);
        private static readonly Color ToastBottom = Color.FromArgb(200, 0, 80);
        private static readonly Color ToastBorder = Color.FromArgb(255, 170, 230);
        private static readonly Color ToastText = Color.White;

        public event EventHandler Finished;

        public CopyToastControl(string text, Point anchor, Rectangle availableArea)
        {
            toastText = string.IsNullOrWhiteSpace(text) ? "COPIED" : text.ToUpperInvariant();

            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            TabStop = false;
            Enabled = false;
            Font = Ui.DisplayFont(9F);

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
            using (SolidBrush badgeFill = new SolidBrush(WithAlpha(Color.FromArgb(34, 3, 251), animationAlpha)))
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

    // The giveaway table. Its empty area shows the window's backdrop under a dark glass layer, so the
    // background art carries on behind the rows (cells do the same in MainForm.GridCellPainting).
    public class BufferedGrid : DataGridView
    {
        public BufferedGrid() { DoubleBuffered = true; }

        public static readonly Color Glass = Color.FromArgb(212, 22, 20, 44);

        protected override void PaintBackground(Graphics graphics, Rectangle clipBounds, Rectangle gridBounds)
        {
            Ui.PaintBackdrop(this, graphics, gridBounds);
            using (SolidBrush glass = new SolidBrush(Glass)) graphics.FillRectangle(glass, gridBounds);
        }
    }
}

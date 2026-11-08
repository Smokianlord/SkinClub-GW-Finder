using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace SkinClubGiveawayDesktop
{
    // A window that paints the SkinClub-style backdrop (glows plus faint 3D cases, coins, cash and skins)
    // behind everything. Child controls show it through transparent backgrounds or by painting their
    // own slice of it with Ui.PaintBackdrop.
    internal interface IBackdropHost
    {
        Bitmap Backdrop { get; }
        // Changes whenever a new backdrop is swapped in, so cached control images know to redraw.
        int BackdropVersion { get; }
    }

    public class ThemedForm : Form, IBackdropHost
    {
        private Bitmap backdrop;
        private int backdropVersion;
        private Size laidOutFor = Size.Empty;
        private bool rendering, renderAgain;
        private readonly System.Windows.Forms.Timer relayout = new System.Windows.Forms.Timer();
        public Bitmap Backdrop { get { return backdrop; } }
        public int BackdropVersion { get { return backdropVersion; } }

        // Dialogs use a calmer backdrop than the main window.
        protected virtual bool HeroArt { get { return true; } }

        // While the user drags the window edge, laying out every control on each mouse move (about 40 ms
        // for the main window) makes the drag stutter; frozen, a mouse move costs about 5 ms. Layout is
        // paused while the edge moves, caught up whenever the mouse rests for a moment, and done once more
        // when the drag ends.
        private readonly System.Windows.Forms.Timer dragLayout = new System.Windows.Forms.Timer();
        private bool inSizeMove;
        private Size laidOutClient = Size.Empty;

        public ThemedForm()
        {
            DoubleBuffered = true;
            BackColor = Ui.Bg;
            ForeColor = Ui.Text;
            // After the window stops changing size, lay the art out again for the new size (off the UI thread).
            relayout.Interval = 250;
            relayout.Tick += delegate { relayout.Stop(); RelayoutInBackground(); };
            dragLayout.Interval = 120;
            dragLayout.Tick += delegate { dragLayout.Stop(); if (inSizeMove) CatchUpLayout(); };
        }

        // Also raised when the window is only moved; pausing layout then costs nothing.
        protected override void OnResizeBegin(EventArgs e)
        {
            base.OnResizeBegin(e);
            if (inSizeMove) return;
            inSizeMove = true;
            laidOutClient = ClientSize;
            SuspendLayout();
        }

        protected override void OnResizeEnd(EventArgs e)
        {
            base.OnResizeEnd(e);
            if (!inSizeMove) return;
            inSizeMove = false;
            dragLayout.Stop();
            ResumeLayout(true);
            laidOutClient = ClientSize;
        }

        private void CatchUpLayout()
        {
            if (ClientSize == laidOutClient) return;
            ResumeLayout(true);
            laidOutClient = ClientSize;
            SuspendLayout();
        }

        // The art is rendered at the size of the largest screen, so resizing or maximising never needs a new
        // bitmap while the mouse is moving; only when the window is bigger than that is one made right away.
        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            Size client = ClientSize;
            if (client.Width <= 0 || client.Height <= 0) return;
            if (inSizeMove) { dragLayout.Stop(); dragLayout.Start(); } // catch up once the mouse rests
            if (backdrop == null || backdrop.Width < client.Width || backdrop.Height < client.Height)
            {
                Swap(BackdropArt.Render(CanvasSize(client), client, HeroArt), client);
                return;
            }
            if (client != laidOutFor) { relayout.Stop(); relayout.Start(); }
        }

        private Size CanvasSize(Size client)
        {
            if (FormBorderStyle == FormBorderStyle.FixedDialog) return client; // dialogs never resize
            int w = client.Width, h = client.Height;
            foreach (Screen s in Screen.AllScreens) { w = Math.Max(w, s.Bounds.Width); h = Math.Max(h, s.Bounds.Height); }
            return new Size(w, h);
        }

        private void RelayoutInBackground()
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (rendering) { renderAgain = true; return; }
            Size client = ClientSize;
            if (client.Width <= 0 || client.Height <= 0 || client == laidOutFor) return;
            rendering = true;
            Size canvas = CanvasSize(client);
            bool hero = HeroArt;
            System.Threading.Tasks.Task.Run(delegate
            {
                Bitmap art = null;
                try { art = BackdropArt.Render(canvas, client, hero); }
                catch (Exception ex) { Diag.Swallow("Drawing the background", ex); }
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        rendering = false;
                        if (art != null && !IsDisposed) Swap(art, client); else if (art != null) art.Dispose();
                        if (renderAgain) { renderAgain = false; RelayoutInBackground(); }
                    }));
                }
                catch (Exception) { if (art != null) art.Dispose(); }
            });
        }

        private void Swap(Bitmap art, Size layout)
        {
            Bitmap old = backdrop;
            backdrop = art;
            laidOutFor = layout;
            backdropVersion++;
            if (old != null) old.Dispose();
            Invalidate(true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (backdrop == null) { e.Graphics.Clear(Ui.Bg); return; }
            Rectangle r = e.ClipRectangle;
            e.Graphics.CompositingMode = CompositingMode.SourceCopy;
            e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            e.Graphics.DrawImage(backdrop, r, r, GraphicsUnit.Pixel);
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
                relayout.Dispose();
                dragLayout.Dispose();
                if (backdrop != null) { backdrop.Dispose(); backdrop = null; }
            }
            base.Dispose(disposing);
        }
    }

    // Layout containers and labels that paint their own slice of the backdrop in one copy. A standard
    // transparent control instead asks every parent up to the window to repaint the area under it.
    // Moving one changes which slice it shows, so a move repaints it.
    public class GlassTable : TableLayoutPanel
    {
        public GlassTable() { DoubleBuffered = true; BackColor = Color.Transparent; }
        protected override void OnPaintBackground(PaintEventArgs e) { Ui.PaintBackdrop(this, e.Graphics, e.ClipRectangle); }
        protected override void OnLocationChanged(EventArgs e) { base.OnLocationChanged(e); Invalidate(); }
    }

    public class GlassFlow : FlowLayoutPanel
    {
        public GlassFlow() { DoubleBuffered = true; BackColor = Color.Transparent; }
        protected override void OnPaintBackground(PaintEventArgs e) { Ui.PaintBackdrop(this, e.Graphics, e.ClipRectangle); }
        protected override void OnLocationChanged(EventArgs e) { base.OnLocationChanged(e); Invalidate(); }
    }

    public class GlassPanel : Panel
    {
        public GlassPanel() { DoubleBuffered = true; BackColor = Color.Transparent; }
        protected override void OnPaintBackground(PaintEventArgs e) { Ui.PaintBackdrop(this, e.Graphics, e.ClipRectangle); }
        protected override void OnLocationChanged(EventArgs e) { base.OnLocationChanged(e); Invalidate(); }
    }

    public class GlassLabel : Label
    {
        public GlassLabel() { DoubleBuffered = true; BackColor = Color.Transparent; }
        protected override void OnPaintBackground(PaintEventArgs e) { Ui.PaintBackdrop(this, e.Graphics, e.ClipRectangle); }
        protected override void OnLocationChanged(EventArgs e) { base.OnLocationChanged(e); Invalidate(); }
    }

    // 3D icons from Microsoft Fluent Emoji (MIT License, see assets/icons/LICENSE-fluentui-emoji.txt),
    // embedded in the exe. Each size is scaled once with high-quality filtering and kept, so icons are
    // sharp and drawing them is cheap. The artwork is centred in its square, so drawing it into a square
    // centred on a point centres the object.
    internal static class Icons
    {
        public const string Gift = "wrapped_gift", Package = "package", Stopwatch = "stopwatch", Check = "check_mark_button",
            Coin = "coin", MoneyBag = "money_bag", Banknote = "dollar_banknote", MoneyWings = "money_with_wings",
            Gem = "gem_stone", Dagger = "dagger", Trophy = "trophy", Crown = "crown", Star = "star",
            GlowingStar = "glowing_star", Gamepad = "video_game", Magnifier = "magnifying_glass_tilted_left",
            Hourglass = "hourglass_not_done", Fire = "fire";

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Bitmap> Originals = new Dictionary<string, Bitmap>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Bitmap> Sized = new Dictionary<string, Bitmap>(StringComparer.Ordinal);

        public static Bitmap Original(string name)
        {
            lock (Gate)
            {
                Bitmap found;
                if (Originals.TryGetValue(name, out found)) return found;
                try
                {
                    using (Stream s = typeof(Icons).Assembly.GetManifestResourceStream("SkinClubGWFinder.Icons." + name + ".png"))
                        found = s == null ? null : new Bitmap(s);
                }
                catch (Exception ex) { Diag.Swallow("Loading icon " + name, ex); found = null; }
                Originals[name] = found;
                return found;
            }
        }

        public static Bitmap Get(string name, int size)
        {
            size = Math.Max(4, size);
            string key = name + "|" + size;
            lock (Gate)
            {
                Bitmap found;
                if (Sized.TryGetValue(key, out found)) return found;
                Bitmap source = Original(name);
                if (source == null) { Sized[key] = null; return null; }
                found = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(found))
                {
                    g.Clear(Color.Transparent);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    using (ImageAttributes clamp = new ImageAttributes())
                    {
                        clamp.SetWrapMode(WrapMode.TileFlipXY); // no faint border from edge sampling
                        g.DrawImage(source, new Rectangle(0, 0, size, size), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, clamp);
                    }
                }
                Sized[key] = found;
                return found;
            }
        }

        // Draws the icon in a size x size square centred on (cx, cy), at whole pixels so it stays sharp.
        public static void DrawCentered(Graphics g, string name, float cx, float cy, int size)
        {
            Bitmap icon = Get(name, size);
            if (icon == null) return;
            int x = (int)Math.Round(cx - size / 2f), y = (int)Math.Round(cy - size / 2f);
            GraphicsState state = g.Save();
            g.CompositingMode = CompositingMode.SourceOver;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(icon, new Rectangle(x, y, size, size), 0, 0, size, size, GraphicsUnit.Pixel);
            g.Restore(state);
        }

        // Faint, rotated, partly desaturated copy for the background art.
        public static void DrawFaded(Graphics g, string name, float cx, float cy, int size, float angle, float opacity, float saturation)
        {
            Bitmap icon = Get(name, size);
            if (icon == null) return;
            float s = saturation, rw = 0.3086f * (1 - s), gw = 0.6094f * (1 - s), bw = 0.0820f * (1 - s);
            ColorMatrix m = new ColorMatrix(new float[][]
            {
                new float[] { rw + s, rw, rw, 0, 0 },
                new float[] { gw, gw + s, gw, 0, 0 },
                new float[] { bw, bw, bw + s, 0, 0 },
                new float[] { 0, 0, 0, opacity, 0 },
                new float[] { 0, 0, 0, 0, 1 }
            });
            GraphicsState state = g.Save();
            g.TranslateTransform(cx, cy);
            if (angle != 0) g.RotateTransform(angle);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using (ImageAttributes attributes = new ImageAttributes())
            {
                attributes.SetColorMatrix(m);
                g.DrawImage(icon, new Rectangle(-size / 2, -size / 2, size, size), 0, 0, size, size, GraphicsUnit.Pixel, attributes);
            }
            g.Restore(state);
        }
    }

    internal static class BackdropArt
    {
        private static readonly string[] Scatter =
        {
            Icons.Coin, Icons.MoneyBag, Icons.Banknote, Icons.Gem, Icons.Dagger, Icons.Package, Icons.Gift,
            Icons.Star, Icons.Trophy, Icons.Crown, Icons.MoneyWings, Icons.Coin, Icons.Gamepad, Icons.Gem
        };

        // size is the bitmap size; view is the visible window area the composition is laid out for.
        public static Bitmap Render(Size size, Size view, bool hero)
        {
            int w = Math.Max(1, size.Width), h = Math.Max(1, size.Height);
            int vw = Math.Max(1, view.Width), vh = Math.Max(1, view.Height);
            Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                using (LinearGradientBrush b = new LinearGradientBrush(new Rectangle(0, 0, w, h), Color.FromArgb(24, 19, 52), Color.FromArgb(11, 9, 28), 90F))
                    g.FillRectangle(b, 0, 0, w, h);

                float big = Math.Max(vw, vh);
                Glow(g, new PointF(vw * 0.10f, -vh * 0.08f), big * 0.55f, Color.FromArgb(255, 61, 143), hero ? 78 : 56);
                Glow(g, new PointF(vw * 1.0f, vh * 1.05f), big * 0.62f, Color.FromArgb(34, 3, 251), hero ? 105 : 80);
                Glow(g, new PointF(vw * 0.70f, vh * 0.12f), big * 0.22f, Color.FromArgb(0, 200, 255), 26);
                Glow(g, new PointF(vw * 0.30f, vh * 0.95f), big * 0.25f, Color.FromArgb(255, 0, 94), 30);

                // Light beams, as on the site's section dividers.
                Beam(g, vw * 0.18f, vh, Color.FromArgb(255, 51, 200), 24);
                Beam(g, vw * 0.52f, vh, Color.FromArgb(0, 229, 255), 12);
                Beam(g, vw * 0.83f, vh, Color.FromArgb(255, 51, 200), 16);

                // Faint scattered 3D coins, money bags, cash, gems, knives, cases and gifts. Each grid cell picks
                // from its own position, so the pattern stays put when the window is resized.
                const int cell = 150;
                for (int iy = -1; iy * cell < h + cell; iy++)
                {
                    for (int ix = -1; ix * cell < w + cell; ix++)
                    {
                        Random r = new Random(unchecked(ix * 73856093 ^ iy * 19349663 ^ 0x5C1B));
                        if (r.NextDouble() < 0.22) continue;
                        float x = ix * cell + (iy % 2 == 0 ? 0 : cell / 2) + r.Next(-26, 26);
                        float y = iy * cell + r.Next(-26, 26);
                        string icon = Scatter[r.Next(Scatter.Length)];
                        int px = 34 + r.Next(26);
                        float opacity = (hero ? 0.13f : 0.10f) + (float)r.NextDouble() * 0.06f;
                        Icons.DrawFaded(g, icon, x, y, px, r.Next(-28, 28), opacity, 0.55f);
                    }
                }

                if (hero)
                {
                    // A larger pile of loot bottom-right, glowing faintly behind the table.
                    PointF at = new PointF(vw * 0.87f, vh * 0.80f);
                    Glow(g, at, 240, Color.FromArgb(255, 51, 200), 50);
                    Icons.DrawFaded(g, Icons.Gift, at.X, at.Y, 150, -8, 0.30f, 0.85f);
                    Icons.DrawFaded(g, Icons.Dagger, at.X - 60, at.Y - 150, 96, -20, 0.26f, 0.8f);
                    Icons.DrawFaded(g, Icons.Coin, at.X - 170, at.Y + 30, 64, 12, 0.30f, 0.85f);
                    Icons.DrawFaded(g, Icons.Coin, at.X - 125, at.Y - 85, 46, -16, 0.26f, 0.85f);
                    Icons.DrawFaded(g, Icons.Gem, at.X + 105, at.Y - 155, 58, 10, 0.28f, 0.85f);
                    Icons.DrawFaded(g, Icons.MoneyBag, vw * 0.07f, vh * 0.86f, 92, 8, 0.22f, 0.8f);
                }

                // Darken the edges a little so the content in the middle stands out.
                using (GraphicsPath vignette = new GraphicsPath())
                {
                    vignette.AddEllipse(-vw * 0.25f, -vh * 0.35f, vw * 1.5f, vh * 1.7f);
                    using (PathGradientBrush b = new PathGradientBrush(vignette))
                    {
                        b.CenterColor = Color.FromArgb(0, 0, 0, 0);
                        b.SurroundColors = new Color[] { Color.FromArgb(120, 5, 4, 14) };
                        b.FocusScales = new PointF(0.72f, 0.72f);
                        using (Region outside = new Region(new Rectangle(0, 0, w, h)))
                        {
                            g.FillPath(b, vignette);
                            outside.Exclude(vignette);
                            using (SolidBrush edge = new SolidBrush(Color.FromArgb(120, 5, 4, 14))) g.FillRegion(edge, outside);
                        }
                    }
                }
            }
            return bmp;
        }

        public static void Glow(Graphics g, PointF center, float radius, Color color, int alpha)
        {
            if (radius < 2) return;
            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddEllipse(center.X - radius, center.Y - radius, radius * 2, radius * 2);
                using (PathGradientBrush b = new PathGradientBrush(path))
                {
                    b.CenterPoint = center;
                    b.CenterColor = Color.FromArgb(Math.Min(255, alpha), color);
                    b.SurroundColors = new Color[] { Color.FromArgb(0, color) };
                    Blend blend = new Blend();
                    blend.Positions = new float[] { 0f, 0.35f, 0.7f, 1f };
                    blend.Factors = new float[] { 0f, 0.22f, 0.62f, 1f };
                    b.Blend = blend;
                    g.FillPath(b, path);
                }
            }
        }

        private static void Beam(Graphics g, float x, float height, Color color, int alpha)
        {
            GraphicsState state = g.Save();
            g.TranslateTransform(x, height * 0.45f);
            g.RotateTransform(24);
            RectangleF beam = new RectangleF(-30, -height, 60, height * 2.2f);
            using (LinearGradientBrush b = new LinearGradientBrush(beam, Color.FromArgb(0, color), Color.FromArgb(0, color), LinearGradientMode.Horizontal))
            {
                ColorBlend cb = new ColorBlend();
                cb.Colors = new Color[] { Color.FromArgb(0, color), Color.FromArgb(alpha, color), Color.FromArgb(0, color) };
                cb.Positions = new float[] { 0f, 0.5f, 1f };
                b.InterpolationColors = cb;
                g.FillRectangle(b, beam);
            }
            g.Restore(state);
        }
    }

    // The app's mark: a 3D gift (a giveaway) on a dark rounded tile with a pink-to-blue rim and a soft
    // pink glow. Small sizes (taskbar, title bar) use the gift alone, which stays readable.
    internal static class Brand
    {
        public static void DrawMark(Graphics g, Rectangle r, bool tile)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            Bitmap gift = Icons.Original(Icons.Gift);
            if (!tile)
            {
                if (gift != null) g.DrawImage(gift, r);
                return;
            }
            int radius = Math.Max(3, r.Width * 22 / 100);
            float rim = Math.Max(1f, r.Width / 28f);
            RectangleF inner = new RectangleF(r.X + rim / 2, r.Y + rim / 2, r.Width - rim, r.Height - rim);
            using (GraphicsPath shape = Ui.RoundPath(Rectangle.Round(inner), radius))
            {
                using (LinearGradientBrush fill = new LinearGradientBrush(r, Color.FromArgb(58, 22, 104), Color.FromArgb(16, 13, 40), 60F))
                    g.FillPath(fill, shape);
                GraphicsState clip = g.Save();
                g.SetClip(shape);
                BackdropArt.Glow(g, new PointF(r.X + r.Width * 0.5f, r.Y + r.Height * 0.62f), r.Width * 0.62f, Ui.Pink, 150);
                g.Restore(clip);
                using (LinearGradientBrush edge = new LinearGradientBrush(r, Ui.Pink, Ui.Violet, 45F))
                using (Pen pen = new Pen(edge, rim))
                    g.DrawPath(pen, shape);
            }
            if (gift != null)
            {
                float giftSize = r.Width * 0.74f;
                g.DrawImage(gift, new RectangleF(r.X + (r.Width - giftSize) / 2f, r.Y + (r.Height - giftSize) / 2f, giftSize, giftSize));
            }
        }
    }

    // Header logo.
    public class BrandMark : Control
    {
        public BrandMark()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Size = new Size(44, 44);
        }

        private readonly PaintCache cache = new PaintCache();

        protected override void OnPaint(PaintEventArgs e)
        {
            cache.Paint(this, e.Graphics, "", delegate(Graphics g)
            {
                Ui.PaintBackdrop(this, g, ClientRectangle);
                Brand.DrawMark(g, new Rectangle(0, 0, Width - 1, Height - 1), true);
            });
        }

        protected override void OnLocationChanged(EventArgs e) { base.OnLocationChanged(e); Invalidate(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) cache.Dispose();
            base.Dispose(disposing);
        }
    }

    // "SkinClub GW Finder" ("GW Finder" in SkinClub pink), centred on the logo, with the version in small
    // muted text on the same baseline. Plain ClearType text, so the letters stay sharp.
    public class BrandTitle : Control
    {
        private readonly Font titleFont = Ui.BoldFont(16F);
        private readonly Font versionFont = new Font("Segoe UI", 9F);
        private readonly string version;
        private const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.Left | TextFormatFlags.Top;

        public BrandTitle(string version)
        {
            this.version = version;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = 44;
            Size title = TextRenderer.MeasureText("SkinClub GW Finder", titleFont, new Size(1000, 60), Flags);
            Size ver = TextRenderer.MeasureText(version, versionFont, new Size(1000, 60), Flags);
            Width = title.Width + 10 + ver.Width + 4;
        }

        private readonly PaintCache cache = new PaintCache();

        protected override void OnPaint(PaintEventArgs e) { cache.Paint(this, e.Graphics, "", Render); }

        protected override void OnLocationChanged(EventArgs e) { base.OnLocationChanged(e); Invalidate(); }

        private void Render(Graphics g)
        {
            Ui.PaintBackdrop(this, g, ClientRectangle);
            Size first = TextRenderer.MeasureText(g, "SkinClub ", titleFont, new Size(1000, 60), Flags);
            Size title = TextRenderer.MeasureText(g, "SkinClub GW Finder", titleFont, new Size(1000, 60), Flags);
            Size ver = TextRenderer.MeasureText(g, version, versionFont, new Size(1000, 60), Flags);
            int top = Math.Max(0, (Height - title.Height) / 2);
            TextRenderer.DrawText(g, "SkinClub ", titleFont, new Point(0, top), Ui.Text, Flags);
            TextRenderer.DrawText(g, "GW Finder", titleFont, new Point(first.Width, top), Color.FromArgb(255, 79, 208), Flags);
            // Same baseline as the title: Segoe UI's descent is about a quarter of the line height.
            int baseline = top + title.Height - title.Height / 4;
            int versionTop = baseline - (ver.Height - ver.Height / 4);
            TextRenderer.DrawText(g, version, versionFont, new Point(title.Width + 10, versionTop), Ui.Muted, Flags);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { titleFont.Dispose(); versionFont.Dispose(); cache.Dispose(); }
            base.Dispose(disposing);
        }
    }

    // One of the summary tiles above the table (Active, Ending soon, Joined, Cheapest entry).
    public class StatCard : Control
    {
        private static readonly Font TitleFont = Ui.DisplayFont(8.5F);
        private static readonly Font ValueFont = Ui.BoldFont(19F);
        private static readonly Font DetailFont = new Font("Segoe UI", 8.5F);
        private const int IconSize = 52;
        private string value = "0", detail = "";
        public string Title { get; set; }
        public Color Accent { get; set; }
        public string Icon { get; set; }

        public StatCard(string title, Color accent, string icon)
        {
            Title = title;
            Accent = accent;
            Icon = icon;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        public void SetValue(string newValue, string newDetail)
        {
            if (newValue == value && newDetail == detail) return;
            value = newValue ?? "";
            detail = newDetail ?? "";
            Invalidate();
        }

        public string Value { get { return value; } }
        public string Detail { get { return detail; } }

        private readonly PaintCache cache = new PaintCache();

        protected override void OnPaint(PaintEventArgs e)
        {
            cache.Paint(this, e.Graphics, value + "|" + detail + "|" + Title + "|" + Accent.ToArgb() + "|" + Icon, Render);
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
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            float iconX = Width - 18 - IconSize / 2f, iconY = Height / 2f;
            using (GraphicsPath card = Ui.ChamferPath(r, 14))
            {
                using (LinearGradientBrush fill = new LinearGradientBrush(r, Color.FromArgb(238, Ui.Surface), Color.FromArgb(228, Ui.Mix(Ui.Surface, Accent, 0.16)), 20F))
                    g.FillPath(fill, card);
                GraphicsState clip = g.Save();
                g.SetClip(card);
                BackdropArt.Glow(g, new PointF(iconX, iconY), 58, Accent, 80);
                g.Restore(clip);
                using (LinearGradientBrush edge = new LinearGradientBrush(r, Color.FromArgb(200, Accent), Color.FromArgb(40, Accent), LinearGradientMode.Horizontal))
                using (Pen pen = new Pen(edge, 1.2F))
                    g.DrawPath(pen, card);
            }
            using (SolidBrush bar = new SolidBrush(Accent)) g.FillRectangle(bar, 0, 16, 3, Height - 32);
            Icons.DrawCentered(g, Icon, iconX, iconY, IconSize);

            // Title, big number and detail as one block, centred vertically, left of the icon.
            const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.Left | TextFormatFlags.Top;
            int textW = Math.Max(10, Width - 18 - IconSize - 30);
            int titleH = TextRenderer.MeasureText(g, "A", TitleFont, new Size(1000, 40), flags).Height;
            int valueH = TextRenderer.MeasureText(g, "0", ValueFont, new Size(1000, 60), flags).Height;
            int detailH = TextRenderer.MeasureText(g, "A", DetailFont, new Size(1000, 40), flags).Height;
            int top = Math.Max(4, (Height - (titleH + valueH + detailH + 2)) / 2);
            TextRenderer.DrawText(g, (Title ?? "").ToUpperInvariant(), TitleFont, new Rectangle(18, top, textW, titleH), Ui.Muted, flags);
            TextRenderer.DrawText(g, value, ValueFont, new Rectangle(17, top + titleH, textW, valueH), Ui.Text, flags);
            TextRenderer.DrawText(g, detail, DetailFont, new Rectangle(18, top + titleH + valueH + 2, textW, detailH), Ui.Mix(Accent, Ui.TextSoft, 0.45), flags);
        }
    }
}

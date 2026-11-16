// The game's look: Bender font, beige text on near-black, the UI sprites of the project (Tools/installer/Assets,
// cropped by extract_ui_sprites.py) drawn as Unity draws them (9-slice borders from the sprites' m_Border).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace EftInstaller
{
    static class Theme
    {
        // colours of the game's UI (DefaultButton label, requirement colours of the trader screens)
        public static readonly Color Text = Color.FromArgb(197, 195, 178);
        public static readonly Color TextDim = Color.FromArgb(128, 127, 116);
        public static readonly Color TextDark = Color.FromArgb(20, 20, 18);
        public static readonly Color Good = Color.FromArgb(84, 193, 255);
        public static readonly Color Bad = Color.FromArgb(196, 0, 0);
        public static readonly Color Window = Color.FromArgb(12, 12, 11);
        public static readonly Color Panel = Color.FromArgb(7, 7, 6);
        public static readonly Color Line = Color.FromArgb(52, 52, 48);
        public static readonly Color LineLight = Color.FromArgb(86, 85, 78);

        public static float Scale = 1f;

        static readonly Dictionary<string, Image> images = new Dictionary<string, Image>();
        static PrivateFontCollection fonts;
        static FontFamily family;
        static readonly Dictionary<string, Font> fontCache = new Dictionary<string, Font>();

        [DllImport("gdi32.dll")]
        static extern IntPtr AddFontMemResourceEx(IntPtr pbFont, uint cbFont, IntPtr pdv, out uint pcFonts);

        public static string FontName { get { return family != null ? family.Name + " (" + (fonts != null ? fonts.Families.Length : 0) + " loaded)" : "-"; } }

        public static int S(int value) { return (int)Math.Round(value * Scale); }

        public static float SF(float value) { return value * Scale; }

        static Stream Resource(string name)
        {
            return Assembly.GetExecutingAssembly().GetManifestResourceStream("Assets." + name);
        }

        public static Image Img(string name)
        {
            Image img;
            if (images.TryGetValue(name, out img)) return img;
            using (Stream s = Resource(name + ".png"))
            {
                img = s != null ? new Bitmap(Image.FromStream(s)) : new Bitmap(1, 1);
            }
            images[name] = img;
            return img;
        }

        public static Icon AppIcon()
        {
            using (Stream s = Resource("app.ico"))
            {
                return s != null ? new Icon(s) : null;
            }
        }

        /// <summary>Bender from the embedded TrueType files, for GDI+ (drawing) and GDI (text boxes).</summary>
        public static void LoadFonts()
        {
            fonts = new PrivateFontCollection();
            foreach (string name in new[] { "Bender.ttf", "Bender-Bold.ttf" })
            {
                using (Stream s = Resource(name))
                {
                    if (s == null) continue;
                    var data = new byte[s.Length];
                    s.Read(data, 0, data.Length);
                    IntPtr mem = Marshal.AllocCoTaskMem(data.Length); // kept for the process' lifetime
                    Marshal.Copy(data, 0, mem, data.Length);
                    fonts.AddMemoryFont(mem, data.Length);
                    uint count;
                    AddFontMemResourceEx(mem, (uint)data.Length, IntPtr.Zero, out count);
                }
            }
            family = fonts.Families.Length > 0 ? fonts.Families[0] : FontFamily.GenericSansSerif;
        }

        public static Font Font(float pixels, bool bold = false)
        {
            string key = pixels + (bold ? "b" : "");
            Font f;
            if (fontCache.TryGetValue(key, out f)) return f;
            FontStyle style = bold && family.IsStyleAvailable(FontStyle.Bold) ? FontStyle.Bold : FontStyle.Regular;
            f = new Font(family, SF(pixels), style, GraphicsUnit.Pixel);
            fontCache[key] = f;
            return f;
        }

        public static void Quality(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
        }

        public static void DrawText(Graphics g, string text, Font font, Color color, RectangleF rect,
            StringAlignment h = StringAlignment.Near, StringAlignment v = StringAlignment.Center, bool wrap = false)
        {
            if (string.IsNullOrEmpty(text)) return;
            using (var format = new StringFormat(wrap ? 0 : StringFormatFlags.NoWrap))
            using (var brush = new SolidBrush(color))
            {
                format.Alignment = h;
                format.LineAlignment = v;
                format.Trimming = StringTrimming.EllipsisCharacter;
                g.DrawString(text, font, brush, rect, format);
            }
        }

        public static SizeF Measure(Graphics g, string text, Font font, float width)
        {
            using (var format = new StringFormat())
            {
                return g.MeasureString(text ?? "", font, new SizeF(width, 100000), format);
            }
        }

        static ImageAttributes Attributes(Color? tint, float alpha)
        {
            var attributes = new ImageAttributes();
            attributes.SetWrapMode(WrapMode.TileFlipXY); // no bleeding at the slice edges
            if (tint.HasValue || alpha < 1f)
            {
                Color c = tint ?? Color.White;
                var matrix = new ColorMatrix(new[]
                {
                    new float[] { c.R / 255f, 0, 0, 0, 0 },
                    new float[] { 0, c.G / 255f, 0, 0, 0 },
                    new float[] { 0, 0, c.B / 255f, 0, 0 },
                    new float[] { 0, 0, 0, alpha, 0 },
                    new float[] { 0, 0, 0, 0, 1 },
                });
                attributes.SetColorMatrix(matrix);
            }
            return attributes;
        }

        public static void Draw(Graphics g, Image img, Rectangle dst, Color? tint = null, float alpha = 1f)
        {
            using (ImageAttributes a = Attributes(tint, alpha))
            {
                g.DrawImage(img, dst, 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, a);
            }
        }

        /// <summary>9-slice like a Unity Image of type Sliced; border = Unity's m_Border (left, bottom, right, top).</summary>
        public static void Sliced(Graphics g, Image img, Rectangle dst, int left, int bottom, int right, int top, Color? tint = null, float alpha = 1f)
        {
            int dl = S(left), db = S(bottom), dr = S(right), dt = S(top);
            if (dl + dr > dst.Width) { dl = dst.Width * left / Math.Max(1, left + right); dr = dst.Width - dl; }
            if (dt + db > dst.Height) { dt = dst.Height * top / Math.Max(1, top + bottom); db = dst.Height - dt; }
            int[] sx = { 0, left, img.Width - right, img.Width };
            int[] sy = { 0, top, img.Height - bottom, img.Height };
            int[] dx = { dst.Left, dst.Left + dl, dst.Right - dr, dst.Right };
            int[] dy = { dst.Top, dst.Top + dt, dst.Bottom - db, dst.Bottom };
            using (ImageAttributes a = Attributes(tint, alpha))
            {
                for (int y = 0; y < 3; y++)
                    for (int x = 0; x < 3; x++)
                    {
                        int sw = sx[x + 1] - sx[x], sh = sy[y + 1] - sy[y];
                        int dw = dx[x + 1] - dx[x], dh = dy[y + 1] - dy[y];
                        if (sw <= 0 || sh <= 0 || dw <= 0 || dh <= 0) continue;
                        g.DrawImage(img, new Rectangle(dx[x], dy[y], dw, dh), sx[x], sy[y], sw, sh, GraphicsUnit.Pixel, a);
                    }
            }
        }

        /// <summary>Image scaled to cover the rectangle (cropped), like a background.</summary>
        public static void Cover(Graphics g, Image img, Rectangle dst, float alpha = 1f)
        {
            float scale = Math.Max(dst.Width / (float)img.Width, dst.Height / (float)img.Height);
            float w = dst.Width / scale, h = dst.Height / scale;
            var src = new RectangleF((img.Width - w) / 2, (img.Height - h) * 0.4f, w, h);
            using (ImageAttributes a = Attributes(null, alpha))
            {
                g.DrawImage(img, dst, src.X, src.Y, src.Width, src.Height, GraphicsUnit.Pixel, a);
            }
        }

        public static void Frame(Graphics g, Rectangle r, Color color)
        {
            using (var pen = new Pen(color))
            {
                g.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
            }
        }
    }
}

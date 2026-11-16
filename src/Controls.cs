// Owner-drawn controls in the game's style.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace EftInstaller
{
    class TkControl : Control
    {
        protected bool hover, pressed;

        public TkControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
    }

    /// <summary>The game's DefaultButton: caption only, the worn beige plate (mainbutton_idle) with dark text on hover.
    /// Primary = plate always shown, with the main menu's red glow on hover.</summary>
    sealed class TkButton : TkControl
    {
        public bool Primary;

        public TkButton(string text, bool primary = false)
        {
            Text = text;
            Primary = primary;
            Cursor = Cursors.Hand;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Quality(g);
            var r = new Rectangle(0, 0, Width, Height);
            bool lit = Enabled && (Primary || hover);
            if (Primary && Enabled && hover)
            {
                Image glow = Theme.Img("mainbutton_redglow");
                Theme.Draw(g, glow, Rectangle.Inflate(r, -Width / 40, -Height / 12), null, 0.85f);
            }
            if (lit)
            {
                Rectangle plate = pressed ? Rectangle.Inflate(r, -1, -1) : r;
                Theme.Draw(g, Theme.Img("mainbutton_idle"), plate, (Primary && !hover) ? Color.FromArgb(225, 222, 205) : (Color?)null);
            }
            else
            {
                Theme.Frame(g, r, Enabled ? Theme.Line : Color.FromArgb(32, 32, 30));
            }
            Color color = !Enabled ? Color.FromArgb(70, 70, 66) : lit ? Theme.TextDark : Theme.Text;
            Theme.DrawText(g, Text.ToUpperInvariant(), Theme.Font(Primary ? 19 : 15, Primary), color, r, StringAlignment.Center);
        }
    }

    /// <summary>A tab like the game's tab_button: beige when selected.</summary>
    sealed class TkTab : TkControl
    {
        public bool Selected;

        public TkTab(string text)
        {
            Text = text;
            Cursor = Cursors.Hand;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Quality(g);
            var r = new Rectangle(0, 0, Width, Height);
            if (Selected) Theme.Draw(g, Theme.Img("tab_button"), r);
            else
            {
                using (var b = new SolidBrush(hover ? Color.FromArgb(34, 34, 31) : Color.FromArgb(20, 20, 18))) g.FillRectangle(b, r);
                Theme.Frame(g, r, Theme.Line);
            }
            Theme.DrawText(g, Text.ToUpperInvariant(), Theme.Font(17, true), Selected ? Theme.TextDark : (hover ? Color.White : Theme.Text), r, StringAlignment.Center);
        }
    }

    /// <summary>The game's checkbox (checkbox_empty + checkbox_checked) with a caption.</summary>
    sealed class TkCheck : TkControl
    {
        bool isChecked;
        public event EventHandler CheckedChanged = delegate { };

        public bool Checked
        {
            get { return isChecked; }
            set { if (isChecked != value) { isChecked = value; Invalidate(); CheckedChanged(this, EventArgs.Empty); } }
        }

        public TkCheck(string text)
        {
            Text = text;
            Cursor = Cursors.Hand;
        }

        protected override void OnClick(EventArgs e)
        {
            if (Enabled) Checked = !Checked;
            base.OnClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Quality(g);
            int box = Theme.S(20);
            var r = new Rectangle(0, (Height - box) / 2, box, box);
            Theme.Draw(g, Theme.Img("checkbox_empty"), r, hover ? Color.White : Theme.Text);
            if (isChecked)
            {
                Image check = Theme.Img("checkbox_checked");
                int w = Theme.S(check.Width), h = Theme.S(check.Height);
                Theme.Draw(g, check, new Rectangle(r.X + (box - w) / 2, r.Y + (box - h) / 2, w, h), Theme.Text);
            }
            Theme.DrawText(g, Text, Theme.Font(15), Enabled ? Theme.Text : Theme.TextDim, new RectangleF(box + Theme.S(10), 0, Width - box - Theme.S(10), Height));
        }
    }

    /// <summary>The game's input_box (sliced) around a borderless TextBox in Bender.</summary>
    sealed class TkInput : TkControl
    {
        public readonly TextBox Box = new TextBox();

        public TkInput()
        {
            Box.BorderStyle = BorderStyle.None;
            Box.BackColor = Color.FromArgb(10, 10, 9);
            Box.ForeColor = Theme.Text;
            Box.Font = Theme.Font(16);
            Controls.Add(Box);
        }

        public override string Text { get { return Box.Text; } set { Box.Text = value; } }

        protected override void OnLayout(LayoutEventArgs e)
        {
            int pad = Theme.S(12);
            Box.SetBounds(pad, (Height - Box.PreferredHeight) / 2, Width - 2 * pad, Box.PreferredHeight);
            base.OnLayout(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Quality(g);
            using (var b = new SolidBrush(Box.BackColor)) g.FillRectangle(b, ClientRectangle);
            Theme.Sliced(g, Theme.Img("input_box"), ClientRectangle, 21, 21, 21, 21);
        }
    }

    /// <summary>Progress bar from the experience bar of the character screen: experience_bar_background (sliced)
    /// with the experience_bar_gradient fill tinted beige.</summary>
    sealed class TkProgress : TkControl
    {
        double fraction = -1;
        string caption = "";

        public void Set(double value, string text)
        {
            if (Math.Abs(value - fraction) < 0.0005 && text == caption) return;
            fraction = value;
            caption = text ?? "";
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Quality(g);
            var r = new Rectangle(0, 0, Width, Height);
            using (var b = new SolidBrush(Color.FromArgb(6, 6, 5))) g.FillRectangle(b, Rectangle.Inflate(r, -2, -2));
            // the frame's middle is opaque: frame first, fill over it
            Theme.Sliced(g, Theme.Img("experience_bar_background"), r, 4, 4, 4, 4);
            int inset = Theme.S(3);
            var inner = Rectangle.Inflate(r, -inset, -inset);
            if (fraction > 0)
            {
                var fill = new Rectangle(inner.X, inner.Y, (int)(inner.Width * fraction), inner.Height);
                if (fill.Width > 0)
                {
                    using (var b = new SolidBrush(Color.FromArgb(197, 194, 164))) g.FillRectangle(b, fill);
                    // the experience bar's gradient as shading, the level bar's shine on the upper half
                    Theme.Draw(g, Theme.Img("experience_bar_gradient"), fill, null, 0.45f);
                    Theme.Draw(g, Theme.Img("level_fill_bar_current"), new Rectangle(fill.X, fill.Y, fill.Width, fill.Height / 2), null, 0.3f);
                }
            }
            if (caption.Length > 0)
            {
                // text dark over the fill, light over the empty part
                Font font = Theme.Font(14, true);
                int split = fraction > 0 ? inner.X + (int)(inner.Width * fraction) : 0;
                Region clip = g.Clip;
                g.SetClip(new Rectangle(0, 0, split, Height));
                Theme.DrawText(g, caption, font, Theme.TextDark, r, StringAlignment.Center);
                g.SetClip(new Rectangle(split, 0, Width - split, Height));
                Theme.DrawText(g, caption, font, Theme.Text, r, StringAlignment.Center);
                g.Clip = clip;
            }
        }
    }

    sealed class TkLabel : TkControl
    {
        public float FontSize = 15;
        public bool Bold;
        public Color Color = Theme.Text;
        public StringAlignment Align = StringAlignment.Near;
        public bool Wrap { get; set; }

        public TkLabel(string text = "", float size = 15, bool bold = false)
        {
            Text = text;
            FontSize = size;
            Bold = bold;
        }

        public void Set(string text, Color color)
        {
            if (text == Text && color == Color) return;
            Color = color;
            Text = text;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme.Quality(e.Graphics);
            Theme.DrawText(e.Graphics, Text, Theme.Font(FontSize, Bold), Color, ClientRectangle, Align, Wrap ? StringAlignment.Near : StringAlignment.Center, Wrap);
        }
    }

    sealed class TkBlock
    {
        public string Title, Date, Body, Url;
    }

    /// <summary>A scrolling list of titled text blocks (news, release notes) with the game's scroll bar
    /// (slider_background + scroller_button_drag_vertical).</summary>
    sealed class TkFeed : TkControl
    {
        List<TkBlock> blocks = new List<TkBlock>();
        string empty = "";
        int scroll, contentHeight;
        readonly List<KeyValuePair<Rectangle, string>> links = new List<KeyValuePair<Rectangle, string>>();
        bool dragging;
        int dragStart, dragScroll;

        public TkFeed()
        {
            SetStyle(ControlStyles.Selectable, true);
        }

        public void SetBlocks(List<TkBlock> list, string emptyText)
        {
            blocks = list ?? new List<TkBlock>();
            empty = emptyText ?? "";
            scroll = 0;
            Invalidate();
        }

        int Pad { get { return Theme.S(16); } }
        int BarWidth { get { return Theme.S(11); } }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            ScrollTo(scroll - e.Delta / 120 * Theme.S(48));
            base.OnMouseWheel(e);
        }

        void ScrollTo(int value)
        {
            int max = Math.Max(0, contentHeight - Height);
            int v = Math.Max(0, Math.Min(max, value));
            if (v != scroll) { scroll = v; Invalidate(); }
        }

        Rectangle Thumb()
        {
            if (contentHeight <= Height) return Rectangle.Empty;
            int track = Height - Theme.S(8);
            int h = Math.Max(Theme.S(30), track * Height / contentHeight);
            int y = Theme.S(4) + (track - h) * scroll / Math.Max(1, contentHeight - Height);
            return new Rectangle(Width - BarWidth - Theme.S(4), y, BarWidth, h);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            Rectangle thumb = Thumb();
            if (!thumb.IsEmpty && e.X >= thumb.X - 4)
            {
                if (thumb.Contains(e.Location)) { dragging = true; dragStart = e.Y; dragScroll = scroll; }
                else ScrollTo(scroll + (e.Y < thumb.Y ? -Height : Height) * 9 / 10);
                return;
            }
            foreach (var link in links)
                if (link.Key.Contains(e.Location))
                {
                    try { Process.Start(link.Value); } catch (Exception) { }
                    return;
                }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragging)
            {
                int track = Height - Theme.S(8) - Thumb().Height;
                if (track > 0) ScrollTo(dragScroll + (e.Y - dragStart) * (contentHeight - Height) / track);
            }
            bool overLink = false;
            foreach (var link in links) if (link.Key.Contains(e.Location)) overLink = true;
            Cursor = overLink ? Cursors.Hand : Cursors.Default;
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            dragging = false;
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Quality(g);
            var r = new Rectangle(0, 0, Width, Height);
            using (var b = new SolidBrush(Theme.Panel)) g.FillRectangle(b, r);
            Theme.Frame(g, r, Theme.Line);
            links.Clear();
            int width = Width - 2 * Pad - BarWidth - Theme.S(6);
            int y = Pad - scroll;
            if (blocks.Count == 0)
            {
                Theme.DrawText(g, empty, Theme.Font(15), Theme.TextDim, new RectangleF(Pad, Pad, width, Height - 2 * Pad), StringAlignment.Near, StringAlignment.Near, true);
                contentHeight = 0;
                return;
            }
            g.SetClip(Rectangle.Inflate(r, -1, -1));
            for (int i = 0; i < blocks.Count; i++)
            {
                TkBlock block = blocks[i];
                if (i > 0)
                {
                    using (var pen = new Pen(Theme.Line)) g.DrawLine(pen, Pad, y - Theme.S(10), Pad + width, y - Theme.S(10));
                }
                if (!string.IsNullOrEmpty(block.Title))
                {
                    Font title = Theme.Font(19, true);
                    SizeF size = Theme.Measure(g, block.Title, title, width);
                    Theme.DrawText(g, block.Title, title, Color.White, new RectangleF(Pad, y, width, size.Height + 2), StringAlignment.Near, StringAlignment.Near, true);
                    y += (int)size.Height + Theme.S(2);
                }
                if (!string.IsNullOrEmpty(block.Date))
                {
                    Theme.DrawText(g, block.Date, Theme.Font(13), Theme.TextDim, new RectangleF(Pad, y, width, Theme.S(18)));
                    y += Theme.S(22);
                }
                if (!string.IsNullOrEmpty(block.Body))
                {
                    Font body = Theme.Font(15);
                    SizeF size = Theme.Measure(g, block.Body, body, width);
                    Theme.DrawText(g, block.Body, body, Theme.Text, new RectangleF(Pad, y, width, size.Height + 4), StringAlignment.Near, StringAlignment.Near, true);
                    y += (int)size.Height + Theme.S(6);
                }
                if (!string.IsNullOrEmpty(block.Url))
                {
                    var rect = new Rectangle(Pad, y, Theme.S(120), Theme.S(20));
                    Theme.DrawText(g, "READ MORE  >", Theme.Font(14, true), Theme.Good, rect);
                    links.Add(new KeyValuePair<Rectangle, string>(rect, block.Url));
                    y += Theme.S(24);
                }
                y += Theme.S(22);
            }
            contentHeight = y + scroll - Theme.S(10);
            g.ResetClip();
            Rectangle thumb = Thumb();
            if (!thumb.IsEmpty)
            {
                Theme.Sliced(g, Theme.Img("slider_background"), new Rectangle(thumb.X, Theme.S(4), BarWidth, Height - Theme.S(8)), 0, 43, 0, 42, null, 0.8f);
                using (var b = new SolidBrush(dragging ? Color.White : Theme.LineLight)) g.FillRectangle(b, Rectangle.Inflate(thumb, -Theme.S(3), 0));
            }
        }
    }
}

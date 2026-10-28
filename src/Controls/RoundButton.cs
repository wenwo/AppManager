using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using AppManager;

namespace AppManager.Controls
{
    /// <summary>
    /// 圆角按钮：扁平蓝色主按钮与中性灰次按钮，提供清晰的悬停/按下反馈。
    /// </summary>
    public class RoundButton : Control
    {
        private bool _accent;
        private bool _hover;
        private bool _pressed;
        private UiIconKind? _icon;

        public RoundButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.UserPaint
                | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Font = Theme.ButtonFont;
            Cursor = Cursors.Hand;
            Size = new Size(90, 34);
        }

        [Category("外观")]
        public bool Accent { get { return _accent; } set { _accent = value; Invalidate(); } }

        [Category("外观")]
        public UiIconKind? Icon { get { return _icon; } set { _icon = value; Invalidate(); } }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { _pressed = true; Invalidate(); }
            base.OnMouseDown(e);
        }
        protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = ClientRectangle;
            r.Inflate(-1, -1);
            if (_accent)
            {
                Color bg = Theme.Accent;
                if (_pressed) bg = Blend(bg, Color.Black, 0.12f);
                else if (_hover) bg = Blend(bg, Color.White, 0.10f);
                using (SolidBrush b = new SolidBrush(bg)) Draw.FillRoundedRect(g, b, r, 6);
            }
            else
            {
                Color bg = Theme.Card;
                if (_pressed) bg = Blend(bg, Color.Black, 0.06f);
                else if (_hover) bg = Theme.CardHover;
                using (SolidBrush b = new SolidBrush(bg)) Draw.FillRoundedRect(g, b, r, 6);
                Color border = _hover ? Blend(Theme.ControlBorder, Theme.Accent, 0.2f) : Theme.ControlBorder;
                using (Pen p = new Pen(border, 1)) Draw.DrawRoundedRect(g, p, r, 6);
            }
            Color tc = _accent ? Color.White : Theme.TextPrimary;
            using (SolidBrush br = new SolidBrush(tc))
            {
                if (!_icon.HasValue)
                {
                    Draw.DrawStringCenter(g, Text, Font, br, r);
                    return;
                }
                Size textSize = TextRenderer.MeasureText(Text, Font);
                int contentWidth = textSize.Width + 22;
                int startX = r.X + Math.Max(8, (r.Width - contentWidth) / 2);
                Rectangle iconRect = new Rectangle(startX, r.Y + (r.Height - 18) / 2, 18, 18);
                Draw.DrawIcon(g, _icon.Value, iconRect, tc);
                Rectangle textRect = new Rectangle(startX + 22, r.Y,
                    Math.Max(0, r.Right - startX - 26), r.Height);
                Draw.DrawStringLeft(g, Text, Font, br, textRect);
            }
        }

        private static Color Blend(Color a, Color b, float t)
        {
            int r = Clamp(a.R + (int)((b.R - a.R) * t));
            int gg = Clamp(a.G + (int)((b.G - a.G) * t));
            int bb = Clamp(a.B + (int)((b.B - a.B) * t));
            return Color.FromArgb(r, gg, bb);
        }

        private static int Clamp(int v) { return v < 0 ? 0 : (v > 255 ? 255 : v); }
    }
}

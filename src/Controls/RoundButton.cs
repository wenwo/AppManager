using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using AppManager;

namespace AppManager.Controls
{
    /// <summary>
    /// 圆角按钮：iOS 风格，强调/普通两态，悬停/按下反馈。
    /// </summary>
    public class RoundButton : Control
    {
        private bool _accent;
        private bool _hover;
        private bool _pressed;

        public RoundButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.UserPaint
                | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Font = Theme.FontOf(9F);
            Cursor = Cursors.Hand;
            Size = new Size(90, 34);
        }

        [Category("外观")]
        public bool Accent { get { return _accent; } set { _accent = value; Invalidate(); } }

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
            // 按下时整体下移 1px，模拟物理按压
            if (_pressed) r.Offset(0, 1);

            if (_accent)
            {
                // iOS 强调按钮：纵向渐变 + 底部深色边，增加体积感
                Color top = Blend(Theme.Accent, Color.White, 0.12f);
                Color bottom = Theme.Accent;
                if (_hover) { top = Blend(top, Color.White, 0.08f); bottom = Blend(bottom, Color.White, 0.08f); }
                if (_pressed) { top = Blend(top, Color.Black, 0.15f); bottom = Blend(bottom, Color.Black, 0.15f); }
                using (LinearGradientBrush gb = new LinearGradientBrush(r, top, bottom, LinearGradientMode.Vertical))
                    Draw.FillRoundedRect(g, gb, r, 10);
                using (Pen p = new Pen(Blend(Theme.Accent, Color.Black, 0.18f), 1))
                    Draw.DrawRoundedRect(g, p, r, 10);
            }
            else
            {
                Color bg = Theme.Card;
                if (_pressed) bg = Blend(bg, Color.Black, 0.08f);
                else if (_hover) bg = Blend(bg, Theme.Accent, 0.06f);
                using (SolidBrush b = new SolidBrush(bg)) Draw.FillRoundedRect(g, b, r, 10);
                Color border = _hover ? Blend(Theme.Separator, Theme.Accent, 0.3f) : Theme.Separator;
                using (Pen p = new Pen(border, 1)) Draw.DrawRoundedRect(g, p, r, 10);
            }
            Color tc = _accent ? Color.White : Theme.TextPrimary;
            using (SolidBrush br = new SolidBrush(tc)) Draw.DrawStringCenter(g, Text, Font, br, r);
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

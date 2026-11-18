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
        private bool _danger;
        private bool _dropDown;
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
            Size = new Size(90, 34);
        }

        [Category("外观")]
        public bool Accent { get { return _accent; } set { _accent = value; Invalidate(); } }

        /// <summary>危险操作的主按钮（删除等不可撤回的动作）：红色实底，优先于 Accent。</summary>
        [Category("外观")]
        public bool Danger { get { return _danger; } set { _danger = value; Invalidate(); } }

        /// <summary>点了会弹出菜单（而不是直接执行动作）的按钮：文字右侧画一个向下的小箭头。</summary>
        [Category("外观")]
        public bool DropDown { get { return _dropDown; } set { _dropDown = value; Invalidate(); } }

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
        protected override void OnLostFocus(EventArgs e) { _pressed = false; Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space)
            {
                _pressed = true;
                Invalidate();
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }
        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space)
            {
                e.Handled = true;
                if (_pressed)
                {
                    _pressed = false;
                    Invalidate();
                    OnClick(EventArgs.Empty);
                }
                return;
            }
            base.OnKeyUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = ClientRectangle;
            r.Inflate(-1, -1);
            int radius = DpiHelper.Scale(Theme.RadiusControl, DpiHelper.ForControl(this));
            // 带下拉箭头时，文字和图标在箭头左边的区域里居中
            Rectangle content = r;
            if (_dropDown) content.Width = Math.Max(0, content.Width - DpiHelper.Scale(14, DpiHelper.ForControl(this)));
            if (!Enabled)
            {
                // 停用：灰底、浅字，主按钮也一样，一眼能看出现在点不了
                using (SolidBrush b = new SolidBrush(Theme.SegmentBg)) Draw.FillRoundedRect(g, b, r, radius);
                using (SolidBrush br = new SolidBrush(Theme.TextTertiary))
                {
                    if (_icon.HasValue) PaintIconAndText(g, content, br, Theme.TextTertiary);
                    else Draw.DrawStringCenter(g, Text, Font, br, content);
                }
                if (_dropDown) PaintDropDownArrow(g, r, Theme.TextTertiary);
                return;
            }
            if (_danger)
            {
                Color bg = _pressed ? Theme.DangerPressed : (_hover ? Theme.DangerHover : Theme.DeleteRed);
                using (SolidBrush b = new SolidBrush(bg)) Draw.FillRoundedRect(g, b, r, radius);
            }
            else if (_accent)
            {
                Color bg = _pressed ? Theme.AccentPressed : (_hover ? Theme.AccentHover : Theme.Accent);
                using (SolidBrush b = new SolidBrush(bg)) Draw.FillRoundedRect(g, b, r, radius);
            }
            else
            {
                Color bg = Theme.Card;
                if (_pressed) bg = Blend(bg, Color.Black, 0.06f);
                else if (_hover) bg = Theme.CardHover;
                using (SolidBrush b = new SolidBrush(bg)) Draw.FillRoundedRect(g, b, r, radius);
                Color border = _hover ? Blend(Theme.ControlBorder, Theme.Accent, 0.2f) : Theme.ControlBorder;
                using (Pen p = new Pen(border, 1)) Draw.DrawRoundedRect(g, p, r, radius);
            }
            Color tc = _accent || _danger ? Color.White : Theme.TextPrimary;
            using (SolidBrush br = new SolidBrush(tc))
            {
                if (_icon.HasValue) PaintIconAndText(g, content, br, tc);
                else Draw.DrawStringCenter(g, Text, Font, br, content);
            }
            if (_dropDown) PaintDropDownArrow(g, r, _accent || _danger ? Color.White : Theme.TextSecondary);
        }

        /// <summary>按钮右侧向下的小箭头：表示点了会弹出菜单。</summary>
        private void PaintDropDownArrow(Graphics g, Rectangle r, Color color)
        {
            int dpi = DpiHelper.ForControl(this);
            float cx = r.Right - DpiHelper.Scale(13, dpi), cy = r.Y + r.Height / 2F + DpiHelper.Scale(1, dpi) / 2F;
            float half = DpiHelper.Scale(4, dpi), drop = DpiHelper.Scale(4, dpi) / 2F;
            using (Pen pen = new Pen(color, Math.Max(1.4F, DpiHelper.Scale(3, dpi) / 2F)))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                g.DrawLines(pen, new PointF[]
                {
                    new PointF(cx - half, cy - drop), new PointF(cx, cy + drop), new PointF(cx + half, cy - drop)
                });
            }
        }

        private void PaintIconAndText(Graphics g, Rectangle r, Brush br, Color tc)
        {
            // 图标格和它到文字的间距按 DPI 换算：字随缩放变大，图标也跟着变大，不再是字大图标小
            int dpi = DpiHelper.ForControl(this);
            int icon = DpiHelper.Scale(18, dpi), step = DpiHelper.Scale(22, dpi);
            Size textSize = TextRenderer.MeasureText(Text, Font);
            int contentWidth = textSize.Width + step;
            int startX = r.X + Math.Max(DpiHelper.Scale(8, dpi), (r.Width - contentWidth) / 2);
            Rectangle iconRect = new Rectangle(startX, r.Y + (r.Height - icon) / 2, icon, icon);
            Draw.DrawIcon(g, _icon.Value, iconRect, tc, dpi);
            Rectangle textRect = new Rectangle(startX + step, r.Y,
                Math.Max(0, r.Right - startX - step - DpiHelper.Scale(4, dpi)), r.Height);
            Draw.DrawStringLeft(g, Text, Font, br, textRect);
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

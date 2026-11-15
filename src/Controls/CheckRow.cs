using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using AppManager;

namespace AppManager.Controls
{
    /// <summary>
    /// 自绘的勾选项 / 单选项：左边是方框或圆点，右边是一行文字，整行可点，空格切换。
    /// Radio 为 true 时是单选项：同一个父容器里的单选项互斥，已选中的再点不会取消。
    /// </summary>
    public class CheckRow : Control
    {
        private const int BoxSize = 18;
        private const int TextGap = 8;

        private bool _checked;
        private bool _radio;
        private bool _hover;

        public event EventHandler CheckedChanged;

        public CheckRow()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.Selectable
                | ControlStyles.SupportsTransparentBackColor, true);
            SetStyle(ControlStyles.StandardDoubleClick, false);   // 连点两下算切换两次，不合并成一次双击
            BackColor = Color.Transparent;
            ForeColor = Theme.TextPrimary;
            Font = Theme.BodyFont;
            Size = new Size(200, 24);
            TabStop = true;
            AccessibleRole = AccessibleRole.CheckButton;
        }

        [Category("外观")]
        public bool Radio
        {
            get { return _radio; }
            set
            {
                _radio = value;
                AccessibleRole = value ? AccessibleRole.RadioButton : AccessibleRole.CheckButton;
                TabStop = !value || _checked;   // 与系统单选按钮一致：同组只在选中的那一项上停 Tab
                Invalidate();
            }
        }

        [Category("外观")]
        public bool Checked
        {
            get { return _checked; }
            set
            {
                if (_checked == value) return;
                _checked = value;
                if (_radio) TabStop = value;
                // 单选项选中时，同一父容器里的其它单选项取消选中
                if (value && _radio && Parent != null)
                {
                    foreach (Control sibling in Parent.Controls)
                    {
                        CheckRow other = sibling as CheckRow;
                        if (other != null && other != this && other._radio) other.Checked = false;
                    }
                }
                Invalidate();
                if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            }
        }

        private void Toggle()
        {
            if (_radio) Checked = true;
            else Checked = !_checked;
        }

        protected override void OnClick(EventArgs e)
        {
            // Click 对右键、中键也会触发；系统勾选框只认左键
            MouseEventArgs mouse = e as MouseEventArgs;
            if (mouse != null && mouse.Button != MouseButtons.Left) { base.OnClick(e); return; }
            Focus();
            Toggle();
            base.OnClick(e);
        }

        /// <summary>单选项用方向键在同组内切换，方向键需要送进 OnKeyDown 而不是被窗体拿去移动焦点。</summary>
        protected override bool IsInputKey(Keys keyData)
        {
            if (_radio && (keyData == Keys.Up || keyData == Keys.Down || keyData == Keys.Left || keyData == Keys.Right)) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space && e.Modifiers == Keys.None)
            {
                Toggle();
                e.Handled = true;
            }
            else if (_radio && e.Modifiers == Keys.None && Parent != null
                && (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down || e.KeyCode == Keys.Left || e.KeyCode == Keys.Right))
            {
                // 与系统单选按钮一致：方向键移到同组的上一个 / 下一个并选中它，到头不回绕
                bool forward = e.KeyCode == Keys.Down || e.KeyCode == Keys.Right;
                CheckRow target = null;
                bool passed = false;
                foreach (Control sibling in Parent.Controls)
                {
                    CheckRow other = sibling as CheckRow;
                    if (other == null || !other._radio || !other.Enabled) continue;
                    if (other == this) { passed = true; if (!forward) break; continue; }
                    if (!forward) target = other;
                    else if (passed) { target = other; break; }
                }
                if (target != null)
                {
                    target.Focus();
                    target.Checked = true;
                }
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int dpi = DpiHelper.ForControl(this);
            int box = DpiHelper.Scale(BoxSize, dpi);
            Rectangle mark = new Rectangle(1, (Height - box) / 2, box, box);
            if (_radio) Draw.DrawRadio(g, mark, _checked, Enabled, dpi);
            else Draw.DrawCheckBox(g, mark, _checked, Enabled, dpi);
            // 键盘焦点或鼠标悬停：方框外沿描一圈强调色，说明现在按空格 / 点击会作用在这一项上
            if (Enabled && (Focused || _hover) && !_checked)
            {
                using (Pen ring = new Pen(Theme.Accent, Math.Max(1F, DpiHelper.Scale(3, dpi) / 2F)))
                {
                    Rectangle r = new Rectangle(mark.X, mark.Y, mark.Width - 1, mark.Height - 1);
                    if (_radio) g.DrawEllipse(ring, r);
                    else Draw.DrawRoundedRect(g, ring, r, DpiHelper.Scale(4, dpi));
                }
            }
            int textX = mark.Right + DpiHelper.Scale(TextGap, dpi);
            using (SolidBrush text = new SolidBrush(Enabled ? ForeColor : Theme.TextTertiary))
                Draw.DrawStringLeft(g, Text, Font, text, new Rectangle(textX, 0, Math.Max(0, Width - textX), Height));
        }
    }
}

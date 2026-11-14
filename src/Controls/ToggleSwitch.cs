using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using AppManager;

namespace AppManager.Controls
{
    /// <summary>
    /// iOS 风格开关：点击切换，强调色轨道 + 白色圆形拨钮。
    /// </summary>
    public class ToggleSwitch : Control
    {
        private bool _checked;

        public ToggleSwitch()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.UserPaint
                | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Size = new Size(46, 28);
        }

        public event EventHandler CheckedChanged;

        [Category("外观")]
        public bool Checked
        {
            get { return _checked; }
            set
            {
                if (_checked != value)
                {
                    _checked = value;
                    Invalidate();
                    EventHandler h = CheckedChanged;
                    if (h != null) h(this, EventArgs.Empty);
                }
            }
        }

        protected override void OnClick(EventArgs e)
        {
            Checked = !_checked;
            base.OnClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            PaintSwitch(e.Graphics, ClientRectangle, _checked, Enabled);
        }

        /// <summary>在 bounds 内画一个开关；自绘页面（如设置页）不建子控件时复用同一外观。</summary>
        internal static void PaintSwitch(Graphics g, Rectangle bounds, bool on, bool enabled)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(bounds.X, bounds.Y + 2, bounds.Width - 1, bounds.Height - 4);
            Color track = on ? Theme.ToggleOn : Theme.SegmentBg;
            // 禁用态：轨道向卡片底色淡化一半，提示当前不可操作（仍能看出开/关）
            if (!enabled)
                track = Color.FromArgb((track.R + Theme.Card.R) / 2, (track.G + Theme.Card.G) / 2, (track.B + Theme.Card.B) / 2);
            using (SolidBrush b = new SolidBrush(track)) Draw.FillRoundedRect(g, b, r, r.Height);
            // 轨道内阴影（顶部 1px 深色，模拟凹陷）
            using (Pen ip = new Pen(Color.FromArgb(12, 0, 0, 0), 1))
                g.DrawLine(ip, r.X + 3, r.Y + 1, r.Right - 3, r.Y + 1);
            // 白色拨钮 + 柔和投影（iOS 细腻质感）
            int knob = r.Height - 4;
            int kx = on ? r.Right - knob - 2 : r.X + 2;
            int ky = r.Y + 2;
            Rectangle kr = new Rectangle(kx, ky, knob, knob);
            using (GraphicsPath p = Draw.RoundedPath(kr, knob / 2))
            {
                for (int i = 2; i >= 1; i--)
                {
                    Rectangle sr = Rectangle.Inflate(kr, i, i);
                    sr.Offset(0, 1);
                    using (SolidBrush sb = new SolidBrush(Color.FromArgb(10 + (2 - i) * 8, 0, 0, 0)))
                    using (GraphicsPath sp = Draw.RoundedPath(sr, knob / 2 + i))
                        g.FillPath(sb, sp);
                }
                using (SolidBrush kb = new SolidBrush(Color.White))
                    g.FillPath(kb, p);
            }
        }
    }
}

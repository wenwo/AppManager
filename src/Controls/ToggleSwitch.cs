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
            Cursor = Cursors.Hand;
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
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 2, Width - 1, Height - 4);
            Color track = _checked ? Theme.ToggleGreen : Theme.SegmentBg; // iOS 绿/灰
            using (SolidBrush b = new SolidBrush(track)) Draw.FillRoundedRect(g, b, r, r.Height);
            // 轨道内阴影（顶部 1px 深色，模拟凹陷）
            using (Pen ip = new Pen(Color.FromArgb(12, 0, 0, 0), 1))
                g.DrawLine(ip, r.X + 3, r.Y + 1, r.Right - 3, r.Y + 1);
            // 白色拨钮 + 柔和投影（iOS 细腻质感）
            int knob = r.Height - 4;
            int kx = _checked ? r.Right - knob - 2 : r.X + 2;
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

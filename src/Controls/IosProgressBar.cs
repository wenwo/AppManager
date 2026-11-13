using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using AppManager;

namespace AppManager.Controls
{
    /// <summary>
    /// iOS 风格自绘进度条：圆角灰底轨道 + 强调色填充，替换原生方角 ProgressBar，保持全站视觉统一。
    /// </summary>
    public class IosProgressBar : Control
    {
        private int _minimum;
        private int _maximum = 100;
        private int _value;
        private bool _marquee;
        private Timer _marqueeTimer;

        /// <summary>进度未知时的往复滑块；关闭后按 Value 绘制。定时器只在打开期间存在。</summary>
        public bool Marquee
        {
            get { return _marquee; }
            set
            {
                if (_marquee == value) return;
                _marquee = value;
                if (value)
                {
                    _marqueeTimer = new Timer { Interval = 30 };
                    _marqueeTimer.Tick += delegate { Invalidate(); };
                    _marqueeTimer.Start();
                }
                else StopMarquee();
                Invalidate();
            }
        }

        private void StopMarquee()
        {
            if (_marqueeTimer == null) return;
            _marqueeTimer.Stop();
            _marqueeTimer.Dispose();
            _marqueeTimer = null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) StopMarquee();
            base.Dispose(disposing);
        }

        public IosProgressBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Theme.StatusBarBg;
            Size = new Size(180, 18);
        }

        public int Minimum
        {
            get { return _minimum; }
            set { _minimum = value; if (_maximum < _minimum) _maximum = _minimum; Invalidate(); }
        }

        public int Maximum
        {
            get { return _maximum; }
            set
            {
                _maximum = value <= _minimum ? _minimum + 1 : value;
                if (_value > _maximum) _value = _maximum;
                Invalidate();
            }
        }

        public int Value
        {
            get { return _value; }
            set
            {
                _value = value < _minimum ? _minimum : (value > _maximum ? _maximum : value);
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = ClientRectangle;
            r.Inflate(-1, -1);
            if (r.Width <= 0 || r.Height <= 0) return;
            int radius = r.Height / 2;

            // 轨道：iOS 浅灰圆角
            using (SolidBrush b = new SolidBrush(Theme.SegmentBg))
                Draw.FillRoundedRect(g, b, r, radius);

            if (_marquee)
            {
                // 滑块宽为轨道的 30%，1.4 秒从左侧外滑到右侧外；裁剪到圆角轨道内
                const int period = 1400;
                int segment = Math.Max(r.Height, r.Width * 3 / 10);
                int phase = (int)((uint)Environment.TickCount % period);
                int x = r.X - segment + (int)((long)(r.Width + segment) * phase / period);
                using (GraphicsPath track = Draw.RoundedPath(r, radius))
                using (SolidBrush b = new SolidBrush(Theme.Accent))
                {
                    GraphicsState state = g.Save();
                    g.SetClip(track, CombineMode.Intersect);
                    Draw.FillRoundedRect(g, b, new Rectangle(x, r.Y, segment, r.Height), radius);
                    g.Restore(state);
                }
                return;
            }

            // 填充：强调色，按比例绘制
            int span = _maximum - _minimum;
            if (span > 0 && _value > _minimum)
            {
                int w = (int)((long)r.Width * (_value - _minimum) / span);
                if (w > 0)
                {
                    if (w > r.Width) w = r.Width;
                    Rectangle f = new Rectangle(r.X, r.Y, w, r.Height);
                    using (SolidBrush b = new SolidBrush(Theme.Accent))
                        Draw.FillRoundedRect(g, b, f, radius);
                }
            }
        }
    }
}
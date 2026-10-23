using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using AppManager;

namespace AppManager.Controls
{
    /// <summary>
    /// 搜索框：iOS 风格圆角白底 + 放大镜图标 + 占位文本；内嵌无框 TextBox 复用输入能力。
    /// </summary>
    public class SearchField : UserControl
    {
        private readonly TextBox box = new TextBox();
        private string placeholder = "搜索应用…";

        public SearchField()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Color.Transparent;
            DoubleBuffered = true;
            box.BorderStyle = BorderStyle.None;
            box.BackColor = Theme.SearchBg;
            box.ForeColor = Theme.TextPrimary;
            box.Font = Theme.FontOf(10F);
            box.TabStop = true;
            box.TextChanged += delegate(object s, EventArgs e)
            {
                Invalidate();
                EventHandler h = TextChanged;
                if (h != null) h(this, e);
            };
            box.GotFocus += delegate { box.BackColor = Color.White; Invalidate(); };
            box.LostFocus += delegate { box.BackColor = Theme.SearchBg; Invalidate(); };
            Controls.Add(box);
        }

        public override string Text { get { return box.Text; } set { box.Text = value; } }

        [Browsable(true)]
        public new event EventHandler TextChanged;

        [DefaultValue("搜索应用…")]
        public string Placeholder
        {
            get { return placeholder; }
            set { placeholder = value; Invalidate(); }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            int pad = 10;
            int iconArea = 24;
            box.Location = new Point(pad + iconArea, (Height - box.PreferredHeight) / 2);
            box.Width = Math.Max(10, Width - box.Left - pad);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Rectangle r = ClientRectangle;
            r.Inflate(-1, -1);
            // iOS 搜索框：灰底圆角、无边框；聚焦时白底 + 蓝色描边
            Color bg = box.Focused ? Color.White : Theme.SearchBg;
            using (SolidBrush b = new SolidBrush(bg)) Draw.FillRoundedRect(g, b, r, 10);
            if (box.Focused)
            {
                using (Pen p = new Pen(Theme.Accent, 1.5F)) Draw.DrawRoundedRect(g, p, r, 10);
            }
            // 放大镜
            int cx = 13, cy = Height / 2;
            using (Pen mp = new Pen(Theme.TextSecondary, 1.6F))
            {
                g.DrawEllipse(mp, cx, cy - 6, 11, 11);
                g.DrawLine(mp, cx + 9, cy + 3, cx + 15, cy + 9);
            }
            if (string.IsNullOrEmpty(box.Text) && !box.Focused)
            {
                Rectangle tr = new Rectangle(38, 0, r.Width - 44, r.Height);
                using (SolidBrush br = new SolidBrush(Theme.TextSecondary))
                    Draw.DrawStringLeft(g, placeholder, box.Font, br, tr);
            }
        }
    }
}

using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using AppManager;

namespace AppManager.Controls
{
    /// <summary>
    /// 搜索框：中性灰圆角底 + 放大镜图标 + 占位文本；内嵌无框 TextBox 复用输入能力。
    /// </summary>
    public class SearchField : UserControl
    {
        private readonly TextBox box = new TextBox();
        private readonly Label placeholderLabel = new Label();
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
            box.Font = Theme.BodyFont;
            box.TabStop = true;
            box.TextChanged += delegate(object s, EventArgs e)
            {
                UpdatePlaceholder();
                Invalidate();
                EventHandler h = TextChanged;
                if (h != null) h(this, e);
            };
            box.GotFocus += delegate { box.BackColor = Color.White; UpdatePlaceholder(); Invalidate(); };
            box.LostFocus += delegate { box.BackColor = Theme.SearchBg; UpdatePlaceholder(); Invalidate(); };
            Controls.Add(box);
            placeholderLabel.BackColor = Color.Transparent;
            placeholderLabel.ForeColor = Theme.TextTertiary;
            placeholderLabel.Font = box.Font;
            placeholderLabel.Text = placeholder;
            placeholderLabel.TextAlign = ContentAlignment.MiddleLeft;
            placeholderLabel.Cursor = Cursors.IBeam;
            placeholderLabel.Click += delegate { box.Focus(); };
            Controls.Add(placeholderLabel);
            placeholderLabel.BringToFront();
            UpdatePlaceholder();
        }

        public override string Text { get { return box.Text; } set { box.Text = value; } }

        [Browsable(true)]
        public new event EventHandler TextChanged;

        [DefaultValue("搜索应用…")]
        public string Placeholder
        {
            get { return placeholder; }
            set { placeholder = value; placeholderLabel.Text = value; UpdatePlaceholder(); Invalidate(); }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            int pad = 10;
            int iconArea = 24;
            box.Location = new Point(pad + iconArea, (Height - box.PreferredHeight) / 2);
            box.Width = Math.Max(10, Width - box.Left - pad);
            placeholderLabel.SetBounds(box.Left, 0, Math.Max(0, box.Width), Height);
        }

        private void UpdatePlaceholder()
        {
            placeholderLabel.Visible = string.IsNullOrEmpty(box.Text) && !box.Focused;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Rectangle r = ClientRectangle;
            r.Inflate(-1, -1);
            // 默认保留清晰边界，聚焦时使用蓝色描边
            Color bg = box.Focused ? Color.White : Theme.SearchBg;
            using (SolidBrush b = new SolidBrush(bg)) Draw.FillRoundedRect(g, b, r, 6);
            using (Pen p = new Pen(box.Focused ? Theme.Accent : Theme.ControlBorder, box.Focused ? 1.4F : 1F))
                Draw.DrawRoundedRect(g, p, r, 6);
            Draw.DrawIcon(g, UiIconKind.Search, new Rectangle(6, (Height - 20) / 2, 20, 20), Theme.IconColor);
        }
    }
}

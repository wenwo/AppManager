using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using AppManager;

namespace AppManager.Controls
{
    /// <summary>
    /// 搜索框：中性灰圆角底 + 放大镜图标 + 占位文本；内嵌无框 TextBox 复用输入能力。
    /// 有输入时在尾部显示一键清空按钮。
    /// </summary>
    public class SearchField : UserControl
    {
        private readonly TextBox box = new TextBox();
        private readonly Label placeholderLabel = new PlaceholderLabel();
        private readonly ClearButton clearButton = new ClearButton();
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
                UpdateClearButton();
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
            // 显式关闭自动尺寸：占位文案须撑满输入框高度，才能在 PlaceholderLabel 中垂直居中。
            placeholderLabel.AutoSize = false;
            placeholderLabel.Text = placeholder;
            placeholderLabel.TextAlign = ContentAlignment.MiddleLeft;
            placeholderLabel.Cursor = Cursors.IBeam;
            placeholderLabel.Click += delegate { box.Focus(); };
            Controls.Add(placeholderLabel);
            placeholderLabel.BringToFront();
            clearButton.AccessibleName = "清空搜索";
            clearButton.AccessibleRole = AccessibleRole.PushButton;
            clearButton.Click += delegate
            {
                if (box.TextLength == 0) return;
                box.Clear();
                box.Focus();
            };
            Controls.Add(clearButton);
            clearButton.BringToFront();
            UpdateClearButton();
            UpdatePlaceholder();
        }

        public override string Text { get { return box.Text; } set { box.Text = value; } }

        public void FocusInput()
        {
            box.Focus();
            box.SelectAll();
        }

        [Browsable(true)]
        public new event EventHandler TextChanged;

        [DefaultValue("搜索应用…")]
        public string Placeholder
        {
            get { return placeholder; }
            set { placeholder = value; placeholderLabel.Text = value; UpdatePlaceholder(); Invalidate(); }
        }

        protected override bool ScaleChildren { get { return false; } }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            int dpi = DpiHelper.ForControl(this);
            int pad = DpiHelper.Scale(10, dpi);
            int iconArea = DpiHelper.Scale(24, dpi);
            int clearGap = DpiHelper.Scale(5, dpi);
            int clearSize = DpiHelper.Scale(24, dpi);
            int clearReserve = DpiHelper.Scale(31, dpi);
            box.Location = new Point(pad + iconArea, (Height - box.PreferredHeight) / 2);
            box.Width = Math.Max(10, Width - box.Left - clearReserve);
            placeholderLabel.SetBounds(box.Left, 0, Math.Max(0, box.Width), Height);
            clearButton.SetBounds(Width - clearGap - clearSize, (Height - clearSize) / 2, clearSize, clearSize);
        }

        private void UpdatePlaceholder()
        {
            placeholderLabel.Visible = string.IsNullOrEmpty(box.Text) && !box.Focused;
        }

        private void UpdateClearButton()
        {
            clearButton.Visible = box.TextLength > 0;
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

        /// <summary>
        /// 占位文案：固定单行绘制，放不下时尾部省略。
        /// 原生 Label 总是按词折行，搜索框较窄时长文案会折成两行，第二行被隐藏、首行被挤到上沿。
        /// </summary>
        private sealed class PlaceholderLabel : Label
        {
            private const TextFormatFlags Flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;

            protected override void OnPaint(PaintEventArgs e)
            {
                TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor, Flags);
            }
        }

        private sealed class ClearButton : Control
        {
            private bool hover;
            private bool pressed;

            public ClearButton()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                    | ControlStyles.ResizeRedraw | ControlStyles.UserPaint
                    | ControlStyles.SupportsTransparentBackColor, true);
                BackColor = Color.Transparent;
                Cursor = Cursors.Hand;
                TabStop = false;
            }

            protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnMouseDown(MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); }
                base.OnMouseDown(e);
            }
            protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                if (hover || pressed)
                {
                    Rectangle r = ClientRectangle;
                    r.Inflate(-1, -1);
                    Color fill = pressed ? Color.FromArgb(32, 0, 0, 0) : Color.FromArgb(18, 0, 0, 0);
                    using (SolidBrush b = new SolidBrush(fill)) Draw.FillRoundedRect(g, b, r, 5);
                }
                float cx = Width / 2F;
                float cy = Height / 2F;
                float extent = Math.Max(3F, Width * 0.18F);
                using (Pen p = new Pen(Theme.TextSecondary, 1.5F))
                {
                    p.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                    p.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                    g.DrawLine(p, cx - extent, cy - extent, cx + extent, cy + extent);
                    g.DrawLine(p, cx + extent, cy - extent, cx - extent, cy + extent);
                }
            }
        }
    }
}

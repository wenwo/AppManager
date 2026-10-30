using System;
using System.Collections.Generic;
using System.IO;
using System.Drawing;
using System.Windows.Forms;

namespace AppManager.Controls
{
    /// <summary>应用内纯文本查看器：用于 txt、配置、代码和常见标记文本资源。</summary>
    public class TextViewerControl : Control
    {
        public event Action CloseRequested;

        private readonly RichTextBox textBox;
        private readonly PreviewLoader<TextPreview> loader;
        private readonly RoundButton externalButton;
        private readonly Font textFont;
        private readonly List<string> paths = new List<string>();
        private int index;
        private Rectangle backRect;
        private Rectangle prevRect;
        private Rectangle nextRect;
        private Rectangle infoBarRect;
        private bool loadFailed;
        private bool loading;
        private string failureText = "无法读取此文本";
        private bool truncated;

        private int Dpi(int value)
        {
            return DpiHelper.Scale(value, DpiHelper.ForControl(this));
        }

        private int TopBarH { get { return Dpi(44); } }
        private int InfoBarH { get { return Dpi(44); } }
        private int ChromePad { get { return Dpi(16); } }
        private int CardGap { get { return Dpi(12); } }
        private int PillH { get { return Dpi(40); } }
        private int PillBtnW { get { return Dpi(44); } }

        private Rectangle TextArea
        {
            get
            {
                int bottom = Height - InfoBarH - CardGap - PillH - CardGap;
                return new Rectangle(ChromePad, TopBarH, Math.Max(0, Width - ChromePad * 2),
                    Math.Max(0, bottom - TopBarH));
            }
        }

        public TextViewerControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Theme.Background;
            TabStop = true;
            Visible = false;
            loader = new PreviewLoader<TextPreview>(this);
            externalButton = new RoundButton { Text = "外部打开", Accent = false };
            externalButton.Click += delegate
            {
                if (paths.Count > 0 && !Launcher.OpenWith(paths[index]))
                    MessageBox.Show(this, "无法启动 Windows 打开方式选择器。", "外部打开", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };
            Controls.Add(externalButton);

            textBox = new RichTextBox();
            textBox.ReadOnly = true;
            textBox.Multiline = true;
            textBox.BorderStyle = BorderStyle.None;
            textBox.DetectUrls = false;
            textBox.WordWrap = false;
            textBox.ScrollBars = RichTextBoxScrollBars.Both;
            textBox.BackColor = Color.White;
            textBox.ForeColor = Color.FromArgb(35, 35, 38);
            textFont = new Font(FontFamily.GenericMonospace, 10F, FontStyle.Regular);
            textBox.Font = textFont;
            textBox.Visible = false;
            Controls.Add(textBox);
        }

        public void Open(List<string> textPaths, int startIndex)
        {
            paths.Clear();
            if (textPaths != null) paths.AddRange(textPaths);
            if (paths.Count == 0) { ClosePreview(); RequestClose(); return; }
            index = Math.Min(Math.Max(startIndex, 0), paths.Count - 1);
            Visible = true;
            Focus();
            LoadCurrent();
        }

        private void RequestClose()
        {
            Action handler = CloseRequested;
            if (handler != null) handler();
        }

        private void LoadCurrent()
        {
            loader.Clear();
            loadFailed = false;
            loading = true;
            truncated = false;
            failureText = "无法读取此文本";
            textBox.Visible = false;
            textBox.Clear();
            string path = paths[index];
            loader.Load(delegate(System.Threading.CancellationToken cancellation)
            {
                return TextPreview.Read(path, cancellation);
            }, delegate(TextPreview preview, Exception error)
            {
                loading = false;
                if (error != null)
                {
                    failureText = "无法读取此文本：" + error.Message;
                    loadFailed = true;
                }
                else
                {
                    textBox.Text = preview.Content;
                    truncated = preview.Truncated;
                    textBox.Visible = true;
                }
                Invalidate();
            });
            LayoutTextBox();
            Invalidate();
        }

        public void ClosePreview()
        {
            loader.Clear();
            textBox.Clear();
            textBox.Visible = false;
            loading = false;
            paths.Clear();
            Visible = false;
        }

        private void LayoutTextBox()
        {
            if (textBox == null) return;
            externalButton.Bounds = new Rectangle(Math.Max(0, Width - Dpi(112)), Dpi(6), Dpi(96), Dpi(30));
            Rectangle area = TextArea;
            textBox.Bounds = new Rectangle(area.X + Dpi(1), area.Y + Dpi(1),
                Math.Max(0, area.Width - Dpi(2)), Math.Max(0, area.Height - Dpi(2)));
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutTextBox();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Background);
            LayoutChrome();
            DrawCanvas(g);
            DrawTopBar(g);
            DrawPill(g);
            DrawInfoBar(g);
        }

        private void LayoutChrome()
        {
            backRect = new Rectangle(ChromePad, 6, Dpi(76), TopBarH - 12);
            int infoTop = Height - InfoBarH;
            infoBarRect = new Rectangle(0, infoTop, Width, InfoBarH);
            int pillW = PillBtnW * 2;
            int pillTop = infoTop - CardGap - PillH;
            prevRect = new Rectangle((Width - pillW) / 2, pillTop, PillBtnW, PillH);
            nextRect = new Rectangle(prevRect.Right, pillTop, PillBtnW, PillH);
            LayoutTextBox();
        }

        private void DrawCanvas(Graphics g)
        {
            Rectangle area = TextArea;
            if (area.Width <= 0 || area.Height <= 0) return;
            using (Pen p = new Pen(Theme.Separator, 1)) g.DrawRectangle(p, area);
            if (loadFailed)
            {
                using (SolidBrush b = new SolidBrush(Theme.TextSecondary))
                    Draw.DrawStringCenter(g, failureText, Theme.BodyFont, b, area);
            }
            else if (loading)
            {
                using (SolidBrush b = new SolidBrush(Theme.TextSecondary))
                    Draw.DrawStringCenter(g, "正在加载…", Theme.BodyFont, b, area);
            }
        }

        private void DrawTopBar(Graphics g)
        {
            DrawButtonBackground(g, backRect, false);
            Draw.DrawIcon(g, UiIconKind.Back,
                new Rectangle(backRect.X + Dpi(6), backRect.Y + (backRect.Height - Dpi(20)) / 2, Dpi(20), Dpi(20)), Theme.IconColor);
            TextRenderer.DrawText(g, "返回", Theme.ButtonFont,
                new Rectangle(backRect.X + Dpi(24), backRect.Y, backRect.Width - Dpi(24), backRect.Height),
                Theme.TextPrimary, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        }

        private void DrawPill(Graphics g)
        {
            using (SolidBrush b = new SolidBrush(Theme.TooltipBg))
                Draw.FillRoundedRect(g, b, new Rectangle(prevRect.X, prevRect.Y, prevRect.Width + nextRect.Width, PillH), PillH / 2);
            DrawButtonBackground(g, prevRect, false);
            DrawButtonBackground(g, nextRect, false);
            DrawChevron(g, prevRect, true);
            DrawChevron(g, nextRect, false);
        }

        private void DrawChevron(Graphics g, Rectangle r, bool left)
        {
            using (Pen p = new Pen(paths.Count < 2 ? Color.FromArgb(90, 255, 255, 255) : Color.White, 1.8F))
            {
                p.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                p.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                float cx = r.X + r.Width / 2F;
                float cy = r.Y + r.Height / 2F;
                if (left)
                {
                    g.DrawLine(p, cx + Dpi(3), cy - Dpi(6), cx - Dpi(3), cy);
                    g.DrawLine(p, cx - Dpi(3), cy, cx + Dpi(3), cy + Dpi(6));
                }
                else
                {
                    g.DrawLine(p, cx - Dpi(3), cy - Dpi(6), cx + Dpi(3), cy);
                    g.DrawLine(p, cx + Dpi(3), cy, cx - Dpi(3), cy + Dpi(6));
                }
            }
        }

        private static void DrawButtonBackground(Graphics g, Rectangle r, bool active)
        {
            if (!active) return;
            using (SolidBrush b = new SolidBrush(Color.FromArgb(20, 0, 0, 0)))
                Draw.FillRoundedRect(g, b, r, 8);
        }

        private void DrawInfoBar(Graphics g)
        {
            using (Pen p = new Pen(Theme.Separator, 1))
                g.DrawLine(p, Dpi(12), infoBarRect.Y, infoBarRect.Right - Dpi(12), infoBarRect.Y);
            int pad = Dpi(16);
            int countW = Dpi(80);
            Rectangle countR = new Rectangle(infoBarRect.Right - pad - countW, infoBarRect.Y, countW, infoBarRect.Height);
            Rectangle nameR = new Rectangle(infoBarRect.X + pad, infoBarRect.Y,
                Math.Max(0, countR.Left - Dpi(12) - (infoBarRect.X + pad)), infoBarRect.Height);
            string name = paths.Count > 0 && index < paths.Count ? Path.GetFileName(paths[index]) : string.Empty;
            if (truncated) name += " · 仅预览前 256K 字符，完整内容请外部打开";
            TextRenderer.DrawText(g, name, Theme.BodyFont, nameR, Theme.TextPrimary,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            TextRenderer.DrawText(g, (index + 1) + " / " + paths.Count, Theme.CaptionFont, countR, Theme.TextSecondary,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (backRect.Contains(e.Location)) { RequestClose(); return; }
            if (prevRect.Contains(e.Location)) { Step(-1); return; }
            if (nextRect.Contains(e.Location)) { Step(1); return; }
        }

        private void Step(int delta)
        {
            if (paths.Count < 2) return;
            index = (index + delta + paths.Count) % paths.Count;
            LoadCurrent();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { RequestClose(); return true; }
            if (keyData == (Keys.Alt | Keys.Left)) { Step(-1); return true; }
            if (keyData == (Keys.Alt | Keys.Right)) { Step(1); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                loader.Dispose();
                if (textBox != null) textBox.Dispose();
                textFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

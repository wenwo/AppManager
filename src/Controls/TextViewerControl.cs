using System;
using System.Collections.Generic;
using System.IO;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace AppManager.Controls
{
    /// <summary>
    /// 应用内文本查看器：用于 txt、配置、代码和常见标记文本资源（文档与证件共用）。
    /// 文本视图可直接编辑并按原编码保存；Markdown 文件额外提供渲染预览，可与文本视图切换。
    /// </summary>
    public class TextViewerControl : Control
    {
        public event Action CloseRequested;

        private readonly RichTextBox textBox;
        private readonly RichTextBox markdownBox;
        private readonly PreviewLoader<TextPreview> loader;
        private readonly RoundButton externalButton;
        private readonly RoundButton saveButton;
        private readonly Font textFont;
        private readonly System.Windows.Forms.Timer renderTimer;
        private int renderGeneration;
        private CancellationTokenSource renderCancellation;
        private readonly List<string> paths = new List<string>();
        private int index;
        private Rectangle backRect;
        private Rectangle prevRect;
        private Rectangle nextRect;
        private Rectangle infoBarRect;
        private Rectangle markdownTabRect;
        private Rectangle plainTabRect;
        private bool loadFailed;
        private bool loading;
        private string failureText = "无法读取此文本";
        private bool truncated;
        private string readOnlyReason;   // 非空则只读（截断、编码无法无损写回），并在信息栏提示原因
        private TextPreview current;       // 当前文件的读取结果：编码与修改时间用于保存
        private string savedText;          // 最近一次读取/保存的内容（换行统一为 \n）
        private string newline = "\r\n";   // 文件原有换行风格，保存时还原
        private bool markdownFile;
        private bool showMarkdown = true;  // Markdown 默认渲染预览；切换结果在本次运行内沿用
        private bool dirty;
        private bool syncing;              // 程序写入文本框时不视为用户编辑
        private bool editorLoaded;         // 文本框按需填充：Markdown 预览期间不加载，切到文本视图时再填入
        private int editVersion;
        private int renderedVersion = -1;
        private int renderedWidth;

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
        private bool MarkdownVisible { get { return markdownFile && showMarkdown; } }

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
                if (paths.Count == 0 || !ConfirmLeave("打开外部程序前，是否保存对“{0}”的修改？", false)) return;
                if (!Launcher.OpenWith(paths[index]))
                    MessageBox.Show(this, "无法启动 Windows 打开方式选择器。", "外部打开", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };
            Controls.Add(externalButton);
            saveButton = new RoundButton { Text = "保存", Accent = true, Visible = false };
            saveButton.Click += delegate { Save(); };
            Controls.Add(saveButton);

            textBox = new RichTextBox();
            textBox.ReadOnly = true;
            textBox.Multiline = true;
            textBox.BorderStyle = BorderStyle.None;
            textBox.DetectUrls = false;
            textBox.WordWrap = false;
            textBox.AcceptsTab = true;
            textBox.ScrollBars = RichTextBoxScrollBars.Both;
            textBox.BackColor = Color.White;
            textBox.ForeColor = Color.FromArgb(35, 35, 38);
            textFont = new Font(FontFamily.GenericMonospace, 10F, FontStyle.Regular);
            textBox.Font = textFont;
            textBox.Visible = false;
            textBox.TextChanged += delegate { OnTextEdited(); };
            Controls.Add(textBox);

            markdownBox = new MarkdownRichTextBox();
            markdownBox.ReadOnly = true;
            markdownBox.Multiline = true;
            markdownBox.BorderStyle = BorderStyle.None;
            markdownBox.DetectUrls = false;
            markdownBox.WordWrap = true;
            markdownBox.ScrollBars = RichTextBoxScrollBars.Vertical;
            markdownBox.BackColor = Color.White;
            markdownBox.Visible = false;
            Controls.Add(markdownBox);

            // 窗口拖拽缩放时合并重排：表格/代码块列宽随可用宽度重新渲染
            renderTimer = new System.Windows.Forms.Timer { Interval = 200 };
            renderTimer.Tick += delegate { renderTimer.Stop(); QueueMarkdownResizeRender(); };
        }

        public void Open(List<string> textPaths, int startIndex)
        {
            ResolvePendingEdits();
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
            renderTimer.Stop();
            CancelResizeRender();
            loadFailed = false;
            loading = true;
            truncated = false;
            readOnlyReason = null;
            current = null;
            failureText = "无法读取此文本";
            textBox.Visible = false;
            markdownBox.Visible = false;
            SetEditorText(string.Empty);
            editorLoaded = false;
            markdownBox.Clear();
            editVersion++;
            renderedVersion = -1;
            string path = paths[index];
            markdownFile = MediaRules.IsMarkdown(path);
            LayoutTextBox();
            // Markdown 预览在后台线程一并生成 RTF，界面线程只负责装入
            bool render = MarkdownVisible;
            int width = MarkdownWidth(), twips = MarkdownTwips(width);
            loader.Load(delegate(System.Threading.CancellationToken cancellation)
            {
                TextPreview preview = TextPreview.Read(path, cancellation);
                if (render)
                {
                    cancellation.ThrowIfCancellationRequested();
                    preview.RenderedRtf = MarkdownRtf.Render(preview.Content, twips, cancellation);
                }
                return preview;
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
                    current = preview;
                    truncated = preview.Truncated;
                    readOnlyReason = preview.ReadOnlyReason;
                    newline = DetectNewline(preview.Content);
                    savedText = NormalizeNewlines(preview.Content);
                    if (preview.RenderedRtf != null && MarkdownVisible)
                    {
                        markdownBox.Rtf = preview.RenderedRtf;
                        renderedVersion = editVersion;
                        renderedWidth = width;
                    }
                    preview.RenderedRtf = null;
                    ShowCurrentView();
                }
                Invalidate();
            });
            Invalidate();
        }

        public void ClosePreview()
        {
            ResolvePendingEdits();
            loader.Clear();
            renderTimer.Stop();
            CancelResizeRender();
            SetEditorText(string.Empty);
            editorLoaded = false;
            textBox.Visible = false;
            markdownBox.Clear();
            markdownBox.Visible = false;
            loading = false;
            current = null;
            paths.Clear();
            Visible = false;
        }

        // ===== 视图切换 =====

        private void SetMarkdownView(bool markdown)
        {
            if (!markdownFile || showMarkdown == markdown || current == null) return;
            showMarkdown = markdown;
            ShowCurrentView();
            Invalidate();
        }

        private void ShowCurrentView()
        {
            if (current == null) return;
            LayoutTextBox();
            if (MarkdownVisible) RenderMarkdown(false);
            else if (!editorLoaded)
            {
                SetEditorText(savedText);
                // 截断内容保存会丢失后半部分；编码无法无损写回的内容保存会改坏原字节：都只读
                textBox.ReadOnly = truncated || readOnlyReason != null;
                editorLoaded = true;
            }
            textBox.Visible = !MarkdownVisible;
            markdownBox.Visible = MarkdownVisible;
            if (MarkdownVisible) markdownBox.Focus();
            else textBox.Focus();
        }

        /// <summary>按文本框中的最新内容渲染；未编辑且宽度未变时复用上次结果。</summary>
        private void RenderMarkdown(bool keepScroll)
        {
            if (!MarkdownVisible || current == null) return;
            int width = MarkdownWidth();
            if (renderedVersion == editVersion && Math.Abs(width - renderedWidth) < Dpi(24)) return;
            int firstChar = keepScroll && markdownBox.TextLength > 0 ? markdownBox.GetCharIndexFromPosition(new Point(1, 1)) : 0;
            markdownBox.Rtf = MarkdownRtf.Render(EditorText(), MarkdownTwips(width));
            renderedVersion = editVersion;
            renderedWidth = width;
            markdownBox.Select(0, 0);
            if (firstChar > 0)
            {
                int line = markdownBox.GetLineFromCharIndex(Math.Min(firstChar, markdownBox.TextLength));
                SendMessage(markdownBox.Handle, EM_LINESCROLL, IntPtr.Zero, (IntPtr)line);
            }
        }

        private int MarkdownWidth()
        {
            return Math.Max(Dpi(160), markdownBox.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - Dpi(8));
        }

        private int MarkdownTwips(int width)
        {
            return width * 1440 / Math.Max(96, DpiHelper.ForControl(this));
        }

        /// <summary>窗口尺寸变化后在后台重新渲染 Markdown；旧任务立即取消，UI 线程只负责装入结果。</summary>
        private void QueueMarkdownResizeRender()
        {
            if (!MarkdownVisible || current == null) return;
            CancelResizeRender();
            CancellationTokenSource source = new CancellationTokenSource();
            renderCancellation = source;
            int generation = ++renderGeneration;
            int width = MarkdownWidth();
            int twips = MarkdownTwips(width);
            string text = EditorText();
            int version = editVersion;
            CancellationToken token = source.Token;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string rtf;
                try { rtf = MarkdownRtf.Render(text, twips, token); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { Paths.LogError(ex); return; }
                try
                {
                    BeginInvoke((Action)delegate
                    {
                        if (IsDisposed || Disposing || generation != renderGeneration || token.IsCancellationRequested) return;
                        if (!MarkdownVisible || current == null) return;
                        int firstChar = markdownBox.TextLength > 0
                            ? markdownBox.GetCharIndexFromPosition(new Point(1, 1)) : 0;
                        markdownBox.Rtf = rtf;
                        renderedVersion = version;
                        renderedWidth = width;
                        markdownBox.Select(0, 0);
                        if (firstChar > 0)
                        {
                            int line = markdownBox.GetLineFromCharIndex(Math.Min(firstChar, markdownBox.TextLength));
                            SendMessage(markdownBox.Handle, EM_LINESCROLL, IntPtr.Zero, (IntPtr)line);
                        }
                    });
                }
                catch { }
            });
        }

        private void CancelResizeRender()
        {
            if (renderCancellation != null)
            {
                renderCancellation.Cancel();
                renderCancellation.Dispose();
                renderCancellation = null;
            }
            renderGeneration++;
        }

        // ===== 编辑与保存 =====

        private void SetEditorText(string text)
        {
            syncing = true;
            try
            {
                textBox.Text = text;
                textBox.ClearUndo();
            }
            finally { syncing = false; }
            dirty = false;
            saveButton.Visible = false;
        }

        /// <summary>编辑器当前内容，换行统一为 LF；文本框尚未填充时即为已保存内容。</summary>
        private string EditorText()
        {
            return editorLoaded ? NormalizeNewlines(textBox.Text) : savedText;
        }

        private void OnTextEdited()
        {
            if (syncing || !editorLoaded) return;
            editVersion++;
            // 句柄重建等也会触发 TextChanged：仅内容确实不同才标记为已修改
            if (!dirty && current != null && EditorText() != savedText)
            {
                dirty = true;
                saveButton.Visible = true;
                LayoutTextBox();
                Invalidate();
            }
        }

        private bool HasUnsavedChanges()
        {
            return dirty && current != null && paths.Count > 0 && EditorText() != savedText;
        }

        /// <summary>Ctrl+S / 保存按钮：按读取时的编码与换行写回原文件。</summary>
        private bool Save()
        {
            if (!HasUnsavedChanges())
            {
                MarkClean();
                return true;
            }
            string path = paths[index];
            string text = EditorText();
            try
            {
                if (File.Exists(path) && File.GetLastWriteTimeUtc(path) != current.LastWriteUtc
                    && MessageBox.Show(this, "“" + Path.GetFileName(path) + "”已在外部被修改，保存将覆盖这些修改。是否继续？",
                        "保存", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    return false;
                if (current.MixedNewlines && MessageBox.Show(this, "“" + Path.GetFileName(path) + "”混用了多种换行符，保存后会统一为 "
                        + (newline == "\r\n" ? "CRLF" : newline == "\n" ? "LF" : "CR") + "。是否继续？",
                        "保存", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    return false;
                Encoding encoding = current.Encoding ?? new UTF8Encoding(false);
                string content = text.Replace("\n", newline);
                string target = path;
                if (!File.Exists(path))
                {
                    // 原文件已被外部删除/移走：写回必然失败，提供另存为
                    using (SaveFileDialog pick = new SaveFileDialog { FileName = Path.GetFileName(path), OverwritePrompt = true })
                    {
                        if (pick.ShowDialog(this) != DialogResult.OK) return false;
                        target = pick.FileName;
                    }
                }
                try { TextPreview.Write(target, content, encoding); }
                catch (EncoderFallbackException)
                {
                    if (MessageBox.Show(this, "部分字符无法用原编码（" + encoding.EncodingName + "）保存，是否改用 UTF-8 保存？",
                            "保存", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                        return false;
                    encoding = new UTF8Encoding(false);
                    TextPreview.Write(target, content, encoding);
                }
                current.Encoding = encoding;
                current.MixedNewlines = false;
                if (target == path) current.LastWriteUtc = File.GetLastWriteTimeUtc(path);
                savedText = text;
                MarkClean();
                return true;
            }
            catch (Exception ex)
            {
                if (!(ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException
                    || ex is NotSupportedException || ex is System.Security.SecurityException)) throw;
                MessageBox.Show(this, "保存失败：" + ex.Message, "保存", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }

        private void MarkClean()
        {
            dirty = false;
            saveButton.Visible = false;
            LayoutTextBox();
            Invalidate();
        }

        /// <summary>离开当前文件前处理未保存的修改；返回 false 表示用户选择取消。</summary>
        public bool ConfirmLeave()
        {
            return ConfirmLeave("是否保存对“{0}”的修改？", true);
        }

        /// <summary>
        /// discardOnNo：真正离开当前文件时，选“否”即放弃修改（清除已修改标记），之后的关闭流程不再重复询问；
        /// 仅打开外部程序等不离开的场景传 false，保留编辑内容。
        /// </summary>
        private bool ConfirmLeave(string format, bool discardOnNo)
        {
            if (!HasUnsavedChanges()) return true;
            DialogResult result = MessageBox.Show(this, string.Format(format, Path.GetFileName(paths[index])), "文本",
                MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (result == DialogResult.Yes) return Save();
            if (result != DialogResult.No) return false;
            if (discardOnNo) MarkClean();
            return true;
        }

        /// <summary>
        /// 外部强制关闭（切换分类、进入设置等）无法取消：先问是否保存；保存失败时让用户选择重试或明确放弃，
        /// 绝不在保存失败后静默丢弃修改。
        /// </summary>
        private void ResolvePendingEdits()
        {
            if (!HasUnsavedChanges()) return;
            if (MessageBox.Show(this, "是否保存对“" + Path.GetFileName(paths[index]) + "”的修改？", "文本",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                // 这里无法取消离开，“取消”实为丢弃修改：按钮直接写明“放弃修改”，避免误点。
                while (!Save())
                {
                    if (!ConfirmDialog.Show("修改没有保存", "点“重试”再次保存；点“放弃修改”将丢弃这些修改。", "重试", "放弃修改"))
                        break;
                }
            }
            dirty = false;
        }

        private static string NormalizeNewlines(string text)
        {
            return (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        }

        private static string DetectNewline(string text)
        {
            if (string.IsNullOrEmpty(text)) return "\r\n";
            int lf = text.IndexOf('\n');
            int cr = text.IndexOf('\r');
            if (cr >= 0 && (lf < 0 || cr < lf)) return cr + 1 < text.Length && text[cr + 1] == '\n' ? "\r\n" : "\r";
            return lf >= 0 ? "\n" : "\r\n";
        }

        // ===== 布局与绘制 =====

        private void LayoutTextBox()
        {
            if (textBox == null) return;
            int buttonY = Dpi(6), buttonH = Dpi(30);
            externalButton.Bounds = new Rectangle(Math.Max(0, Width - Dpi(112)), buttonY, Dpi(96), buttonH);
            int right = externalButton.Left - Dpi(8);
            if (markdownFile && current != null)
            {
                plainTabRect = new Rectangle(right - Dpi(60), buttonY, Dpi(60), buttonH);
                markdownTabRect = new Rectangle(plainTabRect.X - Dpi(92), buttonY, Dpi(92), buttonH);
                right = markdownTabRect.Left - Dpi(8);
            }
            else markdownTabRect = plainTabRect = Rectangle.Empty;
            saveButton.Bounds = new Rectangle(Math.Max(0, right - Dpi(72)), buttonY, Dpi(72), buttonH);
            Rectangle area = TextArea;
            textBox.Bounds = new Rectangle(area.X + Dpi(1), area.Y + Dpi(1),
                Math.Max(0, area.Width - Dpi(2)), Math.Max(0, area.Height - Dpi(2)));
            // Markdown 预览留出阅读边距，背景由 DrawCanvas 补白
            markdownBox.Bounds = new Rectangle(area.X + Dpi(20), area.Y + Dpi(12),
                Math.Max(0, area.Width - Dpi(24)), Math.Max(0, area.Height - Dpi(16)));
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutTextBox();
            if (MarkdownVisible && current != null) { renderTimer.Stop(); renderTimer.Start(); }
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
            if (markdownBox.Visible)
                using (SolidBrush b = new SolidBrush(markdownBox.BackColor)) g.FillRectangle(b, area);
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
            DrawViewSwitch(g);
        }

        /// <summary>Markdown / 文本 分段切换：样式与侧栏「软件 | 资源」一致。</summary>
        private void DrawViewSwitch(Graphics g)
        {
            if (markdownTabRect.IsEmpty) return;
            System.Drawing.Drawing2D.SmoothingMode mode = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Rectangle seg = Rectangle.Union(markdownTabRect, plainTabRect);
            using (SolidBrush bg = new SolidBrush(Theme.SegmentBg)) Draw.FillRoundedRect(g, bg, seg, Dpi(7));
            Rectangle active = showMarkdown ? markdownTabRect : plainTabRect;
            active.Inflate(-Dpi(2), -Dpi(2));
            using (SolidBrush b = new SolidBrush(Color.White)) Draw.FillRoundedRect(g, b, active, Dpi(6));
            g.SmoothingMode = mode;
            TextRenderer.DrawText(g, "Markdown", Theme.ButtonFont, markdownTabRect,
                showMarkdown ? Theme.AccentText : Theme.TextSecondary,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            TextRenderer.DrawText(g, "文本", Theme.ButtonFont, plainTabRect,
                showMarkdown ? Theme.TextSecondary : Theme.AccentText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
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
            if (dirty) name += " · 已修改（Ctrl+S 保存）";
            if (readOnlyReason != null) name += " · " + readOnlyReason;
            else if (truncated) name += " · 仅预览前 256K 字符，完整内容请外部打开";
            TextRenderer.DrawText(g, name, Theme.BodyFont, nameR, Theme.TextPrimary,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            TextRenderer.DrawText(g, (index + 1) + " / " + paths.Count, Theme.CaptionFont, countR, Theme.TextSecondary,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }

        // ===== 交互 =====

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (backRect.Contains(e.Location)) { if (ConfirmLeave()) RequestClose(); return; }
            if (prevRect.Contains(e.Location)) { Step(-1); return; }
            if (nextRect.Contains(e.Location)) { Step(1); return; }
            if (markdownTabRect.Contains(e.Location)) { SetMarkdownView(true); return; }
            if (plainTabRect.Contains(e.Location)) { SetMarkdownView(false); return; }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Cursor = markdownTabRect.Contains(e.Location) || plainTabRect.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
        }

        private void Step(int delta)
        {
            if (paths.Count < 2 || !ConfirmLeave()) return;
            index = (index + delta + paths.Count) % paths.Count;
            LoadCurrent();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { if (ConfirmLeave()) RequestClose(); return true; }
            if (keyData == (Keys.Alt | Keys.Left)) { Step(-1); return true; }
            if (keyData == (Keys.Alt | Keys.Right)) { Step(1); return true; }
            if (keyData == (Keys.Control | Keys.S)) { if (current != null) Save(); return true; }
            // 编辑时只粘贴纯文本：RichEdit 默认会粘入图片/格式，保存时会变成占位字符
            if ((keyData == (Keys.Control | Keys.V) || keyData == (Keys.Shift | Keys.Insert)) && textBox.Focused)
            {
                try
                {
                    if (!textBox.ReadOnly && Clipboard.ContainsText()) textBox.SelectedText = Clipboard.GetText();
                }
                catch (ExternalException) { }
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                loader.Dispose();
                renderTimer.Dispose();
                if (textBox != null) textBox.Dispose();
                if (markdownBox != null) markdownBox.Dispose();
                textFont.Dispose();
                CancelResizeRender();
            }
            base.Dispose(disposing);
        }

        private const int EM_LINESCROLL = 0x00B6;

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        /// <summary>
        /// Markdown 预览框：优先使用 RichEdit 4.1+（msftedit.dll）以正确显示表格与单元格底色，缺失时退回默认 RichEdit；
        /// 只读预览不显示插入光标。
        /// </summary>
        private sealed class MarkdownRichTextBox : RichTextBox
        {
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern IntPtr LoadLibraryW(string path);

            [DllImport("user32.dll")]
            private static extern bool HideCaret(IntPtr hWnd);

            private static readonly bool modern =
                LoadLibraryW(Path.Combine(Environment.SystemDirectory, "msftedit.dll")) != IntPtr.Zero;

            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams cp = base.CreateParams;
                    if (modern) cp.ClassName = "RICHEDIT50W";
                    return cp;
                }
            }

            protected override void OnGotFocus(EventArgs e)
            {
                base.OnGotFocus(e);
                HideCaret(Handle);
            }

            protected override void OnSelectionChanged(EventArgs e)
            {
                base.OnSelectionChanged(e);
                if (Focused) HideCaret(Handle);
            }
        }
    }
}

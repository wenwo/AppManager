using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using AppManager;

namespace AppManager.Controls
{
    /// <summary>
    /// 集合信息页：选中集合后替换列表区域展示，包含封面、名称、简介、数量与「进入 / 编辑」按钮。
    /// 单击集合停在此页，点「进入」或双击集合（侧边栏）后切换为集合内容网格；
    /// 点「编辑」直接在本页切换为编辑态（名称/简介/封面），封面优先从集合内图片选择，
    /// 也支持粘贴剪贴板图片或从文件选择，不再弹出编辑对话框。
    /// </summary>
    public class CollectionInfoPanel : Control
    {
        private string parent = string.Empty;
        private string name = string.Empty;
        private string description = string.Empty;
        private string coverPath = string.Empty;
        private int count;
        private Bitmap cover;               // 大封面位图，所有权归本控件
        private Bitmap editPreview;         // 编辑态封面预览（集合内图片/文件选择后设置）
        private bool editPreviewCleared;    // 预览为空时按占位绘制（表示「恢复自动且无可用封面」）
        private Bitmap editPasted;          // 粘贴的剪贴板图片（保存时交由主窗体落盘）
        private bool editing;
        private string editCoverPath = string.Empty;

        // 集合内图片缩略图（编辑态横向选择条）
        private readonly List<string> memberPaths = new List<string>();
        private readonly List<Bitmap> memberThumbs = new List<Bitmap>();
        private readonly List<string> thumbPaths = new List<string>();
        private bool memberThumbsBuilt;
        private int stripScroll;
        private int thumbHover = -1;
        private int thumbPress = -1;

        // 编辑态输入控件（按需创建，隐藏时不占位）
        private Panel namePanel;
        private Panel descPanel;
        private TextBox nameBox;
        private TextBox descBox;

        // 布局缓存：命中测试与绘制共用
        private Rectangle coverRect;
        private Rectangle stripRect;
        private Rectangle btnEnterRect;
        private Rectangle btnEditRect;
        private Rectangle btnPasteRect;
        private Rectangle btnFileRect;
        private Rectangle btnAutoRect;
        private Rectangle btnSaveRect;
        private Rectangle btnCancelRect;
        private Rectangle nameDisplayRect;
        private Rectangle descDisplayRect;
        private Rectangle metaDisplayRect;
        private int textColumnX;
        private int textColumnW;
        private int stripCaptionY;
        private int nameCaptionY;
        private int descCaptionY;
        private int hoverBtn = -1;          // 1 进入 2 编辑 3 从文件 4 恢复自动 5 保存 6 取消 8 粘贴
        private int pressBtn = -1;

        private const int BtnH = 36;
        private const int SmallBtnH = 30;
        private const int ThumbSize = 48;
        private const int ThumbGap = 8;
        private const int MaxMemberThumbs = 60;

        /// <summary>点击「进入」：由主窗体切换到集合内容。</summary>
        public event Action EnterRequested;
        /// <summary>点「保存」：名称 + 简介 + 封面路径 + 粘贴图片（校验与落盘由主窗体处理）。</summary>
        public event Action<string, string, string, Bitmap> EditSaveRequested;
        /// <summary>点「从文件选择…」：由主窗体选择图片并回传预览。</summary>
        public event Action CoverPickRequested;
        /// <summary>点「恢复自动」：由主窗体按成员缩略图重新生成预览。</summary>
        public event Action CoverAutoRequested;

        public CollectionInfoPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            BackColor = Theme.Background;
            Visible = false;
        }

        /// <summary>是否处于页内编辑态（主窗体据此跳过后台刷新覆盖表单）。</summary>
        public bool IsEditing { get { return editing; } }

        /// <summary>编辑态下焦点是否在名称/简介输入框（用于区分 Ctrl+V 粘贴文字与粘贴封面）。</summary>
        public bool IsTextInputFocused
        {
            get
            {
                return editing
                    && ((nameBox != null && nameBox.Focused) || (descBox != null && descBox.Focused));
            }
        }

        /// <summary>注入集合信息；旧封面与成员缩略图由本控件释放。</summary>
        public void SetInfo(string parentCategory, string collectionName, string desc, int itemCount,
            string collectionCoverPath, Bitmap coverBmp, List<string> memberImages)
        {
            EndEdit();
            if (cover != null) { cover.Dispose(); cover = null; }
            DisposeMemberThumbs();
            parent = parentCategory ?? string.Empty;
            name = collectionName ?? string.Empty;
            description = desc ?? string.Empty;
            coverPath = collectionCoverPath ?? string.Empty;
            count = itemCount;
            cover = coverBmp;
            memberPaths.Clear();
            if (memberImages != null)
            {
                for (int i = 0; i < memberImages.Count && i < MaxMemberThumbs; i++)
                    if (!string.IsNullOrEmpty(memberImages[i])) memberPaths.Add(memberImages[i]);
            }
            memberThumbsBuilt = false;
            Invalidate();
        }

        /// <summary>清空信息并释放封面与缩略图。</summary>
        public void Clear()
        {
            EndEdit();
            if (cover != null) { cover.Dispose(); cover = null; }
            DisposeMemberThumbs();
            parent = string.Empty;
            name = string.Empty;
            description = string.Empty;
            coverPath = string.Empty;
            count = 0;
            memberPaths.Clear();
            memberThumbsBuilt = false;
        }

        // ===== 页内编辑 =====

        /// <summary>进入编辑态：填好当前名称/简介，封面从当前自定义封面开始。</summary>
        public void BeginEdit()
        {
            if (name.Length == 0) return;
            EnsureEditFields();
            EndEdit();
            editing = true;
            editCoverPath = coverPath;
            editPreview = null;
            editPreviewCleared = false;
            editPasted = null;
            nameBox.Text = name;
            descBox.Text = description;
            namePanel.Visible = true;
            descPanel.Visible = true;
            stripScroll = 0;
            thumbHover = -1;
            EnsureMemberThumbs();
            LayoutChrome();
            nameBox.Focus();
            nameBox.SelectAll();
            Invalidate();
        }

        /// <summary>退出编辑态并丢弃未保存的封面预览/粘贴图片。</summary>
        public void EndEdit()
        {
            editing = false;
            editCoverPath = string.Empty;
            DisposeEditPreview();
            if (editPasted != null) { editPasted.Dispose(); editPasted = null; }
            if (namePanel != null) namePanel.Visible = false;
            if (descPanel != null) descPanel.Visible = false;
            stripScroll = 0;
            thumbHover = -1;
            thumbPress = -1;
            hoverBtn = -1;
            pressBtn = -1;
            Cursor = Cursors.Default;
            Invalidate();
        }

        /// <summary>设置编辑态封面预览（集合图片/文件选择）；会清除已粘贴的图片。</summary>
        public void SetEditCoverPreview(string path, Bitmap bmp)
        {
            if (!editing)
            {
                if (bmp != null) bmp.Dispose();
                return;
            }
            if (editPasted != null) { editPasted.Dispose(); editPasted = null; }
            DisposeEditPreview();
            editPreview = bmp;
            editPreviewCleared = bmp == null;
            editCoverPath = path ?? string.Empty;
            Invalidate();
        }

        /// <summary>
        /// 粘贴剪贴板内容作为封面：优先取图片位图，其次取剪贴板中的图片文件路径。
        /// 图片位图在保存时由主窗体写入受管封面目录。
        /// </summary>
        public void PasteCover()
        {
            if (!editing) return;
            try
            {
                if (Clipboard.ContainsImage())
                {
                    using (Image img = Clipboard.GetImage())
                    {
                        if (img != null)
                        {
                            SetPastedCover(new Bitmap(img));   // 复制一份，剪贴板对象随 using 释放
                            return;
                        }
                    }
                }
                string path = ClipboardImagePath();
                if (!string.IsNullOrEmpty(path))
                {
                    SelectCoverPath(path);
                    return;
                }
                MessageBox.Show(FindForm(), "剪贴板中没有可用的图片。\r\n可复制图片文件或图片内容后再试。",
                    "粘贴封面", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch
            {
                MessageBox.Show(FindForm(), "读取剪贴板失败，请稍后重试。", "粘贴封面",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static string ClipboardImagePath()
        {
            try
            {
                if (Clipboard.ContainsFileDropList())
                {
                    StringCollection files = Clipboard.GetFileDropList();
                    for (int i = 0; i < files.Count; i++)
                    {
                        string p = files[i];
                        if (!string.IsNullOrEmpty(p) && MediaRules.IsImage(p) && System.IO.File.Exists(p))
                            return p;
                    }
                }
                if (Clipboard.ContainsText())
                {
                    string t = (Clipboard.GetText() ?? string.Empty).Trim().Trim('"');
                    if (t.Length > 0 && MediaRules.IsImage(t) && System.IO.File.Exists(t)) return t;
                }
            }
            catch { }
            return null;
        }

        /// <summary>采纳粘贴的位图：限制长边尺寸，避免超大截图长期占用内存与磁盘。</summary>
        private void SetPastedCover(Bitmap bmp)
        {
            Bitmap normalized = NormalizePasted(bmp);
            if (editPasted != null) { editPasted.Dispose(); editPasted = null; }
            DisposeEditPreview();
            editPasted = normalized;
            editCoverPath = string.Empty;
            editPreviewCleared = false;
            Invalidate();
        }

        /// <summary>把粘贴图片的长边限制在 1600 像素内（缩放失败时保留原图）。</summary>
        private static Bitmap NormalizePasted(Bitmap src)
        {
            if (src == null) return null;
            try
            {
                const int max = 1600;
                int w = src.Width, h = src.Height;
                if (w <= max && h <= max) return src;
                double scale = (double)max / Math.Max(w, h);
                int dw = Math.Max(1, (int)Math.Round(w * scale));
                int dh = Math.Max(1, (int)Math.Round(h * scale));
                Bitmap dst = new Bitmap(dw, dh, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(dst))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(src, new Rectangle(0, 0, dw, dh));
                }
                src.Dispose();
                return dst;
            }
            catch { return src; }
        }

        private void DisposeEditPreview()
        {
            if (editPreview != null) { editPreview.Dispose(); editPreview = null; }
            editPreviewCleared = false;
        }

        private void DisposeMemberThumbs()
        {
            for (int i = 0; i < memberThumbs.Count; i++)
                if (memberThumbs[i] != null) memberThumbs[i].Dispose();
            memberThumbs.Clear();
            thumbPaths.Clear();
        }

        /// <summary>惰性构建集合内图片缩略图（最多 MaxMemberThumbs 张，解码失败则跳过）。</summary>
        private void EnsureMemberThumbs()
        {
            if (memberThumbsBuilt) return;
            memberThumbsBuilt = true;
            for (int i = 0; i < memberPaths.Count && memberThumbs.Count < MaxMemberThumbs; i++)
            {
                Bitmap b = Icons.LoadCoverThumb(memberPaths[i], ThumbSize, ThumbSize);
                if (b == null) continue;
                memberThumbs.Add(b);
                thumbPaths.Add(memberPaths[i]);
            }
        }

        private void EnsureEditFields()
        {
            if (namePanel != null) return;
            namePanel = MakeFieldPanel();
            nameBox = new TextBox();
            nameBox.BorderStyle = BorderStyle.None;
            nameBox.Font = Theme.FontOf(9.5F);
            nameBox.ForeColor = Theme.TextPrimary;
            nameBox.BackColor = Theme.SearchBg;
            nameBox.Location = new Point(10, 6);
            nameBox.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;
                Submit();
            };
            nameBox.GotFocus += delegate { nameBox.BackColor = Color.White; namePanel.Invalidate(); };
            nameBox.LostFocus += delegate { nameBox.BackColor = Theme.SearchBg; namePanel.Invalidate(); };
            namePanel.Controls.Add(nameBox);

            descPanel = MakeFieldPanel();
            descBox = new TextBox();
            descBox.Multiline = true;
            descBox.BorderStyle = BorderStyle.None;
            descBox.ScrollBars = ScrollBars.Vertical;
            descBox.Font = Theme.FontOf(9.5F);
            descBox.ForeColor = Theme.TextPrimary;
            descBox.BackColor = Theme.SearchBg;
            descBox.Location = new Point(10, 8);
            descBox.GotFocus += delegate { descBox.BackColor = Color.White; descPanel.Invalidate(); };
            descBox.LostFocus += delegate { descBox.BackColor = Theme.SearchBg; descPanel.Invalidate(); };
            descPanel.Controls.Add(descBox);

            namePanel.Visible = false;
            descPanel.Visible = false;
            Controls.Add(namePanel);
            Controls.Add(descPanel);
        }

        /// <summary>iOS 风格输入框底：圆角灰底，聚焦时白底 + 强调色描边。</summary>
        private static Panel MakeFieldPanel()
        {
            Panel p = new Panel();
            p.BackColor = Theme.Background;
            p.Paint += delegate(object s, PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle r = new Rectangle(0, 0, p.Width - 1, p.Height - 1);
                bool focused = p.Controls.Count > 0 && p.Controls[0].Focused;
                using (SolidBrush b = new SolidBrush(focused ? Color.White : Theme.SearchBg))
                    Draw.FillRoundedRect(g, b, r, 10);
                if (focused)
                {
                    using (Pen pen = new Pen(Theme.Accent, 1.5F))
                        Draw.DrawRoundedRect(g, pen, r, 10);
                }
            };
            return p;
        }

        /// <summary>提交编辑：名称非空校验后交由主窗体保存（重名等校验也在主窗体）。</summary>
        private void Submit()
        {
            if (!editing) return;
            string n = (nameBox.Text ?? string.Empty).Trim();
            if (n.Length == 0)
            {
                MessageBox.Show(FindForm(), "集合名称不能为空。", "编辑集合",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                nameBox.Focus();
                return;
            }
            Action<string, string, string, Bitmap> h = EditSaveRequested;
            if (h != null) h(n, (descBox.Text ?? string.Empty).Trim(), editCoverPath, editPasted);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (cover != null) { cover.Dispose(); cover = null; }
                DisposeEditPreview();
                if (editPasted != null) { editPasted.Dispose(); editPasted = null; }
                DisposeMemberThumbs();
            }
            base.Dispose(disposing);
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!Visible && editing) EndEdit();
        }

        // ===== 布局 =====

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Width > 0 && Height > 0)
            {
                LayoutChrome();
                ClampStripScroll();
            }
        }

        /// <summary>
        /// 按当前模式计算几何：封面在左、名称/简介在右，按钮等其余元素保持原有位置与样式。
        /// 绘制与命中测试共用。
        /// </summary>
        private void LayoutChrome()
        {
            int avail = Math.Min(760, Math.Max(400, Width - 96));
            int gx = (Width - avail) / 2;
            int coverW = Math.Min(300, Math.Max(180, (int)Math.Round(avail * 0.42)));
            int textW = Math.Max(160, avail - coverW - 24);
            textColumnX = gx + coverW + 24;
            textColumnW = textW;

            if (editing)
            {
                int coverH = Math.Min(170, Math.Max(110, Height - 320));
                int topRowH = Math.Max(coverH, 124);
                int total = topRowH + 12 + 18 + ThumbSize + 8 + SmallBtnH + 16 + BtnH;
                int y = Math.Max(20, (Height - total) / 2);
                coverRect = new Rectangle(gx, y + (topRowH - coverH) / 2, coverW, coverH);

                // 右侧：名称/简介（与封面同一行，纵向居中）
                nameCaptionY = y + (topRowH - 124) / 2;
                int nameY = nameCaptionY + 18;
                if (namePanel != null)
                {
                    namePanel.Bounds = new Rectangle(textColumnX, nameY, textW, 34);
                    nameBox.Bounds = new Rectangle(10, 6, textW - 20, 22);
                }
                descCaptionY = nameY + 34 + 8;
                int descY = descCaptionY + 18;
                if (descPanel != null)
                {
                    descPanel.Bounds = new Rectangle(textColumnX, descY, textW, 46);
                    descBox.Bounds = new Rectangle(10, 8, textW - 20, 30);
                }

                // 下方：封面来源缩略图条 + 封面操作按钮 + 保存/取消（占满可用宽度）
                stripCaptionY = y + topRowH + 12;
                stripRect = new Rectangle(gx, stripCaptionY + 18, avail, ThumbSize);
                int actionsY = stripRect.Bottom + 8;
                int actionsW = 96 + 8 + 112 + 8 + 88;
                int ax = gx + (avail - actionsW) / 2;
                btnPasteRect = new Rectangle(ax, actionsY, 96, SmallBtnH);
                btnFileRect = new Rectangle(btnPasteRect.Right + 8, actionsY, 112, SmallBtnH);
                btnAutoRect = new Rectangle(btnFileRect.Right + 8, actionsY, 88, SmallBtnH);
                int btnY = actionsY + SmallBtnH + 16;
                btnSaveRect = new Rectangle((Width - 104 - 8 - 88) / 2, btnY, 104, BtnH);
                btnCancelRect = new Rectangle(btnSaveRect.Right + 8, btnY, 88, BtnH);
                return;
            }

            int dCoverH = Math.Min(240, Math.Max(110, (int)Math.Round(coverW * 5.0 / 8.0)));
            int displayRowH = Math.Max(dCoverH, 124);
            int dTotal = displayRowH + 24 + BtnH;
            int dy = Math.Max(24, (Height - dTotal) / 2);
            coverRect = new Rectangle(gx, dy + (displayRowH - dCoverH) / 2, coverW, dCoverH);
            stripRect = Rectangle.Empty;

            int textY = dy + (displayRowH - 124) / 2;
            nameDisplayRect = new Rectangle(textColumnX, textY, textW, 30);
            descDisplayRect = new Rectangle(textColumnX, textY + 38, textW, 60);
            metaDisplayRect = new Rectangle(textColumnX, textY + 104, textW, 20);

            int btnY2 = dy + displayRowH + 24;
            btnEnterRect = new Rectangle((Width - 104 - 10 - 88) / 2, btnY2, 104, BtnH);
            btnEditRect = new Rectangle(btnEnterRect.Right + 10, btnY2, 88, BtnH);
        }

        // ===== 封面来源选择条 =====

        private int StripContentWidth
        {
            get
            {
                if (memberThumbs.Count == 0) return 0;
                return memberThumbs.Count * (ThumbSize + ThumbGap) - ThumbGap;
            }
        }

        private int MaxStripScroll
        {
            get
            {
                int m = StripContentWidth - stripRect.Width;
                return m > 0 ? m : 0;
            }
        }

        private void ClampStripScroll()
        {
            if (stripScroll < 0) stripScroll = 0;
            int m = MaxStripScroll;
            if (stripScroll > m) stripScroll = m;
        }

        /// <summary>滚轮横向滚动集合图片选择条（由主窗体滚轮路由调用）。</summary>
        public void ScrollThumbStrip(int delta)
        {
            if (!editing || delta == 0) return;
            int lines = SystemInformation.MouseWheelScrollLines;
            if (lines <= 0) lines = 3;
            stripScroll -= (delta / 120) * (ThumbSize + ThumbGap) * lines;
            ClampStripScroll();
            Invalidate();
        }

        /// <summary>屏幕坐标是否落在集合图片选择条上（供主窗体滚轮路由判定）。</summary>
        public bool ThumbStripContainsScreen(Point screen)
        {
            if (!Visible || !editing || memberThumbs.Count == 0) return false;
            Point p = PointToClient(screen);
            return stripRect.Contains(p);
        }

        /// <summary>选择集合内某张图片作为封面。</summary>
        private void SelectMemberCover(int index)
        {
            if (index < 0 || index >= thumbPaths.Count) return;
            SelectCoverPath(thumbPaths[index]);
        }

        private void SelectCoverPath(string path)
        {
            Bitmap preview = Icons.LoadCoverThumb(path, 320, 200);
            if (preview == null)
            {
                MessageBox.Show(FindForm(), "无法读取该图片，请选择常见的图片格式。", "选择封面",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (editPasted != null) { editPasted.Dispose(); editPasted = null; }
            DisposeEditPreview();
            editPreview = preview;
            editCoverPath = path ?? string.Empty;
            editPreviewCleared = false;
            Invalidate();
        }

        // ===== 绘制 =====

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Background);
            if (name.Length == 0 && !editing) return;

            LayoutChrome();

            // 封面：编辑态优先显示粘贴图片 / 已选预览，展示态显示当前封面
            Bitmap shown = cover;
            if (editing)
            {
                if (editPasted != null) shown = editPasted;
                else if (editPreview != null || editPreviewCleared) shown = editPreview;
            }
            Draw.DrawShadow(g, coverRect, 16, 0.7f);
            using (GraphicsPath cp = Draw.RoundedPath(coverRect, 16))
            {
                using (SolidBrush bg = new SolidBrush(Theme.Card))
                    g.FillPath(bg, cp);
                GraphicsState st = g.Save();
                g.SetClip(cp);
                if (shown != null) DrawCoverFilled(g, shown, coverRect);
                else PaintCoverPlaceholder(g, coverRect);
                g.Restore(st);
            }

            if (editing)
            {
                PaintMemberStrip(g);
                bool autoActive = editPasted == null && string.IsNullOrEmpty(editCoverPath);
                DrawButton(g, btnPasteRect, "粘贴封面", false, 8, true);
                DrawButton(g, btnFileRect, "从文件选择…", false, 3, true);
                DrawButton(g, btnAutoRect, "恢复自动", false, 4, !autoActive);
                PaintCaption(g, "名称", new Rectangle(textColumnX, nameCaptionY, textColumnW, 18));
                PaintCaption(g, "简介", new Rectangle(textColumnX, descCaptionY, textColumnW, 18));
                DrawButton(g, btnSaveRect, "保存", true, 5, true);
                DrawButton(g, btnCancelRect, "取消", false, 6, true);
                return;
            }

            // 封面在左，名称/简介/数量在右（文字左对齐，与 iOS 信息页一致）
            using (SolidBrush nb = new SolidBrush(Theme.TextPrimary))
                Draw.DrawStringLeft(g, name, Theme.FontOf(16F, FontStyle.Bold), nb, nameDisplayRect);

            string desc = description.Length == 0 ? "暂无简介" : description;
            TextRenderer.DrawText(g, desc, Theme.FontOf(9.5F), descDisplayRect, Theme.TextSecondary,
                TextFormatFlags.WordBreak | TextFormatFlags.Top | TextFormatFlags.EndEllipsis);

            string meta = (parent.Length == 0 ? string.Empty : parent + " · ") + count + " 项资源";
            using (SolidBrush sb = new SolidBrush(Theme.TextSecondary))
                Draw.DrawStringLeft(g, meta, Theme.FontOf(9F), sb, metaDisplayRect);

            DrawButton(g, btnEnterRect, "进入", true, 1, true);
            DrawButton(g, btnEditRect, "编辑", false, 2, true);
        }

        /// <summary>集合内图片选择条：优先从集合内容选封面；无图片时给出粘贴/文件提示。</summary>
        private void PaintMemberStrip(Graphics g)
        {
            PaintCaption(g, "从集合选择封面", new Rectangle(stripRect.X, stripCaptionY, stripRect.Width, 18));
            if (memberThumbs.Count == 0)
            {
                PaintCaption(g, "集合内暂无可选图片，可粘贴或从文件选择",
                    new Rectangle(stripRect.X, stripRect.Y, stripRect.Width, ThumbSize));
                return;
            }
            if (MaxStripScroll > 0)
            {
                using (SolidBrush hb = new SolidBrush(Theme.TextSecondary))
                    Draw.DrawStringRight(g, "滚轮查看更多", Theme.FontOf(8F), hb,
                        new Rectangle(stripRect.X, stripCaptionY, stripRect.Width, 18));
            }

            GraphicsState st = g.Save();
            g.SetClip(stripRect);
            int x = stripRect.X - stripScroll;
            for (int i = 0; i < memberThumbs.Count; i++)
            {
                Rectangle r = new Rectangle(x, stripRect.Y, ThumbSize, ThumbSize);
                x += ThumbSize + ThumbGap;
                if (r.Right < stripRect.X || r.X > stripRect.Right) continue;

                using (GraphicsPath cp = Draw.RoundedPath(r, 8))
                {
                    using (SolidBrush bg = new SolidBrush(Theme.Card))
                        g.FillPath(bg, cp);
                    GraphicsState ts = g.Save();
                    g.SetClip(cp);
                    g.DrawImage(memberThumbs[i], r);
                    g.Restore(ts);

                    bool selected = editPasted == null
                        && !string.IsNullOrEmpty(editCoverPath)
                        && string.Equals(editCoverPath, thumbPaths[i], StringComparison.OrdinalIgnoreCase);
                    if (selected)
                    {
                        using (Pen pen = new Pen(Theme.Accent, 2.4F))
                            g.DrawPath(pen, cp);
                    }
                    else if (i == thumbHover)
                    {
                        using (Pen pen = new Pen(Theme.Separator, 1F))
                            g.DrawPath(pen, cp);
                    }
                }
                if (IsSelectedThumb(i)) PaintThumbBadge(g, r);
            }
            g.Restore(st);
        }

        private bool IsSelectedThumb(int index)
        {
            return editPasted == null
                && !string.IsNullOrEmpty(editCoverPath)
                && index >= 0 && index < thumbPaths.Count
                && string.Equals(editCoverPath, thumbPaths[index], StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>选中缩略图的蓝色对勾角标（iOS 选择态）。</summary>
        private static void PaintThumbBadge(Graphics g, Rectangle r)
        {
            Rectangle bd = new Rectangle(r.Right - 12, r.Y + 3, 15, 15);
            using (SolidBrush b = new SolidBrush(Theme.Accent))
                g.FillEllipse(b, bd);
            using (Pen p = new Pen(Color.White, 1.6F))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                int cy = bd.Y + bd.Height / 2;
                g.DrawLine(p, bd.X + 3.5F, cy + 0.5F, bd.X + 6.5F, cy + 3.5F);
                g.DrawLine(p, bd.X + 6.5F, cy + 3.5F, bd.X + 11F, cy - 2.5F);
            }
        }

        private static void PaintCaption(Graphics g, string text, Rectangle r)
        {
            using (SolidBrush b = new SolidBrush(Theme.TextSecondary))
                Draw.DrawStringLeft(g, text, Theme.FontOf(8.5F), b, r);
        }

        /// <summary>统一按钮绘制：主按钮强调色实底，次按钮白底描边 + 强调色文字。</summary>
        private void DrawButton(Graphics g, Rectangle r, string text, bool primary, int id, bool enabled)
        {
            Color fill;
            if (!enabled) fill = Theme.SegmentBg;
            else if (primary)
            {
                fill = pressBtn == id ? Color.FromArgb(255, 0, 96, 200)
                    : (hoverBtn == id ? Color.FromArgb(255, 0, 108, 226) : Theme.Accent);
            }
            else
            {
                fill = pressBtn == id ? Theme.SegmentBg
                    : (hoverBtn == id ? Theme.CardHover : Theme.Card);
            }
            using (SolidBrush b = new SolidBrush(fill))
                Draw.FillRoundedRect(g, b, r, 10);
            if (!primary)
            {
                using (Pen p = new Pen(Theme.Separator, 1F))
                    Draw.DrawRoundedRect(g, p, r, 10);
            }
            Color tc = primary ? Color.White : (enabled ? Theme.Accent : Theme.TextSecondary);
            using (SolidBrush tb = new SolidBrush(tc))
                Draw.DrawStringCenter(g, text, Theme.FontOf(9.5F), tb, r);
        }

        /// <summary>等比裁剪填满封面（居中裁剪，类似 CSS cover）。</summary>
        private static void DrawCoverFilled(Graphics g, Bitmap bmp, Rectangle dest)
        {
            int bw = bmp.Width, bh = bmp.Height;
            if (bw <= 0 || bh <= 0) return;
            Rectangle src;
            if ((double)bw / bh > (double)dest.Width / dest.Height)
            {
                int w = (int)(bh * dest.Width / (double)dest.Height);
                src = new Rectangle((bw - w) / 2, 0, w, bh);
            }
            else
            {
                int h = (int)(bw * dest.Height / (double)dest.Width);
                src = new Rectangle(0, (bh - h) / 2, bw, h);
            }
            g.DrawImage(bmp, dest, src, GraphicsUnit.Pixel);
        }

        /// <summary>无封面占位：浅灰底 + 简约风景图形（按封面区尺寸版式化）。</summary>
        private static void PaintCoverPlaceholder(Graphics g, Rectangle r)
        {
            using (SolidBrush bg = new SolidBrush(Theme.ChipBg))
                g.FillRectangle(bg, r);
            using (Pen p = new Pen(Theme.TextSecondary, 1.8f))
            {
                int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
                g.DrawEllipse(p, cx - 16, cy - 18, 10, 10);
                using (GraphicsPath m = new GraphicsPath())
                {
                    m.AddLine(cx - 34, cy + 22, cx - 10, cy - 8);
                    m.AddLine(cx - 10, cy - 8, cx + 2, cy + 6);
                    m.AddLine(cx + 2, cy + 6, cx + 10, cy - 6);
                    m.AddLine(cx + 10, cy - 6, cx + 34, cy + 22);
                    g.DrawPath(p, m);
                }
            }
        }

        // ===== 交互 =====

        /// <summary>命中按钮编号：1 进入 2 编辑 3 从文件 4 恢复自动 5 保存 6 取消 8 粘贴。</summary>
        private int HitButton(Point p)
        {
            if (editing)
            {
                if (!string.IsNullOrEmpty(editCoverPath) && btnAutoRect.Contains(p)) return 4;
                if (editPasted != null && btnAutoRect.Contains(p)) return 4;
                if (btnPasteRect.Contains(p)) return 8;
                if (btnFileRect.Contains(p)) return 3;
                if (btnSaveRect.Contains(p)) return 5;
                if (btnCancelRect.Contains(p)) return 6;
                return -1;
            }
            if (btnEnterRect.Contains(p)) return 1;
            if (btnEditRect.Contains(p)) return 2;
            return -1;
        }

        /// <summary>按坐标命中集合图片选择条下标；未命中返回 -1。</summary>
        private int ThumbIndexAt(Point p)
        {
            if (!editing || memberThumbs.Count == 0 || !stripRect.Contains(p)) return -1;
            int x = p.X - stripRect.X + stripScroll;
            if (x < 0) return -1;
            int idx = x / (ThumbSize + ThumbGap);
            if (x - idx * (ThumbSize + ThumbGap) >= ThumbSize) return -1;
            return idx < memberThumbs.Count ? idx : -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int id = HitButton(e.Location);
            int thumb = id < 0 ? ThumbIndexAt(e.Location) : -1;
            if (id != hoverBtn || thumb != thumbHover)
            {
                hoverBtn = id;
                thumbHover = thumb;
                Invalidate();
            }
            Cursor = (id >= 0 || thumb >= 0) ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hoverBtn == -1 && pressBtn == -1 && thumbHover == -1) return;
            hoverBtn = -1;
            pressBtn = -1;
            thumbHover = -1;
            Cursor = Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            int thumb = ThumbIndexAt(e.Location);
            if (thumb >= 0)
            {
                thumbPress = thumb;
                Invalidate();
                return;
            }
            int id = HitButton(e.Location);
            if (id < 0) return;
            pressBtn = id;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (thumbPress >= 0)
            {
                int pressed = thumbPress;
                thumbPress = -1;
                Invalidate();
                if (ThumbIndexAt(e.Location) == pressed) SelectMemberCover(pressed);
                return;
            }
            if (pressBtn < 0) return;
            int id = pressBtn;
            pressBtn = -1;
            Invalidate();
            if (HitButton(e.Location) != id) return;
            switch (id)
            {
                case 1: EnterContents(); break;
                case 2: BeginEdit(); break;
                case 3: PickCoverFile(); break;
                case 4: AutoCover(); break;
                case 5: Submit(); break;
                case 6: EndEdit(); break;
                case 8: PasteCover(); break;
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (editing || e.Button != MouseButtons.Left) return;
            if (HitButton(e.Location) >= 0) return;   // 按钮单击已处理，忽略第二击
            EnterContents();                           // 双击信息页其它区域同样进入集合
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (editing && stripRect.Contains(e.Location))
            {
                ScrollThumbStrip(e.Delta);
                return;
            }
            base.OnMouseWheel(e);
        }

        /// <summary>请求进入集合内容（命名避开 Control.Enter 事件，防止隐藏基类成员）。</summary>
        private void EnterContents()
        {
            Action h = EnterRequested;
            if (h != null) h();
        }

        private void PickCoverFile()
        {
            Action h = CoverPickRequested;
            if (h != null) h();
        }

        private void AutoCover()
        {
            Action h = CoverAutoRequested;
            if (h != null) h();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
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

        // 集合内图片缩略图（编辑态分页选择；按原下标异步加载）
        private readonly List<string> memberPaths = new List<string>();
        private readonly Dictionary<int, Bitmap> memberThumbs = new Dictionary<int, Bitmap>();
        private bool memberThumbsBuilt;
        private bool thumbLoading;
        private volatile int thumbGen;
        private int thumbPage;
        private int thumbHover = -1;
        private int thumbPress = -1;

        // 编辑态输入控件（按需创建，隐藏时不占位）
        private Panel namePanel;
        private Panel descPanel;
        private TextBox nameBox;
        private TextBox descBox;

        // 布局缓存：命中测试与绘制共用
        private Rectangle coverRect;
        private Rectangle btnEnterRect;
        private Rectangle btnEditRect;
        private Rectangle btnPasteRect;
        private Rectangle btnFileRect;
        private Rectangle btnAutoRect;
        private Rectangle btnSaveRect;
        private Rectangle btnCancelRect;
        private Rectangle btnPrevPageRect;
        private Rectangle btnNextPageRect;
        private Rectangle nameDisplayRect;
        private Rectangle descDisplayRect;
        private Rectangle metaDisplayRect;
        private int textColumnX;
        private int textColumnW;
        private int pickerCaptionY;
        private int thumbsY;
        private int thumbStartX;
        private int thumbAreaWidth;
        private int thumbCols;
        private int thumbRows;
        private int thumbPerPage;
        private int nameCaptionY;
        private int descCaptionY;
        private int hoverBtn = -1;          // 1 进入 2 编辑 3 从文件 4 恢复自动 5 保存 6 取消 8 粘贴 10 上页 11 下页
        private int pressBtn = -1;

        private const int BtnH = 36;
        private const int ThumbSize = 72;
        private const int ThumbGap = 10;
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
            thumbGen++;              // 使在途加载结果失效
            thumbLoading = false;
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
            thumbPage = 0;
            Invalidate();
        }

        /// <summary>清空信息并释放封面与缩略图。</summary>
        public void Clear()
        {
            EndEdit();
            if (cover != null) { cover.Dispose(); cover = null; }
            thumbGen++;
            thumbLoading = false;
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
            thumbPage = 0;
            thumbHover = -1;
            StartThumbLoad();   // 异步加载，避免编辑页打开时批量解码大图卡住 UI
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
            if (thumbLoading)
            {
                thumbGen++;          // 取消在途加载，下次编辑重新加载
                thumbLoading = false;
                memberThumbsBuilt = false;
            }
            thumbPage = 0;
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
            foreach (Bitmap b in memberThumbs.Values)
                if (b != null) b.Dispose();
            memberThumbs.Clear();
        }

        /// <summary>
        /// 异步加载集合内图片缩略图：避免在 UI 线程批量解码大图导致编辑页卡死。
        /// 结果按原下标写入字典，加载完成后统一刷新；过期/取消的结果自动释放。
        /// </summary>
        private void StartThumbLoad()
        {
            if (memberThumbsBuilt) return;
            memberThumbsBuilt = true;
            if (memberPaths.Count == 0) return;

            int gen = ++thumbGen;
            List<string> paths = new List<string>(memberPaths);
            thumbLoading = true;
            Invalidate();
            ThreadPool.QueueUserWorkItem(delegate
            {
                Dictionary<int, Bitmap> loaded = new Dictionary<int, Bitmap>();
                try
                {
                    for (int i = 0; i < paths.Count && i < MaxMemberThumbs; i++)
                    {
                        if (gen != thumbGen) break;   // 已失效，尽快退出
                        Bitmap b = Icons.LoadCoverThumb(paths[i], ThumbSize, ThumbSize);
                        if (b != null) loaded[i] = b;
                    }
                }
                catch { }
                try
                {
                    BeginInvoke((Action)delegate
                    {
                        if (gen != thumbGen || !editing)
                        {
                            // 过期结果：仅释放，不改动新一轮加载的状态
                            foreach (Bitmap b in loaded.Values) b.Dispose();
                            Invalidate();
                            return;
                        }
                        thumbLoading = false;
                        DisposeMemberThumbs();
                        foreach (KeyValuePair<int, Bitmap> kv in loaded)
                            memberThumbs[kv.Key] = kv.Value;
                        Invalidate();
                    });
                }
                catch
                {
                    foreach (Bitmap b in loaded.Values) b.Dispose();
                }
            });
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
                thumbGen++;          // 使在途加载结果失效
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
            if (Width > 0 && Height > 0) LayoutChrome();
        }

        /// <summary>
        /// 按当前模式计算几何：左列封面、右列名称/简介，各自在列内居中；
        /// 按钮行（含编辑态保存/取消）横向中心对齐封面中心。绘制与命中测试共用。
        /// </summary>
        private void LayoutChrome()
        {
            int avail = Math.Min(760, Math.Max(400, Width - 96));
            int gx = (Width - avail) / 2;
            int leftW = avail / 2;
            int coverW = editing
                ? Math.Min(320, Math.Max(220, (int)Math.Round(avail * 0.46)))
                : Math.Min(280, Math.Max(160, leftW - 32));
            int coverX = editing ? gx : gx + (leftW - coverW) / 2;
            textColumnX = editing ? gx + coverW + 24 : gx + leftW + 24;
            textColumnW = editing ? Math.Max(180, avail - coverW - 24) : Math.Max(150, avail - leftW - 48);

            if (editing)
            {
                // 编辑态：上方大封面 + 右侧名称/高简介；下方集合图片分页选择；保存/取消固定在右下角
                int coverH = Math.Min(230, Math.Max(140, (int)Math.Round(coverW * 5.0 / 8.0)));
                int descH = Math.Min(130, Math.Max(100, Height - 330));
                int fieldsH = 18 + 40 + 12 + 18 + descH;
                int topY = 24;
                int topH = Math.Max(coverH, fieldsH);
                coverRect = new Rectangle(coverX, topY + (topH - coverH) / 2, coverW, coverH);

                nameCaptionY = topY + (topH - fieldsH) / 2;
                int nameY = nameCaptionY + 18;
                if (namePanel != null)
                {
                    Rectangle npr = new Rectangle(textColumnX, nameY, textColumnW, 40);
                    Rectangle nbr = new Rectangle(10, 8, textColumnW - 20, 24);
                    if (namePanel.Bounds != npr) namePanel.Bounds = npr;
                    if (nameBox.Bounds != nbr) nameBox.Bounds = nbr;
                }
                descCaptionY = nameY + 40 + 12;
                int descY = descCaptionY + 18;
                if (descPanel != null)
                {
                    Rectangle dpr = new Rectangle(textColumnX, descY, textColumnW, descH);
                    Rectangle dbr = new Rectangle(10, 8, textColumnW - 20, descH - 16);
                    if (descPanel.Bounds != dpr) descPanel.Bounds = dpr;
                    if (descBox.Bounds != dbr) descBox.Bounds = dbr;
                }

                // 集合图片选择：标题行右侧翻页；缩略图按可用高度自动 1~3 行分页展示
                thumbAreaWidth = avail;
                thumbStartX = gx;
                pickerCaptionY = topY + topH + 16;
                thumbsY = pickerCaptionY + 20;
                int bottomY = Height - 16 - BtnH;
                int gridAvail = bottomY - 16 - thumbsY;
                thumbCols = ThumbColumns(avail);
                thumbRows = (gridAvail + ThumbGap) / (ThumbSize + ThumbGap);
                if (thumbRows < 1) thumbRows = 1;
                if (thumbRows > 3) thumbRows = 3;
                thumbPerPage = thumbCols * thumbRows;
                int pages = ThumbPageCount();
                if (thumbPage > pages - 1) thumbPage = pages - 1;
                if (thumbPage < 0) thumbPage = 0;
                btnNextPageRect = new Rectangle(gx + avail - 22, pickerCaptionY, 22, 20);
                btnPrevPageRect = new Rectangle(btnNextPageRect.Left - 76, pickerCaptionY, 22, 20);

                // 底部操作条：左侧封面来源，右下角保存/取消
                btnPasteRect = new Rectangle(gx, bottomY, 96, BtnH);
                btnFileRect = new Rectangle(btnPasteRect.Right + 8, bottomY, 112, BtnH);
                btnAutoRect = new Rectangle(btnFileRect.Right + 8, bottomY, 88, BtnH);
                int saveW = 104, cancelW = 88, btnGap = 8;
                int sx = Width - 16 - saveW - btnGap - cancelW;
                btnCancelRect = new Rectangle(sx, bottomY, cancelW, BtnH);
                btnSaveRect = new Rectangle(btnCancelRect.Right + btnGap, bottomY, saveW, BtnH);
                return;
            }

            int dCoverH = Math.Min(240, Math.Max(110, (int)Math.Round(coverW * 5.0 / 8.0)));
            int displayRowH = Math.Max(dCoverH, 124);
            int dTotal = displayRowH + 24 + BtnH;
            int dy = Math.Max(24, (Height - dTotal) / 2);
            coverRect = new Rectangle(coverX, dy + (displayRowH - dCoverH) / 2, coverW, dCoverH);

            int textY = dy + (displayRowH - 124) / 2;
            nameDisplayRect = new Rectangle(textColumnX, textY, textColumnW, 30);
            descDisplayRect = new Rectangle(textColumnX, textY + 38, textColumnW, 60);
            metaDisplayRect = new Rectangle(textColumnX, textY + 104, textColumnW, 20);

            int btnW = 104 + 10 + 88;
            int bx = Math.Max(8, Math.Min(coverRect.X + (coverRect.Width - btnW) / 2, Width - 8 - btnW));
            int btnY2 = dy + displayRowH + 24;
            btnEnterRect = new Rectangle(bx, btnY2, 104, BtnH);
            btnEditRect = new Rectangle(btnEnterRect.Right + 10, btnY2, 88, BtnH);
        }

        // ===== 集合图片分页选择 =====

        /// <summary>每行缩略图数量：按可用宽度自适应，3~8 张。</summary>
        private int ThumbColumns(int avail)
        {
            int n = (avail + ThumbGap) / (ThumbSize + ThumbGap);
            if (n < 3) n = 3;
            if (n > 8) n = 8;
            return n;
        }

        /// <summary>总页数（无图片时按 1 页处理，便于布局稳定）。</summary>
        private int ThumbPageCount()
        {
            if (memberPaths.Count == 0 || thumbPerPage <= 0) return 1;
            return (memberPaths.Count + thumbPerPage - 1) / thumbPerPage;
        }

        /// <summary>翻页：越界自动夹紧，翻页后清空缩略图悬停。</summary>
        private void ChangeThumbPage(int delta)
        {
            int pages = ThumbPageCount();
            int p = thumbPage + delta;
            if (p < 0) p = 0;
            if (p > pages - 1) p = pages - 1;
            if (p == thumbPage) return;
            thumbPage = p;
            thumbHover = -1;
            Invalidate();
        }

        /// <summary>选择集合内某张图片作为封面。</summary>
        private void SelectMemberCover(int index)
        {
            if (index < 0 || index >= memberPaths.Count) return;
            SelectCoverPath(memberPaths[index]);
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
                PaintMemberPicker(g);
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

            // 封面在左、文字在右，两侧各自在列内居中
            using (SolidBrush nb = new SolidBrush(Theme.TextPrimary))
                Draw.DrawStringCenter(g, name, Theme.FontOf(16F, FontStyle.Bold), nb, nameDisplayRect);

            string desc = description.Length == 0 ? "暂无简介" : description;
            TextRenderer.DrawText(g, desc, Theme.FontOf(9.5F), descDisplayRect, Theme.TextSecondary,
                TextFormatFlags.WordBreak | TextFormatFlags.HorizontalCenter | TextFormatFlags.Top
                | TextFormatFlags.EndEllipsis);

            string meta = (parent.Length == 0 ? string.Empty : parent + " · ") + count + " 项资源";
            using (SolidBrush sb = new SolidBrush(Theme.TextSecondary))
                Draw.DrawStringCenter(g, meta, Theme.FontOf(9F), sb, metaDisplayRect);

            DrawButton(g, btnEnterRect, "进入", true, 1, true);
            DrawButton(g, btnEditRect, "编辑", false, 2, true);
        }

        /// <summary>集合图片分页选择：标题 + 翻页 + 1~3 行大缩略图；无图片时给出粘贴/文件提示。</summary>
        private void PaintMemberPicker(Graphics g)
        {
            int perPage = thumbPerPage;
            int pages = ThumbPageCount();
            PaintCaption(g, thumbLoading ? "从集合选择封面 · 加载中…" : "从集合选择封面",
                new Rectangle(thumbStartX, pickerCaptionY, Math.Max(0, thumbAreaWidth - 104), 20));

            if (memberPaths.Count == 0)
            {
                PaintCaption(g, "集合内暂无可选图片，可粘贴或从文件选择",
                    new Rectangle(thumbStartX, thumbsY, thumbAreaWidth, 24));
                return;
            }

            // 翻页控件：‹ 页码 ›
            PaintPageArrow(g, btnPrevPageRect, false, thumbPage > 0);
            PaintPageArrow(g, btnNextPageRect, true, thumbPage < pages - 1);
            string pageText = (thumbPage + 1) + " / " + pages;
            using (SolidBrush pb = new SolidBrush(pages > 1 ? Theme.TextSecondary : Theme.Separator))
                Draw.DrawStringCenter(g, pageText, Theme.FontOf(8.5F), pb,
                    new Rectangle(btnPrevPageRect.Right, pickerCaptionY,
                        btnNextPageRect.Left - btnPrevPageRect.Right, 20));

            int start = thumbPage * perPage;
            for (int i = 0; i < perPage; i++)
            {
                int idx = start + i;
                if (idx >= memberPaths.Count) break;
                int row = thumbCols > 0 ? i / thumbCols : 0;
                int col = thumbCols > 0 ? i % thumbCols : 0;
                Rectangle r = new Rectangle(thumbStartX + col * (ThumbSize + ThumbGap),
                    thumbsY + row * (ThumbSize + ThumbGap), ThumbSize, ThumbSize);
                using (GraphicsPath cp = Draw.RoundedPath(r, 10))
                {
                    using (SolidBrush bg = new SolidBrush(Theme.Card))
                        g.FillPath(bg, cp);
                    GraphicsState ts = g.Save();
                    g.SetClip(cp);
                    Bitmap thumb;
                    if (memberThumbs.TryGetValue(idx, out thumb) && thumb != null)
                        g.DrawImage(thumb, r);
                    else
                    {
                        // 缩略图仍在异步加载或解码失败：浅灰占位，不阻塞界面
                        using (SolidBrush ph = new SolidBrush(Theme.ChipBg))
                            g.FillRectangle(ph, r);
                    }
                    g.Restore(ts);

                    if (IsSelectedThumb(idx))
                    {
                        using (Pen pen = new Pen(Theme.Accent, 2.4F))
                            g.DrawPath(pen, cp);
                    }
                    else if (idx == thumbHover)
                    {
                        using (Pen pen = new Pen(Theme.Separator, 1F))
                            g.DrawPath(pen, cp);
                    }
                }
                if (IsSelectedThumb(idx)) PaintThumbBadge(g, r);
            }
        }

        /// <summary>翻页箭头：左右朝向的小箭头，禁用时弱化。</summary>
        private static void PaintPageArrow(Graphics g, Rectangle r, bool right, bool enabled)
        {
            if (r.Width <= 0) return;
            Color c = enabled ? Theme.Accent : Theme.Separator;
            float cx = r.X + r.Width / 2F, cy = r.Y + r.Height / 2F;
            using (Pen p = new Pen(c, 1.8F))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                if (right)
                {
                    g.DrawLine(p, cx - 3, cy - 5, cx + 2, cy);
                    g.DrawLine(p, cx + 2, cy, cx - 3, cy + 5);
                }
                else
                {
                    g.DrawLine(p, cx + 3, cy - 5, cx - 2, cy);
                    g.DrawLine(p, cx - 2, cy, cx + 3, cy + 5);
                }
            }
        }

        private bool IsSelectedThumb(int index)
        {
            return editPasted == null
                && !string.IsNullOrEmpty(editCoverPath)
                && index >= 0 && index < memberPaths.Count
                && string.Equals(editCoverPath, memberPaths[index], StringComparison.OrdinalIgnoreCase);
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
                int pages = ThumbPageCount();
                if (memberPaths.Count > 0 && pages > 1)
                {
                    if (thumbPage > 0 && btnPrevPageRect.Contains(p)) return 10;
                    if (thumbPage < pages - 1 && btnNextPageRect.Contains(p)) return 11;
                }
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

        /// <summary>按坐标命中当前页的缩略图下标（多行多列）；未命中返回 -1。</summary>
        private int ThumbIndexAt(Point p)
        {
            if (!editing || memberPaths.Count == 0 || thumbCols <= 0) return -1;
            if (p.Y < thumbsY) return -1;
            int dy = p.Y - thumbsY;
            int row = dy / (ThumbSize + ThumbGap);
            if (row < 0 || row >= thumbRows) return -1;
            if (dy - row * (ThumbSize + ThumbGap) >= ThumbSize) return -1;
            int x = p.X - thumbStartX;
            if (x < 0) return -1;
            int col = x / (ThumbSize + ThumbGap);
            if (col < 0 || col >= thumbCols) return -1;
            if (x - col * (ThumbSize + ThumbGap) >= ThumbSize) return -1;
            int idx = thumbPage * thumbPerPage + row * thumbCols + col;
            return idx < memberPaths.Count ? idx : -1;
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
                case 10: ChangeThumbPage(-1); break;
                case 11: ChangeThumbPage(1); break;
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (editing || e.Button != MouseButtons.Left) return;
            if (HitButton(e.Location) >= 0) return;   // 按钮单击已处理，忽略第二击
            EnterContents();                           // 双击信息页其它区域同样进入集合
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

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
        private float coverFocusX = 0.5F;   // 当前封面的取景焦点（0~1，居中为 0.5）
        private float coverFocusY = 0.5F;
        private Bitmap editPreview;         // 编辑态封面预览（集合内图片/文件选择的整图，可拖动取景）
        private bool editPreviewCleared;    // 预览为空时按占位绘制（表示「恢复自动且无可用封面」）
        private Bitmap editPasted;          // 粘贴的剪贴板图片（保存时交由主窗体落盘）
        private bool editing;
        private string editCoverPath = string.Empty;
        private float editFocusX = 0.5F;    // 编辑态取景焦点（拖动调整，保存后持久化）
        private float editFocusY = 0.5F;

        // 封面拖动取景
        private bool coverDragging;
        private Point coverDragStart;
        private float coverDragFocusX;
        private float coverDragFocusY;

        // 集合内图片缩略图（编辑态分页选择；按原下标异步加载）
        private readonly List<string> memberPaths = new List<string>();
        private readonly Dictionary<int, Bitmap> memberThumbs = new Dictionary<int, Bitmap>();
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
        // 编辑态：左右卡片
        private Rectangle editCardRect;
        private Rectangle pickerCardRect;
        private bool pickerVisible;
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
        private bool layoutDirty = true;     // 布局缓存失效标记：仅在尺寸/状态变化时重算
        private int previewGen;              // 编辑态封面原图异步加载代次

        private const int BtnH = 36;
        private const int ThumbSize = 72;
        private const int ThumbGap = 10;
        private const int MaxMemberThumbs = 60;
        private const int CoverSourceMax = 1200;   // 编辑态取景原图的长边上限（足够大图预览，控制内存占用）

        /// <summary>点击「进入」：由主窗体切换到集合内容。</summary>
        public event Action EnterRequested;
        /// <summary>点「保存」：名称 + 简介 + 封面路径 + 粘贴图片 + 取景焦点（校验与落盘由主窗体处理）。</summary>
        public event Action<string, string, string, Bitmap, float, float> EditSaveRequested;
        /// <summary>点「从文件选择…」：由主窗体选择图片并回传路径。</summary>
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
            string collectionCoverPath, float focusX, float focusY, Bitmap coverBmp, List<string> memberImages)
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
            coverFocusX = ClampFocus(focusX);
            coverFocusY = ClampFocus(focusY);
            count = itemCount;
            cover = coverBmp;
            memberPaths.Clear();
            if (memberImages != null)
            {
                for (int i = 0; i < memberImages.Count && i < MaxMemberThumbs; i++)
                    if (!string.IsNullOrEmpty(memberImages[i])) memberPaths.Add(memberImages[i]);
            }
            thumbPage = 0;
            layoutDirty = true;
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
            layoutDirty = true;
        }

        // ===== 页内编辑 =====

        /// <summary>进入编辑态：填好当前名称/简介，封面从当前自定义封面开始（无自定义时取首个图片成员）。</summary>
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
            editFocusX = coverFocusX;
            editFocusY = coverFocusY;
            // 预览源：自定义封面或集合首个图片成员；先展示已有封面，整图异步载入后再替换（可拖动取景）
            string src = !string.IsNullOrEmpty(coverPath)
                ? coverPath
                : (memberPaths.Count > 0 ? memberPaths[0] : null);
            if (!string.IsNullOrEmpty(src)) StartPreviewLoad(src);
            nameBox.Text = name;
            descBox.Text = description;
            namePanel.Visible = true;
            descPanel.Visible = true;
            thumbPage = 0;
            thumbHover = -1;
            StartThumbLoad();
            layoutDirty = true;
            EnsureLayout();
            nameBox.Focus();
            nameBox.SelectAll();
            Invalidate();
        }

        /// <summary>退出编辑态并丢弃未保存的封面预览/粘贴图片。</summary>
        public void EndEdit()
        {
            editing = false;
            editCoverPath = string.Empty;
            previewGen++;            // 作废在途的封面原图加载
            DisposeEditPreview();
            if (editPasted != null) { editPasted.Dispose(); editPasted = null; }
            editFocusX = 0.5F;
            editFocusY = 0.5F;
            coverDragging = false;
            layoutDirty = true;
            if (namePanel != null) namePanel.Visible = false;
            if (descPanel != null) descPanel.Visible = false;
            if (thumbLoading)
            {
                thumbGen++;          // 取消在途加载（已解码的缩略图保留，下次编辑直接复用）
                thumbLoading = false;
            }
            thumbPage = 0;
            thumbHover = -1;
            thumbPress = -1;
            hoverBtn = -1;
            pressBtn = -1;
            Cursor = Cursors.Default;
            Invalidate();
        }

        /// <summary>
        /// 设置编辑态封面（集合内图片或从文件选择）：取景焦点回到居中，
        /// 整图在后台载入（到位前保留当前预览），避免点击缩略图时 UI 线程同步解码大图卡顿。
        /// coverPath 为空表示「恢复自动」。
        /// </summary>
        public void SetEditCoverPath(string coverPath, string imagePath)
        {
            if (!editing) return;
            if (!string.IsNullOrEmpty(imagePath)
                && (!MediaRules.IsImage(imagePath) || !System.IO.File.Exists(imagePath)))
            {
                MessageBox.Show(FindForm(), "无法读取该图片，请选择常见的图片格式。", "选择封面",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (editPasted != null) { editPasted.Dispose(); editPasted = null; }
            editCoverPath = coverPath ?? string.Empty;
            editFocusX = 0.5F;
            editFocusY = 0.5F;
            if (string.IsNullOrEmpty(imagePath))
            {
                previewGen++;
                DisposeEditPreview();
                editPreviewCleared = true;
                Invalidate();
                return;
            }
            StartPreviewLoad(imagePath);
            Invalidate();
        }

        /// <summary>异步加载封面取景原图：先展示已有封面/占位，载入完成后替换并支持拖动取景。</summary>
        private void StartPreviewLoad(string path)
        {
            int gen = ++previewGen;
            ThreadPool.QueueUserWorkItem(delegate
            {
                Bitmap bmp = null;
                try { bmp = Icons.LoadCoverSource(path, CoverSourceMax); }
                catch { }
                try
                {
                    BeginInvoke((Action)delegate
                    {
                        if (gen != previewGen || !editing) { if (bmp != null) bmp.Dispose(); return; }
                        if (bmp == null) return;   // 解码失败：保留现有封面预览
                        DisposeEditPreview();
                        editPreview = bmp;
                        editPreviewCleared = false;
                        Invalidate();
                    });
                }
                catch { if (bmp != null) bmp.Dispose(); }
            });
        }

        /// <summary>设置编辑态封面预览位图（无可用图片成员时的版式化封面）；所有权移交本控件。</summary>
        public void SetEditCoverBitmap(string coverPath, Bitmap bmp)
        {
            if (!editing)
            {
                if (bmp != null) bmp.Dispose();
                return;
            }
            ReplaceCoverPreview(bmp, coverPath);
        }

        /// <summary>统一替换编辑态封面预览：清掉旧预览/粘贴图，取景焦点重置为居中。</summary>
        private void ReplaceCoverPreview(Bitmap bmp, string coverPath)
        {
            previewGen++;           // 作废在途的封面原图加载
            if (editPasted != null) { editPasted.Dispose(); editPasted = null; }
            DisposeEditPreview();
            editPreview = bmp;
            editPreviewCleared = bmp == null;
            editCoverPath = coverPath ?? string.Empty;
            editFocusX = 0.5F;
            editFocusY = 0.5F;
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
            previewGen++;           // 作废在途的封面原图加载
            if (editPasted != null) { editPasted.Dispose(); editPasted = null; }
            DisposeEditPreview();
            editPasted = normalized;
            editCoverPath = string.Empty;
            editPreviewCleared = false;
            editFocusX = 0.5F;
            editFocusY = 0.5F;
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
        /// 异步并行加载集合内图片缩略图：优先保证首页可见，解码完成一张立即局部刷新一张，
        /// 避免阻塞 UI 线程与整页重绘造成的卡顿；结果按原下标写入字典，过期/取消的结果自动释放。
        /// </summary>
        private void StartThumbLoad()
        {
            if (thumbLoading || memberPaths.Count == 0) return;
            int total = Math.Min(memberPaths.Count, MaxMemberThumbs);
            // 已加载过的下标直接跳过（重复进入编辑页不重复解码）
            bool[] done = new bool[total];
            int missing = 0;
            for (int i = 0; i < total; i++)
            {
                done[i] = memberThumbs.ContainsKey(i);
                if (!done[i]) missing++;
            }
            if (missing == 0) return;

            int gen = ++thumbGen;
            List<string> paths = new List<string>(memberPaths);
            thumbLoading = true;
            Invalidate();

            int cursor = -1;
            int active = Math.Min(3, Math.Max(1, Environment.ProcessorCount / 2));
            for (int w = 0; w < active; w++)
            {
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        while (true)
                        {
                            int idx = Interlocked.Increment(ref cursor);
                            if (idx >= total || gen != thumbGen) break;
                            if (done[idx]) continue;
                            Bitmap b = null;
                            try { b = Icons.LoadCoverThumb(paths[idx], ThumbSize, ThumbSize); }
                            catch { }
                            if (gen != thumbGen) { if (b != null) b.Dispose(); break; }
                            if (b != null) PostThumbLoaded(gen, idx, b);
                        }
                    }
                    finally
                    {
                        if (Interlocked.Decrement(ref active) == 0) PostThumbLoadDone(gen);
                    }
                });
            }
        }

        /// <summary>单张缩略图就绪：只重绘该缩略图所在区域（不在当前页则留待翻页时绘制）。</summary>
        private void PostThumbLoaded(int gen, int index, Bitmap bmp)
        {
            try
            {
                BeginInvoke((Action)delegate
                {
                    if (gen != thumbGen) { bmp.Dispose(); return; }
                    Bitmap old;
                    if (memberThumbs.TryGetValue(index, out old) && old != null) old.Dispose();
                    memberThumbs[index] = bmp;
                    Rectangle r = ThumbRectFor(index);
                    if (r.Width > 0 && r.Height > 0) Invalidate(r);
                });
            }
            catch { bmp.Dispose(); }
        }

        /// <summary>全部加载完成：结束「加载中」提示。</summary>
        private void PostThumbLoadDone(int gen)
        {
            try
            {
                BeginInvoke((Action)delegate
                {
                    if (gen != thumbGen) return;
                    thumbLoading = false;
                    Invalidate();
                });
            }
            catch { }
        }

        private void EnsureEditFields()
        {
            if (namePanel != null) return;
            namePanel = MakeFieldPanel();
            nameBox = new TextBox();
            nameBox.BorderStyle = BorderStyle.None;
            nameBox.Font = Theme.BodyFont;
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
            nameBox.TextChanged += delegate { if (editing) Invalidate(); };   // 右侧预览随输入实时刷新
            namePanel.Controls.Add(nameBox);

            descPanel = MakeFieldPanel();
            descBox = new TextBox();
            descBox.Multiline = true;
            descBox.BorderStyle = BorderStyle.None;
            descBox.ScrollBars = ScrollBars.Vertical;
            descBox.Font = Theme.BodyFont;
            descBox.ForeColor = Theme.TextPrimary;
            descBox.BackColor = Theme.SearchBg;
            descBox.Location = new Point(10, 8);
            descBox.GotFocus += delegate { descBox.BackColor = Color.White; descPanel.Invalidate(); };
            descBox.LostFocus += delegate { descBox.BackColor = Theme.SearchBg; descPanel.Invalidate(); };
            descBox.TextChanged += delegate { if (editing) Invalidate(); };   // 右侧预览随输入实时刷新
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
            Action<string, string, string, Bitmap, float, float> h = EditSaveRequested;
            if (h != null) h(n, (descBox.Text ?? string.Empty).Trim(), editCoverPath, editPasted,
                editFocusX, editFocusY);
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
            layoutDirty = true;   // 仅标记失效，几何在下次绘制/命中测试时重算
        }

        /// <summary>布局按需计算：仅在尺寸/状态变化后重算，避免每次重绘都重复计算并触发子控件重排。</summary>
        private void EnsureLayout()
        {
            if (!layoutDirty) return;
            layoutDirty = false;
            LayoutChrome();
        }

        /// <summary>
        /// 按当前模式计算几何：展示态左列封面、右列名称/简介，各自在列内居中；
        /// 编辑态左卡「编辑信息」（封面预览 + 名称/简介）、右卡「封面」（集合图片选择）；
        /// 按钮行（含编辑态保存/取消）横向中心对齐封面中心。绘制与命中测试共用。
        /// </summary>
        private void LayoutChrome()
        {
            int avail = Math.Min(760, Math.Max(400, Width - 96));
            int gx = (Width - avail) / 2;
            int leftW = avail / 2;
            int coverW = Math.Min(280, Math.Max(160, leftW - 32));
            int coverX = gx + (leftW - coverW) / 2;
            textColumnX = gx + leftW + 24;
            textColumnW = Math.Max(150, avail - leftW - 48);

            if (editing)
            {
                // 编辑态：左侧「编辑信息」卡片（封面预览在上、名称/简介在下），
                // 右侧「封面」卡片（集合图片选择）；封面预览按住可拖动调整取景。
                int bottomY = Height - 16 - BtnH;
                const int labelH = 18;
                const int labelGap = 6;
                const int gap = 12;
                int cardTop = 16 + labelH + labelGap;
                int topH = bottomY - gap - cardTop;
                if (topH < 220) topH = 220;

                int colGap = 16;
                int leftW2 = (avail - colGap) / 2;
                int rightW2 = avail - leftW2 - colGap;
                editCardRect = new Rectangle(gx, cardTop, leftW2, topH);
                pickerCardRect = new Rectangle(gx + leftW2 + colGap, cardTop, rightW2, topH);
                pickerVisible = true;

                // 左卡：封面预览（8:5，尽量吃满卡片宽度）+ 名称/简介
                int innerW = leftW2 - 32;
                int maxCoverH = topH - 172;      // 为下方名称/简介输入区预留固定高度
                if (maxCoverH < 96) maxCoverH = 96;
                int editCoverW = innerW;
                int editCoverH = (int)Math.Round(editCoverW * 5.0 / 8.0);
                if (editCoverH > maxCoverH || editCoverH > 280)
                {
                    editCoverH = Math.Min(maxCoverH, 280);
                    editCoverW = Math.Max(120, (int)Math.Round(editCoverH * 8.0 / 5.0));
                    if (editCoverW > innerW)
                    {
                        editCoverW = innerW;
                        editCoverH = (int)Math.Round(editCoverW * 5.0 / 8.0);
                    }
                }
                int coverX2 = gx + (leftW2 - editCoverW) / 2;
                int coverY2 = cardTop + 16;
                coverRect = new Rectangle(coverX2, coverY2, editCoverW, editCoverH);

                textColumnX = gx + 16;
                textColumnW = innerW;
                nameCaptionY = coverRect.Bottom + 10;
                int nameY = nameCaptionY + 18;
                if (namePanel != null)
                {
                    Rectangle npr = new Rectangle(textColumnX, nameY, textColumnW, 40);
                    Rectangle nbr = new Rectangle(10, 8, textColumnW - 20, 24);
                    if (namePanel.Bounds != npr) namePanel.Bounds = npr;
                    if (nameBox.Bounds != nbr) nameBox.Bounds = nbr;
                }
                descCaptionY = nameY + 40 + 8;
                int descY = descCaptionY + 18;
                int descH = cardTop + topH - 16 - descY;
                if (descH < 44) descH = 44;
                if (descH > 140) descH = 140;
                if (descPanel != null)
                {
                    Rectangle dpr = new Rectangle(textColumnX, descY, textColumnW, descH);
                    Rectangle dbr = new Rectangle(10, 8, textColumnW - 20, descH - 16);
                    if (descPanel.Bounds != dpr) descPanel.Bounds = dpr;
                    if (descBox.Bounds != dbr) descBox.Bounds = dbr;
                }

                // 右卡：集合图片选择（标题 + 翻页 + 多行缩略图，行数随卡片高度自适应）
                pickerCaptionY = pickerCardRect.Y + 12;
                thumbStartX = pickerCardRect.X + 16;
                thumbAreaWidth = pickerCardRect.Width - 32;
                thumbsY = pickerCaptionY + 24;
                thumbCols = ThumbColumns(thumbAreaWidth);
                int rows = (pickerCardRect.Bottom - 12 - thumbsY + ThumbGap) / (ThumbSize + ThumbGap);
                if (rows < 1) rows = 1;
                if (rows > 6) rows = 6;
                thumbRows = rows;
                thumbPerPage = thumbCols * thumbRows;
                int pages = ThumbPageCount();
                if (thumbPage > pages - 1) thumbPage = pages - 1;
                if (thumbPage < 0) thumbPage = 0;
                btnNextPageRect = new Rectangle(pickerCardRect.Right - 16 - 22, pickerCaptionY, 22, 20);
                btnPrevPageRect = new Rectangle(btnNextPageRect.Left - 76, pickerCaptionY, 22, 20);

                // 底部操作条：左右两组按钮等宽等高，分别与上方左右卡片的外边缘对齐
                int actionW = Math.Min(104, Math.Max(64, (avail - 40) / 5));
                const int btnGap = 8;
                btnPasteRect = new Rectangle(gx, bottomY, actionW, BtnH);
                btnFileRect = new Rectangle(btnPasteRect.Right + btnGap, bottomY, actionW, BtnH);
                btnAutoRect = new Rectangle(btnFileRect.Right + btnGap, bottomY, actionW, BtnH);
                btnSaveRect = new Rectangle(gx + avail - actionW, bottomY, actionW, BtnH);
                btnCancelRect = new Rectangle(btnSaveRect.Left - btnGap - actionW, bottomY, actionW, BtnH);
                return;
            }
            pickerVisible = false;
            coverDragging = false;

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
            SetEditCoverPath(path, path);
        }

        // ===== 绘制 =====

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Background);
            if (name.Length == 0 && !editing) return;

            EnsureLayout();
            if (editing) PaintEditChrome(g);

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
                if (shown != null)
                {
                    // 编辑态用整图按取景焦点绘制（可拖动调整），展示态用预裁剪封面
                    if (editing && (editPasted != null || editPreview != null))
                        DrawCoverFocus(g, shown, coverRect);
                    else DrawCoverFilled(g, shown, coverRect);
                }
                else PaintCoverPlaceholder(g, coverRect);
                g.Restore(st);
            }

            if (editing)
            {
                if (pickerVisible) PaintMemberPicker(g);
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
                Draw.DrawStringCenter(g, name, Theme.PageTitleFont, nb, nameDisplayRect);

            string desc = description.Length == 0 ? "暂无简介" : description;
            TextRenderer.DrawText(g, desc, Theme.BodyFont, descDisplayRect, Theme.TextSecondary,
                TextFormatFlags.WordBreak | TextFormatFlags.HorizontalCenter | TextFormatFlags.Top
                | TextFormatFlags.EndEllipsis);

            string meta = (parent.Length == 0 ? string.Empty : parent + " · ") + count + " 项资源";
            using (SolidBrush sb = new SolidBrush(Theme.TextSecondary))
                Draw.DrawStringCenter(g, meta, Theme.CaptionFont, sb, metaDisplayRect);

            DrawButton(g, btnEnterRect, "进入", true, 1, true);
            DrawButton(g, btnEditRect, "编辑", false, 2, true);
        }

        /// <summary>编辑态卡片骨架：左「编辑信息」、右「封面」两张白色圆角卡片，统一 iOS 卡片化风格。</summary>
        private void PaintEditChrome(Graphics g)
        {
            PaintCaption(g, "编辑信息", new Rectangle(editCardRect.X, 16, editCardRect.Width, 18));
            PaintCaption(g, "封面", new Rectangle(pickerCardRect.X, 16, pickerCardRect.Width, 18));

            Draw.DrawShadow(g, editCardRect, 14, 0.5f);
            using (SolidBrush b = new SolidBrush(Theme.Card))
                Draw.FillRoundedRect(g, b, editCardRect, 14);

            Draw.DrawShadow(g, pickerCardRect, 14, 0.5f);
            using (SolidBrush b = new SolidBrush(Theme.Card))
                Draw.FillRoundedRect(g, b, pickerCardRect, 14);
        }

        /// <summary>集合图片分页选择：标题 + 翻页 + 多行缩略图；无图片时给出粘贴/文件提示。</summary>
        private void PaintMemberPicker(Graphics g)
        {
            int perPage = thumbPerPage;
            int pages = ThumbPageCount();
            bool pageLoading = thumbLoading && CurrentPageMissing();
            PaintCaption(g, pageLoading ? "从集合选择封面 · 加载中…" : "从集合选择封面",
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
                Draw.DrawStringCenter(g, pageText, Theme.CaptionFont, pb,
                    new Rectangle(btnPrevPageRect.Right, pickerCaptionY,
                        btnNextPageRect.Left - btnPrevPageRect.Right, 20));

            int start = thumbPage * perPage;
            for (int i = 0; i < perPage; i++)
            {
                int idx = start + i;
                if (idx >= memberPaths.Count) break;
                Rectangle r = ThumbRectFor(idx);
                if (r.Width <= 0 || r.Height <= 0) continue;
                using (GraphicsPath cp = Draw.RoundedPath(r, 10))
                {
                    using (SolidBrush bg = new SolidBrush(Theme.SearchBg))
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
                Draw.DrawStringLeft(g, text, Theme.CaptionFont, b, r);
        }

        /// <summary>统一按钮绘制：主按钮强调色实底，次按钮白底细描边 + 正文文字。</summary>
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
                Draw.FillRoundedRect(g, b, r, 6);
            if (!primary)
            {
                using (Pen p = new Pen(Theme.ControlBorder, 1F))
                    Draw.DrawRoundedRect(g, p, r, 6);
            }
            Color tc = primary ? Color.White : (enabled ? Theme.TextPrimary : Theme.TextTertiary);
            using (SolidBrush tb = new SolidBrush(tc))
                Draw.DrawStringCenter(g, text, Theme.ButtonFont, tb, r);
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

        /// <summary>按取景焦点绘制整图（拖拽平移取景）：等比放大到填满目标区域，再按焦点偏移裁剪。</summary>
        private void DrawCoverFocus(Graphics g, Bitmap bmp, Rectangle dest)
        {
            if (bmp == null || bmp.Width <= 0 || bmp.Height <= 0) return;
            double scale = Math.Max((double)dest.Width / bmp.Width, (double)dest.Height / bmp.Height);
            int dw = Math.Max(dest.Width, (int)Math.Round(bmp.Width * scale));
            int dh = Math.Max(dest.Height, (int)Math.Round(bmp.Height * scale));
            int ox = dw - dest.Width;
            int oy = dh - dest.Height;
            int x = dest.X - (int)Math.Round(ox * ClampFocus(editFocusX));
            int y = dest.Y - (int)Math.Round(oy * ClampFocus(editFocusY));
            g.DrawImage(bmp, new Rectangle(x, y, dw, dh));
        }

        /// <summary>取景焦点夹紧到 0~1（NaN 回退居中）。</summary>
        private static float ClampFocus(float v)
        {
            if (float.IsNaN(v)) return 0.5F;
            if (v < 0F) return 0F;
            if (v > 1F) return 1F;
            return v;
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
                if (pickerVisible && memberPaths.Count > 0 && pages > 1)
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

        /// <summary>当前页是否还有未就绪的缩略图（用于「加载中」提示，避免已可用的页面一直提示加载）。</summary>
        private bool CurrentPageMissing()
        {
            if (memberPaths.Count == 0 || thumbPerPage <= 0) return false;
            int start2 = thumbPage * thumbPerPage;
            int end = Math.Min(start2 + thumbPerPage, memberPaths.Count);
            for (int i = start2; i < end; i++)
                if (!memberThumbs.ContainsKey(i)) return true;
            return false;
        }

        /// <summary>当前页内缩略图下标对应的绘制矩形；不在当前页返回 Empty。</summary>
        private Rectangle ThumbRectFor(int index)
        {
            if (!pickerVisible || thumbPerPage <= 0 || thumbCols <= 0) return Rectangle.Empty;
            int i = index - thumbPage * thumbPerPage;
            if (i < 0 || i >= thumbPerPage) return Rectangle.Empty;
            return new Rectangle(thumbStartX + (i % thumbCols) * (ThumbSize + ThumbGap),
                thumbsY + (i / thumbCols) * (ThumbSize + ThumbGap), ThumbSize, ThumbSize);
        }

        /// <summary>按坐标命中当前页的缩略图下标（多行多列）；未命中返回 -1。</summary>
        private int ThumbIndexAt(Point p)
        {
            if (!editing || !pickerVisible || memberPaths.Count == 0 || thumbCols <= 0) return -1;
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

        /// <summary>编辑态可拖动取景的整图（粘贴图优先，其次所选/自动封面原图）。</summary>
        private Bitmap CoverPreviewSource
        {
            get { return editPasted != null ? editPasted : editPreview; }
        }

        /// <summary>当前预览图相对封面区的可移动余量（0 表示该方向无可拖空间）。</summary>
        private Point CoverOverflow()
        {
            Bitmap bmp = CoverPreviewSource;
            if (bmp == null || bmp.Width <= 0 || bmp.Height <= 0
                || coverRect.Width <= 0 || coverRect.Height <= 0) return Point.Empty;
            double scale = Math.Max((double)coverRect.Width / bmp.Width, (double)coverRect.Height / bmp.Height);
            return new Point(
                Math.Max(0, (int)Math.Round(bmp.Width * scale) - coverRect.Width),
                Math.Max(0, (int)Math.Round(bmp.Height * scale) - coverRect.Height));
        }

        /// <summary>是否可拖动封面取景：编辑态有整图且至少一个方向存在可移动余量。</summary>
        private bool CanPanCover
        {
            get
            {
                if (!editing) return false;
                Point ov = CoverOverflow();
                return ov.X > 0 || ov.Y > 0;
            }
        }

        /// <summary>拖动封面调整取景：按像素位移换算焦点（向右拖显示左侧内容）。</summary>
        private void PanCover(Point p)
        {
            if (CoverPreviewSource == null) { coverDragging = false; return; }
            Point ov = CoverOverflow();
            float fx = coverDragFocusX;
            float fy = coverDragFocusY;
            if (ov.X > 0) fx = coverDragFocusX - (float)(p.X - coverDragStart.X) / ov.X;
            if (ov.Y > 0) fy = coverDragFocusY - (float)(p.Y - coverDragStart.Y) / ov.Y;
            editFocusX = ClampFocus(fx);
            editFocusY = ClampFocus(fy);
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            EnsureLayout();
            if (coverDragging)
            {
                PanCover(e.Location);
                return;
            }
            int id = HitButton(e.Location);
            int thumb = id < 0 ? ThumbIndexAt(e.Location) : -1;
            if (id != hoverBtn || thumb != thumbHover)
            {
                hoverBtn = id;
                thumbHover = thumb;
                Invalidate();
            }
            if (CanPanCover && coverRect.Contains(e.Location)) Cursor = Cursors.SizeAll;
            else Cursor = (id >= 0 || thumb >= 0) ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (coverDragging) return;
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
            EnsureLayout();
            if (e.Button != MouseButtons.Left) return;
            if (CanPanCover && coverRect.Contains(e.Location))
            {
                coverDragging = true;
                coverDragStart = e.Location;
                coverDragFocusX = editFocusX;
                coverDragFocusY = editFocusY;
                Capture = true;
                Cursor = Cursors.SizeAll;
                return;
            }
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
            EnsureLayout();
            if (coverDragging)
            {
                coverDragging = false;
                Capture = false;
                Cursor = Cursors.Default;
                Invalidate();
                return;
            }
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

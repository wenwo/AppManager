using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using AppManager;

namespace AppManager.Controls
{
    /// <summary>集合编辑页一次提交的内容（新建和编辑共用）；校验与落盘由主窗体处理。</summary>
    public sealed class CollectionEditDraft
    {
        /// <summary>true = 新建；false = 编辑当前集合。</summary>
        public bool Creating;
        /// <summary>新建成功后留在编辑页接着建下一个。</summary>
        public bool KeepCreating;
        public string Name = string.Empty;
        public string Description = string.Empty;
        /// <summary>所在位置：父集合键；空 = 资源分类的最外层。</summary>
        public string ParentCollection = string.Empty;
        /// <summary>封面图片路径；空 = 自动（或使用 PastedCover）。</summary>
        public string CoverPath = string.Empty;
        /// <summary>粘贴进来的封面；位图仍归信息页所有，主窗体只读取并另存。</summary>
        public Bitmap PastedCover;
        public float FocusX = 0.5F;
        public float FocusY = 0.5F;
    }

    /// <summary>
    /// 集合编辑页（新建和编辑共用）：顶部返回与标题，白色卡片里左列封面（预览、来源按钮、集合内图片），
    /// 右列位置 / 名称 / 简介，底部固定操作按钮。名称边输入边校验，问题直接显示在名称下方。
    /// </summary>
    public partial class CollectionInfoPanel
    {
        private bool creating;                       // 编辑页用于新建（还没有集合）
        private string editCategory = string.Empty;  // 所在资源分类（位置路径的第一段）
        private string editParentKey = string.Empty; // 当前选的位置：父集合键；空 = 分类最外层
        private string editOriginalParentKey = string.Empty;
        private int joinCount;                       // 新建后要一起加入的资源数（从「加入集合 → 新建」进入时）
        private string nameIssue;                    // 当前内容的问题说明；null = 可以保存
        private bool submitTried;                    // 点过保存：名称为空也按问题提示
        private string notice;                       // 底部左侧的提示（刚创建了什么 / 没保存成功）
        private bool noticeIsError;

        private Rectangle backRect;
        private Rectangle titleRect;
        private Rectangle hintRect;
        private Rectangle cardRect;
        private Rectangle coverCaptionRect;
        private Rectangle coverHintRect;
        private Rectangle btnFileRect;
        private Rectangle btnPasteRect;
        private Rectangle btnAutoRect;
        private Rectangle locationCaptionRect;
        private Rectangle locationRect;
        private Rectangle nameCaptionRect;
        private Rectangle nameStatusRect;
        private Rectangle descCaptionRect;
        private Rectangle noticeRect;
        private Rectangle btnSaveRect;
        private Rectangle btnContinueRect;
        private Rectangle btnCancelRect;
        private int thumbGapX;                       // 缩略图横向间距（把一行撑满列宽）

        /// <summary>
        /// 边输入边校验用：传入（名称, 所选父集合键），返回给用户看的问题说明；null 表示可以保存。
        /// 未设置时只校验名称本身。
        /// </summary>
        public Func<string, string, string> DraftValidator;
        /// <summary>
        /// 点了「取消」「返回」或按了 Esc：编辑页已关闭。参数为 true 表示关掉的是新建页，
        /// 需要主窗体恢复打开前的内容；编辑已有集合时信息页的数据没动过，不用恢复。
        /// </summary>
        public event Action<bool> EditCancelled;
        /// <summary>点了位置栏：参数是位置栏的屏幕矩形，由主窗体弹出位置菜单并回传 SetEditLocation。</summary>
        public event Action<Rectangle> LocationPickRequested;

        /// <summary>编辑页是否正用于新建集合。</summary>
        public bool IsCreating { get { return editing && creating; } }

        /// <summary>编辑页里当前选的位置（父集合键；空 = 分类最外层）。</summary>
        public string EditParentKey { get { return editParentKey; } }

        /// <summary>正在编辑已有集合，且名称、简介、位置、封面或取景有没保存的改动。</summary>
        public bool HasUnsavedEdit
        {
            get
            {
                if (!editing || creating) return false;
                return !string.Equals(CurrentName, name.Trim(), StringComparison.Ordinal)
                    || !string.Equals((descBox.Text ?? string.Empty).Trim(), description.Trim(), StringComparison.Ordinal)
                    || !string.Equals(editParentKey, editOriginalParentKey, StringComparison.Ordinal)
                    || editPasted != null
                    || !string.Equals(editCoverPath, coverPath, StringComparison.OrdinalIgnoreCase)
                    || editFocusX != coverFocusX || editFocusY != coverFocusY;
            }
        }

        private string CurrentName
        {
            get { return nameBox == null ? string.Empty : (nameBox.Text ?? string.Empty).Trim(); }
        }

        private bool CanKeepCreating { get { return creating && joinCount == 0; } }

        /// <summary>名称输入框是否描红边：有问题，且已经输入了内容或点过保存。</summary>
        private bool NameFieldInvalid
        {
            get { return editing && nameIssue != null && (CurrentName.Length > 0 || submitTried); }
        }

        // ===== 进入 / 离开 =====

        /// <summary>
        /// 打开编辑页编辑当前集合：填好名称/简介/位置，封面从当前自定义封面开始（无自定义时取首个图片成员）。
        /// category 是所在资源分类，parentCollectionKey 是它现在的父集合键（一级集合为空）。
        /// </summary>
        public void BeginEdit(string category, string parentCollectionKey)
        {
            if (name.Length == 0) return;
            EnsureEditFields();
            EndEdit();
            editing = true;
            editCategory = category ?? string.Empty;
            editParentKey = parentCollectionKey ?? string.Empty;
            editOriginalParentKey = editParentKey;
            editCoverPath = coverPath;
            editFocusX = coverFocusX;
            editFocusY = coverFocusY;
            // 预览源：自定义封面或集合首个图片成员；先展示已有封面，整图异步载入后再替换（可拖动取景）
            string src = !string.IsNullOrEmpty(coverPath)
                ? coverPath
                : (memberPaths.Count > 0 ? memberPaths[0] : null);
            if (!string.IsNullOrEmpty(src)) StartPreviewLoad(src);
            nameBox.Text = name;
            descBox.Text = description;
            ShowEditor();
            nameBox.SelectAll();
        }

        /// <summary>
        /// 打开编辑页新建集合：各项为空，位置预先选好。itemsToJoin 是创建后要一起加入的资源数，
        /// candidateImages 是这些资源里的图片（可直接选作封面）。
        /// </summary>
        public void BeginCreate(string category, string parentCollectionKey, int itemsToJoin,
            List<string> candidateImages)
        {
            EnsureEditFields();
            EndEdit();
            // 可选图片换成本次要加入的图片；展示态的数据由主窗体在离开编辑页后重新注入
            thumbGen++;
            thumbLoading = false;
            DisposeMemberThumbs();
            memberPaths.Clear();
            if (candidateImages != null)
            {
                for (int i = 0; i < candidateImages.Count && memberPaths.Count < MaxMemberThumbs; i++)
                    if (!string.IsNullOrEmpty(candidateImages[i])) memberPaths.Add(candidateImages[i]);
            }
            editing = true;
            creating = true;
            editCategory = category ?? string.Empty;
            editParentKey = parentCollectionKey ?? string.Empty;
            editOriginalParentKey = editParentKey;
            joinCount = Math.Max(0, itemsToJoin);
            editPreviewCleared = true;   // 还没有封面：画占位
            nameBox.Text = string.Empty;
            descBox.Text = string.Empty;
            ShowEditor();
        }

        private void ShowEditor()
        {
            namePanel.Visible = true;
            descPanel.Visible = true;
            thumbPage = 0;
            thumbHover = -1;
            StartThumbLoad();
            layoutDirty = true;
            EnsureLayout();
            Revalidate();
            nameBox.Focus();
        }

        private void ResetEditorState()
        {
            creating = false;
            editCategory = string.Empty;
            editParentKey = string.Empty;
            editOriginalParentKey = string.Empty;
            joinCount = 0;
            nameIssue = null;
            submitTried = false;
            notice = null;
            noticeIsError = false;
        }

        /// <summary>关闭编辑页并通知主窗体（取消 / 返回 / Esc）；未保存的内容丢弃。</summary>
        public void CancelEdit()
        {
            if (!editing) return;
            bool wasCreating = creating;
            EndEdit();
            Action<bool> h = EditCancelled;
            if (h != null) h(wasCreating);
        }

        private void RequestEdit()
        {
            Action h = EditRequested;
            if (h != null) h();
        }

        // ===== 位置 / 校验 / 提交 =====

        /// <summary>换一个位置（父集合键；空 = 分类最外层）；已填的内容保留。</summary>
        public void SetEditLocation(string parentCollectionKey)
        {
            if (!editing) return;
            editParentKey = parentCollectionKey ?? string.Empty;
            notice = null;
            Revalidate();
            nameBox.Focus();
        }

        /// <summary>集合树在别处变了（后台刷新等）：按最新数据重新校验当前内容。</summary>
        public void RefreshValidation()
        {
            if (editing) Revalidate();
        }

        /// <summary>在底部显示一条提示（如没保存成功）；编辑页保持打开，内容不丢。</summary>
        public void ShowEditNotice(string text, bool isError)
        {
            if (!editing) return;
            notice = text;
            noticeIsError = isError;
            Invalidate();
        }

        /// <summary>连续新建：刚建好一个，位置保留，其余清空，接着填下一个。</summary>
        public void ContinueCreating(string createdName)
        {
            if (!IsCreating) return;
            ReplaceCoverPreview(null, string.Empty);
            submitTried = false;
            nameBox.Text = string.Empty;
            descBox.Text = string.Empty;
            notice = "已创建「" + (createdName ?? string.Empty) + "」，可以接着建下一个。";
            noticeIsError = false;
            Revalidate();
            nameBox.Focus();
        }

        private void PickLocation()
        {
            Action<Rectangle> h = LocationPickRequested;
            if (h != null) h(RectangleToScreen(locationRect));
        }

        private void OnDraftChanged()
        {
            notice = null;
            Revalidate();
        }

        private void Revalidate()
        {
            Func<string, string, string> validator = DraftValidator;
            string n = CurrentName;
            nameIssue = validator != null ? validator(n, editParentKey) : CategoryRules.ValidateCollectionName(n);
            if (namePanel != null) namePanel.Invalidate();
            Invalidate();
        }

        /// <summary>
        /// 提交：先按最新数据校验一遍，有问题就停在名称框并把问题显示出来；
        /// 没问题则把整页内容交给主窗体保存。keepCreating 只在普通新建时生效。
        /// </summary>
        private void Submit(bool keepCreating)
        {
            if (!editing) return;
            submitTried = true;
            notice = null;
            Revalidate();
            if (nameIssue != null)
            {
                nameBox.Focus();
                return;
            }
            CollectionEditDraft draft = new CollectionEditDraft();
            draft.Creating = creating;
            draft.KeepCreating = keepCreating && CanKeepCreating;
            draft.Name = CurrentName;
            draft.Description = (descBox.Text ?? string.Empty).Trim();
            draft.ParentCollection = editParentKey;
            draft.CoverPath = editCoverPath;
            draft.PastedCover = editPasted;
            draft.FocusX = editFocusX;
            draft.FocusY = editFocusY;
            Action<CollectionEditDraft> h = EditSaveRequested;
            if (h != null) h(draft);
        }

        // ===== 布局 =====

        private static void SetBoundsIfChanged(Control control, Rectangle bounds)
        {
            if (control.Bounds != bounds) control.Bounds = bounds;
        }

        /// <summary>
        /// 编辑页几何：整页限宽居中；卡片内左列封面（预览 8:5、来源按钮、集合内图片或说明），
        /// 右列位置 / 名称 / 简介（简介吃掉剩余高度）；按钮行在卡片下方靠右。高度紧张时省掉标题下的说明行。
        /// </summary>
        private void LayoutEditor()
        {
            int pad = Dpi(20);
            int pageW = Math.Min(Math.Max(Dpi(320), Width - pad * 2), Dpi(980));
            int px = Math.Max(pad, (Width - pageW) / 2);
            bool compact = Height < Dpi(480);

            backRect = new Rectangle(px, Dpi(16), Dpi(74), Dpi(30));
            titleRect = new Rectangle(backRect.Right + Dpi(14), Dpi(14),
                Math.Max(0, px + pageW - backRect.Right - Dpi(14)), Dpi(34));
            hintRect = compact ? Rectangle.Empty : new Rectangle(px, Dpi(54), pageW, Dpi(22));

            int ip = Dpi(20);
            int colGap = Dpi(28);
            int capH = Dpi(20);
            int capGap = Dpi(6);
            int srcH = Dpi(30);
            int fieldH = Dpi(36);
            int innerW = pageW - ip * 2;
            int leftW = Math.Max(Dpi(200), Math.Min(Dpi(400), (innerW - colGap) * 44 / 100));
            int cols = Math.Max(1, Math.Min(8, (leftW + ThumbGap) / (ThumbSize + ThumbGap)));

            // 卡片高度跟着内容走（左列封面 + 来源按钮 + 最多三行图片 / 一段说明），窗口不够高时再压缩；
            // 按钮行紧跟在卡片下面，不贴窗口底边，窗口很大时也不会离表单很远。
            int wantRows = memberPaths.Count == 0 ? 0 : Math.Min(3, (memberPaths.Count + cols - 1) / cols);
            int belowNatural = wantRows > 0 ? Dpi(26) + wantRows * (ThumbSize + ThumbGap) - ThumbGap : Dpi(52);
            int leftNatural = capH + capGap + (int)Math.Round(leftW * 5.0 / 8.0) + Dpi(10) + srcH + Dpi(14) + belowNatural;
            int rightFixed = capH + capGap + fieldH + Dpi(14)
                + capH + capGap + fieldH + Dpi(4) + Dpi(20) + Dpi(8)
                + capH + capGap;
            int naturalH = Math.Max(leftNatural, rightFixed + Dpi(96)) + ip * 2;
            int lowestBarY = Height - Dpi(18) - BtnH;
            int cardTop = compact ? Dpi(56) : Dpi(84);
            int cardH = Math.Max(Dpi(200), Math.Min(naturalH, lowestBarY - Dpi(14) - cardTop));
            cardRect = new Rectangle(px, cardTop, pageW, cardH);
            int barY = Math.Min(lowestBarY, cardRect.Bottom + Dpi(14));

            int innerX = cardRect.X + ip;
            int innerY = cardRect.Y + ip;
            int innerBottom = cardRect.Bottom - ip;

            // 左列：封面预览 + 来源按钮
            coverCaptionRect = new Rectangle(innerX, innerY, leftW, capH);
            int coverTop = innerY + capH + capGap;
            int coverW = leftW;
            int coverH = (int)Math.Round(coverW * 5.0 / 8.0);
            // 高度不够时封面先让出来源按钮的位置；有可选图片时再让出一行缩略图（封面最矮缩到 110）
            int maxCoverH = innerBottom - coverTop - Dpi(10) - srcH;
            if (memberPaths.Count > 0)
                maxCoverH = Math.Max(Math.Min(maxCoverH, Dpi(110)), maxCoverH - Dpi(14) - Dpi(26) - ThumbSize);
            if (coverH > maxCoverH)
            {
                coverH = Math.Max(Dpi(80), maxCoverH);
                coverW = Math.Min(leftW, (int)Math.Round(coverH * 8.0 / 5.0));
            }
            coverRect = new Rectangle(innerX + (leftW - coverW) / 2, coverTop, coverW, coverH);
            int srcY = coverRect.Bottom + Dpi(10);
            int srcGap = Dpi(8);
            int srcW = (leftW - srcGap * 2) / 3;
            btnFileRect = new Rectangle(innerX, srcY, srcW, srcH);
            btnPasteRect = new Rectangle(btnFileRect.Right + srcGap, srcY, srcW, srcH);
            btnAutoRect = new Rectangle(innerX + leftW - srcW, srcY, srcW, srcH);

            // 左列下半：有可选图片时放缩略图（行数随高度），否则放一段说明
            int belowY = srcY + srcH + Dpi(14);
            int belowH = innerBottom - belowY;
            pickerVisible = memberPaths.Count > 0 && belowH >= Dpi(26) + ThumbSize;
            if (pickerVisible)
            {
                coverHintRect = Rectangle.Empty;
                pickerCaptionY = belowY;
                thumbStartX = innerX;
                thumbAreaWidth = leftW;
                thumbsY = belowY + Dpi(26);
                thumbCols = cols;
                // 横向间距把整行撑满列宽，右边不留一条空
                thumbGapX = cols > 1 ? Math.Min(Dpi(28), (leftW - cols * ThumbSize) / (cols - 1)) : ThumbGap;
                thumbRows = Math.Max(1, Math.Min(3, (innerBottom - thumbsY + ThumbGap) / (ThumbSize + ThumbGap)));
                thumbPerPage = thumbCols * thumbRows;
                int pages = ThumbPageCount();
                if (thumbPage > pages - 1) thumbPage = pages - 1;
                if (thumbPage < 0) thumbPage = 0;
                btnNextPageRect = new Rectangle(innerX + leftW - Dpi(22), pickerCaptionY, Dpi(22), Dpi(20));
                btnPrevPageRect = new Rectangle(btnNextPageRect.Left - Dpi(76), pickerCaptionY, Dpi(22), Dpi(20));
            }
            else
            {
                coverHintRect = belowH >= Dpi(34)
                    ? new Rectangle(innerX, belowY, leftW, Math.Min(belowH, Dpi(64))) : Rectangle.Empty;
            }

            // 右列：位置 / 名称（下方一行提示）/ 简介
            int rx = innerX + leftW + colGap;
            int rw = Math.Max(Dpi(120), cardRect.Right - ip - rx);
            int y = innerY;
            locationCaptionRect = new Rectangle(rx, y, rw, capH);
            y += capH + capGap;
            locationRect = new Rectangle(rx, y, rw, fieldH);
            y += fieldH + Dpi(14);
            nameCaptionRect = new Rectangle(rx, y, rw, capH);
            y += capH + capGap;
            int boxH = nameBox.PreferredHeight;
            SetBoundsIfChanged(namePanel, new Rectangle(rx, y, rw, fieldH));
            SetBoundsIfChanged(nameBox, new Rectangle(Dpi(12), (fieldH - boxH) / 2, rw - Dpi(24), boxH));
            y += fieldH + Dpi(4);
            nameStatusRect = new Rectangle(rx, y, rw, Dpi(20));
            y += Dpi(20) + Dpi(8);
            descCaptionRect = new Rectangle(rx, y, rw, capH);
            y += capH + capGap;
            int descH = Math.Max(Dpi(44), Math.Min(Dpi(240), innerBottom - y));
            SetBoundsIfChanged(descPanel, new Rectangle(rx, y, rw, descH));
            SetBoundsIfChanged(descBox, new Rectangle(Dpi(12), Dpi(8), rw - Dpi(24), descH - Dpi(16)));

            // 底部：主按钮在左、取消在最右（与添加页一致）；连续新建只在普通新建时出现
            int btnW = Dpi(92);
            int gap = Dpi(10);
            int right = px + pageW;
            btnCancelRect = new Rectangle(right - btnW, barY, btnW, BtnH);
            btnSaveRect = new Rectangle(btnCancelRect.Left - gap - btnW, barY, btnW, BtnH);
            btnContinueRect = CanKeepCreating
                ? new Rectangle(btnSaveRect.Left - gap - Dpi(124), barY, Dpi(124), BtnH) : Rectangle.Empty;
            int buttonsLeft = CanKeepCreating ? btnContinueRect.Left : btnSaveRect.Left;
            noticeRect = new Rectangle(px, barY, Math.Max(0, buttonsLeft - Dpi(16) - px), BtnH);
        }

        // ===== 绘制 =====

        private void PaintEditor(Graphics g)
        {
            PaintBackButton(g);
            string title = creating ? (editParentKey.Length == 0 ? "新建集合" : "新建子集合") : "编辑集合";
            TextRenderer.DrawText(g, title, Theme.PageTitleFont, titleRect, Theme.TextPrimary,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis
                | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            if (hintRect.Width > 0)
                TextRenderer.DrawText(g, EditorHint(), Theme.CaptionFont, hintRect, Theme.TextSecondary,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis
                    | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);

            using (SolidBrush fill = new SolidBrush(Theme.Card)) Draw.FillRoundedRect(g, fill, cardRect, Dpi(8));
            using (Pen border = new Pen(Theme.ControlBorder, 1)) Draw.DrawRoundedRect(g, border, cardRect, Dpi(8));

            PaintEditCover(g);
            bool autoActive = editPasted == null && string.IsNullOrEmpty(editCoverPath);
            DrawButton(g, btnFileRect, "从文件选", false, 3, true);
            DrawButton(g, btnPasteRect, "粘贴图片", false, 8, true);
            DrawButton(g, btnAutoRect, "恢复自动", false, 4, !autoActive);
            if (pickerVisible) PaintMemberPicker(g);
            else if (coverHintRect.Width > 0)
                TextRenderer.DrawText(g, CoverHint(autoActive), Theme.CaptionFont, coverHintRect, Theme.TextTertiary,
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak
                    | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            PaintCaption(g, "位置", locationCaptionRect);
            PaintLocationField(g);
            PaintCaption(g, "名称", nameCaptionRect);
            int length = CurrentName.Length;
            using (SolidBrush cb = new SolidBrush(length > CategoryRules.MaxNameLength ? Theme.DangerText : Theme.TextTertiary))
                Draw.DrawStringRight(g, length + " / " + CategoryRules.MaxNameLength, Theme.CaptionFont, cb, nameCaptionRect);
            PaintNameStatus(g);
            PaintCaption(g, "简介（可不填）", descCaptionRect);

            if (!string.IsNullOrEmpty(notice) && noticeRect.Width > 0)
            {
                using (SolidBrush nb = new SolidBrush(noticeIsError ? Theme.DangerText : Theme.SuccessText))
                    Draw.DrawStringLeft(g, notice, Theme.CaptionFont, nb, noticeRect);
            }
            if (CanKeepCreating) DrawButton(g, btnContinueRect, "创建并继续", false, 7, true);
            DrawButton(g, btnSaveRect, creating ? "创建" : "保存", true, 5, nameIssue == null);
            DrawButton(g, btnCancelRect, "取消", false, 6, true);
        }

        private string EditorHint()
        {
            if (!creating) return "改了名称或位置，里面的子集合和资源会跟着一起走。Enter 保存，Esc 取消。";
            if (joinCount > 0) return "创建后会把选中的 " + joinCount + " 项资源放进这个集合。Enter 创建，Esc 取消。";
            return "起个名字就能创建，封面和简介以后也能再改。Enter 创建，Ctrl+Enter 创建后接着建下一个。";
        }

        private string CoverHint(bool autoActive)
        {
            string first = creating && autoActive ? "不选也可以：放进图片后，会自动用第一张当封面。"
                : autoActive ? "现在是自动封面：用集合里的第一张图片。"
                : "按住封面拖动，可以调整显示哪一部分。";
            return first + "复制一张图片后按 Ctrl+V，也能直接贴成封面。";
        }

        private void PaintBackButton(Graphics g)
        {
            Draw.DrawBackButton(g, backRect, hoverBtn == 12, pressBtn == 12, DpiHelper.ForControl(this));
        }

        /// <summary>封面预览：粘贴图 / 已选图按取景焦点画整图（可拖动），整图未到时先画已有封面，都没有画占位。</summary>
        private void PaintEditCover(Graphics g)
        {
            bool autoActive = editPasted == null && string.IsNullOrEmpty(editCoverPath);
            PaintCaption(g, "封面", coverCaptionRect);
            using (SolidBrush sb = new SolidBrush(Theme.TextTertiary))
                Draw.DrawStringRight(g, editPasted != null ? "粘贴的图片" : (autoActive ? "自动" : "自选图片"),
                    Theme.CaptionFont, sb, coverCaptionRect);

            Bitmap whole = editPasted != null ? editPasted : editPreview;
            Bitmap ready = whole == null && !editPreviewCleared && !creating ? cover : null;
            using (GraphicsPath cp = Draw.RoundedPath(coverRect, Dpi(10)))
            {
                using (SolidBrush bg = new SolidBrush(Theme.ChipBg)) g.FillPath(bg, cp);
                GraphicsState st = g.Save();
                g.SetClip(cp);
                if (whole != null) DrawCoverFocus(g, whole, coverRect);
                else if (ready != null) DrawCoverFilled(g, ready, coverRect);
                else PaintCoverPlaceholder(g, coverRect);
                if (CanPanCover && !coverDragging) PaintPanTip(g);
                g.Restore(st);
                using (Pen border = new Pen(Theme.ControlBorder, 1F)) g.DrawPath(border, cp);
            }
        }

        /// <summary>封面可拖动时在底部提示一下，免得没人知道能拖。</summary>
        private void PaintPanTip(Graphics g)
        {
            const string tip = "按住拖动调整位置";
            Size size = TextRenderer.MeasureText(tip, Theme.CaptionFont);
            int w = size.Width + Dpi(16);
            int h = Dpi(24);
            if (w > coverRect.Width - Dpi(16) || h > coverRect.Height - Dpi(16)) return;
            Rectangle pill = new Rectangle(coverRect.X + (coverRect.Width - w) / 2, coverRect.Bottom - Dpi(8) - h, w, h);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(150, 0, 0, 0))) Draw.FillRoundedRect(g, b, pill, h / 2);
            using (SolidBrush tb = new SolidBrush(Color.White)) Draw.DrawStringCenter(g, tip, Theme.CaptionFont, tb, pill);
        }

        /// <summary>位置栏：显示新集合会放在哪（分类 › 父集合 › …），点一下换位置。</summary>
        private void PaintLocationField(Graphics g)
        {
            Color fill = pressBtn == 9 ? Theme.SegmentBg : (hoverBtn == 9 ? Theme.RowHover : Theme.SearchBg);
            using (SolidBrush b = new SolidBrush(fill)) Draw.FillRoundedRect(g, b, locationRect, Dpi(Theme.RadiusControl));
            int arrowW = Dpi(28);
            Rectangle text = new Rectangle(locationRect.X + Dpi(12), locationRect.Y,
                Math.Max(0, locationRect.Width - Dpi(12) - arrowW), locationRect.Height);
            using (SolidBrush tb = new SolidBrush(Theme.TextPrimary))
                Draw.DrawStringLeft(g, FitLocationText(g, text.Width), Theme.BodyFont, tb, text);
            float cx = locationRect.Right - arrowW / 2F - Dpi(2);
            float cy = locationRect.Y + locationRect.Height / 2F;
            using (Pen p = new Pen(Theme.TextSecondary, 1.6F))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                g.DrawLine(p, cx - 4.5F, cy - 2F, cx, cy + 2.5F);
                g.DrawLine(p, cx, cy + 2.5F, cx + 4.5F, cy - 2F);
            }
        }

        /// <summary>位置路径的各段：资源分类 + 各级父集合名。</summary>
        private List<string> LocationSegments()
        {
            List<string> segments = new List<string>();
            if (CollectionKeys.IsCollectionKey(editParentKey))
                segments.AddRange(editParentKey.Substring(CollectionKeys.Prefix.Length).Split('/'));
            else segments.Add(editCategory);
            return segments;
        }

        /// <summary>位置文字放不下时从前面省略，保证最后一级（真正的父集合）看得见。</summary>
        private string FitLocationText(Graphics g, int width)
        {
            List<string> segments = LocationSegments();
            const string sep = " › ";
            string full = string.Join(sep, segments.ToArray());
            if (segments.Count <= 1 || g.MeasureString(full, Theme.BodyFont).Width <= width) return full;
            for (int skip = 1; skip < segments.Count; skip++)
            {
                string shortened = "…" + sep + string.Join(sep, segments.GetRange(skip, segments.Count - skip).ToArray());
                if (skip == segments.Count - 1 || g.MeasureString(shortened, Theme.BodyFont).Width <= width)
                    return shortened;
            }
            return full;
        }

        /// <summary>名称下方的一行提示：没填时说明要填，有问题标红，可用时标绿；编辑时没改动则不显示。</summary>
        private void PaintNameStatus(Graphics g)
        {
            string text;
            Color color;
            int mark;   // 0 无 1 对勾 2 叹号
            if (CurrentName.Length == 0 && !submitTried)
            {
                text = creating ? "先给它起个名字。" : "名称不能留空。";
                color = Theme.TextTertiary;
                mark = 0;
            }
            else if (nameIssue != null)
            {
                text = nameIssue;
                color = Theme.DangerText;
                mark = 2;
            }
            else if (creating || !string.Equals(CurrentName, name, StringComparison.Ordinal)
                || !string.Equals(editParentKey, editOriginalParentKey, StringComparison.Ordinal))
            {
                text = creating ? "名称可用。" : "可以保存。";
                color = Theme.SuccessText;
                mark = 1;
            }
            else return;

            Rectangle textRect = nameStatusRect;
            if (mark != 0)
            {
                int s = Dpi(14);
                Rectangle icon = new Rectangle(nameStatusRect.X, nameStatusRect.Y + (nameStatusRect.Height - s) / 2, s, s);
                using (SolidBrush b = new SolidBrush(color)) g.FillEllipse(b, icon);
                float cx = icon.X + icon.Width / 2F, cy = icon.Y + icon.Height / 2F, u = icon.Width / 14F;
                using (Pen p = new Pen(Color.White, 1.5F * u))
                {
                    p.StartCap = LineCap.Round;
                    p.EndCap = LineCap.Round;
                    if (mark == 1)
                    {
                        g.DrawLine(p, cx - 3F * u, cy + 0.2F * u, cx - 0.8F * u, cy + 2.4F * u);
                        g.DrawLine(p, cx - 0.8F * u, cy + 2.4F * u, cx + 3.2F * u, cy - 2.2F * u);
                    }
                    else
                    {
                        g.DrawLine(p, cx, cy - 3.2F * u, cx, cy + 0.6F * u);
                        g.DrawLine(p, cx, cy + 3F * u, cx, cy + 3.1F * u);
                    }
                }
                textRect = new Rectangle(icon.Right + Dpi(6), nameStatusRect.Y,
                    Math.Max(0, nameStatusRect.Width - s - Dpi(6)), nameStatusRect.Height);
            }
            using (SolidBrush tb = new SolidBrush(color))
                Draw.DrawStringLeft(g, text, Theme.CaptionFont, tb, textRect);
        }

        // ===== 交互 =====

        /// <summary>编辑页命中按钮编号（见 hoverBtn 的编号说明）。主按钮内容有问题时也能点：点了会把问题标出来。</summary>
        private int HitEditButton(Point p)
        {
            if (backRect.Contains(p)) return 12;
            if (locationRect.Contains(p)) return 9;
            int pages = ThumbPageCount();
            if (pickerVisible && memberPaths.Count > 0 && pages > 1)
            {
                if (thumbPage > 0 && btnPrevPageRect.Contains(p)) return 10;
                if (thumbPage < pages - 1 && btnNextPageRect.Contains(p)) return 11;
            }
            bool autoActive = editPasted == null && string.IsNullOrEmpty(editCoverPath);
            if (!autoActive && btnAutoRect.Contains(p)) return 4;
            if (btnPasteRect.Contains(p)) return 8;
            if (btnFileRect.Contains(p)) return 3;
            if (btnSaveRect.Contains(p)) return 5;
            if (CanKeepCreating && btnContinueRect.Contains(p)) return 7;
            if (btnCancelRect.Contains(p)) return 6;
            return -1;
        }
    }
}

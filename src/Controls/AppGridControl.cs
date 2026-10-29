using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using AppManager;

namespace AppManager.Controls
{
    /// <summary>
    /// 主内容区：网格/列表双视图，自绘卡片（图标+名称），支持滚动、悬停、
    /// 右键菜单（屏幕坐标回调）、双击启动。图标由外部注入到 AppInfo.Icon。
    /// </summary>
    public class AppGridControl : ScrollableControl
    {
        private readonly List<AppInfo> apps = new List<AppInfo>();
        private bool grid = true;
        private int hoverIndex = -1;

        // 多选（Ctrl 点选 / Shift 连选）：仅记录当前列表下标，换列表即清空
        private readonly List<int> selected = new List<int>();
        private int selectAnchor = -1;      // 连选锚点
        private bool collapseOnUp;          // 已选多项时普通点击：未拖动则在松手时收敛为单项
        private readonly SolidBrush selectFillBrush = new SolidBrush(Color.FromArgb(24, Theme.Accent));
        private readonly SolidBrush selectRowBrush = new SolidBrush(Color.FromArgb(24, Theme.Accent));
        private readonly Pen selectPen = new Pen(Theme.Accent, 2f);

        // 复用的绘制资源：避免每帧为每个条目新建画笔/字体/路径，降低 GDI 句柄抖动
        private readonly SolidBrush cardBrush = new SolidBrush(Theme.Card);
        private readonly SolidBrush hoverBrush = new SolidBrush(Theme.CardHover);
        private readonly SolidBrush rowHoverBrush = new SolidBrush(Theme.RowHover);
        private readonly SolidBrush textBrush = new SolidBrush(Theme.TextPrimary);
        private readonly SolidBrush subTextBrush = new SolidBrush(Theme.TextSecondary);
        private readonly SolidBrush favoriteBrush = new SolidBrush(Theme.Accent);
        private readonly SolidBrush actionBlueBrush = new SolidBrush(Theme.AccentText);
        private readonly SolidBrush actionRedBrush = new SolidBrush(Theme.DeleteRed);
        private Font subFont;
        private Font actionFont;
        private Font headerFont;   // 列表表头字体（与辅助信息同级，避免过度加粗）
        private GraphicsPath gridItemPath;   // 网格卡片路径（原点，尺寸固定）
        private readonly Pen accentPen = new Pen(Theme.Accent, 2f);
        private readonly SolidBrush chipBrush = new SolidBrush(Theme.ChipBg);   // 分类分段底色
        private readonly SolidBrush headerBgBrush = new SolidBrush(Theme.Background);   // 列表表头底色（吸顶，覆盖滚动到其下方的行）
        private readonly Pen headerLinePen = new Pen(Theme.Separator);   // 列表表头底部分隔线
        private readonly Pen cardBorderPen = new Pen(Theme.Separator);
        private readonly StringFormat pathFormat = CreatePathFormat();   // 路径列：中间省略
        private readonly StringFormat tipFormat = CreateTipFormat();      // 悬浮信息卡：仅按 \n 换行，超宽省略
        private readonly Dictionary<string, ListMeta> metaCache = new Dictionary<string, ListMeta>(StringComparer.OrdinalIgnoreCase);   // 大小/时间列缓存（跨目录复用）
        private readonly object metaSync = new object();
        private volatile MetaLoadBatch metaBatch;
        private static readonly ListMeta EmptyMeta = new ListMeta();   // 非文件条目/尚未加载共用，避免每次重绘分配
        private int locTextMaxW = -1;   // 位置列文本最大宽度（按当前 apps 缓存，重绘不再重复测量）
        private string sortColumn;      // 表头排序列（null=默认顺序）；仅绘制指示箭头，排序由主窗体执行
        private bool sortAsc = true;
        private int catTextMaxW = -1;   // 分类列胶囊组最大宽度（按当前 apps 缓存）
        private int actionReserveMax = -1;   // 整表统一预留的最大操作按钮数（保证列 X 一致）

        // 拖动排序状态：按下→越过系统阈值→拖动→落点缝隙
        private bool allowReorder;
        private bool dragPending;
        private bool dragging;
        private int dragFrom = -1;
        private int dragGap = -1;
        private int dropTarget = -1;   // 拖动悬停命中的目录条目（文件搬迁目标），-1 表示无
        private Point dragStart;
        private bool suppressClick;

        // iOS Grouped List 尺寸（单行表格：名称靠左，分类/大小/位置 靠右且上下对齐）
        private int ListRowH { get { return Dpi(30); } }
        private const int ListTop = 0;            // 表头紧贴内容区顶部
        internal const int ListHeaderH = 36;       // 与左侧分类行同高，保证三栏文字中心对齐
        private int ListBottomPad { get { return Dpi(10); } }     // 列表底部留白（滚动范围）
        private int ListMargin { get { return Dpi(14); } }
        private int ListIcon { get { return Dpi(20); } }
        private int ActionBtnW { get { return Dpi(40); } }
        private int ActionBtnGap { get { return Dpi(4); } }
        // 每条记录为独立卡片：卡片间距 + 圆角
        private int ListGap { get { return Dpi(4); } }
        private int ListCardRadius { get { return Dpi(8); } }
        // 右侧数据列：分类列左对齐（胶囊组左边缘固定）、大小/时间固定宽、位置列按内容宽度并贴右边缘
        private int ColGap { get { return Dpi(18); } }
        private int CatColMinW { get { return Dpi(56); } }
        private int SizeColW { get { return Dpi(76); } }
        private int TimeColW { get { return Dpi(78); } }    // 资源列表「时间」列：yyyy-MM-dd
        private int LocColMinW { get { return Dpi(56); } }
        private int NameColMinW { get { return Dpi(76); } }   // 资源列表出现时间列时，名称列的最小可读宽度
        // 分类分段（胶囊）尺寸
        private int ChipH { get { return Dpi(18); } }
        private int ChipPadX { get { return Dpi(6); } }
        private int ChipGap { get { return Dpi(4); } }

        private int GridItemGap { get { return Dpi(Theme.GridItemGap); } }
        private int GridItemWidth { get { return Dpi(Theme.GridItemWidth); } }
        private int GridItemHeight { get { return Dpi(Theme.GridItemHeight); } }

        private int Dpi(int value)
        {
            return DpiHelper.Scale(value, DpiHelper.ForControl(this));
        }

        public bool GridMode
        {
            get { return grid; }
            set
            {
                if (grid == value) return;
                grid = value;
                UpdateScroll();
                StartMetaLoad();
                Invalidate();
            }
        }

        public event Action<AppInfo> LaunchRequested;
        public event Action<AppInfo, Point> MenuRequested;
        /// <summary>列表行右侧操作按钮：改名。</summary>
        public event Action<AppInfo> RenameRequested;
        /// <summary>列表行右侧操作按钮：删除。</summary>
        public event Action<AppInfo> DeleteRequested;

        /// <summary>是否允许拖动排序（仅在可排序分类下由外部开启）。</summary>
        public bool AllowReorder
        {
            get { return allowReorder; }
            set
            {
                allowReorder = value;
                if (!value) ResetDrag();
                Cursor = Cursors.Default;
                Invalidate();
            }
        }

        /// <summary>拖动排序完成：from=源索引，to=目标索引（列表移除源项后插入的位置）。</summary>
        public event Action<int, int> ReorderRequested;

        /// <summary>网格内拖动条目期间持续触发（携带屏幕坐标），供外部（侧栏分类）联动高亮投放目标。</summary>
        public event Action<Point> DragMoved;
        /// <summary>拖动条目在网格外松手时触发（拖动条目列表 + 屏幕坐标）；多选时整批携带。</summary>
        public event Action<List<AppInfo>, Point> DroppedOutside;

        /// <summary>文件类型条目拖入目录类型条目：请求把条目目标（文件/文件夹）搬迁进该目录。</summary>
        public event Action<AppInfo, AppInfo> MoveIntoFolder;
        /// <summary>空白区右键：请求弹出新建菜单（文件夹/文本文档），携带屏幕坐标。</summary>
        public event Action<Point> BlankRightClicked;

        /// <summary>列表表头点击排序请求：参数为列名（name/size/time），由主窗体决定排序语义并重刷。</summary>
        public event Action<string> SortRequested;
        /// <summary>当前列表的大小/时间元数据全部就绪，供主窗体补应用异步排序。</summary>
        public event Action MetadataReady;

        /// <summary>是否接受外部文件/文件夹拖入（由外部按当前分类是否可编辑设置）。</summary>
        public bool AllowExternalDrop { get; set; }

        /// <summary>
        /// 是否允许把条目拖出网格（拖到侧栏分类即「加入」该分类，多归属，原分类与存储位置不变）。
        /// 用于只读全集分类（全部软件/全部文件/全部资源）：这些分类不支持网格内排序，但需支持拖出加入。
        /// </summary>
        public bool AllowDragOut { get; set; }

        /// <summary>外部文件/文件夹拖入主内容区：携带路径数组，由外部按当前分类入库。</summary>
        public event Action<string[]> FilesDropped;

        /// <summary>是否在列表行右侧显示操作按钮（改名/改地址/删除）。</summary>
        public bool ShowRowActions { get; set; }

        /// <summary>列表是否显示「时间」列（资源模式显示创建日期，软件模式不占用列宽）。</summary>
        public bool ShowTimeColumn { get; set; }

        private string rowDeleteText = "删除";
        private string[] rowActionLabels = new string[] { "改名", "删除" };

        /// <summary>列表行第二个操作按钮的文案：集合内为「移除」（仅从集合移出），其余为「删除」。</summary>
        public string RowDeleteText
        {
            get { return rowDeleteText; }
            set
            {
                string v = string.IsNullOrEmpty(value) ? "删除" : value;
                if (v == rowDeleteText) return;
                rowDeleteText = v;
                rowActionLabels = new string[] { "改名", v };
                Invalidate();   // 文案切换立即重绘，无需依赖调用方再刷新列表
            }
        }

        /// <summary>由外部提供：返回条目「所属」分类的显示文本（多个用逗号分隔）。</summary>
        public Func<AppInfo, string> CategoryTextProvider { get; set; }

        // 自绘悬浮信息卡（替代系统 ToolTip，风格统一）
        private string tipText = string.Empty;
        private bool tipVisible;
        private readonly System.Windows.Forms.Timer tipTimer = new System.Windows.Forms.Timer { Interval = 450 };

        /// <summary>列表「大小/时间」列缓存项：显示文本与排序值一次读取，重绘不再读盘。</summary>
        private sealed class ListMeta
        {
            public string Size = "—";
            public string Date = "—";
            public long SizeValue = -1;
            public DateTime DateValue = DateTime.MinValue;
        }

        /// <summary>一批目录元数据加载任务；切换目录后以新批次替换，旧批次结果直接作废。</summary>
        private sealed class MetaLoadBatch
        {
            public readonly List<AppInfo> Pending = new List<AppInfo>();
        }

        public AppGridControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, true);   // 允许接收键盘焦点（方向键/Enter/Delete 操作条目）
            TabStop = true;
            BackColor = Theme.Background;
            AutoScroll = true;
            Font = Theme.BodyFont;
            DoubleBuffered = true;
            subFont = Theme.CaptionFont;
            actionFont = Theme.ButtonFont;
            headerFont = Theme.SectionTitleFont;
            tipTimer.Tick += delegate { tipVisible = true; tipTimer.Stop(); Invalidate(); };
            AllowDrop = true;   // 接受外部文件/文件夹拖入（是否放行由 AllowExternalDrop 决定）
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                metaBatch = null;
                cardBrush.Dispose();
                hoverBrush.Dispose();
                rowHoverBrush.Dispose();
                textBrush.Dispose();
                subTextBrush.Dispose();
                favoriteBrush.Dispose();
                actionBlueBrush.Dispose();
                actionRedBrush.Dispose();
                // subFont/actionFont/headerFont 来自 Theme 字体缓存（应用生命周期共享），不得在此释放
                if (gridItemPath != null) gridItemPath.Dispose();
                accentPen.Dispose();
                chipBrush.Dispose();
                headerBgBrush.Dispose();
                headerLinePen.Dispose();
                cardBorderPen.Dispose();
                selectFillBrush.Dispose();
                selectRowBrush.Dispose();
                selectPen.Dispose();
                pathFormat.Dispose();
                tipFormat.Dispose();
                tipTimer.Dispose();
            }
            base.Dispose(disposing);
        }

        /// <summary>路径列 StringFormat：贴右对齐、垂直居中、超宽时中间省略。</summary>
        private static StringFormat CreatePathFormat()
        {
            StringFormat sf = new StringFormat();
            sf.Alignment = StringAlignment.Far;
            sf.LineAlignment = StringAlignment.Center;
            sf.Trimming = StringTrimming.EllipsisPath;
            sf.FormatFlags = StringFormatFlags.NoWrap;
            return sf;
        }

        /// <summary>悬浮信息卡 StringFormat：仅在文本自带的 \n 处换行（不按宽度折行），超宽以省略号截断。</summary>
        private static StringFormat CreateTipFormat()
        {
            StringFormat sf = new StringFormat();
            sf.FormatFlags = StringFormatFlags.NoWrap;
            sf.Trimming = StringTrimming.EllipsisCharacter;
            return sf;
        }

        public void SetApps(List<AppInfo> list)
        {
            // 记录旧选中条目的稳定键，替换后按键恢复：后台重扫/刷新不应打断用户的多选
            List<string> oldKeys = new List<string>();
            foreach (AppInfo a in SelectedApps)
            {
                if (a == null) continue;
                string k = a.DisplayKey();
                if (!string.IsNullOrEmpty(k) && !oldKeys.Contains(k)) oldKeys.Add(k);
            }
            ResetDrag();   // 列表被替换后旧下标立即失效，拖动状态必须作废，避免松手时把错误条目搬进错误目录
            apps.Clear();
            apps.AddRange(list);
            selected.Clear();
            selectAnchor = -1;
            collapseOnUp = false;
            for (int i = 0; i < apps.Count; i++)
            {
                string k = apps[i].DisplayKey();
                if (!string.IsNullOrEmpty(k) && oldKeys.Contains(k)) { selected.Add(i); selectAnchor = i; }
            }
            locTextMaxW = -1;
            catTextMaxW = -1;
            actionReserveMax = -1;
            UpdateScroll();
            StartMetaLoad();
            Invalidate();
        }

        /// <summary>当前条目按“可视区域优先、其余随后”排列，供图标与元数据加载共用。</summary>
        public List<AppInfo> GetAppsVisibleFirst()
        {
            List<AppInfo> ordered = new List<AppInfo>(apps.Count);
            if (apps.Count == 0) return ordered;
            bool[] visible = new bool[apps.Count];
            Rectangle viewport = new Rectangle(-AutoScrollPosition.X, -AutoScrollPosition.Y,
                ClientSize.Width, ClientSize.Height);
            for (int i = 0; i < apps.Count; i++)
            {
                Rectangle r = grid ? GridRect(i) : ListRowRect(i);
                if (!r.IntersectsWith(viewport)) continue;
                visible[i] = true;
                ordered.Add(apps[i]);
            }
            for (int i = 0; i < apps.Count; i++)
                if (!visible[i]) ordered.Add(apps[i]);
            return ordered;
        }

        /// <summary>重扫后清空元数据缓存；普通目录切换保留缓存，避免重复读盘。</summary>
        public void ClearMetadataCache()
        {
            metaBatch = null;
            lock (metaSync) { metaCache.Clear(); }
        }

        /// <summary>只读缓存的排序值；未就绪返回 false，调用方不得回退到同步读盘。</summary>
        public bool TryGetSortValues(AppInfo a, out long size, out DateTime date)
        {
            size = -1;
            date = DateTime.MinValue;
            if (a == null || !a.IsFile || string.IsNullOrEmpty(a.TargetPath)) return true;
            ListMeta meta;
            lock (metaSync)
            {
                if (!metaCache.TryGetValue(a.TargetPath, out meta)) return false;
            }
            size = meta.SizeValue;
            date = meta.DateValue;
            return true;
        }

        public void RefreshIcons()
        {
            Invalidate();
        }

        /// <summary>当前选中条目（按选中的先后顺序）；未选中返回空列表。</summary>
        public List<AppInfo> SelectedApps
        {
            get
            {
                List<AppInfo> list = new List<AppInfo>();
                for (int i = 0; i < selected.Count; i++)
                {
                    int idx = selected[i];
                    if (idx >= 0 && idx < apps.Count) list.Add(apps[idx]);
                }
                return list;
            }
        }

        /// <summary>设置表头排序指示（null 清除）；排序本身由主窗体在 RefreshGrid 中应用。</summary>
        public void SetSortIndicator(string column, bool asc)
        {
            sortColumn = column;
            sortAsc = asc;
            Invalidate();
        }

        /// <summary>表头点击：命中 名称/大小/时间 列则发起排序请求（列几何与表头绘制共用，保证命中区域一致）。</summary>
        internal void HandleHeaderClick(Graphics g, Point p)
        {
            if (grid) return;   // 仅列表模式有表头
            Rectangle refRow = new Rectangle(ListMargin, 0, ClientSize.Width - ListMargin * 2, Dpi(ListHeaderH));
            Rectangle nameRect, catRect, sizeRect, timeRect, locRect;
            ListColumns(g, refRow, ActionReserveWidth(), out nameRect, out catRect, out sizeRect, out timeRect, out locRect);
            string col = null;
            if (nameRect.Contains(p)) col = "name";
            else if (sizeRect.Contains(p)) col = "size";
            else if (ShowTimeColumn && timeRect.Contains(p)) col = "time";
            if (col == null) return;
            Action<string> h = SortRequested;
            if (h != null) h(col);
        }

        /// <summary>清空多选状态。</summary>
        public void ClearSelection()
        {
            selectAnchor = -1;
            collapseOnUp = false;
            if (selected.Count == 0) return;
            selected.Clear();
            Invalidate();
        }

        private bool IsSelected(int index)
        {
            return selected.Contains(index);
        }

        private void UpdateScroll()
        {
            int h;
            if (grid)
            {
                int rows = GridRows();
                h = rows * (GridItemHeight + GridItemGap) + GridItemGap;
            }
            else
            {
                // 列表：表头高度 + 底部留白 + 各行（含卡片间距）
                h = Dpi(ListHeaderH) + ListBottomPad + apps.Count * (ListRowH + ListGap) - (apps.Count > 0 ? ListGap : 0);
            }
            AutoScrollMinSize = new Size(0, h);
        }

        /// <summary>列表单条记录矩形（独立卡片，卡片之间留缝；首行让出顶部吸顶表头）。</summary>
        private Rectangle ListRowRect(int index)
        {
            return new Rectangle(ListMargin, ListTop + Dpi(ListHeaderH) + index * (ListRowH + ListGap),
                ClientSize.Width - ListMargin * 2, ListRowH);
        }

        /// <summary>
        /// 位置列宽度：按当前条目文本最大宽度取值（贴右边缘、右侧不留白），
        /// 结果按 SetApps 缓存；并夹在 [LocColMinW, 行宽 42%] 内，保证左侧名称区不被挤没。
        /// </summary>
        private int LocationColumnWidth(Graphics g, int rowWidth)
        {
            if (locTextMaxW < 0)
            {
                int m = 0;
                for (int i = 0; i < apps.Count; i++)
                {
                    int w = (int)Math.Ceiling(g.MeasureString(LocationText(apps[i]), subFont).Width);
                    if (w > m) m = w;
                }
                locTextMaxW = m;
            }
            int w2 = locTextMaxW + 2;
            // 资源列表多出「时间」列，位置列上限相应收紧，优先保证名称列可读
            int cap = (int)(rowWidth * (ShowTimeColumn ? 0.32f : 0.42f));
            if (w2 > cap) w2 = cap;
            if (w2 < LocColMinW) w2 = LocColMinW;
            return w2;
        }

        /// <summary>
        /// 分类列宽度：按当前条目胶囊组最大宽度取值（左对齐、上下对齐），
        /// 结果按 SetApps 缓存；并夹在 [CatColMinW, 行宽 26%] 内，保证左侧名称区不被挤没。
        /// </summary>
        private int CategoryColumnWidth(Graphics g, int rowWidth)
        {
            if (catTextMaxW < 0)
            {
                int m = 0;
                for (int i = 0; i < apps.Count; i++)
                {
                    int w = CategoryGroupWidth(g, apps[i]);
                    if (w > m) m = w;
                }
                catTextMaxW = m;
            }
            int w2 = catTextMaxW + 2;
            int cap = (int)(rowWidth * 0.26f);
            if (w2 > cap) w2 = cap;
            if (w2 < CatColMinW) w2 = CatColMinW;
            return w2;
        }

        /// <summary>整表统一的右侧操作按钮预留宽度（按最大按钮数），保证各行数据列上下对齐。</summary>
        private int ActionReserveWidth()
        {
            if (!ShowRowActions) return 0;
            if (actionReserveMax < 0)
            {
                int m = 0;
                for (int i = 0; i < apps.Count; i++)
                {
                    int c = RowActionCount(apps[i]);
                    if (c > m) m = c;
                }
                actionReserveMax = m;
            }
            return actionReserveMax * (ActionBtnW + ActionBtnGap);
        }

        /// <summary>条目所属分类拆分结果（去空白、忽略空项），供测量与绘制共用。</summary>
        private List<string> CategoryList(AppInfo a)
        {
            List<string> cats = new List<string>();
            string text = (CategoryTextProvider != null) ? CategoryTextProvider(a) : a.Category;
            if (string.IsNullOrEmpty(text)) return cats;
            string[] raw = text.Split(',');
            for (int i = 0; i < raw.Length; i++)
            {
                string s = raw[i].Trim();
                if (s.Length > 0) cats.Add(s);
            }
            return cats;
        }

        /// <summary>分类列内容宽度：全部胶囊宽度 + 间距（不含省略）。</summary>
        private int CategoryGroupWidth(Graphics g, AppInfo a)
        {
            List<string> cats = CategoryList(a);
            if (cats.Count == 0) return 0;
            int w = 0;
            for (int i = 0; i < cats.Count; i++) w += ChipWidth(g, cats[i]);
            return w + ChipGap * (cats.Count - 1);
        }

        private int Columns()
        {
            int w = ClientSize.Width - GridItemGap;
            int cols = w / (GridItemWidth + GridItemGap);
            return cols < 1 ? 1 : cols;
        }

        private int GridRows()
        {
            int cols = Columns();
            return (apps.Count + cols - 1) / cols;
        }

        private Rectangle GridRect(int index)
        {
            int cols = Columns();
            int col = index % cols;
            int row = index / cols;
            int x = GridItemGap + col * (GridItemWidth + GridItemGap);
            int y = GridItemGap + row * (GridItemHeight + GridItemGap);
            return new Rectangle(x, y, GridItemWidth, GridItemHeight);
        }

        private int HitIndex(Point client)
        {
            // 顶部吸顶表头区域不参与命中（否则滚动后点击表头会误命中其下方被遮挡的行）
            if (!grid && client.Y < Dpi(ListHeaderH)) return -1;
            Point p = new Point(client.X - AutoScrollPosition.X, client.Y - AutoScrollPosition.Y);
            // 网格/列表布局规则均固定：按坐标直接算行列并回验矩形，不再线性扫描
            if (grid)
            {
                int cols = Columns();
                int col = (p.X - GridItemGap) / (GridItemWidth + GridItemGap);
                int row = (p.Y - GridItemGap) / (GridItemHeight + GridItemGap);
                if (col >= 0 && row >= 0 && col < cols)
                {
                    int idx = row * cols + col;
                    if (idx < apps.Count && GridRect(idx).Contains(p)) return idx;
                }
            }
            else
            {
                int row = (p.Y - ListTop - Dpi(ListHeaderH)) / (ListRowH + ListGap);
                if (row >= 0 && row < apps.Count && ListRowRect(row).Contains(p)) return row;
            }
            return -1;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TranslateTransform(AutoScrollPosition.X, AutoScrollPosition.Y);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            // 空态：无条目时居中提示，替代一片空白
            if (apps.Count == 0)
            {
                GraphicsState es = g.Save();
                g.ResetTransform();
                PaintEmptyState(g);
                g.Restore(es);
                return;
            }

            if (grid) PaintGrid(g, VisibleContentRect(e.ClipRectangle));
            else PaintList(g, VisibleContentRect(e.ClipRectangle));

            if (dragging && dragGap >= 0) PaintDropIndicator(g);
            if (dragging && dropTarget >= 0) PaintFolderDropHighlight(g);

            // 自绘悬浮信息卡（iOS 深色圆角，替代系统 ToolTip）：锚定在悬停条目下方，避免遮挡条目本身
            if (tipVisible && !string.IsNullOrEmpty(tipText))
            {
                GraphicsState ts = g.Save();
                g.ResetTransform();   // 以客户区坐标绘制，便于按可视区域夹紧
                PaintTooltip(g);
                g.Restore(ts);
            }
        }

        /// <summary>
        /// 绘制悬浮信息卡：以悬停条目为锚点置于其下方（不遮挡条目图标与名称），
        /// 右/下越界时自动翻转并夹紧到可视区域内；文本过长以省略号截断。
        /// </summary>
        private void PaintTooltip(Graphics g)
        {
            if (hoverIndex < 0 || hoverIndex >= apps.Count) return;

            // 内容坐标 → 客户区坐标（与滚动位置一致）
            Rectangle anchor = grid ? GridRect(hoverIndex) : ListRowRect(hoverIndex);
            anchor.Offset(AutoScrollPosition.X, AutoScrollPosition.Y);

            SizeF sz = g.MeasureString(tipText, subFont, new SizeF(10000f, 10000f), tipFormat);
            int w = (int)Math.Ceiling(sz.Width) + 24;
            int h = (int)Math.Ceiling(sz.Height) + 14;
            int maxW = ClientSize.Width - 8;
            if (w > maxW) w = maxW;

            int x = anchor.X;
            if (x + w > ClientSize.Width - 4) x = ClientSize.Width - 4 - w;
            if (x < 4) x = 4;

            int y = anchor.Bottom + 6;                       // 默认置于条目下方
            if (y + h > ClientSize.Height - 4) y = anchor.Y - 6 - h;   // 下方空间不足则翻转到条目上方
            int minY = grid ? 4 : Dpi(ListHeaderH) + 4;           // 列表视图需避开吸顶表头
            if (y < minY) y = minY;

            Rectangle tr = new Rectangle(x, y, w, h);
            Draw.DrawShadow(g, tr, 8, 0.8f);
            using (SolidBrush b = new SolidBrush(Theme.TooltipBg))
            using (GraphicsPath p = Draw.RoundedPath(tr, 8))
                g.FillPath(b, p);
            using (SolidBrush tb = new SolidBrush(Color.White))
                g.DrawString(tipText, subFont, tb,
                    new RectangleF(tr.X + 12, tr.Y + 7, tr.Width - 24, tr.Height - 14), tipFormat);
        }

        /// <summary>空态提示：主内容区居中显示浅灰文案（网格/列表通用）。</summary>
        private void PaintEmptyState(Graphics g)
        {
            using (SolidBrush br = new SolidBrush(Theme.TextSecondary))
                Draw.DrawStringCenter(g, "暂无内容", Theme.BodyFont, br,
                    new Rectangle(0, 0, ClientSize.Width, ClientSize.Height));
        }

        /// <summary>
        /// 本次重绘的内容区可视范围（空矩形表示不裁剪）：把客户区失效区换算为内容坐标，
        /// 供 PaintGrid/PaintList 跳过屏外条目，长列表滚动/局部重绘只画可见部分。
        /// </summary>
        private Rectangle VisibleContentRect(Rectangle clip)
        {
            if (clip.IsEmpty) return Rectangle.Empty;
            clip.Offset(-AutoScrollPosition.X, -AutoScrollPosition.Y);
            clip.Inflate(8, 8);   // 外扩覆盖卡片阴影/悬停位移，避免边缘残影
            return clip;
        }

        /// <summary>网格视图：iOS 大图标卡片（轻阴影 + 悬停上浮）。</summary>
        private void PaintGrid(Graphics g, Rectangle clip)
        {
            EnsurePaths();
            for (int i = 0; i < apps.Count; i++)
            {
                Rectangle r = GridRect(i);
                if (!clip.IsEmpty && !clip.IntersectsWith(r)) continue;
                bool hover = (i == hoverIndex);

                // 默认平面卡片，仅悬停时轻微上浮并显示弱阴影
                Rectangle shadowRect = r;
                if (hover)
                {
                    shadowRect.Offset(0, -1);
                    Draw.DrawShadow(g, shadowRect, Theme.CornerRadius - 4, 0.22f);
                }

                g.TranslateTransform(shadowRect.X, shadowRect.Y);
                g.FillPath(cardBrush, gridItemPath);
                if (hover) g.FillPath(hoverBrush, gridItemPath);
                g.DrawPath(cardBorderPen, gridItemPath);
                if (IsSelected(i))
                {
                    g.FillPath(selectFillBrush, gridItemPath);
                    g.DrawPath(selectPen, gridItemPath);
                }

                AppInfo a = apps[i];
                int iconSize = Dpi(Theme.IconSize);
                Rectangle ir = new Rectangle((r.Width - iconSize) / 2, 10, iconSize, iconSize);
                if (a.Icon != null)
                {
                    try { g.DrawImage(a.Icon, ir); } catch { }
                }
                else
                {
                    // 图标尚未加载：浅灰圆角占位，避免纯白卡片出现空洞
                    using (SolidBrush pb = new SolidBrush(Theme.SegmentBg))
                        Draw.FillRoundedRect(g, pb, ir, 12);
                }
                Rectangle tr = new Rectangle(0, ir.Bottom + 6, r.Width, r.Height - ir.Height - 18);
                Draw.DrawStringCenter(g, a.DisplayName(), Font, textBrush, tr);
                if (a.IsFavorite)
                    g.FillEllipse(favoriteBrush, r.Width - 16, 6, 8, 8);

                g.TranslateTransform(-shadowRect.X, -shadowRect.Y);
            }
        }

        /// <summary>
        /// 列表视图：每条记录为独立圆角卡片（卡片之间留缝），
        /// 单行表格布局——名称 靠左，分类（左对齐）/大小（右对齐）/实际位置（贴右边缘）三列靠右且上下对齐。
        /// </summary>
        private void PaintList(Graphics g, Rectangle clip)
        {
            if (apps.Count == 0) return;

            int actionReserve = ActionReserveWidth();

            // 每条记录独立成卡片，不再合并为整块容器
            for (int i = 0; i < apps.Count; i++)
            {
                Rectangle r = ListRowRect(i);
                if (!clip.IsEmpty && !clip.IntersectsWith(r)) continue;
                AppInfo a = apps[i];

                // 独立卡片：白底圆角；选中强调色淡底 + 描边，悬停浅灰
                bool sel = IsSelected(i);
                using (GraphicsPath cp = Draw.RoundedPath(r, ListCardRadius))
                {
                    g.FillPath(sel ? selectRowBrush : (i == hoverIndex ? rowHoverBrush : cardBrush), cp);
                    g.DrawPath(sel ? selectPen : cardBorderPen, cp);
                }

                // 图标：列表用 shell 小图标按 1:1 绘制（不缩放大图，避免发虚）
                Rectangle ir = new Rectangle(r.X + 12, r.Y + (r.Height - ListIcon) / 2, ListIcon, ListIcon);
                DrawListIcon(g, a, ir);

                // 各数据列矩形（宽度整表统一，保证各行上下对齐；与表头共用同一套几何）
                Rectangle nameRect, catRect, sizeRect, timeRect, locRect;
                ListColumns(g, r, actionReserve, out nameRect, out catRect, out sizeRect, out timeRect, out locRect);

                // 名称：占据图标与分类列之间的剩余宽度，超宽省略
                Draw.DrawStringLeft(g, a.DisplayName(), Font, textBrush, nameRect);

                // 分类（左对齐，胶囊组左边缘固定 → 上下对齐）
                DrawCategoryChips(g, a, catRect.X, catRect.Right, r);

                // 大小（右对齐，数字纵向对齐）/ 时间（资源模式，创建日期）/ 实际位置（贴右边缘）
                Draw.DrawStringRight(g, FormatSize(a), subFont, subTextBrush, sizeRect);
                if (ShowTimeColumn)
                    Draw.DrawStringRight(g, FormatDate(a), subFont, subTextBrush, timeRect);
                DrawStringPath(g, LocationText(a), locRect);

                // 行右侧 iOS 文字按钮：仅悬停当前行显示（占位宽度已由 ActionReserveWidth 预留，避免列跳动）
                if (ShowRowActions && i == hoverIndex) PaintRowActions(g, a, r);
            }
        }

        /// <summary>
        /// 计算列表一行内各数据列矩形（名称/分类/大小/时间/位置），左侧为图标槽。
        /// 行与表头共用，保证列 X 完全一致；时间列仅在资源模式占位。
        /// </summary>
        private void ListColumns(Graphics g, Rectangle row, int actionReserve,
            out Rectangle nameRect, out Rectangle catRect, out Rectangle sizeRect,
            out Rectangle timeRect, out Rectangle locRect)
        {
            int rightEnd = row.Right - 12 - actionReserve;
            int textLeft = row.X + 12 + ListIcon + 10;   // 与行内图标右侧的名称起点一致
            int catW = CategoryColumnWidth(g, row.Width);
            int locW = LocationColumnWidth(g, row.Width);

            nameRect = Rectangle.Empty;
            timeRect = Rectangle.Empty;
            sizeRect = Rectangle.Empty;
            catRect = Rectangle.Empty;
            locRect = Rectangle.Empty;

            for (int attempt = 0; attempt < 2; attempt++)
            {
                locRect = new Rectangle(rightEnd - locW, row.Y, locW, row.Height);
                int x = locRect.X - ColGap;
                if (ShowTimeColumn)
                {
                    timeRect = new Rectangle(x - TimeColW, row.Y, TimeColW, row.Height);
                    x = timeRect.X - ColGap;
                }
                sizeRect = new Rectangle(x - SizeColW, row.Y, SizeColW, row.Height);
                catRect = new Rectangle(sizeRect.X - ColGap - catW, row.Y, catW, row.Height);

                int nameW = catRect.X - ColGap - textLeft;
                if (nameW < 0) nameW = 0;
                nameRect = new Rectangle(textLeft, row.Y, nameW, row.Height);

                // 名称列优先可读：宽度不够时从位置列让位（资源/软件列表同口径）
                if (nameW >= NameColMinW) break;
                int canShrink = locW - LocColMinW;
                if (canShrink <= 0) break;
                locW -= Math.Min(NameColMinW - nameW, canShrink);
            }

            // 极端窄宽度：位置列已到下限名称仍不足，强制把右侧列右移，名称至少保留 NameColMinW 可读
            if (nameRect.Width < NameColMinW)
            {
                int shift = NameColMinW - nameRect.Width;
                locRect = new Rectangle(locRect.X + shift, locRect.Y, locRect.Width, locRect.Height);
                if (ShowTimeColumn) timeRect = new Rectangle(timeRect.X + shift, timeRect.Y, timeRect.Width, timeRect.Height);
                sizeRect = new Rectangle(sizeRect.X + shift, sizeRect.Y, sizeRect.Width, sizeRect.Height);
                catRect = new Rectangle(catRect.X + shift, catRect.Y, catRect.Width, catRect.Height);
                nameRect = new Rectangle(textLeft, row.Y, NameColMinW, row.Height);
            }
        }

        /// <summary>
        /// 列表吸顶表头：底部分隔线 + 各列栏位名（与数据列对齐）。
        /// 由固定在网格顶部的 ListHeaderStrip 调用（表头不参与网格滚动，见该类说明）。
        /// </summary>
        internal void PaintListHeader(Graphics g, int width)
        {
            // 表头底色铺满整宽，遮住其下滚动经过的行
            g.FillRectangle(headerBgBrush, 0, 0, width, Dpi(ListHeaderH));
            g.DrawLine(headerLinePen, 0, Dpi(ListHeaderH) - 1, width, Dpi(ListHeaderH) - 1);

            // 用与数据行同宽同列的参照矩形取列几何（仅 X 相关，Y 用表头高度以垂直居中）
            Rectangle refRow = new Rectangle(ListMargin, 0, width - ListMargin * 2, Dpi(ListHeaderH));
            int actionReserve = ActionReserveWidth();
            Rectangle nameRect, catRect, sizeRect, timeRect, locRect;
            ListColumns(g, refRow, actionReserve, out nameRect, out catRect, out sizeRect, out timeRect, out locRect);

            // 每列栏位名与对应数据的对齐方式一致（名称/分类左对齐，大小/时间/位置右对齐）
            // 排序列名追加方向箭头（▲升序/▼降序）
            string arrow = sortAsc ? " ▲" : " ▼";
            Draw.DrawStringLeft(g, "名称" + (sortColumn == "name" ? arrow : string.Empty), headerFont, subTextBrush, nameRect);
            Draw.DrawStringLeft(g, "分类", headerFont, subTextBrush, catRect);
            Draw.DrawStringRight(g, "大小" + (sortColumn == "size" ? arrow : string.Empty), headerFont, subTextBrush, sizeRect);
            if (ShowTimeColumn)
                Draw.DrawStringRight(g, "时间" + (sortColumn == "time" ? arrow : string.Empty), headerFont, subTextBrush, timeRect);
            Draw.DrawStringRight(g, "位置", headerFont, subTextBrush, locRect);
        }

        /// <summary>列表图标：shell 小图标按 1:1 绘制（不缩放，保证清晰细腻）；大于槽位时回退缩放。</summary>
        private void DrawListIcon(Graphics g, AppInfo a, Rectangle slot)
        {
            Bitmap ico = (a.SmallIcon != null) ? a.SmallIcon : a.Icon;
            if (ico == null) return;
            try
            {
                if (ico.Width <= slot.Width && ico.Height <= slot.Height)
                {
                    g.DrawImageUnscaled(ico,
                        slot.X + (slot.Width - ico.Width) / 2,
                        slot.Y + (slot.Height - ico.Height) / 2);
                }
                else
                {
                    int iw = Math.Min(ico.Width, slot.Width);
                    int ih = Math.Min(ico.Height, slot.Height);
                    g.DrawImage(ico, new Rectangle(
                        slot.X + (slot.Width - iw) / 2, slot.Y + (slot.Height - ih) / 2, iw, ih));
                }
            }
            catch { }
        }

        /// <summary>分类列内分段（胶囊）绘制所属分类；空间不足时以「…」省略。</summary>
        private void DrawCategoryChips(Graphics g, AppInfo a, int startX, int maxRight, Rectangle row)
        {
            List<string> cats = CategoryList(a);
            if (cats.Count == 0) return;

            int x = startX;
            int ellipsisW = ChipWidth(g, "…");
            for (int i = 0; i < cats.Count; i++)
            {
                int w = ChipWidth(g, cats[i]);
                bool last = (i == cats.Count - 1);
                // 非最后一项时，为其之后的内容预留「…」的位置
                int need = last ? w : w + ChipGap + ellipsisW;
                if (x + need > maxRight)
                {
                    if (x + ellipsisW <= maxRight) DrawChip(g, "…", x, row);
                    return;
                }
                DrawChip(g, cats[i], x, row);
                x += w + ChipGap;
            }
        }

        /// <summary>绘制单个分类分段（圆角胶囊）。</summary>
        private void DrawChip(Graphics g, string text, int x, Rectangle row)
        {
            int w = ChipWidth(g, text);
            Rectangle cr = new Rectangle(x, row.Y + (row.Height - ChipH) / 2, w, ChipH);
            using (GraphicsPath p = Draw.RoundedPath(cr, 5))
                g.FillPath(chipBrush, p);
            Draw.DrawStringCenter(g, text, subFont, subTextBrush, cr);
        }

        private int ChipWidth(Graphics g, string text)
        {
            return (int)Math.Ceiling(g.MeasureString(text, subFont).Width) + ChipPadX * 2;
        }

        /// <summary>路径列：垂直居中、超宽时中间省略。</summary>
        private void DrawStringPath(Graphics g, string text, Rectangle rect)
        {
            if (string.IsNullOrEmpty(text)) return;
            g.DrawString(text, subFont, subTextBrush, rect, pathFormat);
        }

        /// <summary>「大小」列文本：文件显示真实大小、文件夹显示「文件夹」、软件显示 —。</summary>
        private string FormatSize(AppInfo a)
        {
            return MetaOf(a).Size;
        }

        /// <summary>「时间」列文本：文件/文件夹的创建日期（yyyy-MM-dd）。</summary>
        private string FormatDate(AppInfo a)
        {
            return MetaOf(a).Date;
        }

        /// <summary>绘制路径只查缓存；未就绪显示占位符，绝不因重绘同步读盘或解码媒体。</summary>
        private ListMeta MetaOf(AppInfo a)
        {
            if (a == null || !a.IsFile || string.IsNullOrEmpty(a.TargetPath)) return EmptyMeta;
            ListMeta meta;
            lock (metaSync)
            {
                return metaCache.TryGetValue(a.TargetPath, out meta) ? meta : EmptyMeta;
            }
        }

        /// <summary>后台补齐当前列表元数据：可视条目优先，单线程顺序读盘，旧目录批次自动失效。</summary>
        private void StartMetaLoad()
        {
            MetaLoadBatch batch = new MetaLoadBatch();
            bool needed = !grid || sortColumn == "size" || sortColumn == "time";
            if (needed)
            {
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                List<AppInfo> ordered = GetAppsVisibleFirst();
                for (int i = 0; i < ordered.Count; i++)
                {
                    AppInfo a = ordered[i];
                    if (a == null || !a.IsFile || string.IsNullOrEmpty(a.TargetPath) || !seen.Add(a.TargetPath)) continue;
                    lock (metaSync) { if (metaCache.ContainsKey(a.TargetPath)) continue; }
                    batch.Pending.Add(a);
                }
            }
            metaBatch = batch.Pending.Count > 0 ? batch : null;
            if (metaBatch == null) return;

            ThreadPool.QueueUserWorkItem(delegate
            {
                for (int i = 0; i < batch.Pending.Count; i++)
                {
                    if (!ReferenceEquals(metaBatch, batch)) return;
                    AppInfo a = batch.Pending[i];
                    ListMeta meta = ReadMeta(a);
                    if (!ReferenceEquals(metaBatch, batch)) return;
                    lock (metaSync)
                    {
                        if (!ReferenceEquals(metaBatch, batch)) return;
                        metaCache[a.TargetPath] = meta;
                    }

                    bool done = i == batch.Pending.Count - 1;
                    // 首批逐条渐进显示，后续每 8 项合并一次重绘，避免消息队列被刷屏。
                    if (i >= 16 && (i & 7) != 7 && !done) continue;
                    try
                    {
                        BeginInvoke((Action)delegate
                        {
                            if (IsDisposed || !ReferenceEquals(metaBatch, batch)) return;
                            Invalidate();
                            if (!done) return;
                            metaBatch = null;
                            Action h = MetadataReady;
                            if (h != null) h();
                        });
                    }
                    catch { return; }
                }
            });
        }

        /// <summary>仅在线程池调用：读取单个文件/目录的大小与时间，并生成排序缓存值。</summary>
        private static ListMeta ReadMeta(AppInfo a)
        {
            ListMeta meta = new ListMeta();
            string path = a.TargetPath;
            try
            {
                if (a.IsFolder)
                {
                    meta.Size = "文件夹";
                    if (Directory.Exists(path))
                    {
                        meta.DateValue = Directory.GetLastWriteTime(path);
                        meta.Date = meta.DateValue.ToString("yyyy-MM-dd");
                    }
                }
                else if (File.Exists(path))
                {
                    FileInfo fi = new FileInfo(path);
                    meta.SizeValue = fi.Length;
                    meta.Size = HumanSize(fi.Length);
                    DateTime taken;
                    meta.DateValue = MediaInfo.TryGetTakenDate(path, out taken) ? taken : fi.LastWriteTime;
                    meta.Date = meta.DateValue.ToString("yyyy-MM-dd");
                }
            }
            catch { }
            return meta;
        }

        private static string HumanSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            double kb = bytes / 1024.0;
            if (kb < 1024) return kb.ToString("0.#") + " KB";
            double mb = kb / 1024.0;
            if (mb < 1024) return mb.ToString("0.#") + " MB";
            return (mb / 1024.0).ToString("0.##") + " GB";
        }

        /// <summary>「实际位置」列文本：显示所在目录；网址/打包应用给出说明。</summary>
        private static string LocationText(AppInfo a)
        {
            if (a == null) return "—";
            string t = a.TargetPath;
            if (string.IsNullOrEmpty(t)) return "—";
            if (Scanner.IsUrl(t)) return "网络";
            if (Scanner.IsAppsFolderTarget(t)) return "打包应用";
            try
            {
                string trimmed = t.TrimEnd(Path.DirectorySeparatorChar);
                if (trimmed.Length == 0) return "—";
                string dir = Path.GetDirectoryName(trimmed);
                return string.IsNullOrEmpty(dir) ? trimmed : dir;
            }
            catch { return t; }
        }

        /// <summary>行操作按钮数量（改名 / 删除）。</summary>
        private int RowActionCount(AppInfo a)
        {
            return 2;
        }

        /// <summary>行右侧绘制 iOS 文字操作按钮：改名 蓝色、第二个操作（删除/移除）红色。</summary>
        private void PaintRowActions(Graphics g, AppInfo a, Rectangle r)
        {
            string[] labels = rowActionLabels;
            int total = labels.Length * (ActionBtnW + ActionBtnGap) - ActionBtnGap;
            int x = r.Right - 12 - total;
            for (int bi = 0; bi < labels.Length; bi++)
            {
                Rectangle br = new Rectangle(x + bi * (ActionBtnW + ActionBtnGap), r.Y, ActionBtnW, r.Height);
                Brush bb = bi == 1 ? (Brush)actionRedBrush : (Brush)actionBlueBrush;
                Draw.DrawStringCenter(g, labels[bi], actionFont, bb, br);
            }
        }

        /// <summary>按需构建/刷新网格卡片缓存路径（尺寸固定）。</summary>
        private void EnsurePaths()
        {
            if (gridItemPath == null)
                gridItemPath = Draw.RoundedPath(
                    new Rectangle(0, 0, GridItemWidth, GridItemHeight), Theme.CornerRadius - 4);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            // 拖动排序：越过系统拖拽阈值才开始，避免与单击/双击冲突
            if (dragPending && (e.Button & MouseButtons.Left) == MouseButtons.Left)
            {
                if (!dragging)
                {
                    Size d = SystemInformation.DragSize;
                    if (Math.Abs(e.X - dragStart.X) > d.Width || Math.Abs(e.Y - dragStart.Y) > d.Height)
                    {
                        dragging = true;
                        tipVisible = false;
                        tipTimer.Stop();
                    }
                }
                if (dragging)
                {
                    // 持续通知外部：便于侧栏分类联动高亮（指针在网格外时同样触发）
                    Action<Point> moved = DragMoved;
                    if (moved != null) moved(PointToScreen(e.Location));

                    // 多选整批拖动：不做网格内排序/目录搬迁，落点只在网格外（集合/分类）
                    if (MultiDrag)
                    {
                        if (dropTarget != -1) { dropTarget = -1; Invalidate(); }
                        if (dragGap != -1) { dragGap = -1; Invalidate(); }
                        Cursor = Cursors.Default;
                        return;
                    }

                    // 命中「可接收的目录条目」→ 高亮为搬迁目标，隐藏插入缝隙指示
                    int t = FolderDropTargetAt(e.Location);
                    if (t != dropTarget) { dropTarget = t; Invalidate(); }
                    if (t >= 0)
                    {
                        if (dragGap != -1) { dragGap = -1; Invalidate(); }
                        Cursor = Cursors.Default;
                        return;
                    }

                    // 指针移出网格边界，或当前分类仅支持拖出（不支持网格内排序）：
                    // 隐藏网格内插入指示，交由外部（侧栏分类）处理
                    if (!allowReorder || !ClientRectangle.Contains(e.Location))
                    {
                        if (dragGap != -1) { dragGap = -1; Invalidate(); }
                        Cursor = Cursors.Default;
                        return;
                    }
                    int gap = DropGap(e.Location);
                    if (gap != dragGap) { dragGap = gap; Invalidate(); }
                    Cursor = Cursors.SizeAll;
                    return;
                }
            }

            int idx = HitIndex(e.Location);
            if (idx != hoverIndex)
            {
                int old = hoverIndex;
                hoverIndex = idx;
                tipVisible = false;
                tipTimer.Stop();
                if (idx >= 0 && idx < apps.Count)
                {
                    AppInfo a = apps[idx];
                    string cat = (CategoryTextProvider != null) ? CategoryTextProvider(a) : a.Category;
                    tipText = "所属：" + cat;
                    if (!string.IsNullOrEmpty(a.TargetPath)) tipText += "\n" + a.TargetPath;
                    tipTimer.Start();
                }
                else
                {
                    tipText = string.Empty;
                }
                InvalidateHover(old, idx);
                Cursor = idx >= 0 ? Cursors.Hand : Cursors.Default;
            }
        }

        /// <summary>悬停条目切换时只重绘新旧两个条目区域（外扩覆盖阴影/行按钮），避免整屏重绘造成鼠标划过时发卡。</summary>
        private void InvalidateHover(int oldIndex, int newIndex)
        {
            Rectangle dirty = Rectangle.Empty;
            if (oldIndex >= 0 && oldIndex < apps.Count)
                dirty = grid ? GridRect(oldIndex) : ListRowRect(oldIndex);
            if (newIndex >= 0 && newIndex < apps.Count)
            {
                Rectangle r = grid ? GridRect(newIndex) : ListRowRect(newIndex);
                dirty = dirty.IsEmpty ? r : Rectangle.Union(dirty, r);
            }
            if (dirty.IsEmpty) { Invalidate(); return; }
            dirty.Inflate(10, 10);
            dirty.Offset(AutoScrollPosition.X, AutoScrollPosition.Y);
            Invalidate(dirty);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hoverIndex = -1;
            tipVisible = false;
            tipTimer.Stop();
            if (dropTarget != -1) dropTarget = -1;   // 拖出网格时清除搬迁落点高亮
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            int oldY = AutoScrollPosition.Y;
            base.OnMouseWheel(e);
            // 滚轮滚动不触发 Scroll 事件：滚动后按光标位置重算悬停项，避免高亮停留在错误条目
            if (AutoScrollPosition.Y == oldY) return;
            SyncHoverToCursor();
        }

        protected override void OnScroll(ScrollEventArgs se)
        {
            base.OnScroll(se);
            // 滚动不触发 MouseMove，需按当前光标位置重算悬停项，否则高亮会停留在错误的条目上
            SyncHoverToCursor();
            // 滚动由系统整体位移（blit）实现，吸顶表头必须整屏重绘，否则会被滚动上来的行覆盖
            Invalidate();
        }

        private void SyncHoverToCursor()
        {
            Point p = PointToClient(Cursor.Position);
            int idx = ClientRectangle.Contains(p) ? HitIndex(p) : -1;
            if (idx != hoverIndex)
            {
                hoverIndex = idx;
                Cursor = idx >= 0 ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        // ===== 外部文件/文件夹拖入（OLE 拖放） =====

        protected override void OnDragEnter(DragEventArgs e)
        {
            base.OnDragEnter(e);
            e.Effect = AcceptFileDrop(e) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        protected override void OnDragOver(DragEventArgs e)
        {
            base.OnDragOver(e);
            e.Effect = AcceptFileDrop(e) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        protected override void OnDragDrop(DragEventArgs e)
        {
            base.OnDragDrop(e);
            if (!AcceptFileDrop(e)) return;
            string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return;
            Action<string[]> h = FilesDropped;
            if (h != null) h(files);
        }

        /// <summary>是否放行拖入：当前分类可编辑且携带文件拖放数据。</summary>
        private bool AcceptFileDrop(DragEventArgs e)
        {
            return AllowExternalDrop && e.Data != null
                && e.Data.GetDataPresent(DataFormats.FileDrop);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (suppressClick) { suppressClick = false; return; }
            int idx = HitIndex(e.Location);
            if (idx < 0)
            {
                ClearSelection();   // 点击空白：结束多选
                // 空白区右键：请求新建菜单（对齐资源管理器桌面交互习惯）
                if (e.Button == MouseButtons.Right)
                {
                    Action<Point> h = BlankRightClicked;
                    if (h != null) h(PointToScreen(e.Location));
                }
                return;
            }
            if (e.Button == MouseButtons.Right)
            {
                // 右键未选中项：先转为单选，保证菜单操作作用于当前条目；右键已选中的一项保留多选
                if (!IsSelected(idx)) SelectSingle(idx);
                Point sp = PointToScreen(e.Location);
                Action<AppInfo, Point> h = MenuRequested;
                if (h != null) h(apps[idx], sp);
                return;
            }
            // 左键：列表行操作按钮优先命中
            if (!grid && ShowRowActions)
            {
                int act = HitActionButton(e.Location, idx);
                if (act >= 0)
                {
                    AppInfo a = apps[idx];
                    if (act == 0) { Action<AppInfo> h = RenameRequested; if (h != null) h(a); }
                    else if (act == 1) { Action<AppInfo> h = DeleteRequested; if (h != null) h(a); }
                    return;
                }
            }
        }

        /// <summary>命中列表行右侧操作按钮：0=改名 1=删除，未命中返回 -1。</summary>
        private int HitActionButton(Point client, int idx)
        {
            Point p = new Point(client.X - AutoScrollPosition.X, client.Y - AutoScrollPosition.Y);
            Rectangle r = ListRowRect(idx);
            int total = 2 * (ActionBtnW + ActionBtnGap) - ActionBtnGap;
            int x = r.Right - 12 - total;
            for (int bi = 0; bi < 2; bi++)
            {
                Rectangle br = new Rectangle(x + bi * (ActionBtnW + ActionBtnGap), r.Y, ActionBtnW, r.Height);
                if (br.Contains(p)) return bi;
            }
            return -1;
        }

        /// <summary>
        /// 键盘导航：方向键移动选中（网格按列数步进/列表按行），Enter 启动选中项，Delete 删除选中项。
        /// 返回 true 表示按键已被网格消费，不再向外冒泡。
        /// </summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (apps.Count > 0)
            {
                int delta = 0;
                if (keyData == Keys.Left) delta = -1;
                else if (keyData == Keys.Right) delta = 1;
                else if (keyData == Keys.Up) delta = grid ? -Columns() : -1;
                else if (keyData == Keys.Down) delta = grid ? Columns() : 1;
                else if (keyData == Keys.Return)
                {
                    List<AppInfo> sel = SelectedApps;
                    if (sel.Count > 0)
                    {
                        Action<AppInfo> h = LaunchRequested;
                        if (h != null) h(sel[0]);
                    }
                    return true;
                }
                else if (keyData == Keys.Delete)
                {
                    List<AppInfo> sel = SelectedApps;
                    if (sel.Count > 0)
                    {
                        Action<AppInfo> h = DeleteRequested;
                        if (h != null) h(sel[0]);
                    }
                    return true;
                }
                if (delta != 0)
                {
                    int cur = selected.Count > 0 ? selected[selected.Count - 1] : (delta > 0 ? -1 : apps.Count);
                    int next = cur + delta;
                    if (next < 0) next = 0;
                    if (next >= apps.Count) next = apps.Count - 1;
                    SelectSingle(next);
                    EnsureVisible(next);
                    return true;
                }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        /// <summary>滚动到指定条目可见（键盘导航后跟随滚动）。</summary>
        public void EnsureVisible(int index)
        {
            if (index < 0 || index >= apps.Count) return;
            Rectangle r = grid ? GridRect(index) : ListRowRect(index);
            int top = -AutoScrollPosition.Y;
            int view = ClientSize.Height;
            if (view <= 0) return;
            int y = top;
            if (r.Top < top) y = r.Top;
            else if (r.Bottom > top + view) y = r.Bottom - view;
            if (y != top) AutoScrollPosition = new Point(AutoScrollPosition.X, y);
        }

        /// <summary>只失效指定条目的矩形：图标加载完成后局部刷新，避免每张图标触发整网格重绘。</summary>
        public void InvalidateItem(AppInfo app)
        {
            if (app == null) return;
            for (int i = 0; i < apps.Count; i++)
            {
                if (!ReferenceEquals(apps[i], app)) continue;
                Rectangle r = grid ? GridRect(i) : ListRowRect(i);
                r.Offset(AutoScrollPosition.X, AutoScrollPosition.Y);
                Invalidate(r);
                return;
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            int idx = HitIndex(e.Location);
            if (idx < 0) return;
            Action<AppInfo> h = LaunchRequested;
            if (h != null) h(apps[idx]);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) Focus();   // 点击后获得焦点，方向键/Enter/Delete 才能作用于网格
            suppressClick = false;
            if (e.Button != MouseButtons.Left) return;

            int idx = HitIndex(e.Location);
            bool ctrl = (ModifierKeys & Keys.Control) == Keys.Control;
            bool shift = (ModifierKeys & Keys.Shift) == Keys.Shift;
            if (idx >= 0)
            {
                if (shift)
                {
                    SelectRange(idx);
                    collapseOnUp = false;
                }
                else if (ctrl)
                {
                    ToggleSelected(idx);
                    selectAnchor = idx;
                    collapseOnUp = false;
                }
                else if (!IsSelected(idx))
                {
                    SelectSingle(idx);
                    collapseOnUp = false;
                }
                else
                {
                    // 已选中的多项之一：先保持，便于整批拖出；未拖动则松手时收敛为单项
                    collapseOnUp = selected.Count > 1;
                }
            }

            if (allowReorder || AllowDragOut)
            {
                if (idx >= 0)
                {
                    dragPending = true;
                    dragging = false;
                    dragFrom = idx;
                    dragGap = -1;
                    dragStart = e.Location;
                }
            }
        }

        /// <summary>单选：清空后选中指定条目。</summary>
        private void SelectSingle(int index)
        {
            selected.Clear();
            selected.Add(index);
            selectAnchor = index;
            Invalidate();
        }

        /// <summary>Ctrl 点击：切换单项选中。</summary>
        private void ToggleSelected(int index)
        {
            if (!selected.Remove(index)) selected.Add(index);
            Invalidate();
        }

        /// <summary>Shift 点击：从锚点到当前条目连选。</summary>
        private void SelectRange(int index)
        {
            if (selectAnchor < 0 || selectAnchor >= apps.Count) { SelectSingle(index); return; }
            int from = Math.Min(selectAnchor, index);
            int to = Math.Max(selectAnchor, index);
            selected.Clear();
            for (int i = from; i <= to; i++) selected.Add(i);
            Invalidate();
        }

        /// <summary>本次拖动的条目集合：多选且拖动项在选区内时整批携带，否则单选拖动项。</summary>
        private List<AppInfo> DragApps()
        {
            List<AppInfo> list = new List<AppInfo>();
            if (dragFrom < 0 || dragFrom >= apps.Count) return list;
            if (selected.Count > 1 && IsSelected(dragFrom)) return SelectedApps;
            list.Add(apps[dragFrom]);
            return list;
        }

        /// <summary>是否多选整批拖动（此时不做网格内排序/目录搬迁）。</summary>
        private bool MultiDrag
        {
            get { return selected.Count > 1 && IsSelected(dragFrom); }
        }

        /// <summary>
        /// 拖动落点是否命中「可接收的目录条目」：仅文件类型条目（含文件夹条目）可拖入目录条目。
        /// 自嵌套 / 已在目标内等磁盘级校验由 MainForm 复核。未命中返回 -1。
        /// </summary>
        private int FolderDropTargetAt(Point client)
        {
            if (!ClientRectangle.Contains(client)) return -1;
            if (dragFrom < 0 || dragFrom >= apps.Count) return -1;
            AppInfo src = apps[dragFrom];
            if (!src.IsFile) return -1;                       // 仅文件类型条目可拖入目录
            int idx = HitIndex(client);
            if (idx < 0 || idx >= apps.Count || idx == dragFrom) return -1;
            AppInfo t = apps[idx];
            if (!t.IsFile || !t.IsFolder) return -1;          // 目标须为目录类型条目
            return idx;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            // 文件条目拖入目录条目：请求搬迁（优先于重排与拖出；多选整批拖动不参与）
            if (dragging && !MultiDrag && dropTarget >= 0 && dropTarget != dragFrom
                && dragFrom >= 0 && dragFrom < apps.Count && dropTarget < apps.Count)
            {
                Action<AppInfo, AppInfo> h = MoveIntoFolder;
                if (h != null) h(apps[dragFrom], apps[dropTarget]);
                collapseOnUp = false;
                ResetDrag();
                suppressClick = true;
                Invalidate();
                return;
            }
            bool wasDrag = dragging;
            bool droppedOutside = wasDrag && !ClientRectangle.Contains(e.Location)
                && dragFrom >= 0 && dragFrom < apps.Count;
            if (droppedOutside)
            {
                Action<List<AppInfo>, Point> h = DroppedOutside;
                if (h != null) h(DragApps(), PointToScreen(e.Location));
            }
            else if (wasDrag && dragFrom >= 0 && dragGap >= 0)
            {
                int to = dragGap > dragFrom ? dragGap - 1 : dragGap;
                if (to != dragFrom)
                {
                    Action<int, int> h = ReorderRequested;
                    if (h != null) h(dragFrom, to);
                }
            }
            if (!wasDrag && collapseOnUp)
            {
                // 已选多项时普通点击（未拖动）：收敛为单项
                int idx = HitIndex(e.Location);
                if (idx >= 0 && IsSelected(idx)) SelectSingle(idx);
            }
            collapseOnUp = false;
            if (wasDrag) suppressClick = true;
            ResetDrag();
            Cursor = Cursors.Default;
            if (wasDrag) Invalidate();
        }

        private void ResetDrag()
        {
            dragPending = false;
            dragging = false;
            dragFrom = -1;
            dragGap = -1;
            dropTarget = -1;
        }

        /// <summary>计算插入缝隙位置（0..count），供拖动指示与落点判定。</summary>
        private int DropGap(Point client)
        {
            int count = apps.Count;
            if (count == 0) return 0;
            Point p = new Point(client.X - AutoScrollPosition.X, client.Y - AutoScrollPosition.Y);
            if (grid)
            {
                int cols = Columns();
                int cw = GridItemWidth + GridItemGap;
                int rh = GridItemHeight + GridItemGap;
                int gcol = (p.X - GridItemGap) / cw;
                int grow = (p.Y - GridItemGap) / rh;
                if (gcol < 0) gcol = 0;
                if (gcol >= cols) gcol = cols - 1;
                if (grow < 0) grow = 0;
                int gidx = grow * cols + gcol;
                if (gidx < 0) return 0;
                if (gidx >= count) return count;
                Rectangle r = GridRect(gidx);
                return (p.X < r.X + r.Width / 2) ? gidx : gidx + 1;
            }
            int pitch = ListRowH + ListGap;
            int idx = (p.Y - ListTop - Dpi(ListHeaderH)) / pitch;
            if (idx < 0) return 0;
            if (idx >= count) return count;
            Rectangle row = ListRowRect(idx);
            return (p.Y < row.Y + row.Height / 2) ? idx : idx + 1;
        }

        /// <summary>文件拖入目录的落点高亮：目标条目以强调色描边（网格卡片 / 列表行通用）。</summary>
        private void PaintFolderDropHighlight(Graphics g)
        {
            if (dropTarget < 0 || dropTarget >= apps.Count) return;
            using (Pen pen = new Pen(Theme.Accent, 2F))
            {
                if (grid)
                {
                    EnsurePaths();
                    Rectangle r = GridRect(dropTarget);
                    System.Drawing.Drawing2D.GraphicsState st = g.Save();
                    g.TranslateTransform(r.X, r.Y);
                    g.DrawPath(pen, gridItemPath);
                    g.Restore(st);
                }
                else
                {
                    Rectangle r = ListRowRect(dropTarget);
                    using (GraphicsPath p = Draw.RoundedPath(r, ListCardRadius))
                        g.DrawPath(pen, p);
                }
            }
        }

        /// <summary>拖动落点指示：网格竖线（带圆帽）/ 列表横线。</summary>
        private void PaintDropIndicator(Graphics g)
        {
            int count = apps.Count;
            if (count == 0) return;
            if (grid)
            {
                int gap = dragGap > count ? count : dragGap;
                Rectangle refRect;
                int x;
                if (gap < count) { refRect = GridRect(gap); x = refRect.X - 4; }
                else { refRect = GridRect(count - 1); x = refRect.Right + 4; }
                g.DrawLine(accentPen, x, refRect.Y, x, refRect.Bottom);
                g.FillEllipse(favoriteBrush, x - 3, refRect.Y - 3, 6, 6);
                g.FillEllipse(favoriteBrush, x - 3, refRect.Bottom - 3, 6, 6);
            }
            else
            {
                int gap = dragGap > count ? count : dragGap;
                int y = ListTop + Dpi(ListHeaderH) + gap * (ListRowH + ListGap) - ListGap / 2;
                g.DrawLine(accentPen, ListMargin + 6, y, ClientSize.Width - ListMargin - 6, y);
            }
        }
    }

    /// <summary>
    /// 列表栏位表头条：固定盖在网格内容区顶部的独立控件，不参与网格滚动。
    /// 表头若绘制在滚动面内，系统滚动（ScrollWindowEx 位移像素）会把行内容带进表头带再重绘，
    /// 表现为滚动时列名晃动/穿模；表头独立成 HWND 后，滚动位移永远影响不到列名占位。
    /// 覆盖区域与网格顶部表头死区（HitIndex 对 Y &lt; ListHeaderH 返回 -1）完全重合，不拦截条目交互；
    /// 不获取焦点，滚轮消息仍发给已聚焦的网格。
    /// </summary>
    internal sealed class ListHeaderStrip : Control
    {
        private readonly AppGridControl grid;

        public ListHeaderStrip(AppGridControl grid)
        {
            this.grid = grid;
            SetStyle(ControlStyles.Selectable, false);   // 不抢焦点：滚轮仍发给已聚焦的网格
            // 整区自绘 + 双缓冲：网格 Invalidate 会级联触发表头重绘（见下），
            // 无双缓冲时 FillRectangle 直绘屏幕，栏位文字会出现可见闪烁
            SetStyle(ControlStyles.Opaque | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            BackColor = Theme.Background;
            // 网格任何重绘（内容/尺寸/列宽变化）都同步刷新表头，保证栏位与数据列对齐
            grid.Invalidated += delegate { if (Visible) Invalidate(); };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            grid.PaintListHeader(e.Graphics, Width);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            // 命中可排序列（名称/大小/时间）时向主窗体发起排序请求
            using (Graphics g = CreateGraphics())
                grid.HandleHeaderClick(g, e.Location);
        }
    }
}

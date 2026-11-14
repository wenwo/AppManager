using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using AppManager;

namespace AppManager.Controls
{
    /// <summary>
    /// 设置页：作为独立页面覆盖在主内容区之上（标题行与窗口按钮保留），点击「返回」退出。
    /// 单列分组卡片（Windows 11 设置风格）：固定页头 + 可滚动内容，每行左侧为彩色图标块，
    /// 右侧为开关、箭头或按钮。除「返回」外全部自绘，行内可点击区域由 Zone 统一命中，
    /// 因此滚动时不存在子控件错位或压住页头的问题。
    /// 资源存储位置直接列在页面里，可就地添加、启停和调整顺序；改动由主窗体校验并保存后回填。
    /// </summary>
    public class SettingsPage : Control
    {
        /// <summary>「重新扫描」行的状态，由主窗体在扫描开始和结束时回传。</summary>
        public enum RescanState { Idle, Running, Done, Failed }

        // 尺寸均为 96 DPI 下的逻辑像素，使用处统一经 Dpi() 缩放
        private const int ContentMaxW = 720;   // 内容列最大宽度
        private const int HeaderH = 74;        // 固定页头高度（返回按钮 + 标题）
        private const int RowH = 64;           // 行高（图标 + 标题 + 说明）
        private const int SectionGap = 22;     // 分组间距
        private const int CardPad = 18;        // 卡片内左右留白
        private const int CardRadius = 10;
        private const int IconTile = 36;       // 图标块边长
        private const int ScrollBarW = 4;      // 滚动条可视宽度
        private const int ScrollHitW = 14;     // 滚动条命中宽度（比可视条宽，便于抓取）
        // 滚动条避开主窗体的边缘缩放命中区（MainForm.ResizeHitTest：右缘 7px、右下角 15px），否则点滑块会变成缩放窗口
        private const int ScrollEdgeGap = 8;
        private const int ScrollCornerGap = 16;

        // 图标字形（Segoe Fluent Icons / MDL2 Assets 共有的码位）
        private const string GlyphEye = "\uE890";
        private const string GlyphTray = "\uE896";
        private const string GlyphSync = "\uE895";
        private const string GlyphRefresh = "\uE72C";
        private const string GlyphDrive = "\uEDA2";
        private const string GlyphHistory = "\uE81C";
        private const string GlyphRepair = "\uE90F";
        private const string GlyphTest = "\uE9D9";

        // 图标块配色：每类设置一个色相，图标块底色为该色与卡片白的浅混色
        private static readonly Color TintBlue = Theme.Accent;
        private static readonly Color TintGreen = Color.FromArgb(38, 166, 91);
        private static readonly Color TintOrange = Color.FromArgb(240, 140, 30);
        private static readonly Color TintPurple = Color.FromArgb(124, 92, 235);
        private static readonly Color TintTeal = Color.FromArgb(16, 160, 170);
        private static readonly Color TintGray = Color.FromArgb(110, 116, 125);
        private static readonly Color WarningText = Color.FromArgb(196, 98, 0);

        // 路径文本过长时省略中间，保留盘符和末级目录
        private static readonly StringFormat PathFormat = MakePathFormat();

        private enum RowKind { Toggle, Navigate, Button, Store, Add, Note }
        private enum ZoneKind { Row, StoreToggle, MoveUp, MoveDown }
        private enum Arrow { Up, Down, Right }

        /// <summary>一行内容。Bounds 为内容坐标（未减滚动偏移）。</summary>
        private sealed class Row
        {
            public RowKind Kind;
            public Rectangle Bounds;
            public string Glyph;
            public Color Tint;
            public string Title = string.Empty;
            public string Subtitle;
            public bool Checked;           // 开关行、存储位置行的开关状态
            public string ButtonText;      // 按钮行右侧按钮文字
            public bool Enabled = true;    // 为假时置灰且不响应点击
            public bool Child;             // 依附上一行的子项：不画图标块，文字与上一行对齐
            public bool Warning;           // 说明文字用警示色
            public bool FirstInCard;
            public bool LastInCard;
            public Action Activate;        // 整行点击的动作；存储位置行和说明行为空
            public Action Toggle;          // 存储位置行：启用/停用
            public Action MoveUp;          // 存储位置行：上移（只有一个位置时为空，不显示排序按钮）
            public Action MoveDown;
            public bool CanMoveUp;
            public bool CanMoveDown;
            public int RowZone = -1;       // 整行点击区在 zones 中的下标
        }

        /// <summary>一个可点击区域。Bounds 为内容坐标。</summary>
        private sealed class Zone
        {
            public ZoneKind Kind;
            public int Row;
            public Rectangle Bounds;
            public bool Enabled;
            public Action Action;
        }

        /// <summary>一个分组：标题（可带一行说明）+ 白色圆角卡片。</summary>
        private sealed class Section
        {
            public string Title = string.Empty;
            public string Note;
            public bool NoteWarning;
            public Rectangle LabelRect;
            public Rectangle NoteRect;
            public Rectangle CardRect;
        }

        private readonly RoundButton backBtn;
        private readonly ToolTip pathTip = new ToolTip();   // 悬停存储位置行时显示完整路径
        private string pathTipText = string.Empty;
        private readonly List<Section> sections = new List<Section>();
        private readonly List<Row> rows = new List<Row>();
        private readonly List<Zone> zones = new List<Zone>();
        private readonly List<ResourceStoreLocation> storeLocations = new List<ResourceStoreLocation>();

        private bool showHidden;
        private bool autoScan;
        private bool autoOrganizeDesktop;
        private bool closeToTray;
        private string defaultStoreRoot = string.Empty;
        private RescanState rescanState = RescanState.Idle;
        private bool selfTestRunning;

        private Rectangle titleRect;
        private Rectangle contentSpan;      // 内容列的水平范围（页头分隔线用）
        private int contentHeight;
        private int scrollOffset;
        private int wheelRemainder;         // 不足一像素的滚轮累计量（单位：delta × 像素）
        private bool scrollDragging;
        private int scrollGrabDy;
        private bool scrollbarHot;
        private int hoverZone = -1;
        private int pressedZone = -1;

        public event EventHandler BackRequested;
        public event EventHandler ShowHiddenChanged;
        public event EventHandler AutoScanChanged;
        public event EventHandler AutoOrganizeDesktopChanged;
        public event EventHandler CloseToTrayChanged;
        public event EventHandler RescanRequested;
        public event EventHandler SelfTestRequested;
        public event EventHandler LayoutBackupRequested;
        public event EventHandler DatabaseRequested;
        /// <summary>请求添加一个存储位置（由主窗体弹出选择文件夹）。</summary>
        public event EventHandler StorageAddRequested;
        /// <summary>请求启用或停用存储位置：参数为位置下标、目标启用状态。</summary>
        public event Action<int, bool> StorageEnabledRequested;
        /// <summary>请求调整存储位置顺序：参数为位置下标、方向（-1 上移，1 下移）。</summary>
        public event Action<int, int> StorageMoveRequested;

        public SettingsPage()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.UserPaint
                | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Theme.Background;

            backBtn = new RoundButton();
            backBtn.Text = "返回";
            backBtn.Icon = UiIconKind.Back;
            backBtn.Accent = false;
            backBtn.Click += delegate { Raise(BackRequested); };
            Controls.Add(backBtn);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) pathTip.Dispose();
            base.Dispose(disposing);
        }

        private static StringFormat MakePathFormat()
        {
            StringFormat sf = new StringFormat();
            sf.Alignment = StringAlignment.Near;
            sf.LineAlignment = StringAlignment.Center;
            sf.Trimming = StringTrimming.EllipsisPath;
            sf.FormatFlags = StringFormatFlags.NoWrap;
            return sf;
        }

        private int Dpi(int value)
        {
            return DpiHelper.Scale(value, DpiHelper.ForControl(this));
        }

        /// <summary>「返回」按钮的位置和尺寸由 Rebuild 按当前 DPI 计算，不再叠加框架缩放。</summary>
        protected override bool ScaleChildren { get { return false; } }

        // ===== 对外状态 =====

        public bool ShowHidden { get { return showHidden; } }
        public bool AutoScan { get { return autoScan; } }
        public bool AutoOrganizeDesktop { get { return autoOrganizeDesktop; } }
        public bool CloseToTray { get { return closeToTray; } }

        /// <summary>
        /// 回填页面状态（开关与存储位置列表）；不触发变更事件，也不改变滚动位置。
        /// locations 为配置里的有序位置（含已停用），为空时页面显示 defaultStoreRoot 作为默认位置。
        /// </summary>
        public void Bind(bool showHidden, bool autoScan, bool autoOrganizeDesktop, bool closeToTray,
            IList<ResourceStoreLocation> locations, string defaultStoreRoot)
        {
            this.showHidden = showHidden;
            this.autoScan = autoScan;
            this.autoOrganizeDesktop = autoOrganizeDesktop;
            this.closeToTray = closeToTray;
            storeLocations.Clear();
            if (locations != null)
                foreach (ResourceStoreLocation location in locations)
                    if (location != null) storeLocations.Add(location.Clone());
            this.defaultStoreRoot = defaultStoreRoot ?? string.Empty;
            Rebuild();
        }

        /// <summary>更新「重新扫描」行：进行中置灰防止重复触发，结束后在说明里给出结果。</summary>
        public void SetRescanState(RescanState state)
        {
            if (rescanState == state) return;
            rescanState = state;
            Rebuild();
        }

        /// <summary>更新「运行自测」行：运行中置灰防止重复触发。</summary>
        public void SetSelfTestRunning(bool running)
        {
            if (selfTestRunning == running) return;
            selfTestRunning = running;
            Rebuild();
        }

        /// <summary>回到页面顶部（每次进入设置页时调用）。</summary>
        public void ScrollToTop()
        {
            wheelRemainder = 0;
            SetScroll(0);
        }

        /// <summary>鼠标滚轮滚动页面（由窗体消息过滤器按光标位置路由到此）。delta 为 WM_MOUSEWHEEL 增量。</summary>
        public void ScrollByWheel(int delta)
        {
            if (delta == 0 || MaxScroll <= 0) return;
            int lines = SystemInformation.MouseWheelScrollLines;
            if (lines <= 0) lines = 3;
            // 按像素换算并累计余数：精确式触控板/高分辨率滚轮每次 delta 小于 120，整除会得到 0 而完全滚不动
            wheelRemainder += delta * Dpi(32) * lines;
            int pixels = wheelRemainder / 120;
            wheelRemainder -= pixels * 120;
            if (pixels != 0) SetScroll(scrollOffset - pixels);
        }

        private void Raise(EventHandler handler)
        {
            if (handler != null) handler(this, EventArgs.Empty);
        }

        // ===== 布局 =====

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Rebuild();
        }

        /// <summary>按当前状态和尺寸重建分组、行与点击区，夹紧滚动位置并重绘。</summary>
        private void Rebuild()
        {
            sections.Clear();
            rows.Clear();
            zones.Clear();
            hoverZone = -1;
            pressedZone = -1;
            contentHeight = 0;
            if (Width <= 0 || Height <= 0) return;

            int width = Math.Min(Dpi(ContentMaxW), Math.Max(Dpi(360), Width - Dpi(96)));
            int left = (Width - width) / 2;
            contentSpan = new Rectangle(left, 0, width, 0);
            backBtn.Bounds = new Rectangle(left, Dpi(22), Dpi(74), Dpi(30));
            titleRect = new Rectangle(backBtn.Right + Dpi(14), Dpi(22), Math.Max(0, width - Dpi(88)), Dpi(30));

            int y = Dpi(4);
            y = AddSection("常规", null, false, left, width, y, new Row[]
            {
                ToggleRow(GlyphEye, TintPurple, "显示隐藏项", "在列表里显示已设为隐藏的软件和资源。", showHidden, delegate
                {
                    showHidden = !showHidden;
                    Rebuild();
                    Raise(ShowHiddenChanged);
                }),
                ToggleRow(GlyphTray, TintBlue, "关闭时隐藏到托盘", "开启后关闭窗口隐藏到托盘；关闭则直接退出。", closeToTray, delegate
                {
                    closeToTray = !closeToTray;
                    Rebuild();
                    Raise(CloseToTrayChanged);
                })
            });

            // 「自动收纳」由自动扫描的监听触发：作为它的子项显示，扫描关闭时置灰并说明原因
            Row organize = ToggleRow(null, TintGreen, "自动收纳桌面新快捷方式",
                autoScan ? "桌面出现新快捷方式时自动收进「快捷中心」；关闭则只更新列表，不移动文件。"
                         : "需要先开启上面的自动扫描。",
                autoOrganizeDesktop, delegate
                {
                    autoOrganizeDesktop = !autoOrganizeDesktop;
                    Rebuild();
                    Raise(AutoOrganizeDesktopChanged);
                });
            organize.Child = true;
            organize.Enabled = autoScan;
            y = AddSection("扫描与收纳", null, false, left, width, y, new Row[]
            {
                ToggleRow(GlyphSync, TintGreen, "自动扫描应用与桌面变化", "新装软件或桌面有变化时自动更新列表。", autoScan, delegate
                {
                    autoScan = !autoScan;
                    Rebuild();
                    Raise(AutoScanChanged);
                }),
                organize,
                RescanRow()
            });

            bool anyEnabled = storeLocations.Count == 0;
            foreach (ResourceStoreLocation location in storeLocations) if (location.Enabled) anyEnabled = true;
            y = AddSection("资源存储位置",
                anyEnabled ? "导入的资源按分类存放在这里。有多个位置时从上到下依次使用。"
                           : "所有位置都已停用，现在无法导入资源。",
                !anyEnabled, left, width, y, StoreRows());

            y = AddSection("备份与数据", null, false, left, width, y, new Row[]
            {
                NavRow(GlyphHistory, TintBlue, "布局备份与还原", "备份当前的分类和排列，或还原到之前的备份。",
                    delegate { Raise(LayoutBackupRequested); }),
                NavRow(GlyphRepair, TintPurple, "数据维护与操作记录", "查看操作记录、撤销最近一次操作，完整备份或恢复数据库。",
                    delegate { Raise(DatabaseRequested); })
            });

            y = AddSection("诊断", null, false, left, width, y, new Row[]
            {
                ButtonRow(GlyphTest, TintGray, "运行自测", "在独立进程里检查核心功能，结果写入 selftest.log。",
                    selfTestRunning ? "运行中…" : "运行", selfTestRunning, delegate { Raise(SelfTestRequested); })
            });

            contentHeight = y - Dpi(SectionGap) + Dpi(28);
            if (scrollOffset > MaxScroll) scrollOffset = MaxScroll;
            hoverZone = ZoneUnderCursor();
            Invalidate();
        }

        /// <summary>追加一个分组，返回下一个分组的起始 Y（内容坐标）。</summary>
        private int AddSection(string title, string note, bool noteWarning, int x, int width, int y, IList<Row> list)
        {
            Section s = new Section();
            s.Title = title;
            s.Note = note;
            s.NoteWarning = noteWarning;
            s.LabelRect = new Rectangle(x + Dpi(4), y, width - Dpi(8), Dpi(22));
            int cardTop = s.LabelRect.Bottom + Dpi(6);
            if (!string.IsNullOrEmpty(note))
            {
                s.NoteRect = new Rectangle(x + Dpi(4), s.LabelRect.Bottom, width - Dpi(8), Dpi(20));
                cardTop = s.NoteRect.Bottom + Dpi(8);
            }
            int rowTop = cardTop;
            for (int i = 0; i < list.Count; i++)
            {
                Row r = list[i];
                r.Bounds = new Rectangle(x, rowTop, width, Dpi(RowH));
                r.FirstInCard = (i == 0);
                r.LastInCard = (i == list.Count - 1);
                rows.Add(r);
                AddZones(r, rows.Count - 1);
                rowTop += r.Bounds.Height;
            }
            s.CardRect = new Rectangle(x, cardTop, width, rowTop - cardTop);
            sections.Add(s);
            return s.CardRect.Bottom + Dpi(SectionGap);
        }

        /// <summary>登记一行的点击区：普通行整行可点；存储位置行只有开关和排序按钮可点。</summary>
        private void AddZones(Row r, int index)
        {
            if (r.Activate != null)
            {
                r.RowZone = zones.Count;
                AddZone(ZoneKind.Row, index, r.Bounds, r.Enabled, r.Activate);
                return;
            }
            if (r.Kind != RowKind.Store) return;
            AddZone(ZoneKind.StoreToggle, index, Rectangle.Inflate(ToggleRect(r.Bounds), Dpi(6), Dpi(8)), true, r.Toggle);
            if (r.MoveUp == null) return;
            AddZone(ZoneKind.MoveUp, index, MoveRect(r.Bounds, true), r.CanMoveUp, r.MoveUp);
            AddZone(ZoneKind.MoveDown, index, MoveRect(r.Bounds, false), r.CanMoveDown, r.MoveDown);
        }

        private void AddZone(ZoneKind kind, int row, Rectangle bounds, bool enabled, Action action)
        {
            Zone z = new Zone();
            z.Kind = kind;
            z.Row = row;
            z.Bounds = bounds;
            z.Enabled = enabled;
            z.Action = action;
            zones.Add(z);
        }

        private Row ToggleRow(string glyph, Color tint, string title, string subtitle, bool value, Action flip)
        {
            Row r = new Row();
            r.Kind = RowKind.Toggle;
            r.Glyph = glyph;
            r.Tint = tint;
            r.Title = title;
            r.Subtitle = subtitle;
            r.Checked = value;
            r.Activate = flip;
            return r;
        }

        /// <summary>打开下一级窗口的行（右侧箭头）。</summary>
        private Row NavRow(string glyph, Color tint, string title, string subtitle, Action open)
        {
            Row r = new Row();
            r.Kind = RowKind.Navigate;
            r.Glyph = glyph;
            r.Tint = tint;
            r.Title = title;
            r.Subtitle = subtitle;
            r.Activate = open;
            return r;
        }

        /// <summary>立即执行的行（右侧按钮）；busy 时置灰，不重复触发。</summary>
        private Row ButtonRow(string glyph, Color tint, string title, string subtitle, string button, bool busy, Action run)
        {
            Row r = new Row();
            r.Kind = RowKind.Button;
            r.Glyph = glyph;
            r.Tint = tint;
            r.Title = title;
            r.Subtitle = subtitle;
            r.ButtonText = button;
            r.Enabled = !busy;
            r.Activate = run;
            return r;
        }

        private Row RescanRow()
        {
            bool running = rescanState == RescanState.Running;
            Row r = ButtonRow(GlyphRefresh, TintTeal, "重新扫描", "立即重新读取应用、桌面和已收录的内容。",
                running ? "扫描中…" : "立即扫描", running, delegate { Raise(RescanRequested); });
            if (rescanState == RescanState.Done) r.Subtitle = "扫描完成，列表已更新。";
            else if (rescanState == RescanState.Failed)
            {
                r.Subtitle = "扫描失败，已保留当前列表。";
                r.Warning = true;
            }
            return r;
        }

        /// <summary>存储位置分组的行：每个位置一行（没有显式位置时显示默认位置），最后是「添加」。</summary>
        private List<Row> StoreRows()
        {
            List<Row> list = new List<Row>();
            int count = storeLocations.Count;
            int firstEnabled = -1;
            for (int i = 0; i < count && firstEnabled < 0; i++)
                if (storeLocations[i].Enabled) firstEnabled = i;
            for (int i = 0; i < count; i++)
            {
                int index = i;   // 每行的委托各自捕获自己的下标
                bool enabled = storeLocations[i].Enabled;
                Row r = new Row();
                r.Kind = RowKind.Store;
                r.Glyph = GlyphDrive;
                r.Tint = enabled ? TintOrange : TintGray;
                r.Title = storeLocations[i].Path;
                r.Checked = enabled;
                if (!enabled) r.Subtitle = "已停用：不再存入新资源，里面已有的资源照常显示。";
                else if (i == firstEnabled) r.Subtitle = "优先使用。";
                else r.Subtitle = "排在前面的位置不可用或放不下时使用。";
                r.Toggle = delegate
                {
                    Action<int, bool> handler = StorageEnabledRequested;
                    if (handler != null) handler(index, !enabled);
                };
                if (count > 1)
                {
                    r.CanMoveUp = i > 0;
                    r.CanMoveDown = i < count - 1;
                    r.MoveUp = delegate { RaiseStorageMove(index, -1); };
                    r.MoveDown = delegate { RaiseStorageMove(index, 1); };
                }
                list.Add(r);
            }
            if (count == 0)
            {
                Row note = new Row();
                note.Kind = RowKind.Note;
                note.Glyph = GlyphDrive;
                note.Tint = TintGray;
                note.Title = defaultStoreRoot;
                note.Subtitle = "默认位置。添加其它位置后它会保留在列表第一项，可以再停用或调整顺序。";
                list.Add(note);
            }
            Row add = new Row();
            add.Kind = RowKind.Add;
            add.Tint = TintBlue;
            add.Title = "添加存储位置…";
            add.Activate = delegate { Raise(StorageAddRequested); };
            list.Add(add);
            return list;
        }

        private void RaiseStorageMove(int index, int delta)
        {
            Action<int, int> handler = StorageMoveRequested;
            if (handler != null) handler(index, delta);
        }

        /// <summary>行右侧开关的矩形。</summary>
        private Rectangle ToggleRect(Rectangle row)
        {
            Size size = new Size(Dpi(46), Dpi(28));
            return new Rectangle(row.Right - Dpi(CardPad) - size.Width, row.Top + (row.Height - size.Height) / 2,
                size.Width, size.Height);
        }

        /// <summary>存储位置行的排序按钮矩形：位于开关左侧，上移在左、下移在右。</summary>
        private Rectangle MoveRect(Rectangle row, bool up)
        {
            int size = Dpi(28);
            int right = ToggleRect(row).Left - Dpi(14);
            int x = right - size - (up ? size + Dpi(2) : 0);
            return new Rectangle(x, row.Top + (row.Height - size) / 2, size, size);
        }

        // ===== 滚动 =====

        /// <summary>页头以下的可滚动视口（客户区坐标）。</summary>
        private Rectangle Viewport
        {
            get
            {
                int top = Dpi(HeaderH);
                return new Rectangle(0, top, Width, Math.Max(0, Height - top));
            }
        }

        private int MaxScroll { get { return Math.Max(0, contentHeight - Viewport.Height); } }

        /// <summary>内容坐标换算为客户区坐标。</summary>
        private Point ToClient(Point content)
        {
            return new Point(content.X, content.Y + Viewport.Top - scrollOffset);
        }

        private Rectangle ScrollTrackRect()
        {
            Rectangle v = Viewport;
            int w = Dpi(ScrollBarW);
            return new Rectangle(Width - Dpi(ScrollEdgeGap) - Dpi(2) - w, v.Top + Dpi(4), w,
                Math.Max(0, v.Height - Dpi(4) - Dpi(ScrollCornerGap)));
        }

        /// <summary>滚动条命中区：比可视条宽，整体位于窗体边缘缩放区内侧。</summary>
        private Rectangle ScrollHitRect()
        {
            Rectangle v = Viewport;
            int w = Dpi(ScrollHitW);
            return new Rectangle(Width - Dpi(ScrollEdgeGap) - w, v.Top, w, Math.Max(0, v.Height - Dpi(ScrollCornerGap)));
        }

        /// <summary>滚动条滑块矩形；无需滚动时为空。</summary>
        private Rectangle ScrollThumbRect()
        {
            if (MaxScroll <= 0) return Rectangle.Empty;
            Rectangle t = ScrollTrackRect();
            int thumbH = (int)((long)t.Height * Viewport.Height / contentHeight);
            if (thumbH < Dpi(30)) thumbH = Dpi(30);
            if (thumbH > t.Height) thumbH = t.Height;
            int travel = t.Height - thumbH;
            int y = t.Y + (travel <= 0 ? 0 : (int)((long)travel * scrollOffset / MaxScroll));
            return new Rectangle(t.X, y, t.Width, thumbH);
        }

        private void SetScroll(int value)
        {
            int max = MaxScroll;
            if (value < 0) value = 0;
            if (value > max) value = max;
            if (value == scrollOffset) return;
            scrollOffset = value;
            hoverZone = ZoneUnderCursor();   // 滚动不触发 MouseMove，需按当前光标重算悬停区
            Invalidate();
        }

        // ===== 绘制 =====

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush bg = new SolidBrush(Theme.Background))
                g.FillRectangle(bg, ClientRectangle);
            if (contentHeight <= 0) return;

            // 滚动内容：裁剪到视口内，页头区域始终干净
            Rectangle viewport = Viewport;
            GraphicsState state = g.Save();
            try
            {
                g.SetClip(viewport);
                g.TranslateTransform(0, viewport.Top - scrollOffset);
                PaintSections(g);
                for (int i = 0; i < rows.Count; i++) PaintRow(g, i);
            }
            finally { g.Restore(state); }

            // 固定页头：标题；内容滚上来后在页头下缘画一条分隔线
            using (SolidBrush tb = new SolidBrush(Theme.TextPrimary))
                Draw.DrawStringLeft(g, "设置", Theme.PageTitleFont, tb, titleRect);
            if (scrollOffset > 0)
            {
                using (Pen line = new Pen(Theme.Separator, 1))
                    g.DrawLine(line, contentSpan.Left, viewport.Top - 1, contentSpan.Right, viewport.Top - 1);
            }

            // 滚动条（仅内容超出视口时显示，细条风格，与侧栏一致）
            if (MaxScroll > 0)
            {
                int alpha = (scrollbarHot || scrollDragging) ? 150 : 80;
                using (SolidBrush tb = new SolidBrush(Color.FromArgb(alpha, 60, 60, 67)))
                    Draw.FillRoundedRect(g, tb, ScrollThumbRect(), Dpi(ScrollBarW) / 2);
            }
        }

        private void PaintSections(Graphics g)
        {
            using (SolidBrush titleBrush = new SolidBrush(Theme.TextSecondary))
            using (SolidBrush noteBrush = new SolidBrush(Theme.TextTertiary))
            using (SolidBrush warnBrush = new SolidBrush(WarningText))
            using (SolidBrush cardBrush = new SolidBrush(Theme.Card))
            {
                foreach (Section s in sections)
                {
                    Draw.DrawStringLeft(g, s.Title, Theme.SectionTitleFont, titleBrush, s.LabelRect);
                    if (!string.IsNullOrEmpty(s.Note))
                        Draw.DrawStringLeft(g, s.Note, Theme.CaptionFont, s.NoteWarning ? warnBrush : noteBrush, s.NoteRect);
                    Draw.DrawShadow(g, s.CardRect, Dpi(CardRadius), 0.18f);
                    Draw.FillRoundedRect(g, cardBrush, s.CardRect, Dpi(CardRadius));
                }
            }
        }

        private void PaintRow(Graphics g, int index)
        {
            Row r = rows[index];
            Rectangle b = r.Bounds;

            // 整行悬停/按下高亮：按下后指针移出该行则不再高亮（松开即取消）
            if (r.RowZone >= 0)
            {
                bool pressed = (r.RowZone == pressedZone && r.RowZone == hoverZone);
                if (pressed || (r.RowZone == hoverZone && pressedZone < 0))
                {
                    using (SolidBrush hb = new SolidBrush(pressed ? Theme.RowHover : Theme.CardHover))
                    using (GraphicsPath hp = RowPath(r))
                        g.FillPath(hb, hp);
                }
            }

            int textX = b.Left + Dpi(16) + Dpi(IconTile) + Dpi(14);
            // 行分隔线：从文字起始处画到卡片右侧留白
            if (!r.LastInCard)
            {
                using (Pen sep = new Pen(Theme.Separator, 1))
                    g.DrawLine(sep, textX, b.Bottom - 1, b.Right - Dpi(CardPad), b.Bottom - 1);
            }

            // 图标块：浅色底 + 同色相图标；子项不画，置灰行用灰色。「添加」的加号用线条画，不依赖图标字体
            if (!r.Child)
            {
                Color tint = r.Enabled ? r.Tint : TintGray;
                Rectangle tile = new Rectangle(b.Left + Dpi(16), b.Top + (b.Height - Dpi(IconTile)) / 2, Dpi(IconTile), Dpi(IconTile));
                using (SolidBrush tb = new SolidBrush(Mix(tint, Theme.Card, 0.87f)))
                    Draw.FillRoundedRect(g, tb, tile, Dpi(9));
                if (r.Kind == RowKind.Add) Draw.DrawIcon(g, UiIconKind.Add, tile, tint);
                else Draw.DrawGlyph(g, r.Glyph, Rectangle.Inflate(tile, -Dpi(9), -Dpi(9)), tint);
            }

            int textRight = PaintTrailing(g, r, index);
            int textW = Math.Max(0, textRight - textX);

            // 标题与说明作为一个整体在行内垂直居中；没有说明时标题单独居中
            Color titleColor = !r.Enabled ? Theme.TextTertiary : (r.Kind == RowKind.Add ? Theme.AccentText : Theme.TextPrimary);
            Font titleFont = Theme.FontOf(10F);
            using (SolidBrush titleBrush = new SolidBrush(titleColor))
            using (SolidBrush subBrush = new SolidBrush(r.Warning ? WarningText : Theme.TextSecondary))
            {
                bool isPath = (r.Kind == RowKind.Store || r.Kind == RowKind.Note);
                if (string.IsNullOrEmpty(r.Subtitle))
                {
                    DrawTitle(g, r.Title, titleFont, titleBrush, new Rectangle(textX, b.Top, textW, b.Height), isPath);
                }
                else
                {
                    int titleH = Dpi(22);
                    int subH = Dpi(18);
                    int blockTop = b.Top + (b.Height - titleH - subH) / 2;
                    DrawTitle(g, r.Title, titleFont, titleBrush, new Rectangle(textX, blockTop, textW, titleH), isPath);
                    Draw.DrawStringLeft(g, r.Subtitle, Theme.CaptionFont, subBrush,
                        new Rectangle(textX, blockTop + titleH, textW, subH));
                }
            }
        }

        private static void DrawTitle(Graphics g, string text, Font font, Brush brush, Rectangle rect, bool isPath)
        {
            if (!isPath)
            {
                Draw.DrawStringLeft(g, text, font, brush, rect);
                return;
            }
            if (string.IsNullOrEmpty(text)) return;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.DrawString(text, font, brush, rect, PathFormat);
        }

        /// <summary>画行右侧的开关、箭头或按钮，返回文字可用的右边界。</summary>
        private int PaintTrailing(Graphics g, Row r, int index)
        {
            Rectangle b = r.Bounds;
            int right = b.Right - Dpi(CardPad);
            switch (r.Kind)
            {
                case RowKind.Toggle:
                    Rectangle toggle = ToggleRect(b);
                    ToggleSwitch.PaintSwitch(g, toggle, r.Checked, r.Enabled);
                    return toggle.Left - Dpi(16);
                case RowKind.Navigate:
                    DrawChevron(g, new Rectangle(right - Dpi(12), b.Top + (b.Height - Dpi(12)) / 2, Dpi(12), Dpi(12)),
                        Arrow.Right, Theme.TextTertiary);
                    return right - Dpi(28);
                case RowKind.Button:
                    return PaintRowButton(g, r, right) - Dpi(16);
                case RowKind.Store:
                    Rectangle storeToggle = ToggleRect(b);
                    ToggleSwitch.PaintSwitch(g, storeToggle, r.Checked, true);
                    if (r.MoveUp == null) return storeToggle.Left - Dpi(16);
                    PaintMoveButton(g, index, ZoneKind.MoveUp, MoveRect(b, true), Arrow.Up, r.CanMoveUp);
                    PaintMoveButton(g, index, ZoneKind.MoveDown, MoveRect(b, false), Arrow.Down, r.CanMoveDown);
                    return MoveRect(b, true).Left - Dpi(12);
                default:
                    return right;
            }
        }

        /// <summary>按钮行右侧的圆角按钮（整行可点，按钮跟随行的悬停/按下状态）；返回按钮左边界。</summary>
        private int PaintRowButton(Graphics g, Row r, int right)
        {
            Rectangle b = r.Bounds;
            Font font = Theme.ButtonFont;
            int width = (int)Math.Ceiling(g.MeasureString(r.ButtonText, font).Width) + Dpi(24);
            Rectangle rect = new Rectangle(right - width, b.Top + (b.Height - Dpi(30)) / 2, width, Dpi(30));
            bool hot = r.Enabled && r.RowZone == hoverZone;
            bool pressed = hot && r.RowZone == pressedZone;
            Color fill = !r.Enabled ? Theme.Background : (pressed ? Theme.RowHover : Theme.Card);
            Color border = hot ? Mix(Theme.Accent, Theme.ControlBorder, 0.5f) : Theme.ControlBorder;
            using (SolidBrush fb = new SolidBrush(fill)) Draw.FillRoundedRect(g, fb, rect, Dpi(6));
            using (Pen pen = new Pen(border, 1)) Draw.DrawRoundedRect(g, pen, rect, Dpi(6));
            using (SolidBrush tb = new SolidBrush(r.Enabled ? Theme.TextPrimary : Theme.TextTertiary))
                Draw.DrawStringCenter(g, r.ButtonText, font, tb, rect);
            return rect.Left;
        }

        /// <summary>存储位置行的上移/下移小按钮：悬停时显示浅底，不可用时图标变淡。</summary>
        private void PaintMoveButton(Graphics g, int row, ZoneKind kind, Rectangle rect, Arrow arrow, bool enabled)
        {
            int zone = FindZone(row, kind);
            if (enabled && zone >= 0 && zone == hoverZone && (pressedZone < 0 || pressedZone == zone))
            {
                using (SolidBrush hb = new SolidBrush(pressedZone == zone ? Theme.SegmentBg : Theme.RowHover))
                    Draw.FillRoundedRect(g, hb, rect, Dpi(6));
            }
            DrawChevron(g, Rectangle.Inflate(rect, -Dpi(8), -Dpi(8)), arrow, enabled ? Theme.TextSecondary : Theme.ControlBorder);
        }

        /// <summary>
        /// 在 box 内画线条箭头。排序按钮和导航箭头是功能性图标，用线条绘制而不走图标字体：
        /// 系统缺少图标字体时它们仍然可见（图标块里的装饰图标缺失只会留空）。
        /// </summary>
        private void DrawChevron(Graphics g, Rectangle box, Arrow arrow, Color color)
        {
            float cx = box.X + box.Width / 2F;
            float cy = box.Y + box.Height / 2F;
            float half = box.Width / 2F;      // 开口方向上的半宽
            float depth = box.Width / 4F;     // 箭头尖到开口的一半距离
            PointF[] points;
            if (arrow == Arrow.Right)
                points = new PointF[] { new PointF(cx - depth, cy - half), new PointF(cx + depth, cy), new PointF(cx - depth, cy + half) };
            else
            {
                float tip = arrow == Arrow.Up ? -depth : depth;
                points = new PointF[] { new PointF(cx - half, cy - tip), new PointF(cx, cy + tip), new PointF(cx + half, cy - tip) };
            }
            using (Pen pen = new Pen(color, Dpi(16) / 10F))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                g.DrawLines(pen, points);
            }
        }

        private int FindZone(int row, ZoneKind kind)
        {
            for (int i = 0; i < zones.Count; i++)
                if (zones[i].Row == row && zones[i].Kind == kind) return i;
            return -1;
        }

        /// <summary>行的高亮形状：卡片首行圆上角、末行圆下角、中间行直角，高亮不溢出卡片圆角。</summary>
        private GraphicsPath RowPath(Row r)
        {
            GraphicsPath p = new GraphicsPath();
            Rectangle b = r.Bounds;
            int d = Dpi(CardRadius) * 2;
            if (r.FirstInCard)
            {
                p.AddArc(b.X, b.Y, d, d, 180, 90);
                p.AddArc(b.Right - d, b.Y, d, d, 270, 90);
            }
            else p.AddLine(b.X, b.Y, b.Right, b.Y);
            if (r.LastInCard)
            {
                p.AddArc(b.Right - d, b.Bottom - d, d, d, 0, 90);
                p.AddArc(b.X, b.Bottom - d, d, d, 90, 90);
            }
            else p.AddLine(b.Right, b.Bottom, b.X, b.Bottom);
            p.CloseFigure();
            return p;
        }

        /// <summary>两色线性混合：amount 为 b 所占比例（0~1）。</summary>
        private static Color Mix(Color a, Color b, float amount)
        {
            return Color.FromArgb(
                (int)Math.Round(a.R + (b.R - a.R) * amount),
                (int)Math.Round(a.G + (b.G - a.G) * amount),
                (int)Math.Round(a.B + (b.B - a.B) * amount));
        }

        // ===== 交互 =====

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (scrollDragging)
            {
                Rectangle track = ScrollTrackRect();
                int travel = track.Height - ScrollThumbRect().Height;
                if (travel > 0) SetScroll((int)((long)(e.Y - scrollGrabDy - track.Y) * MaxScroll / travel));
                return;
            }
            bool hot = MaxScroll > 0 && ScrollHitRect().Contains(e.Location);
            int zone = hot ? -1 : ZoneAt(e.Location);
            SetPathTip(hot ? null : PathAt(e.Location));
            if (hot == scrollbarHot && zone == hoverZone) return;
            scrollbarHot = hot;
            hoverZone = zone;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            SetPathTip(null);
            if (hoverZone >= 0 || scrollbarHot)
            {
                hoverZone = -1;
                scrollbarHot = false;
                Invalidate();
            }
            base.OnMouseLeave(e);
        }

        /// <summary>鼠标捕获丢失（弹出对话框、切换窗口等）时收不到 MouseUp：在这里结束滚动条拖动和按下状态。</summary>
        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture) CancelPointerState();
        }

        /// <summary>页面被隐藏（如按 Esc 返回）时清掉交互状态，下次打开不带着上一次的拖动或按下。</summary>
        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!Visible) CancelPointerState();
        }

        private void CancelPointerState()
        {
            SetPathTip(null);
            if (!scrollDragging && pressedZone < 0) return;
            scrollDragging = false;
            pressedZone = -1;
            Invalidate();
        }

        /// <summary>指针所在行如果显示的是存储路径，返回完整路径（界面上过长的路径会省略中间）；否则返回 null。</summary>
        private string PathAt(Point client)
        {
            Rectangle viewport = Viewport;
            if (!viewport.Contains(client)) return null;
            Point p = new Point(client.X, client.Y - viewport.Top + scrollOffset);
            foreach (Row r in rows)
                if ((r.Kind == RowKind.Store || r.Kind == RowKind.Note) && r.Bounds.Contains(p)) return r.Title;
            return null;
        }

        /// <summary>更新悬停提示：只在文本变化时设置，避免鼠标每移动一次就重置提示计时。</summary>
        private void SetPathTip(string text)
        {
            text = text ?? string.Empty;
            if (text == pathTipText) return;
            pathTipText = text;
            pathTip.SetToolTip(this, text);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;

            // 滚动条：命中滑块则抓取拖动；点在轨道空白处则滑块跳到点击处并进入拖动
            if (MaxScroll > 0 && ScrollHitRect().Contains(e.Location))
            {
                Rectangle track = ScrollTrackRect();
                Rectangle thumb = ScrollThumbRect();
                scrollDragging = true;
                if (e.Y >= thumb.Y && e.Y <= thumb.Bottom) scrollGrabDy = e.Y - thumb.Y;
                else
                {
                    scrollGrabDy = thumb.Height / 2;
                    int travel = track.Height - thumb.Height;
                    if (travel > 0) SetScroll((int)((long)(e.Y - scrollGrabDy - track.Y) * MaxScroll / travel));
                }
                Invalidate();
                return;
            }

            hoverZone = ZoneAt(e.Location);
            pressedZone = hoverZone;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            if (scrollDragging)
            {
                scrollDragging = false;
                Invalidate();
                return;
            }
            int zone = pressedZone;
            pressedZone = -1;
            Invalidate();
            if (zone < 0 || zone != ZoneAt(e.Location)) return;   // 按下后移出该区域再松开：视为取消
            Action action = zones[zone].Action;
            if (action != null) action();
        }

        /// <summary>命中可响应点击的区域（客户区坐标，未置灰）；未命中返回 -1。</summary>
        private int ZoneAt(Point client)
        {
            Rectangle viewport = Viewport;
            if (!viewport.Contains(client)) return -1;
            Point p = new Point(client.X, client.Y - viewport.Top + scrollOffset);
            for (int i = 0; i < zones.Count; i++)
                if (zones[i].Enabled && zones[i].Bounds.Contains(p)) return i;
            return -1;
        }

        /// <summary>按指针当前位置取悬停区：内容重建或滚动后没有鼠标移动消息，需要主动重算。</summary>
        private int ZoneUnderCursor()
        {
            if (!IsHandleCreated || !Visible) return -1;
            Point p = PointToClient(Cursor.Position);
            return ClientRectangle.Contains(p) ? ZoneAt(p) : -1;
        }
    }
}

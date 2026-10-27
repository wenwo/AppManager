using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using AppManager.Controls;

namespace AppManager
{
    /// <summary>
    /// 主窗体：无边框圆角 iOS 风格；侧栏 + 双行顶栏(行1 标题+窗口按钮，行2 搜索/视图/整理) + 主内容区(网格/列表)。
    /// 集成扫描、图标加载、分类过滤、自绘右键菜单、快捷中心收纳/还原、配置持久化。
    /// </summary>
    public class MainForm : Form, IMessageFilter
    {
        /// <summary>「快捷中心」分类名：常驻侧栏、可添加/拖放、按规则入库。</summary>
        private const string QuickCenter = "快捷中心";

        private AppConfig config;
        private Scanner scanner;
        private DesktopOrganizer organizer;
        private List<AppInfo> allApps = new List<AppInfo>();

        // 控件
        private SidebarControl sidebar;
        private AppGridControl grid;
        private CollectionSidebar collectionSidebar;   // 资源分类的第二级集合侧边栏（全部 + 自定义集合）
        private CollectionInfoPanel collectionInfo;    // 选中集合后的信息页（封面/名称/简介/进入）
        private string currentCollection;   // 当前选中的集合键（col:父分类/集合名）；空 = 「全部」
        private bool collectionEntered;     // 是否已进入集合内容（false = 停留集合信息页）
        private SearchField search;
        private System.Windows.Forms.Timer searchDebounce;   // 搜索输入防抖计时器
        private ToggleSwitch viewToggle;
        private Label toggleLabel;
        private RoundButton organizeBtn;
        private RoundButton addBtn;
        private IconButton settingsBtn;   // 标题行：进入设置页
        private IconButton minBtn;
        private IconButton maxBtn;
        private IconButton closeBtn;
        private SettingsPage settingsPage; // 设置页（独立页面，覆盖主内容区）
        private bool settingsOpen;         // 是否处于设置页
        private ImageViewerControl imageViewer;  // 图片查看器：双击照片后替换列表区域展示大图
        private VideoPlayerControl videoPlayer;  // 视频播放器：双击视频后替换列表区域播放
        private NotifyIcon tray;          // 系统托盘图标（常驻）
        private bool exiting;             // 真正退出标志：为真时关闭不再拦截为「最小化到托盘」
        private bool trayTipShown;        // 首次最小化到托盘的气泡提示只弹一次
        private Label countLabel;
        private Label loadingLabel;
        // 底部状态栏：显示当前处理进度（如搬运文件）
        private Panel statusBar;
        private Label statusLabel;
        private IosProgressBar statusProgress;
        private Label statusDetail;   // 每项独立进度详情

        // 自动收录：开始菜单 / 桌面有变化时静默重扫，并把新出现的桌面快捷方式收进快捷中心
        private AutoScanWatcher autoScan;

        // 扫描串行化：正在扫描时再次请求只登记一轮补扫，防止多线程同时回写列表
        private bool scanRunning;
        private bool scanPending;

        // 状态
        private string currentCategory = "常用";
        private SidebarControl.ContentMode contentMode = SidebarControl.ContentMode.Software;
        // 软件模式下上次选中的分类：切到资源模式再切回时恢复，避免丢失浏览位置
        private string lastSoftwareCategory = "常用";
        private bool _ready = false;
        private int categoryTotal = 0;
        private int iconGen = 0;        // 图标加载代号：换列表即自增，使旧加载任务自动失效
        private int refreshPending = 0; // 图标重绘合并标志，0=无待处理请求

        // 当前网格展示的条目顺序（拖拽排序的落盘依据）＋ 侧栏条目快照（分类重排的下标映射）
        private List<AppInfo> shownApps = new List<AppInfo>();
        private List<SidebarControl.SidebarItem> sidebarItems;

        // 拖动窗体
        [DllImport("user32.dll")]
        private static extern int SendMessage(IntPtr h, int msg, IntPtr wp, IntPtr lp);
        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();
        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HTCAPTION = 2;

        // 无边框窗体默认不含「最大化/最小化框」样式，缺少 WS_MINIMIZEBOX 时点击任务栏图标无法最小化
        private const int WS_MINIMIZEBOX = 0x00020000;
        private const int WM_SYSCOMMAND = 0x0112;
        private const int SC_MINIMIZE = 0xF020;
        private const int WM_MOUSEWHEEL = 0x020A;

        // Windows 11 系统圆角（边缘平滑，避免 Region 锯齿）
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("ntdll.dll")]
        private static extern int RtlGetVersion(ref OSVERSIONINFOEX info);
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OSVERSIONINFOEX
        {
            public int dwOSVersionInfoSize;
            public int dwMajorVersion;
            public int dwMinorVersion;
            public int dwBuildNumber;
            public int dwPlatformId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szCSDVersion;
            public ushort wServicePackMajor;
            public ushort wServicePackMinor;
            public ushort wSuiteMask;
            public byte wProductType;
            public byte wReserved;
        }

        public MainForm(AppConfig cfg)
        {
            config = cfg;
            scanner = new Scanner(cfg);
            organizer = new DesktopOrganizer(cfg);

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1080, 680);
            BackColor = Theme.Background;
            DoubleBuffered = true;
            Font = Theme.FontOf(9F);
            Text = "应用中心";
            AllowDrop = true;   // 窗体空白区（顶栏等）也接受外部拖入
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Application.AddMessageFilter(this);   // 按光标位置把滚轮消息路由到侧栏
            BuildControls();
            LayoutControls();
            BuildTray();
            StartAutoScan();
            _ready = true;
            MinimumSize = new Size(960, 560);
            UpdateRegion();
            RebuildSidebar();
            if (config.AutoScan)
            {
                StartScan();
            }
            else
            {
                SwapApps(scanner.ScanAll());
                RefreshGrid();
                StartIconLoad();
            }
        }

        // ===== 任务栏最小化 / 关闭行为 / 系统托盘 =====

        /// <summary>补上最小化框样式：无边框窗体默认缺失 WS_MINIMIZEBOX，导致点击任务栏图标无法最小化收起。</summary>
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.Style |= WS_MINIMIZEBOX;
                return cp;
            }
        }

        /// <summary>拦截系统最小化命令，兜底保证任务栏点击等入口都能收起为最小化。</summary>
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_SYSCOMMAND && (m.WParam.ToInt32() & 0xFFF0) == SC_MINIMIZE)
            {
                WindowState = FormWindowState.Minimized;
                return;
            }
            base.WndProc(ref m);
        }

        /// <summary>
        /// 最大化/还原切换：无边框窗体按屏幕工作区最大化，避免遮住任务栏。
        /// </summary>
        private void ToggleMaximize()
        {
            if (WindowState == FormWindowState.Maximized)
            {
                WindowState = FormWindowState.Normal;
            }
            else
            {
                MaximizedBounds = Screen.FromControl(this).WorkingArea;
                WindowState = FormWindowState.Maximized;
            }
        }

        /// <summary>点「×」：按设置彻底退出或最小化到系统托盘。</summary>
        private void OnCloseRequested()
        {
            if (config.CloseToTray)
            {
                HideToTray();
            }
            else
            {
                ExitApp();
            }
        }

        /// <summary>关闭兜底：用户关闭且配置为托盘时拦截为隐藏（如任务栏右键「关闭窗口」）。</summary>
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (!exiting && config.CloseToTray && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray();
            }
        }

        /// <summary>窗体真正关闭时释放托盘资源，避免残留图标。</summary>
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            StopAutoScan();
            Application.RemoveMessageFilter(this);
            if (searchDebounce != null)
            {
                searchDebounce.Stop();
                searchDebounce.Dispose();
                searchDebounce = null;
            }
            base.OnFormClosed(e);
            if (tray != null)
            {
                tray.Visible = false;
                tray.Dispose();
                tray = null;
            }
        }

        // ===== 滚轮路由：光标悬停侧栏时滚动分类列表，否则交默认处理（主内容区滚动）=====

        /// <summary>
        /// 系统把 WM_MOUSEWHEEL 发给焦点控件（通常是主内容区），导致光标悬停侧栏时无法滚动。
        /// 此过滤器先于派发按光标位置判定：命中侧栏则滚动侧栏并吞掉消息，否则放行。
        /// </summary>
        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WM_MOUSEWHEEL) return false;
            long lp = m.LParam.ToInt64();
            Point screen = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
            int delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
            bool sidebarVisible = sidebar != null && sidebar.Visible;
            bool colVisible = collectionSidebar != null && collectionSidebar.Visible;
            if (!sidebarVisible && !colVisible) return false;
            // 集合侧边栏优先：光标悬停其上时滚动集合列表
            if (colVisible && collectionSidebar.RectangleToScreen(collectionSidebar.ClientRectangle).Contains(screen))
            {
                collectionSidebar.ScrollByWheel(delta);
                return true;   // 已消费：避免主内容区同时滚动
            }
            if (!sidebarVisible) return false;
            if (!sidebar.RectangleToScreen(sidebar.ClientRectangle).Contains(screen)) return false;
            sidebar.ScrollByWheel(delta);
            return true;
        }

        /// <summary>构建常驻系统托盘图标；右键弹出 iOS 风格菜单（打开/设置/退出），双击图标恢复窗口。</summary>
        private void BuildTray()
        {
            tray = new NotifyIcon();
            tray.Text = "应用中心";
            Icon ico = ResolveAppIcon();
            if (ico != null) tray.Icon = ico;
            tray.Visible = true;
            tray.DoubleClick += delegate { ShowFromTray(); };
            tray.MouseUp += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Right) ShowTrayMenu();
            };
        }

        /// <summary>托盘右键菜单：复用应用内 iOS 风格自绘菜单，自托盘图标向右、向上展开，越界时向屏幕内侧翻转。</summary>
        private void ShowTrayMenu()
        {
            List<MenuWindow.MenuItem> items = new List<MenuWindow.MenuItem>();
            items.Add(MkMenu("打开应用中心", delegate { ShowFromTray(); }));
            items.Add(MkMenu("设置", delegate { ShowFromTray(); OpenSettings(); }));
            items.Add(MkSep());
            items.Add(MkMenu("退出", delegate { ExitApp(); }));

            MenuWindow mw = new MenuWindow(items, 160);
            Point p = Cursor.Position;
            Rectangle wa = Screen.GetWorkingArea(p);
            int x = p.X + 8;                                         // 首选自托盘图标向右展开
            if (x + mw.Width > wa.Right - 8) x = p.X - 8 - mw.Width; // 右侧放不下翻到图标左侧
            if (x < wa.Left + 8) x = wa.Left + 8;
            int y = p.Y - mw.Height - 8;                             // 首选底边悬于光标上方，自图标向上展开
            if (y < wa.Top + 8) y = p.Y + 8;                         // 上方放不下翻到图标下方
            if (y + mw.Height > wa.Bottom - 8) y = wa.Bottom - 8 - mw.Height;
            mw.Location = new Point(x, y);
            // 托盘菜单锚定的是系统托盘而非应用窗口：置顶显示（与原生托盘菜单一致），避免被任务栏/前台窗口遮挡
            mw.TopMost = true;
            // 窗口隐藏到托盘时没有可归属的宿主，独立显示并置前
            if (Visible && WindowState != FormWindowState.Minimized) mw.Show(this);
            else { mw.Show(); mw.Activate(); }
        }

        /// <summary>解析托盘图标：优先取 exe 关联图标（编译期已嵌入），失败则回退系统默认程序图标。</summary>
        private static Icon ResolveAppIcon()
        {
            try
            {
                Icon ico = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (ico != null) return ico;
            }
            catch { }
            return SystemIcons.Application;
        }

        /// <summary>隐藏窗口到托盘（任务栏按钮随之消失），首次提示一次。</summary>
        private void HideToTray()
        {
            HideVideoPlayer();   // 窗体隐藏不触发子控件 VisibleChanged，先收起播放器，避免托盘后台继续出声
            Hide();
            if (!trayTipShown && tray != null)
            {
                trayTipShown = true;
                tray.ShowBalloonTip(3000, "应用中心", "已最小化到托盘，双击图标可重新打开。", ToolTipIcon.Info);
            }
        }

        /// <summary>从托盘恢复窗口并置前。</summary>
        private void ShowFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }

        /// <summary>彻底退出：置标志后关闭窗体，由 OnFormClosed 释放托盘资源。</summary>
        private void ExitApp()
        {
            exiting = true;
            if (tray != null) tray.Visible = false;
            Close();
        }

        /// <summary>设置关闭主窗口行为并落盘。</summary>
        private void SetCloseToTray(bool toTray)
        {
            config.CloseToTray = toTray;
            config.Save();
        }

        // ===== 窗体空白区（顶栏等）接受外部文件/文件夹拖入 =====

        protected override void OnDragEnter(DragEventArgs e)
        {
            base.OnDragEnter(e);
            e.Effect = AcceptFormDrop(e) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        protected override void OnDragOver(DragEventArgs e)
        {
            base.OnDragOver(e);
            e.Effect = AcceptFormDrop(e) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        protected override void OnDragDrop(DragEventArgs e)
        {
            base.OnDragDrop(e);
            if (!AcceptFormDrop(e)) return;
            string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return;
            OnGridFilesDropped(files);
        }

        /// <summary>窗体是否放行拖入：当前分类可编辑且携带文件拖放数据；设置页打开时不接收。</summary>
        private bool AcceptFormDrop(DragEventArgs e)
        {
            return !settingsOpen && CanEditCategory(currentCategory) && e.Data != null
                && e.Data.GetDataPresent(DataFormats.FileDrop);
        }

        private void BuildControls()
        {
            sidebar = new SidebarControl();
            sidebar.BackColor = Color.Transparent;   // 透出顶栏渐变与整体背景
            sidebar.SelectedChanged += OnSidebarSelect;
            sidebar.FilesDropped += OnFilesDroppedToCategory;
            sidebar.ModeChanged += OnModeChanged;
            sidebar.AddCategoryRequested += OnAddCategory;
            sidebar.CategoryContextRequested += OnCategoryContext;
            sidebar.ReorderRequested += OnSidebarReorder;
            // 侧栏空白区可拖动窗体
            sidebar.MouseDown += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left && sidebar.IsBlankArea(e.Location))
                {
                    ReleaseCapture();
                    SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
                }
            };
            Controls.Add(sidebar);

            grid = new AppGridControl();
            grid.LaunchRequested += OnLaunch;
            grid.MenuRequested += OnContextMenu;
            grid.RenameRequested += RenameAddedEntry;
            grid.DeleteRequested += OnGridRowRemove;   // 集合内=移除（仅移出该集合）；分类/全部资源=删除
            grid.CategoryTextProvider = GetCategoryText;
            grid.ReorderRequested += OnGridReorder;
            grid.DragMoved += OnGridDragMoved;
            grid.DroppedOutside += OnGridDroppedOutside;
            grid.FilesDropped += OnGridFilesDropped;
            grid.MoveIntoFolder += OnGridMoveIntoFolder;
            grid.BlankRightClicked += OnGridBlankRightClicked;
            Controls.Add(grid);

            // 集合侧边栏：资源分类的第二级内嵌栏（全部 + 自定义集合 + 新建），单击选中/双击进入/右键编辑删除
            collectionSidebar = new CollectionSidebar();
            collectionSidebar.Visible = false;
            collectionSidebar.ItemSelected += OnCollectionSelected;
            collectionSidebar.ItemActivated += EnterCollection;
            collectionSidebar.ContextRequested += ShowCollectionMenu;
            collectionSidebar.CreateRequested += delegate { OnCreateCollection(null); };
            collectionSidebar.FilesDropped += OnCollectionFilesDropped;   // 外部文件拖到集合行 = 加入该集合
            // 集合侧边栏空白区可拖动窗体（行/底部按钮/滚动条除外）
            collectionSidebar.MouseDown += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left && collectionSidebar.IsBlankArea(e.Location))
                {
                    ReleaseCapture();
                    SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
                }
            };
            Controls.Add(collectionSidebar);

            // 集合信息页：选中自定义集合后替换列表区域展示，点「进入」切换为集合内容
            collectionInfo = new CollectionInfoPanel();
            collectionInfo.EnterRequested += delegate { if (currentCollection != null) EnterCollection(currentCollection); };
            collectionInfo.EditSaveRequested += OnCollectionEditSave;     // 页内编辑保存（名称/简介/封面）
            collectionInfo.CoverPickRequested += OnCollectionPickCover;   // 页内编辑更换封面
            collectionInfo.CoverAutoRequested += OnCollectionAutoCover;   // 页内编辑恢复自动封面
            Controls.Add(collectionInfo);

            // 图片查看器：默认隐藏，双击照片时替换列表区域（尺寸/位置与 grid 一致）
            imageViewer = new ImageViewerControl();
            imageViewer.CloseRequested += HideImageViewer;
            Controls.Add(imageViewer);

            // 视频播放器：默认隐藏，双击视频时替换列表区域（尺寸/位置与 grid 一致）
            videoPlayer = new VideoPlayerControl();
            videoPlayer.CloseRequested += OnVideoCloseRequested;     // 全屏时先退全屏再退播放器
            videoPlayer.FullscreenRequested += ToggleVideoFullscreen;
            Controls.Add(videoPlayer);

            search = new SearchField();
            // 输入防抖：连续按键期间不反复全量刷新（RefreshGrid 遍历全集较重），停顿 250ms 后刷新一次；
            // 清空搜索时立即刷新，保证 Esc/删空即时恢复全量列表
            searchDebounce = new System.Windows.Forms.Timer { Interval = 250 };
            searchDebounce.Tick += delegate
            {
                searchDebounce.Stop();
                RefreshGrid();
            };
            search.TextChanged += delegate
            {
                if (string.IsNullOrEmpty(search.Text))
                {
                    searchDebounce.Stop();
                    RefreshGrid();
                }
                else
                {
                    searchDebounce.Stop();
                    searchDebounce.Start();
                }
            };
            Controls.Add(search);

            countLabel = new Label();
            countLabel.AutoSize = false;
            countLabel.BackColor = Color.Transparent;
            countLabel.Font = Theme.FontOf(9F);
            countLabel.ForeColor = Theme.TextSecondary;
            countLabel.TextAlign = ContentAlignment.MiddleRight;
            Controls.Add(countLabel);

            viewToggle = new ToggleSwitch();
            viewToggle.Checked = GetCategoryGridMode(currentCategory);
            viewToggle.CheckedChanged += delegate
            {
                SetCategoryGridMode(currentCategory, viewToggle.Checked);
                config.Save();
                grid.GridMode = viewToggle.Checked;
                UpdateToggleLabel();
            };
            Controls.Add(viewToggle);

            toggleLabel = new Label();
            toggleLabel.Text = "网格";
            toggleLabel.AutoSize = true;
            toggleLabel.BackColor = Color.Transparent;
            toggleLabel.Font = Theme.FontOf(9F);
            toggleLabel.ForeColor = Theme.TextSecondary;
            toggleLabel.Click += delegate { viewToggle.Checked = !viewToggle.Checked; };
            Controls.Add(toggleLabel);

            organizeBtn = new RoundButton();
            organizeBtn.Text = "整理桌面";
            organizeBtn.Accent = false;
            organizeBtn.Size = new Size(80, 32);
            organizeBtn.Click += OnOrganize;
            Controls.Add(organizeBtn);

            addBtn = new RoundButton();
            addBtn.Text = "+ 添加";
            addBtn.Accent = false;
            addBtn.Size = new Size(72, 32);
            addBtn.Visible = false;   // 仅在内置/自定义分类显示
            addBtn.Click += OnAdd;
            Controls.Add(addBtn);

            // 标题行窗口按钮：无边框图标按钮，依次为 设置 / 最小化 / 最大化 / 关闭
            settingsBtn = new IconButton();
            settingsBtn.Kind = IconKind.Settings;
            settingsBtn.Click += delegate { OpenSettings(); };
            Controls.Add(settingsBtn);

            minBtn = new IconButton();
            minBtn.Kind = IconKind.Minimize;
            minBtn.Click += delegate { WindowState = FormWindowState.Minimized; };
            Controls.Add(minBtn);

            maxBtn = new IconButton();
            maxBtn.Kind = IconKind.Maximize;
            maxBtn.Click += delegate { ToggleMaximize(); };
            Controls.Add(maxBtn);

            closeBtn = new IconButton();
            closeBtn.Kind = IconKind.Close;
            closeBtn.Click += delegate { OnCloseRequested(); };
            Controls.Add(closeBtn);

            settingsPage = new SettingsPage();
            settingsPage.BackRequested += delegate { CloseSettings(); };
            settingsPage.ShowHiddenChanged += delegate
            {
                config.ShowHidden = settingsPage.ShowHidden;
                config.Save();
                RefreshGrid();
            };
            settingsPage.ShowAllInstalledChanged += delegate
            {
                config.ShowAllInstalled = settingsPage.ShowAllInstalled;
                config.Save();
                RefreshGrid();
            };
            settingsPage.AutoScanChanged += delegate
            {
                config.AutoScan = settingsPage.AutoScan;
                config.Save();
                if (config.AutoScan) StartAutoScan();
                else StopAutoScan();
            };
            settingsPage.CloseToTrayChanged += delegate { SetCloseToTray(settingsPage.CloseToTray); };
            settingsPage.StorageLocationRequested += delegate
            {
                ShowStorageLocationDialog();
                // 对话框可能改了存储目录，回到页面后刷新展示的路径
                settingsPage.Bind(config.ShowHidden, config.ShowAllInstalled, config.AutoScan,
                    config.CloseToTray, FolderStoreOf(), FileStoreOf(), ResourceStoreRoot());
            };
            settingsPage.RescanRequested += delegate { StartScan(); };
            settingsPage.LayoutBackupRequested += delegate { ShowLayoutBackups(); };
            settingsPage.SelfTestRequested += delegate
            {
                ThreadPool.QueueUserWorkItem(delegate { SelfTest.Run(); });
            };
            Controls.Add(settingsPage);
            settingsPage.Visible = false;   // 默认隐藏，仅点「设置」后显示

            loadingLabel = new Label();
            loadingLabel.AutoSize = false;
            loadingLabel.BackColor = Theme.Background;
            loadingLabel.Font = Theme.FontOf(11F);
            loadingLabel.ForeColor = Theme.TextSecondary;
            loadingLabel.TextAlign = ContentAlignment.MiddleCenter;
            loadingLabel.Text = "正在扫描应用…";
            loadingLabel.Visible = false;
            Controls.Add(loadingLabel);

            // 底部状态栏（默认隐藏，搬运/处理时显示）
            statusBar = new Panel();
            statusBar.BackColor = Theme.StatusBarBg;
            statusBar.Visible = false;
            Controls.Add(statusBar);
            statusLabel = new Label();
            statusLabel.AutoSize = false;
            statusLabel.BackColor = Color.Transparent;
            statusLabel.Font = Theme.FontOf(9F);
            statusLabel.ForeColor = Theme.TextPrimary;
            statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            statusBar.Controls.Add(statusLabel);
            statusProgress = new IosProgressBar();
            statusBar.Controls.Add(statusProgress);
            statusDetail = new Label();
            statusDetail.AutoSize = false;
            statusDetail.BackColor = Color.Transparent;
            statusDetail.Font = Theme.FontOf(8.5F);
            statusDetail.ForeColor = Theme.TextSecondary;
            statusDetail.TextAlign = ContentAlignment.MiddleLeft;
            statusBar.Controls.Add(statusDetail);

            grid.GridMode = GetCategoryGridMode(currentCategory);
            grid.BringToFront();

            // 让无边框窗口可被拖动（顶栏/侧栏任意非交互区）
            MakeDraggable(this);
        }

        private void LayoutControls()
        {
            int top = Theme.TopBarHeight;
            int row1 = Theme.TitleRowHeight;    // 行1：标题 + 窗口按钮
            int row2 = Theme.ToolbarRowHeight;  // 行2：搜索/计数 + 视图切换 + 操作按钮
            int sb = Theme.SidebarWidth;
            int statusH = 34;

            sidebar.Location = new Point(0, 0);
            sidebar.Size = new Size(sb, ClientSize.Height);

            // 行1（标题行）：标题由 OnPaint 绘制；图标按钮贴右上角，等宽无间隔
            int rx = ClientSize.Width - 16;
            closeBtn.Location = new Point(rx - closeBtn.Width, (row1 - closeBtn.Height) / 2);
            maxBtn.Location = new Point(closeBtn.Left - maxBtn.Width, (row1 - maxBtn.Height) / 2);
            minBtn.Location = new Point(maxBtn.Left - minBtn.Width, (row1 - minBtn.Height) / 2);
            settingsBtn.Location = new Point(minBtn.Left - settingsBtn.Width, (row1 - settingsBtn.Height) / 2);
            // 最大化/还原图标随窗口状态切换
            maxBtn.Kind = WindowState == FormWindowState.Maximized ? IconKind.Restore : IconKind.Maximize;

            // 行2（工具行）：搜索靠左；操作按钮自右缘向左排布，与行1右缘对齐
            search.Location = new Point(sb + 16, row1 + (row2 - 34) / 2);
            search.Size = new Size(240, 34);

            // 先更新切换文案，再依其实际宽度定位，避免文案切换后宽度变化导致错位
            UpdateToggleLabel();
            organizeBtn.Location = new Point(rx - organizeBtn.Width, row1 + (row2 - organizeBtn.Height) / 2);
            addBtn.Location = new Point(organizeBtn.Left - addBtn.Width - 10, row1 + (row2 - addBtn.Height) / 2);
            int clusterLeft = addBtn.Visible ? addBtn.Left : organizeBtn.Left;
            toggleLabel.Location = new Point(clusterLeft - toggleLabel.PreferredWidth - 10, row1 + (row2 - toggleLabel.Height) / 2);
            viewToggle.Location = new Point(toggleLabel.Left - viewToggle.Width - 6, row1 + (row2 - viewToggle.Height) / 2);

            // 计数标签：夹在搜索框与「网格/列表」切换簇之间，宽度按剩余空间收敛。
            // 双行后行2空间充裕，最小窗口宽度下搜索框与右侧簇也不再重叠。
            int countLeft = search.Right + 12;
            int countRight = viewToggle.Left - 12;
            countLabel.Location = new Point(countLeft, row1);
            countLabel.Size = new Size(Math.Max(0, countRight - countLeft), row2);

            int gridHeight = ClientSize.Height - top - (statusBar.Visible ? statusH : 0);
            // 集合侧边栏可见时占据主侧栏与内容区之间，网格与各查看器按该宽度让位
            int colW = 0;
            if (collectionSidebar != null)
            {
                colW = collectionSidebar.Visible ? CollectionSidebar.PreferredWidth : 0;
                collectionSidebar.Location = new Point(sb, top);
                collectionSidebar.Size = new Size(colW, gridHeight);
            }
            int contentX = sb + colW;
            int contentW = ClientSize.Width - contentX;
            grid.Location = new Point(contentX, top);
            grid.Size = new Size(contentW, gridHeight);
            imageViewer.Location = grid.Location;
            imageViewer.Size = grid.Size;
            videoPlayer.Location = grid.Location;
            videoPlayer.Size = grid.Size;
            collectionInfo.Location = grid.Location;
            collectionInfo.Size = grid.Size;

            loadingLabel.Location = new Point(contentX, top);
            loadingLabel.Size = new Size(contentW, gridHeight);

            // 设置页：铺满标题行以下的全部区域（含侧栏与工具行），隐藏时位置保持有效
            settingsPage.Location = new Point(0, Theme.TitleRowHeight);
            settingsPage.Size = new Size(ClientSize.Width, Math.Max(0, ClientSize.Height - Theme.TitleRowHeight));

            // 底部状态栏
            statusBar.Location = new Point(sb, ClientSize.Height - statusH);
            statusBar.Size = new Size(ClientSize.Width - sb, statusH);
            statusLabel.Location = new Point(12, 0);
            statusLabel.Size = new Size(140, statusH);
            statusProgress.Location = new Point(160, 8);
            statusProgress.Size = new Size(180, statusH - 16);
            statusDetail.Location = new Point(350, 0);
            statusDetail.Size = new Size(statusBar.Width - 360, statusH);

            // 播放器全屏：视频区铺满客户区（置顶覆盖侧栏与顶栏），其余控件位置不动
            if (playerFullscreen && videoPlayer != null)
            {
                videoPlayer.Location = new Point(0, 0);
                videoPlayer.Size = ClientSize;
            }
        }

        private void UpdateToggleLabel()
        {
            toggleLabel.Text = viewToggle.Checked ? "网格" : "列表";
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (!_ready) return;
            LayoutControls();
            UpdateRegion();
            grid.Invalidate();
        }

        private void UpdateRegion()
        {
            // 最大化时窗口与屏幕边缘贴合；Region 回退路径下需取消圆角，避免四角留缺口
            if (WindowState == FormWindowState.Maximized && !IsWindows11OrGreater())
            {
                Region = null;
                return;
            }
            ApplyRoundedCorners(this);
        }

        /// <summary>
        /// 应用窗口圆角：Windows 11 交由 DWM 做系统圆角（边缘平滑无锯齿）；
        /// 旧系统回退到 Region 圆角（轻微锯齿，但保证圆角形状）。
        /// internal 供模态遮罩（ConfirmDialog.ShowModal）复用，保持遮罩与宿主同圆角。
        /// </summary>
        internal static void ApplyRoundedCorners(Form f)
        {
            try
            {
                if (IsWindows11OrGreater())
                {
                    int pref = DWMWCP_ROUND;
                    if (DwmSetWindowAttribute(f.Handle, DWMWA_WINDOW_CORNER_PREFERENCE,
                            ref pref, sizeof(int)) == 0)
                    {
                        f.Region = null;
                        return;
                    }
                }
            }
            catch { }
            try { f.Region = new Region(Draw.RoundedPath(f.ClientRectangle, 12)); } catch { }
        }

        /// <summary>通过 RtlGetVersion 判断是否为 Windows 11（Build ≥ 22000）。</summary>
        private static bool IsWindows11OrGreater()
        {
            try
            {
                OSVERSIONINFOEX info = new OSVERSIONINFOEX();
                info.dwOSVersionInfoSize = Marshal.SizeOf(typeof(OSVERSIONINFOEX));
                if (RtlGetVersion(ref info) != 0) return false;
                return info.dwMajorVersion >= 10 && info.dwBuildNumber >= 22000;
            }
            catch { return false; }
        }

        // ===== 侧栏 =====

        private void RebuildSidebar()
        {
            List<SidebarControl.SidebarItem> list = new List<SidebarControl.SidebarItem>();
            if (contentMode == SidebarControl.ContentMode.Website)
            {
                // 资源模式：自定义资源分类在前、内置资源分类在后（与软件侧一致，以分隔线分组），底部「全部资源」
                // 两者均为资源条目投放目标
                for (int i = 0; i < config.ResourceCategories.Count; i++)
                {
                    string c = config.ResourceCategories[i];
                    list.Add(MkItem(c, c, true));
                }
                for (int i = 0; i < CategoryRules.ResourceBuiltIn.Length; i++)
                {
                    string c = CategoryRules.ResourceBuiltIn[i];
                    if (i == 0 && config.ResourceCategories.Count > 0) list.Add(Sep());
                    list.Add(MkItem(c, c, true));
                }
                list.Add(Sep());
                list.Add(MkItem("全部资源", "全部资源"));
            }
            else
            {
                list.Add(MkItem("常用", "常用"));
                // 「快捷中心」常驻显示（为空也在），并作为文件/文件夹的投放目标
                list.Add(MkItem(QuickCenter, QuickCenter, true));
                list.Add(Sep());
                // 中间区块（可拖动排序）：自定义分类在前、内置分类在后，中间以分隔线断开
                List<string> mid = OrderedMiddleCategories();
                int customCount = 0;
                for (int i = 0; i < mid.Count; i++)
                    if (config.CustomCategories.Contains(mid[i])) customCount++;
                for (int i = 0; i < mid.Count; i++)
                {
                    if (i == customCount && customCount > 0 && customCount < mid.Count)
                        list.Add(Sep());
                    // 中间区块均为可编辑分类：既是拖拽排序对象，也是文件/目录投放目标
                    list.Add(MkItem(mid[i], mid[i], true, true, i < customCount ? 1 : 2));
                }
                list.Add(Sep());
                // 只读全集分类：全部软件 / 全部文件（资源条目统一在资源模式「全部资源」查看）
                list.Add(MkItem("全部软件", "全部软件"));
                list.Add(MkItem("全部文件", "全部文件"));
            }
            sidebarItems = list;
            sidebar.SetItems(list);
            sidebar.SetActive(currentCategory);
        }

        /// <summary>
        /// 中间区块分类顺序：自定义分类在前、内置分类在后；两组分别按 config.SidebarOrder 记录排序，缺失项按各自默认顺序补齐。
        /// </summary>
        private List<string> OrderedMiddleCategories()
        {
            List<string> custom = new List<string>();
            for (int i = 0; i < config.CustomCategories.Count; i++)
                if (!custom.Contains(config.CustomCategories[i])) custom.Add(config.CustomCategories[i]);

            List<string> builtIn = new List<string>();
            for (int i = 0; i < CategoryRules.BuiltIn.Length; i++)
                if (!custom.Contains(CategoryRules.BuiltIn[i])) builtIn.Add(CategoryRules.BuiltIn[i]);

            List<string> result = new List<string>();
            AppendOrderedCategories(result, custom);
            AppendOrderedCategories(result, builtIn);
            return result;
        }

        private void AppendOrderedCategories(List<string> result, List<string> defaults)
        {
            for (int i = 0; i < config.SidebarOrder.Count; i++)
            {
                string name = config.SidebarOrder[i];
                if (defaults.Contains(name) && !result.Contains(name)) result.Add(name);
            }
            for (int i = 0; i < defaults.Count; i++)
                if (!result.Contains(defaults[i])) result.Add(defaults[i]);
        }

        /// <summary>侧栏分类拖动排序：把新的中间区块顺序写入 config.SidebarOrder 并重建（按分组换算，分隔线不影响结果）。</summary>
        private void OnSidebarReorder(int from, int to)
        {
            if (sidebarItems == null || from < 0 || from >= sidebarItems.Count || to < 0 || to >= sidebarItems.Count) return;
            if (!sidebarItems[from].Orderable || !sidebarItems[to].Orderable
                || sidebarItems[from].OrderGroup != sidebarItems[to].OrderGroup) return;

            // 源项所在分组在 items 中的起始下标（同组内条目在 items 中连续，组内不含分隔线）
            int group = sidebarItems[from].OrderGroup;
            int groupStart = from;
            while (groupStart - 1 >= 0 && sidebarItems[groupStart - 1].Orderable
                && sidebarItems[groupStart - 1].OrderGroup == group) groupStart--;
            int groupStartMid = CountOrderableBefore(groupStart);

            int fromMid = groupStartMid + (from - groupStart);
            int toMid = groupStartMid + (to - groupStart);

            List<string> order = OrderedMiddleCategories();
            if (fromMid < 0 || fromMid >= order.Count || fromMid == toMid) return;
            if (toMid < 0 || toMid >= order.Count) return;
            string moved = order[fromMid];
            order.RemoveAt(fromMid);
            order.Insert(toMid, moved);
            config.SidebarOrder = order;
            config.Save();
            RebuildSidebar();
        }

        /// <summary>统计 items 中下标 &lt; itemIndex 的可排序条目数量（即对应的 mid 下标）。</summary>
        private int CountOrderableBefore(int itemIndex)
        {
            int n = 0;
            for (int i = 0; i < itemIndex && i < sidebarItems.Count; i++)
                if (sidebarItems[i].Orderable) n++;
            return n;
        }

        private static SidebarControl.SidebarItem MkItem(string text, string key)
        {
            return MkItem(text, key, false);
        }

        private static SidebarControl.SidebarItem MkItem(string text, string key, bool dropTarget)
        {
            return MkItem(text, key, dropTarget, false);
        }

        private static SidebarControl.SidebarItem MkItem(string text, string key, bool dropTarget, bool orderable)
        {
            return MkItem(text, key, dropTarget, orderable, 0);
        }

        private static SidebarControl.SidebarItem MkItem(string text, string key, bool dropTarget, bool orderable, int orderGroup)
        {
            SidebarControl.SidebarItem it = new SidebarControl.SidebarItem();
            it.Text = text; it.Key = key; it.DropTarget = dropTarget;
            it.Orderable = orderable; it.OrderGroup = orderGroup;
            return it;
        }
        private static SidebarControl.SidebarItem Sep()
        {
            SidebarControl.SidebarItem it = new SidebarControl.SidebarItem();
            it.Separator = true;
            return it;
        }

        private void OnSidebarSelect(string key)
        {
            currentCategory = key;
            currentCollection = null;   // 切换分类即退出集合，回到「全部」
            collectionEntered = true;   // 「全部」直接展示分类网格
            sidebar.SetActive(key);
            // 每分类独立视图模式：切换分类时恢复该分类上次选择的网格/列表
            bool gm = GetCategoryGridMode(key);
            viewToggle.Checked = gm;   // 触发 CheckedChanged 自动落盘与应用
            grid.GridMode = gm;
            addBtn.Visible = CanEditCategory(key);
            SyncCollectionSidebarVisibility();   // 集合侧边栏可见性先定，布局才能按宽度让位
            LayoutControls();
            RefreshGrid();
        }

        /// <summary>
        /// 内容模式切换（软件/资源）：重建侧栏分类并恢复各自上次选中的分类。
        /// 资源模式同样提供网格/列表开关，偏好按分类独立记忆。
        /// </summary>
        private void OnModeChanged(SidebarControl.ContentMode m)
        {
            contentMode = m;
            bool website = m == SidebarControl.ContentMode.Website;
            grid.ShowRowActions = website;
            grid.ShowTimeColumn = website;   // 资源列表额外显示创建时间列
            RebuildSidebar();
            if (website)
            {
                lastSoftwareCategory = currentCategory;
                OnSidebarSelect("全部资源");
                return;
            }
            // 切回软件模式：恢复上次选中的软件分类，已不存在时回退「常用」
            bool exists = false;
            for (int i = 0; i < sidebarItems.Count; i++)
                if (!sidebarItems[i].Separator && sidebarItems[i].Key == lastSoftwareCategory) { exists = true; break; }
            OnSidebarSelect(exists ? lastSoftwareCategory : "常用");
        }

        /// <summary>
        /// 取指定分类的网格/列表偏好。已记录的按记忆值；
        /// 未记录时软件分类用全局 ViewGrid 兜底，资源分类默认列表（保持资源模式原有外观）。
        /// </summary>
        private bool GetCategoryGridMode(string cat)
        {
            bool v;
            if (config.CategoryViewMode.TryGetValue(cat, out v)) return v;
            return config.ViewGrid && contentMode != SidebarControl.ContentMode.Website;
        }

        /// <summary>设置指定分类的网格/列表偏好。</summary>
        private void SetCategoryGridMode(string cat, bool grid)
        {
            config.CategoryViewMode[cat] = grid;
        }

        /// <summary>
        /// 允许添加/移除条目的分类：内置分类、自定义分类、资源分类与「快捷中心」；
        /// 常用与只读全集分类（全部软件/全部文件/全部资源）不可编辑。
        /// </summary>
        private bool CanEditCategory(string cat)
        {
            if (IsReadOnlyCategory(cat) || cat == "常用") return false;
            if (cat == QuickCenter) return true;
            if (IsCustomCategory(cat)) return true;
            if (config.ResourceCategories.Contains(cat)) return true;
            for (int i = 0; i < CategoryRules.BuiltIn.Length; i++)
                if (CategoryRules.BuiltIn[i] == cat) return true;
            for (int i = 0; i < CategoryRules.ResourceBuiltIn.Length; i++)
                if (CategoryRules.ResourceBuiltIn[i] == cat) return true;
            return false;
        }

        /// <summary>只读全集分类：全部软件 / 全部文件（软件模式）与全部资源（资源模式），不参与编辑与分类移动。</summary>
        private static bool IsReadOnlyCategory(string cat)
        {
            return cat == "全部软件" || cat == "全部文件" || cat == "全部资源";
        }

        /// <summary>
        /// 允许网格内拖动排序的分类：可编辑分类 +「常用」。
        /// 「常用」不可增删条目，但用户可调整其展示顺序（与「拖出加入其它分类」相互独立）。
        /// </summary>
        private bool CanReorderCategory(string cat)
        {
            return CanEditCategory(cat) || cat == "常用";
        }

        /// <summary>可作为「拖出到其它分类加入」来源的分类：只读全集与「常用」；
        /// 其条目本身不可编辑，但支持多归属拖出（原归类与存储位置均不变）。</summary>
        private static bool IsDragOutSourceCategory(string cat)
        {
            return IsReadOnlyCategory(cat) || cat == "常用";
        }

        // ===== 扫描与图标 =====

        /// <summary>按设置启动自动监听：关掉「自动收录」或窗口已销毁时直接跳过。</summary>
        private void StartAutoScan()
        {
            StopAutoScan();
            if (config == null || !config.AutoScan || IsDisposed) return;
            autoScan = new AutoScanWatcher(this);
            autoScan.ChangesDetected += OnAutoScanDetected;
        }

        private void StopAutoScan()
        {
            if (autoScan == null) return;
            autoScan.ChangesDetected -= OnAutoScanDetected;
            autoScan.Dispose();
            autoScan = null;
        }

        /// <summary>
        /// 监听回调：先把桌面上新出现的快捷方式收进「快捷中心」（仅搬家模式），
        /// 再静默重扫，新装的软件随即出现在「全部软件」里。
        /// </summary>
        private void OnAutoScanDetected(object sender, EventArgs e)
        {
            if (config == null || !config.AutoScan || autoScan == null) return;
            List<string> fresh = autoScan.TakeNewDesktopFiles();
            if (fresh.Count > 0 && !config.OrganizeCopy && config.OrganizeShortcut)
            {
                // 先登记「这些删除是我们自己造成的」，避免搬运后再白扫一遍
                autoScan.IgnoreDeletions(fresh);
                int failed;
                DesktopOrganizer.AutoStashShortcuts(config, fresh, out failed);
            }
            StartScan(false, true);   // 静默重扫：保留打开中的图片/视频，不打断播放
        }

        private void StartScan()
        {
            StartScan(true, false);
        }

        /// <summary>执行扫描；showLoading=false 时不显示「正在扫描」遮罩（自动收录的静默重扫）。</summary>
        private void StartScan(bool showLoading)
        {
            StartScan(showLoading, false);
        }

        /// <summary>执行扫描；keepViewers=true 时列表原位刷新，不退出正在查看的图片/视频（静默重扫不打断播放）。</summary>
        private void StartScan(bool showLoading, bool keepViewers)
        {
            if (IsDisposed) return;
            if (showLoading)
            {
                loadingLabel.Visible = true;
                loadingLabel.BringToFront();
                // 设置页打开时扫描进度不应压住页面（从设置页「重新扫描」触发）
                if (settingsOpen) settingsPage.BringToFront();
            }
            // 扫描进行中再次请求：只登记一次补扫，避免多个扫描线程同时回写列表
            if (scanRunning)
            {
                scanPending = true;
                return;
            }
            scanRunning = true;
            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                List<AppInfo> result;
                try { result = scanner.ScanAll(); }
                catch { result = new List<AppInfo>(); }
                try
                {
                    BeginInvoke((Action)delegate
                    {
                        // 换列表与释放旧图标必须在 UI 线程完成，避免与绘制/图标加载竞态
                        scanRunning = false;
                        SwapApps(result);
                        RefreshGrid(keepViewers);
                        StartIconLoad();
                        if (scanPending)
                        {
                            scanPending = false;
                            StartScan(false, true);   // 扫描期间又来了一轮请求：补扫（同样不打断查看器）
                        }
                        else
                        {
                            loadingLabel.Visible = false;
                        }
                    });
                }
                catch { scanRunning = false; }
            });
        }

        /// <summary>
        /// 替换应用列表：自增图标代号使旧加载任务失效，并释放旧图标位图，防止 GDI 句柄累积。
        /// 仅在 UI 线程调用。
        /// </summary>
        private void SwapApps(List<AppInfo> newList)
        {
            iconGen++;
            DisposeIcons(allApps);
            allApps = newList ?? new List<AppInfo>();
        }

        private static void DisposeIcons(List<AppInfo> list)
        {
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                AppInfo a = list[i];
                if (a == null) continue;
                if (a.Icon != null)
                {
                    try { a.Icon.Dispose(); } catch { }
                    a.Icon = null;
                }
                if (a.SmallIcon != null)
                {
                    try { a.SmallIcon.Dispose(); } catch { }
                    a.SmallIcon = null;
                }
            }
        }

        private void StartIconLoad()
        {
            int gen = iconGen;
            List<AppInfo> snapshot = allApps;
            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                for (int i = 0; i < snapshot.Count; i++)
                {
                    if (gen != iconGen) return; // 已被新一轮扫描取代，放弃
                    AppInfo a = snapshot[i];
                    bool changed = false;
                    // 网格大图标
                    if (a.Icon == null)
                    {
                        Bitmap bmp = null;
                        try { bmp = Icons.Extract(a, Theme.IconSize); }
                        catch { bmp = null; }
                        if (gen != iconGen) { if (bmp != null) bmp.Dispose(); return; }
                        a.Icon = bmp;
                        changed = true;
                    }
                    // 列表小图标（shell 16x16 原图，对齐资源管理器，避免大图缩小发虚）
                    if (a.SmallIcon == null)
                    {
                        Bitmap sm = null;
                        try { sm = Icons.ExtractSmall(a, Theme.ListIconSize); }
                        catch { sm = null; }
                        if (gen != iconGen) { if (sm != null) sm.Dispose(); return; }
                        a.SmallIcon = sm;
                        changed = true;
                    }
                    if (changed) RequestIconRefresh();
                }
                RequestIconRefresh();
            });
        }

        /// <summary>合并图标重绘请求：同一时刻至多挂起一次全量重绘，减少重复绘制。</summary>
        private void RequestIconRefresh()
        {
            if (Interlocked.CompareExchange(ref refreshPending, 1, 0) != 0) return;
            try
            {
                BeginInvoke((Action)delegate
                {
                    Interlocked.Exchange(ref refreshPending, 0);
                    grid.RefreshIcons();
                });
            }
            catch { Interlocked.Exchange(ref refreshPending, 0); }
        }

        // ===== 过滤与刷新 =====

        private void RefreshGrid()
        {
            RefreshGrid(false);
        }

        /// <summary>刷新列表；keepViewers=true 时保留打开中的图片/视频查看器（自动收录的静默重扫不打断播放）。</summary>
        private void RefreshGrid(bool keepViewers)
        {
            if (!keepViewers)
            {
                // 列表内容即将变化（切分类/搜索/手动重扫），先退出图片/视频查看器，避免浏览序列过期
                HideImageViewer();
                HideVideoPlayer();
            }
            string kw = (search.Text ?? string.Empty).Trim().ToLowerInvariant();
            List<AppInfo> shown = new List<AppInfo>();

            if (kw.Length > 0)
            {
                // 全局搜索：跨全部分类匹配名称，命中即显示（受内容模式过滤）
                foreach (AppInfo a in allApps)
                {
                    if (!ShouldShow(a)) continue;
                    if (!MatchesContentMode(a)) continue;
                    // 多字段匹配：名称 / 分类，任一命中即显示（归一化，故「vscode」可命中「VS Code」）
                    if (Scanner.FuzzyMatch(a.Name, kw)
                        || Scanner.FuzzyMatch(a.Category, kw))
                        shown.Add(a);
                }
                categoryTotal = CountVisible();
            }
            else if (contentMode == SidebarControl.ContentMode.Website)
            {
                // 资源模式：「全部资源」展示全部资源条目（资源分类下的本地文件/文件夹），其余分类按归属过滤；
                // 进入集合时仅显示集合成员（成员 = 属于父分类且额外归属该集合）
                foreach (AppInfo a in allApps)
                {
                    if (!ShouldShow(a)) continue;
                    if (!IsResourceEntry(a)) continue;
                    if (currentCategory == "全部资源" || InCategory(a, currentCategory))
                    {
                        if (currentCollection != null && !IsInExtraCategory(a, currentCollection)) continue;
                        shown.Add(a);
                    }
                }
                categoryTotal = shown.Count;
            }
            else
            {
                List<AppInfo> cat = new List<AppInfo>();
                foreach (AppInfo a in allApps)
                {
                    if (!ShouldShow(a)) continue;
                    if (InCategory(a, currentCategory)) cat.Add(a);
                }
                categoryTotal = cat.Count;
                shown.AddRange(cat);
                // 仅可排序分类（内置/自定义/快捷中心/常用，非搜索态）应用用户拖拽顺序
                if (kw.Length == 0 && CanReorderCategory(currentCategory))
                    ApplyItemOrder(shown, currentCategory);
            }

            shownApps = shown;
            // 行内第二个按钮：进入集合后为「移除」（仅从集合移出），分类/全部资源/搜索下为「删除」
            grid.RowDeleteText = (contentMode == SidebarControl.ContentMode.Website && kw.Length == 0
                && currentCollection != null) ? "移除" : "删除";
            grid.SetApps(shown);
            // 拖动排序仅在可排序分类的软件模式、非搜索态开放
            grid.AllowReorder = kw.Length == 0
                && contentMode == SidebarControl.ContentMode.Software
                && CanReorderCategory(currentCategory);
            // 外部文件/文件夹拖入主内容区：软件/资源模式下当前分类可编辑时放行
            grid.AllowExternalDrop = CanEditCategory(currentCategory);
            // 拖出来源：软件模式=搜索态或只读全集/「常用」；资源模式=搜索态、「全部资源」或可编辑资源分类
            // （后两者拖出用于「加入集合侧边栏/侧栏分类」，多归属）。与网格内排序相互独立。
            grid.AllowDragOut = contentMode == SidebarControl.ContentMode.Website
                ? (kw.Length > 0 || currentCategory == "全部资源" || CanEditCategory(currentCategory))
                : (kw.Length > 0 || IsDragOutSourceCategory(currentCategory));
            UpdateCount(shown.Count, categoryTotal);
            UpdateCollectionUi();   // 集合侧边栏/信息页随归属与可见性刷新
            SyncContentVisibility();
        }

        /// <summary>内容区显隐：集合信息页/查看器/设置页优先，其余情况展示网格并置前。</summary>
        private void SyncContentVisibility()
        {
            if (settingsOpen) return;
            bool showGrid = !(collectionInfo != null && collectionInfo.Visible)
                && !imageViewer.Visible && !videoPlayer.Visible;
            if (showGrid)
            {
                if (!grid.Visible) grid.Visible = true;
                grid.BringToFront();
            }
            else if (grid.Visible)
            {
                grid.Visible = false;
            }
        }

        /// <summary>按拖拽排序记录重排：已记录的按序在前，未记录的保持原相对顺序排后。</summary>
        private void ApplyItemOrder(List<AppInfo> list, string category)
        {
            List<string> order;
            if (!config.ItemOrder.TryGetValue(category, out order) || order == null || order.Count == 0) return;
            Dictionary<string, int> rank = new Dictionary<string, int>();
            for (int i = 0; i < order.Count; i++)
                if (!rank.ContainsKey(order[i])) rank[order[i]] = i;

            List<AppInfo> known = new List<AppInfo>();
            List<AppInfo> unknown = new List<AppInfo>();
            for (int i = 0; i < list.Count; i++)
            {
                int r;
                // 旧版配置的排序键是「名称归一化 id」，导入时加 af: 前缀；按名称回退匹配
                if (list[i].Key != null && rank.TryGetValue(list[i].Key, out r)) known.Add(list[i]);
                else if (list[i].Name != null
                    && rank.TryGetValue("af:" + Scanner.NormalizeEntryName(list[i].Name), out r)) known.Add(list[i]);
                else unknown.Add(list[i]);
            }
            known.Sort(delegate(AppInfo x, AppInfo y)
            {
                int rx = RankOf(rank, x);
                int ry = RankOf(rank, y);
                return rx.CompareTo(ry);
            });
            list.Clear();
            list.AddRange(known);
            list.AddRange(unknown);
        }

        /// <summary>排序名次：优先精确 Key，其次按名称归一化回退（兼容旧版导入的 af: 键）。</summary>
        private static int RankOf(Dictionary<string, int> rank, AppInfo a)
        {
            int r;
            if (a.Key != null && rank.TryGetValue(a.Key, out r)) return r;
            if (a.Name != null && rank.TryGetValue("af:" + Scanner.NormalizeEntryName(a.Name), out r)) return r;
            return int.MaxValue;
        }

        /// <summary>条目拖动排序完成：调整当前展示顺序并写入 config.ItemOrder 后刷新。</summary>
        private void OnGridReorder(int from, int to)
        {
            if (from < 0 || from >= shownApps.Count) return;
            if (to < 0 || to >= shownApps.Count) return;
            AppInfo moved = shownApps[from];
            shownApps.RemoveAt(from);
            shownApps.Insert(to, moved);

            List<string> order = new List<string>();
            for (int i = 0; i < shownApps.Count; i++) order.Add(shownApps[i].Key ?? string.Empty);
            config.ItemOrder[currentCategory] = order;
            config.Save();
            RefreshGrid();
        }

        /// <summary>网格拖动条目期间：优先联动集合侧边栏高亮，其次侧栏可投放分类。</summary>
        private void OnGridDragMoved(Point screen)
        {
            string colKey = collectionSidebar.Visible ? collectionSidebar.DropCollectionAtScreen(screen) : null;
            collectionSidebar.SetExternalDropHighlight(colKey);
            sidebar.SetExternalDropHighlight(
                colKey == null ? sidebar.DropCategoryAtScreen(screen) : null);
        }

        /// <summary>网格外松手：命中集合行则整批「加入集合」；否则落侧栏分类按原逻辑「加入」。</summary>
        private void OnGridDroppedOutside(List<AppInfo> apps, Point screen)
        {
            if (apps == null || apps.Count == 0) return;
            string colKey = collectionSidebar.Visible ? collectionSidebar.DropCollectionAtScreen(screen) : null;
            collectionSidebar.SetExternalDropHighlight(null);
            if (colKey != null)
            {
                sidebar.SetExternalDropHighlight(null);
                AddItemsToCategory(apps, colKey);
                return;
            }
            string cat = sidebar.DropCategoryAtScreen(screen);
            sidebar.SetExternalDropHighlight(null);
            if (string.IsNullOrEmpty(cat)) return;
            AddItemsToCategory(apps, cat);
        }

        /// <summary>外部文件/文件夹拖入主内容区：按当前模式入库（软件/资源各自的存储位置，实体移动、原文件不保留）；集合视图内同时加入当前集合。</summary>
        private void OnGridFilesDropped(string[] paths)
        {
            if (paths == null || paths.Length == 0) return;
            if (!CanEditCategory(currentCategory)) return;
            AddPaths(paths, currentCategory, ActiveCollectionKey);
        }

        /// <summary>外部文件拖到集合侧边栏的自定义集合行：加入该集合（同时入库到父分类）。</summary>
        private void OnCollectionFilesDropped(string collectionKey, string[] paths)
        {
            if (paths == null || paths.Length == 0) return;
            ResourceCollection c = FindCollectionByKey(collectionKey);
            if (c == null) return;   // 集合已不存在：忽略本次投放
            AddPaths(paths, c.Parent, collectionKey);
        }

        /// <summary>
        /// 文件类型条目拖入目录类型条目：把条目目标（文件/文件夹）搬迁进该目录，
        /// 并同步自建条目的持久化记录与归属配置（Key 由路径派生，需迁移）。
        /// 已在目标内 / 拖入自身子目录 / 搬运失败时不做任何变更。
        /// </summary>
        private void OnGridMoveIntoFolder(AppInfo src, AppInfo folder)
        {
            if (src == null || folder == null || !src.IsFile || !folder.IsFolder) return;
            string sp = src.TargetPath;
            string dp = folder.TargetPath;
            if (string.IsNullOrEmpty(sp) || string.IsNullOrEmpty(dp)) return;
            if (!Directory.Exists(dp)) return;               // 目录条目失效
            bool srcIsDir = Directory.Exists(sp);
            if (!srcIsDir && !File.Exists(sp)) return;       // 文件条目失效
            if (IsUnder(sp, dp) || IsUnder(dp, sp)) return;  // 拖入自身子目录 / 已在目标内

            string dest = UniqueDest(dp, Path.GetFileName(sp));
            try
            {
                if (srcIsDir) DesktopOrganizer.MoveDirectory(sp, dest);   // 同卷 Move / 跨卷 复制+删除
                else File.Move(sp, dest);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("拖入目录搬迁失败 " + sp + " -> " + dest + ": " + ex.Message);
                return;
            }

            // 同步持久化：自建条目记录路径（Key=add:路径，需一并迁移归属配置，避免分类关联失联）
            string oldKey = src.Key;
            string newKey = "add:" + dest;
            for (int i = 0; i < config.AddedItems.Count; i++)
            {
                AddedEntry e2 = config.AddedItems[i];
                if (string.Equals(e2.Path, sp, StringComparison.OrdinalIgnoreCase))
                {
                    e2.Path = dest;
                    e2.IsFolder = srcIsDir;
                }
            }
            if (!string.IsNullOrEmpty(oldKey))
            {
                string ov;
                if (config.CategoryOverride.TryGetValue(oldKey, out ov))
                {
                    config.CategoryOverride.Remove(oldKey);
                    config.CategoryOverride[newKey] = ov;
                }
                List<string> cats;
                if (config.ItemCategories.TryGetValue(oldKey, out cats))
                {
                    config.ItemCategories.Remove(oldKey);
                    config.ItemCategories[newKey] = cats;
                }
            }
            config.Save();
            SwapApps(scanner.ScanAll());
            RefreshGrid();
            StartIconLoad();
        }

        /// <summary>网格空白处右键：新建文件夹 / 文本文档（存入配置的存储位置，收录进当前分类）；资源分类附「新建集合…」。</summary>
        private void OnGridBlankRightClicked(Point screen)
        {
            if (!CanEditCategory(currentCategory)) return;
            List<MenuWindow.MenuItem> items = new List<MenuWindow.MenuItem>();
            items.Add(MkMenu("新建文件夹", delegate { CreateManagedEntry(true); }));
            items.Add(MkMenu("新建文本文档", delegate { CreateManagedEntry(false); }));
            if (contentMode == SidebarControl.ContentMode.Website)
            {
                items.Add(MkSep());
                items.Add(MkMenu("新建集合…", delegate { OnCreateCollection(null); }));
            }
            MenuWindow mw = new MenuWindow(items, 180);
            mw.Location = FixLocation(screen, mw.Size);
            mw.Show(this);
        }

        /// <summary>
        /// 在配置的存储位置新建文件夹/文本文档（文件夹入「文件夹存储位置」、文档入「文件存储位置」，
        /// 未配置时按入库规则兜底），重名自动追加 _1，并收录进当前分类。
        /// </summary>
        private void CreateManagedEntry(bool folder)
        {
            string dir = folder ? FolderStoreOf() : FileStoreOf();
            string name = folder ? "新建文件夹" : "新建文本文档.txt";
            string path;
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                path = UniqueDest(dir, name);
                if (folder) Directory.CreateDirectory(path);
                else File.Create(path).Dispose();            // 0 字节空文本文档
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("新建" + (folder ? "文件夹" : "文档") + "失败: " + ex.Message);
                return;
            }
            AddedEntry e = new AddedEntry();
            e.Path = path;
            e.Category = currentCategory;
            e.IsFolder = folder;
            config.AddedItems.Add(e);
            config.Save();
            SwapApps(scanner.ScanAll());
            RefreshGrid();
            StartIconLoad();
        }

        /// <summary>文档/目录条目追加「删除」项：仅限自建收录的条目（软件不适用）。</summary>
        private void AddDeleteMenu(List<MenuWindow.MenuItem> items, AppInfo app)
        {
            if (!app.IsFile) return;
            if (FindAddedEntry(app.TargetPath, currentCategory) < 0 && !IsAddedAnywhere(app.TargetPath)) return;
            items.Add(MkMenu("删除", delegate { ConfirmDeleteEntryDisk(app); }));
        }

        /// <summary>右键「删除」二次确认：文件进回收站可恢复，但收录记录与归属配置会一并清理。</summary>
        private void ConfirmDeleteEntryDisk(AppInfo app)
        {
            if (app == null) return;
            if (!ConfirmDialog.Show("删除文件",
                "确认删除「" + app.DisplayName() + "」？\n文件将移入回收站，并从资源库移除。")) return;
            DeleteEntryDisk(app);
        }

        /// <summary>直接删除收录的文档/目录（磁盘文件进回收站，可恢复），并清理收录与归属配置后刷新网格。
        /// 与键盘/行按钮的 DeleteAddedEntry（仅移除记录）区分：本方法删除磁盘文件本身。</summary>
        private void DeleteEntryDisk(AppInfo app)
        {
            if (app == null) return;
            string p = app.TargetPath;
            if (string.IsNullOrEmpty(p)) return;
            if (!File.Exists(p) && !Directory.Exists(p)) return;   // 条目已失效
            if (!Launcher.DeletePath(p, true))
            {
                MessageBox.Show("删除失败：\n" + p, "删除", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // 清理持久化：收录记录 + Key（add:路径）派生的常用/隐藏/归属，避免残留孤儿配置
            string key = app.Key;
            for (int i = config.AddedItems.Count - 1; i >= 0; i--)
                if (string.Equals(config.AddedItems[i].Path, p, StringComparison.OrdinalIgnoreCase))
                    config.AddedItems.RemoveAt(i);
            if (!string.IsNullOrEmpty(key))
            {
                config.Favorites.Remove(key);
                config.Hidden.Remove(key);
                config.CategoryOverride.Remove(key);
                config.ItemCategories.Remove(key);
                foreach (List<string> order in config.ItemOrder.Values)
                    for (int i = order.Count - 1; i >= 0; i--)
                        if (string.Equals(order[i], key, StringComparison.OrdinalIgnoreCase)) order.RemoveAt(i);
            }
            config.Save();
            SwapApps(scanner.ScanAll());
            RefreshGrid();
            StartIconLoad();
        }

        /// <summary>当前内容模式下条目是否应展示：资源模式=资源条目（资源分类下的本地文件/文件夹），软件模式=其余条目。</summary>
        private bool MatchesContentMode(AppInfo a)
        {
            bool resource = IsResourceEntry(a);
            return contentMode == SidebarControl.ContentMode.Website ? resource : !resource;
        }

        /// <summary>可见性判定：隐藏项开关 + 显示全部已安装开关。</summary>
        private bool ShouldShow(AppInfo a)
        {
            if (a.IsHidden && !config.ShowHidden) return false;
            if (!config.ShowAllInstalled && !IsLaunchable(a)) return false;
            return true;
        }

        /// <summary>是否可直接启动/打开：UWP、文件/文件夹条目、或有可用目标路径。</summary>
        private static bool IsLaunchable(AppInfo a)
        {
            return a.IsUwp || a.IsFile || !string.IsNullOrEmpty(a.TargetPath);
        }

        /// <summary>统计当前可见应用总数（用于搜索态计数显示）。</summary>
        private int CountVisible()
        {
            int n = 0;
            foreach (AppInfo a in allApps)
                if (ShouldShow(a)) n++;
            return n;
        }

        private bool InCategory(AppInfo a, string category)
        {
            // 只读全集分类：全部软件=软件（不含文件/文件夹），全部文件=软件存储中的文件/文件夹，全部资源=资源条目
            if (category == "全部软件") return !a.IsFile;
            if (category == "全部文件") return a.IsFile && !IsResourceEntry(a);
            if (category == "全部资源") return IsResourceEntry(a);
            if (category == "常用") return a.IsFavorite;

            // 额外归属分类（拖放「加入分类」，多归属）：原分类保留，命中即视为属于该分类
            if (IsInExtraCategory(a, category)) return true;

            // 显式分类覆盖（含「移动到分类」结果）优先于规则归类
            string ov;
            bool overridden = config.CategoryOverride.TryGetValue(a.Key, out ov) && !string.IsNullOrEmpty(ov);
            if (overridden) return ov == category;
            // 「快捷中心」：桌面收纳项 + 手动添加项
            if (category == QuickCenter)
                return a.IsDesktop || a.Category == QuickCenter || IsAddedToCategory(a, QuickCenter);
            // 内置分类：未被 override 覆盖时按规则分类
            return a.Category == category;
        }

        /// <summary>是否为手动添加在指定分类下的条目（按路径匹配 AddedItems）。</summary>
        private bool IsAddedToCategory(AppInfo a, string category)
        {
            if (a == null || string.IsNullOrEmpty(a.TargetPath)) return false;
            for (int i = 0; i < config.AddedItems.Count; i++)
            {
                AddedEntry e = config.AddedItems[i];
                if (e.Category == category
                    && string.Equals(e.Path, a.TargetPath, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>是否为拖放「加入」到指定分类的条目（额外归属，多分类；不改原分类与存储位置）。</summary>
        private bool IsInExtraCategory(AppInfo a, string category)
        {
            if (a == null || string.IsNullOrEmpty(a.Key) || string.IsNullOrEmpty(category)) return false;
            List<string> cats;
            return config.ItemCategories.TryGetValue(a.Key, out cats) && cats.Contains(category);
        }

        /// <summary>资源条目判定：按资源模式收纳登记过的本地文件/文件夹。</summary>
        private bool IsResourceEntry(AppInfo a)
        {
            if (a == null) return false;
            if (!a.IsFile) return false;
            // 资源文件一律来自带 IsResource 标记的 AddedItems 记录（改名/移动分类只改归属，不改变该记录）
            return IsAddedAsResource(a);
        }

        /// <summary>是否为按资源模式登记的手动添加条目（依据 AddedItems.IsResource 判定）。</summary>
        private bool IsAddedAsResource(AppInfo a)
        {
            if (string.IsNullOrEmpty(a.TargetPath)) return false;
            for (int i = 0; i < config.AddedItems.Count; i++)
            {
                AddedEntry e = config.AddedItems[i];
                if (e.IsResource
                    && string.Equals(e.Path, a.TargetPath, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>把单个条目「加入」指定分类（多归属）；集合键（col:）同样适用。</summary>
        private void AddItemToCategory(AppInfo app, string category)
        {
            if (app == null) return;
            List<AppInfo> one = new List<AppInfo>();
            one.Add(app);
            AddItemsToCategory(one, category);
        }

        /// <summary>
        /// 批量把条目「加入」指定分类（多归属）：原分类与存储位置均不变，已在该分类则跳过；
        /// 集合键额外校验集合存在与父分类类型约束。一次落盘与刷新，避免批量操作重复刷新。
        /// </summary>
        private void AddItemsToCategory(List<AppInfo> apps, string category)
        {
            if (apps == null || apps.Count == 0) return;
            bool isCollection = CollectionKeys.IsCollectionKey(category);
            ResourceCollection col = null;
            if (isCollection)
            {
                col = FindCollectionByKey(category);
                if (col == null) return;
            }
            else if (!CanEditCategory(category)) return;

            bool changed = false;
            for (int i = 0; i < apps.Count; i++)
            {
                AppInfo a = apps[i];
                if (a == null || string.IsNullOrEmpty(a.Key)) continue;
                if (isCollection && !CanJoinResourceCategory(a, col.Parent)) continue;
                if (InCategory(a, category)) continue;
                if (CollectionOps.AddMember(config, a.Key, category)) changed = true;
            }
            if (!changed) return;
            config.Save();
            RefreshGrid();
        }

        /// <summary>把单个条目从指定分类「移出」（仅移除额外归属，不影响主分类与存储位置）。</summary>
        private void RemoveItemFromCategory(AppInfo app, string category)
        {
            if (app == null) return;
            List<AppInfo> one = new List<AppInfo>();
            one.Add(app);
            RemoveItemsFromCategory(one, category);
        }

        /// <summary>批量移出分类/集合：一次落盘刷新。</summary>
        private void RemoveItemsFromCategory(List<AppInfo> apps, string category)
        {
            if (apps == null || apps.Count == 0) return;
            bool changed = false;
            for (int i = 0; i < apps.Count; i++)
            {
                AppInfo a = apps[i];
                if (a == null || string.IsNullOrEmpty(a.Key)) continue;
                if (CollectionOps.RemoveMember(config, a.Key, category)) changed = true;
            }
            if (!changed) return;
            config.Save();
            RefreshGrid();
        }

        // ===== 集合（资源分类的第二级侧边栏 + 信息页） =====

        /// <summary>集合信息页大封面尺寸（外部构建位图按此尺寸，绘制时按可用宽度缩放）。</summary>
        private const int CollectionInfoCoverW = 320;
        private const int CollectionInfoCoverH = 200;

        /// <summary>是否展示集合侧边栏：仅资源模式且当前为可编辑资源分类（「全部资源」与软件模式不展示）。</summary>
        private bool CanShowCollectionSidebar
        {
            get
            {
                return collectionSidebar != null
                    && contentMode == SidebarControl.ContentMode.Website
                    && !settingsOpen
                    && CanEditCategory(currentCategory);
            }
        }

        /// <summary>当前是否处于搜索态（搜索展示全局结果，不展示集合信息页）。</summary>
        private bool SearchActive
        {
            get { return search != null && !string.IsNullOrEmpty((search.Text ?? string.Empty).Trim()); }
        }

        /// <summary>刷新集合侧边栏与信息页：可见性、集合列表与封面、选中态、信息页数据。</summary>
        private void UpdateCollectionUi()
        {
            if (!CanShowCollectionSidebar)
            {
                currentCollection = null;   // 离开可编辑资源分类/进入设置页：集合上下文失效
                collectionEntered = false;
                collectionInfo.Visible = false;
                SetCollectionSidebarVisible(false);
                return;
            }
            if (currentCollection != null && FindCollectionByKey(currentCollection) == null)
            {
                currentCollection = null;   // 集合已被删除等：回退「全部」
                collectionEntered = true;
            }
            SetCollectionSidebarVisible(true);
            collectionSidebar.SetItems(BuildCollectionItems());
            collectionSidebar.SetActive(currentCollection == null ? string.Empty : currentCollection);
            UpdateCollectionInfo();
        }

        /// <summary>同步侧边栏可见性（不重建列表，供布局前调用）；变化时重排布局让出宽度。</summary>
        private void SyncCollectionSidebarVisibility()
        {
            if (collectionSidebar == null) return;
            SetCollectionSidebarVisible(CanShowCollectionSidebar);
        }

        /// <summary>切换集合侧边栏可见性；变化时重排布局（内容区按侧边栏宽度让位）。</summary>
        private void SetCollectionSidebarVisible(bool visible)
        {
            if (collectionSidebar == null || collectionSidebar.Visible == visible) return;
            collectionSidebar.Visible = visible;
            if (!visible)
            {
                collectionInfo.Visible = false;
                collectionInfo.Clear();   // 侧边栏收起时一并释放信息页封面
            }
            LayoutControls();
        }

        /// <summary>集合信息页：选中自定义集合、未进入内容且非搜索态时展示名称/简介/封面/进入。</summary>
        private void UpdateCollectionInfo()
        {
            if (collectionInfo.IsEditing)
            {
                // 页内编辑中：后台刷新（自动重扫等）不覆盖表单；离开该集合信息页或进入搜索时才退出编辑
                if (currentCollection != null && !collectionEntered && !SearchActive) return;
                collectionInfo.EndEdit();
            }
            bool show = collectionSidebar.Visible
                && currentCollection != null
                && !collectionEntered
                && !SearchActive;
            if (!show)
            {
                collectionInfo.Visible = false;
                collectionInfo.Clear();   // 释放大封面位图，回到信息页时再按需重建
                return;
            }
            ResourceCollection c = FindCollectionByKey(currentCollection);
            if (c == null)
            {
                collectionInfo.Visible = false;
                collectionInfo.Clear();
                return;
            }
            int count;
            List<string> images;
            AppInfo first = CollectionSummary(currentCollection, out count, out images);
            collectionInfo.SetInfo(c.Parent, c.Name, c.Description, count, c.Cover,
                c.CoverFocusX, c.CoverFocusY,
                BuildCover(c, first, CollectionInfoCoverW, CollectionInfoCoverH),
                images);
            collectionInfo.Visible = true;
            collectionInfo.BringToFront();
        }

        /// <summary>
        /// 当前生效的集合上下文：已进入集合内容时返回集合键，否则 null。
        /// 信息页/搜索态/侧边栏不可见时，「添加/拖入」不应静默作用于集合。
        /// </summary>
        private string ActiveCollectionKey
        {
            get
            {
                return collectionSidebar != null && collectionSidebar.Visible && collectionEntered
                    ? currentCollection : null;
            }
        }

        /// <summary>按集合键（col:父分类/集合名）查找集合定义；格式非法或不存在返回 null。</summary>
        private ResourceCollection FindCollectionByKey(string key)
        {
            if (!CollectionKeys.IsCollectionKey(key)) return null;
            string rest = key.Substring(CollectionKeys.Prefix.Length);
            int bar = rest.IndexOf('/');
            if (bar <= 0) return null;
            return CollectionOps.Find(config, rest.Substring(0, bar), rest.Substring(bar + 1));
        }

        /// <summary>构造当前分类下的集合侧边栏条目：首位固定「全部」，其后为当前分类的自定义集合（含成员数与封面）。</summary>
        private List<CollectionSidebar.Item> BuildCollectionItems()
        {
            List<CollectionSidebar.Item> list = new List<CollectionSidebar.Item>();
            List<AppInfo> pool = VisibleResourcePool(currentCategory);   // 一次过滤供各集合复用
            CollectionSidebar.Item all = new CollectionSidebar.Item();
            all.IsAll = true;
            all.Key = string.Empty;
            all.Name = "全部";
            list.Add(all);
            for (int i = 0; i < config.Collections.Count; i++)
            {
                ResourceCollection c = config.Collections[i];
                if (c.Parent != currentCategory) continue;
                CollectionSidebar.Item it = new CollectionSidebar.Item();
                it.Key = CollectionKeys.Make(c.Parent, c.Name);
                it.Name = c.Name;
                AppInfo first = null;
                AppInfo firstImage = null;
                for (int j = 0; j < pool.Count; j++)
                {
                    if (!IsInExtraCategory(pool[j], it.Key)) continue;
                    if (first == null) first = pool[j];   // 兜底：无图片成员时用首个成员版式化
                    if (IsImageMember(pool[j])) { firstImage = pool[j]; break; }   // 取首个图片成员作自适应封面
                }
                it.Cover = BuildCover(c, firstImage ?? first, CollectionSidebar.ThumbSize, CollectionSidebar.ThumbSize);
                list.Add(it);
            }
            return list;
        }

        /// <summary>分类内「当前可见」的资源条目池：与网格过滤同口径（ShouldShow），保证卡片计数与网格显示一致。</summary>
        private List<AppInfo> VisibleResourcePool(string category)
        {
            List<AppInfo> pool = new List<AppInfo>();
            for (int i = 0; i < allApps.Count; i++)
            {
                AppInfo a = allApps[i];
                if (!ShouldShow(a) || !IsResourceEntry(a) || !InCategory(a, category)) continue;
                pool.Add(a);
            }
            return pool;
        }

        /// <summary>
        /// 集合概览（与集合视图过滤同口径，一次遍历取全）：返回封面用首个图片成员
        /// （无图片时返回首个成员，供图标版式化）；同时输出可见成员数与图片成员路径（最多 60 张）。
        /// </summary>
        private AppInfo CollectionSummary(string key, out int count, out List<string> imagePaths)
        {
            count = 0;
            imagePaths = new List<string>();
            AppInfo first = null;
            AppInfo firstImage = null;
            ResourceCollection c = FindCollectionByKey(key);
            if (c == null) return null;
            List<AppInfo> pool = VisibleResourcePool(c.Parent);
            for (int i = 0; i < pool.Count; i++)
            {
                AppInfo a = pool[i];
                if (!IsInExtraCategory(a, key)) continue;
                count++;
                if (first == null) first = a;
                if (!IsImageMember(a)) continue;
                if (firstImage == null) firstImage = a;
                if (imagePaths.Count < 60) imagePaths.Add(a.TargetPath);
            }
            return firstImage ?? first;
        }

        /// <summary>条目是否可作为图片封面（有效路径 + 图片扩展名 + 文件存在）。</summary>
        private static bool IsImageMember(AppInfo a)
        {
            return a != null
                && !string.IsNullOrEmpty(a.TargetPath)
                && MediaRules.IsImage(a.TargetPath)
                && File.Exists(a.TargetPath);
        }

        /// <summary>
        /// 构造集合封面位图（尺寸由调用方指定，所有权移交目标控件）：
        /// 用户自定义封面 → 首个成员的图片缩略图 → 首个成员的图标居中版式化；
        /// 图片按集合保存的取景焦点裁剪。全部不可用时返回 null，由控件绘制占位图形。
        /// </summary>
        private static Bitmap BuildCover(ResourceCollection c, AppInfo first, int width, int height)
        {
            if (!string.IsNullOrEmpty(c.Cover))
            {
                Bitmap custom = Icons.LoadCoverThumb(c.Cover, width, height, c.CoverFocusX, c.CoverFocusY);
                if (custom != null) return custom;
            }
            if (first == null || string.IsNullOrEmpty(first.TargetPath)) return null;
            // 图片成员：直接取裁剪填满的缩略图（与封面区等比例，无留白）
            Bitmap photo = Icons.LoadCoverThumb(first.TargetPath, width, height, c.CoverFocusX, c.CoverFocusY);
            if (photo != null) return photo;
            // 非图片成员（文档/压缩包等）：取文件图标居中版式化，避免在封面区露出方形缩略图留白
            Bitmap icon = null;
            try { icon = Icons.Extract(first, 64); }
            catch { icon = null; }
            if (icon == null) return null;
            try { return ComposeIconCover(icon, width, height); }
            finally { icon.Dispose(); }
        }

        /// <summary>把文件图标居中版式化到封面尺寸（透明底，与卡片底色自然融合）。</summary>
        private static Bitmap ComposeIconCover(Bitmap icon, int width, int height)
        {
            try
            {
                Bitmap bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    // 留白按封面高度自适应：大封面留边明显，小缩略图不留过多空白
                    int pad = Math.Max(4, height / 10);
                    int s = Math.Min(Math.Min(icon.Width, icon.Height), height - pad);
                    if (s < 1) s = 1;
                    g.DrawImage(icon, new Rectangle((width - s) / 2, (height - s) / 2, s, s));
                }
                return bmp;
            }
            catch { return null; }
        }

        /// <summary>进入集合内容（双击集合 / 信息页「进入」）：网格过滤为集合成员；空键表示「全部」。</summary>
        private void EnterCollection(string key)
        {
            if (!collectionSidebar.Visible) return;
            collectionInfo.EndEdit();   // 进入内容前放弃未保存的页内编辑
            if (string.IsNullOrEmpty(key))
            {
                currentCollection = null;
                collectionEntered = true;
            }
            else
            {
                if (FindCollectionByKey(key) == null) return;
                currentCollection = key;
                collectionEntered = true;
            }
            collectionSidebar.SetActive(key ?? string.Empty);
            RefreshGrid();
        }

        /// <summary>单击集合：全部直接展示内容网格；自定义集合停留信息页（名称/简介/封面/进入）。</summary>
        private void OnCollectionSelected(string key)
        {
            if (!collectionSidebar.Visible) return;
            collectionInfo.EndEdit();   // 切换集合即放弃未保存的页内编辑
            if (string.IsNullOrEmpty(key))
            {
                currentCollection = null;
                collectionEntered = true;
            }
            else
            {
                if (FindCollectionByKey(key) == null) return;
                currentCollection = key;
                collectionEntered = false;
            }
            collectionSidebar.SetActive(key ?? string.Empty);
            RefreshGrid();
        }

        /// <summary>新建集合（当前分类为父分类）；appsToJoin 非空时新建后立即把这些条目加入集合。</summary>
        private void OnCreateCollection(List<AppInfo> appsToJoin)
        {
            // 重名时保留已输入内容重开对话框，避免用户重填
            ResourceCollection seed = null;
            ResourceCollection c;
            while (true)
            {
                c = CollectionDialog.Show("新建集合（" + currentCategory + "）", seed);
                if (c == null) return;
                if (!CollectionOps.NameExists(config, currentCategory, c.Name)) break;
                MessageBox.Show("集合「" + c.Name + "」已存在，请换一个名称。", "新建集合",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                seed = c;
            }
            collectionInfo.EndEdit();   // 新建并切换到新集合前，放弃当前页内编辑
            c.Parent = currentCategory;
            config.Collections.Add(c);
            string key = CollectionKeys.Make(c.Parent, c.Name);
            if (appsToJoin != null)
            {
                for (int i = 0; i < appsToJoin.Count; i++)
                {
                    AppInfo a = appsToJoin[i];
                    if (a == null || string.IsNullOrEmpty(a.Key)) continue;
                    if (!CanJoinResourceCategory(a, currentCategory)) continue;
                    CollectionOps.AddMember(config, a.Key, key);
                }
            }
            config.Save();
            if (appsToJoin == null || appsToJoin.Count == 0)
            {
                // 从侧边栏「新建集合」：选中新集合并停留在信息页
                currentCollection = key;
                collectionEntered = false;
                collectionSidebar.SetActive(key);
            }
            RefreshGrid();
        }

        /// <summary>集合右键菜单：编辑 / 删除。</summary>
        private void ShowCollectionMenu(string key, Point screen)
        {
            ResourceCollection c = FindCollectionByKey(key);
            if (c == null) return;
            List<MenuWindow.MenuItem> items = new List<MenuWindow.MenuItem>();
            items.Add(MkMenu("编辑集合", delegate { EditCollection(c); }));   // 进入信息页页内编辑，不弹对话框
            items.Add(MkMenu("删除集合", delegate { DeleteCollection(c); }));
            MenuWindow mw = new MenuWindow(items, 160);
            mw.Location = FixLocation(screen, mw.Size);
            mw.Show(this);
        }

        /// <summary>编辑集合：选中该集合进入信息页，并直接切换为页内编辑态（不再弹出编辑对话框）。</summary>
        private void EditCollection(ResourceCollection c)
        {
            if (c == null) return;
            collectionInfo.EndEdit();   // 先退出可能存在的页内编辑，避免把旧表单数据带到另一个集合
            string key = CollectionKeys.Make(c.Parent, c.Name);
            currentCollection = key;
            collectionEntered = false;
            collectionSidebar.SetActive(key);
            RefreshGrid();          // 先展示信息页，再进入编辑态
            collectionInfo.BeginEdit();
        }

        /// <summary>信息页「保存」：校验重名并落盘；粘贴图片写入受管封面目录；改名时迁移成员归属键。</summary>
        private void OnCollectionEditSave(string newName, string newDescription, string newCoverPath,
            Bitmap pastedCover, float focusX, float focusY)
        {
            ResourceCollection c = FindCollectionByKey(currentCollection);
            if (c == null) { collectionInfo.EndEdit(); return; }
            string name = (newName ?? string.Empty).Trim();
            if (name.Length == 0) return;
            if (!string.Equals(name, c.Name, StringComparison.Ordinal)
                && CollectionOps.NameExists(config, c.Parent, name))
            {
                MessageBox.Show(this, "集合「" + name + "」已存在，请换一个名称。", "编辑集合",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string finalCover = newCoverPath ?? string.Empty;
            if (pastedCover != null)
            {
                string saved = SaveManagedCoverImage(pastedCover);
                if (string.IsNullOrEmpty(saved))
                {
                    MessageBox.Show(this, "保存封面图片失败，请重试。", "编辑集合",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                finalCover = saved;
            }

            string oldCover = c.Cover;
            string oldKey = CollectionKeys.Make(c.Parent, c.Name);
            c.Name = name;
            c.Description = newDescription ?? string.Empty;
            c.Cover = finalCover;
            c.CoverFocusX = ClampFocus(focusX);
            c.CoverFocusY = ClampFocus(focusY);
            string newKey = CollectionKeys.Make(c.Parent, c.Name);
            if (newKey != oldKey)
            {
                CollectionOps.RenameMemberKey(config, oldKey, newKey);
                if (currentCollection == oldKey) currentCollection = newKey;
            }
            config.Save();
            DeleteManagedCoverIfUnused(oldCover, finalCover);
            collectionInfo.EndEdit();
            RefreshGrid();   // 侧边栏/信息页随名称、简介与封面同步刷新
        }

        /// <summary>受管封面目录：粘贴生成的封面统一存放于此，便于清理且不污染资源库。</summary>
        private static string ManagedCoverDir
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AppManager", "CollectionCovers");
            }
        }

        /// <summary>把粘贴的封面位图保存为 PNG（受管目录），返回路径；失败返回 null。</summary>
        private static string SaveManagedCoverImage(Bitmap bmp)
        {
            try
            {
                string dir = ManagedCoverDir;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "cover_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff")
                    + "_" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".png");
                bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
                return file;
            }
            catch { return null; }
        }

        /// <summary>路径是否位于受管封面目录内。</summary>
        private static bool IsManagedCoverPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            return path.StartsWith(ManagedCoverDir + "\\", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>替换封面后清理不再被任何集合引用的受管封面文件（共享封面与外部路径均不动）。</summary>
        private void DeleteManagedCoverIfUnused(string oldPath, string newPath)
        {
            if (!IsManagedCoverPath(oldPath)) return;
            if (!string.IsNullOrEmpty(newPath)
                && string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase)) return;
            for (int i = 0; i < config.Collections.Count; i++)
                if (string.Equals(config.Collections[i].Cover, oldPath, StringComparison.OrdinalIgnoreCase))
                    return;   // 仍被其它集合用作封面
            try { if (File.Exists(oldPath)) File.Delete(oldPath); } catch { }
        }

        /// <summary>封面取景焦点夹紧到 0~1（NaN 回退居中）。</summary>
        private static float ClampFocus(float v)
        {
            if (float.IsNaN(v)) return 0.5F;
            if (v < 0F) return 0F;
            if (v > 1F) return 1F;
            return v;
        }

        /// <summary>信息页「更换封面」：选择图片并回传路径（保存后才写入配置）。</summary>
        private void OnCollectionPickCover()
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "选择集合封面";
                dlg.Filter = MediaRules.ResourceCategoryFilter("照片");
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                collectionInfo.SetEditCoverPath(dlg.FileName, dlg.FileName);
            }
        }

        /// <summary>信息页「恢复自动」：按首个图片成员重新生成预览（无图片成员时回退版式化封面）。</summary>
        private void OnCollectionAutoCover()
        {
            ResourceCollection c = FindCollectionByKey(currentCollection);
            if (c == null) return;
            int count;
            List<string> images;
            AppInfo first = CollectionSummary(currentCollection, out count, out images);
            if (images.Count > 0)
            {
                collectionInfo.SetEditCoverPath(string.Empty, images[0]);
                return;
            }
            ResourceCollection auto = new ResourceCollection();
            auto.Name = c.Name;
            auto.Parent = c.Parent;
            auto.Description = c.Description;
            auto.Cover = string.Empty;   // 忽略自定义封面，仅按成员生成
            collectionInfo.SetEditCoverBitmap(string.Empty,
                BuildCover(auto, first, CollectionInfoCoverW, CollectionInfoCoverH));
        }

        /// <summary>删除集合：仅移除定义与成员归属，文件保留在父分类（相簿删除语义）。</summary>
        private void DeleteCollection(ResourceCollection c)
        {
            if (!ConfirmDialog.Show("删除集合",
                "删除集合「" + c.Name + "」不会删除集合内的文件，\n文件仍保留在「" + c.Parent + "」分类中。\n\n确认删除？")) return;
            string key = CollectionKeys.Make(c.Parent, c.Name);
            string oldCover = c.Cover;
            config.Collections.Remove(c);
            CollectionOps.RemoveCollectionKey(config, key);
            if (currentCollection == key)
            {
                currentCollection = null;   // 删除当前集合：回到「全部」
                collectionEntered = true;
            }
            config.Save();
            DeleteManagedCoverIfUnused(oldCover, null);   // 粘贴生成的封面随集合删除一并清理
            RefreshGrid();
        }

        /// <summary>
        /// 条目右键「加入集合…」级联子项：列出当前分类下的集合（所选条目全部已加入/类型不符时置灰），
        /// 末尾附「新建集合…」新建后即加入。
        /// </summary>
        private List<MenuWindow.MenuItem> BuildJoinCollectionItems(List<AppInfo> targets)
        {
            List<MenuWindow.MenuItem> items = new List<MenuWindow.MenuItem>();
            if (targets == null || targets.Count == 0) return items;
            int count = 0;
            for (int i = 0; i < config.Collections.Count; i++)
            {
                ResourceCollection c = config.Collections[i];
                if (c.Parent != currentCategory) continue;
                string key = CollectionKeys.Make(c.Parent, c.Name);
                MenuWindow.MenuItem m = MkMenu(c.Name, delegate { AddItemsToCategory(targets, key); });
                bool addable = false;
                for (int j = 0; j < targets.Count; j++)
                {
                    AppInfo a = targets[j];
                    if (a == null || IsInExtraCategory(a, key)) continue;
                    if (!CanJoinResourceCategory(a, c.Parent)) continue;
                    addable = true;
                    break;
                }
                m.Disabled = !addable;
                items.Add(m);
                count++;
            }
            if (count > 0) items.Add(MkSep());
            items.Add(MkMenu("新建集合…", delegate { OnCreateCollection(targets); }));
            return items;
        }

        /// <summary>返回条目所属的全部分类名（逗号分隔），供列表「所属」字段与悬浮提示使用。</summary>
        private string GetCategoryText(AppInfo a)
        {
            if (a == null) return string.Empty;
            List<string> cats = new List<string>();
            if (!string.IsNullOrEmpty(a.Category)) cats.Add(a.Category);
            if (a.IsFavorite) cats.Add("常用");
            if (a.IsDesktop || a.Category == QuickCenter || IsAddedToCategory(a, QuickCenter))
                if (!cats.Contains(QuickCenter)) cats.Add(QuickCenter);
            // 拖放「加入」的额外归属分类（排除 col: 集合键：集合是分类内分组，不进「所属」列）
            List<string> extra;
            if (!string.IsNullOrEmpty(a.Key) && config.ItemCategories.TryGetValue(a.Key, out extra))
            {
                for (int i = 0; i < extra.Count; i++)
                    if (!CollectionKeys.IsCollectionKey(extra[i]) && !cats.Contains(extra[i]))
                        cats.Add(extra[i]);
            }
            // 自定义分类通过 override 已体现在 a.Category
            return string.Join(", ", cats.ToArray());
        }

        private bool IsCustomCategory(string name)
        {
            return config.CustomCategories.Contains(name);
        }

        /// <summary>侧栏底部「+ 新建分类」：按当前内容模式创建软件自定义分类或资源自定义分类。</summary>
        private void OnAddCategory()
        {
            string name = InputDialog.Show("新建分类", "请输入分类名称：");
            if (string.IsNullOrEmpty(name)) return;
            name = name.Trim();
            if (name.Length == 0) return;
            bool resource = contentMode == SidebarControl.ContentMode.Website;
            List<string> cats = resource ? config.ResourceCategories : config.CustomCategories;
            if (cats.Contains(name)
                || (resource && Array.IndexOf(CategoryRules.ResourceBuiltIn, name) >= 0))
            {
                MessageBox.Show("分类「" + name + "」已存在。", "新建分类",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            cats.Add(name);
            config.Save();
            RebuildSidebar();
            OnSidebarSelect(name);
        }

        /// <summary>自定义分类右键菜单：重命名 / 删除（资源模式下作用于资源自定义分类）。</summary>
        private void OnCategoryContext(string category, Point screen)
        {
            bool custom = contentMode == SidebarControl.ContentMode.Website
                ? config.ResourceCategories.Contains(category)
                : IsCustomCategory(category);
            if (!custom) return;
            List<MenuWindow.MenuItem> items = new List<MenuWindow.MenuItem>();
            items.Add(MkMenu("重命名…", delegate { RenameCategory(category); }));
            items.Add(MkMenu("删除分类", delegate { DeleteCategory(category); }));
            MenuWindow mw = new MenuWindow(items, 160);
            mw.Location = FixLocation(screen, mw.Size);
            mw.Show(this);
        }

        /// <summary>重命名自定义分类（软件/资源按当前内容模式），并同步所有引用该分类的配置。</summary>
        private void RenameCategory(string oldName)
        {
            string name = InputDialog.Show("重命名分类", "请输入新名称：", oldName);
            if (string.IsNullOrEmpty(name)) return;
            name = name.Trim();
            if (name.Length == 0 || name == oldName) return;
            bool resource = contentMode == SidebarControl.ContentMode.Website;
            List<string> cats = resource ? config.ResourceCategories : config.CustomCategories;
            if (cats.Contains(name))
            {
                MessageBox.Show("分类「" + name + "」已存在。", "重命名分类",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            int idx = cats.IndexOf(oldName);
            if (idx >= 0) cats[idx] = name;
            // 同步所有指向旧分类的 override 与 added items
            List<string> keys = new List<string>(config.CategoryOverride.Keys);
            foreach (string k in keys)
                if (config.CategoryOverride[k] == oldName) config.CategoryOverride[k] = name;
            foreach (AddedEntry e in config.AddedItems)
                if (e.Category == oldName) e.Category = name;
            // 同步额外归属记录（拖放「加入分类」结果），避免改名后条目丢失归属
            foreach (List<string> extra in config.ItemCategories.Values)
                for (int i = 0; i < extra.Count; i++)
                    if (extra[i] == oldName) extra[i] = name;
            // 同步排序记录中的分类名
            if (config.ItemOrder.ContainsKey(oldName))
            {
                config.ItemOrder[name] = config.ItemOrder[oldName];
                config.ItemOrder.Remove(oldName);
            }
            if (!resource)
            {
                for (int i = 0; i < config.SidebarOrder.Count; i++)
                    if (config.SidebarOrder[i] == oldName) config.SidebarOrder[i] = name;
            }
            // 同步该分类下的集合：父分类与成员归属键随改名迁移；
            // 若正浏览其下某个集合，集合键一并跟随（browsing.Parent 已被 RenameParent 就地更新）
            ResourceCollection browsing = currentCollection == null ? null : FindCollectionByKey(currentCollection);
            if (resource) CollectionOps.RenameParent(config, oldName, name);
            if (browsing != null && browsing.Parent == name)
                currentCollection = CollectionKeys.Make(name, browsing.Name);
            if (currentCategory == oldName) currentCategory = name;
            config.Save();
            RebuildSidebar();
            RefreshGrid();
        }

        /// <summary>删除自定义分类（软件/资源按当前内容模式）；分类下条目回归默认归属。</summary>
        private void DeleteCategory(string category)
        {
            if (!ConfirmDialog.Show("删除分类", "确认删除分类「" + category + "」？\n该分类下的条目将回归默认分类。")) return;
            bool resource = contentMode == SidebarControl.ContentMode.Website;
            if (resource) config.ResourceCategories.Remove(category);
            else config.CustomCategories.Remove(category);
            List<string> keys = new List<string>(config.CategoryOverride.Keys);
            foreach (string k in keys)
                if (config.CategoryOverride[k] == category) config.CategoryOverride.Remove(k);
            foreach (AddedEntry e in config.AddedItems)
                if (e.Category == category)
                    e.Category = resource ? "文件" : CategoryRules.Classify(Path.GetFileName(e.Path));
            // 清理指向已删分类的额外归属记录
            foreach (List<string> extra in config.ItemCategories.Values)
                extra.Remove(category);
            config.ItemOrder.Remove(category);
            if (!resource) config.SidebarOrder.Remove(category);
            // 清理该分类下的集合定义与成员归属键，避免留下无法进入的孤儿集合
            if (resource) CollectionOps.RemoveByParent(config, category);
            config.Save();
            if (currentCategory == category) currentCategory = resource ? "全部资源" : "全部软件";
            RebuildSidebar();
            sidebar.SetActive(currentCategory);
            RefreshGrid();
        }

        private void UpdateCount(int shown, int total)
        {
            countLabel.Text = shown.ToString() + " / " + total.ToString();
        }

        // ===== 启动与菜单 =====

        private void OnLaunch(AppInfo app)
        {
            // 本地图片：应用内查看器直接浏览大图，不再调起系统图片查看器
            if (app != null && !app.IsFolder
                && MediaRules.IsImage(app.TargetPath) && File.Exists(app.TargetPath))
            {
                ShowImageViewer(app);
                return;
            }
            // 本地视频：应用内 libmpv 播放器直接播放（不调起外部播放器）
            if (app != null && !app.IsFolder
                && MediaRules.IsVideo(app.TargetPath) && File.Exists(app.TargetPath))
            {
                ShowVideoPlayer(app);
                return;
            }
            Launcher.Launch(app);
        }

        /// <summary>双击照片：打开查看器。资源模式下浏览序列为资源库全部图片（不限当前分类，各分类查看体验一致）。</summary>
        private void ShowImageViewer(AppInfo app)
        {
            List<string> imgs = MediaSequence(MediaRules.IsImage);
            if (imgs.Count == 0) { Launcher.Launch(app); return; }
            int idx = imgs.FindIndex(delegate(string p) { return string.Equals(p, app.TargetPath, StringComparison.OrdinalIgnoreCase); });
            grid.Visible = false;
            imageViewer.BringToFront();
            imageViewer.Open(imgs, idx);
        }

        /// <summary>退出查看器：恢复列表展示；未打开时为空操作，供设置页等场景直接调用。</summary>
        private void HideImageViewer()
        {
            if (!imageViewer.Visible) return;
            imageViewer.Visible = false;
            SyncContentVisibility();
            grid.Invalidate();
        }

        /// <summary>双击视频：打开内置播放器。资源模式下播放序列为资源库全部视频（不限当前分类，各分类查看体验一致）。</summary>
        private void ShowVideoPlayer(AppInfo app)
        {
            List<string> vids = MediaSequence(MediaRules.IsVideo);
            if (vids.Count == 0) { Launcher.Launch(app); return; }
            int idx = vids.FindIndex(delegate(string p) { return string.Equals(p, app.TargetPath, StringComparison.OrdinalIgnoreCase); });
            if (idx < 0)
            {
                // 浏览序列未含当前文件（极端竞态）：把双击项放到首位，避免误播其它视频
                vids.Insert(0, app.TargetPath);
                idx = 0;
            }
            grid.Visible = false;
            videoPlayer.BringToFront();
            videoPlayer.Open(vids, idx);
        }

        /// <summary>
        /// 媒体浏览序列：资源模式取资源库全部同类媒体（跨分类统一，双击来源必在其中）；
        /// 软件模式取当前列表内同类媒体（保持原有行为）。
        /// </summary>
        private List<string> MediaSequence(Func<string, bool> typeMatch)
        {
            List<string> result = new List<string>();
            if (contentMode == SidebarControl.ContentMode.Website)
            {
                foreach (AppInfo a in allApps)
                {
                    if (!IsResourceEntry(a)) continue;
                    if (!ShouldShow(a)) continue;   // 隐藏项不进浏览序列，与列表可见性一致
                    if (a.IsFolder) continue;
                    if (typeMatch(a.TargetPath) && File.Exists(a.TargetPath))
                        result.Add(a.TargetPath);
                }
            }
            else
            {
                foreach (AppInfo a in shownApps)
                {
                    if (a.IsFolder) continue;
                    if (typeMatch(a.TargetPath) && File.Exists(a.TargetPath))
                        result.Add(a.TargetPath);
                }
            }
            return result;
        }

        /// <summary>退出播放器：先恢复全屏状态，再恢复列表展示并停止播放；未打开时为空操作。</summary>
        private void HideVideoPlayer()
        {
            if (!videoPlayer.Visible) return;
            if (playerFullscreen) ExitVideoFullscreen();
            videoPlayer.Visible = false;   // OnVisibleChanged 内自动停止播放
            SyncContentVisibility();
            grid.Invalidate();
        }

        // ===== 视频播放器：全屏 =====

        /// <summary>是否处于播放器全屏（视频区铺满客户区，覆盖侧栏与顶栏）。</summary>
        private bool playerFullscreen;
        /// <summary>进入全屏前的窗体状态（还原时回到普通/最大化）。</summary>
        private FormWindowState prePlayerWindowState;

        /// <summary>Esc/返回：全屏时先退全屏，非全屏才退出播放器（一线播放器的两级退出）。</summary>
        private void OnVideoCloseRequested()
        {
            if (playerFullscreen) ExitVideoFullscreen();
            else HideVideoPlayer();
        }

        /// <summary>切换播放器全屏（双击视频区触发，占位态由控件侧拦截）。</summary>
        private void ToggleVideoFullscreen()
        {
            if (!videoPlayer.Visible) return;
            if (!playerFullscreen) EnterVideoFullscreen();
            else ExitVideoFullscreen();
        }

        /// <summary>进入全屏：无边框窗体按屏幕整体最大化盖住任务栏，视频区铺满客户区。</summary>
        private void EnterVideoFullscreen()
        {
            playerFullscreen = true;
            prePlayerWindowState = WindowState;
            MaximizedBounds = Screen.FromControl(this).Bounds;
            if (prePlayerWindowState == FormWindowState.Normal)
            {
                WindowState = FormWindowState.Maximized;
            }
            else
            {
                // 已最大化时 MaximizedBounds 变更不自动生效，重挂一次最大化铺满整屏
                WindowState = FormWindowState.Normal;
                WindowState = FormWindowState.Maximized;
            }
            LayoutControls();
            videoPlayer.BringToFront();
        }

        /// <summary>退出全屏：恢复进入前的窗体状态与常规布局。</summary>
        private void ExitVideoFullscreen()
        {
            playerFullscreen = false;
            MaximizedBounds = Screen.FromControl(this).WorkingArea;   // 恢复不遮任务栏的最大化边界
            if (prePlayerWindowState == FormWindowState.Normal)
            {
                WindowState = FormWindowState.Normal;
            }
            else
            {
                WindowState = FormWindowState.Normal;
                WindowState = FormWindowState.Maximized;
            }
            LayoutControls();
        }

        private void OnContextMenu(AppInfo app, Point screen)
        {
            List<MenuWindow.MenuItem> items = new List<MenuWindow.MenuItem>();
            bool website = contentMode == SidebarControl.ContentMode.Website;
            bool readOnly = IsReadOnlyCategory(currentCategory);

            if (readOnly)
            {
                // 与双击一致：图片/视频走应用内查看器与播放器，其余交给系统启动
                items.Add(MkMenu("打开", delegate { OnLaunch(app); }));
                items.Add(MkMenu("打开所在位置", delegate { Launcher.OpenFileLocation(app); }));
                items.Add(MkSep());
                items.Add(MkMenu("复制名称", delegate { CopyText(app.Name); }));
                items.Add(MkMenu("复制路径", delegate { CopyText(app.TargetPath); }));
                if (website && app.IsFile && IsLocalPath(app.TargetPath))
                    items.Add(MkMenu("属性…", delegate { PropertiesDialog.Show(app); }));
                items.Add(MkSep());
                // 只读全集分类：以「加入」方式把条目多归属到其它分类（原归属与「全部」中的条目均不变）
                items.Add(MkSubMenu("移动到分类…", website ? BuildResourceAddItems(app) : BuildAddCategoryItems(app, false)));
                if (!website)
                    items.Add(MkSubMenu("移动到自定义…", BuildAddCategoryItems(app, true)));
                AddDeleteMenu(items, app);
            }
            else
            {
                if (app.IsFile)
                {
                    // 文件/文件夹条目：打开自身 + 定位（媒体文件与双击一致，走应用内查看器/播放器）
                    items.Add(MkMenu(app.IsFolder ? "打开文件夹" : "打开文件", delegate { OnLaunch(app); }));
                    items.Add(MkMenu("打开所在位置", delegate { Launcher.OpenFileLocation(app); }));
                    // 用户添加的条目可改名
                    if (FindAddedEntry(app.TargetPath, currentCategory) >= 0 || IsAddedAnywhere(app.TargetPath))
                    {
                        items.Add(MkMenu("改名…", delegate { RenameAddedEntry(app); }));
                    }
                    AddDeleteMenu(items, app);   // 文档/目录条目可直接删除（进回收站）
                }
                else
                {
                    items.Add(MkMenu("打开", delegate { Launcher.Launch(app); }));
                    items.Add(MkMenu("打开安装目录", delegate { Launcher.OpenInstallDirectory(app); }));
                    items.Add(MkMenu("打开文件", delegate { Launcher.OpenFile(app); }));
                    items.Add(MkMenu("打开所在位置", delegate { Launcher.OpenFileLocation(app); }));
                }
                items.Add(MkSep());
                if (website)
                {
                    // 资源条目：仅资源分类相关操作（硬移动到资源分类），不涉及常用/软件自定义分类
                    items.Add(MkSubMenu("移动到分类…", BuildMoveResourceItems(app)));
                    // 加入集合：相簿式多归属分组，不搬文件（多选时整批加入）
                    if (CanEditCategory(currentCategory))
                        items.Add(MkSubMenu("加入集合…", BuildJoinCollectionItems(MenuTargets(app))));
                }
                else
                {
                    if (app.IsFavorite)
                        items.Add(MkMenu("从常用移除", delegate { ToggleFavorite(app, false); }));
                    else
                        items.Add(MkMenu("添加到常用", delegate { ToggleFavorite(app, true); }));
                    items.Add(MkSubMenu("移动到分类…", BuildMoveCategoryItems(app)));
                    if (InCustom(app))
                        items.Add(MkMenu("从自定义移除", delegate { RemoveFromCustom(app); }));
                    items.Add(MkSubMenu("添加到自定义…", BuildAddCustomItems(app)));
                }
                if (CanEditCategory(currentCategory))
                    items.Add(MkMenu("从「" + currentCategory + "」移除", delegate { RemoveFromCategory(app); }));
                // 集合视图内：移出仅解除集合归属，文件保留在父分类（多选时整批移出）
                if (currentCollection != null)
                {
                    List<AppInfo> removeTargets = MenuTargets(app);
                    items.Add(MkMenu("从集合移出", delegate { RemoveItemsFromCategory(removeTargets, currentCollection); }));
                }
                items.Add(MkSep());
                if (app.IsHidden)
                    items.Add(MkMenu("取消隐藏", delegate { SetHidden(app, false); }));
                else
                    items.Add(MkMenu("隐藏", delegate { SetHidden(app, true); }));
                items.Add(MkMenu("复制名称", delegate { CopyText(app.Name); }));
                items.Add(MkMenu("复制路径", delegate { CopyText(app.TargetPath); }));
                if (website && app.IsFile && IsLocalPath(app.TargetPath))
                    items.Add(MkMenu("属性…", delegate { PropertiesDialog.Show(app); }));
                if (!website && !string.IsNullOrEmpty(app.UninstallString))
                {
                    items.Add(MkSep());
                    items.Add(MkMenu("卸载", delegate { Launcher.Uninstall(app); }));
                }
            }

            MenuWindow mw = new MenuWindow(items, 230);
            mw.Location = FixLocation(screen, mw.Size);
            mw.Show(this);
        }

        /// <summary>
        /// 计算自绘菜单/弹窗的落点：优先限制在窗口客户区内（避免溢出窗口边框），
        /// 若菜单比窗口还大则退化为限制在屏幕工作区内。
        /// </summary>
        private Point FixLocation(Point p, Size sz)
        {
            Rectangle form = RectangleToScreen(ClientRectangle);
            Rectangle sc = Screen.GetWorkingArea(p);
            int minX = form.Left + 8, minY = form.Top + 8;
            int maxX = form.Right - 8, maxY = form.Bottom - 8;
            if (sz.Width > maxX - minX || sz.Height > maxY - minY)
            {
                // 窗口装不下：退回屏幕范围
                minX = Math.Max(minX, sc.Left + 8);
                minY = Math.Max(minY, sc.Top + 8);
                maxX = Math.Min(maxX, sc.Right - 8);
                maxY = Math.Min(maxY, sc.Bottom - 8);
            }
            int x = p.X, y = p.Y;
            if (x + sz.Width > maxX) x = maxX - sz.Width;
            if (y + sz.Height > maxY) y = maxY - sz.Height;
            if (x < minX) x = minX;
            if (y < minY) y = minY;
            return new Point(x, y);
        }

        /// <summary>右键操作目标：条目被多选且选中数大于 1 时返回整批选中项，否则仅当前条目。</summary>
        private List<AppInfo> MenuTargets(AppInfo clicked)
        {
            List<AppInfo> sel = grid.SelectedApps;
            if (sel.Count > 1)
            {
                for (int i = 0; i < sel.Count; i++)
                    if (object.ReferenceEquals(sel[i], clicked)) return sel;
            }
            List<AppInfo> one = new List<AppInfo>();
            one.Add(clicked);
            return one;
        }

        /// <summary>资源「属性」仅对磁盘上仍存在的文件/文件夹可用。</summary>
        private static bool IsLocalPath(string path)
        {
            return !string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path));
        }

        private static MenuWindow.MenuItem MkMenu(string text, Action action)
        {
            MenuWindow.MenuItem m = new MenuWindow.MenuItem();
            m.Text = text; m.Action = action;
            return m;
        }

        private static MenuWindow.MenuItem MkSep()
        {
            MenuWindow.MenuItem m = new MenuWindow.MenuItem();
            m.Separator = true;
            return m;
        }

        /// <summary>构造父项菜单：悬停即在右侧展开级联子菜单（父项本身不执行动作）。</summary>
        private static MenuWindow.MenuItem MkSubMenu(string text, List<MenuWindow.MenuItem> subs)
        {
            MenuWindow.MenuItem m = MkMenu(text, null);
            m.SubItems = subs;
            return m;
        }

        // ===== 分类与配置动作 =====

        private void ToggleFavorite(AppInfo app, bool on)
        {
            app.IsFavorite = on;
            if (on) { if (!config.Favorites.Contains(app.Key)) config.Favorites.Add(app.Key); }
            else { config.Favorites.Remove(app.Key); }
            config.Save();
            RefreshGrid();
        }

        private void SetHidden(AppInfo app, bool on)
        {
            app.IsHidden = on;
            if (on) { if (!config.Hidden.Contains(app.Key)) config.Hidden.Add(app.Key); }
            else { config.Hidden.Remove(app.Key); }
            config.Save();
            RefreshGrid();
        }

        private bool InCustom(AppInfo app)
        {
            string ov;
            if (config.CategoryOverride.TryGetValue(app.Key, out ov))
                return config.CustomCategories.Contains(ov);
            return false;
        }

        /// <summary>「移动到分类」的级联子项：硬移动（覆盖原归属）。</summary>
        private List<MenuWindow.MenuItem> BuildMoveCategoryItems(AppInfo app)
        {
            List<MenuWindow.MenuItem> items = new List<MenuWindow.MenuItem>();
            // 「移动到分类 ▸ 快捷中心」：把别处的条目收进快捷中心
            items.Add(MkMenu(QuickCenter, MakeSetCat(app, QuickCenter)));
            items.Add(MkSep());
            for (int i = 0; i < CategoryRules.BuiltIn.Length; i++)
            {
                string c = CategoryRules.BuiltIn[i];
                items.Add(MkMenu(c, MakeSetCat(app, c)));
            }
            if (config.CustomCategories.Count > 0) items.Add(MkSep());
            for (int i = 0; i < config.CustomCategories.Count; i++)
            {
                string c = config.CustomCategories[i];
                items.Add(MkMenu(c, MakeSetCat(app, c)));
            }
            return items;
        }

        /// <summary>资源条目「移动到分类」的级联子项：内置四大资源分类 + 自定义资源分类（硬归属）。</summary>
        private List<MenuWindow.MenuItem> BuildMoveResourceItems(AppInfo app)
        {
            List<MenuWindow.MenuItem> items = new List<MenuWindow.MenuItem>();
            for (int i = 0; i < CategoryRules.ResourceBuiltIn.Length; i++)
            {
                string c = CategoryRules.ResourceBuiltIn[i];
                MenuWindow.MenuItem m = MkMenu(c, MakeSetCat(app, c));
                m.Disabled = !CanJoinResourceCategory(app, c);   // 类型不符的分类置灰
                items.Add(m);
            }
            if (config.ResourceCategories.Count > 0) items.Add(MkSep());
            for (int i = 0; i < config.ResourceCategories.Count; i++)
            {
                string c = config.ResourceCategories[i];
                items.Add(MkMenu(c, MakeSetCat(app, c)));
            }
            return items;
        }

        /// <summary>「全部资源」条目的「加入分类」级联子项：资源分类（多归属），已加入的置灰。</summary>
        private List<MenuWindow.MenuItem> BuildResourceAddItems(AppInfo app)
        {
            List<MenuWindow.MenuItem> items = new List<MenuWindow.MenuItem>();
            for (int i = 0; i < CategoryRules.ResourceBuiltIn.Length; i++)
                items.Add(MkAddCatMenu(app, CategoryRules.ResourceBuiltIn[i]));
            if (config.ResourceCategories.Count > 0) items.Add(MkSep());
            for (int i = 0; i < config.ResourceCategories.Count; i++)
                items.Add(MkAddCatMenu(app, config.ResourceCategories[i]));
            return items;
        }

        /// <summary>
        /// 硬归属：写入分类覆盖，并清除该条目的额外归属（拖放「加入分类」的结果）。
        /// InCategory 中额外归属先于分类覆盖命中，若不清除，条目会仍留在原分类，
        /// 表现为「操作后没有任何变化」。
        /// </summary>
        private void SetHardCategory(AppInfo app, string category)
        {
            config.CategoryOverride[app.Key] = category;
            config.ItemCategories.Remove(app.Key);
            app.Category = category;
        }

        /// <summary>「移动到分类」的级联子项：硬归属（覆盖原归属）。</summary>
        private Action MakeSetCat(AppInfo app, string cat)
        {
            return delegate
            {
                SetHardCategory(app, cat);
                config.Save();
                RefreshGrid();
            };
        }

        /// <summary>
        /// 「添加到自定义」的级联子项：列出已有自定义分类（点击即「加入」，多归属，
        /// 原分类与存储位置均不变——与「添加到常用」语义一致），末尾附「新建自定义分类…」
        /// 走输入框新建。条目已在的自定义分类置灰，避免无效点击。
        /// </summary>
        private List<MenuWindow.MenuItem> BuildAddCustomItems(AppInfo app)
        {
            List<MenuWindow.MenuItem> items = new List<MenuWindow.MenuItem>();
            for (int i = 0; i < config.CustomCategories.Count; i++)
                items.Add(MkAddCatMenu(app, config.CustomCategories[i]));
            if (items.Count == 0) items.Add(MkDisabledMenu("（暂无自定义分类）"));
            items.Add(MkSep());
            items.Add(MkMenu("新建自定义分类…", delegate { AddToCustom(app); }));
            return items;
        }

        /// <summary>
        /// 「全部*」条目右键的「加入分类」级联子项：选中即把条目「加入」该分类（多归属），
        /// 原归属与「全部」中的条目均不变。customOnly=true 时仅列出自定义分类。
        /// </summary>
        private List<MenuWindow.MenuItem> BuildAddCategoryItems(AppInfo app, bool customOnly)
        {
            List<MenuWindow.MenuItem> items = new List<MenuWindow.MenuItem>();
            if (customOnly)
            {
                for (int i = 0; i < config.CustomCategories.Count; i++)
                    items.Add(MkAddCatMenu(app, config.CustomCategories[i]));
                if (items.Count == 0) items.Add(MkDisabledMenu("（暂无自定义分类）"));
            }
            else
            {
                items.Add(MkAddCatMenu(app, QuickCenter));
                items.Add(MkSep());
                for (int i = 0; i < CategoryRules.BuiltIn.Length; i++)
                    items.Add(MkAddCatMenu(app, CategoryRules.BuiltIn[i]));
            }
            return items;
        }

        /// <summary>构造「加入分类」菜单项：条目已属于该分类时置灰，避免点击无效果。</summary>
        private MenuWindow.MenuItem MkAddCatMenu(AppInfo app, string category)
        {
            MenuWindow.MenuItem m = MkMenu(category, delegate { AddItemToCategory(app, category); });
            m.Disabled = InCategory(app, category) || !CanJoinResourceCategory(app, category);
            return m;
        }

        /// <summary>
        /// 资源条目能否归入该内置分类：本地文件按分类类型约束（照片只收图片……）。
        /// </summary>
        private bool CanJoinResourceCategory(AppInfo app, string category)
        {
            if (app == null) return false;
            // 集合成员须属于父分类（搜索跨分类结果不可加入），且类型符合父分类约束
            if (!InCategory(app, category)) return false;
            return MediaRules.MatchCategoryExt(category, app.TargetPath);
        }

        /// <summary>构造置灰（不可点）菜单项，用作空态提示。</summary>
        private static MenuWindow.MenuItem MkDisabledMenu(string text)
        {
            MenuWindow.MenuItem m = MkMenu(text, null);
            m.Disabled = true;
            return m;
        }

        /// <summary>「新建自定义分类…」：新建后把条目「加入」该分类（多归属，保留原分类）。</summary>
        private void AddToCustom(AppInfo app)
        {
            string name = InputDialog.Show("添加到自定义", "请输入自定义分类名称：");
            if (string.IsNullOrEmpty(name)) return;
            name = name.Trim();
            if (!config.CustomCategories.Contains(name))
            {
                config.CustomCategories.Add(name);
            }
            AddItemToCategory(app, name);
            RebuildSidebar();
            RefreshGrid();
        }

        private void RemoveFromCustom(AppInfo app)
        {
            if (!ConfirmDialog.Show("从自定义移除", "确认将此应用从自定义分类移除？")) return;
            string ov;
            if (config.CategoryOverride.TryGetValue(app.Key, out ov)
                && config.CustomCategories.Contains(ov))
            {
                config.CategoryOverride.Remove(app.Key);
                app.Category = CategoryRules.Classify(app.Name);
                config.Save();
                RefreshGrid();
            }
        }

        private void CopyText(string text)
        {
            try { Clipboard.SetText(text ?? string.Empty); } catch { }
        }

        // ===== 添加 / 移除条目 =====

        /// <summary>
        /// 点击「+ 添加」：软件模式弹出"添加软件/文件/文件夹"菜单；
        /// 资源模式按当前分类自适应——照片/视频/音乐等分类分别添加对应类型。
        /// </summary>
        private void OnAdd(object sender, EventArgs e)
        {
            List<MenuWindow.MenuItem> items = new List<MenuWindow.MenuItem>();
            if (contentMode == SidebarControl.ContentMode.Website)
            {
                string typeName = MediaRules.ResourceCategoryTypeName(currentCategory);
                items.Add(MkMenu("添加" + typeName + "…", delegate { PickFiles(); }));
                items.Add(MkMenu("添加文件夹…", delegate { PickFolder(); }));
            }
            else
            {
                items.Add(MkMenu("添加软件…", delegate { PickSoftware(); }));
                items.Add(MkMenu("添加文件…", delegate { PickFiles(); }));
                items.Add(MkMenu("添加文件夹…", delegate { PickFolder(); }));
            }
            MenuWindow mw = new MenuWindow(items, 180);
            mw.Location = FixLocation(addBtn.PointToScreen(new Point(0, addBtn.Height)), mw.Size);
            mw.Show(this);
        }

        /// <summary>添加软件（.exe）：只登记不搬运。</summary>
        private void PickSoftware()
        {
            // R7：优先从全量已扫描应用列表中勾选添加，避免手动浏览目录
            if (PickFromAppList()) return;
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "添加软件到「" + currentCategory + "」";
                dlg.Multiselect = true;
                dlg.Filter = "程序 (*.exe)|*.exe|所有文件 (*.*)|*.*";
                if (dlg.ShowDialog(this) == DialogResult.OK) AddPaths(dlg.FileNames, currentCategory);
            }
        }

        /// <summary>
        /// 弹出全量应用列表供勾选添加。返回 true 表示用户操作完毕（无论是否选中），
        /// false 表示用户取消并希望回退到文件浏览器方式。
        /// </summary>
        private bool PickFromAppList()
        {
            Form f = new Form();
            f.FormBorderStyle = FormBorderStyle.None;
            f.StartPosition = FormStartPosition.CenterParent;
            f.ClientSize = new Size(480, 560);
            f.BackColor = Theme.Background;
            f.ShowInTaskbar = false;
            ApplyRoundedCorners(f);

            Label title = new Label();
            title.Text = "选择要添加的软件";
            title.Font = Theme.FontOf(12F, FontStyle.Bold);
            title.ForeColor = Theme.TextPrimary;
            title.Location = new Point(20, 16);
            title.AutoSize = true;
            f.Controls.Add(title);

            Label hint = new Label();
            hint.Text = "已收录的应用（可多选）：";
            hint.Font = Theme.FontOf(9F);
            hint.ForeColor = Theme.TextSecondary;
            hint.Location = new Point(20, 48);
            hint.AutoSize = true;
            f.Controls.Add(hint);

            CheckedListBox list = new CheckedListBox();
            list.BorderStyle = BorderStyle.None;
            list.BackColor = Theme.Card;
            list.ForeColor = Theme.TextPrimary;
            list.Font = Theme.FontOf(9.5F);
            list.CheckOnClick = true;
            list.Location = new Point(20, 74);
            list.Size = new Size(440, 420);
            // 列出所有可启动的条目，排除已添加的
            HashSet<string> added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AddedEntry ae in config.AddedItems) added.Add(ae.Path);
            List<AppInfo> candidates = new List<AppInfo>();
            foreach (AppInfo a in allApps)
            {
                if (string.IsNullOrEmpty(a.TargetPath) && string.IsNullOrEmpty(a.ShortcutPath)) continue;
                if (added.Contains(a.TargetPath)) continue;
                candidates.Add(a);
            }
            candidates.Sort((x, y) => string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase));
            foreach (AppInfo a in candidates) list.Items.Add(a);
            list.DisplayMember = "Name";
            f.Controls.Add(list);

            RoundButton ok = new RoundButton();
            ok.Text = "添加 (" + 0 + ")";
            ok.Accent = true;
            ok.Size = new Size(100, 32);
            ok.Location = new Point(f.Width - 220, f.Height - 52);
            f.Controls.Add(ok);
            RoundButton cancel = new RoundButton();
            cancel.Text = "取消";
            cancel.Accent = false;
            cancel.Size = new Size(100, 32);
            cancel.Location = new Point(f.Width - 110, f.Height - 52);
            f.Controls.Add(cancel);

            list.ItemCheck += delegate(object s, ItemCheckEventArgs e)
            {
                f.BeginInvoke((Action)delegate
                {
                    ok.Text = "添加 (" + list.CheckedItems.Count + ")";
                });
            };

            ok.Click += delegate
            {
                List<string> paths = new List<string>();
                foreach (AppInfo a in list.CheckedItems)
                {
                    string p = !string.IsNullOrEmpty(a.TargetPath) ? a.TargetPath : a.ShortcutPath;
                    if (!string.IsNullOrEmpty(p)) paths.Add(p);
                }
                if (paths.Count > 0)
                    AddPaths(paths.ToArray(), currentCategory);
                f.Close();
            };
            cancel.Click += delegate { f.Close(); };

            ConfirmDialog.ShowModal(f);   // 模态 + 遮罩层
            f.Dispose();
            return true;   // 无论是否选中，已提供列表选择，不再回退文件浏览器
        }

        /// <summary>多选文件加入当前分类：资源模式下按分类限定文件类型（对话框过滤 + 入库前二次校验）。</summary>
        private void PickFiles()
        {
            bool resource = contentMode == SidebarControl.ContentMode.Website;
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                if (resource)
                {
                    dlg.Title = "添加" + MediaRules.ResourceCategoryTypeName(currentCategory) + "到「" + currentCategory + "」";
                    dlg.Filter = MediaRules.ResourceCategoryFilter(currentCategory);
                }
                else
                {
                    dlg.Title = "添加文件到「" + currentCategory + "」";
                    dlg.Filter = "所有文件 (*.*)|*.*";
                }
                dlg.Multiselect = true;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    AddPaths(dlg.FileNames, currentCategory, ActiveCollectionKey);   // 集合视图内：入库后自动加入集合
            }
        }

        /// <summary>选择文件夹加入当前分类。</summary>
        private void PickFolder()
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "添加文件夹到「" + currentCategory + "」";
                dlg.ShowNewFolderButton = false;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    // 集合视图内：入库后自动加入当前集合
                    AddPaths(new string[] { dlg.SelectedPath }, currentCategory, ActiveCollectionKey);
                }
            }
        }

        /// <summary>
        /// 把路径批量写入 AddedItems（按 路径+分类 去重），并按当前模式搬运入库：
        /// 软件模式进入软件存储位置；资源模式进入资源存储位置（按分类建子目录），原文件移动后即不保留。
        /// collectionKey 非空时入库后把新条目加入该集合（多归属）。
        /// 搬运为异步执行，状态栏显示每项独立进度。
        /// </summary>
        private void AddPaths(string[] paths, string category, string collectionKey = null)
        {
            if (paths == null || paths.Length == 0) return;
            if (!CanEditCategory(category)) return;   // 只读分类不接受收录
            bool resource = contentMode == SidebarControl.ContentMode.Website;
            if (resource)
            {
                // 资源内置分类按类型过滤：选文件对话框已限定类型，此处兜底拦截拖拽等其它入口
                List<string> ok = new List<string>();
                int skipped = 0;
                for (int i = 0; i < paths.Length; i++)
                {
                    string p = paths[i];
                    if (Directory.Exists(p) || MediaRules.MatchCategoryExt(category, p)) ok.Add(p);
                    else skipped++;
                }
                if (skipped > 0 && ok.Count == 0)
                {
                    MessageBox.Show("所选文件与「" + category + "」的类型不符，未添加。", "添加资源",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (skipped > 0)
                {
                    MessageBox.Show("有 " + skipped + " 个文件与「" + category + "」的类型不符，已跳过。", "添加资源",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                if (ok.Count == 0) return;
                paths = ok.ToArray();
            }
            MoveToCategoryAsync(paths, category, resource, collectionKey);
        }

        /// <summary>
        /// 异步把路径搬入目标分类：逐项移动并更新状态栏进度，完成后重扫刷新。
        /// 每项独立显示进度详情。collectionKey 非空时新登记条目同步加入该集合（多归属）。
        /// </summary>
        private void MoveToCategoryAsync(string[] paths, string category, bool resource, string collectionKey = null)
        {
            string[] valid = paths.Where(p => !string.IsNullOrEmpty(p)
                && (File.Exists(p) || Directory.Exists(p))).ToArray();
            if (valid.Length == 0) return;
            int total = valid.Length;
            ShowStatusBar("正在移入「" + category + "」…", 0, total);
            List<string> storedList = new List<string>();
            ThreadPool.QueueUserWorkItem(delegate
            {
                for (int i = 0; i < total; i++)
                {
                    string p = valid[i];
                    string name = Path.GetFileName(p);
                    UpdateStatus(i, total, name);
                    string stored = StoreIntoCategory(p, category, resource);
                    lock (storedList) storedList.Add(stored);
                }
                BeginInvoke((Action)delegate
                {
                    int added = 0;
                    bool changed = false;   // 是否产生配置变化（新增登记或补集合归属）
                    foreach (string stored in storedList)
                    {
                        if (FindAddedEntry(stored, category) < 0)
                        {
                            AddedEntry e = new AddedEntry();
                            e.Path = stored;
                            e.Category = category;
                            e.IsFolder = Directory.Exists(stored);
                            e.IsResource = resource;
                            config.AddedItems.Add(e);
                            added++;
                        }
                        // 已登记过的条目重复添加时仍要补集合归属（否则表现为「加入集合无效果」）；
                        // 与右键/拖拽入口同口径：类型不符父分类约束的（如文件夹进照片集）不成为成员
                        if (!string.IsNullOrEmpty(collectionKey)
                            && MediaRules.MatchCategoryExt(category, stored)
                            && CollectionOps.AddMember(config, "add:" + stored, collectionKey))
                            changed = true;
                    }
                    if (added > 0)
                    {
                        config.Save();
                        SwapApps(scanner.ScanAll());
                        RefreshGrid();
                        StartIconLoad();
                    }
                    else if (changed)
                    {
                        // 全部条目已登记、仅补集合归属：无需重扫，落盘并刷新集合侧边栏/网格即可
                        config.Save();
                        RefreshGrid();
                    }
                    HideStatusBar();
                });
            });
        }

        /// <summary>
        /// 入库规则：软件模式下文件夹/文件收进配置的统一存储位置（.exe 只登记不搬）；
        /// 资源模式收进「资源存储位置」下按分类建立的子目录，所有文件（含 .exe）均实体移动。
        /// 未配置时回退 ManageRoot（H:\Manage）。已在目标目录内或搬运失败时返回原路径，保证条目不丢失。
        /// </summary>
        private string StoreIntoCategory(string path, string category, bool resource)
        {
            try
            {
                bool isFolder = Directory.Exists(path);
                if (!resource && !isFolder && IsSoftwareFile(path)) return path; // 软件分类下的 .exe 只登记不搬
                string destDir = resource
                    ? ResourceStoreOf(category)
                    : (isFolder ? FolderStoreOf() : FileStoreOf());
                if (IsUnder(destDir, path)) return path;                 // 已在目标目录内，避免二次搬运
                if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);
                string name = Path.GetFileName(path);
                string dest = UniqueDest(destDir, name);
                if (isFolder) DesktopOrganizer.MoveDirectory(path, dest); else File.Move(path, dest);
                return dest;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("分类入库失败 " + category + " " + path + ": " + ex.Message);
                return path;
            }
        }

        /// <summary>目录 dir 下不重名的落点路径：重名时追加 _1、_2…（与入库规则一致）。internal 供自测验证。</summary>
        internal static string UniqueDest(string dir, string name)
        {
            string dest = Path.Combine(dir, name);
            int i = 1;
            while (File.Exists(dest) || Directory.Exists(dest))
            {
                dest = Path.Combine(dir,
                    Path.GetFileNameWithoutExtension(name) + "_" + i + Path.GetExtension(name));
                i++;
            }
            return dest;
        }

        /// <summary>统一存储位置：文件夹一律收入配置的「文件夹存储位置」，不再按分类细分二级目录。</summary>
        private string FolderStoreOf()
        {
            if (!string.IsNullOrEmpty(config.FolderStorePath))
            {
                string p = DesktopOrganizer.NormalizeRoot(config.FolderStorePath);
                try { Directory.CreateDirectory(p); return p; }
                catch { }
            }
            return ResolveManageRoot();
        }

        /// <summary>统一存储位置：文件一律收入配置的「文件存储位置」，不再按分类细分二级目录。</summary>
        private string FileStoreOf()
        {
            if (!string.IsNullOrEmpty(config.FileStorePath))
            {
                string p = DesktopOrganizer.NormalizeRoot(config.FileStorePath);
                try { Directory.CreateDirectory(p); return p; }
                catch { }
            }
            return Path.Combine(ResolveManageRoot(), "Files");
        }

        /// <summary>资源存储根目录：配置了「资源存储位置」则用之，否则回退 ManageRoot\Resources。</summary>
        private string ResourceStoreRoot()
        {
            if (!string.IsNullOrEmpty(config.ResourceStorePath))
            {
                string p = DesktopOrganizer.NormalizeRoot(config.ResourceStorePath);
                try { Directory.CreateDirectory(p); return p; }
                catch { }
            }
            string root = Path.Combine(ResolveManageRoot(), "Resources");
            try { Directory.CreateDirectory(root); }
            catch { }
            return root;
        }

        /// <summary>资源分类的存储目录：资源存储根目录下的分类子目录（分类名做非法字符替换）。</summary>
        private string ResourceStoreOf(string category)
        {
            string dir = Path.Combine(ResourceStoreRoot(), SafeDirName(category));
            try { Directory.CreateDirectory(dir); }
            catch { }
            return dir;
        }

        /// <summary>把分类名转换为合法目录名：替换非法字符并去除首尾空格/句点。</summary>
        private static string SafeDirName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "其他";
            char[] invalid = Path.GetInvalidFileNameChars();
            char[] chars = name.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                for (int j = 0; j < invalid.Length; j++)
                    if (chars[i] == invalid[j]) { chars[i] = '_'; break; }
            }
            string s = new string(chars).Trim().Trim('.');
            return s.Length == 0 ? "其他" : s;
        }

        /// <summary>解析收纳根路径：配置 ManageRootPath 可用则用之，否则默认 H:\Manage，再回退本地 QuickCenter。</summary>
        private string ResolveManageRoot()
        {
            if (!string.IsNullOrEmpty(config.ManageRootPath))
            {
                string p = DesktopOrganizer.NormalizeRoot(config.ManageRootPath);
                try { Directory.CreateDirectory(p); return p; }
                catch { }
            }
            // 默认 H:\Manage（R20）
            string hdrive = @"H:\Manage";
            try
            {
                if (Directory.Exists(@"H:\") || DriveInfo.GetDrives().Any(d => d.Name.StartsWith("H:", StringComparison.OrdinalIgnoreCase)))
                {
                    Directory.CreateDirectory(hdrive);
                    config.ManageRootPath = hdrive;
                    return hdrive;
                }
            }
            catch { }
            return DesktopOrganizer.QuickCenterDir;
        }

        // ===== 状态栏 =====

        private void ShowStatusBar(string text, int value, int total)
        {
            statusBar.Visible = true;
            statusLabel.Text = text;
            statusProgress.Maximum = Math.Max(1, total);
            statusProgress.Value = Math.Min(value, statusProgress.Maximum);
            statusDetail.Text = string.Empty;
            LayoutControls();
        }

        private void UpdateStatus(int index, int total, string currentName)
        {
            if (statusBar.InvokeRequired)
            {
                statusBar.BeginInvoke((Action)delegate { UpdateStatus(index, total, currentName); });
                return;
            }
            statusProgress.Value = Math.Min(index + 1, statusProgress.Maximum);
            statusDetail.Text = (index + 1) + "/" + total + "  " + currentName;
        }

        private void HideStatusBar()
        {
            statusBar.Visible = false;
            LayoutControls();
        }

        /// <summary>是否为软件可执行文件（.exe）。</summary>
        private static bool IsSoftwareFile(string path)
        {
            return string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>判断 path 是否位于目录 dir 之下（含自身）。internal 供自测验证。</summary>
        internal static bool IsUnder(string dir, string path)
        {
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(path)) return false;
            string d = dir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string p = path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return p.StartsWith(d, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>文件/文件夹拖放到侧栏某分类：切到该分类并按当前模式入库（异步 + 状态栏进度）。</summary>
        private void OnFilesDroppedToCategory(string category, string[] paths)
        {
            if (paths == null || paths.Length == 0) return;
            if (!CanEditCategory(category)) return;
            currentCategory = category;
            sidebar.SetActive(currentCategory);
            addBtn.Visible = CanEditCategory(currentCategory);
            LayoutControls();
            AddPaths(paths, category);
        }

        /// <summary>
        /// 从当前分类移除条目：拖放「加入」的额外归属仅移出该分类（原分类与存储位置不变）；
        /// 用户手动添加项删除其 AddedItems 记录（不动磁盘文件）；
        /// 其余来源条目按隐藏处理，可通过「取消隐藏」恢复。
        /// </summary>
        private void RemoveFromCategory(AppInfo app)
        {
            if (app == null) return;
            // 额外归属（拖放加入）：仅移出该分类
            if (IsInExtraCategory(app, currentCategory)) { RemoveItemFromCategory(app, currentCategory); return; }
            int idx = FindAddedEntry(app.TargetPath, currentCategory);
            if (idx >= 0)
            {
                if (!ConfirmDialog.Show("移除条目", "确认从「" + currentCategory + "」移除该条目？\n（仅移除记录，不删除磁盘文件）")) return;
                config.AddedItems.RemoveAt(idx);
                config.Save();
                SwapApps(scanner.ScanAll());
                RefreshGrid();
                StartIconLoad();
                return;
            }
            if (!ConfirmDialog.Show("移除", "确认从「" + currentCategory + "」移除该应用？")) return;
            SetHidden(app, true);
        }

        /// <summary>查找指定路径在给定分类下的 AddedItems 下标，未命中返回 -1。</summary>
        private int FindAddedEntry(string path, string category)
        {
            if (string.IsNullOrEmpty(path)) return -1;
            for (int i = 0; i < config.AddedItems.Count; i++)
            {
                AddedEntry e = config.AddedItems[i];
                if (string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)
                    && e.Category == category) return i;
            }
            return -1;
        }

        /// <summary>路径是否在任意分类下被用户添加过。</summary>
        private bool IsAddedAnywhere(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            for (int i = 0; i < config.AddedItems.Count; i++)
                if (string.Equals(config.AddedItems[i].Path, path, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>改名：修改用户添加条目的显示名。</summary>
        private void RenameAddedEntry(AppInfo app)
        {
            if (app == null) return;
            string newName = InputDialog.Show("改名", "请输入新名称：", app.Name);
            if (newName == null) return;
            newName = newName.Trim();
            if (newName.Length == 0) return;
            bool changed = false;
            for (int i = 0; i < config.AddedItems.Count; i++)
            {
                AddedEntry e = config.AddedItems[i];
                if (string.Equals(e.Path, app.TargetPath, StringComparison.OrdinalIgnoreCase))
                {
                    e.Name = newName;
                    changed = true;
                }
            }
            if (!changed) return;
            app.Name = newName;
            config.Save();
            SwapApps(scanner.ScanAll());
            RefreshGrid();
            StartIconLoad();
        }

        /// <summary>
        /// 列表行右侧第二个按钮：进入集合后为「移除」（仅从当前集合移出，分类与「全部资源」中的归属不变）；
        /// 分类、全部资源与搜索态为「删除」（移除收录记录，保持原有删除功能）。
        /// </summary>
        private void OnGridRowRemove(AppInfo app)
        {
            if (app == null) return;
            if (contentMode != SidebarControl.ContentMode.Website
                || SearchActive || currentCollection == null)
            {
                DeleteAddedEntry(app);
                return;
            }
            RemoveItemFromCategory(app, currentCollection);
        }

        /// <summary>删除：移除用户添加的条目记录（不动磁盘文件）。</summary>
        private void DeleteAddedEntry(AppInfo app)
        {
            if (app == null) return;
            if (!ConfirmDialog.Show("删除条目", "确认删除「" + app.Name + "」？\n（仅移除记录，不删除磁盘文件）")) return;
            bool removed = false;
            for (int i = config.AddedItems.Count - 1; i >= 0; i--)
            {
                AddedEntry e = config.AddedItems[i];
                if (string.Equals(e.Path, app.TargetPath, StringComparison.OrdinalIgnoreCase))
                {
                    config.AddedItems.RemoveAt(i);
                    removed = true;
                }
            }
            if (!removed) return;
            config.Save();
            SwapApps(scanner.ScanAll());
            RefreshGrid();
            StartIconLoad();
        }

        // ===== 桌面收纳 =====

        private void OnOrganize(object sender, EventArgs e)
        {
            List<MenuWindow.MenuItem> items = new List<MenuWindow.MenuItem>();
            items.Add(MkMenu("一键收纳桌面（" + (config.OrganizeCopy ? "副本" : "搬家") + "）", delegate { DoOrganize(); }));
            if (organizer.HasBackup())
            {
                items.Add(MkMenu("备份清单（" + config.DesktopBackup.Count + " 项）…", delegate { ShowBackupListDialog(); }));
                items.Add(MkMenu("更新备份", delegate { DoUpdateBackup(); }));
                items.Add(MkMenu("还原全部", delegate { DoRestore(); }));
            }
            items.Add(MkSep());
            items.Add(MkMenu((config.OrganizeCopy ? "√ " : "   ") + "副本模式（保留桌面原件）", delegate { ToggleOrganizeCopy(); }));
            items.Add(MkMenu((config.OrganizeShortcut ? "√ " : "   ") + "收纳范围：快捷方式", delegate { ToggleOrganizeScope(0); }));
            items.Add(MkMenu((config.OrganizeFile ? "√ " : "   ") + "收纳范围：文件", delegate { ToggleOrganizeScope(1); }));
            items.Add(MkMenu((config.OrganizeDir ? "√ " : "   ") + "收纳范围：文件夹", delegate { ToggleOrganizeScope(2); }));
            items.Add(MkSep());
            items.Add(MkMenu("导出备份包…", delegate { ExportBackup(); }));
            items.Add(MkMenu("导入备份包…", delegate { ImportBackup(); }));
            MenuWindow mw = new MenuWindow(items, 250);
            mw.Location = FixLocation(organizeBtn.PointToScreen(new Point(0, organizeBtn.Height)), mw.Size);
            mw.Show(this);
        }

        /// <summary>切换收纳模式（搬家 ⇄ 副本）。</summary>
        private void ToggleOrganizeCopy()
        {
            config.OrganizeCopy = !config.OrganizeCopy;
            config.Save();
        }

        /// <summary>切换收纳范围（快捷方式/文件/文件夹），至少保留一项。</summary>
        private void ToggleOrganizeScope(int which)
        {
            if (which == 0) config.OrganizeShortcut = !config.OrganizeShortcut;
            else if (which == 1) config.OrganizeFile = !config.OrganizeFile;
            else config.OrganizeDir = !config.OrganizeDir;

            if (!config.OrganizeShortcut && !config.OrganizeFile && !config.OrganizeDir)
            {
                if (which == 0) config.OrganizeShortcut = true;
                else if (which == 1) config.OrganizeFile = true;
                else config.OrganizeDir = true;
                MessageBox.Show("至少保留一项收纳范围。", "整理桌面",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            config.Save();
        }

        private void DoUpdateBackup()
        {
            int n = 0;
            try { n = organizer.UpdateBackup(); }
            catch { }
            ReloadLibrary();
            MessageBox.Show("备份已更新，当前 " + n + " 项。", "整理桌面",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void DoOrganize()
        {
            int n = 0;
            try { n = organizer.TidyDesktop(Application.ExecutablePath); }
            catch { }
            ReloadLibrary();
            MessageBox.Show("已收纳 " + n + " 个桌面条目（" + (config.OrganizeCopy ? "副本模式" : "搬家模式") + "）。",
                "整理桌面", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void DoRestore()
        {
            int n = 0;
            try { n = organizer.RestoreAll(); }
            catch { }
            ReloadLibrary();
            MessageBox.Show("已还原 " + n + " 个条目到桌面。", "整理桌面",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>桌面备份清单：列出条目，支持单条还原/移除、全部还原。</summary>
        private void ShowBackupListDialog()
        {
            using (Form f = new Form())
            {
                f.FormBorderStyle = FormBorderStyle.None;
                f.StartPosition = FormStartPosition.CenterParent;
                f.ClientSize = new Size(560, 440);
                f.BackColor = Theme.Background;
                f.ShowInTaskbar = false;
                ApplyRoundedCorners(f);

                Label title = new Label();
                title.Text = "桌面备份清单";
                title.Font = Theme.FontOf(13F, FontStyle.Bold);
                title.ForeColor = Theme.TextPrimary;
                title.BackColor = Color.Transparent;
                title.Location = new Point(24, 18);
                title.AutoSize = true;
                f.Controls.Add(title);

                Label desc = new Label();
                desc.Text = "选择一项后可单独还原或移除；「还原」对搬家项移回桌面、对副本项清理备份副本。";
                desc.Font = Theme.FontOf(9F);
                desc.ForeColor = Theme.TextSecondary;
                desc.BackColor = Color.Transparent;
                desc.Location = new Point(24, 48);
                desc.AutoSize = true;
                f.Controls.Add(desc);

                ListBox list = new ListBox();
                list.BorderStyle = BorderStyle.None;
                list.Font = Theme.FontOf(9.5F);
                list.BackColor = Color.White;
                list.IntegralHeight = false;
                list.Location = new Point(24, 78);
                list.Size = new Size(f.ClientSize.Width - 48, f.ClientSize.Height - 158);
                f.Controls.Add(list);

                Action refresh = null;
                refresh = delegate
                {
                    int sel = list.SelectedIndex;
                    list.BeginUpdate();
                    list.Items.Clear();
                    foreach (DesktopBackupEntry e in config.DesktopBackup)
                        list.Items.Add(Path.GetFileName(e.CurrentPath) + (e.IsCopy ? "（副本）" : ""));
                    list.EndUpdate();
                    if (list.Items.Count > 0)
                        list.SelectedIndex = Math.Min(Math.Max(sel, 0), list.Items.Count - 1);
                };
                refresh();

                RoundButton restoreSel = new RoundButton();
                restoreSel.Text = "还原选中";
                restoreSel.Accent = true;
                restoreSel.Size = new Size(96, 32);
                restoreSel.Location = new Point(f.ClientSize.Width - 424, f.ClientSize.Height - 56);
                restoreSel.Click += delegate
                {
                    int idx = list.SelectedIndex;
                    if (idx < 0 || idx >= config.DesktopBackup.Count) return;
                    DesktopBackupEntry e = config.DesktopBackup[idx];
                    if (organizer.RestoreOne(e)) organizer.RemoveEntry(e);
                    else MessageBox.Show("还原失败：备份文件已不存在，已保留该清单项。", "还原",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    ReloadLibrary();
                    refresh();
                };
                f.Controls.Add(restoreSel);

                RoundButton removeSel = new RoundButton();
                removeSel.Text = "移除选中";
                removeSel.Accent = false;
                removeSel.Size = new Size(96, 32);
                removeSel.Location = new Point(f.ClientSize.Width - 320, f.ClientSize.Height - 56);
                removeSel.Click += delegate
                {
                    int idx = list.SelectedIndex;
                    if (idx < 0 || idx >= config.DesktopBackup.Count) return;
                    if (!ConfirmDialog.Show("移除清单项", "仅从清单移除记录，不会删除备份文件。确认？")) return;
                    organizer.RemoveEntry(config.DesktopBackup[idx]);
                    refresh();
                };
                f.Controls.Add(removeSel);

                RoundButton restoreAll = new RoundButton();
                restoreAll.Text = "全部还原";
                restoreAll.Accent = false;
                restoreAll.Size = new Size(96, 32);
                restoreAll.Location = new Point(f.ClientSize.Width - 216, f.ClientSize.Height - 56);
                restoreAll.Click += delegate
                {
                    int n = 0;
                    try { n = organizer.RestoreAll(); } catch { }
                    ReloadLibrary();
                    refresh();
                    MessageBox.Show("已还原 " + n + " 个条目到桌面。", "还原全部",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                };
                f.Controls.Add(restoreAll);

                RoundButton close = new RoundButton();
                close.Text = "关闭";
                close.Accent = false;
                close.Size = new Size(96, 32);
                close.Location = new Point(f.ClientSize.Width - 112, f.ClientSize.Height - 56);
                close.Click += delegate { f.Close(); };
                f.Controls.Add(close);

                ConfirmDialog.ShowModal(f);   // 模态 + 遮罩层
            }
        }

        /// <summary>打开「布局备份」管理窗口；恢复后重载配置与列表。</summary>
        private void ShowLayoutBackups()
        {
            using (BackupForm f = new BackupForm())
            {
                ConfirmDialog.ShowModal(f);
                if (f.Restored) ReloadAfterRestore();
            }
        }

        /// <summary>布局恢复后：重载配置、重建扫描器与列表，并把设置页状态同步到恢复后的值。</summary>
        private void ReloadAfterRestore()
        {
            config = AppConfig.Load();
            scanner = new Scanner(config);
            organizer = new DesktopOrganizer(config);
            grid.GridMode = GetCategoryGridMode(currentCategory);
            settingsPage.Bind(config.ShowHidden, config.ShowAllInstalled, config.AutoScan,
                config.CloseToTray, FolderStoreOf(), FileStoreOf(), ResourceStoreRoot());
            StartAutoScan();
            RebuildSidebar();
            StartScan();
            Invalidate();
        }

        /// <summary>导出备份包（.ambak，仅记录与设置，不含物理文件）。</summary>
        private void ExportBackup()
        {
            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Title = "导出备份包";
                dlg.Filter = "应用中心备份包 (*.ambak)|*.ambak";
                dlg.FileName = "AppManagerBackup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".ambak";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    config.ExportTo(dlg.FileName);
                    MessageBox.Show("已导出备份包：\n" + dlg.FileName, "导出备份包",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("导出失败：" + ex.Message, "导出备份包",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        /// <summary>导入备份包（.ambak）：校验通过后整库覆盖；覆盖前自动备份当前配置。</summary>
        private void ImportBackup()
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "导入备份包";
                dlg.Filter = "应用中心备份包 (*.ambak)|*.ambak|所有文件 (*.*)|*.*";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                AppConfig imported = AppConfig.ImportFrom(dlg.FileName);
                if (imported == null)
                {
                    MessageBox.Show("备份包无效或已损坏，已中止导入。", "导入备份包",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (!ConfirmDialog.Show("导入备份包",
                    "导入将整库覆盖当前配置（分类、收录条目、网址、排序、桌面备份清单）。\n" +
                    "导入前会自动备份当前配置。确认继续？")) return;

                string bak = config.BackupSnapshot();
                if (bak == null)
                {
                    MessageBox.Show("无法备份当前配置，已中止导入。", "导入备份包",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                imported.Save();
                config = imported;
                scanner = new Scanner(config);
                organizer = new DesktopOrganizer(config);
                currentCategory = "全部软件";
                RebuildSidebar();
                sidebar.SetActive(currentCategory);
                viewToggle.Checked = GetCategoryGridMode(currentCategory);
                grid.GridMode = GetCategoryGridMode(currentCategory);
                addBtn.Visible = CanEditCategory(currentCategory);
                LayoutControls();
                ReloadLibrary();
                MessageBox.Show("导入完成，已还原至备份包状态。\n原配置已备份至：\n" + bak, "导入备份包",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        /// <summary>换列表后统一刷新：重扫 → 刷新网格 → 加载图标。</summary>
        private void ReloadLibrary()
        {
            SwapApps(scanner.ScanAll());
            RefreshGrid();
            StartIconLoad();
        }

        // ===== 设置页 =====

        /// <summary>进入设置页：隐藏主内容区控件，设置页铺满标题行以下区域。</summary>
        private void OpenSettings()
        {
            if (settingsOpen) return;
            settingsOpen = true;
            HideImageViewer();   // 查看器/播放器属于列表区域，进入设置页前先退出
            HideVideoPlayer();
            settingsPage.Bind(config.ShowHidden, config.ShowAllInstalled, config.AutoScan,
                config.CloseToTray, FolderStoreOf(), FileStoreOf(), ResourceStoreRoot());
            SetMainContentVisible(false);
            settingsBtn.Visible = false;
            settingsPage.Visible = true;
            settingsPage.BringToFront();
            LayoutControls();
            Invalidate();
        }

        /// <summary>退出设置页：按当前内容模式与分类恢复主内容区控件。</summary>
        private void CloseSettings()
        {
            if (!settingsOpen) return;
            settingsOpen = false;
            settingsPage.Visible = false;
            settingsBtn.Visible = true;
            SetMainContentVisible(true);
            LayoutControls();
            RefreshGrid();   // 开关可能改变了条目可见性，返回时同步刷新
            Invalidate();
        }

        /// <summary>主内容区控件显隐：设置页打开时整体隐藏，关闭时按模式/分类恢复。</summary>
        private void SetMainContentVisible(bool show)
        {
            sidebar.Visible = show;
            grid.Visible = show;
            search.Visible = show;
            countLabel.Visible = show;
            organizeBtn.Visible = show;
            viewToggle.Visible = show;
            toggleLabel.Visible = show;
            addBtn.Visible = show && CanEditCategory(currentCategory);   // 「全部资源」等只读分类不显示
            if (collectionSidebar != null)
                collectionSidebar.Visible = show && CanShowCollectionSidebar;
            if (!show && collectionInfo != null) collectionInfo.Visible = false;
        }

        /// <summary>存储位置设置：分别设定文件夹、文件与资源的入库目标目录（iOS 风格对话框）。</summary>
        private void ShowStorageLocationDialog()
        {
            string folderPath = FolderStoreOf();
            string filePath = FileStoreOf();
            string resourcePath = ResourceStoreRoot();

            using (Form f = new Form())
            {
                f.FormBorderStyle = FormBorderStyle.None;
                f.StartPosition = FormStartPosition.CenterParent;
                f.ClientSize = new Size(520, 358);
                f.BackColor = Theme.Background;
                f.ShowInTaskbar = false;
                ApplyRoundedCorners(f);

                Label title = new Label();
                title.Text = "存储位置设置";
                title.Font = Theme.FontOf(13F, FontStyle.Bold);
                title.ForeColor = Theme.TextPrimary;
                title.BackColor = Color.Transparent;
                title.Location = new Point(24, 20);
                title.AutoSize = true;
                f.Controls.Add(title);

                Label desc = new Label();
                desc.Text = "拖入分类的文件夹与文件，实体均移动到以下位置（原文件不再保留）。";
                desc.Font = Theme.FontOf(9F);
                desc.ForeColor = Theme.TextSecondary;
                desc.BackColor = Color.Transparent;
                desc.Location = new Point(24, 48);
                desc.AutoSize = true;
                f.Controls.Add(desc);

                TextBox folderBox = MakePathRow(f, "文件夹存储位置", folderPath, 82);
                TextBox fileBox = MakePathRow(f, "文件存储位置", filePath, 152);
                TextBox resourceBox = MakePathRow(f, "资源存储位置（按分类建子目录）", resourcePath, 222);
                // 避免首个输入框获得焦点后被整体选中（观感突兀）
                f.Shown += delegate
                {
                    folderBox.SelectionStart = folderBox.TextLength;
                    folderBox.SelectionLength = 0;
                    fileBox.SelectionLength = 0;
                    resourceBox.SelectionLength = 0;
                };

                RoundButton ok = new RoundButton();
                ok.Text = "保存";
                ok.Accent = true;
                ok.Size = new Size(96, 34);
                ok.Location = new Point(f.ClientSize.Width - 216, f.ClientSize.Height - 54);
                ok.Click += delegate
                {
                    config.FolderStorePath = folderBox.Text.Trim();
                    config.FileStorePath = fileBox.Text.Trim();
                    config.ResourceStorePath = resourceBox.Text.Trim();
                    config.Save();
                    f.Close();
                };
                f.Controls.Add(ok);

                RoundButton cancel = new RoundButton();
                cancel.Text = "取消";
                cancel.Accent = false;
                cancel.Size = new Size(96, 34);
                cancel.Location = new Point(f.ClientSize.Width - 112, f.ClientSize.Height - 54);
                cancel.Click += delegate { f.Close(); };
                f.Controls.Add(cancel);

                ConfirmDialog.ShowModal(f);   // 模态 + 遮罩层
            }
        }

        /// <summary>在设置对话框中创建一行「标签 + 圆角路径输入框 + 浏览按钮」（iOS 风格）。</summary>
        private TextBox MakePathRow(Form f, string label, string value, int y)
        {
            Label lb = new Label();
            lb.Text = label;
            lb.Font = Theme.FontOf(9.5F);
            lb.ForeColor = Theme.TextSecondary;
            lb.BackColor = Color.Transparent;
            lb.Location = new Point(24, y);
            lb.AutoSize = true;
            f.Controls.Add(lb);

            // 圆角白底容器：面板底色与对话框一致，仅在其中绘制抗锯齿圆角矩形
            Panel fieldBg = new Panel();
            fieldBg.BackColor = Theme.Background;
            fieldBg.Location = new Point(24, y + 24);
            fieldBg.Size = new Size(f.ClientSize.Width - 200, 32);
            fieldBg.Paint += delegate(object s, PaintEventArgs pe)
            {
                pe.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle rr = new Rectangle(0, 0, fieldBg.Width - 1, fieldBg.Height - 1);
                using (SolidBrush b = new SolidBrush(Color.White))
                    Draw.FillRoundedRect(pe.Graphics, b, rr, 8);
                using (Pen p = new Pen(Theme.Separator, 1))
                    Draw.DrawRoundedRect(pe.Graphics, p, rr, 8);
            };
            f.Controls.Add(fieldBg);

            TextBox box = new TextBox();
            box.Font = Theme.FontOf(9.5F);
            box.BorderStyle = BorderStyle.None;
            box.BackColor = Color.White;
            box.Text = value;
            box.Location = new Point(12, 8);
            box.Width = fieldBg.Width - 24;
            fieldBg.Controls.Add(box);

            RoundButton browse = new RoundButton();
            browse.Text = "选择…";
            browse.Accent = false;
            browse.Size = new Size(96, 32);
            browse.Location = new Point(f.ClientSize.Width - 116, y + 22);
            browse.Click += delegate
            {
                using (FolderBrowserDialog dlg = new FolderBrowserDialog())
                {
                    dlg.Description = "选择" + label;
                    dlg.ShowNewFolderButton = true;
                    string cur = box.Text.Trim();
                    if (cur.Length > 0 && Directory.Exists(cur)) dlg.SelectedPath = cur;
                    if (dlg.ShowDialog(f) == DialogResult.OK) box.Text = dlg.SelectedPath;
                }
            };
            f.Controls.Add(browse);
            return box;
        }

        // ===== 桌面快捷方式 =====
        // 「应用中心」桌面快捷方式的创建已收敛到 DesktopOrganizer.EnsureLauncherShortcutOnDesktop，
        // 与命令行 --tidy-desktop 共用同一实现，避免两处逻辑漂移。

        // ===== 绘制与拖动 =====

        /// <summary>
        /// 背景绘制放入 OnPaintBackground：顶栏渐变/分隔线属于背景层，
        /// 透明子控件（Label/侧栏）可正确透出，避免系统灰底。
        /// </summary>
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // 整体背景
            using (SolidBrush b = new SolidBrush(Theme.Background))
                g.FillRectangle(b, ClientRectangle);
            if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;

            // 顶栏：iOS 导航栏渐变（白 → 极浅灰），横跨全宽（含侧栏上方，macOS 风格）
            Rectangle topBar = new Rectangle(0, 0, ClientSize.Width, Theme.TopBarHeight);
            using (LinearGradientBrush gb = new LinearGradientBrush(
                topBar, Color.FromArgb(255, 255, 255), Color.FromArgb(248, 248, 250), LinearGradientMode.Vertical))
                g.FillRectangle(gb, topBar);

            // 顶栏底部分隔线（1px 实色 + 1px 投影，iOS 磨砂玻璃质感）
            using (Pen p1 = new Pen(Theme.Separator, 1))
                g.DrawLine(p1, 0, Theme.TopBarHeight, ClientSize.Width, Theme.TopBarHeight);
            using (Pen p2 = new Pen(Color.FromArgb(20, 0, 0, 0), 1))
                g.DrawLine(p2, 0, Theme.TopBarHeight + 1, ClientSize.Width, Theme.TopBarHeight + 1);

            // 侧栏右侧分隔线（仅顶栏以下）
            using (Pen ps = new Pen(Theme.Separator, 1))
                g.DrawLine(ps, Theme.SidebarWidth, Theme.TopBarHeight, Theme.SidebarWidth, ClientSize.Height);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // 设置页打开时标题由页面自身绘制，标题行不再重复绘制应用名
            if (settingsOpen) return;
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // 标题：顶栏行1（标题行）
            using (SolidBrush br = new SolidBrush(Theme.TextPrimary))
                Draw.DrawStringLeft(g, "应用中心", Theme.FontOf(12F, FontStyle.Bold), br, new Rectangle(Theme.SidebarWidth + 16, 0, 128, Theme.TitleRowHeight));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            TryDrag(e.Button, e.X, e.Y);
            base.OnMouseDown(e);
        }

        /// <summary>
        /// 无边框窗口拖动：顶栏空白区（除搜索/开关/按钮等交互控件）与侧栏空白区均可拖动。
        /// 配合 MakeDraggable 递归绑定子控件，避免子控件吞噬鼠标消息导致无法拖动。
        /// </summary>
        private void TryDrag(MouseButtons btn, int x, int y)
        {
            if (btn != MouseButtons.Left) return;
            bool inTopBar = y < Theme.TopBarHeight;
            bool inSidebar = x <= Theme.SidebarWidth && y >= Theme.TopBarHeight;
            if (!inTopBar && !inSidebar) return;
            // 顶栏内：命中搜索框、视图切换、操作按钮、窗口按钮时不拖动
            if (inTopBar && HitsTopBarControl(x, y)) return;
            ReleaseCapture();
            SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
        }

        /// <summary>顶栏内是否命中交互控件（搜索/视图切换/操作按钮/窗口按钮），命中处点击不转发为窗体拖动。</summary>
        private bool HitsTopBarControl(int x, int y)
        {
            Control[] hits = new Control[]
            {
                search, viewToggle, toggleLabel, organizeBtn, addBtn, settingsBtn, minBtn, maxBtn, closeBtn
            };
            for (int i = 0; i < hits.Length; i++)
            {
                Control c = hits[i];
                if (c == null || !c.Visible) continue;
                Rectangle r = new Rectangle(c.Left - 4, c.Top - 4, c.Width + 8, c.Height + 8);
                if (r.Contains(x, y)) return true;
            }
            return false;
        }

        /// <summary>递归为窗体所有子控件绑定拖动转发（排除交互控件本身的点击区域）。</summary>
        private void MakeDraggable(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                // 交互控件不转发（避免吞掉自身点击）；侧栏/网格自身处理点击，不参与拖动转发
                if (c is RoundButton || c is IconButton || c is ToggleSwitch || c is SearchField
                    || c is IosProgressBar || c is CheckedListBox || c is TextBox
                    || c is SidebarControl || c is AppGridControl || c is SettingsPage)
                    continue;
                c.MouseDown += delegate(object s, MouseEventArgs e)
                {
                    Point p = c.PointToScreen(new Point(e.X, e.Y));
                    Point f = PointToClient(p);
                    TryDrag(e.Button, f.X, f.Y);
                };
                MakeDraggable(c);
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.F))
            {
                search.Focus();
                return true;
            }
            // Ctrl+V：集合信息页编辑态粘贴剪贴板图片作为封面（焦点在名称/简介输入框时按普通文本粘贴）
            if (keyData == (Keys.Control | Keys.V) && collectionInfo != null && collectionInfo.IsEditing
                && !collectionInfo.IsTextInputFocused && !(search != null && search.ContainsFocus))
            {
                collectionInfo.PasteCover();
                return true;
            }
            // Esc：设置页优先返回主内容区；页内编辑先取消编辑；再清空搜索；最后按集合层级回退
            if (keyData == Keys.Escape && settingsOpen)
            {
                CloseSettings();
                return true;
            }
            if (keyData == Keys.Escape && collectionInfo != null && collectionInfo.IsEditing)
            {
                collectionInfo.EndEdit();   // 取消页内编辑，保留原集合信息展示
                return true;
            }
            if (keyData == Keys.Escape && search != null && !string.IsNullOrEmpty(search.Text))
            {
                search.Text = string.Empty;   // 触发 TextChanged → RefreshGrid
                return true;
            }
            // Esc：集合内容内先回到信息页；信息页再回到「全部」，符合「一处撤销」的直觉
            if (keyData == Keys.Escape && collectionSidebar != null && collectionSidebar.Visible
                && (currentCollection != null || !collectionEntered))
            {
                if (collectionEntered)
                {
                    collectionEntered = false;
                }
                else
                {
                    currentCollection = null;
                    collectionEntered = true;
                    collectionSidebar.SetActive(string.Empty);
                }
                RefreshGrid();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}

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
        private SearchField search;
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

        // 状态
        private string currentCategory = "常用";
        private SidebarControl.ContentMode contentMode = SidebarControl.ContentMode.Software;
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
            Application.RemoveMessageFilter(this);
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
            if (m.Msg != WM_MOUSEWHEEL || sidebar == null || !sidebar.Visible) return false;
            long lp = m.LParam.ToInt64();
            Point screen = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
            if (!sidebar.RectangleToScreen(sidebar.ClientRectangle).Contains(screen)) return false;
            int delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
            sidebar.ScrollByWheel(delta);
            return true;   // 已消费：避免主内容区同时滚动
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
            grid.EditUrlRequested += EditUrlAddress;
            grid.DeleteRequested += DeleteAddedEntry;
            grid.CategoryTextProvider = GetCategoryText;
            grid.ReorderRequested += OnGridReorder;
            grid.DragMoved += OnGridDragMoved;
            grid.DroppedOutside += OnGridDroppedOutside;
            grid.FilesDropped += OnGridFilesDropped;
            grid.MoveIntoFolder += OnGridMoveIntoFolder;
            grid.BlankRightClicked += OnGridBlankRightClicked;
            Controls.Add(grid);

            search = new SearchField();
            search.TextChanged += delegate { RefreshGrid(); };
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
            };
            settingsPage.CloseToTrayChanged += delegate { SetCloseToTray(settingsPage.CloseToTray); };
            settingsPage.StorageLocationRequested += delegate
            {
                ShowStorageLocationDialog();
                // 对话框可能改了存储目录，回到页面后刷新展示的路径
                settingsPage.Bind(config.ShowHidden, config.ShowAllInstalled, config.AutoScan,
                    config.CloseToTray, FolderStoreOf(), FileStoreOf());
            };
            settingsPage.RescanRequested += delegate { StartScan(); };
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
            // 网站模式隐藏网格/列表开关后，计数标签右侧延伸到操作按钮簇，不留空白
            int countRight = (viewToggle.Visible ? viewToggle.Left : clusterLeft) - 12;
            countLabel.Location = new Point(countLeft, row1);
            countLabel.Size = new Size(Math.Max(0, countRight - countLeft), row2);

            int gridHeight = ClientSize.Height - top - (statusBar.Visible ? statusH : 0);
            grid.Location = new Point(sb, top);
            grid.Size = new Size(ClientSize.Width - sb, gridHeight);

            loadingLabel.Location = new Point(sb, top);
            loadingLabel.Size = new Size(ClientSize.Width - sb, gridHeight);

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
            // 只读全集分类：全部软件 / 全部文件 / 全部网站
            list.Add(MkItem("全部软件", "全部软件"));
            list.Add(MkItem("全部文件", "全部文件"));
            list.Add(MkItem("全部网站", "全部网站"));
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
            sidebar.SetActive(key);
            // 每分类独立视图模式：切换分类时恢复该分类上次选择的网格/列表
            bool gm = GetCategoryGridMode(key);
            viewToggle.Checked = gm;   // 触发 CheckedChanged 自动落盘与应用
            // 网站模式强制列表：切换分类不得把网址切回网格（与 OnModeChanged 保持一致）
            grid.GridMode = contentMode == SidebarControl.ContentMode.Website ? false : gm;
            addBtn.Visible = CanEditCategory(key) && contentMode == SidebarControl.ContentMode.Software;
            LayoutControls();
            RefreshGrid();
        }

        /// <summary>内容模式切换（软件/网站）：网站模式下展示全部网址条目，且强制列表视图。</summary>
        private void OnModeChanged(SidebarControl.ContentMode m)
        {
            contentMode = m;
            if (m == SidebarControl.ContentMode.Website)
            {
                grid.GridMode = false;
                grid.ShowRowActions = true;
                addBtn.Visible = true;   // 网站模式下可添加网址
                // 网站强制列表视图：隐藏网格/列表开关，避免切换后破坏强制列表
                viewToggle.Visible = false;
                toggleLabel.Visible = false;
            }
            else
            {
                grid.GridMode = GetCategoryGridMode(currentCategory);
                viewToggle.Checked = GetCategoryGridMode(currentCategory);
                viewToggle.Visible = true;
                toggleLabel.Visible = true;
                grid.ShowRowActions = false;
                addBtn.Visible = CanEditCategory(currentCategory);
            }
            LayoutControls();
            RefreshGrid();
        }

        /// <summary>取指定分类的网格/列表偏好，缺失时用全局 ViewGrid 兜底。</summary>
        private bool GetCategoryGridMode(string cat)
        {
            bool v;
            if (config.CategoryViewMode.TryGetValue(cat, out v)) return v;
            return config.ViewGrid;
        }

        /// <summary>设置指定分类的网格/列表偏好。</summary>
        private void SetCategoryGridMode(string cat, bool grid)
        {
            config.CategoryViewMode[cat] = grid;
        }

        /// <summary>
        /// 允许添加/移除条目的分类：内置分类、自定义分类与「快捷中心」；
        /// 常用与只读全集分类（全部软件/全部文件/全部网站）不可编辑。
        /// </summary>
        private bool CanEditCategory(string cat)
        {
            if (IsReadOnlyCategory(cat) || cat == "常用") return false;
            if (cat == QuickCenter) return true;
            if (IsCustomCategory(cat)) return true;
            for (int i = 0; i < CategoryRules.BuiltIn.Length; i++)
                if (CategoryRules.BuiltIn[i] == cat) return true;
            return false;
        }

        /// <summary>只读全集分类：全部软件 / 全部文件 / 全部网站（不参与编辑与分类移动）。</summary>
        private static bool IsReadOnlyCategory(string cat)
        {
            return cat == "全部软件" || cat == "全部文件" || cat == "全部网站";
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

        private void StartScan()
        {
            loadingLabel.Visible = true;
            loadingLabel.BringToFront();
            // 设置页打开时扫描进度不应压住页面（从设置页「重新扫描」触发）
            if (settingsOpen) settingsPage.BringToFront();
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
                        SwapApps(result);
                        loadingLabel.Visible = false;
                        RefreshGrid();
                        StartIconLoad();
                    });
                }
                catch { }
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
            string kw = (search.Text ?? string.Empty).Trim().ToLowerInvariant();
            List<AppInfo> shown = new List<AppInfo>();

            if (kw.Length > 0)
            {
                // 全局搜索：跨全部分类匹配名称，命中即显示（受内容模式过滤）
                foreach (AppInfo a in allApps)
                {
                    if (!ShouldShow(a)) continue;
                    if (!MatchesContentMode(a)) continue;
                    // 多字段匹配：名称 / 分类 / 网址，任一命中即显示（归一化，故「vscode」可命中「VS Code」）
                    if (Scanner.FuzzyMatch(a.Name, kw)
                        || Scanner.FuzzyMatch(a.Category, kw)
                        || (Scanner.IsUrl(a.TargetPath) && Scanner.FuzzyMatch(a.TargetPath, kw)))
                        shown.Add(a);
                }
                categoryTotal = CountVisible();
            }
            else if (contentMode == SidebarControl.ContentMode.Website)
            {
                // 网站模式：展示全部网址条目（忽略分类选择）
                foreach (AppInfo a in allApps)
                {
                    if (!ShouldShow(a)) continue;
                    if (Scanner.IsUrl(a.TargetPath)) shown.Add(a);
                }
                categoryTotal = shown.Count;
            }
            else
            {
                List<AppInfo> cat = new List<AppInfo>();
                foreach (AppInfo a in allApps)
                {
                    if (!ShouldShow(a)) continue;
                    if (Scanner.IsUrl(a.TargetPath)) continue;   // 软件模式不显示网址
                    if (InCategory(a, currentCategory)) cat.Add(a);
                }
                categoryTotal = cat.Count;
                shown.AddRange(cat);
                // 仅可排序分类（内置/自定义/快捷中心/常用，非搜索态）应用用户拖拽顺序
                if (kw.Length == 0 && CanReorderCategory(currentCategory))
                    ApplyItemOrder(shown, currentCategory);
            }

            shownApps = shown;
            grid.SetApps(shown);
            // 拖动排序仅在可排序分类的软件模式、非搜索态开放
            grid.AllowReorder = kw.Length == 0
                && contentMode == SidebarControl.ContentMode.Software
                && CanReorderCategory(currentCategory);
            // 主内容区接受外部文件/文件夹拖入：仅可编辑分类放行
            grid.AllowExternalDrop = CanEditCategory(currentCategory);
            // 拖出来源：搜索态（结果跨分类，可直接拖到侧栏「加入」）或只读全集 /「常用」分类；
            // 与网格内排序相互独立：只读全集不排序，「常用」两者皆可（网格内排序 + 拖出加入）
            grid.AllowDragOut = contentMode == SidebarControl.ContentMode.Software
                && (kw.Length > 0 || IsDragOutSourceCategory(currentCategory));
            UpdateCount(shown.Count, categoryTotal);
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
                if (list[i].Key != null && rank.TryGetValue(list[i].Key, out r)) known.Add(list[i]);
                else unknown.Add(list[i]);
            }
            known.Sort(delegate(AppInfo x, AppInfo y)
            {
                return rank[x.Key].CompareTo(rank[y.Key]);
            });
            list.Clear();
            list.AddRange(known);
            list.AddRange(unknown);
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

        /// <summary>网格拖动条目期间：指针落到侧栏可投放分类时联动高亮。</summary>
        private void OnGridDragMoved(Point screen)
        {
            sidebar.SetExternalDropHighlight(sidebar.DropCategoryAtScreen(screen));
        }

        /// <summary>网格外松手：若落在侧栏某分类上，则把该条目「加入」该分类（多归属，不改原分类与存储位置）。</summary>
        private void OnGridDroppedOutside(AppInfo app, Point screen)
        {
            string cat = sidebar.DropCategoryAtScreen(screen);
            sidebar.SetExternalDropHighlight(null);
            if (string.IsNullOrEmpty(cat)) return;
            AddItemToCategory(app, cat);
        }

        /// <summary>外部文件/文件夹拖入主内容区：按当前分类入库（实体搬入配置存储路径，分类中收录引用）。</summary>
        private void OnGridFilesDropped(string[] paths)
        {
            if (paths == null || paths.Length == 0) return;
            if (!CanEditCategory(currentCategory)) return;
            AddPaths(paths, currentCategory);
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

        /// <summary>网格空白处右键：新建文件夹 / 文本文档（存入配置的存储位置，收录进当前分类）。</summary>
        private void OnGridBlankRightClicked(Point screen)
        {
            if (!CanEditCategory(currentCategory)) return;
            List<MenuWindow.MenuItem> items = new List<MenuWindow.MenuItem>();
            items.Add(MkMenu("新建文件夹", delegate { CreateManagedEntry(true); }));
            items.Add(MkMenu("新建文本文档", delegate { CreateManagedEntry(false); }));
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

        /// <summary>文档/目录条目追加「删除」项：仅限自建收录的非网址条目（软件/网址不适用）。</summary>
        private void AddDeleteMenu(List<MenuWindow.MenuItem> items, AppInfo app)
        {
            if (!app.IsFile || Scanner.IsUrl(app.TargetPath)) return;
            if (FindAddedEntry(app.TargetPath, currentCategory) < 0 && !IsAddedAnywhere(app.TargetPath)) return;
            items.Add(MkMenu("删除", delegate { DeleteEntryDisk(app); }));
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

        /// <summary>当前内容模式下条目是否应展示。</summary>
        private bool MatchesContentMode(AppInfo a)
        {
            bool isUrl = Scanner.IsUrl(a.TargetPath);
            return contentMode == SidebarControl.ContentMode.Website ? isUrl : !isUrl;
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
            // 只读全集分类：全部软件=软件（不含文件/文件夹），全部文件=文件/文件夹，全部网站=网址
            if (category == "全部软件") return !a.IsFile;
            if (category == "全部文件") return a.IsFile && !Scanner.IsUrl(a.TargetPath);
            if (category == "全部网站") return Scanner.IsUrl(a.TargetPath);
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

        /// <summary>把条目「加入」指定分类（多归属）：原分类与存储位置均不变，已在该分类则不重复添加。</summary>
        private void AddItemToCategory(AppInfo app, string category)
        {
            if (app == null || string.IsNullOrEmpty(app.Key)) return;
            if (!CanEditCategory(category)) return;
            if (InCategory(app, category)) return;

            List<string> cats;
            if (!config.ItemCategories.TryGetValue(app.Key, out cats))
            {
                cats = new List<string>();
                config.ItemCategories[app.Key] = cats;
            }
            if (!cats.Contains(category)) cats.Add(category);
            config.Save();
            RefreshGrid();
        }

        /// <summary>把条目从指定分类「移出」（仅移除额外归属，不影响主分类与存储位置）。</summary>
        private void RemoveItemFromCategory(AppInfo app, string category)
        {
            if (app == null || string.IsNullOrEmpty(app.Key)) return;
            List<string> cats;
            if (!config.ItemCategories.TryGetValue(app.Key, out cats) || !cats.Remove(category)) return;
            if (cats.Count == 0) config.ItemCategories.Remove(app.Key);
            config.Save();
            RefreshGrid();
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
            // 拖放「加入」的额外归属分类
            List<string> extra;
            if (!string.IsNullOrEmpty(a.Key) && config.ItemCategories.TryGetValue(a.Key, out extra))
            {
                for (int i = 0; i < extra.Count; i++)
                    if (!cats.Contains(extra[i])) cats.Add(extra[i]);
            }
            // 自定义分类通过 override 已体现在 a.Category
            return string.Join(", ", cats.ToArray());
        }

        private bool IsCustomCategory(string name)
        {
            return config.CustomCategories.Contains(name);
        }

        /// <summary>侧栏底部「+ 新建分类」：弹出输入框创建自定义分类。</summary>
        private void OnAddCategory()
        {
            string name = InputDialog.Show("新建分类", "请输入分类名称：");
            if (string.IsNullOrEmpty(name)) return;
            name = name.Trim();
            if (name.Length == 0) return;
            if (config.CustomCategories.Contains(name))
            {
                MessageBox.Show("分类「" + name + "」已存在。", "新建分类",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            config.CustomCategories.Add(name);
            config.Save();
            RebuildSidebar();
            OnSidebarSelect(name);
        }

        /// <summary>自定义分类右键菜单：重命名 / 删除。</summary>
        private void OnCategoryContext(string category, Point screen)
        {
            if (!IsCustomCategory(category)) return;
            List<MenuWindow.MenuItem> items = new List<MenuWindow.MenuItem>();
            items.Add(MkMenu("重命名…", delegate { RenameCategory(category); }));
            items.Add(MkMenu("删除分类", delegate { DeleteCategory(category); }));
            MenuWindow mw = new MenuWindow(items, 160);
            mw.Location = FixLocation(screen, mw.Size);
            mw.Show(this);
        }

        private void RenameCategory(string oldName)
        {
            string name = InputDialog.Show("重命名分类", "请输入新名称：", oldName);
            if (string.IsNullOrEmpty(name)) return;
            name = name.Trim();
            if (name.Length == 0 || name == oldName) return;
            if (config.CustomCategories.Contains(name))
            {
                MessageBox.Show("分类「" + name + "」已存在。", "重命名分类",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            int idx = config.CustomCategories.IndexOf(oldName);
            if (idx >= 0) config.CustomCategories[idx] = name;
            // 同步所有指向旧分类的 override 与 added items
            List<string> keys = new List<string>(config.CategoryOverride.Keys);
            foreach (string k in keys)
                if (config.CategoryOverride[k] == oldName) config.CategoryOverride[k] = name;
            foreach (AddedEntry e in config.AddedItems)
                if (e.Category == oldName) e.Category = name;
            // 同步排序记录中的分类名
            if (config.ItemOrder.ContainsKey(oldName))
            {
                config.ItemOrder[name] = config.ItemOrder[oldName];
                config.ItemOrder.Remove(oldName);
            }
            for (int i = 0; i < config.SidebarOrder.Count; i++)
                if (config.SidebarOrder[i] == oldName) config.SidebarOrder[i] = name;
            if (currentCategory == oldName) currentCategory = name;
            config.Save();
            RebuildSidebar();
            RefreshGrid();
        }

        private void DeleteCategory(string category)
        {
            if (!ConfirmDialog.Show("删除分类", "确认删除分类「" + category + "」？\n该分类下的条目将回归默认分类。")) return;
            config.CustomCategories.Remove(category);
            List<string> keys = new List<string>(config.CategoryOverride.Keys);
            foreach (string k in keys)
                if (config.CategoryOverride[k] == category) config.CategoryOverride.Remove(k);
            foreach (AddedEntry e in config.AddedItems)
                if (e.Category == category) e.Category = CategoryRules.Classify(Path.GetFileName(e.Path));
            config.ItemOrder.Remove(category);
            config.SidebarOrder.Remove(category);
            config.Save();
            if (currentCategory == category) currentCategory = "全部软件";
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
            Launcher.Launch(app);
        }

        private void OnContextMenu(AppInfo app, Point screen)
        {
            List<MenuWindow.MenuItem> items = new List<MenuWindow.MenuItem>();
            bool readOnly = IsReadOnlyCategory(currentCategory) && contentMode != SidebarControl.ContentMode.Website;

            if (readOnly)
            {
                items.Add(MkMenu("打开", delegate { Launcher.Launch(app); }));
                items.Add(MkMenu("打开所在位置", delegate { Launcher.OpenFileLocation(app); }));
                items.Add(MkSep());
                items.Add(MkMenu("复制名称", delegate { CopyText(app.Name); }));
                items.Add(MkMenu("复制路径", delegate { CopyText(app.TargetPath); }));
                items.Add(MkSep());
                // 只读全集分类：以「加入」方式把条目多归属到其它分类（原归属与「全部」中的条目均不变）
                items.Add(MkSubMenu("移动到分类…", BuildAddCategoryItems(app, false)));
                items.Add(MkSubMenu("移动到自定义…", BuildAddCategoryItems(app, true)));
                AddDeleteMenu(items, app);
            }
            else
            {
                if (app.IsFile)
                {
                    // 文件/文件夹/网址条目：打开自身 + 定位（网址同样可被资源管理器定位）
                    items.Add(MkMenu(app.IsFolder ? "打开文件夹" : (Scanner.IsUrl(app.TargetPath) ? "打开网址" : "打开文件"), delegate { Launcher.Launch(app); }));
                    items.Add(MkMenu("打开所在位置", delegate { Launcher.OpenFileLocation(app); }));
                    // 用户添加的条目可改名；网址还可改地址
                    if (FindAddedEntry(app.TargetPath, currentCategory) >= 0 || IsAddedAnywhere(app.TargetPath))
                    {
                        items.Add(MkMenu("改名…", delegate { RenameAddedEntry(app); }));
                        if (Scanner.IsUrl(app.TargetPath))
                            items.Add(MkMenu("改地址…", delegate { EditUrlAddress(app); }));
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
                if (app.IsFavorite)
                    items.Add(MkMenu("从常用移除", delegate { ToggleFavorite(app, false); }));
                else
                    items.Add(MkMenu("添加到常用", delegate { ToggleFavorite(app, true); }));
                items.Add(MkSubMenu("移动到分类…", BuildMoveCategoryItems(app)));
                if (InCustom(app))
                    items.Add(MkMenu("从自定义移除", delegate { RemoveFromCustom(app); }));
                items.Add(MkSubMenu("添加到自定义…", BuildAddCustomItems(app)));
                if (CanEditCategory(currentCategory))
                    items.Add(MkMenu("从「" + currentCategory + "」移除", delegate { RemoveFromCategory(app); }));
                items.Add(MkSep());
                if (app.IsHidden)
                    items.Add(MkMenu("取消隐藏", delegate { SetHidden(app, false); }));
                else
                    items.Add(MkMenu("隐藏", delegate { SetHidden(app, true); }));
                items.Add(MkMenu("复制名称", delegate { CopyText(app.Name); }));
                items.Add(MkMenu("复制路径", delegate { CopyText(app.TargetPath); }));
                if (!string.IsNullOrEmpty(app.UninstallString))
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
            m.Disabled = InCategory(app, category);
            return m;
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

        /// <summary>点击「+ 添加」：弹出"添加软件/文件/文件夹"菜单，网址走「添加网址…」。</summary>
        private void OnAdd(object sender, EventArgs e)
        {
            List<MenuWindow.MenuItem> items = new List<MenuWindow.MenuItem>();
            items.Add(MkMenu("添加软件…", delegate { PickSoftware(); }));
            items.Add(MkMenu("添加文件…", delegate { PickFiles(); }));
            items.Add(MkMenu("添加文件夹…", delegate { PickFolder(); }));
            items.Add(MkSep());
            items.Add(MkMenu("添加网址…", delegate { AddWebsite(); }));
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
            // 列出所有可启动的非网址条目，排除已添加的
            HashSet<string> added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AddedEntry ae in config.AddedItems) added.Add(ae.Path);
            List<AppInfo> candidates = new List<AppInfo>();
            foreach (AppInfo a in allApps)
            {
                if (Scanner.IsUrl(a.TargetPath)) continue;
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

        /// <summary>多选文件加入当前分类。</summary>
        private void PickFiles()
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "添加文件到「" + currentCategory + "」";
                dlg.Multiselect = true;
                dlg.Filter = "所有文件 (*.*)|*.*";
                if (dlg.ShowDialog(this) == DialogResult.OK) AddPaths(dlg.FileNames, currentCategory);
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
                    AddPaths(new string[] { dlg.SelectedPath }, currentCategory);
            }
        }

        /// <summary>「网站模式」：输入网址后以网站条目登记，可在「全部网站」查看。</summary>
        private void AddWebsite()
        {
            string url = InputDialog.Show("添加网址", "请输入网址（http/https）：");
            if (url == null) return;
            url = url.Trim();
            if (url.Length == 0) return;
            if (url.IndexOf("://", StringComparison.Ordinal) < 0) url = "https://" + url;
            if (!Scanner.IsUrl(url))
            {
                MessageBox.Show("网址格式不正确，请输入以 http:// 或 https:// 开头的地址。", "添加网址",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            AddUrl(url, currentCategory);
        }

        /// <summary>把网址写入 AddedItems（按 网址+分类 去重），随后重扫并刷新。</summary>
        private void AddUrl(string url, string category)
        {
            if (FindAddedEntry(url, category) >= 0) return;
            AddedEntry e = new AddedEntry();
            e.Path = url;
            e.Category = category;
            e.IsUrl = true;
            config.AddedItems.Add(e);
            config.Save();
            SwapApps(scanner.ScanAll());
            RefreshGrid();
            StartIconLoad();
        }

        /// <summary>
        /// 把路径批量写入 AddedItems（按 路径+分类 去重），并按分类规则搬运入库：
        /// 文件夹搬入该分类目录、文件搬入其 Files 子目录、软件（.exe）只登记不搬运。
        /// 搬运为异步执行，状态栏显示每项独立进度。
        /// </summary>
        private void AddPaths(string[] paths, string category)
        {
            if (paths == null || paths.Length == 0) return;
            if (!CanEditCategory(category)) return;   // 只读分类不接受收录
            MoveToCategoryAsync(paths, category);
        }

        /// <summary>
        /// 异步把路径搬入目标分类：逐项移动并更新状态栏进度，完成后重扫刷新。
        /// 每项独立显示进度详情；软件（.exe）只登记不搬。
        /// </summary>
        private void MoveToCategoryAsync(string[] paths, string category)
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
                    string stored = StoreIntoCategory(p, category);
                    lock (storedList) storedList.Add(stored);
                }
                BeginInvoke((Action)delegate
                {
                    int added = 0;
                    foreach (string stored in storedList)
                    {
                        if (FindAddedEntry(stored, category) >= 0) continue;
                        AddedEntry e = new AddedEntry();
                        e.Path = stored;
                        e.Category = category;
                        e.IsFolder = Directory.Exists(stored);
                        config.AddedItems.Add(e);
                        added++;
                    }
                    if (added > 0)
                    {
                        config.Save();
                        SwapApps(scanner.ScanAll());
                        RefreshGrid();
                        StartIconLoad();
                    }
                    HideStatusBar();
                });
            });
        }

        /// <summary>
        /// 入库规则：文件夹收进配置的「文件夹存储位置」、文件收进「文件存储位置」（均为统一目录，不再按分类细分）；
        /// 软件（.exe）只登记不搬运。未配置时回退 ManageRoot（H:\Manage，其下 Files 收文件）。
        /// 已在目标目录内或搬运失败时返回原路径，保证条目不丢失。
        /// </summary>
        private string StoreIntoCategory(string path, string category)
        {
            try
            {
                bool isFolder = Directory.Exists(path);
                if (!isFolder && IsSoftwareFile(path)) return path;      // 软件只登记不搬
                string destDir = isFolder ? FolderStoreOf() : FileStoreOf();
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

        /// <summary>文件/文件夹拖放到侧栏某分类：切到该分类并按规则入库（异步 + 状态栏进度）。</summary>
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

        /// <summary>改地址：修改网址条目的 URL。</summary>
        private void EditUrlAddress(AppInfo app)
        {
            if (app == null) return;
            string newUrl = InputDialog.Show("改地址", "请输入新网址：", app.TargetPath);
            if (newUrl == null) return;
            newUrl = newUrl.Trim();
            if (newUrl.Length == 0) return;
            if (newUrl.IndexOf("://", StringComparison.Ordinal) < 0) newUrl = "https://" + newUrl;
            if (!Scanner.IsUrl(newUrl))
            {
                MessageBox.Show("网址格式不正确。", "改地址", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            for (int i = 0; i < config.AddedItems.Count; i++)
            {
                AddedEntry e = config.AddedItems[i];
                if (string.Equals(e.Path, app.TargetPath, StringComparison.OrdinalIgnoreCase)
                    && Scanner.IsUrl(e.Path))
                {
                    e.Path = newUrl;
                }
            }
            config.Save();
            SwapApps(scanner.ScanAll());
            RefreshGrid();
            StartIconLoad();
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
                addBtn.Visible = CanEditCategory(currentCategory) && contentMode == SidebarControl.ContentMode.Software;
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
            settingsPage.Bind(config.ShowHidden, config.ShowAllInstalled, config.AutoScan,
                config.CloseToTray, FolderStoreOf(), FileStoreOf());
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
            bool website = contentMode == SidebarControl.ContentMode.Website;
            viewToggle.Visible = show && !website;
            toggleLabel.Visible = show && !website;
            addBtn.Visible = show && (website || CanEditCategory(currentCategory));
        }

        /// <summary>存储位置设置：分别设定文件夹与文件的入库目标目录（iOS 风格对话框）。</summary>
        private void ShowStorageLocationDialog()
        {
            string folderPath = FolderStoreOf();
            string filePath = FileStoreOf();

            using (Form f = new Form())
            {
                f.FormBorderStyle = FormBorderStyle.None;
                f.StartPosition = FormStartPosition.CenterParent;
                f.ClientSize = new Size(520, 288);
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
                desc.Text = "拖入分类的文件夹与文件，实体分别存放到以下位置；分类中仅收录其快捷方式。";
                desc.Font = Theme.FontOf(9F);
                desc.ForeColor = Theme.TextSecondary;
                desc.BackColor = Color.Transparent;
                desc.Location = new Point(24, 48);
                desc.AutoSize = true;
                f.Controls.Add(desc);

                TextBox folderBox = MakePathRow(f, "文件夹存储位置", folderPath, 82);
                TextBox fileBox = MakePathRow(f, "文件存储位置", filePath, 152);
                // 避免首个输入框获得焦点后被整体选中（观感突兀）
                f.Shown += delegate
                {
                    folderBox.SelectionStart = folderBox.TextLength;
                    folderBox.SelectionLength = 0;
                    fileBox.SelectionLength = 0;
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
            using (Font f = Theme.FontOf(12F, FontStyle.Bold))
            using (SolidBrush br = new SolidBrush(Theme.TextPrimary))
                Draw.DrawStringLeft(g, "应用中心", f, br, new Rectangle(Theme.SidebarWidth + 16, 0, 128, Theme.TitleRowHeight));
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
            // Esc：设置页优先返回主内容区；否则清空搜索（有内容时），符合「一处撤销」的直觉
            if (keyData == Keys.Escape && settingsOpen)
            {
                CloseSettings();
                return true;
            }
            if (keyData == Keys.Escape && search != null && !string.IsNullOrEmpty(search.Text))
            {
                search.Text = string.Empty;   // 触发 TextChanged → RefreshGrid
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}

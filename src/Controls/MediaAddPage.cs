using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using AppManager;

namespace AppManager.Controls
{
    /// <summary>
    /// 资源分类的「添加文件」页：和设置页一样盖在主窗口标题行以下，不另弹窗口。
    /// 左栏是「已选的文件」、常用位置和磁盘；中栏是当前文件夹（照片用缩略图网格，其它类型用带大小和修改时间的列表），
    /// 单击文件夹进入，单击文件在右栏预览，点勾选框或双击勾选，可以在几个文件夹里一起选；
    /// 右栏预览单击的文件（图片显示画面和尺寸，音乐显示封面和歌曲信息，其它显示文件信息）。
    /// 底部是已选数量、粘贴路径的输入框和「添加」「取消」；也可以把文件拖进来或 Ctrl+V 粘贴。
    /// 目录读取、缩略图、预览和确定时的磁盘检查都在后台进行；页面关闭（释放）后迟到的结果直接丢弃。
    /// </summary>
    internal sealed class MediaAddPage : Control
    {
        /// <summary>一个文件夹最多列出的文件数，其余只计数（缩略图按需解码、有上限，列表只存路径和大小）。</summary>
        internal const int MaxListed = 5000;
        private const int EM_SETCUEBANNER = 0x1501;
        private const int PlaceHeaderHeight = 30;
        private const int PlaceRowHeight = 38;
        private const int FileRowHeight = 40;
        private const int CoverMax = 640;   // 音乐封面预览的长边上限：够画右栏，内存有界

        private const string GlyphFolder = "";
        private const string GlyphDrive = "";
        private const string GlyphPicture = "";
        private const string GlyphAudio = "";
        private const string GlyphVideo = "";
        private const string GlyphFile = "";
        private const string GlyphChecked = "";
        private const string GlyphDownload = "";

        private static readonly Guid DownloadsFolder = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        /// <summary>本次运行里每类文件上次添加时所在的文件夹：再次打开时回到这里。</summary>
        private static readonly Dictionary<string, string> lastFolders = new Dictionary<string, string>(StringComparer.Ordinal);

        private enum PlaceKind { Header, Note, Checked, Place, Drive }

        private sealed class PlaceRow
        {
            internal PlaceKind Kind;
            internal string Title = string.Empty;
            internal string Path;
            internal string Glyph;
            internal bool Enabled = true;

            public override string ToString() { return Title; }
        }

        private enum FileKind { Folder, File, Note }

        private sealed class FileRow
        {
            internal FileKind Kind;
            internal string Path;
            internal string Name = string.Empty;
            internal long Length = -1;                       // -1：不知道（「已选的文件」里不再读磁盘）
            internal DateTime Modified = DateTime.MinValue;  // MinValue：不知道
            internal string Detail;                          // 「已选的文件」里显示所在文件夹

            public override string ToString() { return Name; }
        }

        private readonly string pageTitle;
        private readonly string typeName;
        private readonly string[] extensions;
        private readonly bool imageMode;

        private readonly RoundButton backButton = new RoundButton();
        private readonly RoundButton upButton = new RoundButton();
        private readonly RoundButton allButton = new RoundButton();
        private readonly RoundButton checkButton = new RoundButton();
        private readonly RoundButton confirmButton = new RoundButton();
        private readonly RoundButton cancelButton = new RoundButton();
        private readonly SoftwarePickerDialog.PickerList placeList = new SoftwarePickerDialog.PickerList();
        private readonly SoftwarePickerDialog.PickerList fileList = new SoftwarePickerDialog.PickerList();
        private readonly CoverImageGrid grid = new CoverImageGrid();
        private readonly TextBox pathBox = new TextBox();

        private readonly List<PlaceRow> places = new List<PlaceRow>();
        private List<LocalScanScopeDrive> drives;   // null：仍在后台读取

        // 勾选：按勾选先后，可以跨文件夹
        private readonly List<string> checkedFiles = new List<string>();
        private readonly HashSet<string> checkedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private int checkedFolderCount;   // 勾选的文件来自几个文件夹：勾选变化时算一次，状态行重画时不再逐个计算

        // 中栏
        private string currentFolder;
        private bool showingChecked;                 // 中栏显示的是「已选的文件」而不是某个文件夹
        private readonly List<string> listedFiles = new List<string>();   // 中栏列出的文件（全选按它）
        private string folderSummary = string.Empty;
        private int loadGen;
        private bool loading;
        private string pendingSelect;                // 文件夹读完后要选中并预览的文件
        private bool pressedListCheck;               // 列表这次按下落在勾选框上：双击不再重复切换
        private int pressedListFolder = -1;

        // 右栏预览
        private Bitmap previewImage;
        private string previewPath;
        private readonly List<KeyValuePair<string, string>> previewInfo = new List<KeyValuePair<string, string>>();
        private bool previewLoading;
        private bool previewFailed;
        private string previewError = string.Empty;
        private int previewGen;

        private string notice;                       // 最近一次操作的提示；勾选或输入变化时清掉
        private bool noticeIsError;
        private bool checking;
        private int checkVersion;
        private bool started;
        private bool settingText;
        private int laidOutDpi;

        // 布局（像素，RefreshLayout 按当前 DPI 计算）
        private Rectangle titleRect, hintRect, placeFrame, browseFrame, previewFrame, statusRect, pathFrame;

        /// <summary>点了「添加」并且检查通过：参数是勾选的文件（按勾选先后）。</summary>
        internal event Action<List<string>> Confirmed;
        /// <summary>点了「返回」「取消」或按了 Esc。</summary>
        internal event Action Cancelled;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, string lParam);

        /// <summary>
        /// title 是页标题；typeName 是分类的类型名（照片、音乐……，决定常用位置和「上次添加」的记忆）；
        /// extensions 为空表示不限类型。initialFolder 为空时打开这类文件上次添加时的文件夹，再没有就打开对应的系统文件夹或桌面。
        /// </summary>
        internal MediaAddPage(string title, string typeName, string[] extensions, string initialFolder)
        {
            pageTitle = title ?? string.Empty;
            this.typeName = typeName ?? string.Empty;
            this.extensions = extensions ?? new string[0];
            imageMode = IsImageOnly(this.extensions);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Background;
            AllowDrop = true;

            backButton.Text = "返回";
            backButton.Icon = UiIconKind.Back;
            backButton.Click += delegate { RequestCancel(); };
            Controls.Add(backButton);

            placeList.DrawMode = DrawMode.OwnerDrawVariable;
            placeList.AccessibleName = "位置";
            placeList.MeasureItem += MeasurePlace;
            placeList.DrawItem += DrawPlace;
            placeList.MouseUp += PlaceList_MouseUp;
            placeList.KeyDown += PlaceList_KeyDown;
            Controls.Add(placeList);

            upButton.Text = "上一级";
            upButton.Icon = UiIconKind.Back;
            upButton.Font = Theme.CaptionFont;
            upButton.Click += delegate { GoUp(); };
            Controls.Add(upButton);

            allButton.Text = "全选";
            allButton.Font = Theme.CaptionFont;
            allButton.Click += delegate { ToggleAllListed(); };
            Controls.Add(allButton);

            if (imageMode)
            {
                grid.AccessibleName = "文件夹和照片";
                grid.IsChecked = delegate(string path) { return checkedSet.Contains(path); };
                grid.FolderOpened += delegate(string path) { Navigate(path); };
                grid.ImageSelected += delegate(string path) { StartPreview(path); };
                grid.CheckToggled += delegate(string path) { ToggleFile(path); };
                grid.ImageActivated += delegate(string path) { ToggleFile(path); };
                Controls.Add(grid);
            }
            else
            {
                fileList.DrawMode = DrawMode.OwnerDrawFixed;
                fileList.AccessibleName = "文件夹和文件";
                fileList.DrawItem += DrawFileRow;
                fileList.SelectedIndexChanged += delegate { PreviewListSelection(); };
                fileList.MouseDown += FileList_MouseDown;
                fileList.MouseUp += FileList_MouseUp;
                fileList.MouseDoubleClick += FileList_MouseDoubleClick;
                Controls.Add(fileList);
            }

            checkButton.Text = "勾选这个文件";
            checkButton.Click += delegate { if (previewPath != null) ToggleFile(previewPath); };
            Controls.Add(checkButton);

            pathBox.BorderStyle = BorderStyle.None;
            pathBox.BackColor = Theme.SearchBg;
            pathBox.ForeColor = Theme.TextPrimary;
            pathBox.Font = Theme.BodyFont;
            pathBox.AccessibleName = "文件或文件夹路径";
            pathBox.HandleCreated += delegate { SendMessage(pathBox.Handle, EM_SETCUEBANNER, (IntPtr)1, "粘贴文件或文件夹路径后回车：文件会被勾上，文件夹会被打开"); };
            pathBox.GotFocus += delegate { pathBox.BackColor = Color.White; Invalidate(pathFrame); };
            pathBox.LostFocus += delegate { pathBox.BackColor = Theme.SearchBg; Invalidate(pathFrame); };
            pathBox.TextChanged += delegate
            {
                if (settingText) return;
                notice = null;
                RefreshState();
            };
            Controls.Add(pathBox);

            confirmButton.Text = "添加";
            confirmButton.Accent = true;
            confirmButton.Click += delegate { Confirm(); };
            Controls.Add(confirmButton);

            cancelButton.Text = "取消";
            cancelButton.Click += delegate { RequestCancel(); };
            Controls.Add(cancelButton);

            BuildPlaces();
            currentFolder = StartFolder(initialFolder);
            RefreshPlaces();
            RefreshState();
        }

        /// <summary>
        /// 扩展名都在「照片」分类接收的格式里（含 HEIC、TIFF 等，解不开的格子显示「无法预览」）时用缩略图网格；
        /// 不限类型或混有其它类型时用列表。
        /// </summary>
        internal static bool IsImageOnly(string[] extensions)
        {
            if (extensions == null || extensions.Length == 0) return false;
            string[] photo = MediaRules.ResourceCategoryExt("照片") ?? MediaRules.ImageExt;
            foreach (string extension in extensions)
                if (!PathPickerDialog.HasExtension(extension, photo)) return false;
            return true;
        }

        // ===== 对外：测试与宿主 =====

        internal string CurrentFolder { get { return currentFolder; } }
        internal bool ShowingChecked { get { return showingChecked; } }
        internal bool Loading { get { return loading; } }
        internal bool Checking { get { return checking; } }
        internal bool PreviewLoading { get { return previewLoading; } }
        internal bool PreviewFailed { get { return previewFailed; } }
        internal string PreviewPath { get { return previewPath; } }
        internal List<string> CheckedFiles { get { return new List<string>(checkedFiles); } }
        internal List<string> ListedFiles { get { return new List<string>(listedFiles); } }

        /// <summary>预览区里的文件信息，「名称：值」逐行拼起来（测试读这里）。</summary>
        internal string PreviewText
        {
            get
            {
                List<string> parts = new List<string>();
                foreach (KeyValuePair<string, string> pair in previewInfo) parts.Add(pair.Key + "：" + pair.Value);
                return string.Join("\n", parts.ToArray());
            }
        }

        /// <summary>底部状态行：有提示时显示提示，否则是已选数量和说明。</summary>
        internal string StatusText
        {
            get
            {
                if (notice != null) return notice;
                if (checking) return "正在检查…";
                return Summary();
            }
        }

        /// <summary>焦点放到中栏（打开页面时调用）。</summary>
        internal void FocusBrowse()
        {
            if (imageMode) grid.Focus();
            else fileList.Focus();
        }

        /// <summary>主窗口按光标位置路由来的滚轮：光标在缩略图网格上就由网格滚动；原生列表返回 false，由列表自己滚动。</summary>
        internal bool HandleWheel(Point screen, int delta)
        {
            if (imageMode && grid.Visible && grid.RectangleToScreen(grid.ClientRectangle).Contains(screen))
            {
                grid.ScrollByWheel(delta);
                return true;
            }
            return false;
        }

        // ===== 位置 =====

        private void BuildPlaces()
        {
            string last;
            lastFolders.TryGetValue(typeName, out last);
            AddPlace("上次添加", last, GlyphFolder);
            switch (typeName)
            {
                case "照片": AddPlace("图片", SafeSpecialFolder(Environment.SpecialFolder.MyPictures), GlyphPicture); break;
                case "音乐": AddPlace("音乐", SafeSpecialFolder(Environment.SpecialFolder.MyMusic), GlyphAudio); break;
                case "视频": AddPlace("视频", SafeSpecialFolder(Environment.SpecialFolder.MyVideos), GlyphVideo); break;
            }
            AddPlace("桌面", SafeSpecialFolder(Environment.SpecialFolder.DesktopDirectory), GlyphFolder);
            string downloads = null;
            try { downloads = Scanner.KnownFolderPath(DownloadsFolder); } catch (Exception ex) { Paths.LogError(ex); }
            AddPlace("下载", downloads, GlyphDownload);
            AddPlace("文档", SafeSpecialFolder(Environment.SpecialFolder.MyDocuments), GlyphFolder);
        }

        private static string SafeSpecialFolder(Environment.SpecialFolder folder)
        {
            try { return Environment.GetFolderPath(folder); }
            catch (PlatformNotSupportedException) { return null; }
        }

        private void AddPlace(string label, string path, string glyph)
        {
            string full = LocalScanScope.NormalizePath(path);
            if (string.IsNullOrEmpty(full)) return;
            foreach (PlaceRow place in places)
                if (string.Equals(place.Path, full, StringComparison.OrdinalIgnoreCase)) return;
            // 只看本机盘符：断开的网络映射盘查目录是否存在会卡住界面
            if (!CoverPickerDialog.IsQuickLocal(full)) return;
            try { if (!Directory.Exists(full)) return; } catch { return; }
            places.Add(new PlaceRow { Kind = PlaceKind.Place, Title = label, Path = full, Glyph = glyph });
        }

        /// <summary>起始文件夹：调用方指定的、这类文件上次添加的、对应的系统文件夹，取第一个能用的。</summary>
        private string StartFolder(string initialFolder)
        {
            string wanted = LocalScanScope.NormalizePath(initialFolder);
            if (wanted != null)
            {
                if (CoverPickerDialog.IsQuickLocal(wanted)) { try { if (Directory.Exists(wanted)) return wanted; } catch { } }
                else return wanted;   // 网络路径不在界面线程上检查，读取失败时在中栏说明
            }
            return places.Count > 0 ? places[0].Path : null;   // 「上次添加」排在最前，其次是对应的系统文件夹
        }

        private void LoadDrivesAsync()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<LocalScanScopeDrive> loaded;
                try { loaded = LocalScanScope.Drives(); }
                catch (Exception ex) { Paths.LogError(ex); loaded = new List<LocalScanScopeDrive>(); }
                Post(delegate
                {
                    drives = loaded;
                    RefreshPlaces();
                });
            });
        }

        private void RefreshPlaces()
        {
            List<PlaceRow> rows = new List<PlaceRow>();
            rows.Add(new PlaceRow
            {
                Kind = PlaceKind.Checked, Glyph = GlyphChecked,
                Title = checkedFiles.Count > 0 ? "已选的文件（" + checkedFiles.Count + "）" : "已选的文件"
            });
            if (places.Count > 0)
            {
                rows.Add(new PlaceRow { Kind = PlaceKind.Header, Title = "常用位置", Enabled = false });
                rows.AddRange(places);
            }
            rows.Add(new PlaceRow { Kind = PlaceKind.Header, Title = "磁盘", Enabled = false });
            if (drives == null) rows.Add(new PlaceRow { Kind = PlaceKind.Note, Title = "正在读取磁盘…", Enabled = false });
            else if (drives.Count == 0) rows.Add(new PlaceRow { Kind = PlaceKind.Note, Title = "没有找到可用的磁盘", Enabled = false });
            else
                foreach (LocalScanScopeDrive drive in drives)
                    rows.Add(new PlaceRow
                    {
                        Kind = PlaceKind.Drive, Title = drive.Title, Path = LocalScanScope.NormalizePath(drive.Path),
                        Glyph = GlyphDrive, Enabled = drive.Supported
                    });
            int top = placeList.TopIndex;
            placeList.BeginUpdate();
            try
            {
                placeList.Items.Clear();
                placeList.Items.AddRange(rows.ToArray());
            }
            finally { placeList.EndUpdate(); }
            placeList.ResetHover();
            if (top > 0 && top < placeList.Items.Count) placeList.TopIndex = top;
        }

        /// <summary>「已选的文件」一行的数量变了：只改这一行的文字并重画，不重建列表（保留滚动位置）。</summary>
        private void RefreshCheckedPlace()
        {
            PlaceRow row = PlaceAt(0);
            if (row == null || row.Kind != PlaceKind.Checked) return;
            row.Title = checkedFiles.Count > 0 ? "已选的文件（" + checkedFiles.Count + "）" : "已选的文件";
            placeList.InvalidateRow(0);
        }

        private PlaceRow PlaceAt(int index)
        {
            return index < 0 || index >= placeList.Items.Count ? null : placeList.Items[index] as PlaceRow;
        }

        /// <summary>左栏画成选中的一行：显示已选时是「已选的文件」，否则是路径前缀最长的位置。</summary>
        private PlaceRow ActivePlace()
        {
            PlaceRow best = null;
            for (int i = 0; i < placeList.Items.Count; i++)
            {
                PlaceRow row = PlaceAt(i);
                if (row == null) continue;
                if (showingChecked) { if (row.Kind == PlaceKind.Checked) return row; continue; }
                if (row.Path == null || currentFolder == null || !CoverPickerDialog.IsWithin(row.Path, currentFolder)) continue;
                if (best == null || row.Path.Length > best.Path.Length) best = row;
            }
            return best;
        }

        private void OpenPlace(PlaceRow row)
        {
            if (row == null || !row.Enabled) return;
            if (row.Kind == PlaceKind.Checked) ShowChecked();
            else if (row.Path != null) Navigate(row.Path);
        }

        private void PlaceList_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            OpenPlace(PlaceAt(placeList.HitRow(e.Location)));
        }

        private void PlaceList_KeyDown(object sender, KeyEventArgs e)
        {
            int step = e.KeyCode == Keys.Down ? 1 : e.KeyCode == Keys.Up ? -1 : 0;
            if (step == 0)
            {
                if (e.KeyCode != Keys.Right && e.KeyCode != Keys.Space) return;
                OpenPlace(PlaceAt(placeList.SelectedIndex));
                if (e.KeyCode == Keys.Right) FocusBrowse();
            }
            else
            {
                int index = placeList.SelectedIndex;
                if (index < 0 && step < 0) index = placeList.Items.Count;
                for (index += step; index >= 0 && index < placeList.Items.Count; index += step)
                {
                    PlaceRow row = PlaceAt(index);
                    if (row == null || !row.Enabled) continue;
                    placeList.SelectedIndex = index;
                    OpenPlace(row);
                    break;
                }
            }
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        private void MeasurePlace(object sender, MeasureItemEventArgs e)
        {
            PlaceRow row = PlaceAt(e.Index);
            e.ItemHeight = Dpi(row == null || row.Kind == PlaceKind.Header || row.Kind == PlaceKind.Note ? PlaceHeaderHeight : PlaceRowHeight);
        }

        private void DrawPlace(object sender, DrawItemEventArgs e)
        {
            PlaceRow item = PlaceAt(e.Index);
            Rectangle row = e.Bounds;
            if (item == null || row.Width <= 0 || row.Height <= 0) return;
            using (BufferedGraphics buffer = BufferedGraphicsManager.Current.Allocate(e.Graphics, row))
            {
                Graphics g = buffer.Graphics;
                using (SolidBrush back = new SolidBrush(Theme.Card)) g.FillRectangle(back, row);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                if (item.Kind == PlaceKind.Header || item.Kind == PlaceKind.Note)
                {
                    Font font = item.Kind == PlaceKind.Header ? Theme.FontOf(9F, FontStyle.Bold) : Theme.CaptionFont;
                    Color color = item.Kind == PlaceKind.Header ? Theme.TextSecondary : Theme.TextTertiary;
                    using (SolidBrush brush = new SolidBrush(color))
                        Draw.DrawStringLeft(g, item.Title, font, brush,
                            new Rectangle(row.X + Dpi(12), row.Y + Dpi(4), row.Width - Dpi(24), row.Height - Dpi(4)));
                }
                else
                {
                    bool chosen = ReferenceEquals(item, ActivePlace());
                    bool hover = item.Enabled && (e.Index == placeList.HoverIndex
                        || (placeList.KeyboardActive && e.Index == placeList.SelectedIndex));
                    if (chosen || hover)
                        using (SolidBrush b = new SolidBrush(chosen ? Theme.SelectionBg : Theme.RowHover))
                            Draw.FillRoundedRect(g, b, new Rectangle(row.X + Dpi(2), row.Y + 1, row.Width - Dpi(4), row.Height - 2), Dpi(6));
                    int iconSize = Dpi(18);
                    Rectangle icon = new Rectangle(row.X + Dpi(12), row.Y + (row.Height - iconSize) / 2, iconSize, iconSize);
                    Color iconColor = !item.Enabled ? Theme.TextTertiary : chosen || item.Kind == PlaceKind.Checked ? Theme.Accent
                        : item.Kind == PlaceKind.Drive ? Theme.IconColor : Color.FromArgb(232, 160, 40);
                    Draw.DrawGlyph(g, item.Glyph, icon, iconColor);
                    using (SolidBrush brush = new SolidBrush(!item.Enabled ? Theme.TextTertiary : chosen ? Theme.AccentText : Theme.TextPrimary))
                        Draw.DrawStringLeft(g, item.Title, chosen ? Theme.FontOf(9.5F, FontStyle.Bold) : Theme.BodyFont, brush,
                            new Rectangle(icon.Right + Dpi(10), row.Y, Math.Max(0, row.Right - icon.Right - Dpi(18)), row.Height));
                    if (item.Kind == PlaceKind.Checked)
                        using (Pen line = new Pen(Theme.Separator, 1))
                            g.DrawLine(line, row.X + Dpi(10), row.Bottom - 1, row.Right - Dpi(10), row.Bottom - 1);
                }
                buffer.Render(e.Graphics);
            }
        }

        // ===== 中栏：浏览 =====

        /// <summary>进入一个文件夹：后台读取子文件夹和匹配类型的文件，读完前中栏显示「正在读取」。</summary>
        internal void Navigate(string folder)
        {
            string full = LocalScanScope.NormalizePath(folder);
            if (full == null) return;
            if (!string.Equals(full, currentFolder, StringComparison.OrdinalIgnoreCase)) pendingSelect = null;
            currentFolder = full;
            showingChecked = false;
            LoadFolder();
        }

        private void LoadFolder()
        {
            string folder = currentFolder;
            int gen = ++loadGen;
            folderSummary = string.Empty;
            listedFiles.Clear();
            loading = folder != null;
            SetBrowseItems(null, null, folder == null ? "从左侧选一个位置。" : "正在读取…");
            placeList.Invalidate();
            RefreshState();
            if (folder == null) return;
            string[] wanted = extensions;
            Func<bool> stale = delegate { return gen != Volatile.Read(ref loadGen); };   // 换了文件夹或页面已关闭
            ThreadPool.QueueUserWorkItem(delegate
            {
                LocalScanScopeChildren folders = null;
                List<FileRow> files = new List<FileRow>();
                string error = null;
                int omitted = 0;
                try
                {
                    folders = LocalScanScope.ListSubfolders(folder, null);
                    error = folders.Error;
                    if (error == null && !stale())
                        foreach (FileInfo info in PathPickerDialog.ListFileInfos(folder, wanted, MaxListed, stale, out omitted, out error))
                        {
                            // 大小和时间来自目录枚举；个别文件取不到时只是不显示
                            FileRow row = new FileRow { Kind = FileKind.File, Path = info.FullName, Name = info.Name };
                            try { row.Length = info.Length; row.Modified = info.LastWriteTime; }
                            catch (IOException) { }
                            catch (UnauthorizedAccessException) { }
                            files.Add(row);
                        }
                }
                catch (Exception ex) { Paths.LogError(ex); error = ex.Message; }
                LocalScanScopeChildren listed = folders;
                List<FileRow> found = files;
                string failure = error;
                int more = omitted;
                Post(delegate { if (gen == loadGen) FinishLoad(listed, found, more, failure); });
            });
        }

        private void FinishLoad(LocalScanScopeChildren folders, List<FileRow> files, int omitted, string error)
        {
            loading = false;
            List<FileRow> subfolders = new List<FileRow>();
            bool omittedMore = false;
            if (folders != null)
            {
                foreach (string path in folders.Paths)
                    subfolders.Add(new FileRow { Kind = FileKind.Folder, Path = path, Name = Path.GetFileName(path) });
                omitted += folders.Omitted;
                omittedMore = folders.OmittedMore;
            }
            listedFiles.Clear();
            foreach (FileRow file in files) listedFiles.Add(file.Path);
            string empty = error != null ? "无法读取这个文件夹：" + error
                : "这个文件夹里没有" + (typeName.Length > 0 ? typeName : "可添加的文件") + "，也没有子文件夹。";
            SetBrowseItems(subfolders, files, empty);
            List<string> parts = new List<string>();
            if (subfolders.Count > 0) parts.Add(subfolders.Count + " 个文件夹");
            if (files.Count > 0) parts.Add(files.Count + " 个文件");
            folderSummary = string.Join(" · ", parts.ToArray());
            if (omittedMore) folderSummary += "（还有更多未列出）";
            else if (omitted > 0) folderSummary += "（还有 " + omitted + " 项未列出）";
            string select = pendingSelect;
            pendingSelect = null;
            if (select != null) SelectInBrowse(select);
            // 子文件夹列出来了、文件却读取失败：中栏不是空的，空白提示显示不出来，改在状态行说明
            if (error != null && subfolders.Count + files.Count > 0) SetNotice("无法读取这个文件夹里的文件：" + error, true);
            RefreshState();
        }

        /// <summary>中栏改为显示所有勾选的文件（来自哪个文件夹都在一起），可以在这里取消勾选。</summary>
        internal void ShowChecked()
        {
            loadGen++;   // 还在读的文件夹作废
            loading = false;
            showingChecked = true;
            pendingSelect = null;
            listedFiles.Clear();
            listedFiles.AddRange(checkedFiles);
            List<FileRow> rows = new List<FileRow>();
            foreach (string path in checkedFiles)
                rows.Add(new FileRow { Kind = FileKind.File, Path = path, Name = SafeFileName(path), Detail = SafeDirectory(path) });
            SetBrowseItems(null, rows, "还没有勾选文件。从左侧选位置，在文件上点勾选框或双击就能勾上。");
            folderSummary = checkedFiles.Count > 0 ? "共 " + checkedFiles.Count + " 个" : string.Empty;
            placeList.Invalidate();
            RefreshState();
        }

        /// <summary>把一批文件夹和文件放进中栏（照片用网格，其它用列表）；两者都为空时显示 empty。</summary>
        private void SetBrowseItems(List<FileRow> folders, List<FileRow> files, string empty)
        {
            if (imageMode)
            {
                List<CoverImageGrid.Item> items = new List<CoverImageGrid.Item>();
                if (folders != null) foreach (FileRow row in folders) items.Add(new CoverImageGrid.Item { Folder = true, Path = row.Path, Name = row.Name });
                if (files != null) foreach (FileRow row in files) items.Add(new CoverImageGrid.Item { Path = row.Path, Name = row.Name });
                grid.SetItems(items, empty);
                return;
            }
            List<FileRow> rows = new List<FileRow>();
            if (folders != null) rows.AddRange(folders);
            if (files != null) rows.AddRange(files);
            if (rows.Count == 0) rows.Add(new FileRow { Kind = FileKind.Note, Name = empty });
            fileList.BeginUpdate();
            try
            {
                fileList.Items.Clear();
                fileList.Items.AddRange(rows.ToArray());
            }
            finally { fileList.EndUpdate(); }
            fileList.ResetHover();
            pressedListFolder = -1;
        }

        /// <summary>中栏里选中这个文件（不改勾选），并在右栏预览它。</summary>
        private void SelectInBrowse(string path)
        {
            if (imageMode) grid.SelectPath(path);
            else
                for (int i = 0; i < fileList.Items.Count; i++)
                {
                    FileRow row = fileList.Items[i] as FileRow;
                    if (row == null || row.Kind != FileKind.File || !string.Equals(row.Path, path, StringComparison.OrdinalIgnoreCase)) continue;
                    fileList.SelectedIndex = i;
                    break;
                }
            StartPreview(path);
        }

        internal void GoUp()
        {
            if (showingChecked || checking) return;
            string parent = ParentFolder(currentFolder);
            if (parent == null) return;
            string from = currentFolder;
            Navigate(parent);
            if (imageMode) grid.RevealAfterLoad(from);
        }

        private static string ParentFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return null;
            try { return LocalScanScope.NormalizePath(Path.GetDirectoryName(folder)); }
            catch (ArgumentException) { return null; }
            catch (PathTooLongException) { return null; }
        }

        private static string SafeDirectory(string path)
        {
            try { return LocalScanScope.NormalizePath(Path.GetDirectoryName(path)); }
            catch (ArgumentException) { return null; }
            catch (PathTooLongException) { return null; }
        }

        private static string SafeFileName(string path)
        {
            try { return Path.GetFileName(path); }
            catch (ArgumentException) { return path; }
        }

        private FileRow ListRow(int index)
        {
            return index < 0 || index >= fileList.Items.Count ? null : fileList.Items[index] as FileRow;
        }

        /// <summary>列表里行左侧的勾选框区域（放宽到行首，好点中）。</summary>
        private bool HitsListCheck(int index, Point p)
        {
            FileRow row = ListRow(index);
            if (row == null || row.Kind != FileKind.File) return false;
            Rectangle bounds = fileList.GetItemRectangle(index);
            return p.X < bounds.X + Dpi(40);
        }

        private void FileList_MouseDown(object sender, MouseEventArgs e)
        {
            pressedListCheck = false;
            pressedListFolder = -1;
            if (e.Button != MouseButtons.Left) return;
            int index = fileList.HitRow(e.Location);
            FileRow row = ListRow(index);
            if (row == null) return;
            if (row.Kind == FileKind.Folder) { pressedListFolder = index; return; }
            if (row.Kind == FileKind.File && HitsListCheck(index, e.Location))
            {
                pressedListCheck = true;
                ToggleFile(row.Path);
            }
        }

        private void FileList_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            int index = fileList.HitRow(e.Location);
            int pressed = pressedListFolder;
            pressedListFolder = -1;
            FileRow row = ListRow(index);
            if (pressed >= 0 && index == pressed && row != null && row.Kind == FileKind.Folder) Navigate(row.Path);
        }

        private void FileList_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || pressedListCheck) return;
            FileRow row = ListRow(fileList.HitRow(e.Location));
            if (row != null && row.Kind == FileKind.File) ToggleFile(row.Path);
        }

        private void PreviewListSelection()
        {
            FileRow row = ListRow(fileList.SelectedIndex);
            if (row != null && row.Kind == FileKind.File) StartPreview(row.Path);
        }

        /// <summary>中栏里当前选中的文件夹（回车进入它）；没有时返回 null。</summary>
        private string SelectedFolder()
        {
            if (imageMode) return grid.Focused ? grid.SelectedFolder : null;
            FileRow row = fileList.Focused ? ListRow(fileList.SelectedIndex) : null;
            return row != null && row.Kind == FileKind.Folder ? row.Path : null;
        }

        /// <summary>中栏里当前选中的文件（空格切换它的勾选）；没有时返回 null。</summary>
        private string SelectedFile()
        {
            if (imageMode) return grid.SelectedImage;
            FileRow row = ListRow(fileList.SelectedIndex);
            return row != null && row.Kind == FileKind.File ? row.Path : null;
        }

        // ===== 勾选 =====

        /// <summary>切换一个文件的勾选；超过上限时提示，不勾上。检查期间不改动勾选。</summary>
        internal void ToggleFile(string path)
        {
            if (checking || string.IsNullOrEmpty(path)) return;
            if (checkedSet.Contains(path)) RemoveChecked(path);
            else if (checkedFiles.Count >= PathPickerDialog.MaxCheckedFiles)
            {
                SetNotice("一次最多选 " + PathPickerDialog.MaxCheckedFiles + " 个文件。", true);
                return;
            }
            else AddChecked(path);
            notice = null;
            CheckedChanged();
        }

        /// <summary>中栏列出的文件全部勾上；已经全部勾上时全部取消。超过上限的不勾。</summary>
        internal void ToggleAllListed()
        {
            if (checking) return;
            if (listedFiles.Count == 0)
            {
                SetNotice(showingChecked ? "还没有勾选文件。" : "这个文件夹里没有可以勾选的文件。", false);
                return;
            }
            bool all = AllListedChecked();
            int changed = 0, over = 0;
            foreach (string file in listedFiles)
            {
                if (all) { RemoveChecked(file); changed++; continue; }
                if (checkedSet.Contains(file)) continue;
                if (checkedFiles.Count >= PathPickerDialog.MaxCheckedFiles) { over++; continue; }
                AddChecked(file);
                changed++;
            }
            CheckedChanged();
            if (over > 0) SetNotice("一次最多选 " + PathPickerDialog.MaxCheckedFiles + " 个文件，有 " + over + " 个没有勾上。", true);
            else SetNotice((all ? "已取消勾选这里列出的 " : "已勾选这里列出的 ") + changed + " 个文件；共选 " + checkedFiles.Count + " 个。", false);
        }

        private bool AllListedChecked()
        {
            if (listedFiles.Count == 0) return false;
            foreach (string file in listedFiles) if (!checkedSet.Contains(file)) return false;
            return true;
        }

        private void AddChecked(string path)
        {
            if (checkedSet.Add(path)) checkedFiles.Add(path);
        }

        private void RemoveChecked(string path)
        {
            if (!checkedSet.Remove(path)) return;
            checkedFiles.RemoveAll(delegate(string value) { return string.Equals(value, path, StringComparison.OrdinalIgnoreCase); });
        }

        /// <summary>勾选变化后：重画中栏和左栏的数量（「已选的文件」里取消的项留在原处，可以再勾回来）。</summary>
        private void CheckedChanged()
        {
            HashSet<string> folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in checkedFiles) folders.Add(SafeDirectory(path) ?? string.Empty);
            checkedFolderCount = folders.Count;
            if (imageMode) grid.Invalidate();
            else fileList.Invalidate();
            RefreshCheckedPlace();
            RefreshState();
        }

        /// <summary>
        /// 拖进或粘贴进来的项：先在后台分清文件夹和文件（网络路径可能很慢，不在界面线程上访问磁盘），
        /// 只有一个文件夹时打开它；文件里类型匹配的全部勾上，其余和文件夹一起跳过并说明。
        /// 不限类型的分类也靠这一步区分，文件夹不会被当成文件勾上。文件是否还在留到确定时检查。
        /// </summary>
        internal void DropMany(string[] dropped)
        {
            if (checking || dropped == null) return;
            List<string> items = new List<string>();
            foreach (string item in dropped) if (!string.IsNullOrEmpty(item)) items.Add(item);
            if (items.Count == 0) return;
            checking = true;
            notice = null;
            RefreshState();
            int version = ++checkVersion;
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<string> folders = new List<string>();
                List<string> files = new List<string>();
                foreach (string item in items)
                {
                    if (version != Volatile.Read(ref checkVersion)) return;   // 页面已关闭
                    if (Directory.Exists(item)) folders.Add(item);   // 不抛异常：路径非法或无权访问时返回 false，按文件处理
                    else files.Add(item);
                }
                Post(delegate { if (version == checkVersion) FinishDrop(folders, files); });
            });
        }

        private void FinishDrop(List<string> folders, List<string> files)
        {
            checking = false;
            if (files.Count == 0 && folders.Count == 1)
            {
                Navigate(folders[0]);
                SetNotice("已打开拖进来的文件夹。", false);
                return;
            }
            int added = 0, skipped = 0, over = 0;
            foreach (string item in files)
            {
                if (!PathPickerDialog.HasExtension(item, extensions)) { skipped++; continue; }
                if (checkedSet.Contains(item)) continue;
                if (checkedFiles.Count >= PathPickerDialog.MaxCheckedFiles) { over++; continue; }
                AddChecked(item);
                added++;
            }
            CheckedChanged();
            string folderNote = folders.Count > 0 ? "；" + folders.Count + " 个文件夹没有加入（整个文件夹请用「添加文件夹…」）" : string.Empty;
            if (over > 0) SetNotice("一次最多选 " + PathPickerDialog.MaxCheckedFiles + " 个文件，有 " + over + " 个没有加入" + folderNote + "。", true);
            else if (skipped > 0)
                SetNotice("已勾选 " + added + " 个；" + skipped + " 项不是" + TypeText() + "文件，已跳过" + folderNote + "。", false);
            else SetNotice("已勾选拖进来的 " + added + " 个文件" + folderNote + "。左侧「已选的文件」里可以查看。", false);
        }

        /// <summary>Ctrl+V：剪贴板里有文件时按拖进来处理。剪贴板被别的程序占用时说明原因。</summary>
        private bool PasteFiles()
        {
            string[] paths;
            try
            {
                IDataObject data = Clipboard.GetDataObject();
                if (data == null || !data.GetDataPresent(DataFormats.FileDrop)) return false;
                paths = data.GetData(DataFormats.FileDrop) as string[];
            }
            catch (ExternalException ex)
            {
                SetNotice("无法读取剪贴板：" + ex.Message, true);
                return true;
            }
            if (paths == null || paths.Length == 0) return false;
            DropMany(paths);
            return true;
        }

        // ===== 右栏：预览 =====

        private void ClearPreview()
        {
            previewGen++;
            if (previewImage != null) { previewImage.Dispose(); previewImage = null; }
            previewPath = null;
            previewInfo.Clear();
            previewLoading = false;
            previewFailed = false;
            previewError = string.Empty;
            Invalidate(previewFrame);
            RefreshState();
        }

        /// <summary>
        /// 后台读取预览：图片解码一张有上限的预览图并取原始尺寸；音乐读标签（标题、歌手、专辑和封面）；
        /// 其它只取大小和修改时间。读不出画面不影响勾选和添加，只在预览区说明。
        /// </summary>
        internal void StartPreview(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (string.Equals(previewPath, path, StringComparison.OrdinalIgnoreCase) && (previewLoading || previewInfo.Count > 0 || previewFailed))
            {
                RefreshState();
                return;
            }
            ClearPreview();
            previewPath = path;
            previewLoading = true;
            int gen = previewGen;
            bool image = PathPickerDialog.HasExtension(path, MediaRules.ImageExt);
            bool audio = PathPickerDialog.HasExtension(path, MediaRules.AudioExt);
            ThreadPool.QueueUserWorkItem(delegate
            {
                Bitmap bmp = null;
                string failure = null;
                List<KeyValuePair<string, string>> info = new List<KeyValuePair<string, string>>();
                try
                {
                    FileInfo file = new FileInfo(path);
                    if (!file.Exists) failure = "文件不存在或当前不可访问。";
                    else
                    {
                        if (image)
                        {
                            int width, height;
                            bmp = CoverPickerDialog.LoadPreviewSource(path, out width, out height);
                            if (bmp == null) failure = "这张图片无法预览，可能已损坏或格式不受支持。";
                            else if (width > 0 && height > 0) info.Add(new KeyValuePair<string, string>("尺寸", width + " × " + height));
                        }
                        else if (audio)
                        {
                            MusicTrackInfo track = MusicMetadata.Read(path);
                            try
                            {
                                info.Add(new KeyValuePair<string, string>("标题", track.Title ?? string.Empty));
                                info.Add(new KeyValuePair<string, string>("歌手", track.Artist ?? string.Empty));
                                info.Add(new KeyValuePair<string, string>("专辑", track.Album ?? string.Empty));
                                if (track.Cover != null) bmp = ScaledCopy(track.Cover, CoverMax);
                            }
                            finally { track.Dispose(); }
                        }
                        info.Add(new KeyValuePair<string, string>("大小", LocalScanScope.FormatBytes(file.Length)));
                        info.Add(new KeyValuePair<string, string>("修改时间", file.LastWriteTime.ToString("yyyy-MM-dd HH:mm")));
                    }
                }
                catch (Exception ex)
                {
                    // 线程池上不能抛出（会让程序退出）：说明原因，勾选和添加不受影响
                    if (!(ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException
                        || ex is NotSupportedException || ex is System.Security.SecurityException || ex is OutOfMemoryException))
                        Paths.LogError(ex);
                    if (bmp != null) { bmp.Dispose(); bmp = null; }
                    failure = "无法读取这个文件：" + ex.Message;
                }
                string folder = SafeDirectory(path);
                if (folder != null) info.Add(new KeyValuePair<string, string>("位置", folder));
                Bitmap result = bmp;
                Post(delegate
                {
                    if (gen != previewGen) { if (result != null) result.Dispose(); return; }
                    previewLoading = false;
                    previewImage = result;
                    previewFailed = failure != null;
                    previewError = failure ?? string.Empty;
                    previewInfo.Clear();
                    previewInfo.AddRange(info);
                    Invalidate(previewFrame);
                    RefreshState();
                }, delegate { if (result != null) result.Dispose(); });
            });
            Invalidate(previewFrame);
            RefreshState();
        }

        /// <summary>按长边上限复制一份（音乐封面可能很大）；只在后台线程调用，失败返回 null。</summary>
        private static Bitmap ScaledCopy(Image source, int max)
        {
            try
            {
                int w = source.Width, h = source.Height;
                if (w <= 0 || h <= 0) return null;
                double scale = Math.Min(1.0, max / (double)Math.Max(w, h));
                int nw = Math.Max(1, (int)Math.Round(w * scale)), nh = Math.Max(1, (int)Math.Round(h * scale));
                Bitmap copy = new Bitmap(nw, nh, PixelFormat.Format32bppPArgb);
                try
                {
                    using (Graphics g = Graphics.FromImage(copy))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(source, new Rectangle(0, 0, nw, nh));
                    }
                    Bitmap done = copy;
                    copy = null;
                    return done;
                }
                finally { if (copy != null) copy.Dispose(); }
            }
            catch (Exception) { return null; }   // 封面数据损坏或内存不足：只是不显示封面
        }

        // ===== 状态行与按钮 =====

        private void SetNotice(string text, bool isError)
        {
            notice = text;
            noticeIsError = isError;
            RefreshState();
        }

        private string TypeText()
        {
            string types = DescribeTypes();
            return types.Length > 0 ? " " + types + " " : "可添加的";
        }

        private string DescribeTypes()
        {
            return PathPickerDialog.DescribeExtensions(extensions);
        }

        /// <summary>没有提示时状态行的内容：还没勾选时说明怎么选；勾了以后是数量、来自几个文件夹和前几个文件名。</summary>
        private string Summary()
        {
            if (checkedFiles.Count == 0)
                return "单击文件在右侧预览，点勾选框或双击勾选要添加的" + (typeName.Length > 0 ? typeName : "文件")
                    + "；可以在几个文件夹里一起选，Ctrl+A 全选当前文件夹。";
            List<string> names = new List<string>();
            for (int i = 0; i < checkedFiles.Count && i < 3; i++) names.Add(SafeFileName(checkedFiles[i]));
            return "已选 " + checkedFiles.Count + " 个（来自 " + checkedFolderCount + " 个文件夹）：" + string.Join("、", names.ToArray())
                + (checkedFiles.Count > 3 ? " 等" : string.Empty) + "。左侧「已选的文件」可以查看和取消。";
        }

        private void RefreshState()
        {
            bool ready = !checking && (checkedFiles.Count > 0 || pathBox.Text.Trim().Length > 0);
            confirmButton.Text = checking ? "正在检查…" : checkedFiles.Count > 0 ? "添加 " + checkedFiles.Count + " 个" : "添加";
            confirmButton.Enabled = ready;
            confirmButton.Accent = ready;
            upButton.Enabled = !checking && !showingChecked && ParentFolder(currentFolder) != null;
            allButton.Enabled = !checking && listedFiles.Count > 0;
            allButton.Text = AllListedChecked() ? "取消全选" : "全选";
            bool previewChecked = previewPath != null && checkedSet.Contains(previewPath);
            checkButton.Enabled = !checking && previewPath != null;
            checkButton.Accent = false;
            checkButton.Text = previewChecked ? "取消勾选" : "勾选这个文件";
            Invalidate(statusRect);
            Invalidate(BrowseHeaderRect());
            Invalidate(previewFrame);
        }

        // ===== 确定 =====

        /// <summary>「添加」：输入框里的路径一起检查，再核对勾选的文件还在，全部通过才交给宿主。</summary>
        internal void Confirm()
        {
            Check(true);
        }

        /// <summary>
        /// 在后台检查：输入框里是文件夹就打开它（不结束）；是文件就勾上它。finish 时再核对每个勾选的文件现在还在，
        /// 已经不在（或其实是文件夹）的取消勾选并说明，留在页面里让用户再确认剩下的，不会把失效路径交给宿主。
        /// </summary>
        private void Check(bool finish)
        {
            if (checking) return;
            string cleaned = null;
            if (pathBox.Text.Trim().Length > 0)
            {
                string reason;
                cleaned = LocalScanScope.CleanTypedPath(pathBox.Text, out reason);
                if (cleaned == null) { SetNotice(reason ?? "路径格式不正确。", true); return; }
            }
            else if (!finish) return;
            else if (checkedFiles.Count == 0) { SetNotice("请先勾选要添加的文件。", true); return; }
            checking = true;
            notice = null;
            RefreshState();
            int version = ++checkVersion;
            string[] wanted = extensions;
            List<string> chosen = finish ? new List<string>(checkedFiles) : new List<string>();
            ThreadPool.QueueUserWorkItem(delegate
            {
                PathPickerCheck typed = null;
                bool folder = false;
                List<string> gone;
                try
                {
                    if (cleaned != null)
                    {
                        string full = Path.GetFullPath(cleaned);
                        if (Directory.Exists(full))
                        {
                            folder = true;
                            typed = new PathPickerCheck { Path = LocalScanScope.NormalizePath(full) ?? full };
                        }
                        else typed = PathPickerDialog.CheckPath(PathPickerMode.OpenFile, cleaned, wanted, false, null, null);
                    }
                    gone = PathPickerDialog.UnavailableFiles(chosen);
                }
                catch (Exception ex)
                {
                    if (!(ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException
                        || ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException))
                        Paths.LogError(ex);
                    typed = new PathPickerCheck { Error = "路径格式不正确或无法访问：" + ex.Message };
                    gone = new List<string>();
                }
                bool isFolder = folder;
                Post(delegate { if (version == checkVersion) FinishCheck(typed, isFolder, gone, finish); });
            });
        }

        private void FinishCheck(PathPickerCheck typed, bool folder, List<string> gone, bool finish)
        {
            checking = false;
            if (typed != null)
            {
                if (typed.Error != null) { SetNotice(typed.Error, true); return; }
                SetPathText(string.Empty);
                if (folder)
                {
                    Navigate(typed.Path);
                    SetNotice(finish ? "已打开输入的文件夹；勾选好文件后再点「添加」。" : "已打开输入的文件夹。", false);
                    return;
                }
                if (!checkedSet.Contains(typed.Path) && checkedFiles.Count >= PathPickerDialog.MaxCheckedFiles)
                {
                    SetNotice("一次最多选 " + PathPickerDialog.MaxCheckedFiles + " 个文件，输入的文件没有加入。", true);
                    return;
                }
                AddChecked(typed.Path);
                CheckedChanged();
                if (!finish)
                {
                    // 回车：勾上输入的文件，转到它所在的文件夹并选中预览
                    string dir = SafeDirectory(typed.Path);
                    if (dir != null && (showingChecked || !string.Equals(dir, currentFolder, StringComparison.OrdinalIgnoreCase)))
                    {
                        Navigate(dir);
                        pendingSelect = typed.Path;
                    }
                    else SelectInBrowse(typed.Path);
                    SetNotice("已勾选「" + SafeFileName(typed.Path) + "」。", false);
                    return;
                }
            }
            if (gone.Count > 0)
            {
                foreach (string path in gone) RemoveChecked(path);
                CheckedChanged();
                SetNotice(gone.Count + " 个已选的文件已不存在或不是文件（" + SafeFileName(gone[0]) + (gone.Count > 1 ? " 等" : string.Empty)
                    + "），已取消勾选" + (checkedFiles.Count > 0 ? "；确认剩下的 " + checkedFiles.Count + " 个请再点一次「添加」。" : "。"), true);
                return;
            }
            if (!finish) { RefreshState(); return; }
            if (checkedFiles.Count == 0) { SetNotice("请先勾选要添加的文件。", true); return; }
            string remember = !showingChecked && currentFolder != null ? currentFolder : SafeDirectory(checkedFiles[checkedFiles.Count - 1]);
            if (remember != null) lastFolders[typeName] = remember;
            RefreshState();
            List<string> result = new List<string>(checkedFiles);
            if (Confirmed != null) Confirmed(result);   // 宿主会关闭并释放本页：之后不再访问任何成员
        }

        private void SetPathText(string text)
        {
            settingText = true;
            try
            {
                pathBox.Text = text ?? string.Empty;
                pathBox.SelectionStart = pathBox.Text.Length;
            }
            finally { settingText = false; }
            RefreshState();
        }

        /// <summary>返回 / 取消：放到消息队列里再通知宿主，免得在按钮自己的点击事件里被释放。</summary>
        private void RequestCancel()
        {
            Post(delegate { if (Cancelled != null) Cancelled(); });
        }

        // ===== 生命周期、键盘与拖放 =====

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (started) return;
            started = true;
            RefreshLayout();
            LoadDrivesAsync();
            LoadFolder();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            bool inBox = pathBox.Focused;
            if (keyData == Keys.Escape) { RequestCancel(); return true; }
            if (keyData == Keys.Enter)
            {
                // 自绘按钮只认空格：回车时焦点在哪个按钮上就等同点哪个，在左栏就打开选中的位置，
                // 在输入框里只处理输入的路径，在中栏选中的是文件夹就进入它；只有焦点在中栏文件或「添加」上才是添加
                if (cancelButton.Focused || backButton.Focused) RequestCancel();
                else if (inBox) Check(false);
                else if (upButton.Focused) GoUp();
                else if (allButton.Focused) ToggleAllListed();
                else if (checkButton.Focused) { if (previewPath != null) ToggleFile(previewPath); }
                else if (placeList.Focused) OpenPlace(PlaceAt(placeList.SelectedIndex));
                else
                {
                    string folder = SelectedFolder();
                    if (folder != null) Navigate(folder);
                    else Confirm();
                }
                return true;
            }
            if (!inBox)
            {
                if (keyData == Keys.Back) { GoUp(); return true; }
                if (keyData == (Keys.Control | Keys.A)) { ToggleAllListed(); return true; }
                if (keyData == (Keys.Control | Keys.V) && PasteFiles()) return true;
                if (keyData == Keys.Space && (grid.Focused || fileList.Focused))
                {
                    string file = SelectedFile();
                    if (file != null) ToggleFile(file);
                    return true;
                }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnDragEnter(DragEventArgs e)
        {
            base.OnDragEnter(e);
            e.Effect = !checking && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        protected override void OnDragDrop(DragEventArgs e)
        {
            base.OnDragDrop(e);
            string[] dropped;
            try { dropped = e.Data.GetData(DataFormats.FileDrop) as string[]; }
            catch (ExternalException ex)
            {
                SetNotice("无法读取拖入的内容：" + ex.Message, true);
                return;
            }
            DropMany(dropped);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                checkVersion++;   // 还在后台的检查、目录读取和预览结果作废
                loadGen++;
                previewGen++;
                if (previewImage != null) { previewImage.Dispose(); previewImage = null; }
                // 网格和列表只有一个加进了页面：另一个不会随页面释放
                grid.Dispose();
                fileList.Dispose();
            }
            base.Dispose(disposing);
        }

        /// <summary>后台线程把结果交回界面线程；页面已关闭时丢弃。</summary>
        private void Post(Action action)
        {
            Post(action, null);
        }

        /// <summary>同上；结果被丢弃时调用 dropped 释放随结果带回的资源（如位图）。</summary>
        private void Post(Action action, Action dropped)
        {
            if (IsDisposed || !IsHandleCreated) { if (dropped != null) dropped(); return; }
            try
            {
                BeginInvoke((Action)delegate
                {
                    if (!IsDisposed && !Disposing) action();
                    else if (dropped != null) dropped();
                });
            }
            catch (ObjectDisposedException) { if (dropped != null) dropped(); }
            catch (InvalidOperationException) { if (dropped != null) dropped(); }
        }

        // ===== 布局 =====

        private int Dpi(int value)
        {
            return DpiHelper.Scale(value, DpiHelper.ForControl(this));
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            RefreshLayout();
        }

        /// <summary>按当前大小和 DPI 摆放各栏；宿主在窗口缩放、换屏幕后调用。DPI 变了时重新测量左栏行高。</summary>
        internal void RefreshLayout()
        {
            if (Width <= 0 || Height <= 0) return;
            int pad = Dpi(20), gap = Dpi(12);
            backButton.Bounds = new Rectangle(pad, Dpi(16), Dpi(74), Dpi(30));
            titleRect = new Rectangle(backButton.Right + Dpi(14), Dpi(14), Math.Max(0, Width - backButton.Right - Dpi(14) - pad), Dpi(34));
            hintRect = new Rectangle(pad, Dpi(54), Math.Max(0, Width - pad * 2), Dpi(22));

            int rowH = Dpi(36);
            int buttonsY = Height - Dpi(18) - rowH;
            statusRect = new Rectangle(pad, buttonsY - Dpi(32), Math.Max(0, Width - pad * 2), Dpi(26));
            int bodyTop = Dpi(84);
            int bodyH = Math.Max(0, statusRect.Y - Dpi(6) - bodyTop);
            int placeW = Dpi(210);
            int previewW = Math.Max(Dpi(250), Math.Min(Dpi(340), Width / 4));
            placeFrame = new Rectangle(pad, bodyTop, placeW, bodyH);
            previewFrame = new Rectangle(Math.Max(placeFrame.Right + gap, Width - pad - previewW), bodyTop, previewW, bodyH);
            browseFrame = new Rectangle(placeFrame.Right + gap, bodyTop, Math.Max(0, previewFrame.X - gap - placeFrame.Right - gap), bodyH);

            placeList.Bounds = new Rectangle(placeFrame.X + Dpi(4), placeFrame.Y + Dpi(4), Math.Max(0, placeFrame.Width - Dpi(8)), Math.Max(0, placeFrame.Height - Dpi(8)));
            upButton.Bounds = new Rectangle(browseFrame.X + Dpi(8), browseFrame.Y + Dpi(8), Dpi(92), Dpi(28));
            allButton.Bounds = new Rectangle(Math.Max(upButton.Right, browseFrame.Right - Dpi(8) - Dpi(96)), browseFrame.Y + Dpi(8), Dpi(96), Dpi(28));
            Rectangle content = new Rectangle(browseFrame.X + Dpi(4), browseFrame.Y + Dpi(46), Math.Max(0, browseFrame.Width - Dpi(8)), Math.Max(0, browseFrame.Height - Dpi(50)));
            if (imageMode) grid.Bounds = content;
            else
            {
                fileList.ItemHeight = Math.Min(255, Dpi(FileRowHeight));
                fileList.Bounds = content;
            }
            checkButton.Bounds = new Rectangle(previewFrame.X + Dpi(14), Math.Max(previewFrame.Y, previewFrame.Bottom - Dpi(14) - Dpi(34)), Math.Max(0, previewFrame.Width - Dpi(28)), Dpi(34));

            cancelButton.Bounds = new Rectangle(Width - pad - Dpi(90), buttonsY, Dpi(90), rowH);
            confirmButton.Bounds = new Rectangle(cancelButton.Left - Dpi(10) - Dpi(130), buttonsY, Dpi(130), rowH);
            pathFrame = new Rectangle(pad, buttonsY, Math.Max(0, confirmButton.Left - Dpi(16) - pad), rowH);
            pathBox.SetBounds(pathFrame.X + Dpi(12), pathFrame.Y + (pathFrame.Height - pathBox.Height) / 2, Math.Max(0, pathFrame.Width - Dpi(24)), pathBox.Height);

            int dpi = DpiHelper.ForControl(this);
            if (laidOutDpi != 0 && laidOutDpi != dpi) RefreshPlaces();   // 可变行高不随缩放重新测量，重新加入条目才会
            laidOutDpi = dpi;
            Invalidate();
        }

        private Rectangle BrowseHeaderRect()
        {
            return new Rectangle(browseFrame.X, browseFrame.Y, browseFrame.Width, Dpi(44));
        }

        // ===== 绘制 =====

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            TextRenderer.DrawText(g, pageTitle, Theme.PageTitleFont, titleRect, Theme.TextPrimary,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, "左侧选位置，单击文件夹进入；单击文件在右侧预览，点勾选框或双击勾选。也可以把文件拖进来，或在下方粘贴路径。",
                Theme.CaptionFont, hintRect, Theme.TextSecondary,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);

            PaintFrame(g, placeFrame);
            PaintFrame(g, browseFrame);
            PaintBrowseHeader(g);
            PaintPreview(g, previewFrame);

            Color statusColor = notice != null ? (noticeIsError ? Theme.DeleteRed : Theme.WarningText)
                : checkedFiles.Count > 0 ? Theme.AccentText : Theme.TextSecondary;
            TextRenderer.DrawText(g, StatusText, Theme.CaptionFont, statusRect, statusColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);

            bool focused = pathBox.Focused;
            using (SolidBrush fill = new SolidBrush(focused ? Color.White : Theme.SearchBg)) Draw.FillRoundedRect(g, fill, pathFrame, Dpi(6));
            using (Pen border = new Pen(focused ? Theme.Accent : Theme.ControlBorder, focused ? 1.4F : 1F))
                Draw.DrawRoundedRect(g, border, pathFrame, Dpi(6));
        }

        private void PaintFrame(Graphics g, Rectangle frame)
        {
            if (frame.Width <= 0 || frame.Height <= 0) return;
            using (SolidBrush fill = new SolidBrush(Theme.Card)) Draw.FillRoundedRect(g, fill, frame, Dpi(8));
            using (Pen border = new Pen(Theme.ControlBorder, 1)) Draw.DrawRoundedRect(g, border, frame, Dpi(8));
        }

        /// <summary>中栏顶部：上一级按钮和全选按钮之间是当前文件夹（过长时省略中间）和数量统计，下方一条分隔线。</summary>
        private void PaintBrowseHeader(Graphics g)
        {
            Rectangle browse = browseFrame;
            if (browse.Width <= 0 || browse.Height <= 0) return;
            int left = upButton.Right + Dpi(10);
            int right = allButton.Left - Dpi(10);
            int summaryWidth = string.IsNullOrEmpty(folderSummary) ? 0
                : Math.Min(Dpi(220), TextRenderer.MeasureText(folderSummary, Theme.CaptionFont).Width + Dpi(4));
            Rectangle summary = new Rectangle(right - summaryWidth, browse.Y + Dpi(8), summaryWidth, Dpi(28));
            Rectangle path = new Rectangle(left, browse.Y + Dpi(8), Math.Max(0, summary.X - left - Dpi(10)), Dpi(28));
            string heading = showingChecked ? "已选的文件" : currentFolder ?? "未选择位置";
            TextRenderer.DrawText(g, heading, Theme.FontOf(9.5F, FontStyle.Bold), path, Theme.TextPrimary,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.PathEllipsis | TextFormatFlags.SingleLine
                | TextFormatFlags.NoPrefix);
            if (summaryWidth > 0)
                TextRenderer.DrawText(g, folderSummary, Theme.CaptionFont, summary, Theme.TextSecondary,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine
                    | TextFormatFlags.NoPrefix);
            using (Pen line = new Pen(Theme.Separator, 1))
                g.DrawLine(line, browse.X + 1, browse.Y + Dpi(43), browse.Right - 2, browse.Y + Dpi(43));
        }

        /// <summary>右栏：上面是画面（图片按比例完整显示，音乐显示封面，其它显示类型图标），下面是文件名、信息和勾选按钮。</summary>
        private void PaintPreview(Graphics g, Rectangle frame)
        {
            if (frame.Width <= 0 || frame.Height <= 0) return;
            PaintFrame(g, frame);
            int inner = Dpi(14);
            int mediaW = Math.Max(0, frame.Width - inner * 2);
            int mediaH = Math.Max(0, Math.Min(mediaW * 3 / 4, frame.Height / 2));
            Rectangle media = new Rectangle(frame.X + inner, frame.Y + inner, mediaW, mediaH);
            if (media.Width > 0 && media.Height > 0)
                using (GraphicsPath clip = Draw.RoundedPath(media, Dpi(6)))
                {
                    using (SolidBrush bg = new SolidBrush(Theme.SearchBg)) g.FillPath(bg, clip);
                    if (previewImage != null)
                    {
                        GraphicsState state = g.Save();
                        g.SetClip(clip);
                        DrawContain(g, previewImage, media);
                        g.Restore(state);
                    }
                    else
                    {
                        int size = Dpi(40);
                        Color color = previewFailed ? Theme.DeleteRed : Theme.TextTertiary;
                        Draw.DrawGlyph(g, GlyphFor(previewPath), new Rectangle(media.X + (media.Width - size) / 2, media.Y + (media.Height - size) / 2, size, size), color);
                    }
                }

            int textX = frame.X + inner;
            int textW = mediaW;
            int y = media.Bottom + Dpi(12);
            int bottom = checkButton.Top - Dpi(8);
            if (previewPath == null)
            {
                TextRenderer.DrawText(g, "单击中间的文件，这里会显示预览和文件信息。", Theme.CaptionFont,
                    new Rectangle(textX, y, textW, Math.Max(0, bottom - y)), Theme.TextTertiary,
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                return;
            }
            Rectangle name = new Rectangle(textX, y, textW, Dpi(44));
            TextRenderer.DrawText(g, SafeFileName(previewPath), Theme.FontOf(10F, FontStyle.Bold), name, Theme.TextPrimary,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            y = name.Bottom + Dpi(4);
            if (checkedSet.Contains(previewPath))
            {
                TextRenderer.DrawText(g, "已勾选", Theme.FontOf(9F, FontStyle.Bold), new Rectangle(textX, y, textW, Dpi(20)), Theme.AccentText,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                y += Dpi(24);
            }
            if (previewLoading || previewFailed)
            {
                TextRenderer.DrawText(g, previewLoading ? "正在读取…" : previewError, Theme.CaptionFont,
                    new Rectangle(textX, y, textW, Dpi(40)), previewFailed ? Theme.DeleteRed : Theme.TextSecondary,
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                y += Dpi(44);
            }
            int labelW = Dpi(64);
            foreach (KeyValuePair<string, string> pair in previewInfo)
            {
                if (y + Dpi(20) > bottom) break;
                TextRenderer.DrawText(g, pair.Key, Theme.CaptionFont, new Rectangle(textX, y, labelW, Dpi(20)), Theme.TextTertiary,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                TextFormatFlags valueFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix
                    | (pair.Key == "位置" ? TextFormatFlags.PathEllipsis : TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(g, pair.Value, Theme.CaptionFont, new Rectangle(textX + labelW, y, Math.Max(0, textW - labelW), Dpi(20)),
                    Theme.TextPrimary, valueFlags);
                y += Dpi(22);
            }
        }

        /// <summary>等比完整显示（不裁切），居中。</summary>
        private static void DrawContain(Graphics g, Bitmap source, Rectangle target)
        {
            if (source == null || source.Width <= 0 || source.Height <= 0 || target.Width <= 0 || target.Height <= 0) return;
            float scale = Math.Min(target.Width / (float)source.Width, target.Height / (float)source.Height);
            float w = source.Width * scale, h = source.Height * scale;
            RectangleF dest = new RectangleF(target.X + (target.Width - w) / 2F, target.Y + (target.Height - h) / 2F, w, h);
            InterpolationMode old = g.InterpolationMode;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(source, dest);
            g.InterpolationMode = old;
        }

        private static string GlyphFor(string path)
        {
            if (string.IsNullOrEmpty(path)) return GlyphFile;
            if (PathPickerDialog.HasExtension(path, MediaRules.ImageExt)) return GlyphPicture;
            if (PathPickerDialog.HasExtension(path, MediaRules.AudioExt)) return GlyphAudio;
            if (PathPickerDialog.HasExtension(path, MediaRules.VideoExt)) return GlyphVideo;
            return GlyphFile;
        }

        /// <summary>列表行：勾选框、类型图标、文件名，右侧是大小和修改时间（「已选的文件」里第二行是所在文件夹）。</summary>
        private void DrawFileRow(object sender, DrawItemEventArgs e)
        {
            FileRow item = ListRow(e.Index);
            Rectangle row = e.Bounds;
            if (item == null || row.Width <= 0 || row.Height <= 0) return;
            using (BufferedGraphics buffer = BufferedGraphicsManager.Current.Allocate(e.Graphics, row))
            {
                Graphics g = buffer.Graphics;
                using (SolidBrush back = new SolidBrush(Theme.Card)) g.FillRectangle(back, row);
                // 缓冲区带有平移变换，TextRenderer 不认它（文字会画到第一行的位置），行内文字一律用 GDI+ 绘制
                if (item.Kind == FileKind.Note)
                {
                    using (SolidBrush brush = new SolidBrush(Theme.TextTertiary))
                        Draw.DrawStringLeft(g, item.Name, Theme.CaptionFont, brush, new Rectangle(row.X + Dpi(12), row.Y, Math.Max(0, row.Width - Dpi(24)), row.Height));
                    buffer.Render(e.Graphics);
                    return;
                }
                g.SmoothingMode = SmoothingMode.AntiAlias;
                bool isFile = item.Kind == FileKind.File;
                bool isChecked = isFile && checkedSet.Contains(item.Path);
                bool selected = e.Index == fileList.SelectedIndex;
                bool hover = e.Index == fileList.HoverIndex;
                if (selected || hover || isChecked)
                    using (SolidBrush b = new SolidBrush(selected ? Theme.SelectionBg : hover ? Theme.RowHover : Theme.SelectedCard))
                        Draw.FillRoundedRect(g, b, new Rectangle(row.X + Dpi(2), row.Y + 1, row.Width - Dpi(4), row.Height - 2), Dpi(6));
                int x = row.X + Dpi(12);
                int box = Dpi(18);
                if (isFile) Draw.DrawCheckBox(g, new Rectangle(x, row.Y + (row.Height - box) / 2, box, box), isChecked, !checking, DpiHelper.ForControl(this));
                x += box + Dpi(12);
                int iconSize = Dpi(18);
                Rectangle icon = new Rectangle(x, row.Y + (row.Height - iconSize) / 2, iconSize, iconSize);
                Draw.DrawGlyph(g, isFile ? GlyphFor(item.Path) : GlyphFolder, icon, isFile ? Theme.IconColor : Color.FromArgb(232, 160, 40));
                int textX = icon.Right + Dpi(10);
                int right = row.Right - Dpi(12);
                if (item.Detail == null && isFile)
                {
                    // 修改时间和大小靠右两列；宽度不够时先不显示时间
                    int dateW = Dpi(124), sizeW = Dpi(76);
                    bool showDate = item.Modified != DateTime.MinValue && right - textX > Dpi(200) + dateW;
                    if (showDate)
                    {
                        using (SolidBrush brush = new SolidBrush(Theme.TextSecondary))
                            Draw.DrawStringRight(g, item.Modified.ToString("yyyy-MM-dd HH:mm"), Theme.CaptionFont, brush, new Rectangle(right - dateW, row.Y, dateW, row.Height));
                        right -= dateW + Dpi(8);
                    }
                    if (item.Length >= 0 && right - textX > Dpi(140) + sizeW)
                    {
                        using (SolidBrush brush = new SolidBrush(Theme.TextSecondary))
                            Draw.DrawStringRight(g, LocalScanScope.FormatBytes(item.Length), Theme.CaptionFont, brush, new Rectangle(right - sizeW, row.Y, sizeW, row.Height));
                        right -= sizeW + Dpi(8);
                    }
                }
                Rectangle nameRect = new Rectangle(textX, row.Y, Math.Max(0, right - textX), row.Height);
                if (item.Detail != null)
                {
                    nameRect = new Rectangle(textX, row.Y + Dpi(2), Math.Max(0, right - textX), row.Height / 2);
                    using (SolidBrush brush = new SolidBrush(Theme.TextTertiary))
                        Draw.DrawStringLeft(g, item.Detail, Theme.CaptionFont, brush, new Rectangle(textX, row.Y + row.Height / 2 - Dpi(1), nameRect.Width, row.Height / 2));
                }
                using (SolidBrush brush = new SolidBrush(isChecked ? Theme.AccentText : Theme.TextPrimary))
                    Draw.DrawStringLeft(g, item.Name, isChecked ? Theme.FontOf(9.5F, FontStyle.Bold) : Theme.BodyFont, brush, nameRect);
                buffer.Render(e.Graphics);
            }
        }
    }
}

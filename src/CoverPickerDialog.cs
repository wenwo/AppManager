using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using AppManager.Controls;

namespace AppManager
{
    /// <summary>
    /// 选集合封面的专用窗口：左侧是常用位置和磁盘，右侧按缩略图列出当前文件夹里的子文件夹和图片，
    /// 底部按集合封面的比例预览选中的图片并显示尺寸和大小。单击图片选中，双击直接使用；单击文件夹进入。
    /// 也可以在底部输入框粘贴图片或文件夹路径，或把图片拖进来。
    /// 目录读取、缩略图解码、预览解码和确定时的磁盘检查都在后台进行，窗口关闭后结果直接丢弃。
    /// </summary>
    internal sealed class CoverPickerDialog : BufferedDialogForm
    {
        internal const string DialogTitle = "选择集合封面";
        private const int EM_SETCUEBANNER = 0x1501;
        private const int PlaceHeaderHeight = 30;
        private const int PlaceRowHeight = 38;
        private const int PreviewSourceMax = 640;   // 预览原图长边上限：够画底部小预览，内存有界
        private const float CoverAspect = 8F / 5F;   // 与集合信息页封面同比例

        private const string GlyphFolder = "";
        private const string GlyphDrive = "";
        private const string GlyphPictures = "";

        private static readonly Guid DownloadsFolder = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        /// <summary>本次运行里上一次选封面时所在的文件夹和选中的图片：再次打开时回到这里并选中它。</summary>
        private static string lastFolder;
        private static string lastFile;

        // 96 DPI 设计坐标，绘制和布局时换算。四个外框随页面大小重新计算（见 LayoutPage）：
        // 嵌在主窗口里时页面跟着主窗口缩放，独立显示时是 880×640。
        private Rectangle placeFrame = new Rectangle(24, 88, 196, 404);
        private Rectangle browseFrame = new Rectangle(232, 88, 624, 404);
        private Rectangle previewFrame = new Rectangle(24, 504, 832, 72);
        private Rectangle pathFrame = new Rectangle(24, 588, 600, 34);

        private enum PlaceKind { Header, Note, Place, Drive }

        private sealed class PlaceRow
        {
            internal PlaceKind Kind;
            internal string Title = string.Empty;
            internal string Path;
            internal string Glyph;
            internal bool Enabled = true;

            public override string ToString() { return Title; }
        }

        private readonly List<PlaceRow> places = new List<PlaceRow>();
        private readonly SoftwarePickerDialog.PickerList placeList = new SoftwarePickerDialog.PickerList();
        private readonly CoverImageGrid grid = new CoverImageGrid();
        private readonly RoundButton backButton = new RoundButton();
        private readonly Label titleLabel = new Label();
        private readonly Label hint = new Label();
        private readonly RoundButton upButton = new RoundButton();
        private readonly TextBox pathBox = new TextBox();
        private readonly RoundButton confirmButton = new RoundButton();
        private readonly RoundButton cancelButton = new RoundButton();
        private List<LocalScanScopeDrive> drives;   // null：仍在后台读取

        private string currentFolder;
        private string folderSummary = string.Empty;
        private int loadGen;
        private string pendingSelect;       // 文件夹读完后要选中的图片
        private string pendingNotice;       // 文件夹读完、选中图片后再显示的提示（选中会清掉之前的提示）

        // 底部预览
        private Bitmap previewSource;
        private string previewPath;
        private string previewInfo;
        private bool previewLoading;
        private bool previewFailed;
        private string previewError = string.Empty;   // 预览失败的原因：文件不存在，或解不开
        private int previewGen;
        private bool confirmAfterPreview;   // 预览还在读时点了确定（如双击）：读完、确认能用再继续

        private string notice;              // 最近一次操作的提示；输入或选择变化时清掉
        private bool noticeIsError;
        private bool checking;
        private int checkVersion;
        private bool settingText;

        /// <summary>确定后的图片完整路径。</summary>
        internal string ResultPath;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, string lParam);

        /// <summary>
        /// 选封面页。主窗口把它嵌在标题行以下显示（和添加页、设置页一样不另弹窗口，见 MainForm.CoverPage.cs）：
        /// 点「使用此封面」或「返回 / 取消」后页面自己关闭，结果在 DialogResult 和 ResultPath 里。
        /// initialFolder 为空时打开上次选封面的文件夹，再没有就打开「图片」。
        /// </summary>
        internal CoverPickerDialog(string initialFolder)
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(880, 640);
            BackColor = Theme.Background;
            ShowInTaskbar = false;
            AllowDrop = true;
            Text = DialogTitle;

            // 页头与添加页一致：左上角「返回」，右边是页标题，下面一行说明
            backButton.Text = "返回";
            backButton.Icon = UiIconKind.Back;
            backButton.SetBounds(24, 16, 74, 30);
            backButton.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(backButton);

            titleLabel.Text = DialogTitle;
            titleLabel.Font = Theme.PageTitleFont;
            titleLabel.ForeColor = Theme.TextPrimary;
            titleLabel.AutoSize = false;
            titleLabel.UseMnemonic = false;
            titleLabel.TextAlign = ContentAlignment.MiddleLeft;
            titleLabel.SetBounds(114, 14, 742, 34);
            Controls.Add(titleLabel);

            hint.Text = "左侧选位置，单击文件夹进入；单击图片看封面效果，双击直接使用。也可以粘贴路径或把图片拖进来。";
            hint.Font = Theme.CaptionFont;
            hint.ForeColor = Theme.TextSecondary;
            hint.AutoSize = false;
            hint.AutoEllipsis = true;
            hint.UseMnemonic = false;
            hint.SetBounds(24, 56, 832, 22);
            Controls.Add(hint);

            placeList.DrawMode = DrawMode.OwnerDrawVariable;
            placeList.SetBounds(placeFrame.X + 4, placeFrame.Y + 4, placeFrame.Width - 8, placeFrame.Height - 8);
            placeList.AccessibleName = "位置";
            placeList.MeasureItem += MeasurePlace;
            placeList.DrawItem += DrawPlace;
            placeList.MouseUp += PlaceList_MouseUp;
            placeList.KeyDown += PlaceList_KeyDown;
            Controls.Add(placeList);

            upButton.Text = "上一级";
            upButton.Icon = UiIconKind.Back;
            upButton.Font = Theme.CaptionFont;
            upButton.SetBounds(browseFrame.X + 8, browseFrame.Y + 8, 92, 28);
            upButton.Click += delegate { GoUp(); };
            Controls.Add(upButton);

            grid.SetBounds(browseFrame.X + 4, browseFrame.Y + 46, browseFrame.Width - 8, browseFrame.Height - 50);
            grid.AccessibleName = "文件夹和图片";
            grid.FolderOpened += delegate(string path) { Navigate(path); };
            grid.ImageSelected += delegate(string path) { ChooseImage(path); };
            grid.ImageActivated += delegate(string path) { ChooseImage(path); Confirm(); };
            Controls.Add(grid);

            pathBox.BorderStyle = BorderStyle.None;
            pathBox.BackColor = Theme.SearchBg;
            pathBox.ForeColor = Theme.TextPrimary;
            pathBox.Font = Theme.BodyFont;
            pathBox.AccessibleName = "图片路径";
            pathBox.HandleCreated += delegate { SendMessage(pathBox.Handle, EM_SETCUEBANNER, (IntPtr)1, "粘贴图片或文件夹路径，也可以把图片拖进来"); };
            pathBox.GotFocus += delegate { pathBox.BackColor = Color.White; Invalidate(); };
            pathBox.LostFocus += delegate { pathBox.BackColor = Theme.SearchBg; Invalidate(); };
            pathBox.TextChanged += delegate
            {
                if (settingText) return;
                notice = null;
                grid.SelectPath(pathBox.Text.Trim().Trim('"'));
                SyncPreviewWithText();
                RefreshState();
            };
            Controls.Add(pathBox);
            LayoutPathBox();

            confirmButton.Text = "使用此封面";
            confirmButton.Accent = true;
            confirmButton.SetBounds(636, 588, 120, 34);
            confirmButton.Click += delegate { Confirm(); };
            Controls.Add(confirmButton);

            cancelButton.Text = "取消";
            cancelButton.SetBounds(766, 588, 90, 34);
            cancelButton.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(cancelButton);

            LayoutPage();
            BuildPlaces();
            currentFolder = StartFolder(initialFolder);
            if (currentFolder != null && lastFile != null
                && string.Equals(SafeDirectory(lastFile), currentFolder, StringComparison.OrdinalIgnoreCase))
                pendingSelect = lastFile;
            RefreshPlaces();
            RefreshState();
        }

        // ===== 位置 =====

        private void BuildPlaces()
        {
            AddPlace("上次使用", lastFolder, GlyphFolder);
            AddPlace("图片", SafeSpecialFolder(Environment.SpecialFolder.MyPictures), GlyphPictures);
            AddPlace("桌面", SafeSpecialFolder(Environment.SpecialFolder.DesktopDirectory), GlyphFolder);
            string downloads = null;
            try { downloads = Scanner.KnownFolderPath(DownloadsFolder); } catch (Exception ex) { Paths.LogError(ex); }
            AddPlace("下载", downloads, GlyphFolder);
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
            if (!IsQuickLocal(full)) return;
            try { if (!Directory.Exists(full)) return; } catch { return; }
            places.Add(new PlaceRow { Kind = PlaceKind.Place, Title = label, Path = full, Glyph = glyph });
        }

        /// <summary>起始文件夹：调用方指定的、上次使用的、「图片」，取第一个能用的。</summary>
        private string StartFolder(string initialFolder)
        {
            string wanted = LocalScanScope.NormalizePath(initialFolder);
            if (wanted != null)
            {
                if (IsQuickLocal(wanted)) { try { if (Directory.Exists(wanted)) return wanted; } catch { } }
                else return wanted;   // 网络路径不在界面线程上检查，读取失败时在网格里说明
            }
            if (lastFolder != null)
                foreach (PlaceRow place in places)
                    if (string.Equals(place.Path, lastFolder, StringComparison.OrdinalIgnoreCase)) return place.Path;
            foreach (PlaceRow place in places)
                if (place.Title == "图片") return place.Path;
            return places.Count > 0 ? places[0].Path : null;
        }

        /// <summary>路径是否在本机固定/可移动磁盘上（只看盘符类型，不访问磁盘）。</summary>
        internal static bool IsQuickLocal(string full)
        {
            if (full == null || full.Length < 3 || full[1] != ':') return false;
            try
            {
                DriveType type = new DriveInfo(full.Substring(0, 3)).DriveType;
                return type == DriveType.Fixed || type == DriveType.Removable;
            }
            catch (ArgumentException) { return false; }
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

        private PlaceRow PlaceAt(int index)
        {
            return index < 0 || index >= placeList.Items.Count ? null : placeList.Items[index] as PlaceRow;
        }

        /// <summary>当前文件夹属于哪个位置：取路径前缀最长的那一行，左侧把它画成选中。</summary>
        private PlaceRow ActivePlace()
        {
            PlaceRow best = null;
            if (currentFolder == null) return null;
            for (int i = 0; i < placeList.Items.Count; i++)
            {
                PlaceRow row = PlaceAt(i);
                if (row == null || row.Path == null || !IsWithin(row.Path, currentFolder)) continue;
                if (best == null || row.Path.Length > best.Path.Length) best = row;
            }
            return best;
        }

        internal static bool IsWithin(string root, string path)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(path)) return false;
            if (string.Equals(root, path, StringComparison.OrdinalIgnoreCase)) return true;
            string prefix = root.EndsWith("\\", StringComparison.Ordinal) ? root : root + "\\";
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private void PlaceList_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            PlaceRow row = PlaceAt(placeList.HitRow(e.Location));
            if (row == null || !row.Enabled || row.Path == null) return;
            Navigate(row.Path);
        }

        private void PlaceList_KeyDown(object sender, KeyEventArgs e)
        {
            int step = e.KeyCode == Keys.Down ? 1 : e.KeyCode == Keys.Up ? -1 : 0;
            if (step == 0)
            {
                if (e.KeyCode != Keys.Right && e.KeyCode != Keys.Space) return;
                PlaceRow row = PlaceAt(placeList.SelectedIndex);
                if (row != null && row.Enabled && row.Path != null) Navigate(row.Path);
                if (e.KeyCode == Keys.Right) grid.Focus();
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
                    Navigate(row.Path);
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
                    Color iconColor = !item.Enabled ? Theme.TextTertiary : chosen ? Theme.Accent
                        : item.Kind == PlaceKind.Drive ? Theme.IconColor : Theme.FolderTint;
                    Draw.DrawGlyph(g, item.Glyph, icon, iconColor);
                    using (SolidBrush brush = new SolidBrush(!item.Enabled ? Theme.TextTertiary : chosen ? Theme.AccentText : Theme.TextPrimary))
                        Draw.DrawStringLeft(g, item.Title, chosen ? Theme.FontOf(9.5F, FontStyle.Bold) : Theme.BodyFont, brush,
                            new Rectangle(icon.Right + Dpi(10), row.Y, Math.Max(0, row.Right - icon.Right - Dpi(18)), row.Height));
                }
                buffer.Render(e.Graphics);
            }
        }

        // ===== 浏览 =====

        /// <summary>进入一个文件夹：后台读取子文件夹和图片，读完前网格显示「正在读取」。</summary>
        internal void Navigate(string folder)
        {
            string full = LocalScanScope.NormalizePath(folder);
            if (full == null) return;
            if (!string.Equals(full, currentFolder, StringComparison.OrdinalIgnoreCase)) { pendingSelect = null; pendingNotice = null; }
            currentFolder = full;
            LoadFolder();
        }

        private void LoadFolder()
        {
            string folder = currentFolder;
            int gen = ++loadGen;
            folderSummary = string.Empty;
            grid.SetItems(new List<CoverImageGrid.Item>(), "正在读取…");
            placeList.Invalidate();
            RefreshState();
            if (folder == null)
            {
                grid.SetItems(new List<CoverImageGrid.Item>(), "从左侧选一个位置。");
                return;
            }
            string[] extensions = MediaRules.ImageExt;
            ThreadPool.QueueUserWorkItem(delegate
            {
                LocalScanScopeChildren folders = null;
                List<string> files = new List<string>();
                string error = null;
                int omitted = 0;
                try
                {
                    folders = LocalScanScope.ListSubfolders(folder, null);
                    error = folders.Error;
                    if (error == null) files = PathPickerDialog.ListFiles(folder, extensions, out omitted, out error);
                }
                catch (Exception ex) { Paths.LogError(ex); error = ex.Message; }
                LocalScanScopeChildren listed = folders;
                List<string> images = files;
                string failure = error;
                int more = omitted;
                Post(delegate { if (gen == loadGen) FinishLoad(listed, images, more, failure); });
            });
        }

        private void FinishLoad(LocalScanScopeChildren folders, List<string> files, int omitted, string error)
        {
            List<CoverImageGrid.Item> items = new List<CoverImageGrid.Item>();
            int folderCount = 0;
            bool omittedMore = false;
            if (folders != null)
            {
                foreach (string path in folders.Paths)
                {
                    items.Add(new CoverImageGrid.Item { Folder = true, Path = path, Name = Path.GetFileName(path) });
                    folderCount++;
                }
                omitted += folders.Omitted;
                omittedMore = folders.OmittedMore;
            }
            foreach (string path in files)
                items.Add(new CoverImageGrid.Item { Path = path, Name = Path.GetFileName(path) });
            string empty = error != null ? "无法读取这个文件夹：" + error
                : "这个文件夹里没有图片，也没有子文件夹。";
            grid.SetItems(items, empty);
            List<string> parts = new List<string>();
            if (folderCount > 0) parts.Add(folderCount + " 个文件夹");
            if (files.Count > 0) parts.Add(files.Count + " 张图片");
            folderSummary = string.Join(" · ", parts.ToArray());
            if (omittedMore) folderSummary += "（还有更多未列出）";
            else if (omitted > 0) folderSummary += "（还有 " + omitted + " 项未列出）";
            string select = pendingSelect, later = pendingNotice;
            pendingSelect = null;
            pendingNotice = null;
            if (select != null)
            {
                // 上次用的或刚拖进来的图片：选中并填好路径、显示预览
                grid.SelectPath(select);
                if (grid.SelectedImage != null) ChooseImage(grid.SelectedImage);
            }
            else grid.SelectPath(pathBox.Text.Trim().Trim('"'));
            // 子文件夹列出来了、图片却读取失败：网格不是空的，空白提示显示不出来，改在预览区说明
            if (error != null && items.Count > 0) SetNotice("无法读取这个文件夹里的图片：" + error, true);
            else if (later != null) SetNotice(later, false);
            Invalidate(BrowseHeaderRect());
            RefreshState();
        }

        internal void GoUp()
        {
            string parent = ParentFolder(currentFolder);
            if (parent == null) return;
            string from = currentFolder;
            Navigate(parent);
            grid.RevealAfterLoad(from);
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

        /// <summary>网格里点了一张图片：写进输入框并开始预览。</summary>
        private void ChooseImage(string path)
        {
            if (checking || string.IsNullOrEmpty(path)) return;
            notice = null;
            SetPathText(path);
            StartPreview(path);
            RefreshState();
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
        }

        // ===== 预览 =====

        /// <summary>输入框改成了别的路径：是图片就预览它，否则清掉预览（是否存在留到确定时在后台检查）。</summary>
        private void SyncPreviewWithText()
        {
            string reason;
            string cleaned = LocalScanScope.CleanTypedPath(pathBox.Text, out reason);
            if (cleaned != null && PathPickerDialog.HasExtension(cleaned, MediaRules.ImageExt)) StartPreview(cleaned);
            else ClearPreview();
        }

        private void ClearPreview()
        {
            previewGen++;
            if (previewSource != null) { previewSource.Dispose(); previewSource = null; }
            previewPath = null;
            previewInfo = null;
            previewLoading = false;
            previewFailed = false;
            confirmAfterPreview = false;   // 换了图片：之前等着的确定作废
            Invalidate(PreviewRect());
        }

        /// <summary>后台解码预览原图并读取原始尺寸和文件大小；解码失败时说明原因，不让它当封面。</summary>
        private void StartPreview(string path)
        {
            if (string.Equals(previewPath, path, StringComparison.OrdinalIgnoreCase) && (previewLoading || previewSource != null || previewFailed)) return;
            ClearPreview();
            previewPath = path;
            previewLoading = true;
            int gen = previewGen;
            ThreadPool.QueueUserWorkItem(delegate
            {
                int width, height;
                Bitmap bmp = LoadPreviewSource(path, out width, out height);
                long length = -1;
                bool missing = false;
                try
                {
                    FileInfo file = new FileInfo(path);
                    if (file.Exists) length = file.Length;
                    else missing = true;
                }
                catch (Exception) { }   // 只是不显示大小；文件是否可用由确定时的检查负责
                string info = DescribeImage(width, height, length);
                string failure = missing ? "文件不存在或当前不可访问。" : "这张图片无法读取，换一张试试。";
                Post(delegate
                {
                    if (gen != previewGen) { if (bmp != null) bmp.Dispose(); return; }
                    previewLoading = false;
                    previewSource = bmp;
                    previewFailed = bmp == null;
                    previewError = failure;
                    previewInfo = bmp == null ? null : info;
                    Invalidate(PreviewRect());
                    RefreshState();
                    if (!confirmAfterPreview) return;
                    confirmAfterPreview = false;
                    if (!previewFailed) Confirm();   // 解不开时预览区已经说明原因，留在窗口里
                }, delegate { if (bmp != null) bmp.Dispose(); });
            });
            Invalidate(PreviewRect());
        }

        /// <summary>优先用 WIC 解码（顺带拿到计入 EXIF 旋转的原始尺寸），失败时退回封面加载的通用路径。只在后台线程调用。</summary>
        internal static Bitmap LoadPreviewSource(string path, out int width, out int height)
        {
            int w = 0, h = 0;
            try
            {
                Bitmap bmp = WicImageDecoder.Load(path, delegate(int ow, int oh) { w = ow; h = oh; return PreviewSourceMax; });
                width = w;
                height = h;
                return bmp;
            }
            catch (Exception) { }   // 未装解码器、文件损坏或被占用：交给下面的通用路径再试一次
            width = 0;
            height = 0;
            try { return Icons.LoadCoverSource(path, PreviewSourceMax); }
            catch (Exception) { return null; }
        }

        internal static string DescribeImage(int width, int height, long length)
        {
            List<string> parts = new List<string>();
            if (width > 0 && height > 0) parts.Add(width + " × " + height);
            if (length >= 0) parts.Add(LocalScanScope.FormatBytes(length));
            return string.Join(" · ", parts.ToArray());
        }

        // ===== 提示与按钮状态 =====

        private void SetNotice(string text, bool isError)
        {
            notice = text;
            noticeIsError = isError;
            RefreshState();
        }

        private void RefreshState()
        {
            bool ready = !checking && !confirmAfterPreview && pathBox.Text.Trim().Length > 0 && !PreviewBlocksConfirm();
            confirmButton.Enabled = ready;
            confirmButton.Accent = ready;
            upButton.Enabled = !checking && ParentFolder(currentFolder) != null;
            Invalidate(PreviewRect());
        }

        /// <summary>输入框里的图片已确认解不开：不让它当封面（集合页同样画不出来）。</summary>
        private bool PreviewBlocksConfirm()
        {
            if (!previewFailed || previewPath == null) return false;
            string reason;
            string cleaned = LocalScanScope.CleanTypedPath(pathBox.Text, out reason);
            return cleaned != null && string.Equals(cleaned, previewPath, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>底部预览区的说明文字（第二行）。测试也从这里读。</summary>
        internal string StatusText
        {
            get
            {
                if (notice != null) return notice;
                if (checking) return "正在检查…";
                if (previewPath != null)
                {
                    if (previewLoading) return "正在读取图片…";
                    if (previewFailed) return previewError;
                    return string.IsNullOrEmpty(previewInfo) ? "回到编辑页后可以拖动封面调整显示区域。"
                        : previewInfo + " · 回到编辑页后可以拖动封面调整显示区域";
                }
                return pathBox.Text.Trim().Length > 0 ? "将使用：" + pathBox.Text.Trim() : "单击右侧的图片，这里会按封面比例预览。";
            }
        }

        private bool StatusIsError
        {
            get { return notice != null ? noticeIsError : !checking && previewPath != null && previewFailed; }
        }

        // ===== 确定 =====

        /// <summary>
        /// 在后台检查输入框里的路径：是文件夹就进入它；是图片且存在就关闭并返回；否则在预览区说明原因，窗口留着可以重选。
        /// </summary>
        internal void Confirm()
        {
            if (checking) return;
            string reason;
            string cleaned = LocalScanScope.CleanTypedPath(pathBox.Text, out reason);
            if (cleaned == null) { SetNotice(reason ?? "请先单击选一张图片。", true); return; }
            if (PreviewBlocksConfirm()) { SetNotice(previewError, true); return; }
            if (previewLoading && string.Equals(cleaned, previewPath, StringComparison.OrdinalIgnoreCase))
            {
                // 还不知道这张能不能解开（双击或选中后马上回车）：等预览读完再决定，坏图不会被当成封面返回
                confirmAfterPreview = true;
                notice = null;
                RefreshState();
                return;
            }
            checking = true;
            notice = null;
            confirmButton.Text = "正在检查…";
            RefreshState();
            int version = ++checkVersion;
            string[] extensions = MediaRules.ImageExt;
            ThreadPool.QueueUserWorkItem(delegate
            {
                PathPickerCheck result;
                bool folder = false;
                try
                {
                    string full = Path.GetFullPath(cleaned);
                    folder = Directory.Exists(full);
                    result = folder ? new PathPickerCheck { Path = full }
                        : PathPickerDialog.CheckPath(PathPickerMode.OpenFile, cleaned, extensions, false, null, null);
                }
                catch (Exception ex)
                {
                    if (!(ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException
                        || ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException))
                        Paths.LogError(ex);
                    result = new PathPickerCheck { Error = "路径格式不正确：" + ex.Message };
                }
                bool isFolder = folder;
                Post(delegate { if (version == checkVersion) FinishConfirm(result, isFolder); });
            });
        }

        private void FinishConfirm(PathPickerCheck result, bool folder)
        {
            checking = false;
            confirmButton.Text = "使用此封面";
            if (result.Error != null) { SetNotice(result.Error, true); return; }
            if (folder)
            {
                SetPathText(string.Empty);
                ClearPreview();
                Navigate(result.Path);
                return;
            }
            ResultPath = result.Path;
            lastFolder = SafeDirectory(result.Path);
            lastFile = result.Path;
            DialogResult = DialogResult.OK;
            Close();
        }

        // ===== 生命周期、布局与绘制 =====

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            grid.Focus();
            LoadDrivesAsync();
            LoadFolder();
        }

        protected override void OnDpiScaleChanged()
        {
            base.OnDpiScaleChanged();
            LayoutPage();
            RefreshPlaces();   // 行高不随窗体缩放自动变化，重新加入条目才会重新测量
            Invalidate(true);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (!DpiChangeInProgress) LayoutPage();
        }

        /// <summary>
        /// 按页面当前大小排版：左侧位置栏宽度固定，浏览区占其余宽度和高度，预览条、路径输入和按钮贴在底部。
        /// 先算出 96 DPI 设计坐标下的四个外框，再把控件摆到换算后的像素位置。
        /// </summary>
        private void LayoutPage()
        {
            if (placeList == null || grid == null) return;
            int dpi = Math.Max(96, CurrentDpi);
            int w = Math.Max(640, ClientSize.Width * 96 / dpi), h = Math.Max(440, ClientSize.Height * 96 / dpi);
            const int pad = 24, top = 88, placesWidth = 196, gap = 12, previewHeight = 72, rowHeight = 34, bottomPad = 18;
            int pathTop = h - bottomPad - rowHeight;
            int previewTop = pathTop - gap - previewHeight;
            int framesHeight = previewTop - gap - top;
            placeFrame = new Rectangle(pad, top, placesWidth, framesHeight);
            browseFrame = new Rectangle(pad + placesWidth + gap, top, w - pad * 2 - placesWidth - gap, framesHeight);
            previewFrame = new Rectangle(pad, previewTop, w - pad * 2, previewHeight);
            pathFrame = new Rectangle(pad, pathTop, w - pad * 2 - 120 - 90 - 12 - 10, rowHeight);

            backButton.Bounds = Scaled(new Rectangle(pad, 16, 74, 30));
            titleLabel.Bounds = Scaled(new Rectangle(pad + 74 + 16, 14, Math.Max(1, w - pad * 2 - 90), 34));
            hint.Bounds = Scaled(new Rectangle(pad, 56, w - pad * 2, 22));
            placeList.Bounds = Scaled(new Rectangle(placeFrame.X + 4, placeFrame.Y + 4, placeFrame.Width - 8, placeFrame.Height - 8));
            upButton.Bounds = Scaled(new Rectangle(browseFrame.X + 8, browseFrame.Y + 8, 92, 28));
            grid.Bounds = Scaled(new Rectangle(browseFrame.X + 4, browseFrame.Y + 46, browseFrame.Width - 8, browseFrame.Height - 50));
            confirmButton.Bounds = Scaled(new Rectangle(pathFrame.Right + 12, pathTop, 120, rowHeight));
            cancelButton.Bounds = Scaled(new Rectangle(pathFrame.Right + 12 + 120 + 10, pathTop, 90, rowHeight));
            LayoutPathBox();
            Invalidate();
        }

        /// <summary>单行输入框的高度由字体决定，不随窗体缩放：按当前 DPI 重新放到外框中间。</summary>
        private void LayoutPathBox()
        {
            pathBox.SetBounds(Dpi(pathFrame.X + 12), Dpi(pathFrame.Y) + (Dpi(pathFrame.Height) - pathBox.Height) / 2,
                Dpi(pathFrame.Width - 24), pathBox.Height);
        }

        private Rectangle Scaled(Rectangle r)
        {
            return new Rectangle(Dpi(r.X), Dpi(r.Y), Dpi(r.Width), Dpi(r.Height));
        }

        private Rectangle PreviewRect() { return Scaled(previewFrame); }

        private Rectangle BrowseHeaderRect()
        {
            Rectangle frame = Scaled(browseFrame);
            return new Rectangle(frame.X, frame.Y, frame.Width, Dpi(44));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            PaintFrame(g, Scaled(placeFrame));
            Rectangle browse = Scaled(browseFrame);
            PaintFrame(g, browse);
            PaintBrowseHeader(g, browse);
            PaintPreview(g, PreviewRect());

            Rectangle input = Scaled(pathFrame);
            bool focused = pathBox.Focused;
            using (SolidBrush fill = new SolidBrush(focused ? Color.White : Theme.SearchBg)) Draw.FillRoundedRect(g, fill, input, Dpi(6));
            using (Pen border = new Pen(focused ? Theme.Accent : Theme.ControlBorder, focused ? 1.4F : 1F))
                Draw.DrawRoundedRect(g, border, input, Dpi(6));
        }

        private void PaintFrame(Graphics g, Rectangle frame)
        {
            using (SolidBrush fill = new SolidBrush(Theme.Card)) Draw.FillRoundedRect(g, fill, frame, Dpi(8));
            using (Pen border = new Pen(Theme.ControlBorder, 1)) Draw.DrawRoundedRect(g, border, frame, Dpi(8));
        }

        /// <summary>浏览区顶部：上一级按钮右侧是当前文件夹（过长时省略中间）和数量统计，下方一条分隔线。</summary>
        private void PaintBrowseHeader(Graphics g, Rectangle browse)
        {
            int left = browse.X + Dpi(108);
            int summaryWidth = string.IsNullOrEmpty(folderSummary) ? 0
                : Math.Min(Dpi(240), TextRenderer.MeasureText(folderSummary, Theme.CaptionFont).Width + Dpi(4));
            Rectangle summary = new Rectangle(browse.Right - Dpi(12) - summaryWidth, browse.Y + Dpi(8), summaryWidth, Dpi(28));
            Rectangle path = new Rectangle(left, browse.Y + Dpi(8), Math.Max(0, summary.X - left - Dpi(10)), Dpi(28));
            TextRenderer.DrawText(g, currentFolder ?? "未选择位置", Theme.FontOf(9.5F, FontStyle.Bold), path, Theme.TextPrimary,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.PathEllipsis | TextFormatFlags.SingleLine
                | TextFormatFlags.NoPrefix);
            if (summaryWidth > 0)
                TextRenderer.DrawText(g, folderSummary, Theme.CaptionFont, summary, Theme.TextSecondary,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine
                    | TextFormatFlags.NoPrefix);
            using (Pen line = new Pen(Theme.Separator, 1))
                g.DrawLine(line, browse.X + 1, browse.Y + Dpi(43), browse.Right - 2, browse.Y + Dpi(43));
        }

        /// <summary>底部预览：左边按封面比例（8:5、居中取景）画选中的图片，右边是文件名和尺寸、大小或提示。</summary>
        private void PaintPreview(Graphics g, Rectangle frame)
        {
            PaintFrame(g, frame);
            Rectangle thumb = new Rectangle(frame.X + Dpi(10), frame.Y + Dpi(8), (int)Math.Round((frame.Height - Dpi(16)) * CoverAspect), frame.Height - Dpi(16));
            using (GraphicsPath clip = Draw.RoundedPath(thumb, Dpi(6)))
            {
                using (SolidBrush bg = new SolidBrush(Theme.SearchBg)) g.FillPath(bg, clip);
                GraphicsState state = g.Save();
                g.SetClip(clip);
                if (previewSource != null) DrawCoverCrop(g, previewSource, thumb);
                else
                {
                    int size = Dpi(22);
                    Draw.DrawGlyph(g, GlyphPictures, new Rectangle(thumb.X + (thumb.Width - size) / 2, thumb.Y + (thumb.Height - size) / 2, size, size),
                        previewFailed ? Theme.DeleteRed : Theme.TextTertiary);
                }
                g.Restore(state);
            }
            int textX = thumb.Right + Dpi(14);
            int textWidth = Math.Max(0, frame.Right - textX - Dpi(14));
            string title = previewPath != null ? Path.GetFileName(previewPath) : "还没有选图片";
            using (SolidBrush brush = new SolidBrush(previewPath != null ? Theme.TextPrimary : Theme.TextSecondary))
                Draw.DrawStringLeft(g, title, Theme.FontOf(10F, FontStyle.Bold), brush, new Rectangle(textX, frame.Y + Dpi(10), textWidth, Dpi(24)));
            Color statusColor = StatusIsError ? Theme.DeleteRed : notice != null ? Theme.WarningText : Theme.TextSecondary;
            using (SolidBrush brush = new SolidBrush(statusColor))
                Draw.DrawStringLeft(g, StatusText, Theme.CaptionFont, brush, new Rectangle(textX, frame.Y + Dpi(36), textWidth, Dpi(24)));
        }

        /// <summary>等比裁剪填满（居中取景），与集合封面默认的显示方式一致。</summary>
        internal static void DrawCoverCrop(Graphics g, Bitmap source, Rectangle target)
        {
            if (source == null || target.Width <= 0 || target.Height <= 0) return;
            float scale = Math.Max(target.Width / (float)source.Width, target.Height / (float)source.Height);
            float w = target.Width / scale, h = target.Height / scale;
            RectangleF src = new RectangleF((source.Width - w) / 2F, (source.Height - h) / 2F, w, h);
            InterpolationMode old = g.InterpolationMode;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(source, target, src, GraphicsUnit.Pixel);
            g.InterpolationMode = old;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); return true; }
            if (keyData == Keys.Enter)
            {
                // 自绘按钮不是系统默认按钮：焦点在「取消」上回车就取消，在网格里选中的是文件夹就进入它，其余等同主按钮
                if (cancelButton.Focused) { DialogResult = DialogResult.Cancel; Close(); }
                else if (grid.Focused && grid.SelectedFolder != null) Navigate(grid.SelectedFolder);
                else Confirm();
                return true;
            }
            if (keyData == Keys.Back && !pathBox.Focused) { GoUp(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        /// <summary>
        /// 嵌在主窗口里时，Tab 在页面里循环：页面只是盖在集合编辑页上面，编辑页的输入框还在下面；
        /// 不循环的话 Tab 走到最后一个控件后会把焦点交给主窗口，落进被盖住的名称或简介框，打字就改到看不见的地方。
        /// </summary>
        protected override bool ProcessTabKey(bool forward)
        {
            if (Embedded) return SelectNextControl(ActiveControl, forward, true, true, true);
            return base.ProcessTabKey(forward);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            checkVersion++;   // 还在后台检查的结果作废
            loadGen++;
            previewGen++;
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                checkVersion++;
                loadGen++;
                previewGen++;
                if (previewSource != null) { previewSource.Dispose(); previewSource = null; }
            }
            base.Dispose(disposing);
        }

        protected override void OnDragEnter(DragEventArgs e)
        {
            base.OnDragEnter(e);
            e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        /// <summary>拖进图片：进入它所在的文件夹并选中它；拖进文件夹：进入它（是否存在在后台确定时检查）。</summary>
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
            if (dropped == null || dropped.Length == 0) return;
            DropPath(dropped[0], dropped.Length > 1);
        }

        internal void DropPath(string path, bool more)
        {
            if (checking || string.IsNullOrEmpty(path)) return;
            if (PathPickerDialog.HasExtension(path, MediaRules.ImageExt))
            {
                string folder = SafeDirectory(path);
                ChooseImage(path);
                string tooMany = more ? "一次只能选一张，已使用第一张。" : null;
                if (folder != null && !string.Equals(folder, currentFolder, StringComparison.OrdinalIgnoreCase))
                {
                    Navigate(folder);
                    pendingSelect = path;
                    pendingNotice = tooMany;   // 读完文件夹选中图片时会清掉提示，届时再显示
                }
                else grid.SelectPath(path);
                if (tooMany != null) SetNotice(tooMany, false);
                return;
            }
            // 不是图片：当作文件夹路径交给确定流程在后台检查，是文件夹就进入
            ClearPreview();
            SetPathText(path);
            Confirm();
        }

        /// <summary>后台线程把结果交回界面线程；窗口已关闭时丢弃。</summary>
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
    }
}

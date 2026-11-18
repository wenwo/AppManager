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
    /// <summary>路径选择窗口的用途：选一个文件夹、打开一个已有文件、或指定保存位置和文件名。</summary>
    internal enum PathPickerMode { Folder, OpenFile, SaveFile }

    internal sealed class PathPickerOptions
    {
        internal string Title = string.Empty;
        internal string Hint = string.Empty;
        internal PathPickerMode Mode;
        internal string ConfirmText = "确定";
        /// <summary>打开时预先展开并选中的文件夹；为空时用「上次使用」的文件夹，再没有就用「文档」。</summary>
        internal string InitialFolder;
        /// <summary>保存模式的默认文件名。</summary>
        internal string FileName;
        /// <summary>打开/保存模式允许的扩展名（带点，如 ".db"）；保存时没写扩展名会补上第一个。</summary>
        internal string[] Extensions = new string[0];
        /// <summary>选文件夹时允许填一个还不存在的路径（再确认一次后新建）。</summary>
        internal bool AllowCreateFolder;
        /// <summary>选定后、关闭前的业务校验（界面线程调用）：返回错误说明则留在窗口里显示，返回 null 表示通过。</summary>
        internal Func<string, string> Validate;
    }

    /// <summary>后台检查输入路径的结果。</summary>
    internal sealed class PathPickerCheck
    {
        internal string Path;
        internal string Error;
        internal bool AskCreate;      // 文件夹不存在：再确认一次就新建
        internal bool AskOverwrite;   // 已有同名文件：再确认一次就替换
    }

    /// <summary>
    /// 与「添加本地扫描」同一风格的路径选择窗口，代替系统的打开/保存/选文件夹对话框：
    /// 常用位置和磁盘列在同一个清单里，行左侧的箭头原地展开下一级（打开、保存时连同匹配类型的文件一起列出）；
    /// 底部输入框显示选中的路径，也可以输入、粘贴或把文件/文件夹拖进来。
    /// 出错、替换同名文件、新建文件夹都在清单下方的提示行里说明，需要确认的再点一次主按钮即可，不另弹窗口。
    /// 目录在后台读取；确认时的磁盘检查也在后台进行，网络路径卡住时界面不冻结。
    /// </summary>
    internal sealed class PathPickerDialog : BufferedDialogForm
    {
        private const int HeaderHeight = 30;
        private const int NoteHeight = 30;
        private const int RowHeight = 46;
        private const int ChildRowHeight = 34;
        private const int IndentBase = 8;
        private const int IndentStep = 20;
        private const int ChevronWidth = 20;
        private const int IconSize = 18;
        internal const int MaxListedFiles = 500;
        /// <summary>主窗口添加页一次最多勾选的项数。</summary>
        internal const int MaxCheckedFiles = 1000;
        private const int EM_SETCUEBANNER = 0x1501;

        private const string GlyphFolder = "\uE8B7";
        private const string GlyphDrive = "\uEDA2";
        private const string GlyphFile = "\uE8A5";
        private const string GlyphPictures = "\uEB9F";
        private const string GlyphMusic = "\uE8D6";
        private const string GlyphVideo = "\uE71D";
        private const string GlyphDownload = "\uE896";

        /// <summary>本次运行里上一次选定的文件夹：再次打开时列在「常用位置」并预先展开。</summary>
        private static string lastFolder;

        private enum RowKind { Header, Note, Place, Drive, Folder, File }

        private sealed class Row
        {
            internal RowKind Kind;
            internal string Title = string.Empty;
            internal string Detail;
            internal string Path;
            internal int Depth;
            internal bool Enabled = true;
            internal bool Warning;
            internal bool Expandable;
            internal bool Expanded;
            internal string Glyph;

            public override string ToString() { return Title; }
        }

        /// <summary>一个文件夹的下一级内容；Loading 表示后台仍在读取。</summary>
        private sealed class Contents
        {
            internal readonly List<string> Folders = new List<string>();
            internal readonly List<string> Files = new List<string>();
            internal bool Loading;
            internal string Error;
            internal int Omitted;
        }

        private sealed class Place
        {
            internal string Label;
            internal string Path;
            internal FilePickerPlaceKind Kind;
        }

        private readonly PathPickerOptions options;
        private readonly List<Place> places = new List<Place>();
        private readonly HashSet<string> expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Contents> contents = new Dictionary<string, Contents>(StringComparer.OrdinalIgnoreCase);
        private readonly SoftwarePickerDialog.PickerList list = new SoftwarePickerDialog.PickerList();
        private readonly Label noticeLabel = new Label();
        private readonly TextBox pathBox = new TextBox();
        private readonly RoundButton confirmButton = new RoundButton();
        private readonly RoundButton cancelButton = new RoundButton();
        private readonly Rectangle listFrame = new Rectangle(24, 80, 552, 372);   // 96 DPI 设计坐标，绘制时换算
        private readonly Rectangle pathFrame = new Rectangle(24, 500, 552, 34);
        private List<LocalScanScopeDrive> drives;   // null：仍在后台读取
        private string notice;            // 最近一次操作的提示；输入变化时清掉，恢复显示选中路径
        private bool noticeIsError;
        private string confirmedCreate;   // 已提示过「不存在、再点一次新建」的文件夹
        private string confirmedOverwrite;   // 已提示过「再点一次替换」的文件
        private bool checking;
        private int checkVersion;
        private bool settingText;         // 程序改写输入框时不清掉提示和待确认状态

        /// <summary>确定后的完整路径（保存模式已补好扩展名）。</summary>
        internal string ResultPath;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, string lParam);

        /// <summary>以模态方式显示（带遮罩），返回选定的路径；取消返回 null。</summary>
        internal static string Pick(PathPickerOptions options)
        {
            using (PathPickerDialog dialog = new PathPickerDialog(options))
            {
                ConfirmDialog.ShowModal(dialog);
                return dialog.DialogResult == DialogResult.OK ? dialog.ResultPath : null;
            }
        }

        internal PathPickerDialog(PathPickerOptions options)
        {
            if (options == null) throw new ArgumentNullException("options");
            this.options = options;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(600, 604);
            BackColor = Theme.Background;
            ShowInTaskbar = false;
            AllowDrop = true;
            Text = options.Title;

            Label titleLabel = new Label();
            titleLabel.Text = options.Title;
            titleLabel.Font = Theme.DialogTitleFont;
            titleLabel.ForeColor = Theme.TextPrimary;
            titleLabel.AutoSize = false;
            titleLabel.UseMnemonic = false;
            titleLabel.SetBounds(24, 18, 552, 28);
            Controls.Add(titleLabel);

            Label hint = new Label();
            hint.Text = string.IsNullOrEmpty(options.Hint) ? DefaultHint() : options.Hint;
            hint.Font = Theme.CaptionFont;
            hint.ForeColor = Theme.TextSecondary;
            hint.AutoSize = false;
            hint.AutoEllipsis = true;
            hint.UseMnemonic = false;
            hint.SetBounds(24, 48, 552, 22);
            Controls.Add(hint);

            list.DrawMode = DrawMode.OwnerDrawVariable;
            list.SetBounds(listFrame.X + 4, listFrame.Y + 4, listFrame.Width - 8, listFrame.Height - 8);
            list.AccessibleName = options.Mode == PathPickerMode.Folder ? "文件夹" : "文件夹和文件";
            list.MeasureItem += MeasureRow;
            list.DrawItem += DrawRow;
            list.MouseUp += List_MouseUp;
            list.MouseDoubleClick += List_MouseDoubleClick;
            list.KeyDown += List_KeyDown;
            Controls.Add(list);

            // 两行高：平时显示选中的路径，出错或需要再确认时换成提示文字
            noticeLabel.Font = Theme.CaptionFont;
            noticeLabel.AutoSize = false;
            noticeLabel.AutoEllipsis = true;
            noticeLabel.UseMnemonic = false;
            noticeLabel.TextAlign = ContentAlignment.MiddleLeft;
            noticeLabel.SetBounds(24, 456, 552, 38);
            Controls.Add(noticeLabel);

            pathBox.BorderStyle = BorderStyle.None;
            pathBox.BackColor = Theme.SearchBg;
            pathBox.ForeColor = Theme.TextPrimary;
            pathBox.Font = Theme.BodyFont;
            pathBox.AccessibleName = "路径";
            pathBox.HandleCreated += delegate { SendMessage(pathBox.Handle, EM_SETCUEBANNER, (IntPtr)1, CueText()); };
            pathBox.GotFocus += delegate { pathBox.BackColor = Color.White; Invalidate(); };
            pathBox.LostFocus += delegate { pathBox.BackColor = Theme.SearchBg; Invalidate(); };
            pathBox.TextChanged += delegate
            {
                if (settingText) return;
                notice = null;
                RefreshNotice();
                list.Invalidate();   // 选中行跟随输入框
            };
            Controls.Add(pathBox);
            LayoutPathBox();

            confirmButton.Text = options.ConfirmText;
            confirmButton.Accent = true;
            confirmButton.SetBounds(356, 548, 120, 34);
            confirmButton.Click += delegate { Confirm(); };
            Controls.Add(confirmButton);

            cancelButton.Text = "取消";
            cancelButton.SetBounds(486, 548, 90, 34);
            cancelButton.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(cancelButton);

            BuildPlaces();
            string start = StartFolder();
            if (start != null)
            {
                if (options.Mode == PathPickerMode.SaveFile)
                    SetPathText(JoinPath(start, string.IsNullOrEmpty(options.FileName) ? string.Empty : options.FileName));
                else if (options.Mode == PathPickerMode.Folder && options.InitialFolder != null)
                    SetPathText(start);
                // 打开和保存时预先展开起始文件夹，直接看到里面已有的文件（窗口显示后才开始读取）
                if (options.Mode != PathPickerMode.Folder) expanded.Add(start);
            }
            else if (options.Mode == PathPickerMode.SaveFile && !string.IsNullOrEmpty(options.FileName))
                SetPathText(options.FileName);
            RefreshRows();
        }

        private string DefaultHint()
        {
            switch (options.Mode)
            {
                case PathPickerMode.OpenFile: return "点左侧箭头展开文件夹，选中要打开的文件；也可以在下面粘贴路径或把文件拖进来。";
                case PathPickerMode.SaveFile: return "选一个文件夹，在下面改文件名；也可以直接粘贴完整路径。";
                default: return "选中一个文件夹，点左侧箭头可展开子文件夹；也可以在下面粘贴路径或把文件夹拖进来。";
            }
        }

        private string CueText()
        {
            switch (options.Mode)
            {
                case PathPickerMode.OpenFile: return "粘贴或输入文件路径，也可以把文件拖进来";
                case PathPickerMode.SaveFile: return "保存位置和文件名，例如 D:\\备份\\" + (options.FileName ?? "文件");
                default: return "粘贴或输入文件夹路径，也可以把文件夹拖进来";
            }
        }

        // ===== 起始位置 =====

        private void BuildPlaces()
        {
            foreach (FilePickerPlace item in FilePickerPlaces.Build(lastFolder, options.InitialFolder, null, true))
                places.Add(new Place { Label = item.Label, Path = item.Path, Kind = item.Kind });
        }

        /// <summary>起始文件夹：调用方指定的、上次使用的、「文档」，取第一个列出来的。</summary>
        private string StartFolder()
        {
            string wanted = LocalScanScope.NormalizePath(options.InitialFolder ?? lastFolder);
            foreach (Place place in places)
                if (wanted != null && string.Equals(place.Path, wanted, StringComparison.OrdinalIgnoreCase)) return place.Path;
            foreach (Place place in places)
                if (place.Label == "文档") return place.Path;
            return places.Count > 0 ? places[0].Path : null;
        }

        /// <summary>路径是否在本机固定/可移动磁盘上（只看盘符类型，不访问磁盘）。</summary>
        private static bool IsQuickLocal(string full)
        {
            if (full == null || full.Length < 3 || full[1] != ':') return false;
            try
            {
                DriveType type = new DriveInfo(full.Substring(0, 3)).DriveType;
                return type == DriveType.Fixed || type == DriveType.Removable;
            }
            catch (ArgumentException) { return false; }
        }

        // ===== 生命周期与布局 =====

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (options.Mode == PathPickerMode.SaveFile) FocusFileName();
            else list.Focus();
            LoadDrivesAsync();
            foreach (string path in new List<string>(expanded)) LoadContents(path);
            RefreshRows();
        }

        protected override void OnDpiScaleChanged()
        {
            base.OnDpiScaleChanged();
            LayoutPathBox();
            RefreshRows();   // 行高不随窗体缩放自动变化，重新加入条目才会重新测量
            Invalidate(true);
        }

        /// <summary>单行输入框的高度由字体决定，不随窗体缩放：按当前 DPI 重新放到外框中间。</summary>
        private void LayoutPathBox()
        {
            pathBox.SetBounds(Dpi(pathFrame.X + 12), Dpi(pathFrame.Y) + (Dpi(pathFrame.Height) - pathBox.Height) / 2,
                Dpi(pathFrame.Width - 24), pathBox.Height);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle frame = new Rectangle(Dpi(listFrame.X), Dpi(listFrame.Y), Dpi(listFrame.Width), Dpi(listFrame.Height));
            using (SolidBrush fill = new SolidBrush(Theme.Card)) Draw.FillRoundedRect(g, fill, frame, Dpi(8));
            using (Pen border = new Pen(Theme.ControlBorder, 1)) Draw.DrawRoundedRect(g, border, frame, Dpi(8));

            Rectangle input = new Rectangle(Dpi(pathFrame.X), Dpi(pathFrame.Y), Dpi(pathFrame.Width), Dpi(pathFrame.Height));
            bool focused = pathBox.Focused;
            using (SolidBrush fill = new SolidBrush(focused ? Color.White : Theme.SearchBg)) Draw.FillRoundedRect(g, fill, input, Dpi(6));
            using (Pen border = new Pen(focused ? Theme.Accent : Theme.ControlBorder, focused ? 1.4F : 1F))
                Draw.DrawRoundedRect(g, border, input, Dpi(6));
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); return true; }
            if (keyData == Keys.Enter)
            {
                if (list.Focused)
                {
                    Row row = RowAt(list.SelectedIndex);
                    if (row != null && row.Expandable && options.Mode != PathPickerMode.Folder)
                    {
                        ToggleExpand(list.SelectedIndex);
                        return true;
                    }
                }
                // 自绘按钮不是系统默认按钮：焦点在「取消」上回车就取消，其余情况等同主按钮
                if (cancelButton.Focused) { DialogResult = DialogResult.Cancel; Close(); }
                else Confirm();
                return true;
            }
            if (keyData == Keys.Back && !pathBox.Focused) { CollapseSelection(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void CollapseSelection()
        {
            Row current = RowAt(list.SelectedIndex);
            if (current == null) return;
            if (current.Expanded) { ToggleExpand(list.SelectedIndex); return; }
            for (int index = list.SelectedIndex - 1; index >= 0; index--)
            {
                Row parent = RowAt(index);
                if (parent == null || !parent.Expandable || parent.Depth >= current.Depth) continue;
                ToggleExpand(index);
                list.SelectedIndex = index;
                return;
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            checkVersion++;   // 还在后台检查的结果作废
            base.OnFormClosed(e);
        }

        protected override void OnDragEnter(DragEventArgs e)
        {
            base.OnDragEnter(e);
            e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        /// <summary>拖进来的文件写进输入框；拖进文件夹按用途进入、展开或替换保存位置。</summary>
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
            if (string.IsNullOrEmpty(path)) return;
            bool folder = false;
            try { folder = Directory.Exists(path); } catch { }
            if (folder)
            {
                if (options.Mode == PathPickerMode.SaveFile) ChooseFolder(path);
                else
                {
                    if (options.Mode == PathPickerMode.Folder) SetPathText(path);
                    else EnterDroppedFolder(path);
                }
            }
            else SetPathText(path);
            notice = null;
            if (more) SetNotice("一次只能选一项，已使用第一项：" + path, false);
            else RefreshNotice();
            list.Invalidate();
        }

        private void EnterDroppedFolder(string path)
        {
            string full = LocalScanScope.NormalizePath(path) ?? path;
            bool known = false;
            foreach (Place place in places)
            {
                if (!string.Equals(place.Path, full, StringComparison.OrdinalIgnoreCase)) continue;
                known = true;
                break;
            }
            if (!known) places.Add(new Place { Label = "当前位置", Path = full, Kind = FilePickerPlaceKind.Current });
            expanded.Add(full);
            LoadContents(full);
            RefreshRows();
            for (int i = 0; i < list.Items.Count; i++)
            {
                Row row = RowAt(i);
                if (row != null && string.Equals(row.Path, full, StringComparison.OrdinalIgnoreCase))
                {
                    list.SelectedIndex = i;
                    break;
                }
            }
        }

        // ===== 目录读取 =====

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
                    RefreshRows();
                });
            });
        }

        /// <summary>后台读取一个文件夹的下一级；每次展开都重新读取，看到的是磁盘上的当前内容。</summary>
        private void LoadContents(string path)
        {
            Contents current;
            if (contents.TryGetValue(path, out current) && current.Loading) return;
            contents[path] = new Contents { Loading = true };
            bool files = options.Mode != PathPickerMode.Folder;
            string[] extensions = options.Extensions;
            ThreadPool.QueueUserWorkItem(delegate
            {
                Contents loaded = new Contents();
                LocalScanScopeChildren folders = LocalScanScope.ListSubfolders(path, null);
                loaded.Error = folders.Error;
                loaded.Folders.AddRange(folders.Paths);
                loaded.Omitted = folders.Omitted;
                if (files && loaded.Error == null)
                {
                    string error;
                    int omitted;
                    loaded.Files.AddRange(ListFiles(path, extensions, out omitted, out error));
                    loaded.Error = error;
                    loaded.Omitted += omitted;
                }
                Post(delegate
                {
                    contents[path] = loaded;
                    if (expanded.Contains(path)) RefreshRows();
                });
            });
        }

        /// <summary>
        /// 列出文件夹里扩展名匹配的文件（不含隐藏文件），按资源管理器的名称顺序，最多 MaxListedFiles 个，其余只计数。
        /// 会访问磁盘，只在后台线程调用；读取失败时返回空列表并给出 error。
        /// </summary>
        internal static List<string> ListFiles(string folder, string[] extensions, out int omitted, out string error)
        {
            List<FileInfo> infos = ListFileInfos(folder, extensions, MaxListedFiles, null, out omitted, out error);
            List<string> found = new List<string>(infos.Count);
            foreach (FileInfo file in infos) found.Add(file.FullName);
            return found;
        }

        /// <summary>
        /// 同 ListFiles，但最多列出 max 个，并带回文件信息：大小和修改时间来自目录枚举本身，读取时不再逐个访问磁盘。
        /// 会访问磁盘，只在后台线程调用；读取中途失败时，已读到的部分照样排序、截断并给出 error。
        /// cancelled 不为空且返回 true 时立即停止（换了文件夹、页面已关闭），返回值不再使用。
        /// </summary>
        internal static List<FileInfo> ListFileInfos(string folder, string[] extensions, int max, Func<bool> cancelled,
            out int omitted, out string error)
        {
            omitted = 0;
            error = null;
            List<FileInfo> found = new List<FileInfo>();
            try
            {
                foreach (FileInfo file in new DirectoryInfo(folder).EnumerateFiles())
                {
                    if (cancelled != null && cancelled()) return found;
                    if ((file.Attributes & FileAttributes.Hidden) != 0) continue;
                    if (!HasExtension(file.Name, extensions)) continue;
                    found.Add(file);
                }
            }
            catch (IOException ex) { error = ex.Message; }
            catch (UnauthorizedAccessException ex) { error = ex.Message; }
            catch (System.Security.SecurityException ex) { error = ex.Message; }
            catch (ArgumentException ex) { error = ex.Message; }
            try { found.Sort(delegate(FileInfo a, FileInfo b) { return StrCmpLogicalW(a.Name, b.Name); }); }
            catch (InvalidOperationException) { found.Sort(delegate(FileInfo a, FileInfo b) { return StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name); }); }
            if (found.Count > max)
            {
                omitted = found.Count - max;
                found.RemoveRange(max, omitted);
            }
            return found;
        }

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern int StrCmpLogicalW(string a, string b);

        internal static bool HasExtension(string name, string[] extensions)
        {
            if (extensions == null || extensions.Length == 0) return true;
            foreach (string extension in extensions)
                if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>后台线程把结果交回界面线程；窗口已关闭时丢弃。</summary>
        private void Post(Action action)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke((Action)delegate { if (!IsDisposed && !Disposing) action(); }); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        // ===== 清单 =====

        private void Expand(string path)
        {
            if (expanded.Add(path)) LoadContents(path);
        }

        private void RefreshRows()
        {
            List<Row> rows = BuildRows();
            Row selected = RowAt(list.SelectedIndex);
            string selectedPath = selected == null ? null : selected.Path;
            int top = list.TopIndex;
            list.BeginUpdate();
            try
            {
                list.Items.Clear();
                list.Items.AddRange(rows.ToArray());
            }
            finally { list.EndUpdate(); }
            list.ResetHover();
            if (selectedPath != null)
                for (int i = 0; i < rows.Count; i++)
                    if (rows[i].Enabled && string.Equals(rows[i].Path, selectedPath, StringComparison.OrdinalIgnoreCase)) { list.SelectedIndex = i; break; }
            if (top > 0 && top < list.Items.Count) list.TopIndex = top;
            RefreshNotice();
        }

        private List<Row> BuildRows()
        {
            List<Row> rows = new List<Row>();
            if (places.Count > 0)
            {
                rows.Add(new Row { Kind = RowKind.Header, Title = "常用位置", Enabled = false });
                foreach (Place place in places)
                {
                    rows.Add(FolderRow(RowKind.Place, place.Label, place.Path, place.Path, 0, GlyphForPlace(place.Kind)));
                    AddChildren(rows, place.Path, 1);
                }
            }
            rows.Add(new Row { Kind = RowKind.Header, Title = "磁盘", Enabled = false });
            if (drives == null) rows.Add(new Row { Kind = RowKind.Note, Title = "正在读取磁盘…", Enabled = false });
            else if (drives.Count == 0) rows.Add(new Row { Kind = RowKind.Note, Title = "没有找到可用的磁盘。", Enabled = false, Warning = true });
            else
            {
                foreach (LocalScanScopeDrive drive in drives)
                {
                    string root = LocalScanScope.NormalizePath(drive.Path);
                    Row row = FolderRow(RowKind.Drive, drive.Title, drive.Detail, root, 0, GlyphDrive);
                    row.Enabled = drive.Supported;
                    row.Expandable = drive.Supported;
                    row.Expanded = drive.Supported && expanded.Contains(root);
                    rows.Add(row);
                    if (drive.Supported) AddChildren(rows, root, 1);
                }
            }
            return rows;
        }

        private Row FolderRow(RowKind kind, string title, string detail, string path, int depth, string glyph)
        {
            return new Row
            {
                Kind = kind, Title = title, Detail = detail, Path = path, Depth = depth,
                Expandable = true, Expanded = expanded.Contains(path), Glyph = glyph
            };
        }

        private static string GlyphForPlace(FilePickerPlaceKind kind)
        {
            switch (kind)
            {
                case FilePickerPlaceKind.Pictures: return GlyphPictures;
                case FilePickerPlaceKind.Music: return GlyphMusic;
                case FilePickerPlaceKind.Videos: return GlyphVideo;
                case FilePickerPlaceKind.Downloads: return GlyphDownload;
                default: return GlyphFolder;
            }
        }

        private void AddChildren(List<Row> rows, string path, int depth)
        {
            if (!expanded.Contains(path)) return;
            Contents current;
            if (!contents.TryGetValue(path, out current) || current.Loading)
            {
                rows.Add(new Row { Kind = RowKind.Note, Title = "正在读取…", Depth = depth, Enabled = false });
                return;
            }
            if (current.Error != null)
                rows.Add(new Row { Kind = RowKind.Note, Title = "无法读取：" + current.Error, Depth = depth, Enabled = false, Warning = true });
            foreach (string folder in current.Folders)
            {
                rows.Add(FolderRow(RowKind.Folder, Path.GetFileName(folder), null, folder, depth, GlyphFolder));
                AddChildren(rows, folder, depth + 1);
            }
            foreach (string file in current.Files)
                rows.Add(new Row { Kind = RowKind.File, Title = Path.GetFileName(file), Path = file, Depth = depth });
            if (current.Omitted > 0)
                rows.Add(new Row { Kind = RowKind.Note, Title = "还有 " + current.Omitted + " 项没有列出，可以在下面直接输入路径。",
                    Depth = depth, Enabled = false });
            else if (current.Error == null && current.Folders.Count == 0 && current.Files.Count == 0)
                rows.Add(new Row { Kind = RowKind.Note, Title = options.Mode == PathPickerMode.Folder ? "没有子文件夹" : "没有子文件夹或可选的文件", Depth = depth, Enabled = false });
        }

        private Row RowAt(int index)
        {
            return index < 0 || index >= list.Items.Count ? null : list.Items[index] as Row;
        }

        /// <summary>图标左边缘相对行左边缘的位置；它左侧是缩进和展开箭头。</summary>
        private int IconLeft(Row row)
        {
            return Dpi(IndentBase + row.Depth * IndentStep + ChevronWidth + 2);
        }

        private void List_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            int index = list.HitRow(e.Location);
            Row row = RowAt(index);
            if (row == null || !row.Enabled) return;
            if (row.Expandable && e.X < IconLeft(row)) ToggleExpand(index);
            else Choose(index);
        }

        /// <summary>
        /// 双击文件：选中并确定；
        /// 双击文件夹：展开或收起（打开模式下单击文件夹只选中这一行，同样双击展开）。
        /// </summary>
        private void List_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            int index = list.HitRow(e.Location);
            Row row = RowAt(index);
            if (row == null || !row.Enabled || (row.Expandable && e.X < IconLeft(row))) return;
            if (row.Kind == RowKind.File) Confirm();
            else if (row.Expandable) ToggleExpand(index);
        }

        private void List_KeyDown(object sender, KeyEventArgs e)
        {
            Row row = RowAt(list.SelectedIndex);
            if (e.KeyCode == Keys.Space) Choose(list.SelectedIndex);
            else if (e.KeyCode == Keys.Down) MoveSelection(1);
            else if (e.KeyCode == Keys.Up) MoveSelection(-1);
            else if (e.KeyCode == Keys.Right) { if (row != null && row.Expandable && !row.Expanded) ToggleExpand(list.SelectedIndex); }
            else if (e.KeyCode == Keys.Left) { if (row != null && row.Expanded) ToggleExpand(list.SelectedIndex); }
            else return;
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        /// <summary>上下键只在可选的行之间移动，跳过分组标题、提示行和不可用的磁盘。</summary>
        private void MoveSelection(int step)
        {
            int index = list.SelectedIndex;
            if (index < 0 && step < 0) index = list.Items.Count;
            for (index += step; index >= 0 && index < list.Items.Count; index += step)
            {
                Row row = RowAt(index);
                if (row == null || !row.Enabled) continue;
                list.SelectedIndex = index;
                return;
            }
        }

        private void ToggleExpand(int index)
        {
            Row row = RowAt(index);
            if (row == null || !row.Expandable) return;
            list.SelectedIndex = index;
            if (!expanded.Remove(row.Path)) Expand(row.Path);
            RefreshRows();
        }

        /// <summary>
        /// 点选一行：文件夹模式选中文件夹；保存模式把文件夹换进输入框、保留文件名，点文件则用它的名字（会提示替换）；
        /// 打开模式点文件夹只是移到这一行（双击或点箭头展开），点文件才选中。
        /// </summary>
        private void Choose(int index)
        {
            Row row = RowAt(index);
            if (row == null || !row.Enabled || row.Path == null) return;
            list.SelectedIndex = index;
            if (row.Kind == RowKind.File) SetPathText(row.Path);
            else if (options.Mode == PathPickerMode.OpenFile) { list.Invalidate(); return; }
            else if (options.Mode == PathPickerMode.SaveFile) ChooseFolder(row.Path);
            else SetPathText(row.Path);
            notice = null;
            RefreshNotice();
            list.Invalidate();
        }

        /// <summary>保存模式选了一个文件夹：换掉输入框里的文件夹部分，文件名不变。</summary>
        private void ChooseFolder(string folder)
        {
            SetPathText(JoinPath(folder, CurrentFileName()));
        }

        private string CurrentFileName()
        {
            string text = pathBox.Text.Trim().Trim('"');
            string name = string.Empty;
            try { name = Path.GetFileName(text); } catch (ArgumentException) { }
            return name.Length > 0 ? name : (options.FileName ?? string.Empty);
        }

        private static string JoinPath(string folder, string name)
        {
            string root = LocalScanScope.NormalizePath(folder) ?? folder;
            if (string.IsNullOrEmpty(name)) return root;
            return root.EndsWith("\\", StringComparison.Ordinal) ? root + name : root + "\\" + name;
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

        /// <summary>保存模式打开时选中文件名（不含扩展名），可以直接改名。</summary>
        private void FocusFileName()
        {
            pathBox.Focus();
            string text = pathBox.Text;
            int slash = text.LastIndexOf('\\');
            int start = slash + 1;
            string name = text.Substring(start);
            int dot = name.LastIndexOf('.');
            pathBox.Select(start, dot > 0 ? dot : name.Length);
        }

        // ===== 提示行 =====

        private void SetNotice(string text, bool isError)
        {
            notice = text;
            noticeIsError = isError;
            RefreshNotice();
        }

        private void RefreshNotice()
        {
            if (notice != null)
            {
                noticeLabel.Text = notice;
                noticeLabel.ForeColor = noticeIsError ? Theme.DeleteRed : Theme.WarningText;
            }
            else
            {
                string text = pathBox.Text.Trim();
                noticeLabel.ForeColor = text.Length == 0 ? Theme.TextSecondary : Theme.AccentText;
                if (text.Length == 0)
                    noticeLabel.Text = options.Mode == PathPickerMode.OpenFile ? "请选择一个 " + ExtensionText() + "文件。"
                        : options.Mode == PathPickerMode.SaveFile ? "请选择保存位置并填写文件名。" : "请选择一个文件夹。";
                else
                    noticeLabel.Text = (options.Mode == PathPickerMode.SaveFile ? "将保存为：" : "已选：") + text;
            }
            bool ready = !checking && pathBox.Text.Trim().Length > 0;
            confirmButton.Enabled = ready;
            confirmButton.Accent = ready;
        }

        private string ExtensionText()
        {
            return options.Extensions == null || options.Extensions.Length == 0 ? string.Empty
                : DescribeExtensions(options.Extensions) + " ";
        }

        /// <summary>提示里写的类型：不超过 4 种全部列出，再多只列前 3 种加总数，免得一行提示被扩展名占满、截掉真正的说明。</summary>
        internal static string DescribeExtensions(string[] extensions)
        {
            if (extensions == null || extensions.Length == 0) return string.Empty;
            if (extensions.Length <= 4) return string.Join(" / ", extensions);
            return string.Join(" / ", extensions, 0, 3) + " 等 " + extensions.Length + " 种";
        }

        // ===== 确定 =====

        /// <summary>
        /// 在后台检查输入框里的路径（磁盘访问可能很慢），回到界面线程后：出错就提示；需要新建文件夹或替换文件时
        /// 先提示，同一路径再点一次才执行；通过后再走调用方的业务校验，全部通过才关闭窗口。
        /// </summary>
        internal void Confirm()
        {
            if (checking) return;
            string reason;
            string cleaned = LocalScanScope.CleanTypedPath(pathBox.Text, out reason);
            if (cleaned == null)
            {
                SetNotice(reason ?? (options.Mode == PathPickerMode.Folder ? "请先选择一个文件夹。" : "请先选择一个文件。"), true);
                return;
            }
            // 选文件夹时先做业务校验（只看路径，不访问磁盘）：不合格的路径不提示新建，更不会先建出一个空文件夹
            if (options.Mode == PathPickerMode.Folder)
            {
                string full = null;
                try { full = LocalScanScope.NormalizePath(Path.GetFullPath(cleaned)); }
                catch (Exception ex)
                {
                    if (!(ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)) throw;
                }
                string early = full == null ? null : RunValidate(full);
                if (early != null) { SetNotice(early, true); return; }
            }
            checking = true;
            confirmButton.Text = "正在检查…";
            RefreshNotice();
            int version = ++checkVersion;
            PathPickerMode mode = options.Mode;
            string[] extensions = options.Extensions;
            bool allowCreate = options.AllowCreateFolder;
            string create = confirmedCreate, overwrite = confirmedOverwrite;
            ThreadPool.QueueUserWorkItem(delegate
            {
                PathPickerCheck result;
                try { result = CheckPath(mode, cleaned, extensions, allowCreate, create, overwrite); }
                catch (Exception ex) { Paths.LogError(ex); result = new PathPickerCheck { Error = ex.Message }; }
                Post(delegate { if (version == checkVersion) FinishConfirm(result); });
            });
        }

        private void FinishConfirm(PathPickerCheck result)
        {
            checking = false;
            confirmButton.Text = options.ConfirmText;
            if (result.Error != null) { SetNotice(result.Error, true); return; }
            if (result.AskCreate)
            {
                confirmedCreate = result.Path;
                SetNotice("文件夹「" + result.Path + "」还不存在。再点一次「" + options.ConfirmText + "」会新建它。", false);
                return;
            }
            if (result.AskOverwrite)
            {
                confirmedOverwrite = result.Path;
                SetNotice("已有同名文件「" + Path.GetFileName(result.Path) + "」。再点一次「" + options.ConfirmText + "」会替换它。", false);
                return;
            }
            string error = RunValidate(result.Path);
            if (error != null) { SetNotice(error, true); return; }
            ResultPath = result.Path;
            lastFolder = options.Mode == PathPickerMode.Folder ? result.Path : Path.GetDirectoryName(result.Path);
            DialogResult = DialogResult.OK;
            Close();
        }

        /// <summary>列出现在已经不存在、或其实是文件夹的路径（会访问磁盘，只在后台线程调用；主窗口添加页确定前用）。</summary>
        internal static List<string> UnavailableFiles(IEnumerable<string> paths)
        {
            List<string> gone = new List<string>();
            foreach (string path in paths)
                if (!File.Exists(path)) gone.Add(path);
            return gone;
        }

        /// <summary>调用方的业务校验；没有校验时返回 null。校验本身出错时记日志并当作不通过，窗口留着可以换个路径重试。</summary>
        private string RunValidate(string path)
        {
            if (options.Validate == null) return null;
            try { return options.Validate(path); }
            catch (Exception ex)
            {
                Paths.LogError(ex);
                return "无法检查这个位置：" + ex.Message;
            }
        }

        /// <summary>
        /// 检查一个完整路径能否作为结果（会访问磁盘，只在后台线程调用）。
        /// confirmedCreate / confirmedOverwrite 是用户已经被提示过、再次确认的路径：与本次路径相同时才新建文件夹或接受替换。
        /// </summary>
        internal static PathPickerCheck CheckPath(PathPickerMode mode, string path, string[] extensions, bool allowCreate,
            string confirmedCreate, string confirmedOverwrite)
        {
            PathPickerCheck result = new PathPickerCheck();
            string full;
            try { full = Path.GetFullPath(path); }
            catch (Exception ex)
            {
                if (!(ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)) throw;
                result.Error = "路径格式不正确：" + ex.Message;
                return result;
            }
            if (mode == PathPickerMode.Folder)
            {
                full = LocalScanScope.NormalizePath(full) ?? full;
                result.Path = full;
                if (Directory.Exists(full)) return result;
                if (File.Exists(full)) { result.Error = "这是一个文件，请选择文件夹。"; return result; }
                if (!allowCreate) { result.Error = "文件夹不存在或当前不可访问。"; return result; }
                if (!string.Equals(confirmedCreate, full, StringComparison.OrdinalIgnoreCase)) { result.AskCreate = true; return result; }
                try { Directory.CreateDirectory(full); }
                catch (Exception ex)
                {
                    if (!(ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)) throw;
                    result.Error = "无法新建文件夹：" + ex.Message;
                }
                return result;
            }

            string name = Path.GetFileName(full);
            if (Directory.Exists(full) || name.Length == 0)
            {
                result.Error = mode == PathPickerMode.OpenFile ? "这是文件夹，请展开后选中其中的文件。" : "请在路径末尾写上文件名。";
                return result;
            }
            if (mode == PathPickerMode.OpenFile)
            {
                result.Path = full;
                if (!HasExtension(name, extensions)) result.Error = "只能选择 " + DescribeExtensions(extensions) + " 文件。";
                else if (!File.Exists(full)) result.Error = "文件不存在或当前不可访问。";
                return result;
            }

            if (!HasExtension(name, extensions) && extensions != null && extensions.Length > 0) full += extensions[0];
            result.Path = full;
            string folder = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                result.Error = "保存位置的文件夹不存在：" + folder;
                return result;
            }
            if (Directory.Exists(full)) { result.Error = "已有同名的文件夹，请换个文件名。"; return result; }
            if (!File.Exists(full)) return result;
            if ((File.GetAttributes(full) & FileAttributes.ReadOnly) != 0)
            {
                result.Error = "同名文件是只读的，不能替换，请换个文件名。";
                return result;
            }
            if (!string.Equals(confirmedOverwrite, full, StringComparison.OrdinalIgnoreCase)) result.AskOverwrite = true;
            return result;
        }

        // ===== 行绘制 =====

        private void MeasureRow(object sender, MeasureItemEventArgs e)
        {
            Row row = RowAt(e.Index);
            int height = row == null || row.Kind == RowKind.Header ? HeaderHeight
                : row.Kind == RowKind.Note ? NoteHeight
                : row.Depth > 0 ? ChildRowHeight : RowHeight;
            e.ItemHeight = Dpi(height);
        }

        /// <summary>输入框里的路径正好是这一行：画成选中。</summary>
        private bool IsChosen(Row row)
        {
            if (row.Path == null) return false;
            string text = pathBox.Text.Trim().Trim('"');
            if (text.Length == 0) return false;
            string target = options.Mode == PathPickerMode.SaveFile && row.Kind != RowKind.File ? SafeDirectory(text) : text;
            string a = LocalScanScope.NormalizePath(target), b = LocalScanScope.NormalizePath(row.Path);
            return a != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static string SafeDirectory(string path)
        {
            try { return Path.GetDirectoryName(path); }
            catch (ArgumentException) { return null; }
            catch (PathTooLongException) { return null; }
        }

        private void DrawRow(object sender, DrawItemEventArgs e)
        {
            Row item = RowAt(e.Index);
            Rectangle row = e.Bounds;
            if (item == null || row.Width <= 0 || row.Height <= 0) return;
            // 整行先画到缓冲再贴上去：悬停、选中的重绘都不闪
            using (BufferedGraphics buffer = BufferedGraphicsManager.Current.Allocate(e.Graphics, row))
            {
                Graphics g = buffer.Graphics;
                // 底色先按整像素铺满，再开抗锯齿：否则行的上、左边缘会半透明地透出缓冲区的黑底
                using (SolidBrush back = new SolidBrush(Theme.Card)) g.FillRectangle(back, row);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                if (item.Kind == RowKind.Header)
                {
                    using (SolidBrush brush = new SolidBrush(Theme.TextSecondary))
                        Draw.DrawStringLeft(g, item.Title, Theme.FontOf(9F, FontStyle.Bold), brush,
                            new Rectangle(row.X + Dpi(12), row.Y + Dpi(6), row.Width - Dpi(24), row.Height - Dpi(6)));
                }
                else if (item.Kind == RowKind.Note)
                {
                    int left = item.Depth == 0 ? Dpi(12) : IconLeft(item);
                    using (SolidBrush brush = new SolidBrush(item.Warning ? Theme.DeleteRed : Theme.TextTertiary))
                        Draw.DrawStringLeft(g, item.Title, Theme.CaptionFont, brush,
                            new Rectangle(row.X + left, row.Y, Math.Max(0, row.Width - left - Dpi(12)), row.Height));
                }
                else DrawPathRow(g, row, item, e.Index);
                buffer.Render(e.Graphics);
            }
        }

        private void DrawPathRow(Graphics g, Rectangle row, Row item, int index)
        {
            bool chosen = IsChosen(item);
            bool active = item.Enabled && (index == list.HoverIndex || (list.KeyboardActive && index == list.SelectedIndex));
            if (chosen || active)
            {
                Rectangle highlight = new Rectangle(row.X + Dpi(2), row.Y + 1, row.Width - Dpi(4), row.Height - 2);
                using (SolidBrush b = new SolidBrush(chosen ? Theme.SelectionBg : Theme.RowHover))
                    Draw.FillRoundedRect(g, b, highlight, Dpi(6));
            }

            int iconSize = Dpi(IconSize);
            Rectangle icon = new Rectangle(row.X + IconLeft(item), row.Y + (row.Height - iconSize) / 2, iconSize, iconSize);
            if (item.Expandable)
                DrawChevron(g, new Rectangle(icon.X - Dpi(ChevronWidth + 2), row.Y, Dpi(ChevronWidth), row.Height), item.Expanded);
            string glyph = item.Kind == RowKind.File ? GlyphFile : item.Glyph ?? (item.Kind == RowKind.Drive ? GlyphDrive : GlyphFolder);
            Color iconColor = !item.Enabled ? Theme.TextTertiary : chosen ? Theme.Accent
                : item.Kind == RowKind.File ? Theme.TextSecondary : Theme.FolderTint;
            Draw.DrawGlyph(g, glyph, icon, iconColor);

            int textX = icon.Right + Dpi(10);
            int textWidth = Math.Max(0, row.Right - textX - Dpi(10));
            using (SolidBrush titleBrush = new SolidBrush(!item.Enabled ? Theme.TextTertiary : chosen ? Theme.AccentText : Theme.TextPrimary))
            {
                Rectangle title = item.Depth > 0 ? new Rectangle(textX, row.Y, textWidth, row.Height)
                    : new Rectangle(textX, row.Y + Dpi(4), textWidth, Dpi(21));
                Draw.DrawStringLeft(g, item.Title, Theme.BodyFont, titleBrush, title);
            }
            if (item.Depth == 0)
                using (SolidBrush detailBrush = new SolidBrush(Theme.TextTertiary))
                    Draw.DrawStringLeft(g, item.Detail ?? string.Empty, Theme.CaptionFont, detailBrush,
                        new Rectangle(textX, row.Y + Dpi(24), textWidth, Dpi(18)));
        }

        /// <summary>展开箭头：收起时朝右，展开后朝下。</summary>
        private void DrawChevron(Graphics g, Rectangle box, bool isExpanded)
        {
            using (Pen pen = new Pen(Theme.TextSecondary, Math.Max(1.4F, Dpi(3) / 2F)))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                float cx = box.X + box.Width / 2F, cy = box.Y + box.Height / 2F, arm = Dpi(4), half = arm / 2F;
                PointF[] points = isExpanded
                    ? new PointF[] { new PointF(cx - arm, cy - half), new PointF(cx, cy + half), new PointF(cx + arm, cy - half) }
                    : new PointF[] { new PointF(cx - half, cy - arm), new PointF(cx + half, cy), new PointF(cx - half, cy + arm) };
                g.DrawLines(pen, points);
            }
        }
    }
}

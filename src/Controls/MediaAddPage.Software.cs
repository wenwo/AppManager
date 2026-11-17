using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using AppManager;

namespace AppManager.Controls
{
    /// <summary>
    /// 添加页的「添加软件」部分：左栏「已安装的软件」列出可加入的已安装软件（带搜索和「显示系统工具」开关），
    /// 与在各位置里翻到的 .exe 一起勾选。图标在后台提取，位图归本页所有，关闭时统一释放（不动主列表的图标缓存）。
    /// </summary>
    internal sealed partial class MediaAddPage
    {
        private const string GlyphApps = "";
        private const int AppIconPx = 24;
        private const int PreviewIconPx = 64;

        private List<AppInfo> installed = new List<AppInfo>();
        private readonly List<AppInfo> checkedApps = new List<AppInfo>();
        private readonly HashSet<AppInfo> checkedAppSet = new HashSet<AppInfo>();
        private readonly List<AppInfo> listedApps = new List<AppInfo>();   // 中栏列出的软件（全选按它）
        private bool showingApps;                    // 中栏显示的是已安装的软件
        private SearchField appSearch;
        private ToggleSwitch systemToggle;
        private Rectangle systemLabelRect;
        private readonly Dictionary<AppInfo, Bitmap> appIcons = new Dictionary<AppInfo, Bitmap>();
        private int iconGen;
        private bool iconsStarted;
        private AppInfo previewApp;                  // 右栏正在预览的已安装软件

        private int TotalChecked { get { return checkedFiles.Count + checkedApps.Count; } }

        /// <summary>勾选的已安装软件（按勾选先后）；宿主在 Confirmed 里读取。</summary>
        internal List<AppInfo> CheckedApps { get { return new List<AppInfo>(checkedApps); } }
        internal List<AppInfo> ListedApps { get { return new List<AppInfo>(listedApps); } }
        internal bool ShowingInstalled { get { return showingApps; } }
        internal int AppIconCount { get { return appIcons.Count; } }
        internal AppInfo PreviewApp { get { return previewApp; } }

        private bool SearchFocused { get { return appSearch != null && appSearch.ContainsFocus; } }

        private void InitSoftware(List<AppInfo> candidates, bool showSystem)
        {
            installed = candidates ?? new List<AppInfo>();
            appSearch = new SearchField();
            appSearch.Placeholder = "搜索已安装的软件…";
            appSearch.TextChanged += delegate { if (showingApps) RefreshApps(); };
            Controls.Add(appSearch);
            systemToggle = new ToggleSwitch();
            systemToggle.Checked = showSystem;
            systemToggle.AccessibleName = "显示系统工具";
            systemToggle.CheckedChanged += delegate
            {
                // 关掉开关表示不要系统工具：已勾选的系统工具一并取消，避免把列表里看不见的条目加进分类
                if (!systemToggle.Checked)
                {
                    foreach (AppInfo app in new List<AppInfo>(checkedApps))
                        if (Scanner.IsSystemComponent(app)) RemoveCheckedApp(app);
                    CheckedChanged();
                }
                if (showingApps) RefreshApps();
            };
            Controls.Add(systemToggle);
        }

        private string InstalledTitle()
        {
            return "已安装的软件（" + installed.Count + "）";
        }

        /// <summary>中栏改为列出已安装的软件（受搜索和「显示系统工具」影响），第一次进入时开始提取图标。</summary>
        internal void ShowInstalled()
        {
            if (kind != MediaAddKind.Software) return;
            loadGen++;   // 还在读的文件夹作废
            loading = false;
            showingChecked = false;
            showingApps = true;
            pendingSelect = null;
            SyncHeaderControls();
            RefreshApps();
            StartIconLoad();
            placeList.Invalidate();
        }

        private void RefreshApps()
        {
            string keyword = appSearch.Text;
            bool includeSystem = systemToggle.Checked;
            List<AppInfo> visible = SoftwarePickerDialog.Filter(installed, keyword, includeSystem);
            listedFiles.Clear();
            listedApps.Clear();
            listedApps.AddRange(visible);
            List<FileRow> rows = new List<FileRow>();
            foreach (AppInfo app in visible) rows.Add(AppRow(app));
            bool searching = (keyword ?? string.Empty).Trim().Length > 0;
            // 关着「显示系统工具」时，说明还有多少项没列出来，避免以为软件丢了
            int hidden = includeSystem ? 0 : SoftwarePickerDialog.Filter(installed, keyword, true).Count - visible.Count;
            string empty = hidden > 0 ? "没有" + (searching ? "匹配的" : "可添加的") + "已安装软件；打开「显示系统工具」还有 " + hidden + " 项。"
                : installed.Count == 0 ? "没有可添加的已安装软件。便携程序可以从左侧的位置里找 .exe。"
                : "没有匹配的软件。便携程序可以从左侧的位置里找 .exe。";
            SetBrowseItems(null, rows, empty);
            folderSummary = (searching ? "找到 " : "共 ") + visible.Count + " 个" + (hidden > 0 ? " · 另有 " + hidden + " 个系统工具未显示" : string.Empty);
            RefreshState();
        }

        private FileRow AppRow(AppInfo app)
        {
            return new FileRow { Kind = FileKind.App, App = app, Name = app.Name ?? string.Empty, Detail = SoftwarePickerDialog.Describe(app) };
        }

        /// <summary>切换一个已安装软件的勾选；超过上限时提示，不勾上。检查期间不改动勾选。</summary>
        internal void ToggleApp(AppInfo app)
        {
            if (checking || app == null) return;
            if (checkedAppSet.Contains(app)) RemoveCheckedApp(app);
            else if (TotalChecked >= PathPickerDialog.MaxCheckedFiles)
            {
                SetNotice("一次最多选 " + PathPickerDialog.MaxCheckedFiles + " 个软件。", true);
                return;
            }
            else AddCheckedApp(app);
            notice = null;
            CheckedChanged();
        }

        private void AddCheckedApp(AppInfo app)
        {
            if (checkedAppSet.Add(app)) checkedApps.Add(app);
        }

        private void RemoveCheckedApp(AppInfo app)
        {
            if (checkedAppSet.Remove(app)) checkedApps.Remove(app);
        }

        /// <summary>中栏顶部按视图切换：列已安装的软件时是搜索框和「显示系统工具」开关，否则是上一级按钮和当前文件夹。</summary>
        private void SyncHeaderControls()
        {
            upButton.Visible = !showingApps;
            if (appSearch != null)
            {
                appSearch.Visible = showingApps;
                systemToggle.Visible = showingApps;
            }
            LayoutSoftwareHeader();
            Invalidate(BrowseHeaderRect());
        }

        private void LayoutSoftwareHeader()
        {
            if (appSearch == null || browseFrame.Width <= 0) return;
            int toggleW = Dpi(40);
            Rectangle toggle = new Rectangle(allButton.Left - Dpi(12) - toggleW, browseFrame.Y + Dpi(10), toggleW, Dpi(24));
            systemToggle.Bounds = toggle;
            int labelW = Dpi(86);
            systemLabelRect = new Rectangle(toggle.X - Dpi(6) - labelW, browseFrame.Y + Dpi(8), labelW, Dpi(28));
            int left = browseFrame.X + Dpi(8);
            int searchW = Math.Max(Dpi(120), Math.Min(Dpi(260), systemLabelRect.X - Dpi(16) - left));
            appSearch.Bounds = new Rectangle(left, browseFrame.Y + Dpi(6), searchW, Dpi(32));
        }

        /// <summary>列已安装的软件时的中栏顶部：搜索框右侧是数量，开关左侧是「显示系统工具」，下方一条分隔线。</summary>
        private void PaintInstalledHeader(Graphics g)
        {
            Rectangle browse = browseFrame;
            int summaryX = appSearch.Right + Dpi(10);
            Rectangle summary = new Rectangle(summaryX, browse.Y + Dpi(8), Math.Max(0, systemLabelRect.X - Dpi(8) - summaryX), Dpi(28));
            if (summary.Width > 0 && !string.IsNullOrEmpty(folderSummary))
                TextRenderer.DrawText(g, folderSummary, Theme.CaptionFont, summary, Theme.TextSecondary,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, "显示系统工具", Theme.CaptionFont, systemLabelRect, Theme.TextSecondary,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            using (Pen line = new Pen(Theme.Separator, 1))
                g.DrawLine(line, browse.X + 1, browse.Y + Dpi(43), browse.Right - 2, browse.Y + Dpi(43));
        }

        /// <summary>点「显示系统工具」几个字也能切换开关。</summary>
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && showingApps && systemToggle != null && systemLabelRect.Contains(e.Location))
                systemToggle.Checked = !systemToggle.Checked;
        }

        /// <summary>在搜索框里回车：焦点交给列表，用方向键和空格继续勾选。</summary>
        private void FocusBrowseList()
        {
            fileList.Focus();
            if (fileList.SelectedIndex < 0 && fileList.Items.Count > 0) fileList.SelectedIndex = 0;
        }

        // ===== 图标 =====

        /// <summary>后台逐个提取已安装软件的图标：先取当前列出的，再取被筛掉的。页面关闭后停止，迟到的位图直接释放。</summary>
        private void StartIconLoad()
        {
            if (iconsStarted || installed.Count == 0) return;
            iconsStarted = true;
            int size = Dpi(AppIconPx);
            int gen = iconGen;
            List<AppInfo> pending = new List<AppInfo>();
            HashSet<AppInfo> queued = new HashSet<AppInfo>();
            foreach (AppInfo app in listedApps) if (queued.Add(app)) pending.Add(app);
            foreach (AppInfo app in installed) if (app != null && queued.Add(app)) pending.Add(app);
            ThreadPool.QueueUserWorkItem(delegate
            {
                foreach (AppInfo app in pending)
                {
                    if (gen != Volatile.Read(ref iconGen)) return;
                    Bitmap bmp;
                    try { bmp = Icons.Extract(app, size); }
                    catch (Exception ex)
                    {
                        // 取不到图标时这一行保留占位块，不影响勾选添加
                        Debug.WriteLine("提取软件图标失败 " + app.Name + ": " + ex.Message);
                        bmp = null;
                    }
                    if (bmp == null) continue;
                    Bitmap icon = bmp;
                    AppInfo owner = app;
                    Post(delegate
                    {
                        if (gen != iconGen) { icon.Dispose(); return; }
                        Bitmap old;
                        if (appIcons.TryGetValue(owner, out old) && old != null) old.Dispose();
                        appIcons[owner] = icon;
                        InvalidateAppRow(owner);
                    }, delegate { icon.Dispose(); });
                }
            });
        }

        private void InvalidateAppRow(AppInfo app)
        {
            for (int i = 0; i < fileList.Items.Count; i++)
            {
                FileRow row = fileList.Items[i] as FileRow;
                if (row != null && ReferenceEquals(row.App, app)) { fileList.InvalidateRow(i); return; }
            }
        }

        private void DrawAppIcon(Graphics g, AppInfo app, Rectangle r)
        {
            Bitmap icon;
            if (app != null && appIcons.TryGetValue(app, out icon) && icon != null)
            {
                InterpolationMode old = g.InterpolationMode;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(icon, r);
                g.InterpolationMode = old;
            }
            else
                using (SolidBrush placeholder = new SolidBrush(Theme.ChipBg))
                    Draw.FillRoundedRect(g, placeholder, r, Dpi(6));
        }

        // ===== 预览 =====

        /// <summary>预览一个已安装的软件：后台取大图标；程序文件在本机时再读版本信息。</summary>
        internal void StartAppPreview(AppInfo app)
        {
            if (app == null) return;
            if (ReferenceEquals(previewApp, app)) { RefreshState(); return; }
            ClearPreview();
            previewApp = app;
            previewIsIcon = true;
            previewLoading = true;
            int gen = previewGen;
            int size = Dpi(PreviewIconPx);
            string category = string.IsNullOrEmpty(app.Category) ? "未分类" : app.Category;
            string where = SoftwarePickerDialog.Where(app);
            string target = app.TargetPath;
            ThreadPool.QueueUserWorkItem(delegate
            {
                Bitmap bmp = null;
                List<KeyValuePair<string, string>> info = new List<KeyValuePair<string, string>>();
                info.Add(new KeyValuePair<string, string>("所在分类", category));
                try
                {
                    bmp = Icons.Extract(app, size);
                    if (!string.IsNullOrEmpty(target) && PathPickerDialog.HasExtension(target, new string[] { ".exe" }) && File.Exists(target))
                        AddVersionInfo(info, target);
                }
                catch (Exception ex)
                {
                    // 线程池上不能抛出：只是少显示图标或版本，原因记进日志
                    Paths.LogError(ex);
                }
                if (!string.IsNullOrEmpty(where)) info.Add(new KeyValuePair<string, string>("位置", where));
                Bitmap result = bmp;
                Post(delegate
                {
                    if (gen != previewGen) { if (result != null) result.Dispose(); return; }
                    previewLoading = false;
                    previewImage = result;
                    previewInfo.Clear();
                    previewInfo.AddRange(info);
                    Invalidate(previewFrame);
                    RefreshState();
                }, delegate { if (result != null) result.Dispose(); });
            });
            Invalidate(previewFrame);
            RefreshState();
        }

        /// <summary>程序文件的图标（只在后台线程调用）；取不到返回 null，原因记进日志，右栏改显示类型图标。</summary>
        private static Bitmap ExtractFileIcon(string path, int size)
        {
            try { return Icons.Extract(new AppInfo { Name = Path.GetFileNameWithoutExtension(path), TargetPath = path }, size); }
            catch (Exception ex)
            {
                Paths.LogError(ex);
                return null;
            }
        }

        /// <summary>读程序文件的产品名、版本和公司（只在后台线程调用）；读不到的项不显示。</summary>
        private static void AddVersionInfo(List<KeyValuePair<string, string>> info, string path)
        {
            FileVersionInfo version;
            try { version = FileVersionInfo.GetVersionInfo(path); }
            catch (FileNotFoundException) { return; }
            catch (ArgumentException) { return; }
            if (!string.IsNullOrEmpty(version.ProductName)) info.Add(new KeyValuePair<string, string>("产品", version.ProductName.Trim()));
            string number = !string.IsNullOrEmpty(version.ProductVersion) ? version.ProductVersion : version.FileVersion;
            if (!string.IsNullOrEmpty(number)) info.Add(new KeyValuePair<string, string>("版本", number.Trim()));
            if (!string.IsNullOrEmpty(version.CompanyName)) info.Add(new KeyValuePair<string, string>("公司", version.CompanyName.Trim()));
        }

        private void DisposeSoftware()
        {
            iconGen++;   // 后台提取停止，迟到的位图由 Post 的丢弃回调释放
            foreach (Bitmap bmp in appIcons.Values) if (bmp != null) bmp.Dispose();
            appIcons.Clear();
        }
    }
}

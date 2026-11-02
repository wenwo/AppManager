using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AppManager.Controls
{
    /// <summary>
    /// 应用内压缩包查看器：只读列出压缩包内的文件夹与文件（不解压）。
    /// 双击或回车进入文件夹，退格 / Alt+↑ / 鼠标后退键返回上一级；顶部路径可点击跳转；点列头排序。
    /// </summary>
    public class ArchiveViewerControl : Control
    {
        public event Action CloseRequested;

        private const int MaxIconTypes = 512;      // 扩展名种类过多（构造的压缩包）时其余共用通用图标
        private const int MaxTypeNames = 4096;

        private readonly ListView list;
        private readonly ImageList icons;
        private readonly PreviewLoader<ArchivePreview> loader;
        private readonly RoundButton externalButton;
        private readonly Timer hintTimer;
        private readonly List<string> paths = new List<string>();
        private readonly List<ArchiveEntryInfo> rows = new List<ArchiveEntryInfo>();   // null 为「上一级」行
        private readonly List<Crumb> crumbs = new List<Crumb>();
        private readonly Dictionary<string, int> iconIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> typeNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private int index;
        private ArchivePreview current;
        private ArchiveEntryInfo folder;
        private bool loading, loadFailed;
        private string failureText = "无法读取此压缩包";
        private string hint;
        private int sortColumn;
        private bool sortDescending;
        private int hoverCrumb = -1;
        private Rectangle backRect, prevRect, nextRect, infoBarRect;

        private sealed class Crumb
        {
            public string Text;
            public Rectangle Bounds;
            public ArchiveEntryInfo Target;   // null：不可点击（当前目录或省略号）
        }

        private int Dpi(int value)
        {
            return DpiHelper.Scale(value, DpiHelper.ForControl(this));
        }

        private int TopBarH { get { return Dpi(44); } }
        private int InfoBarH { get { return Dpi(44); } }
        private int ChromePad { get { return Dpi(16); } }
        private int CardGap { get { return Dpi(12); } }
        private int PillH { get { return Dpi(40); } }
        private int PillBtnW { get { return Dpi(44); } }

        private Rectangle ListArea
        {
            get
            {
                int bottom = Height - InfoBarH - CardGap - PillH - CardGap;
                return new Rectangle(ChromePad, TopBarH, Math.Max(0, Width - ChromePad * 2), Math.Max(0, bottom - TopBarH));
            }
        }

        public ArchiveViewerControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Theme.Background;
            TabStop = true;
            Visible = false;
            loader = new PreviewLoader<ArchivePreview>(this);

            externalButton = new RoundButton { Text = "外部打开", Accent = false };
            externalButton.Click += delegate
            {
                if (paths.Count == 0) return;
                if (!Launcher.OpenWith(paths[index]))
                    MessageBox.Show(this, "无法启动 Windows 打开方式选择器。", "外部打开", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };
            Controls.Add(externalButton);

            icons = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(16, 16) };
            list = new ListView
            {
                View = View.Details,
                VirtualMode = true,
                FullRowSelect = true,
                HideSelection = false,
                MultiSelect = false,
                BorderStyle = BorderStyle.None,
                HeaderStyle = ColumnHeaderStyle.Clickable,
                BackColor = Color.White,
                ForeColor = Theme.TextPrimary,
                Font = Theme.BodyFont,
                SmallImageList = icons,
                Visible = false
            };
            list.Columns.Add("名称");
            list.Columns.Add("大小", 90, HorizontalAlignment.Right);
            list.Columns.Add("类型");
            list.Columns.Add("修改时间");
            list.RetrieveVirtualItem += OnRetrieveItem;
            list.ItemActivate += delegate { ActivateSelected(); };
            list.ColumnClick += delegate(object sender, ColumnClickEventArgs e) { SortBy(e.Column); };
            list.MouseUp += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.XButton1) GoUp(); };
            list.HandleCreated += delegate
            {
                SetWindowTheme(list.Handle, "Explorer", null);   // 与资源管理器一致的悬停/选中样式
                UpdateSortArrows();
            };
            Controls.Add(list);

            hintTimer = new Timer { Interval = 3000 };
            hintTimer.Tick += delegate { hintTimer.Stop(); hint = null; Invalidate(infoBarRect); };
        }

        public void Open(List<string> archivePaths, int startIndex)
        {
            paths.Clear();
            if (archivePaths != null) paths.AddRange(archivePaths);
            if (paths.Count == 0) { ClosePreview(); RequestClose(); return; }
            index = Math.Min(Math.Max(startIndex, 0), paths.Count - 1);
            Visible = true;
            Focus();
            LoadCurrent();
        }

        public void ClosePreview()
        {
            loader.Clear();
            hintTimer.Stop();
            hint = null;
            ResetContent();
            loading = false;
            paths.Clear();
            Visible = false;
        }

        private void RequestClose()
        {
            Action handler = CloseRequested;
            if (handler != null) handler();
        }

        private void ResetContent()
        {
            current = null;
            folder = null;
            rows.Clear();
            list.SelectedIndices.Clear();
            list.VirtualListSize = 0;
            list.Visible = false;
            crumbs.Clear();
            hoverCrumb = -1;
        }

        private void LoadCurrent()
        {
            loader.Clear();
            ResetContent();
            hintTimer.Stop();
            hint = null;
            loadFailed = false;
            loading = true;
            failureText = "无法读取此压缩包";
            string path = paths[index];
            LayoutChrome();
            loader.Load(delegate(System.Threading.CancellationToken cancellation)
            {
                ArchivePreview preview = ArchivePreview.Read(path, cancellation);
                // 默认按名称排序在后台完成，进入文件夹时无需再排
                SortTree(preview.Root, cancellation);
                return preview;
            }, delegate(ArchivePreview preview, Exception error)
            {
                loading = false;
                if (error != null)
                {
                    failureText = "无法读取此压缩包：" + error.Message;
                    loadFailed = true;
                    LayoutChrome();
                }
                else
                {
                    current = preview;
                    EnsureIconSize();
                    ShowFolder(preview.Root, null);
                }
                Invalidate();
            });
            Invalidate();
        }

        // ===== 目录浏览 =====

        private void ShowFolder(ArchiveEntryInfo target, ArchiveEntryInfo select)
        {
            if (current == null || target == null) return;
            folder = target;
            rows.Clear();
            if (folder.Parent != null) rows.Add(null);
            rows.AddRange(folder.Children);
            if (sortColumn != 0 || sortDescending) SortRows();
            list.SelectedIndices.Clear();
            list.VirtualListSize = rows.Count;
            list.Visible = rows.Count > 0;
            if (rows.Count > 0)
            {
                // 默认选中第一个实际条目（而不是「上一级」），回车即可进入
                int selected = select != null ? rows.IndexOf(select) : -1;
                if (selected < 0) selected = rows[0] == null && rows.Count > 1 ? 1 : 0;
                list.SelectedIndices.Add(selected);
                list.Items[selected].Focused = true;
                list.EnsureVisible(selected);
                list.Invalidate();
                list.Focus();
            }
            LayoutChrome();
            Invalidate();
        }

        private void GoUp()
        {
            if (folder == null || folder.Parent == null) return;
            ShowFolder(folder.Parent, folder);
        }

        private void ActivateSelected()
        {
            if (list.SelectedIndices.Count == 0) return;
            int i = list.SelectedIndices[0];
            if (i < 0 || i >= rows.Count) return;
            ArchiveEntryInfo entry = rows[i];
            if (entry == null) GoUp();
            else if (entry.IsFolder) ShowFolder(entry, null);
            else ShowHint("暂不支持直接打开压缩包内的文件，可点「外部打开」用其它程序查看");
        }

        private void ShowHint(string text)
        {
            hint = text;
            hintTimer.Stop();
            hintTimer.Start();
            Invalidate(infoBarRect);
        }

        private void Step(int delta)
        {
            if (paths.Count < 2) return;
            index = (index + delta + paths.Count) % paths.Count;
            LoadCurrent();
        }

        // ===== 列表项 =====

        private void OnRetrieveItem(object sender, RetrieveVirtualItemEventArgs e)
        {
            ArchiveEntryInfo entry = e.ItemIndex >= 0 && e.ItemIndex < rows.Count ? rows[e.ItemIndex] : null;
            ListViewItem item;
            if (entry == null)
            {
                item = new ListViewItem("..", IconFor(null));
                item.SubItems.Add(string.Empty);
                item.SubItems.Add("上一级");
                item.SubItems.Add(string.Empty);
            }
            else
            {
                item = new ListViewItem(entry.Name, IconFor(entry));
                item.SubItems.Add(SizeText(entry));
                item.SubItems.Add(entry.Encrypted ? TypeText(entry) + " · 已加密" : TypeText(entry));
                item.SubItems.Add(entry.Modified == DateTime.MinValue ? string.Empty : entry.Modified.ToString("yyyy-MM-dd HH:mm"));
            }
            e.Item = item;
        }

        private static string SizeText(ArchiveEntryInfo entry)
        {
            if (entry.IsFolder) return entry.FileCount > 0 ? HumanSize(entry.Size) : string.Empty;
            return entry.Size < 0 ? "—" : HumanSize(entry.Size);
        }

        internal static string HumanSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            double kb = bytes / 1024.0;
            if (kb < 1024) return kb.ToString("0.#") + " KB";
            double mb = kb / 1024.0;
            if (mb < 1024) return mb.ToString("0.#") + " MB";
            return (mb / 1024.0).ToString("0.##") + " GB";
        }

        private static string Extension(ArchiveEntryInfo entry)
        {
            string ext = Path.GetExtension(entry.Name);
            return ext.Length > 1 && ext.Length <= 16 ? ext.ToLowerInvariant() : string.Empty;
        }

        private string TypeText(ArchiveEntryInfo entry)
        {
            if (entry.IsFolder) return "文件夹";
            string ext = Extension(entry);
            string name;
            if (typeNames.TryGetValue(ext, out name)) return name;
            name = typeNames.Count < MaxTypeNames ? Icons.ExtensionTypeName(ext, false) : null;
            if (string.IsNullOrEmpty(name)) name = ext.Length > 0 ? ext.Substring(1).ToUpperInvariant() + " 文件" : "文件";
            typeNames[ext] = name;
            return name;
        }

        private int IconFor(ArchiveEntryInfo entry)
        {
            bool isFolder = entry == null || entry.IsFolder;
            string key = isFolder ? "<folder>" : Extension(entry);
            int found;
            if (iconIndex.TryGetValue(key, out found)) return found;
            if (!isFolder && key.Length > 0 && iconIndex.Count >= MaxIconTypes)
            {
                key = string.Empty;   // 种类过多：共用无扩展名的通用文件图标
                if (iconIndex.TryGetValue(key, out found)) return found;
            }
            int result = -1;
            using (Bitmap bitmap = Icons.ExtensionIcon(key, isFolder, true))
            {
                if (bitmap != null)
                {
                    // 句柄已建时 Add 会把位图复制进系统图标表，随后即可释放原位图
                    IntPtr handle = icons.Handle;
                    icons.Images.Add(bitmap);
                    result = icons.Images.Count - 1;
                }
            }
            iconIndex[key] = result;
            return result;
        }

        /// <summary>图标随 DPI 缩放：尺寸变化时重建图标表，列表重新取图。</summary>
        private void EnsureIconSize()
        {
            int size = Dpi(16);
            if (icons.ImageSize.Width == size) return;
            icons.Images.Clear();
            icons.ImageSize = new Size(size, size);
            iconIndex.Clear();
            list.Invalidate();
        }

        // ===== 排序 =====

        private static int CompareByName(ArchiveEntryInfo a, ArchiveEntryInfo b)
        {
            if (a.IsFolder != b.IsFolder) return a.IsFolder ? -1 : 1;
            int r = StrCmpLogicalW(a.Name, b.Name);
            return r != 0 ? r : string.CompareOrdinal(a.Name, b.Name);
        }

        private static void SortTree(ArchiveEntryInfo root, System.Threading.CancellationToken cancellation)
        {
            Stack<ArchiveEntryInfo> stack = new Stack<ArchiveEntryInfo>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                ArchiveEntryInfo node = stack.Pop();
                node.Children.Sort(CompareByName);
                foreach (ArchiveEntryInfo child in node.Children)
                    if (child.IsFolder && child.Children.Count > 0) stack.Push(child);
            }
        }

        private int CompareRows(ArchiveEntryInfo a, ArchiveEntryInfo b)
        {
            // 文件夹始终在前，与资源管理器一致；倒序只作用于同类之间
            if (a.IsFolder != b.IsFolder) return a.IsFolder ? -1 : 1;
            int r = 0;
            if (sortColumn == 1) r = a.Size.CompareTo(b.Size);
            else if (sortColumn == 2) r = string.Compare(TypeText(a), TypeText(b), StringComparison.CurrentCulture);
            else if (sortColumn == 3) r = a.Modified.CompareTo(b.Modified);
            if (r == 0) r = CompareByName(a, b);
            return sortDescending ? -r : r;
        }

        private void SortRows()
        {
            int start = rows.Count > 0 && rows[0] == null ? 1 : 0;
            if (rows.Count - start > 1)
                rows.Sort(start, rows.Count - start, Comparer<ArchiveEntryInfo>.Create(CompareRows));
        }

        private void SortBy(int column)
        {
            if (column == sortColumn) sortDescending = !sortDescending;
            else { sortColumn = column; sortDescending = column == 1 || column == 3; }   // 大小、时间默认从大到小
            UpdateSortArrows();
            if (current == null || folder == null) return;
            int old = list.SelectedIndices.Count > 0 ? list.SelectedIndices[0] : -1;
            ArchiveEntryInfo selected = old >= 0 && old < rows.Count ? rows[old] : null;   // null 也可能是「上一级」行
            if (sortColumn == 0 && !sortDescending)
            {
                rows.Clear();
                if (folder.Parent != null) rows.Add(null);
                rows.AddRange(folder.Children);   // 已按名称排好
            }
            else SortRows();
            list.SelectedIndices.Clear();
            int i = old < 0 ? -1 : selected != null ? rows.IndexOf(selected) : 0;   // 「上一级」行排序后仍在首行
            if (i >= 0) { list.SelectedIndices.Add(i); list.Items[i].Focused = true; list.EnsureVisible(i); }
            list.Invalidate();
        }

        private void UpdateSortArrows()
        {
            if (!list.IsHandleCreated) return;
            IntPtr header = SendMessage(list.Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
            if (header == IntPtr.Zero) return;
            for (int i = 0; i < list.Columns.Count; i++)
            {
                HDITEM item = new HDITEM { mask = HDI_FORMAT };
                if (SendMessage(header, HDM_GETITEMW, (IntPtr)i, ref item) == IntPtr.Zero) continue;
                item.fmt &= ~(HDF_SORTUP | HDF_SORTDOWN);
                if (i == sortColumn) item.fmt |= sortDescending ? HDF_SORTDOWN : HDF_SORTUP;
                SendMessage(header, HDM_SETITEMW, (IntPtr)i, ref item);
            }
        }

        // ===== 布局与绘制 =====

        private void LayoutChrome()
        {
            int buttonY = Dpi(6), buttonH = Dpi(30);
            externalButton.Bounds = new Rectangle(Math.Max(0, Width - Dpi(112)), buttonY, Dpi(96), buttonH);
            backRect = new Rectangle(ChromePad, 6, Dpi(76), TopBarH - 12);
            int infoTop = Height - InfoBarH;
            infoBarRect = new Rectangle(0, infoTop, Width, InfoBarH);
            int pillW = PillBtnW * 2;
            int pillTop = infoTop - CardGap - PillH;
            prevRect = new Rectangle((Width - pillW) / 2, pillTop, PillBtnW, PillH);
            nextRect = new Rectangle(prevRect.Right, pillTop, PillBtnW, PillH);
            Rectangle area = ListArea;
            list.Bounds = new Rectangle(area.X + 1, area.Y + 1, Math.Max(0, area.Width - 2), Math.Max(0, area.Height - 2));
            LayoutColumns();
            LayoutCrumbs();
        }

        /// <summary>名称列占满剩余宽度，其余列按 DPI 固定宽度。</summary>
        private void LayoutColumns()
        {
            if (list.Columns.Count < 4) return;
            int size = Dpi(90), type = Dpi(150), time = Dpi(140);
            int name = Math.Max(Dpi(160), list.ClientSize.Width - size - type - time - SystemInformation.VerticalScrollBarWidth);
            if (list.Columns[0].Width != name) list.Columns[0].Width = name;
            if (list.Columns[1].Width != size) list.Columns[1].Width = size;
            if (list.Columns[2].Width != type) list.Columns[2].Width = type;
            if (list.Columns[3].Width != time) list.Columns[3].Width = time;
        }

        /// <summary>顶部路径：压缩包名 › 文件夹 › …，放不下时从左侧省略。</summary>
        private void LayoutCrumbs()
        {
            crumbs.Clear();
            if (paths.Count == 0) return;
            List<Crumb> chain = new List<Crumb>();
            for (ArchiveEntryInfo node = folder; node != null; node = node.Parent)
                chain.Insert(0, new Crumb { Text = node.Parent == null ? Path.GetFileName(paths[index]) : node.Name, Target = node });
            if (chain.Count == 0) chain.Add(new Crumb { Text = Path.GetFileName(paths[index]) });
            chain[chain.Count - 1].Target = null;   // 当前目录不可点击

            Font font = Theme.ButtonFont;
            TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
            int left = backRect.Right + Dpi(12);
            int right = externalButton.Left - Dpi(12);
            int sep = SeparatorWidth();
            int ellipsis = TextRenderer.MeasureText("…", font, Size.Empty, flags).Width + Dpi(4);
            int[] widths = new int[chain.Count];
            for (int i = 0; i < chain.Count; i++)
                widths[i] = TextRenderer.MeasureText(chain[i].Text, font, Size.Empty, flags).Width + Dpi(4);

            // 从当前目录向左累计，能放下多少段就放多少段
            int available = Math.Max(0, right - left);
            int first = chain.Count - 1, used = widths[first];
            while (first > 0 && used + sep + widths[first - 1] + (first - 1 > 0 ? sep + ellipsis : 0) <= available)
            {
                first--;
                used += sep + widths[first];
            }
            int x = left;
            if (first > 0)
            {
                crumbs.Add(new Crumb { Text = "…", Bounds = new Rectangle(x, 0, ellipsis, TopBarH) });
                x += ellipsis + sep;
            }
            for (int i = first; i < chain.Count; i++)
            {
                int w = Math.Min(widths[i], Math.Max(0, right - x));
                chain[i].Bounds = new Rectangle(x, 0, w, TopBarH);
                crumbs.Add(chain[i]);
                x += w + sep;
            }
        }

        private int SeparatorWidth()
        {
            return TextRenderer.MeasureText(" › ", Theme.ButtonFont, Size.Empty,
                TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutChrome();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Background);
            DrawCanvas(g);
            DrawTopBar(g);
            DrawPill(g);
            DrawInfoBar(g);
        }

        private void DrawCanvas(Graphics g)
        {
            Rectangle area = ListArea;
            if (area.Width <= 0 || area.Height <= 0) return;
            using (SolidBrush b = new SolidBrush(Color.White)) g.FillRectangle(b, area);
            using (Pen p = new Pen(Theme.Separator, 1)) g.DrawRectangle(p, area);
            string message = loadFailed ? failureText
                : loading ? "正在读取…"
                : current != null && rows.Count == 0 ? "压缩包为空" : null;
            if (message != null)
                using (SolidBrush b = new SolidBrush(Theme.TextSecondary))
                    Draw.DrawStringCenter(g, message, Theme.BodyFont, b, area);
        }

        private void DrawTopBar(Graphics g)
        {
            Draw.DrawIcon(g, UiIconKind.Back,
                new Rectangle(backRect.X + Dpi(6), backRect.Y + (backRect.Height - Dpi(20)) / 2, Dpi(20), Dpi(20)), Theme.IconColor);
            TextRenderer.DrawText(g, "返回", Theme.ButtonFont,
                new Rectangle(backRect.X + Dpi(24), backRect.Y, backRect.Width - Dpi(24), backRect.Height),
                Theme.TextPrimary, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
                | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
            int sep = SeparatorWidth();
            for (int i = 0; i < crumbs.Count; i++)
            {
                Crumb crumb = crumbs[i];
                bool last = i == crumbs.Count - 1;
                Color color = last ? Theme.TextPrimary
                    : crumb.Target != null && i == hoverCrumb ? Theme.AccentText : Theme.TextSecondary;
                TextRenderer.DrawText(g, crumb.Text, Theme.ButtonFont, crumb.Bounds, color, flags);
                if (!last)
                    TextRenderer.DrawText(g, " › ", Theme.ButtonFont,
                        new Rectangle(crumb.Bounds.Right, 0, sep, TopBarH), Theme.TextTertiary, flags);
            }
        }

        private void DrawPill(Graphics g)
        {
            using (SolidBrush b = new SolidBrush(Theme.TooltipBg))
                Draw.FillRoundedRect(g, b, new Rectangle(prevRect.X, prevRect.Y, prevRect.Width + nextRect.Width, PillH), PillH / 2);
            DrawChevron(g, prevRect, true);
            DrawChevron(g, nextRect, false);
        }

        private void DrawChevron(Graphics g, Rectangle r, bool left)
        {
            using (Pen p = new Pen(paths.Count < 2 ? Color.FromArgb(90, 255, 255, 255) : Color.White, 1.8F))
            {
                p.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                p.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                float cx = r.X + r.Width / 2F;
                float cy = r.Y + r.Height / 2F;
                float dx = left ? Dpi(3) : -Dpi(3);
                g.DrawLine(p, cx + dx, cy - Dpi(6), cx - dx, cy);
                g.DrawLine(p, cx - dx, cy, cx + dx, cy + Dpi(6));
            }
        }

        private void DrawInfoBar(Graphics g)
        {
            using (Pen p = new Pen(Theme.Separator, 1))
                g.DrawLine(p, Dpi(12), infoBarRect.Y, infoBarRect.Right - Dpi(12), infoBarRect.Y);
            int pad = Dpi(16);
            int countW = Dpi(80);
            Rectangle countR = new Rectangle(infoBarRect.Right - pad - countW, infoBarRect.Y, countW, infoBarRect.Height);
            Rectangle textR = new Rectangle(infoBarRect.X + pad, infoBarRect.Y,
                Math.Max(0, countR.Left - Dpi(12) - (infoBarRect.X + pad)), infoBarRect.Height);
            TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis
                | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            if (hint != null) TextRenderer.DrawText(g, hint, Theme.BodyFont, textR, Theme.AccentText, flags);
            else TextRenderer.DrawText(g, Summary(), Theme.BodyFont, textR, Theme.TextPrimary, flags);
            if (paths.Count > 0)
                TextRenderer.DrawText(g, (index + 1) + " / " + paths.Count, Theme.CaptionFont, countR, Theme.TextSecondary,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }

        /// <summary>底栏摘要：文件名 · 数量 · 解压后大小 · 压缩包大小，以及加密/截断/损坏提示。</summary>
        private string Summary()
        {
            if (paths.Count == 0 || index >= paths.Count) return string.Empty;
            string text = Path.GetFileName(paths[index]);
            if (current == null) return text;
            text += " · " + (current.FolderCount > 0 ? current.FolderCount + " 个文件夹，" : string.Empty) + current.FileCount + " 个文件";
            if (current.SizeUnknown && current.TotalSize == 0) text += " · 解压后大小未知";
            else text += " · 解压后 " + HumanSize(current.TotalSize) + (current.SizeUnknown ? "+" : string.Empty);
            text += " · 压缩包 " + HumanSize(current.ArchiveBytes);
            if (current.HasEncrypted) text += " · 含加密文件";
            if (current.Truncated) text += " · 仅列出前 " + ArchivePreview.MaxEntries.ToString("N0") + " 项";
            if (current.Warning != null) text += " · " + current.Warning;
            return text;
        }

        // ===== 交互 =====

        private int CrumbAt(Point p)
        {
            for (int i = 0; i < crumbs.Count; i++)
                if (crumbs[i].Target != null && crumbs[i].Bounds.Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.XButton1) { GoUp(); return; }
            if (e.Button != MouseButtons.Left) return;
            if (backRect.Contains(e.Location)) { RequestClose(); return; }
            if (prevRect.Contains(e.Location)) { Step(-1); return; }
            if (nextRect.Contains(e.Location)) { Step(1); return; }
            int crumb = CrumbAt(e.Location);
            if (crumb >= 0)
            {
                // 跳到上层目录时选中来时经过的那个子文件夹
                ArchiveEntryInfo target = crumbs[crumb].Target, from = folder;
                while (from != null && from.Parent != target) from = from.Parent;
                ShowFolder(target, from);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int crumb = CrumbAt(e.Location);
            Cursor = crumb >= 0 ? Cursors.Hand : Cursors.Default;
            if (crumb != hoverCrumb) { hoverCrumb = crumb; Invalidate(new Rectangle(0, 0, Width, TopBarH)); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hoverCrumb >= 0) { hoverCrumb = -1; Invalidate(new Rectangle(0, 0, Width, TopBarH)); }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { RequestClose(); return true; }
            if (keyData == (Keys.Alt | Keys.Left)) { Step(-1); return true; }
            if (keyData == (Keys.Alt | Keys.Right)) { Step(1); return true; }
            if (keyData == Keys.Back || keyData == (Keys.Alt | Keys.Up)) { GoUp(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                loader.Dispose();
                hintTimer.Dispose();
                list.Dispose();
                icons.Dispose();
            }
            base.Dispose(disposing);
        }

        // ===== Win32 =====

        private const int LVM_GETHEADER = 0x101F;
        private const int HDM_GETITEMW = 0x120B;
        private const int HDM_SETITEMW = 0x120C;
        private const uint HDI_FORMAT = 0x0004;
        private const int HDF_SORTDOWN = 0x0200;
        private const int HDF_SORTUP = 0x0400;

        [StructLayout(LayoutKind.Sequential)]
        private struct HDITEM
        {
            public uint mask;
            public int cxy;
            public IntPtr pszText;
            public IntPtr hbm;
            public int cchTextMax;
            public int fmt;
            public IntPtr lParam;
            public int iImage;
            public int iOrder;
            public uint type;
            public IntPtr pvFilter;
            public uint state;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref HDITEM lParam);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hWnd, string appName, string idList);

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern int StrCmpLogicalW(string a, string b);
    }
}

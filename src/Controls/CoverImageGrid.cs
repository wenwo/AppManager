using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Threading;
using System.Windows.Forms;
using AppManager;

namespace AppManager.Controls
{
    /// <summary>
    /// 选封面窗口和添加照片页的缩略图网格（添加页用勾选模式，见 IsChecked）：先列子文件夹，再列图片。单击文件夹进入，单击图片选中，双击图片直接使用；
    /// 方向键移动，回车由窗口处理。缩略图只为露出来的格子在后台解码，最多同时解码 MaxWorkers 张、
    /// 最多保留 MaxThumbs 张（超出时先丢离视口最远的）；换文件夹或关闭时取消在途解码并释放全部位图。
    /// </summary>
    internal sealed class CoverImageGrid : ScrollListBase
    {
        internal sealed class Item
        {
            internal bool Folder;
            internal string Path;
            internal string Name = string.Empty;
        }

        /// <summary>一个文件夹的一批缩略图任务；换文件夹时整批取消，迟到的结果按批次丢弃。</summary>
        private sealed class ThumbBatch
        {
            internal readonly List<string> Paths = new List<string>();
            internal readonly CancellationTokenSource Cancel = new CancellationTokenSource();
            internal readonly List<int> Pending = new List<int>();
            internal readonly HashSet<int> Requested = new HashSet<int>();
            internal int Active;
            internal int First;     // 当前露出来的下标范围：离开视口太远的排队任务直接丢掉，滚回来再排
            internal int Last;
            internal int TilePixels;
        }

        private const int MaxWorkers = 3;
        internal const int MaxThumbs = 300;
        private const int MinTile = 96;
        private const int Gap = 10;
        private const int Pad = 10;
        private const int LabelHeight = 24;
        private const string GlyphFolder = "";
        private const string GlyphPicture = "";

        private readonly List<Item> items = new List<Item>();
        private readonly Dictionary<int, Bitmap> thumbs = new Dictionary<int, Bitmap>();
        private readonly HashSet<int> failed = new HashSet<int>();
        private ThumbBatch batch;
        private string emptyText = string.Empty;
        private int selected = -1;
        private int hover = -1;
        private int pressedFolder = -1;
        private string revealPath;
        // 刚进入文件夹后的双击间隔内不响应点击，免得双击文件夹时第二下点中新文件夹里的图片。
        // 用「是否在抑制 + 开始时刻」而不是截止时刻：TickCount 开机约 24.9 天后为负数，与 0 比较会把所有点击都当成抑制
        private bool suppressActive;
        private int suppressStart;
        private bool suppressedPress;
        private bool pressedCheck;   // 这次按下落在勾选框上：已切换勾选，双击不再重复切换

        internal event Action<string> FolderOpened;
        internal event Action<string> ImageSelected;
        internal event Action<string> ImageActivated;
        /// <summary>勾选模式下点了图片右上角的勾选框。</summary>
        internal event Action<string> CheckToggled;

        /// <summary>
        /// 勾选模式：非空时每张图片右上角都画勾选框，勾选状态由宿主维护（这里只读）；勾上的格子描粗边。
        /// 单击仍只是选中（供宿主预览），点勾选框才触发 CheckToggled。为空时保持选封面时的单选样式。
        /// </summary>
        internal Func<string, bool> IsChecked;

        internal CoverImageGrid()
        {
            BackColor = Theme.Card;
        }

        internal int Count { get { return items.Count; } }
        internal int ThumbCount { get { return thumbs.Count; } }
        internal int FailedCount { get { return failed.Count; } }

        internal string SelectedFolder
        {
            get { return selected >= 0 && selected < items.Count && items[selected].Folder ? items[selected].Path : null; }
        }

        internal string SelectedImage
        {
            get { return selected >= 0 && selected < items.Count && !items[selected].Folder ? items[selected].Path : null; }
        }

        internal string EmptyText { get { return emptyText; } }

        /// <summary>换一批内容：取消上一批缩略图任务并释放位图，滚回顶部。</summary>
        internal void SetItems(List<Item> list, string empty)
        {
            CancelBatch();
            items.Clear();
            if (list != null) items.AddRange(list);
            emptyText = empty ?? string.Empty;
            selected = -1;
            hover = -1;
            pressedFolder = -1;
            batch = new ThumbBatch();
            foreach (Item item in items) batch.Paths.Add(item.Folder ? null : item.Path);
            SetContentHeight(ContentHeight());
            SetScroll(0);
            if (revealPath != null && items.Count > 0)
            {
                int index = IndexOfPath(revealPath, true);
                revealPath = null;
                if (index >= 0) { selected = index; Reveal(index); }
            }
            Invalidate();
        }

        /// <summary>下一次载入内容后把这个文件夹滚进视口并移上键盘光标（返回上一级时用）。</summary>
        internal void RevealAfterLoad(string folder)
        {
            revealPath = folder;
        }

        /// <summary>选中路径对应的图片（不触发事件）；找不到时只取消图片选中，文件夹上的键盘光标保留。</summary>
        internal void SelectPath(string path)
        {
            int index = string.IsNullOrEmpty(path) ? -1 : IndexOfPath(path, false);
            if (index >= 0)
            {
                if (index != selected) { selected = index; Invalidate(); }
                Reveal(index);
            }
            else if (SelectedImage != null) { selected = -1; Invalidate(); }
        }

        /// <summary>刚进入文件夹：双击间隔内的点击不算数。</summary>
        internal void SuppressClicksBriefly()
        {
            suppressActive = true;
            suppressStart = Environment.TickCount;
        }

        /// <summary>从 start 起是否仍在 window 毫秒内；TickCount 为负或回绕时按差值计算，结果不受影响。</summary>
        internal static bool InSuppressWindow(int start, int now, int window)
        {
            int elapsed = unchecked(now - start);
            return elapsed >= 0 && elapsed < window;
        }

        private int IndexOfPath(string path, bool folder)
        {
            string wanted = LocalScanScope.NormalizePath(path);
            if (wanted == null) return -1;
            for (int i = 0; i < items.Count; i++)
                if (items[i].Folder == folder && string.Equals(items[i].Path, wanted, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        // ===== 排版 =====

        private int Columns
        {
            get
            {
                int avail = Math.Max(1, Width - Dpi(Pad) * 2 - Dpi(8));
                return Math.Max(1, (avail + Dpi(Gap)) / (Dpi(MinTile) + Dpi(Gap)));
            }
        }

        private int TileSize
        {
            get
            {
                int cols = Columns;
                int avail = Math.Max(1, Width - Dpi(Pad) * 2 - Dpi(8));
                return Math.Max(1, (avail - Dpi(Gap) * (cols - 1)) / cols);
            }
        }

        private int RowHeight { get { return TileSize + Dpi(LabelHeight) + Dpi(Gap); } }

        private int ContentHeight()
        {
            if (items.Count == 0) return 0;
            int rows = (items.Count + Columns - 1) / Columns;
            return Dpi(Pad) * 2 + rows * RowHeight - Dpi(Gap);
        }

        /// <summary>格子的缩略图区域（内容坐标，不含下方文件名）。</summary>
        private Rectangle TileRect(int index)
        {
            int cols = Columns, tile = TileSize;
            return new Rectangle(Dpi(Pad) + (index % cols) * (tile + Dpi(Gap)), Dpi(Pad) + (index / cols) * RowHeight, tile, tile);
        }

        private Rectangle CellRect(int index)
        {
            Rectangle tile = TileRect(index);
            return new Rectangle(tile.X, tile.Y, tile.Width, tile.Height + Dpi(LabelHeight));
        }

        private Rectangle ClientCell(int index)
        {
            Rectangle cell = CellRect(index);
            cell.Offset(0, -ScrollOffset);
            return cell;
        }

        private int IndexAt(Point client)
        {
            if (!ClientRectangle.Contains(client) || items.Count == 0) return -1;
            Point p = new Point(client.X, client.Y + ScrollOffset);
            int cols = Columns, tile = TileSize;
            int col = (p.X - Dpi(Pad)) / (tile + Dpi(Gap));
            int row = (p.Y - Dpi(Pad)) / RowHeight;
            if (p.X < Dpi(Pad) || p.Y < Dpi(Pad) || col < 0 || col >= cols || row < 0) return -1;
            int index = row * cols + col;
            return index < items.Count && CellRect(index).Contains(p) ? index : -1;
        }

        private void Reveal(int index)
        {
            if (index < 0 || index >= items.Count) return;
            Rectangle cell = CellRect(index);
            EnsureVisible(cell.Top - Dpi(Pad), cell.Bottom + Dpi(Pad));
        }

        private void VisibleRange(out int first, out int last)
        {
            int cols = Columns, rowH = Math.Max(1, RowHeight);
            first = Math.Max(0, (ScrollOffset - Dpi(Pad)) / rowH * cols);
            last = Math.Min(items.Count - 1, ((ScrollOffset + Height) / rowH + 1) * cols - 1);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            SetContentHeight(ContentHeight());
            DropThumbsIfTooSmall();
            Invalidate();
        }

        /// <summary>
        /// 格子明显变大（移到高缩放屏幕等）时，已解出的缩略图放大后会发糊：全部释放，按新尺寸重新排队。
        /// 失败标记保留（换尺寸也解不开）；在途的旧尺寸结果到达后照常显示，下次变化时再替换。
        /// </summary>
        private void DropThumbsIfTooSmall()
        {
            ThumbBatch current = batch;
            if (current == null || thumbs.Count == 0) return;
            int tile = TileSize;
            lock (current)
            {
                if (current.TilePixels <= 0 || tile <= current.TilePixels * 5 / 4) return;
                current.TilePixels = tile;
                current.Pending.Clear();
                current.Requested.Clear();
            }
            foreach (Bitmap bmp in thumbs.Values) if (bmp != null) bmp.Dispose();
            thumbs.Clear();
        }

        protected override void OnScrolled()
        {
            hover = IsHandleCreated ? IndexAt(PointToClient(Cursor.Position)) : -1;
        }

        // ===== 绘制 =====

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (SolidBrush back = new SolidBrush(BackColor)) g.FillRectangle(back, ClientRectangle);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (items.Count == 0)
            {
                TextRenderer.DrawText(g, emptyText, Theme.BodyFont, new Rectangle(Dpi(24), 0, Math.Max(0, Width - Dpi(48)), Height),
                    Theme.TextTertiary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak
                    | TextFormatFlags.NoPrefix);
                return;
            }
            int first, last;
            VisibleRange(out first, out last);
            for (int i = first; i <= last; i++)
            {
                Rectangle cell = ClientCell(i);
                if (!cell.IntersectsWith(e.ClipRectangle)) continue;
                PaintCell(g, i, cell);
            }
            PaintScrollBar(g);
            RequestThumbs(first, last);
        }

        private void PaintCell(Graphics g, int index, Rectangle cell)
        {
            Item item = items[index];
            Rectangle tile = new Rectangle(cell.X, cell.Y, cell.Width, cell.Width);
            bool isSelected = index == selected;
            bool isHover = index == hover && !OnScrollBar;
            bool checkMode = IsChecked != null && !item.Folder;
            bool isChecked = checkMode && IsChecked(item.Path);
            int radius = Dpi(8);
            using (GraphicsPath clip = Draw.RoundedPath(tile, radius))
            {
                Bitmap thumb;
                if (item.Folder)
                {
                    using (SolidBrush bg = new SolidBrush(isHover ? Theme.RowHover : Theme.SearchBg)) g.FillPath(bg, clip);
                    int size = tile.Width * 2 / 5;
                    Draw.DrawGlyph(g, GlyphFolder, new Rectangle(tile.X + (tile.Width - size) / 2, tile.Y + (tile.Height - size) / 2, size, size),
                        Theme.FolderTint);
                }
                else if (thumbs.TryGetValue(index, out thumb) && thumb != null)
                {
                    GraphicsState state = g.Save();
                    g.SetClip(clip);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(thumb, tile);
                    g.Restore(state);
                }
                else
                {
                    bool broken = failed.Contains(index);
                    using (SolidBrush bg = new SolidBrush(Theme.ChipBg)) g.FillPath(bg, clip);
                    int size = tile.Width / 4;
                    Draw.DrawGlyph(g, GlyphPicture, new Rectangle(tile.X + (tile.Width - size) / 2, tile.Y + (tile.Height - size) / 2 - (broken ? Dpi(8) : 0), size, size),
                        Theme.TextTertiary);
                    if (broken)
                        TextRenderer.DrawText(g, "无法预览", Theme.CaptionFont,
                            new Rectangle(tile.X, tile.Y + tile.Height / 2 + Dpi(8), tile.Width, Dpi(20)), Theme.TextTertiary,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                }
                // 勾选模式：勾上的描粗边，选中（正在预览）和悬停描细边；否则选中描粗边
                bool strong = checkMode ? isChecked : isSelected;
                if (strong || isSelected || (isHover && !item.Folder))
                {
                    float width = strong ? Math.Max(2F, Dpi(5) / 2F) : 1.5F;
                    Color color = strong ? Theme.Accent : Color.FromArgb(140, Theme.Accent);
                    using (Pen pen = new Pen(color, width))
                    {
                        Rectangle edge = Rectangle.Inflate(tile, -(int)Math.Ceiling(width / 2F), -(int)Math.Ceiling(width / 2F));
                        Draw.DrawRoundedRect(g, pen, edge, radius);
                    }
                }
            }
            if (checkMode) Draw.DrawCheckBox(g, CheckBoxRect(tile), isChecked, true, DpiHelper.ForControl(this));
            else if (isSelected && !item.Folder) Draw.DrawCheckBox(g, CheckBoxRect(tile), true, true, DpiHelper.ForControl(this));
            Rectangle label = new Rectangle(cell.X, tile.Bottom + Dpi(2), cell.Width, Dpi(LabelHeight) - Dpi(2));
            TextRenderer.DrawText(g, item.Name, isSelected ? Theme.FontOf(9F, FontStyle.Bold) : Theme.CaptionFont, label,
                isSelected ? Theme.AccentText : Theme.TextPrimary,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis
                | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }

        /// <summary>格子右上角的勾选框（与格子同一坐标系）。</summary>
        private Rectangle CheckBoxRect(Rectangle tile)
        {
            int badge = Dpi(20);
            return new Rectangle(tile.Right - badge - Dpi(6), tile.Y + Dpi(6), badge, badge);
        }

        /// <summary>勾选模式下客户区坐标是否落在这张图片的勾选框上（四周放宽几像素，好点中）。</summary>
        private bool HitsCheckBox(int index, Point client)
        {
            if (IsChecked == null || index < 0 || index >= items.Count || items[index].Folder) return false;
            Rectangle cell = ClientCell(index);
            Rectangle box = CheckBoxRect(new Rectangle(cell.X, cell.Y, cell.Width, cell.Width));
            box.Inflate(Dpi(5), Dpi(5));
            return box.Contains(client);
        }

        // ===== 缩略图 =====

        /// <summary>为露出来的、还没有缩略图的图片排队解码，最近露出来的先解。</summary>
        private void RequestThumbs(int first, int last)
        {
            ThumbBatch current = batch;
            if (current == null) return;
            int tilePixels = TileSize;
            lock (current)
            {
                current.First = first;
                current.Last = last;
                current.TilePixels = tilePixels;   // 之后排队的按当前格子大小解码
                for (int i = last; i >= first; i--)
                {
                    if (items[i].Folder || thumbs.ContainsKey(i) || failed.Contains(i)) continue;
                    if (current.Requested.Add(i)) current.Pending.Add(i);
                }
                while (current.Active < MaxWorkers && current.Pending.Count > 0)
                {
                    current.Active++;
                    bool queued = false;
                    try { queued = ThreadPool.QueueUserWorkItem(delegate { ThumbWorker(current); }); }
                    catch (NotSupportedException) { }
                    if (!queued) { current.Active--; break; }
                }
            }
        }

        private void ThumbWorker(ThumbBatch work)
        {
            try { ThumbWorkerLoop(work); }
            catch (Exception ex)
            {
                // 兜底：线程池上未处理的异常会让程序退出；记下原因，让出名额，剩下的格子滚动时会重新排队
                Paths.LogError(ex);
                lock (work) { work.Active--; work.Pending.Clear(); work.Requested.Clear(); }
            }
        }

        private void ThumbWorkerLoop(ThumbBatch work)
        {
            while (true)
            {
                int index;
                string path;
                int pixels;
                lock (work)
                {
                    if (work.Cancel.IsCancellationRequested || work.Pending.Count == 0) { work.Active--; return; }
                    index = work.Pending[work.Pending.Count - 1];
                    work.Pending.RemoveAt(work.Pending.Count - 1);
                    int margin = Math.Max(12, work.Last - work.First + 1);
                    if (index < work.First - margin || index > work.Last + margin)
                    {
                        work.Requested.Remove(index);   // 已经滚远了：不解，滚回来时再排
                        continue;
                    }
                    path = work.Paths[index];
                    pixels = work.TilePixels;
                }
                Bitmap bmp = DecodeTile(path, pixels, work.Cancel.Token);
                if (work.Cancel.IsCancellationRequested) { if (bmp != null) bmp.Dispose(); continue; }
                PostThumb(work, index, bmp);
            }
        }

        private void PostThumb(ThumbBatch work, int index, Bitmap bmp)
        {
            bool posted = false;
            try
            {
                if (!IsDisposed && IsHandleCreated)
                {
                    BeginInvoke((Action)delegate
                    {
                        if (IsDisposed || Disposing || work != batch) { if (bmp != null) bmp.Dispose(); return; }
                        if (bmp == null) failed.Add(index);
                        else
                        {
                            Bitmap old;
                            if (thumbs.TryGetValue(index, out old) && old != null) old.Dispose();
                            thumbs[index] = bmp;
                            EvictFarThumbs();
                        }
                        Invalidate(ClientCell(index));
                    });
                    posted = true;
                }
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
            if (!posted && bmp != null) bmp.Dispose();
        }

        /// <summary>超过上限时释放离视口最远的缩略图，并允许它们滚回来时重新排队。</summary>
        private void EvictFarThumbs()
        {
            if (thumbs.Count <= MaxThumbs || batch == null) return;
            int first, last;
            VisibleRange(out first, out last);
            int center = (first + last) / 2;
            List<int> keys = new List<int>(thumbs.Keys);
            keys.Sort(delegate(int a, int b) { return Math.Abs(b - center).CompareTo(Math.Abs(a - center)); });
            int remove = thumbs.Count - MaxThumbs * 4 / 5;
            for (int i = 0; i < remove && i < keys.Count; i++)
            {
                Bitmap bmp = thumbs[keys[i]];
                thumbs.Remove(keys[i]);
                if (bmp != null) bmp.Dispose();
                lock (batch) batch.Requested.Remove(keys[i]);
            }
        }

        /// <summary>
        /// 解出一张正方形缩略图（居中取景）：WIC 在解码阶段就按短边缩小，大照片不整图解码。
        /// 列出的格式都由 WIC 解码，解不开（未装解码器、文件损坏或被占用）就返回 null，格子显示「无法预览」；
        /// 能不能当封面由选中后的预览另行判断。任何失败都不抛出（在线程池上抛出会让程序退出）。只在后台线程调用。
        /// </summary>
        internal static Bitmap DecodeTile(string path, int pixels, CancellationToken cancel)
        {
            if (string.IsNullOrEmpty(path) || pixels <= 0 || cancel.IsCancellationRequested) return null;
            Bitmap source;
            try
            {
                source = WicImageDecoder.Load(path, delegate(int w, int h)
                {
                    int shortSide = Math.Min(w, h), longSide = Math.Max(w, h);
                    if (shortSide <= 0) return pixels;
                    return (int)Math.Min((long)pixels * 6, (long)Math.Ceiling(pixels * (double)longSide / shortSide));
                });
            }
            catch (Exception) { return null; }
            if (source == null) return null;
            Bitmap tile = null;
            try
            {
                if (cancel.IsCancellationRequested) return null;
                tile = new Bitmap(pixels, pixels, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(tile))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    int side = Math.Min(source.Width, source.Height);
                    Rectangle src = new Rectangle((source.Width - side) / 2, (source.Height - side) / 2, side, side);
                    g.DrawImage(source, new Rectangle(0, 0, pixels, pixels), src, GraphicsUnit.Pixel);
                }
                Bitmap done = tile;
                tile = null;
                return done;
            }
            catch (Exception) { return null; }   // 内存或 GDI 资源不足：这一格显示「无法预览」
            finally
            {
                if (tile != null) tile.Dispose();
                source.Dispose();
            }
        }

        private void CancelBatch()
        {
            if (batch != null)
            {
                lock (batch) batch.Cancel.Cancel();
                // 不在这里 Dispose CancellationTokenSource：还在解码的线程会读它的状态，结束后随批次一起回收
            }
            foreach (Bitmap bmp in thumbs.Values) if (bmp != null) bmp.Dispose();
            thumbs.Clear();
            failed.Clear();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CancelBatch();
                batch = null;
            }
            base.Dispose(disposing);
        }

        // ===== 鼠标与键盘 =====

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (ScrollMouseMove(e)) { SetHover(-1); return; }
            SetHover(IndexAt(e.Location));
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            SetHover(-1);
        }

        private void SetHover(int index)
        {
            if (index == hover) return;
            int old = hover;
            hover = index;
            if (old >= 0 && old < items.Count) Invalidate(ClientCell(old));
            if (index >= 0 && index < items.Count) Invalidate(ClientCell(index));
            Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            suppressedPress = false;
            pressedCheck = false;
            pressedFolder = -1;
            if (ScrollMouseDown(e) || e.Button != MouseButtons.Left) return;
            if (suppressActive)
            {
                if (InSuppressWindow(suppressStart, Environment.TickCount, SystemInformation.DoubleClickTime)) { suppressedPress = true; return; }
                suppressActive = false;
            }
            int index = IndexAt(e.Location);
            if (index < 0) return;
            if (items[index].Folder) pressedFolder = index;
            else
            {
                pressedCheck = HitsCheckBox(index, e.Location);
                SelectIndex(index, true);
                if (pressedCheck)
                {
                    Invalidate(ClientCell(index));
                    if (CheckToggled != null) CheckToggled(items[index].Path);
                }
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (ScrollMouseUp(e) || e.Button != MouseButtons.Left) return;
            int index = IndexAt(e.Location);
            int pressed = pressedFolder;
            pressedFolder = -1;
            if (pressed < 0 || index != pressed) return;
            OpenFolder(index);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button != MouseButtons.Left || suppressedPress || pressedCheck) return;
            int index = IndexAt(e.Location);
            if (index < 0 || items[index].Folder || index != selected) return;
            if (ImageActivated != null) ImageActivated(items[index].Path);
        }

        private void OpenFolder(int index)
        {
            string path = items[index].Path;
            SuppressClicksBriefly();
            if (FolderOpened != null) FolderOpened(path);
        }

        private void SelectIndex(int index, bool raise)
        {
            if (index < 0 || index >= items.Count) return;
            int old = selected;
            selected = index;
            Reveal(index);
            if (old >= 0 && old < items.Count) Invalidate(ClientCell(old));
            Invalidate(ClientCell(index));
            if (raise && !items[index].Folder && ImageSelected != null) ImageSelected(items[index].Path);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Left:
                case Keys.Right:
                case Keys.Up:
                case Keys.Down:
                case Keys.Home:
                case Keys.End:
                    return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (items.Count == 0) return;
            int cols = Columns;
            int target = selected;
            switch (e.KeyCode)
            {
                case Keys.Left: target = selected <= 0 ? 0 : selected - 1; break;
                case Keys.Right: target = selected < 0 ? 0 : Math.Min(items.Count - 1, selected + 1); break;
                case Keys.Up: target = selected < 0 ? 0 : Math.Max(0, selected - cols); break;
                case Keys.Down: target = selected < 0 ? 0 : Math.Min(items.Count - 1, selected + cols); break;
                case Keys.Home: target = 0; break;
                case Keys.End: target = items.Count - 1; break;
                default: return;
            }
            e.Handled = true;
            if (target != selected) SelectIndex(target, true);
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            Invalidate();
        }
    }
}

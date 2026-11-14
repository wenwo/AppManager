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
    /// <summary>导入记录里一次导入的整体状态。</summary>
    internal enum ImportBatchStatus { Done, Cleanup, Partial, NotSaved, Unfinished }

    /// <summary>状态标签的色调：成功、中性、需留意、失败。</summary>
    internal enum ImportTone { Good, Neutral, Warn, Bad }

    /// <summary>
    /// 「导入记录」窗口：左边是每次导入的记录，右边是选中那次的数字汇总和逐个文件明细。
    /// 窗口只读取并展示日志；用户确认清理原件后把批次放进 RetryBatch，删源仍由主窗口的导入队列执行。
    /// </summary>
    internal sealed class ImportHistoryDialog : BufferedDialogForm
    {
        /// <summary>明细每次列出的行数。文件很多时不一次塞进列表：有问题的排在前面，其余点「显示更多」逐页追加。</summary>
        internal const int PageRows = 1000;
        /// <summary>明细末尾的「显示更多」行；列表里除它以外都是 Row。</summary>
        private static readonly string MoreRows = "显示更多";
        private const int BatchRowHeight = 58;

        private static readonly Color GoodText = Color.FromArgb(24, 128, 66);
        private static readonly Color GoodBg = Color.FromArgb(228, 245, 234);
        private static readonly Color WarnText = Color.FromArgb(196, 98, 0);
        private static readonly Color WarnBg = Color.FromArgb(255, 241, 222);
        private static readonly Color BadText = Color.FromArgb(207, 48, 42);
        private static readonly Color BadBg = Color.FromArgb(255, 235, 233);

        // 仅界面线程使用的共享绘制格式：路径在中间省略（保留盘符和文件名），错误信息折行显示
        private static readonly StringFormat PathFormat = new StringFormat
        {
            LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisPath, FormatFlags = StringFormatFlags.NoWrap
        };
        private static readonly StringFormat ErrorFormat = new StringFormat
        {
            Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.LineLimit
        };

        private readonly string journal;
        private readonly Func<bool> importBusy;
        private readonly SoftwarePickerDialog.PickerList batchList = new SoftwarePickerDialog.PickerList();
        private readonly SoftwarePickerDialog.PickerList itemList = new SoftwarePickerDialog.PickerList();
        private readonly Label notice = new Label();
        private readonly Label footer = new Label();
        private readonly RoundButton retry = new RoundButton();
        private readonly RoundButton close = new RoundButton();
        private readonly ToolTip tip = new ToolTip();

        private Rectangle leftFrame, rightFrame, tilesRect, noticeRect;   // 已按当前 DPI 换算
        private string placeholder = "正在读取导入记录…";
        private string headerSummary = string.Empty;
        private Record current;
        private int tipIndex = -1;
        private int shownRows;   // 当前记录已列出的明细行数（不含「显示更多」行）
        private bool layingOut;
        private volatile bool closed;

        /// <summary>用户确认要清理原件的批次；直接关闭窗口时为 null。</summary>
        internal ImportBatch RetryBatch { get; private set; }

        /// <summary>importBusy 返回 true 表示导入队列正在工作，此时不提供清理原件的入口。</summary>
        internal ImportHistoryDialog(string journal, Func<bool> importBusy)
        {
            this.journal = journal;
            this.importBusy = importBusy;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(960, 640);
            BackColor = Theme.Background;
            ShowInTaskbar = false;
            Text = "导入记录";

            batchList.ItemHeight = BatchRowHeight;
            batchList.AccessibleName = "导入记录";
            batchList.DrawItem += DrawBatch;
            batchList.SelectedIndexChanged += delegate { ShowRecord(batchList.SelectedItem as Record); };
            Controls.Add(batchList);

            itemList.DrawMode = DrawMode.OwnerDrawVariable;
            itemList.AccessibleName = "文件明细";
            itemList.MeasureItem += delegate(object s, MeasureItemEventArgs e)
            {
                if (e.Index < 0 || e.Index >= itemList.Items.Count) return;
                Row row = itemList.Items[e.Index] as Row;
                e.ItemHeight = row == null ? Dpi(44) : RowHeightOf(row);
            };
            itemList.DrawItem += DrawRow;
            itemList.MouseMove += delegate(object s, MouseEventArgs e) { UpdateTip(itemList.HitRow(e.Location)); };
            itemList.MouseLeave += delegate { UpdateTip(-1); };
            itemList.MouseUp += delegate(object s, MouseEventArgs e)
            {
                int index = itemList.HitRow(e.Location);
                if (index < 0) return;
                Row row = itemList.Items[index] as Row;
                if (row == null) { if (e.Button == MouseButtons.Left) ShowMoreRows(); return; }
                if (e.Button != MouseButtons.Right) return;
                itemList.SelectedIndex = index;
                ShowRowMenu(row);
            };
            Controls.Add(itemList);

            // 批次级的错误和处理建议：放不下时省略，鼠标停留显示全文（Label 自带）
            notice.Font = Theme.CaptionFont;
            notice.ForeColor = WarnText;
            notice.BackColor = WarnBg;
            notice.AutoSize = false;
            notice.AutoEllipsis = true;
            notice.UseMnemonic = false;
            Controls.Add(notice);

            footer.Font = Theme.CaptionFont;
            footer.ForeColor = Theme.TextSecondary;
            footer.AutoSize = false;
            footer.AutoEllipsis = true;
            footer.UseMnemonic = false;
            footer.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(footer);

            retry.Text = "删除已入库项的源文件";
            retry.Visible = false;
            retry.Click += delegate { RequestRetry(); };
            Controls.Add(retry);

            close.Text = "关闭";
            close.Click += delegate { Close(); };
            Controls.Add(close);

            tip.AutoPopDelay = 20000;
            ApplyVisibility();
            LayoutContent();
            ActiveControl = close;
        }

        // ===== 记录与明细的整理（纯逻辑，internal 供回归测试） =====

        /// <summary>一次导入：批次本身、日志最后更新时间，以及后台线程里先算好的数字和明细行。</summary>
        internal sealed class Record
        {
            internal readonly ImportBatch Batch;
            internal readonly DateTime Time;
            internal readonly ImportBatchStatus Status;
            /// <summary>已复制进资源库的项数、原本就已收录或被跳过的项数、失败或被撤回的项数、原件还没清理的项数。</summary>
            internal readonly int Stored, Existing, Failed, Cleanup;
            internal readonly bool HasRolledBack;
            internal readonly List<Row> Rows;

            internal Record(ImportBatch batch, DateTime time)
            {
                Batch = batch;
                Time = time;
                Status = Classify(batch);
                foreach (ImportItem item in batch.Items)
                {
                    string state = item.State;
                    if (state == FileImport.Moved || state == FileImport.Copied || state == FileImport.DeleteFailed) Stored++;
                    else if (state == FileImport.Linked || state == FileImport.Skipped) Existing++;
                    else if (state == FileImport.Failed || state == FileImport.RolledBack) Failed++;
                    if (state == FileImport.RolledBack) HasRolledBack = true;
                    if (MainForm.AwaitsSourceDelete(batch, item)) Cleanup++;
                }
                Rows = BuildRows(batch);
            }

            public override string ToString()
            {
                return Title(Batch) + "，" + StatusText(Status) + "，" + FileImport.Summary(Batch);
            }
        }

        /// <summary>明细里的一行：一个导入项及其显示用的状态文字、色调、文件名。</summary>
        internal sealed class Row
        {
            internal readonly ImportItem Item;
            internal readonly string Label, Name;
            internal readonly ImportTone Tone;
            /// <summary>排序用：0 失败或被撤回，1 删源失败或带错误说明，2 原件待清理，3 未处理，4 正常。</summary>
            internal readonly int Rank;
            /// <summary>错误信息占几行（0 表示没有）；按长度估算，不逐行测量文字。</summary>
            internal readonly int ErrorLines;

            internal Row(ImportBatch batch, ImportItem item)
            {
                Item = item;
                string state = item.State;
                bool cleanup = MainForm.AwaitsSourceDelete(batch, item);
                if (state == FileImport.Failed || state == FileImport.RolledBack) { Tone = ImportTone.Bad; Rank = 0; }
                else if (state == FileImport.DeleteFailed) { Tone = ImportTone.Warn; Rank = 1; }
                else if (cleanup) { Tone = ImportTone.Warn; Rank = 2; }
                else if (state == FileImport.Moved || (state == FileImport.Copied && batch.Committed)) { Tone = ImportTone.Good; Rank = 4; }
                else { Tone = ImportTone.Neutral; Rank = state == FileImport.Linked || state == FileImport.Skipped ? 4 : 3; }
                // 状态正常但带着错误说明（例如恢复时留下的“临时副本需核对”）：排到失败项之后
                if (Rank > 1 && !string.IsNullOrEmpty(item.Error)) Rank = 1;
                Label = cleanup && state == FileImport.Copied ? "原件待清理" : (string.IsNullOrEmpty(state) ? "未知" : state);
                Name = DisplayName(item.Source);
                ErrorLines = string.IsNullOrEmpty(item.Error) ? 0 : (item.Error.Length > 44 || item.Error.IndexOf('\n') >= 0 ? 2 : 1);
            }

            public override string ToString()
            {
                return Label + "：" + Item.Source;
            }
        }

        internal static ImportBatchStatus Classify(ImportBatch batch)
        {
            if (!batch.Finished) return ImportBatchStatus.Unfinished;
            if (!batch.Committed) return ImportBatchStatus.NotSaved;
            if (MainForm.CanRetryDelete(batch)) return ImportBatchStatus.Cleanup;
            if (!string.IsNullOrEmpty(batch.Error) || batch.Items.Any(i => i.State == FileImport.Failed
                || i.State == FileImport.DeleteFailed || i.State == FileImport.RolledBack)) return ImportBatchStatus.Partial;
            return ImportBatchStatus.Done;
        }

        internal static string StatusText(ImportBatchStatus status)
        {
            switch (status)
            {
                case ImportBatchStatus.Done: return "完成";
                case ImportBatchStatus.Cleanup: return "原件待清理";
                case ImportBatchStatus.Partial: return "部分完成";
                case ImportBatchStatus.NotSaved: return "未入库";
                default: return "未完成";
            }
        }

        private static ImportTone ToneOf(ImportBatchStatus status)
        {
            switch (status)
            {
                case ImportBatchStatus.Done: return ImportTone.Good;
                case ImportBatchStatus.NotSaved: return ImportTone.Bad;
                case ImportBatchStatus.Unfinished: return ImportTone.Neutral;
                default: return ImportTone.Warn;
            }
        }

        /// <summary>全部明细行：有问题的排在前面，同类保持日志里的顺序。</summary>
        internal static List<Row> BuildRows(ImportBatch batch)
        {
            return batch.Items.Select(i => new Row(batch, i)).OrderBy(r => r.Rank).ToList();
        }

        /// <summary>路径最后一段。日志内容不可信，不用会对非法字符抛异常的 Path.GetFileName。</summary>
        internal static string DisplayName(string path)
        {
            if (string.IsNullOrEmpty(path)) return "（未知来源）";
            string trimmed = path.TrimEnd('\\', '/');
            int cut = trimmed.LastIndexOfAny(new char[] { '\\', '/' });
            string name = cut >= 0 ? trimmed.Substring(cut + 1) : trimmed;
            return name.Length == 0 ? path : name;
        }

        internal static string Title(ImportBatch batch)
        {
            string category = string.IsNullOrEmpty(batch.Category) ? "未指定分类" : batch.Category;
            return string.IsNullOrEmpty(batch.Collection) ? category : category + " › " + batch.Collection;
        }

        /// <summary>批次级的错误说明和处理建议；都没有时返回空串。</summary>
        internal static string NoticeText(ImportBatch batch)
        {
            string hint = batch.Items.Any(i => i.State == FileImport.DeleteFailed)
                ? "可重试删除已入库项的源文件；复制失败项需重新扫描或重新导入。"
                : batch.Items.Any(i => i.State == FileImport.Failed)
                    ? "复制失败项需重新扫描或重新导入；本记录不能重试复制。" : string.Empty;
            string error = (batch.Error ?? string.Empty).Trim();
            if (error.Length == 0) return hint;
            return hint.Length == 0 ? error : error + "\r\n" + hint;
        }

        /// <summary>左侧记录里的时间：当天只写时刻，当年省略年份。</summary>
        internal static string ListTime(DateTime time, DateTime now)
        {
            if (time.Date == now.Date) return "今天 " + time.ToString("HH:mm");
            return time.Year == now.Year ? time.ToString("MM-dd HH:mm") : time.ToString("yyyy-MM-dd");
        }

        private static string OperationText(ImportBatch batch, bool full)
        {
            if (batch.Operation == ImportOperation.Move) return full ? "移入资源库" : "移入";
            if (batch.Operation == ImportOperation.Copy) return full ? "复制到资源库" : "复制";
            return "导入";
        }

        // ===== 读取 =====

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (closed) return;
            string directory = journal;
            // 日志可能很多、很大：后台读取并整理，界面只接收结果。
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<Record> loaded = new List<Record>();
                int unreadable = 0;
                bool failed = false;
                try
                {
                    if (Directory.Exists(directory))
                        foreach (FileInfo file in new DirectoryInfo(directory).GetFiles("*.xml").OrderByDescending(f => f.LastWriteTimeUtc)
                            .Where((f, index) => index < 100 || !File.Exists(Path.ChangeExtension(f.FullName, ".complete"))))
                        {
                            if (closed) return;
                            try { loaded.Add(new Record(FileImport.Load(file.FullName), file.LastWriteTime)); }
                            catch (Exception ex) { unreadable++; Paths.LogError(ex); }
                        }
                }
                catch (Exception ex) { failed = true; Paths.LogError(ex); }
                try
                {
                    BeginInvoke((Action)delegate { if (!closed && !IsDisposed) ShowLoaded(loaded, unreadable, failed); });
                }
                catch (InvalidOperationException) { }   // 窗口已关闭
            });
        }

        private void ShowLoaded(List<Record> loaded, int unreadable, bool failed)
        {
            int attention = loaded.Count(r => r.Status != ImportBatchStatus.Done);
            headerSummary = loaded.Count == 0 ? string.Empty : "共 " + loaded.Count + " 次导入"
                + (attention > 0 ? " · " + attention + " 次需留意" : string.Empty);
            if (unreadable > 0) headerSummary += (headerSummary.Length > 0 ? " · " : string.Empty) + unreadable + " 条记录无法读取";
            placeholder = failed && loaded.Count == 0 ? "读取导入记录失败，请稍后再试。"
                : loaded.Count == 0 ? (unreadable > 0 ? "导入记录无法读取。" : "暂无导入记录") : null;
            ApplyVisibility();
            if (placeholder == null)
            {
                batchList.Items.AddRange(loaded.ToArray());
                batchList.SelectedIndex = 0;   // 触发 ShowRecord
                batchList.Focus();
            }
            Invalidate();
        }

        private void ShowRecord(Record record)
        {
            current = record;
            FillRows();
            notice.Text = record == null ? string.Empty : NoticeText(record.Batch);
            bool retryable = record != null && MainForm.CanRetryDelete(record.Batch);
            bool busy = retryable && importBusy();
            retry.Visible = retryable && !busy;
            footer.Text = !retryable ? string.Empty
                : busy ? "正在导入，结束后重新打开导入记录即可清理原件。"
                : "只删除已成功入库的原文件，删除前会再次校验内容；不进入回收站。";
            ApplyVisibility();
            LayoutContent();
            Invalidate();
        }

        private void FillRows()
        {
            tipIndex = -1;
            tip.SetToolTip(itemList, null);
            shownRows = 0;
            itemList.BeginUpdate();
            try
            {
                itemList.Items.Clear();
                if (current != null) AppendRows();
            }
            finally { itemList.EndUpdate(); }
            itemList.ResetHover();
        }

        /// <summary>再列出一页明细；后面还有时在末尾放一行「显示更多」。调用方负责 BeginUpdate / EndUpdate。</summary>
        private void AppendRows()
        {
            int count = Math.Min(PageRows, current.Rows.Count - shownRows);
            if (count > 0) itemList.Items.AddRange(current.Rows.GetRange(shownRows, count).ToArray());
            shownRows += count;
            if (shownRows < current.Rows.Count) itemList.Items.Add(MoreRows);
        }

        /// <summary>点「显示更多」：去掉该行，接着列出下一页；滚动位置不变，选中新列出的第一行。</summary>
        private void ShowMoreRows()
        {
            int last = itemList.Items.Count - 1;
            if (current == null || last < 0 || itemList.Items[last] is Row) return;
            int top = itemList.TopIndex;
            tipIndex = -1;
            tip.SetToolTip(itemList, null);
            itemList.BeginUpdate();
            try
            {
                itemList.Items.RemoveAt(last);
                AppendRows();
            }
            finally { itemList.EndUpdate(); }
            itemList.ResetHover();
            itemList.SelectedIndex = last;
            itemList.TopIndex = top;
            Invalidate();   // 标题下的“已列出前 N 项”
        }

        private void RequestRetry()
        {
            if (current == null || !retry.Visible) return;
            ImportBatch batch = current.Batch;
            if (!batch.DeleteAuthorized)
            {
                // 部分失败的批次当时保留了全部源文件；需用户明确确认后才删除已入库项的源文件。
                if (MessageBox.Show(this, "该批次当时有失败项，因此保留了全部源文件。\n确定删除已成功入库项目的源文件吗？删除前会再次校验内容。",
                    "导入记录", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                batch.DeleteAuthorized = true;
            }
            RetryBatch = batch;
            Close();
        }

        // ===== 布局 =====

        private void ApplyVisibility()
        {
            bool ready = placeholder == null;
            batchList.Visible = ready;
            itemList.Visible = ready && current != null && current.Rows.Count > 0;
            notice.Visible = ready && notice.Text.Length > 0;
            footer.Visible = ready;
            if (!ready) retry.Visible = false;
        }

        private void LayoutContent()
        {
            if (layingOut) return;
            layingOut = true;
            try
            {
                int width = ClientSize.Width, height = ClientSize.Height, pad = Dpi(24);
                int top = Dpi(82), bottom = Math.Max(top + 1, height - Dpi(72));
                leftFrame = Rectangle.FromLTRB(pad, top, pad + Dpi(288), bottom);
                rightFrame = Rectangle.FromLTRB(leftFrame.Right + Dpi(12), top, Math.Max(leftFrame.Right + Dpi(13), width - pad), bottom);
                batchList.Bounds = Rectangle.Inflate(leftFrame, -Dpi(4), -Dpi(4));

                int innerX = rightFrame.X + Dpi(16), innerWidth = Math.Max(1, rightFrame.Width - Dpi(32));
                tilesRect = new Rectangle(innerX, rightFrame.Y + Dpi(66), innerWidth, Dpi(54));
                int y = tilesRect.Bottom + Dpi(10);
                noticeRect = Rectangle.Empty;
                if (notice.Text.Length > 0)
                {
                    int textWidth = Math.Max(1, innerWidth - Dpi(24));
                    TextFormatFlags flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;
                    int line = TextRenderer.MeasureText("国", notice.Font, new Size(textWidth, int.MaxValue), flags).Height;
                    int textHeight = Math.Min(line * 3,
                        TextRenderer.MeasureText(notice.Text, notice.Font, new Size(textWidth, int.MaxValue), flags).Height);
                    noticeRect = new Rectangle(innerX, y, innerWidth, textHeight + Dpi(14));
                    notice.Bounds = new Rectangle(innerX + Dpi(12), y + Dpi(7), textWidth, textHeight);
                    y = noticeRect.Bottom + Dpi(8);
                }
                itemList.Bounds = Rectangle.FromLTRB(rightFrame.X + Dpi(4), y, rightFrame.Right - Dpi(4),
                    Math.Max(y + 1, rightFrame.Bottom - Dpi(4)));

                int buttonTop = height - Dpi(56);
                close.Bounds = new Rectangle(width - pad - Dpi(90), buttonTop, Dpi(90), Dpi(34));
                retry.Bounds = new Rectangle(close.Left - Dpi(12) - Dpi(196), buttonTop, Dpi(196), Dpi(34));
                int footerRight = retry.Visible ? retry.Left : close.Left;
                footer.Bounds = new Rectangle(pad, buttonTop, Math.Max(1, footerRight - Dpi(12) - pad), Dpi(34));
            }
            finally { layingOut = false; }
        }

        protected override void OnDpiScaleChanged()
        {
            base.OnDpiScaleChanged();
            Rectangle work = Screen.FromControl(this).WorkingArea;
            Size = new Size(Math.Min(Width, work.Width), Math.Min(Height, work.Height));
            // 窗体按 DPI 放大时左上角不动，大窗口会明显偏向右下：重新对准宿主窗口中央，且不超出屏幕工作区
            Rectangle host = Owner != null ? Owner.Bounds : work;
            Location = new Point(Math.Max(work.Left, Math.Min(host.X + (host.Width - Width) / 2, work.Right - Width)),
                Math.Max(work.Top, Math.Min(host.Y + (host.Height - Height) / 2, work.Bottom - Height)));
            batchList.ItemHeight = Dpi(BatchRowHeight);   // 行高不随窗体缩放自动变化
            LayoutContent();
            FillRows();                                   // 明细行高按新的 DPI 重新计算
            Invalidate(true);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (!DpiChangeInProgress) LayoutContent();
        }

        // ===== 绘制 =====

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int pad = Dpi(24);
            using (SolidBrush primary = new SolidBrush(Theme.TextPrimary))
            using (SolidBrush secondary = new SolidBrush(Theme.TextSecondary))
            using (SolidBrush tertiary = new SolidBrush(Theme.TextTertiary))
            {
                Draw.DrawStringLeft(g, "导入记录", Theme.DialogTitleFont, primary, new Rectangle(pad, Dpi(18), Dpi(300), Dpi(28)));
                Draw.DrawStringLeft(g, "这里记着最近的导入；原件还没清理完的，可以在这里处理。", Theme.CaptionFont, secondary,
                    new Rectangle(pad, Dpi(48), ClientSize.Width - pad * 2, Dpi(22)));
                Draw.DrawStringRight(g, headerSummary, Theme.CaptionFont, secondary,
                    new Rectangle(ClientSize.Width - pad - Dpi(420), Dpi(20), Dpi(420), Dpi(24)));

                if (placeholder != null)
                {
                    Rectangle whole = Rectangle.FromLTRB(leftFrame.X, leftFrame.Y, rightFrame.Right, leftFrame.Bottom);
                    DrawCard(g, whole);
                    Draw.DrawStringCenter(g, placeholder, Theme.BodyFont, tertiary, whole);
                    return;
                }
                DrawCard(g, leftFrame);
                DrawCard(g, rightFrame);
                if (current == null) return;

                int x = rightFrame.X + Dpi(16), width = rightFrame.Width - Dpi(32);
                Font titleFont = Theme.FontOf(11F, FontStyle.Bold);
                string title = Title(current.Batch), status = StatusText(current.Status);
                int chipWidth = ChipWidth(g, status);
                int titleWidth = Math.Max(1, Math.Min((int)Math.Ceiling(g.MeasureString(title, titleFont).Width) + Dpi(2),
                    width - chipWidth - Dpi(8)));
                Draw.DrawStringLeft(g, title, titleFont, primary, new Rectangle(x, rightFrame.Y + Dpi(12), titleWidth, Dpi(24)));
                DrawChip(g, status, ToneOf(current.Status),
                    new Rectangle(x + titleWidth + Dpi(8), rightFrame.Y + Dpi(14), chipWidth, Dpi(20)));
                int total = current.Batch.Items.Count;
                string subtitle = OperationText(current.Batch, true) + " · 共 " + total + " 项"
                    + (shownRows < total ? "，已列出前 " + shownRows + " 项（有问题的排在前面）" : string.Empty)
                    + " · 更新于 " + current.Time.ToString("yyyy-MM-dd HH:mm");
                Draw.DrawStringLeft(g, subtitle, Theme.CaptionFont, secondary, new Rectangle(x, rightFrame.Y + Dpi(38), width, Dpi(20)));

                DrawTiles(g, primary, secondary);
                if (!noticeRect.IsEmpty)
                    using (SolidBrush fill = new SolidBrush(WarnBg)) Draw.FillRoundedRect(g, fill, noticeRect, Dpi(6));
                if (!itemList.Visible)
                    Draw.DrawStringCenter(g, "这次导入没有文件明细。", Theme.BodyFont, tertiary,
                        Rectangle.FromLTRB(rightFrame.X, tilesRect.Bottom, rightFrame.Right, rightFrame.Bottom));
            }
        }

        private void DrawCard(Graphics g, Rectangle frame)
        {
            using (SolidBrush fill = new SolidBrush(Theme.Card)) Draw.FillRoundedRect(g, fill, frame, Dpi(8));
            using (Pen border = new Pen(Theme.ControlBorder, 1)) Draw.DrawRoundedRect(g, border, frame, Dpi(8));
        }

        /// <summary>四个数字小卡片；失败和原件待清理大于零时换成提醒色。</summary>
        private void DrawTiles(Graphics g, Brush primary, Brush secondary)
        {
            string[] labels = { current.Batch.Committed ? "已入库" : "已复制", "已收录 / 跳过",
                current.HasRolledBack ? "失败 / 撤回" : "失败", "原件待清理" };
            int[] values = { current.Stored, current.Existing, current.Failed, current.Cleanup };
            int gap = Dpi(8), tileWidth = (tilesRect.Width - gap * 3) / 4, inset = Dpi(12);
            if (tileWidth <= inset * 2) return;
            for (int i = 0; i < 4; i++)
            {
                Rectangle tile = new Rectangle(tilesRect.X + i * (tileWidth + gap), tilesRect.Y, tileWidth, tilesRect.Height);
                using (SolidBrush fill = new SolidBrush(Theme.Background)) Draw.FillRoundedRect(g, fill, tile, Dpi(8));
                Color accent = values[i] == 0 ? Theme.TextPrimary : i == 2 ? BadText : i == 3 ? WarnText : Theme.TextPrimary;
                using (SolidBrush value = new SolidBrush(accent))
                    Draw.DrawStringLeft(g, values[i].ToString(), Theme.FontOf(13F, FontStyle.Bold), value,
                        new Rectangle(tile.X + inset, tile.Y + Dpi(5), tileWidth - inset * 2, Dpi(26)));
                Draw.DrawStringLeft(g, labels[i], Theme.CaptionFont, secondary,
                    new Rectangle(tile.X + inset, tile.Y + Dpi(30), tileWidth - inset * 2, Dpi(18)));
            }
        }

        private int ChipWidth(Graphics g, string text)
        {
            return (int)Math.Ceiling(g.MeasureString(text, Theme.FontOf(8.5F)).Width) + Dpi(10);
        }

        private void DrawChip(Graphics g, string text, ImportTone tone, Rectangle bounds)
        {
            Color back = tone == ImportTone.Good ? GoodBg : tone == ImportTone.Warn ? WarnBg : tone == ImportTone.Bad ? BadBg : Theme.ChipBg;
            Color fore = tone == ImportTone.Good ? GoodText : tone == ImportTone.Warn ? WarnText : tone == ImportTone.Bad ? BadText : Theme.TextSecondary;
            using (SolidBrush fill = new SolidBrush(back)) Draw.FillRoundedRect(g, fill, bounds, Dpi(4));
            using (SolidBrush brush = new SolidBrush(fore)) Draw.DrawStringCenter(g, text, Theme.FontOf(8.5F), brush, bounds);
        }

        /// <summary>行底色与悬停/选中高亮；整行先画到缓冲再贴上去，重绘不闪。</summary>
        private void DrawRowBackground(Graphics g, Rectangle row, bool selected, bool hover)
        {
            // 底色先按整像素铺满，再开抗锯齿：否则行的上、左边缘会半透明地透出缓冲区的黑底
            using (SolidBrush back = new SolidBrush(Theme.Card)) g.FillRectangle(back, row);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (!selected && !hover) return;
            Rectangle highlight = new Rectangle(row.X + Dpi(2), row.Y + 1, row.Width - Dpi(4), row.Height - 2);
            using (SolidBrush brush = new SolidBrush(selected ? Theme.SelectionBg : Theme.RowHover))
                Draw.FillRoundedRect(g, brush, highlight, Dpi(6));
        }

        private void DrawBatch(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= batchList.Items.Count) return;
            Record record = (Record)batchList.Items[e.Index];
            Rectangle row = e.Bounds;
            if (row.Width <= 0 || row.Height <= 0) return;
            using (BufferedGraphics buffer = BufferedGraphicsManager.Current.Allocate(e.Graphics, row))
            {
                Graphics g = buffer.Graphics;
                DrawRowBackground(g, row, e.Index == batchList.SelectedIndex, e.Index == batchList.HoverIndex);
                string status = StatusText(record.Status);
                int chipWidth = ChipWidth(g, status), left = row.X + Dpi(12);
                Rectangle chip = new Rectangle(row.Right - Dpi(12) - chipWidth, row.Y + Dpi(9), chipWidth, Dpi(20));
                DrawChip(g, status, ToneOf(record.Status), chip);
                using (SolidBrush brush = new SolidBrush(Theme.TextPrimary))
                    Draw.DrawStringLeft(g, Title(record.Batch), Theme.ButtonFont, brush,
                        new Rectangle(left, row.Y + Dpi(7), Math.Max(1, chip.X - Dpi(8) - left), Dpi(24)));
                using (SolidBrush brush = new SolidBrush(Theme.TextSecondary))
                    Draw.DrawStringLeft(g, ListTime(record.Time, DateTime.Now) + " · " + OperationText(record.Batch, false)
                        + " " + record.Batch.Items.Count + " 项", Theme.CaptionFont, brush,
                        new Rectangle(left, row.Y + Dpi(31), Math.Max(1, row.Width - Dpi(24)), Dpi(20)));
                buffer.Render(e.Graphics);
            }
        }

        /// <summary>明细行高（96 DPI 设计值再换算）：状态+文件名、来源、目标（有才占行）、错误说明（有才占行）。</summary>
        private int RowHeightOf(Row row)
        {
            int height = 8 + 22 + 2 + 18 + 8;
            if (!string.IsNullOrEmpty(row.Item.Target)) height += 18;
            if (row.ErrorLines > 0) height += 2 + 18 * row.ErrorLines;
            return Math.Min(255, Dpi(height));   // 系统列表单行高度上限 255
        }

        private void DrawRow(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= itemList.Items.Count) return;
            Row item = itemList.Items[e.Index] as Row;
            Rectangle row = e.Bounds;
            if (row.Width <= 0 || row.Height <= 0) return;
            using (BufferedGraphics buffer = BufferedGraphicsManager.Current.Allocate(e.Graphics, row))
            {
                Graphics g = buffer.Graphics;
                bool active = e.Index == itemList.SelectedIndex || e.Index == itemList.HoverIndex;
                DrawRowBackground(g, row, e.Index == itemList.SelectedIndex, e.Index == itemList.HoverIndex);
                if (item == null)
                {
                    int rest = current == null ? 0 : current.Rows.Count - shownRows;
                    using (SolidBrush brush = new SolidBrush(Theme.AccentText))
                        Draw.DrawStringCenter(g, "还有 " + rest + " 项没列出，点这里再显示 " + Math.Min(PageRows, rest) + " 项",
                            Theme.CaptionFont, brush, row);
                    buffer.Render(e.Graphics);
                    return;
                }
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                int left = row.X + Dpi(12), right = row.Right - Dpi(12), y = row.Y + Dpi(8);

                int chipWidth = ChipWidth(g, item.Label);
                DrawChip(g, item.Label, item.Tone, new Rectangle(left, y + Dpi(1), chipWidth, Dpi(20)));
                using (SolidBrush brush = new SolidBrush(Theme.TextPrimary))
                    Draw.DrawStringLeft(g, item.Name, Theme.BodyFont, brush,
                        new Rectangle(left + chipWidth + Dpi(8), y, Math.Max(1, right - left - chipWidth - Dpi(8)), Dpi(22)));
                y += Dpi(24);

                using (SolidBrush label = new SolidBrush(Theme.TextTertiary))
                using (SolidBrush path = new SolidBrush(Theme.TextSecondary))
                {
                    Rectangle text = new Rectangle(left + Dpi(22), y, Math.Max(1, right - left - Dpi(22)), Dpi(18));
                    Draw.DrawStringLeft(g, "从", Theme.CaptionFont, label, new Rectangle(left, y, Dpi(22), Dpi(18)));
                    g.DrawString(string.IsNullOrEmpty(item.Item.Source) ? "—" : item.Item.Source, Theme.CaptionFont, path, text, PathFormat);
                    y += Dpi(18);
                    if (!string.IsNullOrEmpty(item.Item.Target))
                    {
                        text.Y = y;
                        Draw.DrawStringLeft(g, "到", Theme.CaptionFont, label, new Rectangle(left, y, Dpi(22), Dpi(18)));
                        g.DrawString(item.Item.Target, Theme.CaptionFont, path, text, PathFormat);
                        y += Dpi(18);
                    }
                }
                if (item.ErrorLines > 0)
                    using (SolidBrush brush = new SolidBrush(BadText))
                        g.DrawString(item.Item.Error.Replace("\r", string.Empty).Replace('\n', ' '), Theme.CaptionFont, brush,
                            new RectangleF(left, y + Dpi(2), Math.Max(1, right - left), Dpi(18) * item.ErrorLines), ErrorFormat);

                if (!active && e.Index < itemList.Items.Count - 1)
                    using (Pen line = new Pen(Theme.Separator, 1))
                    {
                        g.SmoothingMode = SmoothingMode.None;
                        g.DrawLine(line, left, row.Bottom - 1, right, row.Bottom - 1);
                    }
                buffer.Render(e.Graphics);
            }
        }

        // ===== 明细行的提示、复制 =====

        /// <summary>鼠标所在行有内容被省略时，悬停显示完整的来源、目标和错误说明。</summary>
        private void UpdateTip(int index)
        {
            if (index == tipIndex) return;
            tipIndex = index;
            string text = null;
            if (index >= 0 && index < itemList.Items.Count)
            {
                Row row = itemList.Items[index] as Row;
                int room = itemList.ClientSize.Width - Dpi(12 + 22 + 12);
                if (row != null && (Wider(row.Item.Source, room) || Wider(row.Item.Target, room) || Wider(row.Name, room - Dpi(110))
                    || row.ErrorLines > 1 || Wider(row.Item.Error, room + Dpi(22))))
                    text = RowText(row);
            }
            tip.SetToolTip(itemList, text);
        }

        private static bool Wider(string text, int width)
        {
            return !string.IsNullOrEmpty(text)
                && TextRenderer.MeasureText(text, Theme.CaptionFont, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width > width;
        }

        /// <summary>一行明细的完整文字：状态、来源、目标、错误说明。</summary>
        internal static string RowText(Row row)
        {
            return row.Label + "：" + row.Item.Source
                + (string.IsNullOrEmpty(row.Item.Target) ? string.Empty : "\r\n目标：" + row.Item.Target)
                + (string.IsNullOrEmpty(row.Item.Error) ? string.Empty : "\r\n" + row.Item.Error);
        }

        private void ShowRowMenu(Row row)
        {
            List<MenuWindow.MenuItem> items = new List<MenuWindow.MenuItem>();
            items.Add(CopyEntry("复制来源路径", row.Item.Source));
            items.Add(CopyEntry("复制目标路径", row.Item.Target));
            if (!string.IsNullOrEmpty(row.Item.Error)) items.Add(CopyEntry("复制错误信息", row.Item.Error));
            MenuWindow menu = new MenuWindow(items, 170);
            Point point = Cursor.Position;
            Rectangle work = Screen.GetWorkingArea(point);
            menu.Location = new Point(Math.Max(work.Left, Math.Min(point.X, work.Right - menu.Width)),
                Math.Max(work.Top, Math.Min(point.Y, work.Bottom - menu.Height)));
            menu.Show(this);
        }

        private MenuWindow.MenuItem CopyEntry(string caption, string value)
        {
            MenuWindow.MenuItem entry = new MenuWindow.MenuItem();
            entry.Text = caption;
            entry.Disabled = string.IsNullOrEmpty(value);
            entry.Action = delegate { CopyText(value); };
            return entry;
        }

        private void CopyText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try { Clipboard.SetText(text); }
            catch (ExternalException)
            {
                MessageBox.Show(this, "剪贴板正被其它程序占用，没有复制成功，请再试一次。", "导入记录",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            if (keyData == Keys.Enter)
            {
                // 自绘按钮不是系统默认按钮：焦点在哪个按钮上，回车就执行哪个；清理原件必须明确选中按钮。
                if (retry.Focused) RequestRetry();
                else if (close.Focused) Close();
                else if (itemList.Focused && itemList.SelectedIndex >= 0 && !(itemList.SelectedItem is Row)) ShowMoreRows();
                return true;
            }
            if (keyData == (Keys.Control | Keys.C) && itemList.Focused && itemList.SelectedItem is Row)
            {
                CopyText(RowText((Row)itemList.SelectedItem));
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            closed = true;
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            closed = true;
            if (disposing) tip.Dispose();
            base.Dispose(disposing);
        }
    }
}

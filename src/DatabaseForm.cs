using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using AppManager.Controls;

namespace AppManager
{
    /// <summary>
    /// 「数据维护与操作记录」窗口：左边按时间列出程序对分类、集合和设置做过的每次改动，右边是选中那次的说明、数量和逐处明细。
    /// 完整备份、从备份恢复、检查数据库收在右上角的「数据库维护」菜单里；只有最新的一次业务操作提供「撤销」。
    /// 读取和维护都在后台线程执行；撤销或恢复成功后 Changed 为 true，由主窗口重新加载。
    /// </summary>
    internal sealed class DatabaseForm : BufferedDialogForm
    {
        /// <summary>左侧每次列出的操作数、右侧每次列出的改动数；后面还有时在末尾放一行「显示更多」。</summary>
        internal const int PageOps = 200, PageRows = 300;
        private static readonly string MoreOps = "显示更早的记录", MoreRows = "显示更多";
        private const int OpRowHeight = 58, MenuWidth = 176;

        private static readonly Color GoodText = Theme.SuccessText;
        private static readonly Color GoodBg = Theme.SuccessBg;
        private static readonly Color WarnText = Theme.WarningText;
        private static readonly Color WarnBg = Theme.WarningBg;
        private static readonly Color BadText = Theme.DangerText;
        private static readonly Color BadBg = Theme.DangerBg;

        // 仅界面线程使用的共享绘制格式：说明文字放不下时在末尾省略
        private static readonly StringFormat DetailFormat = new StringFormat
        {
            LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap
        };

        /// <summary>左侧的一次操作。</summary>
        internal sealed class Op
        {
            internal long Id, Reverts;
            internal DateTime Time;
            internal bool TimeKnown;
            internal string Action = string.Empty, Summary = string.Empty, Title = string.Empty, Line = string.Empty;
            /// <summary>已被后面的一次撤销退回（再撤销那次撤销则恢复生效）。</summary>
            internal bool Reverted;

            public override string ToString() { return Title + "，" + Line; }
        }

        private readonly ConfigDatabase database;
        private readonly SoftwarePickerDialog.PickerList opList = new SoftwarePickerDialog.PickerList();
        private readonly SoftwarePickerDialog.PickerList changeList = new SoftwarePickerDialog.PickerList();
        private readonly Label notice = new Label();
        private readonly Label status = new Label();
        private readonly Label filter = new Label();
        private readonly RoundButton maintain = new RoundButton();
        private readonly RoundButton undo = new RoundButton();
        private readonly RoundButton export = new RoundButton();
        private readonly RoundButton close = new RoundButton();
        private readonly ThemedToolTip tip = new ThemedToolTip();
        private readonly List<Op> ops = new List<Op>();

        private Rectangle leftFrame, rightFrame, tilesRect, noticeRect;   // 已按当前 DPI 换算
        private string placeholder = "正在读取操作记录…";
        private string detailText = "正在读取明细…";   // 右侧明细区的占位文字；有明细可看时为 null
        private string headerSummary = string.Empty;
        private Op current;
        private long latestBusiness;      // 当前唯一可能被撤销的操作编号
        private bool showSystem, moreOps;
        private int added = -1, modified, removed;   // added 为 -1 表示数量还没读到
        private int shownRows;            // 已列出的改动数（不含「显示更多」行）
        private long lastChangeId;        // 已列出的最后一处改动的编号，下一页从它之后读
        private int historyVersion, detailVersion;   // 每发起一次读取加一：过期的后台结果直接丢弃
        private int tipIndex = -1;
        private bool running, layingOut, readFailed;
        private volatile bool closed;

        /// <summary>撤销或恢复成功：数据库内容已变，主窗口需要重新加载。</summary>
        internal bool Changed;

        internal DatabaseForm(ConfigDatabase store)
        {
            database = store;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(960, 640);
            BackColor = Theme.Background;
            ShowInTaskbar = false;
            Text = "数据维护与操作记录";

            opList.ItemHeight = OpRowHeight;
            opList.AccessibleName = "操作记录";
            opList.DrawItem += DrawOp;
            opList.SelectedIndexChanged += delegate { Op op = opList.SelectedItem as Op; if (op != null && op != current) ShowOp(op); };
            opList.MouseUp += delegate(object s, MouseEventArgs e)
            {
                int index = opList.HitRow(e.Location);
                if (e.Button == MouseButtons.Left && index >= 0 && !(opList.Items[index] is Op)) LoadHistory(true);
            };
            Controls.Add(opList);

            changeList.DrawMode = DrawMode.OwnerDrawVariable;
            changeList.AccessibleName = "改动明细";
            changeList.MeasureItem += delegate(object s, MeasureItemEventArgs e)
            {
                if (e.Index < 0 || e.Index >= changeList.Items.Count) return;
                ChangeRow row = changeList.Items[e.Index] as ChangeRow;
                e.ItemHeight = row == null ? Dpi(44) : RowHeightOf(row);
            };
            changeList.DrawItem += DrawRow;
            changeList.MouseMove += delegate(object s, MouseEventArgs e) { UpdateTip(changeList.HitRow(e.Location)); };
            changeList.MouseLeave += delegate { UpdateTip(-1); };
            changeList.MouseUp += delegate(object s, MouseEventArgs e)
            {
                int index = changeList.HitRow(e.Location);
                if (index < 0) return;
                ChangeRow row = changeList.Items[index] as ChangeRow;
                if (row == null) { if (e.Button == MouseButtons.Left) LoadDetails(true); return; }
                if (e.Button != MouseButtons.Right) return;
                changeList.SelectedIndex = index;
                ShowRowMenu(row);
            };
            Controls.Add(changeList);

            // 这次操作能不能撤销、为什么：放不下时省略，鼠标停留显示全文（Label 自带）
            notice.Font = Theme.CaptionFont;
            notice.AutoSize = false;
            notice.AutoEllipsis = true;
            notice.UseMnemonic = false;
            Controls.Add(notice);

            filter.Font = Theme.CaptionFont;
            filter.ForeColor = Theme.AccentText;
            filter.AutoSize = false;
            filter.UseMnemonic = false;
            filter.TextAlign = ContentAlignment.MiddleLeft;
            filter.Cursor = Cursors.Hand;
            filter.Click += delegate { if (running) return; showSystem = !showSystem; LoadHistory(false); };
            Controls.Add(filter);

            status.Font = Theme.CaptionFont;
            status.ForeColor = Theme.TextSecondary;
            status.AutoSize = false;
            status.AutoEllipsis = true;
            status.UseMnemonic = false;
            status.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(status);

            maintain.Text = "数据库维护 ▾";
            maintain.Click += delegate { if (!running) ShowMaintainMenu(); };
            Controls.Add(maintain);

            undo.Text = "撤销这次操作";
            undo.Visible = false;
            undo.Click += delegate { RequestUndo(); };
            Controls.Add(undo);

            export.Text = "导出明细";
            export.Visible = false;
            export.Click += delegate { Export(); };
            Controls.Add(export);

            close.Text = "关闭";
            close.Click += delegate { Close(); };
            Controls.Add(close);

            tip.AutoPopDelay = 20000;
            ApplyVisibility();
            LayoutContent();
            ActiveControl = close;
        }

        // ===== 记录的整理（纯逻辑，internal 供回归测试） =====

        /// <summary>把数据库返回的行（编号、UTC 时间、内部名称、摘要、被撤销的操作编号）整理成 Op；格式不对的行跳过。</summary>
        internal static List<Op> ReadOps(List<string[]> rows)
        {
            List<Op> result = new List<Op>();
            foreach (string[] row in rows)
            {
                Op op = new Op();
                if (row.Length < 5 || !long.TryParse(row[0], out op.Id)) continue;
                DateTime utc;
                op.TimeKnown = DateTime.TryParse(row[1], System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out utc);
                if (op.TimeKnown) op.Time = utc.ToLocalTime();
                op.Action = row[2] ?? string.Empty;
                op.Summary = row[3] ?? string.Empty;
                long.TryParse(row[4], out op.Reverts);
                op.Title = OperationHistory.Title(op.Action);
                result.Add(op);
            }
            return result;
        }

        /// <summary>
        /// 标出已被撤销的操作并写好每行的说明。撤销本身也是一条记录：撤销 A 后 A 标为已撤销；
        /// 再撤销那次撤销，A 重新生效、那次撤销标为已撤销，依此交替。列表按编号从新到旧。
        /// </summary>
        internal static void Annotate(List<Op> list)
        {
            Dictionary<long, Op> byId = new Dictionary<long, Op>();
            foreach (Op op in list) { byId[op.Id] = op; op.Reverted = false; }
            for (int i = list.Count - 1; i >= 0; i--)
            {
                bool reverted = true;
                long target = list[i].Reverts;
                Op op;
                // 编号只会指向更早的记录，链条必然终止；数据异常时最多走完已载入的条数
                for (int guard = 0; target > 0 && guard < list.Count && byId.TryGetValue(target, out op); guard++)
                {
                    op.Reverted = reverted;
                    reverted = !reverted;
                    target = op.Reverts;
                }
            }
            foreach (Op op in list)
                op.Line = OperationHistory.SummaryText(op.Action, op.Summary, op.Reverts, delegate(long id)
                {
                    Op target;
                    return byId.TryGetValue(id, out target) ? target.Title : null;
                });
        }

        /// <summary>这条记录现在能不能撤销：必须是最新的业务操作，且不是系统记录、数据升级、整库恢复或动过磁盘文件的操作。</summary>
        internal static bool CanUndo(Op op, long latest)
        {
            return op != null && op.Id == latest && !OperationHistory.IsSystem(op.Action) && OperationHistory.UndoBlock(op.Action) == null;
        }

        /// <summary>右侧的说明：能撤销时说明撤销的效果，不能时说明原因。</summary>
        internal static string NoticeText(Op op, long latest)
        {
            if (OperationHistory.IsSystem(op.Action)) return "扫描时自动产生的记录，不影响分类和集合，不需要撤销。";
            string block = OperationHistory.UndoBlock(op.Action);
            if (block != null) return block;
            if (op.Id != latest)
                return op.Reverted ? "这次操作已经被撤销。" : "只能撤销最新的一次操作；这条之后还有别的改动，不能单独撤销。";
            return op.Reverts > 0 ? "这是一次撤销。再撤销它，当时退回的改动会重新生效。"
                : "可以撤销：会把这次对分类、集合和设置的改动退回去，磁盘上的文件不受影响。";
        }

        /// <summary>记录的状态标签；左侧列表只在「可撤销」「已撤销」时显示。</summary>
        internal static string StatusText(Op op, long latest)
        {
            if (CanUndo(op, latest)) return "可撤销";
            if (op.Reverted) return "已撤销";
            return OperationHistory.IsSystem(op.Action) ? "系统记录" : "不可撤销";
        }

        // ===== 读取 =====

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (!closed) LoadHistory(false);
        }

        /// <summary>后台线程把结果交回界面线程；窗口已关闭时丢弃。</summary>
        private void Post(Action action)
        {
            if (closed) return;
            try { BeginInvoke((Action)delegate { if (!closed && !IsDisposed) action(); }); }
            catch (InvalidOperationException) { }   // 窗口已关闭
        }

        /// <summary>读取操作记录。append 为 true 时接着已列出的最后一条往更早读，否则从最新开始重新读。</summary>
        private void LoadHistory(bool append)
        {
            if (append && (!moreOps || ops.Count == 0)) return;
            int version = ++historyVersion;
            long before = append ? ops[ops.Count - 1].Id : 0;
            bool system = showSystem;
            if (!append && ops.Count == 0) { placeholder = "正在读取操作记录…"; ApplyVisibility(); Invalidate(); }
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<Op> page = null;
                long latest = 0;
                Exception error = null;
                try
                {
                    page = ReadOps(database.HistoryPage(before, PageOps + 1, system));
                    latest = database.LatestBusinessOperation();
                }
                catch (Exception ex) { error = ex; Paths.LogError(ex); }
                Post(delegate { if (version == historyVersion) ShowHistory(page, latest, append, error); });
            });
        }

        private void ShowHistory(List<Op> page, long latest, bool append, Exception error)
        {
            if (error != null)
            {
                // 读不出来时保留已列出的内容；「数据库维护」仍可用（检查、从备份恢复）
                if (ops.Count == 0) { placeholder = "读取操作记录失败：" + error.Message; ApplyVisibility(); Invalidate(); }
                else ShowReadError("读取操作记录失败：" + error.Message);
                return;
            }
            ClearReadError();
            long keep = current == null ? 0 : current.Id;
            int top = opList.TopIndex;
            if (!append) ops.Clear();
            moreOps = page.Count > PageOps;
            if (moreOps) page.RemoveAt(page.Count - 1);
            ops.AddRange(page);
            latestBusiness = latest;
            Annotate(ops);

            headerSummary = ops.Count == 0 ? string.Empty : (moreOps ? "已列出最近 " : "共 ") + ops.Count + " 次操作";
            filter.Text = showSystem ? "隐藏系统自动记录" : "显示系统自动记录";
            placeholder = ops.Count == 0 ? "还没有操作记录" : null;
            int select = 0;
            opList.BeginUpdate();
            try
            {
                opList.Items.Clear();
                opList.Items.AddRange(ops.ToArray());
                if (moreOps) opList.Items.Add(MoreOps);
            }
            finally { opList.EndUpdate(); }
            opList.ResetHover();
            for (int i = 0; i < ops.Count; i++) if (ops[i].Id == keep) select = i;
            if (ops.Count == 0) { ShowOp(null); return; }
            if (append && ops[select] == current)
            {
                // 只是往更早多列了一页：右侧已读出的明细原样保留，只更新撤销资格；列表不滚动
                opList.SelectedIndex = select;
                opList.TopIndex = top;
                ApplyUndoState();
            }
            else
            {
                current = null;   // 同一条记录也重新读：撤销资格和明细都可能变了
                ApplyVisibility();
                opList.SelectedIndex = select;   // 触发 ShowOp
            }
            if (!running) opList.Focus();
            Invalidate();
        }

        private void ShowOp(Op op)
        {
            current = op;
            added = -1; modified = 0; removed = 0; shownRows = 0; lastChangeId = 0;
            detailText = op == null ? null : "正在读取明细…";
            tipIndex = -1;
            tip.SetToolTip(changeList, null);
            changeList.Items.Clear();
            changeList.ResetHover();
            ApplyUndoState();
            if (op != null) LoadDetails(false);
            else detailVersion++;
        }

        /// <summary>按当前记录和最新的业务操作编号，更新说明文字和「撤销」「导出」按钮。</summary>
        private void ApplyUndoState()
        {
            bool undoable = CanUndo(current, latestBusiness);
            notice.Text = current == null ? string.Empty : NoticeText(current, latestBusiness);
            notice.ForeColor = undoable ? GoodText : Theme.TextSecondary;
            notice.BackColor = undoable ? GoodBg : Theme.ChipBg;
            undo.Visible = undoable;
            export.Visible = current != null;
            ApplyVisibility();
            LayoutContent();
            Invalidate();
        }

        /// <summary>读取失败的提示放在状态栏；下一次读取成功后清掉，不让过期的报错一直留着。</summary>
        private void ShowReadError(string text)
        {
            status.Text = text;
            readFailed = true;
        }

        private void ClearReadError()
        {
            if (!readFailed) return;
            readFailed = false;
            status.Text = string.Empty;
        }

        /// <summary>读取当前记录的一页改动明细；append 为 true 时接着已列出的往后读。</summary>
        private void LoadDetails(bool append)
        {
            if (current == null || (append && (shownRows == 0 || changeList.Items.Count == 0
                || changeList.Items[changeList.Items.Count - 1] is ChangeRow))) return;
            int version = ++detailVersion;
            long id = current.Id, after = append ? lastChangeId : 0;
            ThreadPool.QueueUserWorkItem(delegate
            {
                OperationDetails page = null;
                Exception error = null;
                try { page = database.ChangePage(id, after, PageRows, !append); }
                catch (Exception ex) { error = ex; Paths.LogError(ex); }
                Post(delegate { if (version == detailVersion && current != null && current.Id == id) ShowDetails(page, append, error); });
            });
        }

        private void ShowDetails(OperationDetails page, bool append, Exception error)
        {
            if (error != null)
            {
                if (append) ShowReadError("读取明细失败：" + error.Message);
                else detailText = "读取明细失败：" + error.Message;
                ApplyVisibility();
                Invalidate();
                return;
            }
            ClearReadError();
            if (!append) { added = page.Added; modified = page.Modified; removed = page.Removed; }
            int top = changeList.TopIndex, first = changeList.Items.Count - 1;
            tipIndex = -1;
            tip.SetToolTip(changeList, null);
            changeList.BeginUpdate();
            try
            {
                if (append && first >= 0) changeList.Items.RemoveAt(first);   // 去掉「显示更多」行
                changeList.Items.AddRange(page.Rows.ToArray());
                if (page.More) changeList.Items.Add(MoreRows);
            }
            finally { changeList.EndUpdate(); }
            changeList.ResetHover();
            shownRows += page.Rows.Count;
            if (page.Rows.Count > 0) lastChangeId = page.Rows[page.Rows.Count - 1].Id;
            detailText = shownRows == 0 ? "这次操作没有留下改动明细。" : null;
            ApplyVisibility();
            if (append && first >= 0 && first < changeList.Items.Count)
            {
                // 滚动位置不变，选中新列出的第一行
                changeList.SelectedIndex = first;
                changeList.TopIndex = top;
            }
            Invalidate();
        }

        // ===== 撤销与数据库维护 =====

        /// <summary>在后台执行一项维护操作；期间按钮和列表停用、窗口不可关闭。成功后在界面线程调用 done，失败则提示原因。</summary>
        private void Work(string busyText, Func<string> action, Action<string> done)
        {
            if (running) return;
            SetRunning(true);
            readFailed = false;
            status.Text = busyText;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string result = null;
                Exception error = null;
                try { result = action(); } catch (Exception ex) { error = ex; Paths.LogError(ex); }
                Post(delegate
                {
                    SetRunning(false);
                    if (error == null) { done(result); return; }
                    status.Text = "没有完成：" + error.Message;
                    MessageDialog.Show(this, "操作没有完成，数据保持原样。\r\n" + error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                });
            });
        }

        private void SetRunning(bool value)
        {
            running = value;
            maintain.Enabled = undo.Enabled = export.Enabled = close.Enabled = opList.Enabled = changeList.Enabled = !value;
        }

        private void RequestUndo()
        {
            Op op = current;
            if (running || !undo.Visible || !CanUndo(op, latestBusiness)) return;
            string when = op.TimeKnown ? "（" + ImportHistoryDialog.ListTime(op.Time, DateTime.Now) + "）" : string.Empty;
            if (!ConfirmDialog.Show("撤销操作", "撤销「" + op.Title + "」" + when + "？\r\n只退回分类、集合和设置的改动，不动磁盘上的文件。",
                "撤销", "取消")) return;
            StartUndo(op.Id);
        }

        private void StartUndo(long id)
        {
            Work("正在撤销…", delegate
            {
                DesktopOrganizer.RunExclusive(delegate { database.Undo(id); });
                return "已撤销。";
            }, delegate { Changed = true; Close(); });
        }

        private void Backup()
        {
            using (SaveFileDialog dialog = new SaveFileDialog { Title = "完整备份", Filter = "数据库备份 (*.db)|*.db",
                FileName = "AppManager-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".db", OverwritePrompt = true })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                string path = dialog.FileName;
                Work("正在备份…", delegate { database.Backup(path); return "已备份到：" + path; }, delegate(string text) { status.Text = text; });
            }
        }

        private void Restore()
        {
            using (OpenFileDialog dialog = new OpenFileDialog { Title = "从备份恢复", Filter = "数据库备份 (*.db)|*.db" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                string path = dialog.FileName;
                if (!ConfirmDialog.Show("从备份恢复", "用所选备份替换当前的分类、集合和设置？\r\n替换前会自动保留一份当前数据；磁盘上的文件不变。",
                    "恢复", "取消")) return;
                Work("正在恢复…", delegate
                {
                    return DesktopOrganizer.RunExclusive<string>(delegate { return database.Restore(path); });
                }, delegate(string safety)
                {
                    MessageDialog.Show(this, "恢复完成。\r\n恢复前的数据保留在：\r\n" + safety, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    Changed = true;
                    Close();
                });
            }
        }

        private void CheckDatabase()
        {
            Work("正在检查…", delegate { return database.Check(); }, delegate(string raw)
            {
                bool ok;
                status.Text = OperationHistory.CheckText(raw, out ok);
                if (!ok) MessageDialog.Show(this, status.Text, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            });
        }

        private void Export()
        {
            Op op = current;
            if (running || op == null) return;
            using (SaveFileDialog dialog = new SaveFileDialog { Title = "导出明细", Filter = "变更记录 (*.xml)|*.xml", FileName = "operation-" + op.Id + ".xml" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                string path = dialog.FileName;
                Work("正在导出…", delegate { database.ExportChanges(op.Id, path); return "明细已导出：" + path; }, delegate(string text) { status.Text = text; });
            }
        }

        private void ShowMaintainMenu()
        {
            List<MenuWindow.MenuItem> items = new List<MenuWindow.MenuItem>();
            items.Add(MenuEntry("完整备份…", Backup));
            items.Add(MenuEntry("从备份恢复…", Restore));
            items.Add(MenuEntry("检查数据库", CheckDatabase));
            items.Add(new MenuWindow.MenuItem { Separator = true });
            items.Add(MenuEntry("刷新记录", delegate { LoadHistory(false); }));
            MenuWindow menu = new MenuWindow(items, MenuWidth);
            // 菜单显示时才按 DPI 放大：这里先按放大后的宽度算位置，右边缘才能与按钮对齐
            int width = Dpi(MenuWidth);
            Rectangle anchor = maintain.RectangleToScreen(maintain.ClientRectangle);
            Point point = new Point(anchor.Right - width, anchor.Bottom + Dpi(4));
            Rectangle work = Screen.GetWorkingArea(point);
            menu.Location = new Point(Math.Max(work.Left, Math.Min(point.X, work.Right - width)),
                Math.Max(work.Top, Math.Min(point.Y, work.Bottom - menu.Height)));
            menu.Show(this);
        }

        /// <summary>菜单项的动作延后到菜单窗口关闭之后执行：那时本窗口已重新成为活动窗口，文件对话框和确认框才会以它为宿主。</summary>
        private MenuWindow.MenuItem MenuEntry(string text, Action action)
        {
            MenuWindow.MenuItem entry = new MenuWindow.MenuItem();
            entry.Text = text;
            entry.Action = delegate
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke((Action)delegate { if (!IsDisposed && !Disposing && !running) action(); });
            };
            return entry;
        }

        // ===== 布局 =====

        private void ApplyVisibility()
        {
            bool ready = placeholder == null;
            opList.Visible = ready;
            changeList.Visible = ready && current != null && detailText == null;
            notice.Visible = ready && notice.Text.Length > 0;
            if (!ready) { undo.Visible = false; export.Visible = false; }
        }

        private void LayoutContent()
        {
            if (layingOut) return;
            layingOut = true;
            try
            {
                int width = ClientSize.Width, height = ClientSize.Height, pad = Dpi(24);
                int top = Dpi(82), bottom = Math.Max(top + 1, height - Dpi(72));
                maintain.Bounds = new Rectangle(width - pad - Dpi(128), Dpi(18), Dpi(128), Dpi(34));
                leftFrame = Rectangle.FromLTRB(pad, top, pad + Dpi(300), bottom);
                rightFrame = Rectangle.FromLTRB(leftFrame.Right + Dpi(12), top, Math.Max(leftFrame.Right + Dpi(13), width - pad), bottom);
                opList.Bounds = Rectangle.Inflate(leftFrame, -Dpi(4), -Dpi(4));

                int innerX = rightFrame.X + Dpi(16), innerWidth = Math.Max(1, rightFrame.Width - Dpi(32));
                tilesRect = new Rectangle(innerX, rightFrame.Y + Dpi(66), innerWidth, Dpi(54));
                int y = tilesRect.Bottom + Dpi(10);
                noticeRect = Rectangle.Empty;
                if (notice.Text.Length > 0)
                {
                    int textWidth = Math.Max(1, innerWidth - Dpi(24));
                    TextFormatFlags flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;
                    int line = TextRenderer.MeasureText("国", notice.Font, new Size(textWidth, int.MaxValue), flags).Height;
                    int textHeight = Math.Min(line * 2,
                        TextRenderer.MeasureText(notice.Text, notice.Font, new Size(textWidth, int.MaxValue), flags).Height);
                    noticeRect = new Rectangle(innerX, y, innerWidth, textHeight + Dpi(14));
                    notice.Bounds = new Rectangle(innerX + Dpi(12), y + Dpi(7), textWidth, textHeight);
                    y = noticeRect.Bottom + Dpi(8);
                }
                changeList.Bounds = Rectangle.FromLTRB(rightFrame.X + Dpi(4), y, rightFrame.Right - Dpi(4),
                    Math.Max(y + 1, rightFrame.Bottom - Dpi(4)));

                int buttonTop = height - Dpi(56), right = width - pad;
                close.Bounds = new Rectangle(right - Dpi(90), buttonTop, Dpi(90), Dpi(34));
                right = close.Left - Dpi(12);
                if (export.Visible) { export.Bounds = new Rectangle(right - Dpi(100), buttonTop, Dpi(100), Dpi(34)); right = export.Left - Dpi(12); }
                if (undo.Visible) { undo.Bounds = new Rectangle(right - Dpi(128), buttonTop, Dpi(128), Dpi(34)); right = undo.Left - Dpi(12); }
                filter.Bounds = new Rectangle(pad, buttonTop, Dpi(132), Dpi(34));
                status.Bounds = new Rectangle(filter.Right + Dpi(12), buttonTop, Math.Max(1, right - filter.Right - Dpi(12)), Dpi(34));
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
            opList.ItemHeight = Dpi(OpRowHeight);   // 行高不随窗体缩放自动变化
            LayoutContent();
            // 明细行高按新的 DPI 重新计算：原样重新放入列表
            object[] rows = new object[changeList.Items.Count];
            changeList.Items.CopyTo(rows, 0);
            int selected = changeList.SelectedIndex;
            changeList.BeginUpdate();
            try { changeList.Items.Clear(); changeList.Items.AddRange(rows); }
            finally { changeList.EndUpdate(); }
            if (selected >= 0 && selected < changeList.Items.Count) changeList.SelectedIndex = selected;
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
                Draw.DrawStringLeft(g, "数据维护与操作记录", Theme.DialogTitleFont, primary, new Rectangle(pad, Dpi(18), Dpi(320), Dpi(28)));
                Draw.DrawStringLeft(g, "程序对分类、集合和设置的每次改动都记在这里；最新的一次可以撤销。", Theme.CaptionFont, secondary,
                    new Rectangle(pad, Dpi(48), Math.Max(1, maintain.Left - pad - Dpi(12)), Dpi(22)));
                Draw.DrawStringRight(g, headerSummary, Theme.CaptionFont, secondary,
                    new Rectangle(Math.Max(pad, maintain.Left - Dpi(12) - Dpi(240)), Dpi(20), Dpi(240), Dpi(24)));

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
                string label = StatusText(current, latestBusiness);
                int chipWidth = ChipWidth(g, label);
                int titleWidth = Math.Max(1, Math.Min((int)Math.Ceiling(g.MeasureString(current.Title, titleFont).Width) + Dpi(2),
                    width - chipWidth - Dpi(8)));
                Draw.DrawStringLeft(g, current.Title, titleFont, primary, new Rectangle(x, rightFrame.Y + Dpi(12), titleWidth, Dpi(24)));
                DrawChip(g, label, CanUndo(current, latestBusiness) ? GoodText : Theme.TextSecondary,
                    CanUndo(current, latestBusiness) ? GoodBg : Theme.ChipBg,
                    new Rectangle(x + titleWidth + Dpi(8), rightFrame.Y + Dpi(14), chipWidth, Dpi(20)));
                int total = added < 0 ? -1 : added + modified + removed;
                string subtitle = (current.TimeKnown ? current.Time.ToString("yyyy-MM-dd HH:mm:ss") : "时间未知")
                    + (total > shownRows ? " · 已列出前 " + shownRows + " 处，共 " + total + " 处" : string.Empty)
                    + " · 记录编号 " + current.Id
                    + (OperationHistory.IsKnown(current.Action) ? string.Empty : " · 内部名称 " + current.Action);
                Draw.DrawStringLeft(g, subtitle, Theme.CaptionFont, secondary, new Rectangle(x, rightFrame.Y + Dpi(38), width, Dpi(20)));

                DrawTiles(g, secondary);
                if (!noticeRect.IsEmpty)
                    using (SolidBrush fill = new SolidBrush(notice.BackColor)) Draw.FillRoundedRect(g, fill, noticeRect, Dpi(6));
                if (detailText != null)
                    Draw.DrawStringCenter(g, detailText, Theme.BodyFont, tertiary,
                        Rectangle.FromLTRB(rightFrame.X + Dpi(16), Math.Max(tilesRect.Bottom, noticeRect.Bottom), rightFrame.Right - Dpi(16), rightFrame.Bottom));
            }
        }

        private void DrawCard(Graphics g, Rectangle frame)
        {
            using (SolidBrush fill = new SolidBrush(Theme.Card)) Draw.FillRoundedRect(g, fill, frame, Dpi(8));
            using (Pen border = new Pen(Theme.ControlBorder, 1)) Draw.DrawRoundedRect(g, border, frame, Dpi(8));
        }

        /// <summary>三个数字小卡片：这次操作新增、修改、删除了多少处；数量大于零时用对应的颜色。</summary>
        private void DrawTiles(Graphics g, Brush secondary)
        {
            string[] labels = { "新增", "修改", "删除" };
            int[] values = { added, modified, removed };
            Color[] accents = { GoodText, WarnText, BadText };
            int gap = Dpi(8), tileWidth = (tilesRect.Width - gap * 2) / 3, inset = Dpi(12);
            if (tileWidth <= inset * 2) return;
            for (int i = 0; i < 3; i++)
            {
                Rectangle tile = new Rectangle(tilesRect.X + i * (tileWidth + gap), tilesRect.Y, tileWidth, tilesRect.Height);
                using (SolidBrush fill = new SolidBrush(Theme.Background)) Draw.FillRoundedRect(g, fill, tile, Dpi(8));
                using (SolidBrush value = new SolidBrush(added < 0 || values[i] == 0 ? Theme.TextPrimary : accents[i]))
                    Draw.DrawStringLeft(g, added < 0 ? "—" : values[i].ToString(), Theme.FontOf(13F, FontStyle.Bold), value,
                        new Rectangle(tile.X + inset, tile.Y + Dpi(5), tileWidth - inset * 2, Dpi(26)));
                Draw.DrawStringLeft(g, labels[i], Theme.CaptionFont, secondary,
                    new Rectangle(tile.X + inset, tile.Y + Dpi(30), tileWidth - inset * 2, Dpi(18)));
            }
        }

        private int ChipWidth(Graphics g, string text)
        {
            return (int)Math.Ceiling(g.MeasureString(text, Theme.FontOf(8.5F)).Width) + Dpi(10);
        }

        private void DrawChip(Graphics g, string text, Color fore, Color back, Rectangle bounds)
        {
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

        /// <summary>「显示更多」一类的整行链接。</summary>
        private void DrawMoreRow(Graphics g, Rectangle row, string text)
        {
            using (SolidBrush brush = new SolidBrush(Theme.AccentText)) Draw.DrawStringCenter(g, text, Theme.CaptionFont, brush, row);
        }

        private void DrawOp(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= opList.Items.Count) return;
            Op op = opList.Items[e.Index] as Op;
            Rectangle row = e.Bounds;
            if (row.Width <= 0 || row.Height <= 0) return;
            using (BufferedGraphics buffer = BufferedGraphicsManager.Current.Allocate(e.Graphics, row))
            {
                Graphics g = buffer.Graphics;
                DrawRowBackground(g, row, e.Index == opList.SelectedIndex, e.Index == opList.HoverIndex);
                if (op == null)
                {
                    DrawMoreRow(g, row, "显示更早的记录");
                    buffer.Render(e.Graphics);
                    return;
                }
                int left = row.X + Dpi(12), titleRight = row.Right - Dpi(12);
                bool undoable = CanUndo(op, latestBusiness);
                if (undoable || op.Reverted)
                {
                    string label = undoable ? "可撤销" : "已撤销";
                    int chipWidth = ChipWidth(g, label);
                    Rectangle chip = new Rectangle(row.Right - Dpi(12) - chipWidth, row.Y + Dpi(9), chipWidth, Dpi(20));
                    DrawChip(g, label, undoable ? GoodText : Theme.TextSecondary, undoable ? GoodBg : Theme.ChipBg, chip);
                    titleRight = chip.X - Dpi(8);
                }
                using (SolidBrush brush = new SolidBrush(op.Reverted ? Theme.TextSecondary : Theme.TextPrimary))
                    Draw.DrawStringLeft(g, op.Title, Theme.ButtonFont, brush,
                        new Rectangle(left, row.Y + Dpi(7), Math.Max(1, titleRight - left), Dpi(24)));
                using (SolidBrush brush = new SolidBrush(Theme.TextSecondary))
                    Draw.DrawStringLeft(g, (op.TimeKnown ? ImportHistoryDialog.ListTime(op.Time, DateTime.Now) : "时间未知")
                        + (op.Line.Length > 0 ? " · " + op.Line : string.Empty), Theme.CaptionFont, brush,
                        new Rectangle(left, row.Y + Dpi(31), Math.Max(1, row.Width - Dpi(24)), Dpi(20)));
                buffer.Render(e.Graphics);
            }
        }

        /// <summary>明细行高（96 DPI 设计值再换算）：类型+对象一行，有补充说明时再加一行。</summary>
        private int RowHeightOf(ChangeRow row)
        {
            return Dpi(8 + 22 + (row.Detail.Length > 0 ? 2 + 18 : 0) + 8);
        }

        private void DrawRow(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= changeList.Items.Count) return;
            ChangeRow item = changeList.Items[e.Index] as ChangeRow;
            Rectangle row = e.Bounds;
            if (row.Width <= 0 || row.Height <= 0) return;
            using (BufferedGraphics buffer = BufferedGraphicsManager.Current.Allocate(e.Graphics, row))
            {
                Graphics g = buffer.Graphics;
                bool active = e.Index == changeList.SelectedIndex || e.Index == changeList.HoverIndex;
                DrawRowBackground(g, row, e.Index == changeList.SelectedIndex, e.Index == changeList.HoverIndex);
                if (item == null)
                {
                    int rest = Math.Max(0, added + modified + removed - shownRows);
                    // 总数按新增/修改/删除统计；有认不出类型的改动时算不出剩余数量，只写「显示更多」
                    DrawMoreRow(g, row, rest == 0 ? "显示更多" : "还有 " + rest + " 处没列出，点这里再显示 " + Math.Min(PageRows, rest) + " 处");
                    buffer.Render(e.Graphics);
                    return;
                }
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                int left = row.X + Dpi(12), right = row.Right - Dpi(12), y = row.Y + Dpi(8);

                string kind = OperationHistory.KindText(item.Kind);
                int chipWidth = ChipWidth(g, kind);
                DrawChip(g, kind,
                    item.Kind == ChangeKind.Added ? GoodText : item.Kind == ChangeKind.Modified ? WarnText : item.Kind == ChangeKind.Removed ? BadText : Theme.TextSecondary,
                    item.Kind == ChangeKind.Added ? GoodBg : item.Kind == ChangeKind.Modified ? WarnBg : item.Kind == ChangeKind.Removed ? BadBg : Theme.ChipBg,
                    new Rectangle(left, y + Dpi(1), chipWidth, Dpi(20)));
                int x = left + chipWidth + Dpi(8);
                int subjectWidth = Math.Min(Math.Max(0, right - x), (int)Math.Ceiling(g.MeasureString(item.Subject, Theme.BodyFont).Width) + Dpi(2));
                using (SolidBrush brush = new SolidBrush(Theme.TextSecondary))
                    Draw.DrawStringLeft(g, item.Subject, Theme.BodyFont, brush, new Rectangle(x, y, Math.Max(1, subjectWidth), Dpi(22)));
                x += subjectWidth + Dpi(8);
                using (SolidBrush brush = new SolidBrush(Theme.TextPrimary))
                    Draw.DrawStringLeft(g, item.Title, Theme.BodyFont, brush, new Rectangle(x, y, Math.Max(1, right - x), Dpi(22)));
                if (item.Detail.Length > 0)
                    using (SolidBrush brush = new SolidBrush(Theme.TextSecondary))
                        g.DrawString(item.Detail, Theme.CaptionFont, brush,
                            new RectangleF(left, y + Dpi(24), Math.Max(1, right - left), Dpi(18)), DetailFormat);

                if (!active && e.Index < changeList.Items.Count - 1)
                    using (Pen line = new Pen(Theme.Separator, 1))
                    {
                        g.SmoothingMode = SmoothingMode.None;
                        g.DrawLine(line, left, row.Bottom - 1, right, row.Bottom - 1);
                    }
                buffer.Render(e.Graphics);
            }
        }

        // ===== 明细行的提示、复制 =====

        /// <summary>鼠标所在行有内容被省略时，悬停显示完整文字。</summary>
        private void UpdateTip(int index)
        {
            if (index == tipIndex) return;
            tipIndex = index;
            string text = null;
            if (index >= 0 && index < changeList.Items.Count)
            {
                ChangeRow row = changeList.Items[index] as ChangeRow;
                int room = changeList.ClientSize.Width - Dpi(24);
                if (row != null && (Wider(row.Detail, Theme.CaptionFont, room)
                    || Wider(row.Subject + row.Title, Theme.BodyFont, room - Dpi(60)))) text = OperationHistory.RowText(row);
            }
            tip.SetToolTip(changeList, text);
        }

        private static bool Wider(string text, Font font, int width)
        {
            return !string.IsNullOrEmpty(text)
                && TextRenderer.MeasureText(text, font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width > width;
        }

        private void ShowRowMenu(ChangeRow row)
        {
            List<MenuWindow.MenuItem> items = new List<MenuWindow.MenuItem>();
            MenuWindow.MenuItem copy = new MenuWindow.MenuItem();
            copy.Text = "复制这一行";
            copy.Action = delegate { CopyText(OperationHistory.RowText(row)); };
            items.Add(copy);
            MenuWindow menu = new MenuWindow(items, 150);
            Point point = Cursor.Position;
            Rectangle work = Screen.GetWorkingArea(point);
            menu.Location = new Point(Math.Max(work.Left, Math.Min(point.X, work.Right - menu.Width)),
                Math.Max(work.Top, Math.Min(point.Y, work.Bottom - menu.Height)));
            menu.Show(this);
        }

        private void CopyText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try { Clipboard.SetText(text); }
            catch (ExternalException)
            {
                MessageDialog.Show(this, "剪贴板正被其它程序占用，没有复制成功，请再试一次。", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            if (running) return base.ProcessCmdKey(ref msg, keyData);
            if (keyData == Keys.Enter)
            {
                // 自绘按钮不是系统默认按钮：焦点在哪个按钮上，回车就执行哪个；撤销必须明确选中按钮。
                if (undo.Focused) RequestUndo();
                else if (export.Focused) Export();
                else if (maintain.Focused) ShowMaintainMenu();
                else if (close.Focused) Close();
                else if (opList.Focused && opList.SelectedIndex >= 0 && !(opList.SelectedItem is Op)) LoadHistory(true);
                else if (changeList.Focused && changeList.SelectedIndex >= 0 && !(changeList.SelectedItem is ChangeRow)) LoadDetails(true);
                return true;
            }
            if (keyData == Keys.F5) { LoadHistory(false); return true; }
            if (keyData == (Keys.Control | Keys.C) && changeList.Focused && changeList.SelectedItem is ChangeRow)
            {
                CopyText(OperationHistory.RowText((ChangeRow)changeList.SelectedItem));
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // 备份、恢复、撤销进行中不能关：后台线程还在写文件或数据库
            if (running) e.Cancel = true;
            base.OnFormClosing(e);
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

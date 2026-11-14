using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;
using AppManager.Controls;

namespace AppManager
{
    internal enum LocalScanResultAction
    {
        Import,
        Retry,
        ChangeScope,
        Cancel
    }

    internal sealed class LocalScanResultDecision
    {
        internal LocalScanResultAction Action = LocalScanResultAction.Cancel;
        internal ImportOperation Operation = ImportOperation.Copy;
        /// <summary>结果窗口带附加勾选项时，用户是否勾选了它。</summary>
        internal bool OptionChecked;
    }

    /// <summary>扫描结果顶部的一个关键数字，例如「新增 128 张」。Warn 用于失败类数字，大于零时标红。</summary>
    internal sealed class LocalScanStat
    {
        internal readonly string Label;
        internal readonly string Value;
        internal readonly bool Warn;

        internal LocalScanStat(string label, string value, bool warn = false)
        {
            Label = label;
            Value = value;
            Warn = warn;
        }
    }

    /// <summary>
    /// 照片/视频/音乐共用的扫描进度窗口。后台线程通过 Report 更新文字和进度，结束时调用 Finish 关闭。
    /// 扫描结束前用户关闭窗口只会发出取消请求，窗口等后台线程退出后才关闭，避免后台仍在跑而界面已继续往下走。
    /// </summary>
    internal sealed class LocalScanProgressDialog : BufferedDialogForm
    {
        private readonly CancellationTokenSource cancel;
        private readonly Label statusLabel = new Label();
        private readonly Label detailLabel = new Label();
        private readonly IosProgressBar progress = new IosProgressBar();
        private readonly RoundButton cancelButton = new RoundButton();
        private volatile bool finished;
        private bool cancelRequested;

        internal LocalScanProgressDialog(string title, string status, string detail, CancellationTokenSource cancel)
        {
            this.cancel = cancel;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(520, 196);
            BackColor = Theme.Background;
            ShowInTaskbar = false;
            Text = title;

            Label titleLabel = new Label();
            titleLabel.Text = title;
            titleLabel.Font = Theme.DialogTitleFont;
            titleLabel.ForeColor = Theme.TextPrimary;
            titleLabel.AutoSize = false;
            titleLabel.UseMnemonic = false;
            titleLabel.SetBounds(24, 18, 472, 28);
            Controls.Add(titleLabel);

            statusLabel.Text = status;
            statusLabel.Font = Theme.BodyFont;
            statusLabel.ForeColor = Theme.TextPrimary;
            statusLabel.AutoSize = false;
            statusLabel.AutoEllipsis = true;
            statusLabel.UseMnemonic = false;
            statusLabel.SetBounds(24, 56, 472, 22);
            Controls.Add(statusLabel);

            // 这里显示正在处理的路径：路径里的 & 不能被当成快捷键标记
            detailLabel.Text = detail;
            detailLabel.Font = Theme.CaptionFont;
            detailLabel.ForeColor = Theme.TextSecondary;
            detailLabel.AutoSize = false;
            detailLabel.AutoEllipsis = true;
            detailLabel.UseMnemonic = false;
            detailLabel.SetBounds(24, 80, 472, 20);
            Controls.Add(detailLabel);

            progress.BackColor = Theme.Background;
            progress.SetBounds(24, 110, 472, 10);
            progress.Marquee = true;
            Controls.Add(progress);

            cancelButton.Text = "取消";
            cancelButton.SetBounds(406, 140, 90, 34);
            cancelButton.Click += delegate { RequestCancel(); };
            Controls.Add(cancelButton);
        }

        /// <summary>更新状态文字和进度，可在任意线程调用。total 大于零时显示确定进度，否则显示进行中动画。</summary>
        internal void Report(string status, string detail, int index, int total)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke((Action)delegate
                {
                    // 已请求取消后保留「正在取消」的提示，不再被后台迟到的进度覆盖
                    if (IsDisposed || Disposing || cancelRequested) return;
                    statusLabel.Text = status;
                    detailLabel.Text = detail ?? string.Empty;
                    if (total > 0)
                    {
                        progress.Marquee = false;
                        progress.Maximum = total;
                        progress.Value = Math.Max(0, Math.Min(index, total));
                    }
                    else progress.Marquee = true;
                });
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        /// <summary>后台工作结束（成功、失败或取消）后调用，可在任意线程调用；窗口随后关闭。</summary>
        internal void Finish()
        {
            finished = true;
            try
            {
                if (IsDisposed || Disposing) return;
                if (!IsHandleCreated) { Close(); return; }
                BeginInvoke((Action)delegate { if (!IsDisposed) Close(); });
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private void RequestCancel()
        {
            if (cancelRequested) return;
            cancelRequested = true;
            try { cancel.Cancel(); }
            catch (ObjectDisposedException) { }   // 扫描已经结束并释放了取消源：没有可取消的工作
            cancelButton.Enabled = false;
            cancelButton.Text = "正在取消…";
            statusLabel.Text = "正在取消扫描…";
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!finished)
            {
                RequestCancel();
                if (e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
            }
            base.OnFormClosing(e);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape || (keyData == Keys.Enter && cancelButton.Focused))
            {
                RequestCancel();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }

    /// <summary>
    /// 扫描结果确认窗口：一句话结论、关键数字、可滚动明细，以及导入方式、修改范围、重新扫描。
    /// optionText 不为空时在导入方式上方多一个勾选项（默认不勾选），需要用户表态的问题放在这里而不是另弹窗口；
    /// optionRequired 表示不勾选就没有可导入的内容，此时主按钮不可用。
    /// </summary>
    internal static class LocalScanResultDialog
    {
        internal static LocalScanResultDecision Show(string title, string headline, IList<LocalScanStat> stats,
            string details, string importText, bool canImport, bool hasNewItems = true,
            string optionText = null, bool optionRequired = false)
        {
            using (ResultForm form = new ResultForm(title, headline, stats, details, importText, canImport, hasNewItems,
                optionText, optionRequired))
            {
                ConfirmDialog.ShowModal(form);   // 模态 + 遮罩层
                return form.Decision;
            }
        }

        private sealed class ResultForm : BufferedDialogForm
        {
            private readonly string importText;
            private readonly bool canImport;
            private readonly bool hasNewItems;
            private readonly TextBox details;
            private readonly Rectangle detailsFrame;   // 明细外框（96 DPI 设计坐标）
            private readonly RadioButton move;
            private readonly CheckBox option;
            private readonly bool optionRequired;
            private readonly Label warning;
            private readonly RoundButton importButton = new RoundButton();
            private readonly RoundButton cancelButton = new RoundButton();
            private readonly RoundButton scopeButton = new RoundButton();
            private readonly RoundButton retryButton = new RoundButton();

            internal readonly LocalScanResultDecision Decision = new LocalScanResultDecision();

            internal ResultForm(string title, string headline, IList<LocalScanStat> stats, string detailText,
                string importText, bool canImport, bool hasNewItems, string optionText, bool optionRequired)
            {
                this.importText = importText;
                this.canImport = canImport;
                this.hasNewItems = hasNewItems;
                this.optionRequired = optionRequired;
                bool allowMove = canImport && hasNewItems;

                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.CenterParent;
                BackColor = Theme.Background;
                ShowInTaskbar = false;
                Text = title;

                Label titleLabel = new Label();
                titleLabel.Text = title;
                titleLabel.Font = Theme.DialogTitleFont;
                titleLabel.ForeColor = Theme.TextPrimary;
                titleLabel.AutoSize = false;
                titleLabel.UseMnemonic = false;
                titleLabel.SetBounds(24, 18, 552, 28);
                Controls.Add(titleLabel);

                Label headlineLabel = new Label();
                headlineLabel.Text = headline ?? string.Empty;
                headlineLabel.Font = Theme.SectionTitleFont;
                headlineLabel.ForeColor = Theme.TextPrimary;
                headlineLabel.AutoSize = false;
                headlineLabel.AutoEllipsis = true;
                headlineLabel.UseMnemonic = false;
                headlineLabel.SetBounds(24, 52, 552, 24);
                Controls.Add(headlineLabel);

                int y = 86;
                if (stats != null && stats.Count > 0)
                {
                    StatStrip strip = new StatStrip(stats);
                    strip.SetBounds(24, y, 552, 60);
                    Controls.Add(strip);
                    y += 72;
                }

                if (!string.IsNullOrEmpty(detailText))
                {
                    detailsFrame = new Rectangle(24, y, 552, allowMove ? 150 : 200);
                    details = new TextBox();
                    details.Multiline = true;
                    details.ReadOnly = true;
                    details.WordWrap = true;
                    details.ScrollBars = ScrollBars.Vertical;
                    details.BorderStyle = BorderStyle.None;
                    details.BackColor = Theme.Card;
                    details.ForeColor = Theme.TextPrimary;
                    details.Font = Theme.BodyFont;
                    details.TabStop = false;
                    // 多行文本框只认 CRLF 换行
                    details.Text = detailText.Replace("\r\n", "\n").Replace("\n", "\r\n");
                    details.SetBounds(detailsFrame.X + 12, detailsFrame.Y + 10, detailsFrame.Width - 16, detailsFrame.Height - 20);
                    Controls.Add(details);
                    y += detailsFrame.Height + 14;
                }

                if (canImport && !string.IsNullOrEmpty(optionText))
                {
                    option = new CheckBox();
                    option.Text = optionText;
                    option.Font = Theme.BodyFont;
                    option.ForeColor = Theme.TextPrimary;
                    option.AutoSize = false;
                    option.AutoEllipsis = true;
                    option.UseMnemonic = false;
                    option.SetBounds(24, y, 552, 24);
                    option.CheckedChanged += delegate { UpdateButtons(); };
                    Controls.Add(option);
                    y += 32;
                }

                if (allowMove)
                {
                    Label modeLabel = new Label();
                    modeLabel.Text = "导入方式";
                    modeLabel.Font = Theme.SectionTitleFont;
                    modeLabel.ForeColor = Theme.TextPrimary;
                    modeLabel.AutoSize = true;
                    modeLabel.Location = new Point(24, y);
                    Controls.Add(modeLabel);
                    y += 26;

                    move = new RadioButton();
                    move.Text = "移入资源库并清理原件（推荐：成功项删源，失败项保留）";
                    move.Font = Theme.BodyFont;
                    move.ForeColor = Theme.TextPrimary;
                    move.AutoSize = true;
                    move.Location = new Point(24, y);
                    move.Checked = true;
                    Controls.Add(move);
                    y += 26;

                    RadioButton copy = new RadioButton();
                    copy.Text = "仅复制，保留原件";
                    copy.Font = Theme.BodyFont;
                    copy.ForeColor = Theme.TextPrimary;
                    copy.AutoSize = true;
                    copy.Location = new Point(24, y);
                    Controls.Add(copy);
                    y += 28;

                    warning = new Label();
                    warning.Text = "移入会在数据库提交并逐项校验后删除已成功的原文件，失败项保留。删除不进入回收站；可移动磁盘或同步目录中的删除会同步生效。";
                    warning.Font = Theme.CaptionFont;
                    warning.ForeColor = Theme.TextSecondary;
                    warning.AutoSize = false;
                    warning.SetBounds(24, y, 552, 36);
                    Controls.Add(warning);
                    y += 44;

                    move.CheckedChanged += delegate { UpdateButtons(); };
                }

                importButton.Accent = true;
                importButton.Visible = canImport;
                importButton.SetBounds(312, y, 176, 34);
                importButton.Click += delegate { Finish(LocalScanResultAction.Import); };
                Controls.Add(importButton);

                cancelButton.Text = canImport ? "取消" : "关闭";
                cancelButton.SetBounds(496, y, 80, 34);
                cancelButton.Click += delegate { Finish(LocalScanResultAction.Cancel); };
                Controls.Add(cancelButton);

                scopeButton.Text = "修改范围";
                scopeButton.SetBounds(24, y, 92, 34);
                scopeButton.Click += delegate { Finish(LocalScanResultAction.ChangeScope); };
                Controls.Add(scopeButton);

                retryButton.Text = "重新扫描";
                retryButton.SetBounds(124, y, 92, 34);
                retryButton.Click += delegate { Finish(LocalScanResultAction.Retry); };
                Controls.Add(retryButton);

                ClientSize = new Size(600, y + 34 + 22);
                UpdateButtons();
                // 移入模式下初始焦点放在单选项上：回车不会直接触发永久删除
                ActiveControl = move != null ? (Control)move : (canImport ? importButton : cancelButton);
            }

            private bool Destructive
            {
                get { return move != null && move.Checked; }
            }

            private void UpdateButtons()
            {
                bool destructive = Destructive;
                importButton.Text = destructive ? "开始移动并清理原件…"
                    : (canImport && !hasNewItems ? "更新已收录归属" : (string.IsNullOrEmpty(importText) ? "继续" : importText));
                if (warning != null) warning.Visible = destructive;
                // 不勾选附加项就没有可导入的内容时，主按钮不可用
                bool ready = !optionRequired || (option != null && option.Checked);
                importButton.Enabled = ready;
                importButton.Accent = ready;
            }

            private void Finish(LocalScanResultAction action)
            {
                if (action == LocalScanResultAction.Import && !importButton.Enabled) return;
                Decision.Action = action;
                if (action == LocalScanResultAction.Import)
                {
                    Decision.Operation = Destructive ? ImportOperation.Move : ImportOperation.Copy;
                    Decision.OptionChecked = option != null && option.Checked;
                }
                DialogResult = action == LocalScanResultAction.Cancel ? DialogResult.Cancel : DialogResult.OK;
                Close();
            }

            protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
            {
                if (keyData == Keys.Escape) { Finish(LocalScanResultAction.Cancel); return true; }
                if (keyData == Keys.Enter && (details == null || !details.Focused))
                {
                    // 自绘按钮不是系统默认按钮：焦点在哪个按钮上，回车就执行哪个。
                    // 其余情况回车等同主按钮，但移入模式例外——永久删除必须明确点按钮或把焦点移到按钮上。
                    if (cancelButton.Focused) Finish(LocalScanResultAction.Cancel);
                    else if (scopeButton.Focused) Finish(LocalScanResultAction.ChangeScope);
                    else if (retryButton.Focused) Finish(LocalScanResultAction.Retry);
                    else if (canImport && (importButton.Focused || !Destructive)) Finish(LocalScanResultAction.Import);
                    return true;
                }
                return base.ProcessCmdKey(ref msg, keyData);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                if (details == null) return;
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle frame = new Rectangle(Dpi(detailsFrame.X), Dpi(detailsFrame.Y), Dpi(detailsFrame.Width), Dpi(detailsFrame.Height));
                using (SolidBrush fill = new SolidBrush(Theme.Card)) Draw.FillRoundedRect(g, fill, frame, Dpi(8));
                using (Pen border = new Pen(Theme.ControlBorder, 1)) Draw.DrawRoundedRect(g, border, frame, Dpi(8));
            }
        }

        /// <summary>一排等宽的数字卡片：上面是数值，下面是说明。只负责显示，不接收焦点。</summary>
        private sealed class StatStrip : Control
        {
            private readonly IList<LocalScanStat> stats;

            internal StatStrip(IList<LocalScanStat> stats)
            {
                this.stats = stats;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                    | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
                SetStyle(ControlStyles.Selectable, false);
                BackColor = Theme.Background;
                TabStop = false;
                List<string> names = new List<string>();
                foreach (LocalScanStat stat in stats) names.Add(stat.Label + " " + stat.Value);
                AccessibleName = string.Join("，", names.ToArray());
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                int dpi = DpiHelper.ForControl(this);
                int gap = DpiHelper.Scale(8, dpi), pad = DpiHelper.Scale(12, dpi), radius = DpiHelper.Scale(8, dpi);
                int count = stats.Count;
                int width = (Width - gap * (count - 1)) / count;
                if (width <= pad * 2) return;
                for (int i = 0; i < count; i++)
                {
                    Rectangle tile = new Rectangle(i * (width + gap), 0, width - 1, Height - 1);
                    using (SolidBrush fill = new SolidBrush(Theme.Card)) Draw.FillRoundedRect(g, fill, tile, radius);
                    using (Pen border = new Pen(Theme.ControlBorder, 1)) Draw.DrawRoundedRect(g, border, tile, radius);
                    Rectangle text = new Rectangle(tile.X + pad, tile.Y, tile.Width - pad * 2, tile.Height);
                    using (SolidBrush value = new SolidBrush(stats[i].Warn ? Theme.DeleteRed : Theme.TextPrimary))
                        Draw.DrawStringLeft(g, stats[i].Value, Theme.FontOf(13F, FontStyle.Bold), value,
                            new Rectangle(text.X, text.Y + DpiHelper.Scale(6, dpi), text.Width, DpiHelper.Scale(28, dpi)));
                    using (SolidBrush label = new SolidBrush(Theme.TextSecondary))
                        Draw.DrawStringLeft(g, stats[i].Label, Theme.CaptionFont, label,
                            new Rectangle(text.X, text.Y + DpiHelper.Scale(34, dpi), text.Width, DpiHelper.Scale(20, dpi)));
                }
            }
        }
    }
}

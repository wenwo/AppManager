using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Linq;
using System.Windows.Forms;
using AppManager.Controls;

namespace AppManager
{
    /// <summary>重复检查结果确认：默认复用/跳过，内容重复项可逐项勾选“仍然添加”。</summary>
    internal static partial class ImportDuplicateDialog
    {
        /// <summary>列表最多列出的项目数；超出的按默认方式（跳过/复用）处理。</summary>
        internal const int MaxRows = 1000;

        /// <summary>返回用户勾选“仍然添加”的来源路径；取消导入返回 null；没有需要确认的内容时直接返回空集合。</summary>
        internal static ICollection<string> Show(IWin32Window owner, ImportDuplicateReport report)
        {
            if (report == null || !report.NeedsPrompt) return new string[0];
            using (DuplicateForm form = new DuplicateForm(report))
            {
                ConfirmDialog.ShowModal(form, owner as Form);
                return form.Keep;
            }
        }

        /// <summary>列表里的行：能勾选“仍然添加”的排在前面，其余保持检查时的顺序，总数不超过 MaxRows。</summary>
        internal static List<ImportDuplicateFinding> Rows(ImportDuplicateReport report)
        {
            List<ImportDuplicateFinding> rows = report.Findings.Where(f => f.CanKeep).ToList();
            rows.AddRange(report.Findings.Where(f => !f.CanKeep));
            if (rows.Count > MaxRows) rows.RemoveRange(MaxRows, rows.Count - MaxRows);
            return rows;
        }

        /// <summary>行尾的处理方式：这一项导入时会被怎样对待。</summary>
        internal static string Outcome(ImportDuplicateFinding finding, bool keep)
        {
            if (finding.CanKeep) return keep ? "仍然添加" : "跳过";
            switch (finding.Kind)
            {
                case ImportDuplicateKind.ExistingPathOther:
                case ImportDuplicateKind.OtherCategoryContent:
                    return "复用已有";
                case ImportDuplicateKind.Unchecked:
                    return "照常导入";
                default:
                    return "跳过";
            }
        }

        /// <summary>行的第二行：来源路径，以及它和谁重复（库里已有的那份，或同一次选择里的另一项）。</summary>
        internal static string Detail(ImportDuplicateFinding finding)
        {
            string source = finding.Source ?? string.Empty;
            if (string.IsNullOrEmpty(finding.MatchPath)) return source;
            bool sameBatch = finding.Kind == ImportDuplicateKind.BatchContentDuplicate || finding.Kind == ImportDuplicateKind.BatchPathOverlap;
            return source + (sameBatch ? "  →  与本次选择的这一项重复：" : "  →  库里已有：") + finding.MatchPath;
        }

        private sealed class DuplicateForm : BufferedDialogForm
        {
            private const int RowHeight = 46;
            private const int FormWidth = 720;
            private const int Pad = 24;

            private readonly List<ImportDuplicateFinding> rows;
            private readonly HashSet<ImportDuplicateFinding> kept = new HashSet<ImportDuplicateFinding>();
            private readonly SoftwarePickerDialog.PickerList list = new SoftwarePickerDialog.PickerList();
            private readonly RoundButton proceed = new RoundButton();
            private readonly RoundButton cancel = new RoundButton();
            private readonly Rectangle listFrame;   // 列表外框（96 DPI 设计坐标）

            /// <summary>点「继续导入」后为勾选的来源路径；取消时为 null。</summary>
            internal ICollection<string> Keep { get; private set; }

            internal DuplicateForm(ImportDuplicateReport report)
            {
                rows = Rows(report);
                foreach (ImportDuplicateFinding finding in rows)
                    if (finding.CanKeep && finding.Keep) kept.Add(finding);
                int duplicates = report.DuplicateCount, reuse = report.ReuseCount, skipped = report.Unchecked;
                bool anyKeepable = rows.Any(f => f.CanKeep);

                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.CenterParent;
                BackColor = Theme.Background;
                ShowInTaskbar = false;
                Text = "重复文件确认";
                int inner = FormWidth - Pad * 2;

                AddLabel(Text, Theme.DialogTitleFont, Theme.TextPrimary, new Rectangle(Pad, 18, inner, 28));
                AddLabel(duplicates + reuse > 0 ? "有 " + (duplicates + reuse) + " 个文件已经在库里，或者彼此重复"
                    : "有 " + skipped + " 个文件没能检查是否重复", Theme.SectionTitleFont, Theme.TextPrimary, new Rectangle(Pad, 52, inner, 24));

                LocalScanResultDialog.StatStrip strip = new LocalScanResultDialog.StatStrip(new LocalScanStat[]
                {
                    new LocalScanStat("重复，默认跳过", duplicates.ToString()),
                    new LocalScanStat("其它分类已有，直接复用", reuse.ToString()),
                    new LocalScanStat("没能检查，照常导入", skipped.ToString())
                });
                strip.SetBounds(Pad, 86, inner, 60);
                Controls.Add(strip);

                AddLabel(report.Summary(), Theme.CaptionFont, Theme.TextSecondary, new Rectangle(Pad, 154, inner, 20));

                listFrame = new Rectangle(Pad, 182, inner, 280);
                list.SetBounds(listFrame.X + 4, listFrame.Y + 4, listFrame.Width - 8, listFrame.Height - 8);
                list.ItemHeight = RowHeight;
                list.AccessibleName = "重复或需要确认的文件";
                list.DrawItem += DrawRow;
                // 只在同一行按下并松开时切换：拖到别的行再松开、或在上一个窗口按下后松开到这里，都不算点击
                int pressedRow = -1;
                list.MouseDown += delegate(object s, MouseEventArgs e)
                {
                    pressedRow = e.Button == MouseButtons.Left ? list.HitRow(e.Location) : -1;
                };
                list.MouseUp += delegate(object s, MouseEventArgs e)
                {
                    int row = pressedRow;
                    pressedRow = -1;
                    if (e.Button == MouseButtons.Left && row >= 0 && row == list.HitRow(e.Location)) Toggle(row);
                };
                list.KeyDown += delegate(object s, KeyEventArgs e)
                {
                    if (e.KeyCode != Keys.Space) return;
                    Toggle(list.SelectedIndex);
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                };
                list.Items.AddRange(rows.ToArray());
                Controls.Add(list);

                string note = (anyKeepable ? "勾选的项目会再添加一份；没勾选的跳过或复用库里已有的那份，" : "这些项目会跳过或复用库里已有的那份，")
                    + "它们的原文件不会被删除。\n文件夹只比对路径，不检查里面的内容。"
                    + (report.Findings.Count > rows.Count ? "这里只列出前 " + rows.Count + " 项，其余按默认方式处理。" : string.Empty);
                Label noteLabel = AddLabel(note, Theme.CaptionFont, Theme.TextSecondary, new Rectangle(Pad, 470, inner, 38));
                noteLabel.AutoEllipsis = false;

                cancel.Text = "取消导入";
                cancel.SetBounds(FormWidth - Pad - 96, 520, 96, 34);
                cancel.Click += delegate { Close(); };
                proceed.Text = "继续导入";
                proceed.Accent = true;
                proceed.SetBounds(cancel.Left - 10 - 110, 520, 110, 34);
                proceed.Click += delegate { Confirm(); };
                Controls.Add(proceed);
                Controls.Add(cancel);

                ClientSize = new Size(FormWidth, 520 + 34 + Pad);
                ActiveControl = list;
            }

            private Label AddLabel(string text, Font font, Color color, Rectangle bounds)
            {
                Label label = new Label();
                label.Text = text;
                label.Font = font;
                label.ForeColor = color;
                label.AutoSize = false;
                label.AutoEllipsis = true;
                label.UseMnemonic = false;   // 文件名、路径里的 & 不是快捷键标记
                label.Bounds = bounds;
                Controls.Add(label);
                return label;
            }

            private void Toggle(int index)
            {
                if (index < 0 || index >= list.Items.Count) return;
                ImportDuplicateFinding finding = (ImportDuplicateFinding)list.Items[index];
                if (!finding.CanKeep) return;
                if (!kept.Remove(finding)) kept.Add(finding);
                list.InvalidateRow(index);
            }

            private void Confirm()
            {
                List<string> keep = new List<string>();
                foreach (ImportDuplicateFinding finding in rows)
                    if (kept.Contains(finding)) keep.Add(finding.Source);
                Keep = keep;
                DialogResult = DialogResult.OK;
                Close();
            }

            protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
            {
                if (keyData == Keys.Escape) { Close(); return true; }
                if (keyData == Keys.Enter)
                {
                    // 自绘按钮不是系统默认按钮：焦点在「取消导入」上时回车就是取消，其余情况等同主按钮
                    if (cancel.Focused) Close(); else Confirm();
                    return true;
                }
                return base.ProcessCmdKey(ref msg, keyData);
            }

            protected override void OnDpiScaleChanged()
            {
                base.OnDpiScaleChanged();
                list.ItemHeight = Dpi(RowHeight);   // 行高不随窗体缩放自动变化
                Invalidate(true);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle frame = new Rectangle(Dpi(listFrame.X), Dpi(listFrame.Y), Dpi(listFrame.Width), Dpi(listFrame.Height));
                using (SolidBrush fill = new SolidBrush(Theme.Card)) Draw.FillRoundedRect(g, fill, frame, Dpi(8));
                using (Pen border = new Pen(Theme.ControlBorder, 1)) Draw.DrawRoundedRect(g, border, frame, Dpi(8));
            }

            private void DrawRow(object sender, DrawItemEventArgs e)
            {
                if (e.Index < 0 || e.Index >= list.Items.Count) return;
                ImportDuplicateFinding finding = (ImportDuplicateFinding)list.Items[e.Index];
                Rectangle row = e.Bounds;
                if (row.Width <= 0 || row.Height <= 0) return;
                // 整行先画到缓冲再贴上去：悬停、勾选时的重绘都不闪
                using (BufferedGraphics buffer = BufferedGraphicsManager.Current.Allocate(e.Graphics, row))
                {
                    Graphics g = buffer.Graphics;
                    // 底色先按整像素铺满，再开抗锯齿：否则行的上、左边缘会半透明地透出缓冲区的黑底
                    using (SolidBrush back = new SolidBrush(Theme.Card)) g.FillRectangle(back, row);
                    g.SmoothingMode = SmoothingMode.AntiAlias;

                    bool isKept = kept.Contains(finding);
                    bool active = e.Index == list.HoverIndex || (list.KeyboardActive && e.Index == list.SelectedIndex);
                    if (isKept || active)
                    {
                        Rectangle highlight = new Rectangle(row.X + Dpi(2), row.Y + 1, row.Width - Dpi(4), row.Height - 2);
                        using (SolidBrush b = new SolidBrush(isKept ? Theme.SelectionBg : Theme.RowHover))
                            Draw.FillRoundedRect(g, b, highlight, Dpi(6));
                    }

                    // 不能改的行不画勾选框，文字仍与可勾选的行对齐
                    int box = Dpi(18);
                    Rectangle check = new Rectangle(row.X + Dpi(12), row.Y + (row.Height - box) / 2, box, box);
                    if (finding.CanKeep) Draw.DrawCheckBox(g, check, isKept, true, CurrentDpi);

                    string outcome = Outcome(finding, isKept);
                    int outcomeWidth = Dpi(84);
                    Rectangle outcomeRect = new Rectangle(row.Right - Dpi(12) - outcomeWidth, row.Y, outcomeWidth, row.Height);
                    using (SolidBrush outcomeBrush = new SolidBrush(isKept ? Theme.AccentText : Theme.TextSecondary))
                        Draw.DrawStringRight(g, outcome, Theme.CaptionFont, outcomeBrush, outcomeRect);

                    int textX = check.Right + Dpi(12);
                    int textWidth = Math.Max(0, outcomeRect.Left - Dpi(8) - textX);
                    using (SolidBrush nameBrush = new SolidBrush(Theme.TextPrimary))
                        Draw.DrawStringLeft(g, Path.GetFileName((finding.Source ?? string.Empty).TrimEnd('\\', '/')), Theme.BodyFont, nameBrush,
                            new Rectangle(textX, row.Y + Dpi(4), textWidth, Dpi(21)));
                    using (SolidBrush detailBrush = new SolidBrush(Theme.TextTertiary))
                        Draw.DrawStringLeft(g, Detail(finding), Theme.CaptionFont, detailBrush,
                            new Rectangle(textX, row.Y + Dpi(24), textWidth, Dpi(18)));
                    buffer.Render(e.Graphics);
                }
            }
        }
    }
}
namespace AppManager
{
    internal static partial class ImportDuplicateDialog
    {
        /// <summary>在模态进度窗口内后台检查，避免哈希大文件时阻塞界面。</summary>
        internal static ImportDuplicateReport InspectWithProgress(IWin32Window owner, string title, IEnumerable<string> paths,
            string category, bool resource, ImportDuplicateContext context)
        {
            List<string> snapshot = paths == null ? new List<string>() : paths.ToList();
            if (snapshot.Count == 0) return new ImportDuplicateReport();
            ImportDuplicateReport report = null;
            Exception failure = null;
            bool canceled = false;
            bool workerDone = false;
            CancellationTokenSource cancel = new CancellationTokenSource();
            try
            {
                // 与本机扫描共用同一个进度窗口：点取消、按 Esc 或尝试关闭都只是请求取消，等后台检查停下来才关窗
                using (LocalScanProgressDialog dialog = new LocalScanProgressDialog(title, "正在检查重复文件…",
                    "内容判重需要完整读取文件；可以随时取消，源文件不会被修改。", cancel))
                {
                    dialog.CancelingText = "正在取消检查，源文件不会被修改…";
                    dialog.Shown += delegate
                    {
                        ThreadPool.QueueUserWorkItem(delegate
                        {
                            try
                            {
                                report = ImportDuplicateInspector.InspectPaths(snapshot, category, resource, context, cancel.Token);
                            }
                            catch (OperationCanceledException) { canceled = true; }
                            catch (Exception ex) { failure = ex; }
                            finally
                            {
                                workerDone = true;
                                dialog.Finish();
                            }
                        });
                    };
                    ConfirmDialog.ShowModal(dialog, owner as Form);
                    // 用户已经点了取消：即使检查恰好在取消生效前跑完，也按取消处理，不继续导入
                    if (cancel.IsCancellationRequested) canceled = true;
                }
            }
            finally
            {
                // 程序退出等原因关窗时后台检查可能还没停：此时不释放取消源，留给垃圾回收，避免后台用到已释放的对象
                if (workerDone) cancel.Dispose();
                else cancel.Cancel();
            }
            if (canceled) return null;
            if (failure != null) throw new InvalidOperationException("重复检查失败：" + failure.Message, failure);
            return report;
        }
    }
}

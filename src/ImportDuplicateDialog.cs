using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Linq;
using System.Windows.Forms;

namespace AppManager
{
    /// <summary>重复检查结果确认：默认复用/跳过，内容重复项可逐项选择“仍然添加”。</summary>
    internal static partial class ImportDuplicateDialog
    {
        private const int MaxChoices = 1000;

        private sealed class Choice
        {
            internal ImportDuplicateFinding Finding;
            public override string ToString()
            {
                string target = string.IsNullOrEmpty(Finding.MatchPath) ? "" : "\n      复用：" + Finding.MatchPath;
                return Path.GetFileName(Finding.Source) + "  ←  " + Finding.Source + target;
            }
        }

        internal static ICollection<string> Show(IWin32Window owner, ImportDuplicateReport report)
        {
            if (report == null || !report.NeedsPrompt) return new string[0];
            using (Form form = new BufferedDialogForm())
            {
                form.Text = "重复文件确认";
                form.StartPosition = FormStartPosition.CenterParent;
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.MinimizeBox = false;
                form.MaximizeBox = false;
                form.ShowInTaskbar = false;
                form.ClientSize = new Size(780, 640);
                form.BackColor = Theme.Background;

                Label heading = new Label
                {
                    Text = "发现重复或需要确认的文件",
                    AutoSize = false, Location = new Point(20, 16), Size = new Size(740, 30),
                    Font = Theme.DialogTitleFont, ForeColor = Theme.TextPrimary
                };
                Label summary = new Label
                {
                    Text = "重复 " + report.DuplicateCount + " 项；跨分类复用 " + report.ReuseCount
                        + " 项；未检查 " + report.Unchecked + " 项。\n" + report.Summary(),
                    AutoSize = false, Location = new Point(20, 52), Size = new Size(740, 48),
                    Font = Theme.CaptionFont, ForeColor = Theme.TextSecondary
                };
                Label warning = new Label
                {
                    Text = "复用/跳过项不会删除本次选择的原文件；即使选择移入，也只在真实复制成功的项目上删源。\n"
                        + "跨分类重复只复用已有目标并补充当前分类；文件夹不递归检查内容。\n"
                        + "ADS、云端占位、网络路径或哈希失败项默认正常导入。",
                    AutoSize = false, Location = new Point(20, 106), Size = new Size(740, 54),
                    Font = Theme.CaptionFont, ForeColor = Theme.TextSecondary
                };

                List<ImportDuplicateFinding> keepable = report.Findings.Where(f => f.CanKeep).ToList();
                Label choicesLabel = new Label
                {
                    Text = "同分类/同批内容重复项（勾选表示仍然添加；跨分类重复只复用）：",
                    AutoSize = false, Location = new Point(20, 170), Size = new Size(740, 24),
                    Font = Theme.SectionTitleFont, ForeColor = Theme.TextPrimary
                };
                CheckedListBox choices = new CheckedListBox
                {
                    Location = new Point(20, 198), Size = new Size(740, 350),
                    CheckOnClick = true, BorderStyle = BorderStyle.FixedSingle,
                    BackColor = Theme.Card, ForeColor = Theme.TextPrimary,
                    HorizontalScrollbar = true
                };
                choices.BeginUpdate();
                int shown = 0;
                foreach (ImportDuplicateFinding finding in keepable)
                {
                    if (shown >= MaxChoices) break;
                    int index = choices.Items.Add(new Choice { Finding = finding });
                    choices.SetItemChecked(index, finding.Keep);
                    shown++;
                }
                choices.EndUpdate();
                choicesLabel.Visible = keepable.Count > 0;
                choices.Visible = keepable.Count > 0;

                Label scope = new Label
                {
                    Text = keepable.Count > shown
                        ? "仅展示前 " + shown + " 项；其余内容重复项按默认跳过/复用处理。"
                        : "保留原文件：白名单只对本次选择有效，不会修改库中已有文件。",
                    AutoSize = false, Location = new Point(20, 556), Size = new Size(740, 40),
                    Font = Theme.CaptionFont, ForeColor = Theme.TextSecondary
                };

                Button cancel = NewButton("取消导入");
                cancel.Location = new Point(642, 592); cancel.Size = new Size(118, 32);
                Button proceed = NewButton("继续导入");
                proceed.Location = new Point(506, 592); proceed.Size = new Size(124, 32);

                ICollection<string> result = null;
                proceed.Click += delegate
                {
                    List<string> keep = new List<string>();
                    for (int i = 0; i < choices.Items.Count; i++)
                    {
                        Choice choice = choices.Items[i] as Choice;
                        if (choice != null && choices.GetItemChecked(i)) keep.Add(choice.Finding.Source);
                    }
                    result = keep;
                    form.DialogResult = DialogResult.OK;
                    form.Close();
                };
                cancel.Click += delegate { form.DialogResult = DialogResult.Cancel; form.Close(); };
                form.CancelButton = cancel;
                form.Controls.AddRange(new Control[] { heading, summary, warning, choicesLabel, choices, scope, cancel, proceed });
                form.ShowDialog(owner);
                return result;
            }
        }

        private static Button NewButton(string text)
        {
            return new Button
            {
                Text = text, Font = Theme.ButtonFont, UseVisualStyleBackColor = true, FlatStyle = FlatStyle.System
            };
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
            bool canceled = false, done = false;
            CancellationTokenSource cancel = new CancellationTokenSource();
            try
            {
                using (Form dialog = new BufferedDialogForm())
                {
                    dialog.Text = title;
                    dialog.StartPosition = FormStartPosition.CenterParent;
                    dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                    dialog.MinimizeBox = false;
                    dialog.MaximizeBox = false;
                    dialog.ShowInTaskbar = false;
                    dialog.ClientSize = new Size(520, 172);
                    dialog.BackColor = Theme.Background;

                    Label heading = new Label
                    {
                        Text = "正在检查重复文件…", AutoSize = false, Location = new Point(20, 18),
                        Size = new Size(480, 28), Font = Theme.DialogTitleFont, ForeColor = Theme.TextPrimary
                    };
                    Label detail = new Label
                    {
                        Text = "路径/来源优先；内容判重需要完整读取文件。期间可以取消，源文件不会被修改。",
                        AutoSize = false, Location = new Point(20, 50), Size = new Size(480, 40),
                        Font = Theme.CaptionFont, ForeColor = Theme.TextSecondary
                    };
                    ProgressBar progress = new ProgressBar
                    {
                        Location = new Point(20, 96), Size = new Size(480, 16),
                        Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30
                    };
                    Button abort = NewButton("取消");
                    abort.Location = new Point(396, 128); abort.Size = new Size(104, 30);

                    dialog.FormClosing += delegate(object sender, FormClosingEventArgs e)
                    {
                        if (done) return;
                        e.Cancel = true;
                        canceled = true;
                        cancel.Cancel();
                        abort.Enabled = false;
                        detail.Text = "正在取消检查，源文件不会被修改…";
                    };
                    abort.Click += delegate
                    {
                        canceled = true;
                        cancel.Cancel();
                        abort.Enabled = false;
                        detail.Text = "正在取消检查，源文件不会被修改…";
                    };
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
                                try
                                {
                                    dialog.BeginInvoke((Action)delegate
                                    {
                                        done = true;
                                        if (!dialog.IsDisposed) dialog.Close();
                                    });
                                }
                                catch (ObjectDisposedException) { }
                                catch (InvalidOperationException) { }
                            }
                        });
                    };
                    dialog.Controls.AddRange(new Control[] { heading, detail, progress, abort });
                    dialog.ShowDialog(owner);
                }
            }
            finally { cancel.Dispose(); }
            if (canceled) return null;
            if (failure != null) throw new InvalidOperationException("重复检查失败：" + failure.Message, failure);
            return report;
        }
    }
}

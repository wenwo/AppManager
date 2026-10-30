using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace AppManager
{
    internal sealed class DatabaseForm : Form
    {
        private readonly ConfigDatabase database;
        private readonly ListView history = new ListView();
        private readonly Label status = new Label();
        private readonly FlowLayoutPanel actions = new FlowLayoutPanel();
        private bool running;
        internal bool Changed;

        internal DatabaseForm(ConfigDatabase store)
        {
            database = store; Text = "数据维护与操作记录"; StartPosition = FormStartPosition.CenterParent;
            Size = new Size(960, 580); MinimumSize = new Size(760, 420); BackColor = Theme.Background;
            Font = Theme.BodyFont;
            history.Dock = DockStyle.Fill; history.View = View.Details; history.FullRowSelect = true; history.HideSelection = false;
            history.Columns.Add("事务", 65); history.Columns.Add("时间（本地）", 155); history.Columns.Add("操作", 150); history.Columns.Add("变更摘要", 430);
            history.Columns.Add("用户", 100);
            history.DoubleClick += delegate { ShowDetails(); }; Controls.Add(history);
            status.Dock = DockStyle.Top; status.Height = 50; status.Padding = new Padding(12, 6, 12, 6);
            status.Text = "保留关系与配置变更记录。双击查看前后值；撤销只处理最新事务。磁盘文件请用布局备份恢复。"; Controls.Add(status);
            actions.Dock = DockStyle.Bottom; actions.Height = 52; actions.Padding = new Padding(8);
            AddButton("刷新记录", delegate { Reload(); });
            AddButton("导出变更", Export);
            AddButton("完整备份", Backup);
            AddButton("完整性检查", delegate { Work(delegate { return database.Check(); }, false); });
            AddButton("撤销最新事务", Undo);
            AddButton("恢复数据库", Restore);
            AddButton("关闭", delegate { Close(); }); Controls.Add(actions);
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (running) e.Cancel = true; };
            Shown += delegate { Reload(); };
        }
        private void AddButton(string text, Action action)
        {
            Button button = new Button { Text = text, AutoSize = true, Height = 30 };
            button.Click += delegate { if (!running) action(); }; actions.Controls.Add(button);
        }
        private void Reload()
        {
            history.BeginUpdate(); history.Items.Clear();
            foreach (string[] row in database.History(200))
            {
                DateTime utc;
                string time = DateTime.TryParse(row[1], out utc) ? utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : row[1];
                ListViewItem item = new ListViewItem(new string[] { row[0], time, row[2], row[3], row[5] });
                item.Tag = Convert.ToInt64(row[0]); history.Items.Add(item);
            }
            history.EndUpdate();
        }
        private void Work(Func<string> action, bool changed)
        {
            running = true; actions.Enabled = false; status.Text = "正在处理，请稍候…";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string result = null; Exception error = null;
                try { result = action(); } catch (Exception ex) { error = ex; Paths.LogError(ex); }
                try
                {
                    BeginInvoke((Action)delegate
                    {
                        running = false; actions.Enabled = true;
                        status.Text = error == null ? result : "操作失败：" + error.Message;
                        if (error == null && changed) { Changed = true; Close(); }
                        else Reload();
                    });
                }
                catch { }
            });
        }
        private void Backup()
        {
            using (SaveFileDialog dialog = new SaveFileDialog { Filter = "数据库备份 (*.db)|*.db", FileName = "AppManager-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".db", OverwritePrompt = true })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                string path = dialog.FileName;
                Work(delegate { database.Backup(path); return "已备份到：" + path; }, false);
            }
        }
        private void Undo()
        {
            if (history.SelectedItems.Count != 1) { status.Text = "请先选择最新一条事务。"; return; }
            long id = (long)history.SelectedItems[0].Tag;
            if (MessageBox.Show(this, "撤销事务 " + id + " 的配置与关系变更？\n撤销会保留新记录；磁盘文件的位置与内容需通过布局备份恢复。", "撤销确认", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            Work(delegate { database.Undo(id); return "已撤销事务 " + id; }, true);
        }
        private void Restore()
        {
            using (OpenFileDialog dialog = new OpenFileDialog { Filter = "数据库备份 (*.db)|*.db" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                string path = dialog.FileName;
                if (MessageBox.Show(this, "将恢复所选数据库中的配置和关系。\n恢复前会自动保留当前数据库完整副本；磁盘文件保持现有状态。", "恢复确认", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                Work(delegate { return "恢复完成，原数据保留在：" + database.Restore(path); }, true);
            }
        }
        private void Export()
        {
            if (history.SelectedItems.Count != 1) { status.Text = "请先选择一条事务。"; return; }
            long id = (long)history.SelectedItems[0].Tag;
            using (SaveFileDialog dialog = new SaveFileDialog { Filter = "变更记录 (*.xml)|*.xml", FileName = "operation-" + id + ".xml" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                string path = dialog.FileName;
                Work(delegate { database.ExportChanges(id, path); return "变更记录已导出：" + path; }, false);
            }
        }
        private void ShowDetails()
        {
            if (history.SelectedItems.Count != 1) return;
            long id = (long)history.SelectedItems[0].Tag;
            using (Form details = new Form { Text = "事务 " + id + "（最多显示 100 条，完整记录可导出）", Size = new Size(900, 540), StartPosition = FormStartPosition.CenterParent })
            {
                TextBox text = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false };
                text.Text = database.ChangeDetails(id); details.Controls.Add(text); details.ShowDialog(this);
            }
        }
    }
}

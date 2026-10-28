using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using System.Xml;
using AppManager.Controls;

namespace AppManager
{
    /// <summary>一份布局备份的概要信息。</summary>
    internal class LayoutBackupInfo
    {
        public string Id = string.Empty;
        public string Name = string.Empty;
        public string Created = string.Empty;
        public int StashCount;
        public int CustomCount;
        public int CategoryCount;
        public long SizeBytes;

        public string Summary
        {
            get
            {
                return Created + " · 收纳 " + StashCount + " · 自定义 " + CustomCount
                    + " · 分类 " + CategoryCount + " · " + FormatSize(SizeBytes);
            }
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) { return bytes + " B"; }
            if (bytes < 1024 * 1024) { return (bytes / 1024) + " KB"; }
            return (bytes / 1024 / 1024) + " MB";
        }
    }

    /// <summary>
    /// 布局备份：把「配置 + 桌面收纳目录」整体快照成一份具名备份，可保存多份并随时恢复。
    /// 备份位于 %LOCALAPPDATA%\AppManager\Backups\&lt;id&gt;\（config.xml + 收纳目录 + meta.xml）。
    /// </summary>
    internal static class LayoutBackup
    {
        public static string RootDir { get { return Paths.BackupsDir; } }

        /// <summary>默认名字：按时间起名，便于在列表里区分。</summary>
        public static string DefaultName()
        {
            return "布局备份 " + DateTime.Now.ToString("MM-dd HH:mm");
        }

        public static List<LayoutBackupInfo> List()
        {
            List<LayoutBackupInfo> result = new List<LayoutBackupInfo>();
            try
            {
                if (!Directory.Exists(RootDir)) { return result; }
                foreach (string dir in Directory.GetDirectories(RootDir))
                {
                    string id = Path.GetFileName(dir);
                    if (id.StartsWith("_", StringComparison.Ordinal)) { continue; }   // 恢复前的临时目录
                    if (id.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) { continue; }
                    // 只认真正的布局备份：config.xml + meta.xml 缺一不可
                    // （部署时留的 deploy_before_* 快照只有 config.xml，不在列表里冒充备份）
                    if (!File.Exists(Path.Combine(dir, "config.xml"))) { continue; }
                    if (!File.Exists(Path.Combine(dir, "meta.xml"))) { continue; }

                    LayoutBackupInfo info = new LayoutBackupInfo();
                    info.Id = id;
                    info.Name = id;
                    ReadMeta(dir, info);
                    info.SizeBytes = DirSize(dir);
                    result.Add(info);
                }
            }
            catch (Exception ex)
            {
                Paths.LogError(ex);
            }

            // 新的排在前面
            result.Sort(delegate(LayoutBackupInfo a, LayoutBackupInfo b)
            {
                return string.Compare(b.Id, a.Id, StringComparison.OrdinalIgnoreCase);
            });
            return result;
        }

        private static void ReadMeta(string dir, LayoutBackupInfo info)
        {
            try
            {
                string metaFile = Path.Combine(dir, "meta.xml");
                if (!File.Exists(metaFile)) return;
                XmlDocument doc = new XmlDocument();
                doc.Load(metaFile);
                XmlNode root = doc.DocumentElement;
                if (root == null) return;

                string name = Attr(root, "name");
                string created = Attr(root, "created");
                if (name.Length > 0) info.Name = name;
                if (created.Length > 0) info.Created = created;

                int n;
                if (int.TryParse(Attr(root, "stash"), out n)) info.StashCount = n;
                if (int.TryParse(Attr(root, "custom"), out n)) info.CustomCount = n;
                if (int.TryParse(Attr(root, "categories"), out n)) info.CategoryCount = n;
            }
            catch (Exception ex)
            {
                Paths.LogError(ex);
            }
        }

        private static string Attr(XmlNode node, string name)
        {
            return (node.Attributes != null && node.Attributes[name] != null)
                ? node.Attributes[name].Value : string.Empty;
        }

        private static long DirSize(string dir)
        {
            long total = 0;
            try
            {
                foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(f).Length; }
                    catch { }
                }
            }
            catch { }
            return total;
        }

        /// <summary>把当前布局另存为一份备份。</summary>
        public static bool Create(string name, out string id, out string error)
        {
            id = null;
            error = null;
            try
            {
                Directory.CreateDirectory(RootDir);

                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string dirId = stamp;
                int n = 2;
                while (Directory.Exists(Path.Combine(RootDir, dirId)))
                {
                    dirId = stamp + "-" + n;
                    n++;
                }

                string target = Path.Combine(RootDir, dirId);
                string temp = target + ".tmp";
                if (Directory.Exists(temp)) { Directory.Delete(temp, true); }
                Directory.CreateDirectory(temp);

                string showName = string.IsNullOrEmpty(name) ? DefaultName() : name.Trim();
                WriteSnapshot(temp, showName, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

                Directory.Move(temp, target);
                id = dirId;
                return true;
            }
            catch (Exception ex)
            {
                Paths.LogError(ex);
                error = ex.Message;
                return false;
            }
        }

        /// <summary>把当前布局（配置 + 桌面收纳）写进指定目录，并生成 meta.xml。</summary>
        private static void WriteSnapshot(string dir, string showName, string created)
        {
            // 先把内存配置落盘，保证快照与当前界面一致
            AppConfig cfg = AppConfig.Load();
            if (cfg.LoadFailed) throw new InvalidDataException("当前配置读取失败，已取消备份。");
            if (!cfg.SaveTo(Path.Combine(dir, "config.xml")))
                throw new IOException("无法写入备份配置。");

            CopyDir(Paths.DesktopBackupDir, Path.Combine(dir, "DesktopBackup"));
            CopyDir(Paths.LegacyStashDir, Path.Combine(dir, "DesktopStash"));

            XmlWriterSettings s = new XmlWriterSettings();
            s.Indent = true;
            s.Encoding = new System.Text.UTF8Encoding(false);
            using (XmlWriter w = XmlWriter.Create(Path.Combine(dir, "meta.xml"), s))
            {
                w.WriteStartElement("backup");
                w.WriteAttributeString("name", showName ?? string.Empty);
                w.WriteAttributeString("created", created);
                w.WriteAttributeString("stash", cfg.DesktopBackup.Count.ToString());
                w.WriteAttributeString("custom", cfg.AddedItems.Count.ToString());
                w.WriteAttributeString("categories", cfg.CustomCategories.Count.ToString());
                w.WriteEndElement();
            }
        }

        /// <summary>读取备份的名称与创建时间，缺失时保持不变。</summary>
        private static void ReadNameCreated(string metaFile, ref string showName, ref string created)
        {
            try
            {
                if (!File.Exists(metaFile)) return;
                XmlDocument doc = new XmlDocument();
                doc.Load(metaFile);
                XmlNode root = doc.DocumentElement;
                if (root == null) return;
                string name = Attr(root, "name");
                string when = Attr(root, "created");
                if (name.Length > 0) showName = name;
                if (when.Length > 0) created = when;
            }
            catch (Exception ex)
            {
                Paths.LogError(ex);
            }
        }

        /// <summary>
        /// 用当前布局刷新一份已有备份：名称与创建时间保持不变，只把内容替换成当前状态。
        /// 先在临时目录写完整份快照再整体换上去，中途失败不会破坏原备份。
        /// </summary>
        public static bool Update(string id, out string error)
        {
            error = null;
            try
            {
                string target = Path.Combine(RootDir, id);
                string metaFile = Path.Combine(target, "meta.xml");
                if (!File.Exists(metaFile))
                {
                    error = "备份不完整（缺少 meta.xml）。";
                    return false;
                }

                string showName = id;
                string created = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                ReadNameCreated(metaFile, ref showName, ref created);

                string temp = target + ".tmp";
                if (Directory.Exists(temp)) { Directory.Delete(temp, true); }
                Directory.CreateDirectory(temp);
                WriteSnapshot(temp, showName, created);

                // 先把旧备份整体挪到 .old 留底，再换入新快照；换入失败则回滚，任何时刻都有一份完整备份
                string old = target + ".old";
                if (Directory.Exists(old)) { Directory.Delete(old, true); }
                if (Directory.Exists(target)) { Directory.Move(target, old); }
                try
                {
                    Directory.Move(temp, target);
                }
                catch
                {
                    if (!Directory.Exists(target) && Directory.Exists(old)) Directory.Move(old, target);
                    throw;
                }
                if (Directory.Exists(old)) { Directory.Delete(old, true); }
                return true;
            }
            catch (Exception ex)
            {
                Paths.LogError(ex);
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 恢复到指定备份：用备份里的配置与收纳目录覆盖当前状态。
        /// 当前收纳目录先整体挪到 Backups\_prerestore_&lt;时间&gt; 下留底，避免误删用户文件。
        /// </summary>
        public static bool Restore(string id, out string error)
        {
            error = null;
            string transaction = null;
            try
            {
                string dir = Path.Combine(RootDir, id);
                string cfgFile = Path.Combine(dir, "config.xml");
                if (!File.Exists(cfgFile))
                {
                    error = "备份不完整（缺少 config.xml）。";
                    return false;
                }
                AppConfig restoredConfig = AppConfig.ImportFrom(cfgFile);
                if (restoredConfig == null || restoredConfig.LoadFailed)
                {
                    error = "备份配置已损坏，已取消恢复。";
                    return false;
                }

                string snapStash = Path.Combine(dir, "DesktopStash");
                if (!Directory.Exists(snapStash) && Directory.Exists(Path.Combine(dir, "stash")))
                    snapStash = Path.Combine(dir, "stash");
                string snapDesktop = Path.Combine(dir, "DesktopBackup");
                bool replaceDesktop = Directory.Exists(snapDesktop);
                bool replaceStash = Directory.Exists(snapStash);

                transaction = Path.Combine(RootDir, "_prerestore_" + DateTime.Now.ToString("yyyyMMdd-HHmmss")
                    + "-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(transaction);
                string oldConfig = Path.Combine(transaction, "config.xml");
                bool hadConfig = File.Exists(Paths.ConfigFile);
                if (hadConfig) File.Copy(Paths.ConfigFile, oldConfig, true);

                string newDesktop = Path.Combine(transaction, "newDesktopBackup");
                string newStash = Path.Combine(transaction, "newDesktopStash");
                if (replaceDesktop) CopyDir(snapDesktop, newDesktop);
                if (replaceStash) CopyDir(snapStash, newStash);

                string oldDesktop = Path.Combine(transaction, "DesktopBackup");
                string oldStash = Path.Combine(transaction, "DesktopStash");
                bool desktopAside = false, desktopInstalled = false;
                bool stashAside = false, stashInstalled = false;
                try
                {
                    if (replaceDesktop)
                    {
                        if (Directory.Exists(Paths.DesktopBackupDir))
                        {
                            Directory.Move(Paths.DesktopBackupDir, oldDesktop);
                            desktopAside = true;
                        }
                        Directory.Move(newDesktop, Paths.DesktopBackupDir);
                        desktopInstalled = true;
                    }
                    if (replaceStash)
                    {
                        if (Directory.Exists(Paths.LegacyStashDir))
                        {
                            Directory.Move(Paths.LegacyStashDir, oldStash);
                            stashAside = true;
                        }
                        Directory.Move(newStash, Paths.LegacyStashDir);
                        stashInstalled = true;
                    }
                    File.Copy(cfgFile, Paths.ConfigFile, true);
                    if (hadConfig)
                        File.Copy(oldConfig, Path.Combine(Paths.DataDir, "config.before-restore.xml"), true);
                }
                catch
                {
                    if (desktopInstalled && Directory.Exists(Paths.DesktopBackupDir))
                        Directory.Delete(Paths.DesktopBackupDir, true);
                    if (desktopAside && Directory.Exists(oldDesktop) && !Directory.Exists(Paths.DesktopBackupDir))
                        Directory.Move(oldDesktop, Paths.DesktopBackupDir);
                    if (stashInstalled && Directory.Exists(Paths.LegacyStashDir))
                        Directory.Delete(Paths.LegacyStashDir, true);
                    if (stashAside && Directory.Exists(oldStash) && !Directory.Exists(Paths.LegacyStashDir))
                        Directory.Move(oldStash, Paths.LegacyStashDir);
                    if (hadConfig && File.Exists(oldConfig)) File.Copy(oldConfig, Paths.ConfigFile, true);
                    else if (!hadConfig && File.Exists(Paths.ConfigFile)) File.Delete(Paths.ConfigFile);
                    throw;
                }
                return true;
            }
            catch (Exception ex)
            {
                Paths.LogError(ex);
                error = ex.Message;
                return false;
            }
        }

        public static bool Delete(string id, out string error)
        {
            error = null;
            try
            {
                string dir = Path.Combine(RootDir, id);
                if (Directory.Exists(dir)) { Directory.Delete(dir, true); }
                return true;
            }
            catch (Exception ex)
            {
                Paths.LogError(ex);
                error = ex.Message;
                return false;
            }
        }

        public static bool Rename(string id, string name, out string error)
        {
            error = null;
            try
            {
                if (string.IsNullOrEmpty(name) || name.Trim().Length == 0)
                {
                    error = "名称不能为空。";
                    return false;
                }
                string metaFile = Path.Combine(RootDir, id, "meta.xml");
                if (!File.Exists(metaFile))
                {
                    error = "备份不完整（缺少 meta.xml）。";
                    return false;
                }

                string showName = id;
                string created = string.Empty;
                ReadNameCreated(metaFile, ref showName, ref created);

                XmlWriterSettings s = new XmlWriterSettings();
                s.Indent = true;
                s.Encoding = new System.Text.UTF8Encoding(false);
                using (XmlWriter w = XmlWriter.Create(metaFile + ".tmp", s))
                {
                    w.WriteStartElement("backup");
                    w.WriteAttributeString("name", name.Trim());
                    w.WriteAttributeString("created", created);
                    LayoutBackupInfo info = new LayoutBackupInfo();
                    ReadMeta(Path.Combine(RootDir, id), info);
                    w.WriteAttributeString("stash", info.StashCount.ToString());
                    w.WriteAttributeString("custom", info.CustomCount.ToString());
                    w.WriteAttributeString("categories", info.CategoryCount.ToString());
                    w.WriteEndElement();
                }
                if (File.Exists(metaFile)) File.Delete(metaFile);
                File.Move(metaFile + ".tmp", metaFile);
                return true;
            }
            catch (Exception ex)
            {
                Paths.LogError(ex);
                error = ex.Message;
                return false;
            }
        }

        private static void CopyDir(string from, string to)
        {
            if (string.IsNullOrEmpty(from) || !Directory.Exists(from)) return;
            if (!Directory.Exists(to)) Directory.CreateDirectory(to);
            foreach (string f in Directory.GetFiles(from))
                File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
            foreach (string d in Directory.GetDirectories(from))
            {
                CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
            }
        }
    }

    /// <summary>布局备份管理对话框：列表 + 新建 / 更新 / 重命名 / 删除 / 恢复。</summary>
    internal class BackupForm : Form
    {
        private readonly ListBox list;
        private readonly List<LayoutBackupInfo> items = new List<LayoutBackupInfo>();
        private readonly RoundButton btnRestore;
        private readonly RoundButton btnUpdate;
        private readonly RoundButton btnRename;
        private readonly RoundButton btnDelete;
        private bool operationRunning;

        /// <summary>是否执行过恢复（主窗口据此重载配置与重扫）。</summary>
        public bool Restored;

        public BackupForm()
        {
            Text = "布局备份";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            BackColor = Theme.Background;
            ClientSize = new Size(620, 486);
            KeyPreview = true;
            try { Region = new Region(Draw.RoundedPath(ClientRectangle, Theme.CornerRadius)); } catch { }

            int pad = 24;
            list = new ListBox();
            list.BorderStyle = BorderStyle.None;
            list.BackColor = Theme.Card;
            list.ForeColor = Theme.TextPrimary;
            list.Font = Theme.BodyFont;
            list.ItemHeight = 54;
            list.DrawMode = DrawMode.OwnerDrawFixed;
            list.IntegralHeight = false;
            list.Bounds = new Rectangle(pad, 104, ClientSize.Width - pad * 2, 300);
            list.DrawItem += OnDrawItem;
            list.DoubleClick += delegate { RestoreSelected(); };
            list.SelectedIndexChanged += delegate { UpdateButtons(); };
            Controls.Add(list);

            RoundButton btnCreate = MakeButton("新建备份", true);
            btnCreate.Location = new Point(pad, 420);
            btnCreate.Click += delegate { CreateBackup(); };
            Controls.Add(btnCreate);

            btnUpdate = MakeButton("更新", false);
            btnUpdate.Location = new Point(pad + 100, 420);
            btnUpdate.Click += delegate { UpdateBackup(); };
            Controls.Add(btnUpdate);

            btnRename = MakeButton("重命名", false);
            btnRename.Location = new Point(pad + 197, 420);
            btnRename.Click += delegate { RenameBackup(); };
            Controls.Add(btnRename);

            btnDelete = MakeButton("删除", false);
            btnDelete.Location = new Point(pad + 294, 420);
            btnDelete.Click += delegate { DeleteBackup(); };
            Controls.Add(btnDelete);

            btnRestore = MakeButton("恢复", true);
            btnRestore.Location = new Point(ClientSize.Width - pad - 88 - 88 - 8, 420);
            btnRestore.Click += delegate { RestoreSelected(); };
            Controls.Add(btnRestore);

            RoundButton btnClose = MakeButton("关闭", false);
            btnClose.Location = new Point(ClientSize.Width - pad - 88, 420);
            btnClose.Click += delegate { Close(); };
            Controls.Add(btnClose);

            ReloadList();
        }

        private RoundButton MakeButton(string text, bool accent)
        {
            RoundButton b = new RoundButton();
            b.Text = text;
            b.Accent = accent;
            b.Size = new Size(88, 34);
            return b;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            using (SolidBrush tb = new SolidBrush(Theme.TextPrimary))
                Draw.DrawStringLeft(g, "布局备份", Theme.DialogTitleFont, tb, new Rectangle(24, 22, 260, 30));
            using (SolidBrush db = new SolidBrush(Theme.TextSecondary))
            {
                Draw.DrawStringLeft(g, "备份会保存分类、顺序、常用、隐藏与桌面收纳的完整状态，可保存多份。", Theme.CaptionFont, db,
                    new Rectangle(24, 56, ClientSize.Width - 48, 22));
                Draw.DrawStringLeft(g, "行内双击可恢复；当前收纳目录在恢复前会另存留底，不会直接删除。", Theme.CaptionFont, db,
                    new Rectangle(24, 76, ClientSize.Width - 48, 22));
            }
            Draw.DrawShadow(g, list.Bounds, 10, 0.35f);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            if (keyData == Keys.Down || keyData == Keys.Up) return base.ProcessCmdKey(ref msg, keyData);
            if (keyData == Keys.Enter) { RestoreSelected(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void OnDrawItem(object sender, DrawItemEventArgs e)
        {
            Graphics g = e.Graphics;
            e.DrawBackground();
            if (e.Index < 0 || e.Index >= items.Count) return;
            LayoutBackupInfo info = items[e.Index];
            bool selected = (e.State & DrawItemState.Selected) != 0;

            Rectangle r = e.Bounds;
            using (SolidBrush bg = new SolidBrush(selected ? Color.FromArgb(235, 240, 250) : Theme.Card))
                g.FillRectangle(bg, r);
            if (e.Index < items.Count - 1)
            {
                using (Pen p = new Pen(Theme.Separator))
                    g.DrawLine(p, r.Left + 14, r.Bottom - 1, r.Right - 14, r.Bottom - 1);
            }

            using (SolidBrush nb = new SolidBrush(Theme.TextPrimary))
                Draw.DrawStringLeft(g, info.Name, Theme.BodyFont, nb, new Rectangle(r.Left + 14, r.Top + 8, r.Width - 28, 20));
            using (SolidBrush sb = new SolidBrush(Theme.TextSecondary))
                Draw.DrawStringLeft(g, info.Summary, Theme.CaptionFont, sb, new Rectangle(r.Left + 14, r.Top + 30, r.Width - 28, 18));
        }

        private void ReloadList()
        {
            int sel = list.SelectedIndex;
            items.Clear();
            items.AddRange(LayoutBackup.List());
            list.BeginUpdate();
            list.Items.Clear();
            foreach (LayoutBackupInfo info in items) list.Items.Add(info.Name);
            list.EndUpdate();
            if (items.Count > 0) list.SelectedIndex = Math.Min(Math.Max(sel, 0), items.Count - 1);
            UpdateButtons();
            list.Invalidate();
        }

        private void UpdateButtons()
        {
            bool has = Selected != null && !operationRunning;
            btnRestore.Enabled = has;
            btnUpdate.Enabled = has;
            btnRename.Enabled = has;
            btnDelete.Enabled = has;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (operationRunning) e.Cancel = true;
            base.OnFormClosing(e);
        }

        private LayoutBackupInfo Selected
        {
            get
            {
                int i = list.SelectedIndex;
                return (i >= 0 && i < items.Count) ? items[i] : null;
            }
        }

        // ---------- 操作 ----------

        private void CreateBackup()
        {
            string name = InputDialog.Show("新建备份", "给这份布局起个名字（留空则按时间命名）。", LayoutBackup.DefaultName());
            if (name == null) return;   // 取消

            string id = null;
            RunBackupOperation(delegate
            {
                string error;
                return LayoutBackup.Create(name, out id, out error) ? null : error;
            }, delegate
            {
                ReloadList();
                for (int i = 0; i < items.Count; i++)
                {
                    if (string.Equals(items[i].Id, id, StringComparison.OrdinalIgnoreCase))
                    {
                        list.SelectedIndex = i;
                        break;
                    }
                }
            });
        }

        private void UpdateBackup()
        {
            LayoutBackupInfo info = Selected;
            if (info == null) return;
            if (!ConfirmDialog.Show("更新备份", "用当前布局刷新备份「" + info.Name + "」？\r\n\r\n" +
                "备份里的内容会被当前状态覆盖，名称与创建时间保持不变。")) return;

            RunBackupOperation(delegate
            {
                string error;
                return LayoutBackup.Update(info.Id, out error) ? null : error;
            }, ReloadList);
        }

        private void RestoreSelected()
        {
            LayoutBackupInfo info = Selected;
            if (info == null) return;
            if (!ConfirmDialog.Show("恢复备份", "恢复备份「" + info.Name + "」？\r\n\r\n" + info.Summary +
                "\r\n\r\n当前布局会被换成备份时的状态，列表内容与桌面收纳都会一起回到那个时间点。")) return;

            RunBackupOperation(delegate
            {
                string error;
                return LayoutBackup.Restore(info.Id, out error) ? null : error;
            }, delegate
            {
                Restored = true;
                MessageBox.Show(this, "已恢复到「" + info.Name + "」。\r\n桌面收纳与分类布局已回到备份时的状态。",
                    "布局备份", MessageBoxButtons.OK, MessageBoxIcon.Information);
                ReloadList();
            });
        }

        private void RenameBackup()
        {
            LayoutBackupInfo info = Selected;
            if (info == null) return;
            string name = InputDialog.Show("重命名备份", "输入新的备份名称。", info.Name);
            if (name == null) return;

            RunBackupOperation(delegate
            {
                string error;
                return LayoutBackup.Rename(info.Id, name, out error) ? null : error;
            }, ReloadList);
        }

        private void DeleteBackup()
        {
            LayoutBackupInfo info = Selected;
            if (info == null) return;
            if (!ConfirmDialog.Show("删除备份", "删除备份「" + info.Name + "」？\r\n\r\n" + info.Summary +
                "\r\n\r\n备份文件会被永久删除，已经恢复过的布局不受影响。")) return;

            RunBackupOperation(delegate
            {
                string error;
                return LayoutBackup.Delete(info.Id, out error) ? null : error;
            }, ReloadList);
        }

        private void RunBackupOperation(Func<string> operation, Action onSuccess)
        {
            if (operationRunning) return;
            operationRunning = true;
            list.Enabled = false;
            UseWaitCursor = true;
            UpdateButtons();
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error = null;
                try { error = operation(); }
                catch (Exception ex) { error = ex.Message; Paths.LogError(ex); }
                if (IsDisposed) return;
                try
                {
                    BeginInvoke((Action)delegate
                    {
                        operationRunning = false;
                        list.Enabled = true;
                        UseWaitCursor = false;
                        UpdateButtons();
                        if (error != null)
                        {
                            MessageBox.Show(this, "操作失败：" + error, "布局备份",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return;
                        }
                        onSuccess();
                    });
                }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            });
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace AppManager
{
    /// <summary>
    /// 旧版存储位置收尾：软件页面不再收录文件后，旧版「文件夹/文件存储位置」里还留着已转为资源的条目。
    /// 启动时询问一次，确认后按资源分类搬进资源存储位置并改写记录；没有收录的文件、软件条目一律不动。
    /// 每批先写待办日志再搬，整批搬完保存一次：保存失败把这批搬回原处；进程中断时下次启动按日志补登记。
    /// </summary>
    public partial class MainForm
    {
        /// <summary>每批最多搬运的条目数；跨卷复制较慢时到时限也提交，缩短记录与磁盘不一致的窗口。</summary>
        private const int LegacyMoveBatch = 50;
        private static readonly TimeSpan LegacyMoveBatchTime = TimeSpan.FromSeconds(2);
        private const int LegacyReportLines = 10;

        internal sealed class LegacyMove
        {
            internal string Source, Target, Name;
            internal bool Folder, Moved, SourceRemoved;
        }

        /// <summary>界面线程取的配置快照：后台线程只读这些字符串，不碰配置对象。</summary>
        internal sealed class LegacyStoreScan
        {
            internal string DatabaseFile, ResourceRoot;
            internal readonly List<string> LegacyStores = new List<string>(), ResourceRoots = new List<string>(), Structural = new List<string>();
        }

        internal sealed class LegacyMoveResult
        {
            internal int Moved;
            internal string Error;
            internal readonly List<string> Failed = new List<string>(), Kept = new List<string>(), Skipped = new List<string>();
        }

        /// <summary>当前批次的待办日志：每行「已搬\t文件夹\t原路径\t新路径」（路径不可能含制表符）。</summary>
        internal static string LegacyMoveJournal { get { return Path.Combine(Paths.DataDir, "legacy-store-move.pending"); } }

        /// <summary>启动时排进导入队列：排在中断导入的恢复之后，与导入、删源串行执行。</summary>
        private void StartLegacyStoreMigration()
        {
            if (config == null || config.LoadFailed || config.Database == null) return;
            LegacyStoreScan scan = SnapshotLegacyStores();
            CancellationToken cancel = importCancel.Token;
            EnqueueImport(delegate { RunLegacyStoreMigration(scan, cancel); });
        }

        private LegacyStoreScan SnapshotLegacyStores()
        {
            LegacyStoreScan scan = new LegacyStoreScan { DatabaseFile = config.Database.FilePath, ResourceRoot = ConfiguredResourceStorePath() };
            StoreRoots(scan.ResourceRoots, scan.LegacyStores, scan.Structural);
            return scan;
        }

        private void RunLegacyStoreMigration(LegacyStoreScan scan, CancellationToken cancel)
        {
            Func<List<LegacyMove>, List<LegacyMove>, bool> commit = delegate(List<LegacyMove> done, List<LegacyMove> stale)
            { return (bool)Invoke(new Func<bool>(delegate { return CommitLegacyMoves(done, stale); })); };
            LegacyMoveResult result = new LegacyMoveResult();
            try
            {
                if (ResumeLegacyMoves(commit)) RunOnUi(delegate { StartScan(false, true); });
                List<LegacyMove> moves = FindLegacyMoves(scan, result.Skipped);
                if (moves.Count == 0 || cancel.IsCancellationRequested) return;
                bool accepted = (bool)Invoke(new Func<bool>(delegate
                {
                    if (importExitPending) return false;
                    if (!ConfirmDialog.Show("整理旧文件", "旧版存储位置里还有 " + moves.Count + " 个已收录的文件/文件夹。\n要按分类移到资源存储位置吗？",
                        "移动", "以后再说")) return false;
                    ShowStatusBar("正在把旧文件移到资源存储位置…", 0, moves.Count);
                    return true;
                }));
                if (!accepted) return;
                RunLegacyMoves(moves, commit, UpdateStatus, cancel, result);
            }
            catch (Exception ex)
            {
                // 日志保留：已搬未登记的条目由下次启动补登记
                Paths.LogError(new IOException("旧版文件整理中断。", ex));
                result.Error = "整理中断：" + ex.Message;
            }
            RunOnUi(delegate { FinishLegacyStoreMigration(result, scan.ResourceRoot); });
        }

        /// <summary>
        /// 后台线程：查出要搬的旧版条目。只看旧版存储根的下一层（旧版导入就放在这一层），且仍在磁盘上、
        /// 不是存储结构本身、与资源存储位置互不包含。该路径有软件记录、是程序或快捷方式、本身是链接、
        /// 文件夹内还有单独收录的项目时不搬，写进 skipped。目标按首条记录的资源分类放进资源存储位置。
        /// </summary>
        internal static List<LegacyMove> FindLegacyMoves(LegacyStoreScan scan, List<string> skipped)
        {
            List<LegacyMove> moves = new List<LegacyMove>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in scan.LegacyStores)
            {
                if (scan.ResourceRoots.Any(r => IsUnder(r, root))) continue;   // 旧版位置就是（或位于）资源存储位置
                // 同一路径可能登记在多个分类：按路径归组，保持 id 顺序
                Dictionary<string, List<string[]>> byPath = new Dictionary<string, List<string[]>>(StringComparer.OrdinalIgnoreCase);
                List<string> order = new List<string>();
                foreach (string[] row in AddedEntryList.DirectChildren(scan.DatabaseFile, root))
                {
                    List<string[]> rows;
                    if (!byPath.TryGetValue(row[0], out rows)) { byPath[row[0]] = rows = new List<string[]>(); order.Add(row[0]); }
                    rows.Add(row);
                }
                foreach (string source in order)
                {
                    if (!seen.Add(source)) continue;
                    if (scan.Structural.Any(d => SamePath(d, source)) || scan.ResourceRoots.Any(r => IsUnder(source, r))) continue;
                    List<string[]> rows = byPath[source];
                    string name = Path.GetFileName(source.TrimEnd('\\', '/'));
                    try
                    {
                        bool folder = Directory.Exists(source);
                        if (!folder && !File.Exists(source)) continue;   // 已失效：没有可搬的内容
                        if (rows.Any(r => r[2] != "1")) { skipped.Add(name + "：软件页面的条目"); continue; }
                        if (!folder && IsLauncherFile(source)) { skipped.Add(name + "：程序或快捷方式"); continue; }
                        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) { skipped.Add(name + "：链接"); continue; }
                        if (folder && AddedEntryList.HasEntriesUnder(scan.DatabaseFile, source)) { skipped.Add(name + "：文件夹内还有单独收录的项目"); continue; }
                        string dir = Path.GetFullPath(Path.Combine(scan.ResourceRoot, SafeDirName(rows[0][3])));
                        moves.Add(new LegacyMove { Source = source, Name = name, Folder = folder, Target = dir });
                    }
                    catch (Exception ex)
                    {
                        if (!(ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)) throw;
                        skipped.Add(name + "：" + ex.Message);
                    }
                }
            }
            AssignLegacyTargets(scan.DatabaseFile, moves);
            return moves;
        }

        /// <summary>目标名：避开磁盘上已有的、曾收录过的（避免继承旧条目的设置）与本批已占用的名称，重名追加 _1、_2…</summary>
        private static void AssignLegacyTargets(string databaseFile, List<LegacyMove> moves)
        {
            Dictionary<string, string> taken = AddedEntryList.RegisteredPaths(databaseFile, moves.Select(m => Path.Combine(m.Target, m.Name)));
            HashSet<string> reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (LegacyMove m in moves)
            {
                string dir = m.Target, target = Path.Combine(dir, m.Name);
                for (int i = 1; reserved.Contains(target) || File.Exists(target) || Directory.Exists(target) || taken.ContainsKey(target); i++)
                {
                    // 文件夹没有扩展名：「2026.10」追加为「2026.10_1」而不是「2026_1.10」
                    target = Path.Combine(dir, m.Folder ? m.Name + "_" + i
                        : Path.GetFileNameWithoutExtension(m.Name) + "_" + i + Path.GetExtension(m.Name));
                    foreach (KeyValuePair<string, string> hit in AddedEntryList.RegisteredPaths(databaseFile, new[] { target })) taken[hit.Key] = hit.Value;
                }
                reserved.Add(target);
                m.Target = target;
            }
        }

        /// <summary>
        /// 后台线程：分批搬运。每项搬之前和搬完都重写待办日志；一批搬完调用 commit 改写记录并保存一次。
        /// 保存失败时这批全部搬回原处并停止；搬运期间条目被删除或改动的（stale）搬回原处、不登记。
        /// 单项失败只记入 Failed，继续下一项；取消后提交已搬的，其余留到下次启动。
        /// </summary>
        internal static void RunLegacyMoves(List<LegacyMove> moves, Func<List<LegacyMove>, List<LegacyMove>, bool> commit,
            Action<int, int, string> progress, CancellationToken cancel, LegacyMoveResult result)
        {
            int index = 0;
            while (index < moves.Count && !cancel.IsCancellationRequested)
            {
                List<LegacyMove> batch = new List<LegacyMove>();
                Stopwatch watch = Stopwatch.StartNew();
                while (index < moves.Count && batch.Count < LegacyMoveBatch && watch.Elapsed < LegacyMoveBatchTime && !cancel.IsCancellationRequested)
                {
                    LegacyMove m = moves[index++];
                    if (progress != null) progress(index - 1, moves.Count, m.Name);
                    batch.Add(m);
                    if (!WriteLegacyJournal(batch))
                    {
                        // 没有日志就不动磁盘：中断时无法补登记
                        batch.Remove(m);
                        result.Failed.Add(m.Name + "：无法写入整理日志");
                        index = moves.Count;
                        break;
                    }
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(m.Target));
                        Paths.LogRenameIntent("整理旧文件", m.Source, m.Target);
                        m.SourceRemoved = m.Folder ? DesktopOrganizer.MoveDirectory(m.Source, m.Target) : DesktopOrganizer.MoveFile(m.Source, m.Target);
                        m.Moved = true;
                        WriteLegacyJournal(batch);   // 失败无妨：目标已在、原路径已空时补登记照样认得
                    }
                    catch (Exception ex)
                    {
                        Paths.LogError(new IOException("旧版文件未能移动: " + m.Source, ex));
                        result.Failed.Add(m.Name + "：" + ex.Message.TrimEnd('。', '.', '\r', '\n'));
                    }
                }

                List<LegacyMove> done = batch.Where(m => m.Moved).ToList();
                if (done.Count == 0) { DeleteLegacyJournal(); continue; }
                List<LegacyMove> stale = new List<LegacyMove>();
                if (!commit(done, stale))
                {
                    List<string> stuck = RollbackLegacyMoves(done);
                    // 搬不回去的留在日志里：下次启动补登记到新位置
                    if (stuck.Count == 0) DeleteLegacyJournal();
                    result.Error = stuck.Count == 0 ? "资源库未能保存，这一批已搬回原位置，下次启动再试。"
                        : "资源库未能保存，且有 " + stuck.Count + " 项未能搬回原位置（下次启动会补登记）：\n" + string.Join("\n", stuck.Take(LegacyReportLines));
                    return;
                }
                foreach (string error in RollbackLegacyMoves(stale)) result.Failed.Add(error);
                DeleteLegacyJournal();
                foreach (LegacyMove m in done)
                {
                    if (stale.Contains(m)) continue;
                    result.Moved++;
                    if (!m.SourceRemoved) result.Kept.Add(m.Source);
                }
            }
        }

        /// <summary>
        /// 界面线程：把已搬好的条目改写到新路径并保存一次。记录已不在原路径或已不全是资源的（搬运期间被删除或改动）
        /// 放进 stale、不登记。返回是否保存成功（没有需要改写的也算成功）；失败时内存已随 Save 从数据库重载。
        /// </summary>
        private bool CommitLegacyMoves(List<LegacyMove> done, List<LegacyMove> stale)
        {
            int rekeyed = 0;
            foreach (LegacyMove m in done)
            {
                List<AddedEntry> records = EntriesAt(m.Source);
                if (records.Count == 0 || records.Any(r => !r.IsResource)) { stale.Add(m); continue; }
                ForgetMoveOutAfterRename(m.Source);
                RekeyEntry("add:" + m.Source, "add:" + m.Target, m.Source, m.Target, m.Folder);
                rekeyed++;
            }
            if (rekeyed == 0) return true;
            bool saved = config.Save("physical.files.legacy");
            InvalidateAddedIndex();
            if (saved) foreach (LegacyMove m in done) if (!stale.Contains(m)) Paths.LogRenameIntent("已保存", m.Source, m.Target);
            return saved;
        }

        /// <summary>把已搬的条目搬回原处；返回搬不回去的说明（文件仍在新位置，不会丢）。</summary>
        private static List<string> RollbackLegacyMoves(List<LegacyMove> moves)
        {
            List<string> errors = new List<string>();
            foreach (LegacyMove m in moves)
            {
                try { RollbackFolderMove(m.Source, m.Target, m.Folder, m.SourceRemoved); }
                catch (Exception ex)
                {
                    Paths.LogError(new IOException("旧版文件未能搬回原处: " + m.Target + " => " + m.Source, ex));
                    errors.Add(m.Name + "：仍在 " + m.Target);
                }
            }
            return errors;
        }

        /// <summary>
        /// 上次在保存前中断：目标已在、且日志记着已搬完或原路径已空的条目，补登记到新位置；其余不动、不删任何文件。
        /// 记录已不在原路径的（已提交、或条目已删除）忽略。补登记保存成功后删除日志，失败则留到下次启动。
        /// 返回是否有条目被补登记。
        /// </summary>
        internal static bool ResumeLegacyMoves(Func<List<LegacyMove>, List<LegacyMove>, bool> commit)
        {
            List<LegacyMove> pending = ReadLegacyJournal();
            if (pending == null) return false;
            List<LegacyMove> done = pending.Where(m => (m.Moved || !EntryExists(m.Source, m.Folder)) && EntryExists(m.Target, m.Folder)).ToList();
            List<LegacyMove> stale = new List<LegacyMove>();
            if (done.Count > 0 && !commit(done, stale)) return false;
            DeleteLegacyJournal();
            return done.Count > stale.Count;
        }

        private static bool EntryExists(string path, bool folder)
        {
            return folder ? Directory.Exists(path) : File.Exists(path);
        }

        /// <summary>先写临时文件再替换，进程中断时日志要么是旧版本、要么是新版本。</summary>
        private static bool WriteLegacyJournal(List<LegacyMove> batch)
        {
            string path = LegacyMoveJournal, temp = path + ".tmp";
            try
            {
                StringBuilder text = new StringBuilder();
                foreach (LegacyMove m in batch)
                    text.Append(m.Moved ? '1' : '0').Append('\t').Append(m.Folder ? '1' : '0').Append('\t')
                        .Append(m.Source).Append('\t').Append(m.Target).Append("\r\n");
                File.WriteAllText(temp, text.ToString(), Encoding.UTF8);
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
                return true;
            }
            catch (Exception ex)
            {
                if (!(ex is IOException || ex is UnauthorizedAccessException)) throw;
                Paths.LogError(new IOException("整理日志写入失败: " + path, ex));
                return false;
            }
        }

        /// <summary>没有日志返回 null；格式不对的行跳过。读取失败时记日志并返回 null（日志保留，下次再试）。</summary>
        internal static List<LegacyMove> ReadLegacyJournal()
        {
            string path = LegacyMoveJournal;
            if (!File.Exists(path)) return null;
            try
            {
                List<LegacyMove> moves = new List<LegacyMove>();
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string[] parts = line.Split('\t');
                    if (parts.Length != 4) continue;
                    try
                    {
                        if (!Path.IsPathRooted(parts[2]) || !Path.IsPathRooted(parts[3])) continue;
                        moves.Add(new LegacyMove { Moved = parts[0] == "1", Folder = parts[1] == "1", Source = parts[2], Target = parts[3],
                            Name = Path.GetFileName(parts[2].TrimEnd('\\', '/')) });
                    }
                    catch (ArgumentException) { }   // 路径含非法字符：跳过这一行
                }
                return moves;
            }
            catch (Exception ex)
            {
                if (!(ex is IOException || ex is UnauthorizedAccessException)) throw;
                Paths.LogError(new IOException("整理日志读取失败: " + path, ex));
                return null;
            }
        }

        private static void DeleteLegacyJournal()
        {
            try { File.Delete(LegacyMoveJournal); }
            catch (Exception ex)
            {
                if (!(ex is IOException || ex is UnauthorizedAccessException)) throw;
                Paths.LogError(new IOException("整理日志删除失败: " + LegacyMoveJournal, ex));
            }
        }

        private void FinishLegacyStoreMigration(LegacyMoveResult result, string resourceRoot)
        {
            if (result.Moved > 0) StartScan(false, true);
            if (importExitPending) return;
            HideStatusBar();
            List<string> lines = new List<string>();
            if (result.Error != null) lines.Add(result.Error);
            AppendLegacyReport(lines, "未能移动：", result.Failed);
            AppendLegacyReport(lines, "已移到新位置，但原位置有内容未能删除（可能正被使用，或在移动期间被修改），已保留：", result.Kept);
            AppendLegacyReport(lines, "以下项目没有自动移动，留在原处：", result.Skipped);
            if (result.Moved == 0 && result.Error == null && result.Failed.Count == 0) return;   // 没搬任何东西：不打扰
            string head = result.Moved > 0 ? "已把 " + result.Moved + " 个旧文件/文件夹移到资源存储位置（" + resourceRoot + "）。"
                : "旧文件没有移动，下次启动会再询问。";
            if (lines.Count == 0) { ShowNotice(head); return; }
            MessageBox.Show(this, head + "\n\n" + string.Join("\n\n", lines), "整理旧文件", MessageBoxButtons.OK,
                result.Error != null || result.Failed.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }

        private static void AppendLegacyReport(List<string> lines, string title, List<string> items)
        {
            if (items.Count == 0) return;
            string text = title + "\n" + string.Join("\n", items.Take(LegacyReportLines));
            if (items.Count > LegacyReportLines) text += "\n……共 " + items.Count + " 项";
            lines.Add(text);
        }
    }
}

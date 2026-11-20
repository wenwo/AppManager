using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;

namespace AppManager
{
    /// <summary>导出计划里的一个集合：导出后对应一个文件夹，Sources 是它直接成员的实体路径。</summary>
    internal sealed class CollectionExportNode
    {
        internal string Name = string.Empty;   // 集合名（未清洗），文件夹名由它生成
        internal string Key = string.Empty;    // 集合键：数据库模式按它分页读取成员
        internal List<string> Sources = new List<string>();
        internal int Unavailable;              // 没有实体文件的成员（网址，或文件已被移走、删除）
        internal List<CollectionExportNode> Children = new List<CollectionExportNode>();
    }

    /// <summary>不能作为导出位置的目录（相等或位于其下都不行）。</summary>
    internal sealed class CollectionExportForbidden
    {
        internal string Path, Label;
    }

    internal sealed class CollectionExportResult
    {
        internal string Root;
        internal bool Kept, Canceled, SpaceLimited;
        internal bool Declined;                                  // 部分失败时用户选择了全部撤回
        internal bool Refused;                                   // 没开始复制就停下（导出文件夹建不了或刚被占用）
        internal string Error;                                   // 整批停止的原因（日志写不进、临时副本未能清理等）
        internal int Copied, Unavailable;
        internal List<string> Failures = new List<string>();     // 「名称：原因」
        internal List<string> Leftovers = new List<string>();    // 撤回时未能删除、需要用户核对的路径
    }

    /// <summary>
    /// 集合导出：把集合（含子集合）成员的实体文件复制到新建的同名文件夹，子集合对应子文件夹。
    /// 复制复用 FileImport（先写临时副本、校验长度与哈希后改名，不覆盖已有文件，拒绝链接与联接点）；
    /// 只复制、不删源、不改资源库。取消、磁盘满或用户选择撤回时，按身份与内容核对后删除本批副本，
    /// 再删除本批新建且已空的文件夹；中断留下的日志在下次启动时按同样规则收尾。
    /// 撤回与恢复只动「本批建的那个导出文件夹」里的东西：导出前先建好导出文件夹并记下它的身份标记，
    /// 之后每次删除前都核对标记，文件夹已被删掉又被别的导出重建时不会误删。
    /// </summary>
    internal static class CollectionExport
    {
        internal const string FallbackName = "集合";
        internal const int JournalKeep = 50;

        /// <summary>集合名转成可用的文件夹名（规则见 FileNames.SafeName），清洗后为空时用「集合」。</summary>
        internal static string SafeFolderName(string name)
        {
            return FileNames.SafeName(name, FallbackName);
        }

        private static bool IsPathFailure(Exception ex)
        {
            return ex is IOException || ex is UnauthorizedAccessException || ex is Win32Exception
                || ex is ArgumentException || ex is NotSupportedException || ex is System.Security.SecurityException;
        }

        /// <summary>
        /// 只按路径字符串检查（界面线程可调用，不访问磁盘）：必须是带盘符或网络共享的完整路径，
        /// 且不能是禁止的目录或位于其中。返回错误说明，通过时返回 null。
        /// </summary>
        internal static string TargetError(string folder, IEnumerable<CollectionExportForbidden> forbidden)
        {
            string root;
            try
            {
                if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathRooted(folder)) return "请选择一个完整的文件夹路径。";
                root = Path.GetPathRoot(folder);
            }
            catch (ArgumentException) { return "文件夹路径格式无效。"; }
            if (!(root.StartsWith(@"\\", StringComparison.Ordinal) || (root.Length >= 3 && root[1] == ':')))
                return "请选择一个完整的文件夹路径。";
            if (forbidden == null) return null;
            foreach (CollectionExportForbidden item in forbidden)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Path)) continue;
                if (Within(item.Path, folder)) return ForbiddenMessage(item);
            }
            return null;
        }

        /// <summary>child 与 parent 相同或位于其下；路径格式无效时按不相干处理。</summary>
        private static bool Within(string parent, string child)
        {
            try { return FileImport.Within(parent, child); }
            catch (Exception ex)
            {
                if (!IsPathFailure(ex)) throw;
                return false;
            }
        }

        private static string ForbiddenMessage(CollectionExportForbidden item)
        {
            return "不能导出到" + item.Label + "「" + item.Path + "」或其中的文件夹，请换一个位置。";
        }

        /// <summary>
        /// 按磁盘上的真实位置再查一遍（后台调用）：导出根最近的已存在上级目录和每个禁止目录都取真实路径
        /// （跟随联接点和链接；subst 盘、映射盘还原到所在磁盘或共享；8.3 短名、\\?\ 前缀还原成普通写法），
        /// 再看前者是否落在后者之内。只比较路径，不依赖文件 ID，部分 NAS 不提供稳定文件 ID 也不会误拦。
        /// 每个禁止目录只打开一次，循环中可取消；取不到真实路径的目录跳过（字符串检查已做过）。
        /// 剩余风险：服务器上同一目录另起共享名时，本机无法还原出它的真实位置。
        /// </summary>
        internal static string RealLocationError(string exportRoot, IEnumerable<CollectionExportForbidden> forbidden, CancellationToken cancel)
        {
            string current;
            try { current = Path.GetFullPath(exportRoot); }
            catch (Exception ex) { if (!IsPathFailure(ex)) throw; return "文件夹路径格式无效。"; }
            string final = null;
            for (; final == null && !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                try { if (Directory.Exists(current)) final = FileImport.FinalPath(current); }
                catch (Exception ex) { if (!IsPathFailure(ex)) throw; }
            }
            if (final == null) return null;
            foreach (CollectionExportForbidden item in forbidden ?? new CollectionExportForbidden[0])
            {
                cancel.ThrowIfCancellationRequested();
                if (item == null || string.IsNullOrWhiteSpace(item.Path)) continue;
                string blocked;
                try
                {
                    if (!Directory.Exists(item.Path)) continue;
                    blocked = FileImport.FinalPath(item.Path);
                }
                catch (Exception ex) { if (!IsPathFailure(ex)) throw; continue; }
                if (Within(blocked, final)) return ForbiddenMessage(item);
            }
            return null;
        }

        /// <summary>
        /// 后台准备导出批次：核对成员是否仍在磁盘上，选定不重名的导出根，按字符串与真实位置两层检查禁止目录
        /// （成员文件夹本身也列为禁止，防止导出进成员自己里面），然后为每个成员定好目标路径。
        /// 返回 null 时 error 说明原因；不创建任何文件或文件夹。
        /// </summary>
        internal static ImportBatch Prepare(CollectionExportNode plan, string parent, IEnumerable<CollectionExportForbidden> forbidden,
            string journalDirectory, CancellationToken cancel, out int unavailable, out string error)
        {
            unavailable = 0;
            List<CollectionExportForbidden> configured = new List<CollectionExportForbidden>(forbidden ?? new CollectionExportForbidden[0]);
            List<CollectionExportForbidden> blocked = new List<CollectionExportForbidden>(configured);
            error = TargetError(parent, blocked);
            if (error != null) return null;
            // 只在已有的文件夹里新建导出文件夹：所选位置排队期间被删或改名时让用户重选，不顺手补建上级目录。
            try
            {
                if (!Directory.Exists(parent)) { error = "所选文件夹「" + parent + "」已不存在，请重新选择位置。"; return null; }
            }
            catch (Exception ex) { if (!IsPathFailure(ex)) throw; error = "无法访问所选文件夹：" + ex.Message; return null; }

            List<CollectionExportNode> nodes = new List<CollectionExportNode>();
            Flatten(plan, nodes);
            Dictionary<CollectionExportNode, List<KeyValuePair<string, bool>>> present = new Dictionary<CollectionExportNode, List<KeyValuePair<string, bool>>>();
            int files = 0;
            foreach (CollectionExportNode node in nodes)
            {
                int missing;
                List<KeyValuePair<string, bool>> entries = FileNames.Existing(node.Sources, cancel, out missing);
                unavailable += node.Unavailable + missing;
                files += entries.Count;
                present[node] = entries;
                foreach (KeyValuePair<string, bool> entry in entries)
                {
                    // 成员文件夹也不能做导出位置。已在某个禁止目录里的不必单独列：导出位置落在它里面，必然也落在那个目录里，
                    // 这样成员大多放在资源存储位置时，真实位置检查不用逐个打开成千上万个成员文件夹。
                    if (!entry.Value || configured.Any(f => f != null && !string.IsNullOrWhiteSpace(f.Path) && Within(f.Path, entry.Key))) continue;
                    blocked.Add(new CollectionExportForbidden { Path = entry.Key, Label = "集合成员文件夹" });
                }
            }
            if (files == 0)
            {
                error = "集合「" + plan.Name + "」里没有可导出的文件" + (unavailable > 0 ? "（" + unavailable + " 项已不在原位置）" : "") + "。";
                return null;
            }

            string exportRoot;
            try { exportRoot = FileNames.UniquePath(Path.Combine(Path.GetFullPath(parent), SafeFolderName(plan.Name)), false); }
            catch (Exception ex) { if (!IsPathFailure(ex)) throw; error = "文件夹路径格式无效：" + ex.Message; return null; }
            error = TargetError(exportRoot, blocked) ?? RealLocationError(exportRoot, blocked, cancel);
            if (error != null) return null;
            try { FileImport.EnsureNoLinks(exportRoot); }
            catch (Exception ex) { if (!IsPathFailure(ex)) throw; error = ex.Message; return null; }

            ImportBatch batch = new ImportBatch
            {
                Operation = ImportOperation.Copy,
                Resource = true,
                Category = plan.Name,
                JournalDirectory = journalDirectory,
                ExportRoot = exportRoot
            };
            AddItems(batch, plan, exportRoot, present);
            return batch;
        }

        private static void Flatten(CollectionExportNode node, List<CollectionExportNode> nodes)
        {
            nodes.Add(node);
            foreach (CollectionExportNode child in node.Children) Flatten(child, nodes);
        }

        /// <summary>先给子集合文件夹占名，再给成员占名；同一文件夹内重名的一律加「 (2)」，不覆盖。</summary>
        private static void AddItems(ImportBatch batch, CollectionExportNode node, string directory,
            Dictionary<CollectionExportNode, List<KeyValuePair<string, bool>>> present)
        {
            HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> childDirectories = new List<string>();
            foreach (CollectionExportNode child in node.Children)
                childDirectories.Add(Path.Combine(directory, FileNames.Claim(used, SafeFolderName(child.Name), false)));
            foreach (KeyValuePair<string, bool> entry in present[node])
            {
                string name = Path.GetFileName(entry.Key.TrimEnd('\\', '/'));
                if (string.IsNullOrEmpty(name)) name = SafeFolderName(entry.Key);   // 盘符根之类没有名字的文件夹
                batch.Items.Add(new ImportItem
                {
                    Source = entry.Key,
                    Origin = entry.Key,
                    Folder = entry.Value,
                    Target = Path.Combine(directory, FileNames.Claim(used, name, !entry.Value))
                });
            }
            for (int i = 0; i < node.Children.Count; i++)
            {
                batch.ExportDirectories.Add(childDirectories[i]);
                AddItems(batch, node.Children[i], childDirectories[i], present);
            }
        }

        /// <summary>
        /// 导出文件夹的身份标记：卷号+文件 ID；文件系统不提供文件 ID（部分 NAS 返回 0）时改用创建时间。
        /// 同一路径被删掉再重建，标记就不同。
        /// </summary>
        private static string RootStamp(string root)
        {
            string id = FileImport.Identity(root);
            if (!id.EndsWith(":0:0", StringComparison.Ordinal)) return id;
            return "created:" + Directory.GetCreationTimeUtc(root).Ticks;
        }

        /// <summary>导出根仍是本批建的那个文件夹（存在、不是链接、身份标记一致）。旧日志没有标记时一律不认。</summary>
        private static bool OwnsRoot(ImportBatch batch)
        {
            if (string.IsNullOrEmpty(batch.ExportRoot) || string.IsNullOrEmpty(batch.ExportRootStamp)) return false;
            try
            {
                if (!Directory.Exists(batch.ExportRoot)) return false;
                FileImport.EnsureNoLinks(batch.ExportRoot);
                return string.Equals(RootStamp(batch.ExportRoot), batch.ExportRootStamp, StringComparison.Ordinal);
            }
            catch (Exception ex) { if (!IsPathFailure(ex)) throw; return false; }
        }

        /// <summary>本批的子集合文件夹：只取导出根之内的路径，日志被改坏时也不会碰到别处。</summary>
        private static List<string> OwnDirectories(ImportBatch batch)
        {
            List<string> result = new List<string>();
            foreach (string directory in batch.ExportDirectories ?? new List<string>())
                if (!string.IsNullOrEmpty(directory) && Within(batch.ExportRoot, directory)
                    && !string.Equals(directory.TrimEnd('\\', '/'), batch.ExportRoot.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                    result.Add(directory);
            return result;
        }

        /// <summary>
        /// 执行导出（后台线程）。先建好导出文件夹并记下身份标记，再复制。复制完后：被取消、磁盘满、整批停止
        /// 或一项都没复制成功时撤回全部副本；部分失败时由 keepPartial 决定保留已复制的还是全部撤回（为 null 时撤回）。
        /// 保留时补建空的子集合文件夹；撤回时删除本批新建且已空的文件夹。
        /// </summary>
        internal static CollectionExportResult Execute(ImportBatch batch, Func<ImportBatch, bool> keepPartial,
            Action<int, string> progress, CancellationToken cancel)
        {
            string rootError = CreateRoot(batch);
            if (rootError != null)
            {
                CollectionExportResult refused = new CollectionExportResult();
                refused.Root = batch.ExportRoot;
                refused.Refused = true;
                refused.Error = rootError;
                return refused;
            }
            bool declined = false;
            string journalError = null;
            try
            {
                FileImport.Run(batch, delegate(ImportBatch current)
                {
                    if (cancel.IsCancellationRequested || current.SpaceLimited || current.Stopped) return false;
                    if (!current.Items.Any(i => i.State == FileImport.Copied)) return false;
                    if (!current.Items.Any(i => i.State != FileImport.Copied)) return true;
                    bool keep = keepPartial != null && keepPartial(current);
                    if (cancel.IsCancellationRequested) return false;   // 询问期间已选择取消或退出：按撤回处理
                    declined = !keep;
                    return keep;
                }, progress, cancel);
            }
            catch (Exception ex)
            {
                if (!IsPathFailure(ex) && !(ex is InvalidOperationException)) throw;
                Paths.LogError(ex);
                if (!batch.Committed) return Abort(batch, ex);
                // 已决定保留之后才出错（收尾时日志写不进去）：副本保留，按正常保留流程出结果，只把日志补成已提交
                journalError = SettleKeptJournal(batch, ex);
            }

            CollectionExportResult result = new CollectionExportResult();
            result.Root = batch.ExportRoot;
            result.Kept = batch.Committed;
            result.Declined = declined && !batch.Committed;
            result.Canceled = !batch.Committed && cancel.IsCancellationRequested;
            result.SpaceLimited = batch.SpaceLimited;
            result.Copied = batch.Committed ? batch.Items.Count(i => i.State == FileImport.Copied) : 0;
            if (batch.Stopped)
            {
                ImportItem stopped = batch.Items.FirstOrDefault(i => i.State == FileImport.Failed && !string.IsNullOrEmpty(i.Error) && i.Error != "已取消");
                result.Error = stopped != null ? stopped.Error : "导出已停止。";
            }
            foreach (ImportItem item in batch.Items)
            {
                if (item.State == FileImport.Failed && item.Error != "已取消")
                    result.Failures.Add(Path.GetFileName(item.Source.TrimEnd('\\', '/')) + "：" + (item.Error ?? "未知原因"));
            }
            if (journalError != null) result.Failures.Add(journalError);
            if (batch.Committed) CreateDirectories(batch, result.Failures);
            else
            {
                // 撤回不了的副本（被占用、内容已被改动）保留在原处，交给用户核对；同时写日志，提示框没显示出来也能查到。
                result.Leftovers.AddRange(Remaining(batch));
                RemoveDirectories(batch, result.Leftovers);
                LogLeftovers(result.Leftovers);
            }
            PruneJournal(batch.JournalDirectory, JournalKeep);
            return result;
        }

        /// <summary>
        /// 建导出文件夹并记下身份标记（写进日志，撤回和恢复靠它认出这个文件夹）。
        /// 准备之后这个位置被别的程序占了，就不导出，让用户重试，不往别人的文件夹里写。
        /// </summary>
        private static string CreateRoot(ImportBatch batch)
        {
            try
            {
                if (File.Exists(batch.ExportRoot) || Directory.Exists(batch.ExportRoot))
                    return "导出位置「" + batch.ExportRoot + "」刚被占用，未导出，请重试。";
                Directory.CreateDirectory(batch.ExportRoot);
                FileImport.EnsureNoLinks(batch.ExportRoot);
                batch.ExportRootStamp = RootStamp(batch.ExportRoot);
                return null;
            }
            catch (Exception ex)
            {
                if (!IsPathFailure(ex)) throw;
                Paths.LogError(ex);
                return "无法新建导出文件夹「" + batch.ExportRoot + "」：" + ex.Message;
            }
        }

        private static void LogLeftovers(List<string> leftovers)
        {
            if (leftovers.Count > 0)
                Paths.LogError(new IOException("集合导出撤回后以下位置需要手动核对：" + string.Join("；", leftovers)));
        }

        /// <summary>
        /// 保留之后日志写失败：再写一次已提交状态并收尾；仍写不进去就去掉未完成标记，
        /// 标记也删不掉时明确告诉用户下次启动可能撤回（恢复流程只认磁盘上的日志）。
        /// </summary>
        private static string SettleKeptJournal(ImportBatch batch, Exception error)
        {
            try { FileImport.Finish(batch); return null; }
            catch (Exception ex) { if (!IsPathFailure(ex)) throw; Paths.LogError(ex); }
            string marker = Path.Combine(batch.JournalDirectory, batch.Id + ".pending");
            FileImport.DropMarker(marker);
            bool stuck;
            try { stuck = File.Exists(marker); }
            catch (Exception ex) { if (!IsPathFailure(ex)) throw; stuck = true; }
            return stuck
                ? "导出记录未能保存，下次启动时可能撤回本次导出的副本。请先另行备份「" + batch.ExportRoot + "」，或检查数据目录权限：" + error.Message
                : "导出记录未能保存：" + error.Message;
        }

        /// <summary>保留导出时补建子集合文件夹（含没有成员的子集合），保持与集合层级一致；只在本批的导出文件夹里建。</summary>
        private static void CreateDirectories(ImportBatch batch, List<string> failures)
        {
            if (!OwnsRoot(batch))
            {
                failures.Add("导出文件夹「" + batch.ExportRoot + "」已不在原处，子文件夹未补建。");
                return;
            }
            foreach (string directory in OwnDirectories(batch))
            {
                try { Directory.CreateDirectory(directory); }
                catch (Exception ex)
                {
                    if (!IsPathFailure(ex)) throw;
                    failures.Add("文件夹「" + Path.GetFileName(directory) + "」：" + ex.Message);
                }
            }
        }

        /// <summary>
        /// 撤回后删除本批的文件夹：确认导出根仍是本批建的那个，再由深到浅、非递归删除导出根之内已经空了的目录；
        /// 不空（有撤回不了的副本或别人放进去的文件）就保留，路径上出现链接或联接点时不动。导出根仍在时记入 leftovers。
        /// </summary>
        internal static void RemoveDirectories(ImportBatch batch, List<string> leftovers)
        {
            if (string.IsNullOrEmpty(batch.ExportRoot)) return;
            if (OwnsRoot(batch))
            {
                List<string> directories = OwnDirectories(batch);
                directories.Add(batch.ExportRoot);
                foreach (string directory in directories.OrderByDescending(d => d.Length))
                {
                    try
                    {
                        if (!Directory.Exists(directory)) continue;
                        FileImport.EnsureNoLinks(directory);
                        if (Directory.EnumerateFileSystemEntries(directory).Any()) continue;
                        Directory.Delete(directory, false);
                    }
                    catch (Exception ex)
                    {
                        if (!IsPathFailure(ex)) throw;
                        Paths.LogError(new IOException("导出撤回后文件夹未能删除：" + directory, ex));
                    }
                }
            }
            try
            {
                if (OwnsRoot(batch) && !leftovers.Any(p => string.Equals(p, batch.ExportRoot, StringComparison.OrdinalIgnoreCase)))
                    leftovers.Add(batch.ExportRoot);
            }
            catch (Exception ex) { if (!IsPathFailure(ex)) throw; }
        }

        /// <summary>
        /// 启动时处理上次中断的导出：FileImport.Recover 清理临时副本、撤回未完成批次里与源一致的副本；
        /// 日志落后于实际进度而核对不了身份的，再按导出专用规则补撤（见 Salvage），最后删除本批新建且已空的文件夹。
        /// 已决定保留、但收尾前中断的批次补建空的子集合文件夹。仍无法核对的文件保留并写日志。日志只保留最近 50 份。
        /// </summary>
        internal static void Recover(string journalDirectory)
        {
            string[] markers;
            try { markers = FileImport.PendingJournals(journalDirectory); }
            catch (Exception ex) { Paths.LogError(ex); return; }
            foreach (string marker in markers)
            {
                ImportBatch batch;
                try { batch = FileImport.Load(Path.ChangeExtension(marker, ".xml")); }
                catch (Exception ex) { Paths.LogError(ex); FileImport.DropMarker(marker); continue; }
                try
                {
                    bool committed = batch.Committed;
                    FileImport.Recover(batch, false);
                    if (committed)
                    {
                        List<string> failures = new List<string>();
                        CreateDirectories(batch, failures);
                        if (failures.Count > 0)
                            Paths.LogError(new IOException("上次集合导出在收尾时中断：" + string.Join("；", failures)));
                        continue;
                    }
                    Salvage(batch);
                    List<string> leftovers = Remaining(batch);
                    RemoveDirectories(batch, leftovers);
                    if (leftovers.Count > 0)
                        Paths.LogError(new IOException("上次集合导出被中断，以下位置需要手动核对：" + string.Join("；", leftovers)));
                }
                catch (Exception ex) { Paths.LogError(ex); }
            }
            PruneJournal(journalDirectory, JournalKeep);
        }

        /// <summary>
        /// 导出日志数量上限：导出日志没有界面查看，撤回、取消、部分失败的批次也按时间只保留最近 keep 份
        /// （FileImport.Prune 只删已完全成功的批次）；仍有 .pending 标记的批次留给下次恢复。
        /// </summary>
        internal static void PruneJournal(string journalDirectory, int keep)
        {
            try
            {
                FileImport.Prune(journalDirectory, keep);
                if (!Directory.Exists(journalDirectory)) return;
                IEnumerable<FileInfo> stale = new DirectoryInfo(journalDirectory).GetFiles("*.xml")
                    .OrderByDescending(f => f.LastWriteTimeUtc).Skip(keep)
                    .Where(f => !File.Exists(Path.ChangeExtension(f.FullName, ".pending")));
                foreach (FileInfo file in stale)
                {
                    try { file.Delete(); FileImport.DropMarker(Path.ChangeExtension(file.FullName, ".complete")); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex) { if (!IsPathFailure(ex)) throw; Paths.LogError(ex); }
        }

        /// <summary>
        /// 中断恢复的导出专用补撤：日志里没来得及记下身份的副本，FileImport.Recover 为安全起见会保留。
        /// 导出只复制、源文件仍在，所以确认导出根仍是本批建的那个、且路径在其中没有链接时：
        /// 带本批编号的临时副本（.import-批次号）直接清理；文件副本与源文件逐字节相同才删除；文件夹副本仍保留待核对。
        /// </summary>
        private static void Salvage(ImportBatch batch)
        {
            if (batch.Committed || !OwnsRoot(batch)) return;
            foreach (ImportItem item in batch.Items)
            {
                if (string.IsNullOrEmpty(item.Target)) continue;
                try
                {
                    if (!Within(batch.ExportRoot, item.Target)) continue;
                    // 临时副本与条目状态无关：目标还没落地的项会被标成已撤回，但它的临时副本可能还在
                    string staging = item.Target + ".import-" + batch.Id;
                    if (File.Exists(staging) || Directory.Exists(staging))
                    {
                        FileImport.EnsureNoLinks(staging);
                        FileImport.RemoveStaging(staging);
                    }
                    if (item.State == FileImport.RolledBack || item.Folder || !File.Exists(item.Target) || !File.Exists(item.Source)) continue;
                    FileImport.EnsureNoLinks(item.Target);
                    if ((File.GetAttributes(item.Target) & FileAttributes.ReparsePoint) != 0) continue;
                    if (!FileImport.SameContent(item.Source, item.Target)) continue;
                    File.SetAttributes(item.Target, FileAttributes.Normal);   // 副本带着源文件的只读属性
                    File.Delete(item.Target);
                    item.State = FileImport.RolledBack;
                }
                catch (Exception ex)
                {
                    if (!IsPathFailure(ex)) throw;
                    Paths.LogError(new IOException("中断导出的副本未能撤回：" + item.Target, ex));
                }
            }
        }

        /// <summary>撤回后仍留在导出位置的本批文件：没能撤回的副本和没能清理的临时副本。导出根已不是本批的就不算。</summary>
        private static List<string> Remaining(ImportBatch batch)
        {
            List<string> result = new List<string>();
            if (!OwnsRoot(batch)) return result;
            foreach (ImportItem item in batch.Items)
            {
                if (string.IsNullOrEmpty(item.Target) || !Within(batch.ExportRoot, item.Target)) continue;
                string staging = item.Target + ".import-" + batch.Id;
                try
                {
                    if (item.State != FileImport.RolledBack && (File.Exists(item.Target) || Directory.Exists(item.Target))) result.Add(item.Target);
                    if (File.Exists(staging) || Directory.Exists(staging)) result.Add(staging);
                }
                catch (Exception ex) { if (!IsPathFailure(ex)) throw; }
            }
            return result;
        }

        /// <summary>
        /// 复制引擎中途抛出（例如导出日志写不进去）：按中断恢复的同一规则当场撤回本批副本和空文件夹，
        /// 撤回不了的留给下次启动再处理。
        /// </summary>
        private static CollectionExportResult Abort(ImportBatch batch, Exception error)
        {
            CollectionExportResult result = new CollectionExportResult();
            result.Root = batch.ExportRoot;
            result.Error = "导出中断：" + error.Message;
            try { FileImport.Recover(batch, false); }
            catch (Exception ex) { Paths.LogError(ex); }
            Salvage(batch);
            result.Leftovers.AddRange(Remaining(batch));
            RemoveDirectories(batch, result.Leftovers);
            LogLeftovers(result.Leftovers);
            return result;
        }
    }
}

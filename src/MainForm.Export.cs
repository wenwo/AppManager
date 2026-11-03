using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AppManager.Controls;

namespace AppManager
{
    /// <summary>
    /// 导出：复制/剪切到剪贴板、拖出到其它程序，语义对齐资源管理器。
    /// 剪贴板只放文件列表与首选效果，由粘贴方（资源管理器等）执行复制或移动；
    /// 剪切与移动拖出是否完成以磁盘为准：源文件消失且所在目录仍在，才视为已移出并清理条目。
    /// 不实现「粘贴成功后由来源删除」：目标只复制时源文件保留，失败方向是多一份文件而不是丢文件。
    /// </summary>
    public partial class MainForm
    {
        private const string DragOutFormat = "AppManager.DragOut";
        // 剪贴板换掉或拖放结束后继续观察源文件的时长：资源管理器跨盘移动在后台进行，结束时才删除源文件。
        private static readonly TimeSpan MoveOutWatchTime = TimeSpan.FromMinutes(10);

        /// <summary>等待移出的条目；Expires 为 MaxValue 表示剪切仍在剪贴板上，不计时。</summary>
        private sealed class MoveOutWatch
        {
            public AppInfo App;
            public string Path;
            public DateTime Expires;
        }

        /// <summary>当前剪贴板上由本应用放入的剪切内容。</summary>
        private sealed class CutState
        {
            public uint Sequence;
            public bool Resource;
            public List<AppInfo> Apps;
        }

        private readonly List<MoveOutWatch> moveOutWatches = new List<MoveOutWatch>();
        private CutState cutState;
        private Timer moveOutTimer;

        /// <summary>条目可能对应的实体路径：文件/文件夹条目取目标，软件取来源快捷方式；网址与打包应用没有实体文件。</summary>
        private static string ExportCandidate(AppInfo app)
        {
            if (app == null) return null;
            string path = app.IsFile ? app.TargetPath : app.ShortcutPath;
            if (string.IsNullOrEmpty(path) || Scanner.IsUrl(path)
                || path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) return null;
            return path;
        }

        /// <summary>收集磁盘上仍存在的实体路径（去重，保持选中顺序）；complete 表示每个条目都有可导出的文件。</summary>
        private static List<string> ExportPaths(IEnumerable<AppInfo> apps, out bool complete)
        {
            complete = true;
            List<string> paths = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AppInfo app in apps)
            {
                string path = ExportCandidate(app);
                if (path == null) { complete = false; continue; }
                try { path = Path.GetFullPath(path); } catch (Exception) { complete = false; continue; }
                if (!File.Exists(path) && !Directory.Exists(path)) { complete = false; continue; }
                if (seen.Add(path)) paths.Add(path);
            }
            return paths;
        }

        /// <summary>与资源管理器一致的数据：文件列表 + 首选效果（复制或移动）。</summary>
        private static DataObject ExportData(List<string> paths, DragDropEffects preferred)
        {
            StringCollection list = new StringCollection();
            list.AddRange(paths.ToArray());
            DataObject data = new DataObject();
            data.SetFileDropList(list);
            data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes((int)preferred)));
            return data;
        }

        /// <summary>
        /// 可剪切 / 可移动拖出：与「删除」相同的范围（自建收录的文件/文件夹条目），且每一项都有实体文件。
        /// 资源管理器在选中项里有不可移动的项目时整体禁用剪切，这里同样整体判断。
        /// </summary>
        private bool CanMoveOut(List<AppInfo> apps)
        {
            return apps != null && apps.Count > 0 && apps.All(a => CanDeleteEntryDisk(a) && ExportCandidate(a) != null);
        }

        /// <summary>
        /// 拖出到其它程序：默认复制（库内文件不应因为拖到同盘的桌面就被搬走）；
        /// 全部可移动时额外允许移动，按住 Shift 拖放即移动，与资源管理器的强制移动一致。
        /// </summary>
        private ExternalDragRequest ExportDragData(List<AppInfo> apps)
        {
            bool complete;
            List<string> paths = ExportPaths(apps, out complete);
            if (paths.Count == 0) return null;
            bool movable = complete && CanMoveOut(apps);
            DataObject data = ExportData(paths, DragDropEffects.Copy);
            data.SetData(DragOutFormat, string.Empty);
            ExternalDragRequest request = new ExternalDragRequest { Data = data, Allowed = DragDropEffects.Copy | DragDropEffects.Link };
            if (movable)
            {
                List<AppInfo> sources = new List<AppInfo>(apps);
                request.Allowed |= DragDropEffects.Move;
                request.Completed = delegate(DragDropEffects effect)
                {
                    // 目标明确返回复制/快捷方式时源文件不会变；返回移动或 None（资源管理器的优化移动返回 None）时观察源文件。
                    if ((effect & (DragDropEffects.Copy | DragDropEffects.Link)) != 0 && (effect & DragDropEffects.Move) == 0) return;
                    WatchMoveOut(sources, DateTime.Now + MoveOutWatchTime);
                    BeginInvoke((Action)CheckMoveOuts);
                };
            }
            return request;
        }

        /// <summary>剪切/复制菜单项（顺序与资源管理器一致）：剪切仅在全部可移动时显示。</summary>
        private void AddCopyFilesMenu(List<MenuWindow.MenuItem> items, List<AppInfo> targets)
        {
            if (CanMoveOut(targets))
                items.Add(MkMenu("剪切  Ctrl+X", delegate { PutItemsOnClipboard(targets, true); }));
            if (targets.Any(a => ExportCandidate(a) != null))
                items.Add(MkMenu("复制  Ctrl+C", delegate { PutItemsOnClipboard(targets, false); }));
        }

        /// <summary>Ctrl+C / Ctrl+X：把条目实体文件放入剪贴板，可在资源管理器、桌面或聊天窗口中粘贴。</summary>
        private void PutItemsOnClipboard(List<AppInfo> apps, bool cut)
        {
            if (cut && !CanMoveOut(apps))
            {
                MessageBox.Show(this, "所选项目中包含软件、网址或未收录的条目，无法剪切。\n可以改用复制。", "剪切",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            bool complete;
            List<string> paths = ExportPaths(apps, out complete);
            if (paths.Count == 0 || (cut && !complete))
            {
                MessageBox.Show(this, "所选项目没有可" + (cut ? "剪切" : "复制") + "的文件（文件可能已被移动或删除）。",
                    cut ? "剪切" : "复制", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try { Clipboard.SetDataObject(ExportData(paths, cut ? DragDropEffects.Move : DragDropEffects.Copy), true, 5, 100); }
            catch (ExternalException ex)
            {
                MessageBox.Show(this, "剪贴板被其它程序占用，请重试。\n" + ex.Message, cut ? "剪切" : "复制",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            // 剪贴板内容已替换：上一次剪切随之结束（与资源管理器一致，复制新内容即取消剪切）。
            EndCut();
            if (!cut) return;
            cutState = new CutState { Sequence = GetClipboardSequenceNumber(),
                Resource = contentMode == SidebarControl.ContentMode.Website, Apps = new List<AppInfo>(apps) };
            WatchMoveOut(apps, DateTime.MaxValue);
            grid.SetCutKeys(apps.Select(a => a.Key));
        }

        /// <summary>剪切结束（剪贴板被替换、已粘贴或已移出）：取消半透明显示，仍在观察的源文件开始计时。</summary>
        private void EndCut()
        {
            if (cutState == null) return;
            cutState = null;
            DateTime expires = DateTime.Now + MoveOutWatchTime;
            foreach (MoveOutWatch watch in moveOutWatches)
                if (watch.Expires == DateTime.MaxValue) watch.Expires = expires;
            grid.SetCutKeys(null);
        }

        /// <summary>持有剪贴板锁后再比较序号，只清空本应用放入且未被替换的剪贴板内容。</summary>
        private void ClearClipboardIfCurrent(uint sequence)
        {
            if (!OpenClipboard(Handle)) return;
            try { if (GetClipboardSequenceNumber() == sequence) EmptyClipboard(); }
            finally { CloseClipboard(); }
        }

        /// <summary>
        /// 在应用内改名会主动让旧路径消失；这不是“移出”，必须取消旧路径观察，
        /// 否则定时器会把条目误判为已移出并清掉收藏、集合和分类记录。
        /// </summary>
        private void ForgetMoveOutAfterRename(string oldPath)
        {
            if (string.IsNullOrEmpty(oldPath)) return;
            moveOutWatches.RemoveAll(w => string.Equals(w.Path, oldPath, StringComparison.OrdinalIgnoreCase));
            bool wasCut = cutState != null && cutState.Apps.Any(a =>
                string.Equals(a.TargetPath, oldPath, StringComparison.OrdinalIgnoreCase));
            if (wasCut)
            {
                uint sequence = cutState.Sequence;
                ClearClipboardIfCurrent(sequence);
                EndCut();
            }
            if (moveOutWatches.Count == 0 && moveOutTimer != null) moveOutTimer.Stop();
        }

        private void WatchMoveOut(IEnumerable<AppInfo> apps, DateTime expires)
        {
            foreach (AppInfo app in apps)
            {
                string path = ExportCandidate(app);
                if (path == null) continue;
                try { path = Path.GetFullPath(path); } catch (Exception) { continue; }
                MoveOutWatch existing = moveOutWatches.FirstOrDefault(w => string.Equals(w.Path, path, StringComparison.OrdinalIgnoreCase));
                if (existing != null) { existing.App = app; existing.Expires = expires; continue; }
                moveOutWatches.Add(new MoveOutWatch { App = app, Path = path, Expires = expires });
            }
            if (moveOutTimer == null)
            {
                moveOutTimer = new Timer { Interval = 1000 };
                moveOutTimer.Tick += delegate { CheckMoveOuts(); };
            }
            if (moveOutWatches.Count > 0) moveOutTimer.Start();
        }

        /// <summary>源文件已不在原位置：文件与文件夹都不存在，且所在目录仍可访问（排除移动硬盘拔出、网络断开）。</summary>
        private static bool SourceMovedAway(string path)
        {
            try
            {
                if (File.Exists(path) || Directory.Exists(path)) return false;
                string parent = Path.GetDirectoryName(path.TrimEnd('\\', '/'));
                return !string.IsNullOrEmpty(parent) && Directory.Exists(parent);
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// 定时核对剪切/移动拖出的源文件：已移走的条目清理收录与归属记录（与「删除」相同）；
        /// 剪切的全部条目都已移走时清空仍属于本次剪切的剪贴板，避免再次粘贴出错（与资源管理器一致）。
        /// </summary>
        private void CheckMoveOuts()
        {
            if (IsDisposed) return;
            if (cutState != null && GetClipboardSequenceNumber() != cutState.Sequence) EndCut();
            DateTime now = DateTime.Now;
            List<AppInfo> moved = new List<AppInfo>();
            for (int i = moveOutWatches.Count - 1; i >= 0; i--)
            {
                MoveOutWatch watch = moveOutWatches[i];
                if (SourceMovedAway(watch.Path)) { moved.Add(watch.App); moveOutWatches.RemoveAt(i); }
                else if (now >= watch.Expires) moveOutWatches.RemoveAt(i);
            }
            if (moveOutWatches.Count == 0 && moveOutTimer != null) moveOutTimer.Stop();
            if (moved.Count == 0) return;

            if (cutState != null)
            {
                cutState.Apps.RemoveAll(a => moved.Contains(a));
                if (cutState.Apps.Count == 0)
                {
                    ClearClipboardIfCurrent(cutState.Sequence);
                    EndCut();
                }
                else grid.SetCutKeys(cutState.Apps.Select(a => a.Key));
            }
            foreach (AppInfo app in moved)
            {
                // 文件夹整体移走时，其中的收录项随之离开原位置，一并清理。
                if (app.IsFolder)
                    foreach (AddedEntry inner in config.AddedItems.EntriesUnder(app.TargetPath))
                        PurgeEntryConfig(new AppInfo { TargetPath = inner.Path, Key = "add:" + inner.Path, IsFile = true });
                PurgeEntryConfig(app);
            }
            config.Save();
            StartScan(false);
        }

        /// <summary>
        /// 本应用剪切的条目粘贴回应用：资源库的分类是逻辑归属，因此按「移动到分类」处理（文件位置不变）。
        /// 类型不符的条目跳过；已在目标分类时与资源管理器粘贴到原文件夹一样不做任何事，剪切保留。
        /// </summary>
        private void PasteCutItems()
        {
            CutState cut = cutState;
            bool resource = contentMode == SidebarControl.ContentMode.Website;
            if (cut.Resource != resource)
            {
                MessageBox.Show(this, "剪切的是" + (cut.Resource ? "资源" : "软件页面中的文件") + "，请切换到对应页面后再粘贴。",
                    "粘贴", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            bool choose = SearchActive || !CanEditCategory(currentCategory);
            string category = choose ? PickImportCategory(resource, cut.Apps.Select(a => a.TargetPath).ToList(),
                cut.Apps.Select(a => a.IsFolder).ToArray()) : currentCategory;
            if (category == null || cutState != cut) return;
            string collection = choose ? null : ImportCollectionKey;

            List<AppInfo> items = new List<AppInfo>();
            int skipped = 0;
            foreach (AppInfo stale in cut.Apps)
            {
                // 剪切后列表可能已重扫：按 Key 取当前条目，保证分类变更作用在正在显示的对象上。
                AppInfo app = allApps.FirstOrDefault(a => string.Equals(a.Key, stale.Key, StringComparison.OrdinalIgnoreCase)) ?? stale;
                if (!File.Exists(app.TargetPath) && !Directory.Exists(app.TargetPath)) continue;   // 剪切后已被删除或移走
                if (FitsTargetCategory(app, category)) items.Add(app); else skipped++;
            }
            string typeHint = MediaRules.CategoryTypeHint(category);
            if (items.Count == 0)
            {
                ShowNotice(skipped > 0 ? "未移动：" + typeHint : "剪切的项目已不在原位置");
                return;
            }
            bool changed = items.Any(a => !string.Equals(a.Category, category, StringComparison.OrdinalIgnoreCase));
            if (!changed && collection == null) return;

            moveOutWatches.RemoveAll(w => cut.Apps.Any(a => string.Equals(a.Key, w.App.Key, StringComparison.OrdinalIgnoreCase)));
            ClearClipboardIfCurrent(cut.Sequence);
            EndCut();
            if (changed) SetHardCategory(items, category);
            if (collection != null) AddItemsToCategory(items, collection);
            if (skipped > 0) ShowNotice("已跳过 " + skipped + " 个类型不符的项目：" + typeHint);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using System.Threading;
using AppManager.Controls;

namespace AppManager
{
    public partial class MainForm
    {
        private const int DatabasePageSize = 200;
        private string databaseContext;
        private int databasePageNumber;
        private CatalogPage databasePage;
        private readonly List<CatalogPage> databaseCursors = new List<CatalogPage>();
        private readonly Dictionary<string, CatalogPage> databaseSummaries = new Dictionary<string, CatalogPage>();
        // 集合/分类摘要按「数据库实例 + 修订号 + 显示隐藏项」缓存，切换分类不再重复查询；任一变化即整体失效。
        private string databaseSummaryStamp;
        private ConfigDatabase databaseSummaryStore;
        // 摘要独有的条目（不在保留池中）：随摘要缓存存活，摘要失效时释放图标，不占保留池上限。
        private readonly List<AppInfo> databaseSummaryOwned = new List<AppInfo>();
        // 非安全时机（未换代的图标加载可能仍在回写）失效的摘要条目：推迟到下次 RefreshGrid 换代后再释放。
        private readonly List<AppInfo> databaseDeferredDisposals = new List<AppInfo>();
        // 集合侧边栏封面的批量查询缓存：与摘要同一失效口径（Revision|ShowHidden|分类），
        // 保存后一次 GROUP BY 重建，不再逐集合分页查询。条目走保留池，失效时只清空映射。
        private readonly Dictionary<string, AppInfo> collectionCoverImages = new Dictionary<string, AppInfo>(StringComparer.Ordinal);
        private readonly Dictionary<string, AppInfo> collectionCoverFirst = new Dictionary<string, AppInfo>(StringComparer.Ordinal);
        private readonly List<string> collectionCoverKeys = new List<string>();
        private string collectionCoverStamp;
        private bool collectionCoverFirstLoaded;
        // 跨分类/分页保留的条目对象（持有已加载图标），按 Key 复用；超过上限时只留当前页。
        private readonly Dictionary<string, AppInfo> databaseRetained = new Dictionary<string, AppInfo>(StringComparer.Ordinal);
        private const int DatabaseRetainedLimit = 1000;
        private Rectangle databasePrevious, databaseNext;
        private bool databaseMetadataRunning;

        private void BindDatabaseErrors()
        {
            config.SaveFailed += delegate(string error)
            {
                RunOnUi(delegate
                {
                    ReloadUiAfterSaveFailure();
                    MessageBox.Show(this, "变更未保存，已恢复为数据库中的原数据。\n" + error, "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                });
            };
        }

        /// <summary>保存失败后配置已回滚到数据库状态：重建依赖配置的运行时对象并刷新列表。</summary>
        private void ReloadUiAfterSaveFailure()
        {
            if (!_ready || IsDisposed || Disposing) return;
            InvalidateAddedIndex();
            scanner = new Scanner(config);
            organizer = new DesktopOrganizer(config);
            if (currentCollection != null && FindCollectionByKey(currentCollection) == null) currentCollection = null;
            grid.GridMode = GetCategoryGridMode(currentCategory);
            SyncHeaderStrip();
            if (settingsPage != null)
                settingsPage.Bind(config.ShowHidden, config.AutoScan, config.AutoOrganizeDesktop,
                    config.CloseToTray, ResourceStoreSummary());
            StartAutoScan();
            RebuildSidebar();
            StartScan();
            Invalidate();
        }

        private void ScheduleDatabaseMetadata()
        {
            if (databaseMetadataRunning || config.Database == null) return;
            databaseMetadataRunning = true;
            ConfigDatabase store = config.Database;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { store.IndexMetadata(delegate { return IsDisposed || Disposing || config.Database != store; }); }
                catch (Exception ex) { Paths.LogError(ex); }
                RunOnUi(delegate
                {
                    databaseMetadataRunning = false;
                    if (config.Database != store) return;
                    if (listSortColumn == "size" || listSortColumn == "time") { databaseContext = null; RefreshGrid(true); }
                });
            });
        }

        private List<AppInfo> ReadDatabasePage(string keyword)
        {
            string category = !string.IsNullOrEmpty(currentCollection) && keyword.Length == 0 ? currentCollection : currentCategory;
            string context = category + "\n" + contentMode + "\n" + keyword + "\n" + config.ShowHidden + "\n" + listSortColumn + "\n" + listSortAsc + "\n" + config.Database.Revision;
            if (context != databaseContext)
            {
                databaseContext = context; databasePageNumber = 0; databaseCursors.Clear();
            }
            // RefreshGrid 已在锁内换代，旧图标任务不会再回写：此时释放推迟的摘要条目图标
            DisposeIcons(databaseDeferredDisposals);
            databaseDeferredDisposals.Clear();
            CatalogPage after = databasePageNumber == 0 ? null : databaseCursors[databasePageNumber - 1];
            databasePage = config.Database.Page(category, contentMode == SidebarControl.ContentMode.Website, config.ShowHidden,
                keyword, DatabasePageSize, after, listSortColumn == "name", listSortColumn != null && !listSortAsc, listSortColumn);
            if (databasePageNumber < databaseCursors.Count) databaseCursors[databasePageNumber] = databasePage;
            else databaseCursors.Add(databasePage);
            scanner.ApplyConfig(databasePage.Items);
            // 复用看过的条目对象及其图标/元数据缓存：切回分类时图标立即可用，不再先空白再逐个出现。
            for (int i = 0; i < databasePage.Items.Count; i++) databasePage.Items[i] = RetainDatabaseItem(databasePage.Items[i]);
            TrimDatabaseRetained(databasePage.Items);
            categoryTotal = databasePage.Total;
            return databasePage.Items;
        }

        /// <summary>同键且图标来源未变时复用保留对象并刷新其字段；否则以新对象替换并释放旧图标。</summary>
        private AppInfo RetainDatabaseItem(AppInfo fresh)
        {
            return RetainDatabaseItem(fresh, true);
        }

        /// <summary>
        /// replace=false（摘要）：只复用保留池中已有的同源对象，其余条目归摘要所有，
        /// 既不撑大保留池，也不替换当前页仍在显示的对象。
        /// </summary>
        private AppInfo RetainDatabaseItem(AppInfo fresh, bool replace)
        {
            if (string.IsNullOrEmpty(fresh.Key)) { if (!replace) databaseSummaryOwned.Add(fresh); return fresh; }
            AppInfo old;
            if (databaseRetained.TryGetValue(fresh.Key, out old))
            {
                if (old.TargetPath == fresh.TargetPath && old.Name == fresh.Name && old.ShortcutPath == fresh.ShortcutPath
                    && old.DisplayIcon == fresh.DisplayIcon && old.IsUwp == fresh.IsUwp && old.IsFolder == fresh.IsFolder)
                {
                    old.InstallLocation = fresh.InstallLocation; old.UninstallString = fresh.UninstallString;
                    old.Category = fresh.Category; old.IsFavorite = fresh.IsFavorite; old.IsHidden = fresh.IsHidden;
                    old.IsDesktop = fresh.IsDesktop; old.IsFile = fresh.IsFile; old.Source = fresh.Source; old.DatabaseKind = fresh.DatabaseKind;
                    return old;
                }
                if (!replace) { databaseSummaryOwned.Add(fresh); return fresh; }
                // 仅在 ReadDatabasePage（已换代）中替换：旧对象若被缓存摘要引用，令摘要失效后重查，不再沿用旧对象
                DisposeIcons(new List<AppInfo> { old });
                databaseSummaryStamp = null;
            }
            else if (!replace) { databaseSummaryOwned.Add(fresh); return fresh; }
            databaseRetained[fresh.Key] = fresh;
            return fresh;
        }

        /// <summary>保留池超限时释放当前页以外的条目图标（页内对象继续保留）。</summary>
        private void TrimDatabaseRetained(List<AppInfo> keep)
        {
            if (databaseRetained.Count <= DatabaseRetainedLimit) return;
            ReleaseDatabaseRetained(keep);
        }

        /// <summary>
        /// 从保留池移除 keep 以外的条目并释放其图标；摘要引用这些条目，一并失效。
        /// 仅在图标加载已换代时调用（ReadDatabasePage / SwapApps 锁内）。
        /// </summary>
        private void ReleaseDatabaseRetained(List<AppInfo> keep)
        {
            ClearDatabaseSummaries(true);
            HashSet<AppInfo> current = new HashSet<AppInfo>(keep);
            List<string> keys = new List<string>();
            List<AppInfo> drop = new List<AppInfo>();
            foreach (KeyValuePair<string, AppInfo> kv in databaseRetained)
            {
                if (current.Contains(kv.Value)) continue;
                keys.Add(kv.Key); drop.Add(kv.Value);
            }
            foreach (string key in keys) databaseRetained.Remove(key);
            DisposeIcons(drop);
        }

        private CatalogPage DatabaseSummary(string key)
        {
            string stamp = config.Database.Revision + "|" + config.ShowHidden;
            if (!ReferenceEquals(databaseSummaryStore, config.Database) || stamp != databaseSummaryStamp)
            {
                ClearDatabaseSummaries(false);   // 任意时机都可能走到这里：摘要条目图标推迟释放
                databaseSummaryStore = config.Database;
                databaseSummaryStamp = stamp;
            }
            CatalogPage value;
            if (!databaseSummaries.TryGetValue(key, out value))
            {
                value = config.Database.Page(key, true, config.ShowHidden, "", 60, null, true, false);
                // 与当前页共用同一对象：已加载的图标直接用于集合封面，封面所需图标也不必重复加载。
                scanner.ApplyConfig(value.Items);
                for (int i = 0; i < value.Items.Count; i++) value.Items[i] = RetainDatabaseItem(value.Items[i], false);
                databaseSummaries[key] = value;
            }
            return value;
        }

        /// <summary>
        /// 集合侧边栏封面候选的批量缓存：一次 GROUP BY 取当前分类每个集合的首个图片成员；
        /// 无图片成员的集合在首次需要时再批量取首个成员（图标版式化兜底）。
        /// 首个图片成员封面解码失败时回退首个成员，不再逐个尝试后续图片成员。
        /// </summary>
        private void EnsureCollectionCovers(string category)
        {
            string stamp = config.Database.Revision + "|" + config.ShowHidden + "|" + category;
            if (stamp == collectionCoverStamp) return;
            collectionCoverStamp = stamp;
            collectionCoverImages.Clear();
            collectionCoverFirst.Clear();
            collectionCoverFirstLoaded = false;
            collectionCoverKeys.Clear();
            for (int i = 0; i < config.Collections.Count; i++)
                if (config.Collections[i].Parent == category)
                    collectionCoverKeys.Add(CollectionKeys.Make(category, config.Collections[i].Name));
            RetainCoverMembers(config.Database.CollectionFirstMembers(collectionCoverKeys, config.ShowHidden, true), collectionCoverImages);
        }

        private AppInfo CollectionCoverFallback(string key)
        {
            if (!collectionCoverFirstLoaded)
            {
                collectionCoverFirstLoaded = true;
                List<string> missing = new List<string>();
                foreach (string k in collectionCoverKeys)
                {
                    AppInfo image;
                    // 没有图片成员、或首个图片成员封面解码失败的集合，都回退到首个成员的图标版式化
                    if (collectionCoverImages.TryGetValue(k, out image)
                        && !IsCoverFailed(image.TargetPath, CollectionSidebar.ThumbSize, CollectionSidebar.ThumbSize)) continue;
                    missing.Add(k);
                }
                RetainCoverMembers(config.Database.CollectionFirstMembers(missing, config.ShowHidden, false), collectionCoverFirst);
            }
            AppInfo value;
            return collectionCoverFirst.TryGetValue(key, out value) ? value : null;
        }

        /// <summary>与 DatabaseSummary 同口径：先套用扫描配置再进保留池，图标随保留池统一释放。</summary>
        private void RetainCoverMembers(Dictionary<string, AppInfo> found, Dictionary<string, AppInfo> target)
        {
            List<AppInfo> items = new List<AppInfo>(found.Values);
            scanner.ApplyConfig(items);
            foreach (KeyValuePair<string, AppInfo> kv in found) target[kv.Key] = RetainDatabaseItem(kv.Value, false);
        }

        /// <summary>
        /// 摘要整体失效：共享对象归保留池管理，只释放摘要独有条目的图标。
        /// safe=true 表示图标加载已换代，可立即释放；否则推迟到下次 ReadDatabasePage。
        /// </summary>
        private void ClearDatabaseSummaries(bool safe)
        {
            databaseSummaries.Clear();
            databaseSummaryStamp = null;
            if (safe)
            {
                DisposeIcons(databaseSummaryOwned);
                DisposeIcons(databaseDeferredDisposals);
                databaseDeferredDisposals.Clear();
            }
            else databaseDeferredDisposals.AddRange(databaseSummaryOwned);
            databaseSummaryOwned.Clear();
        }

        private bool PaintDatabasePaging(Graphics graphics, Rectangle footer)
        {
            databasePrevious = databaseNext = Rectangle.Empty;
            if (config.Database == null || databasePage == null || settingsOpen || !grid.Visible || categoryTotal <= DatabasePageSize) return false;
            databaseNext = new Rectangle(footer.Right - Dpi(90), footer.Top + Dpi(6), Dpi(76), footer.Height - Dpi(12));
            databasePrevious = new Rectangle(databaseNext.Left - Dpi(90), databaseNext.Top, Dpi(76), databaseNext.Height);
            bool hasPrevious = databasePageNumber > 0;
            bool hasNext = (databasePageNumber + 1L) * DatabasePageSize < categoryTotal;
            using (Brush enabled = new SolidBrush(Theme.TextPrimary))
            using (Brush disabled = new SolidBrush(Theme.TextSecondary))
            {
                Draw.DrawStringLeft(graphics, "第 " + (databasePageNumber + 1) + " 页 · 共 " + categoryTotal + " 项", Theme.CaptionFont,
                    disabled, new Rectangle(footer.Left + Dpi(16), footer.Top, Math.Max(0, databasePrevious.Left - footer.Left - Dpi(24)), footer.Height));
                Draw.DrawStringLeft(graphics, "上一页", Theme.ButtonFont, hasPrevious ? enabled : disabled, databasePrevious);
                Draw.DrawStringLeft(graphics, "下一页", Theme.ButtonFont, hasNext ? enabled : disabled, databaseNext);
            }
            return true;
        }

        private bool HandleDatabasePaging(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || config.Database == null || !grid.Visible || settingsOpen) return false;
            if (databasePrevious.Contains(e.Location))
            {
                if (databasePageNumber > 0) { databasePageNumber--; RefreshGrid(); }
                return true;
            }
            if (databaseNext.Contains(e.Location))
            {
                if ((databasePageNumber + 1L) * DatabasePageSize < categoryTotal) { databasePageNumber++; RefreshGrid(); }
                return true;
            }
            return false;
        }
    }
}

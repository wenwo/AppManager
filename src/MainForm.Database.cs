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
                    MessageBox.Show(this, "变更未保存，数据库中的原数据已保留。\n" + error, "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                });
            };
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
            foreach (CatalogPage summary in databaseSummaries.Values) DisposeIcons(summary.Items);
            databaseSummaries.Clear();
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
            if (string.IsNullOrEmpty(fresh.Key)) return fresh;
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
                DisposeIcons(new List<AppInfo> { old });   // 旧对象只可能在即将被替换的上一页中，随后 SetApps 即换掉
            }
            databaseRetained[fresh.Key] = fresh;
            return fresh;
        }

        /// <summary>保留池超限时释放当前页以外的条目图标（页内对象继续保留）。</summary>
        private void TrimDatabaseRetained(List<AppInfo> keep)
        {
            if (databaseRetained.Count <= DatabaseRetainedLimit) return;
            ReleaseDatabaseRetained(keep);
        }

        /// <summary>从保留池移除 keep 以外的条目并释放其图标。</summary>
        private void ReleaseDatabaseRetained(List<AppInfo> keep)
        {
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
            CatalogPage value;
            if (!databaseSummaries.TryGetValue(key, out value))
            {
                value = config.Database.Page(key, true, config.ShowHidden, "", 60, null, true, false);
                databaseSummaries[key] = value;
            }
            return value;
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

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
            // 保留同页对象及图标/元数据缓存，避免异步元数据完成后反复重载同一页。
            Dictionary<string, AppInfo> previous = new Dictionary<string, AppInfo>();
            foreach (AppInfo a in shownApps) if (!string.IsNullOrEmpty(a.Key)) previous[a.Key] = a;
            for (int i = 0; i < databasePage.Items.Count; i++)
            {
                AppInfo a = databasePage.Items[i], old;
                if (!previous.TryGetValue(a.Key, out old) || old.TargetPath != a.TargetPath || old.Name != a.Name) continue;
                old.IsFavorite = a.IsFavorite; old.IsHidden = a.IsHidden; old.Category = a.Category; old.DatabaseKind = a.DatabaseKind;
                databasePage.Items[i] = old; previous.Remove(a.Key);
            }
            DisposeIcons(new List<AppInfo>(previous.Values));
            categoryTotal = databasePage.Total;
            return databasePage.Items;
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

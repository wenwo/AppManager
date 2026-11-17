using System;
using System.Collections.Generic;
using AppManager.Controls;

namespace AppManager
{
    /// <summary>「添加文件 / 添加文件夹 / 添加软件」页：和设置页一样盖在主窗口标题行以下，不另弹窗口。</summary>
    public partial class MainForm
    {
        /// <summary>
        /// 资源页面多选文件加入当前分类：按分类限定文件类型（页面过滤 + 入库前二次校验）。
        /// 分类和集合在打开时记下：页面打开后集合侧栏会隐藏，ActiveCollectionKey 不再可靠。
        /// </summary>
        private void PickFiles()
        {
            if (addPage != null || settingsOpen) return;
            string category = currentCategory;
            string collectionKey = ActiveCollectionKey;   // 集合视图内：入库后自动加入集合
            string typeName = MediaRules.ResourceCategoryTypeName(category);
            OpenAddPage(new MediaAddPage("添加" + typeName + "到「" + category + "」", typeName,
                MediaRules.ResourceCategoryExt(category) ?? new string[0], null),
                delegate(List<string> picked, List<AppInfo> apps) { AddPaths(picked.ToArray(), category, collectionKey); });
        }

        /// <summary>选择文件夹加入当前分类：可以在几个位置里一起勾选多个文件夹。</summary>
        private void PickFolder()
        {
            if (addPage != null || settingsOpen) return;
            string category = currentCategory;
            string collectionKey = ActiveCollectionKey;   // 集合视图内：入库后自动加入当前集合
            OpenAddPage(MediaAddPage.ForFolders("添加文件夹到「" + category + "」", null),
                delegate(List<string> picked, List<AppInfo> apps) { AddPaths(picked.ToArray(), category, collectionKey); });
        }

        /// <summary>
        /// 添加软件：已安装的软件和便携程序的 .exe 在同一个页面里勾选。已安装的按「加入分类」处理（不重复登记程序文件），
        /// .exe 只登记不搬运。在「系统工具」分类里添加时，要找的多半就是系统自带工具：默认一并列出。
        /// </summary>
        private void PickSoftware()
        {
            if (addPage != null || settingsOpen) return;
            string category = currentCategory;
            OpenAddPage(MediaAddPage.ForSoftware("添加软件到「" + category + "」", PickableSoftware(allApps, category),
                category == "系统工具", null),
                delegate(List<string> picked, List<AppInfo> apps)
                {
                    AddPickedSoftware(apps, category);
                    List<string> files = new List<string>();
                    foreach (string file in picked) if (IsSoftwareFile(file)) files.Add(file);
                    if (files.Count > 0) AddPaths(files.ToArray(), category);
                });
        }

        /// <summary>
        /// 打开添加页：查看器/播放器属于列表区域，先退出（完整播放器只收起，音乐继续播放）；
        /// 点「添加」时先关闭并释放页面，再把勾选的路径和已安装软件交给 onConfirm。
        /// </summary>
        private void OpenAddPage(MediaAddPage page, Action<List<string>, List<AppInfo>> onConfirm)
        {
            HideImageViewer();
            HideTextViewer();
            HideArchiveViewer();
            HideVideoPlayer();
            HideMusicPlayer();
            page.Confirmed += delegate(List<string> picked)
            {
                if (!ReferenceEquals(addPage, page)) return;
                List<AppInfo> apps = page.CheckedApps;   // 页面马上释放：先取出勾选的已安装软件
                CloseAddPage();
                onConfirm(picked, apps);
            };
            page.Cancelled += delegate { if (ReferenceEquals(addPage, page)) CloseAddPage(); };
            addPage = page;
            SetMainContentVisible(false);
            settingsBtn.Visible = false;
            Controls.Add(page);
            LayoutControls();
            page.BringToFront();
            page.FocusBrowse();
            Invalidate();
        }

        /// <summary>关闭添加页：释放页面（后台结果随之作废），按当前模式与分类恢复主内容区。</summary>
        private void CloseAddPage()
        {
            if (!DiscardAddPage()) return;
            settingsBtn.Visible = true;
            SetMainContentVisible(true);
            LayoutControls();
            RefreshGrid();   // 页面打开期间跳过了内容区同步（扫描、刷新），返回时补上
            if (grid.Visible) grid.Focus();
            Invalidate();
        }

        /// <summary>只移除并释放添加页，不恢复主内容区（紧接着要进入别的整页时用）；没有打开时返回 false。</summary>
        private bool DiscardAddPage()
        {
            MediaAddPage page = addPage;
            if (page == null) return false;
            addPage = null;
            Controls.Remove(page);
            page.Dispose();
            return true;
        }
    }
}

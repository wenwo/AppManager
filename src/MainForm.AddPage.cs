using System.Collections.Generic;
using AppManager.Controls;

namespace AppManager
{
    /// <summary>资源分类的「添加文件」页：和设置页一样盖在主窗口标题行以下，不另弹窗口。</summary>
    public partial class MainForm
    {
        /// <summary>
        /// 资源页面多选文件加入当前分类：在主窗口里打开添加页（不弹窗），按分类限定文件类型（页面过滤 + 入库前二次校验）。
        /// 分类和集合在打开时记下：页面打开后集合侧栏会隐藏，ActiveCollectionKey 不再可靠。
        /// </summary>
        private void PickFiles()
        {
            if (addPage != null || settingsOpen) return;
            string category = currentCategory;
            string collectionKey = ActiveCollectionKey;   // 集合视图内：入库后自动加入集合
            string typeName = MediaRules.ResourceCategoryTypeName(category);
            // 查看器/播放器属于列表区域，进入添加页前先退出（完整播放器只收起，音乐继续播放）
            HideImageViewer();
            HideTextViewer();
            HideArchiveViewer();
            HideVideoPlayer();
            HideMusicPlayer();
            MediaAddPage page = new MediaAddPage("添加" + typeName + "到「" + category + "」", typeName,
                MediaRules.ResourceCategoryExt(category) ?? new string[0], null);
            page.Confirmed += delegate(List<string> picked)
            {
                if (!ReferenceEquals(addPage, page)) return;
                CloseAddPage();
                AddPaths(picked.ToArray(), category, collectionKey);
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

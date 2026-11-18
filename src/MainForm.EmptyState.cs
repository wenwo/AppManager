using AppManager.Controls;

namespace AppManager
{
    /// <summary>主列表为空时显示什么：按当前场景给出图标、标题和下一步怎么做。</summary>
    public partial class MainForm
    {
        // 系统图标字体（Segoe Fluent Icons / MDL2）里的字形；系统没有这套字体时只是不画图标
        private const string EmptyGlyphSearch = "";
        private const string EmptyGlyphFolder = "";
        private const string EmptyGlyphApps = "";
        private const string EmptyGlyphStar = "";

        /// <summary>
        /// 列表为空的几种情况分开说：搜不到（换关键词）、集合是空的（怎么放进来）、可编辑的分类是空的（点添加或拖进来）、
        /// 「常用」是空的（右键添加），其余只读视图只说「暂无内容」。keyword 为已去掉首尾空白的搜索词。
        /// </summary>
        private void UpdateGridEmptyState(string keyword)
        {
            bool resource = contentMode == SidebarControl.ContentMode.Website;
            if (!string.IsNullOrEmpty(keyword))
            {
                string shown = (search.Text ?? string.Empty).Trim();
                if (shown.Length > 24) shown = shown.Substring(0, 24) + "…";
                string scope = resource && currentCollection != null ? "当前集合" : "「" + currentCategory + "」";
                grid.SetEmptyState(EmptyGlyphSearch, "没有找到「" + shown + "」",
                    "只在" + scope + "里找；换个关键词试试，或按 Esc 清空搜索。");
                return;
            }
            if (resource && currentCollection != null)
            {
                grid.SetEmptyState(EmptyGlyphFolder, "这个集合还是空的",
                    "点右上角的「添加」把文件直接加进来，",
                    "或者在分类里对资源点右键，选「添加到集合」。");
                return;
            }
            if (currentCategory == "常用")
            {
                grid.SetEmptyState(EmptyGlyphStar, "还没有常用软件", "在别的分类里对软件点右键，选「添加到常用」。");
                return;
            }
            if (CanEditCategory(currentCategory))
            {
                if (resource)
                    grid.SetEmptyState(EmptyGlyphFolder, "「" + currentCategory + "」里还没有内容", "点右上角的「添加」，或把文件直接拖进来。");
                else
                    grid.SetEmptyState(EmptyGlyphApps, "「" + currentCategory + "」里还没有软件", "点右上角的「添加」，把软件加进这个分类。");
                return;
            }
            grid.SetEmptyState(resource ? EmptyGlyphFolder : EmptyGlyphApps, "暂无内容");
        }
    }
}

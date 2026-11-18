using System;
using System.Drawing;
using System.Windows.Forms;

namespace AppManager
{
    /// <summary>
    /// 选封面页：和添加页、设置页一样盖在主窗口标题行以下，不另弹窗口。
    /// 它是从集合编辑页点「从文件选」进来的，所以下面的编辑页原样留着（不隐藏、不结束编辑），
    /// 选完或取消后页面关闭，编辑页直接露出来并带上选好的封面。
    /// </summary>
    public partial class MainForm
    {
        private CoverPickerDialog coverPage;

        /// <summary>有整页盖在标题行以下（添加页或选封面页）：下面的内容区不该再抢到最前面，也不响应主窗口的快捷键。</summary>
        private bool OverlayPageOpen
        {
            get { return addPage != null || coverPage != null; }
        }

        /// <summary>编辑页点「从文件选」：打开选封面页；选定后把路径交回编辑页（保存时才写入配置）。</summary>
        private void OnCollectionPickCover()
        {
            if (coverPage != null || addPage != null || settingsOpen || !collectionInfo.IsEditing) return;
            // 专用的选封面页：按缩略图挑图、按封面比例预览；只列内置解码器能打开的图片
            CoverPickerDialog page = new CoverPickerDialog(null);
            page.TopLevel = false;   // 作为页面嵌进主窗口，不是独立窗口
            page.FormClosed += delegate
            {
                if (!ReferenceEquals(coverPage, page)) return;
                string picked = page.DialogResult == DialogResult.OK ? page.ResultPath : null;
                RemoveCoverPage(false);   // 页面正在自己关闭，随后自行释放
                if (picked != null && collectionInfo.IsEditing) collectionInfo.SetEditCoverPath(picked, picked);
            };
            coverPage = page;
            settingsBtn.Visible = false;
            sidebar.Visible = false;   // 侧栏顶部（「分类」标题和收起按钮）在标题行里，页面盖不到：先收起，关闭时恢复
            Controls.Add(page);
            LayoutControls();   // 先按主窗口大小定好位置，再显示
            page.Show();
            page.BringToFront();
            Invalidate();
        }

        /// <summary>放弃选封面（进入设置页、切换分类等要离开编辑页的时候）：关闭并释放页面，不回传结果；没有打开时什么都不做。</summary>
        private void DiscardCoverPage()
        {
            RemoveCoverPage(true);
        }

        private void RemoveCoverPage(bool dispose)
        {
            CoverPickerDialog page = coverPage;
            if (page == null) return;
            coverPage = null;   // 先清掉引用：页面关闭时触发的 FormClosed 不会再回头处理
            Controls.Remove(page);
            if (dispose && !page.IsDisposed) page.Dispose();
            if (!settingsOpen && addPage == null)
            {
                settingsBtn.Visible = true;
                sidebar.Visible = true;
            }
            LayoutControls();
            if (collectionInfo != null && collectionInfo.Visible) collectionInfo.Focus();
            Invalidate();
        }

        /// <summary>选封面页铺满标题行以下；主窗口每次重排时调用。</summary>
        private void LayoutCoverPage()
        {
            if (coverPage == null) return;
            coverPage.Bounds = new Rectangle(0, Dpi(Theme.TitleRowHeight), ClientSize.Width,
                Math.Max(0, ClientSize.Height - Dpi(Theme.TitleRowHeight)));
        }

        /// <summary>整页（添加页、选封面页）必须一直在最上面：内容区刷新、状态条、扫描提示把别的控件提到前面之后调用。</summary>
        private void KeepOverlayPageOnTop()
        {
            if (addPage != null) addPage.BringToFront();
            if (coverPage != null) coverPage.BringToFront();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using AppManager.Controls;

namespace AppManager
{
    /// <summary>
    /// 「添加软件」用到的公共部分：筛选和描述已安装软件，以及各个选择页面共用的自绘列表。
    /// 添加软件本身已改为主窗口里的添加页（MediaAddPage.ForSoftware），这里不再有单独的窗口。
    /// 默认只列用户安装的软件，Windows 自带的系统工具需打开「显示系统工具」才列出。
    /// </summary>
    internal static class SoftwarePickerDialog
    {
        /// <summary>
        /// 按「是否显示系统工具」和搜索词筛选候选，保持候选原有顺序；名称直接包含搜索词的排在模糊匹配之前。
        /// internal 供回归测试。
        /// </summary>
        internal static List<AppInfo> Filter(IList<AppInfo> candidates, string keyword, bool includeSystem)
        {
            List<AppInfo> direct = new List<AppInfo>();
            List<AppInfo> fuzzy = new List<AppInfo>();
            if (candidates == null) return direct;
            string kw = (keyword ?? string.Empty).Trim();
            for (int i = 0; i < candidates.Count; i++)
            {
                AppInfo a = candidates[i];
                if (a == null) continue;
                if (!includeSystem && Scanner.IsSystemComponent(a)) continue;
                string name = a.Name ?? string.Empty;
                if (kw.Length == 0 || name.IndexOf(kw, StringComparison.CurrentCultureIgnoreCase) >= 0) direct.Add(a);
                else if (Scanner.FuzzyMatch(name, kw)) fuzzy.Add(a);
            }
            direct.AddRange(fuzzy);
            return direct;
        }

        /// <summary>列表第二行的说明：所在分类 + 程序位置（应用商店应用没有可显示的路径）。internal 供回归测试。</summary>
        internal static string Describe(AppInfo app)
        {
            if (app == null) return string.Empty;
            string where = Where(app);
            if (string.IsNullOrEmpty(app.Category)) return where;
            return string.IsNullOrEmpty(where) ? app.Category : app.Category + "  ·  " + where;
        }

        /// <summary>程序在哪：实际启动的程序路径；应用商店应用和系统组件没有可显示的路径，写明类别。</summary>
        internal static string Where(AppInfo app)
        {
            if (app == null) return string.Empty;
            if (!string.IsNullOrEmpty(app.LinkTarget) && !app.LinkTarget.StartsWith("::", StringComparison.Ordinal))
                return app.LinkTarget;
            if (app.IsUwp || Scanner.IsAppsFolderTarget(app.TargetPath))
                return Scanner.IsSystemComponent(app) ? "系统组件" : "应用商店应用";
            return app.TargetPath ?? string.Empty;
        }

        /// <summary>自绘行的列表：记录鼠标悬停行，并且不整块擦除背景（行由 DrawItem 整行重绘）。导入记录窗口复用。</summary>
        internal sealed class PickerList : ThinScrollListBox
        {
            private const int WM_ERASEBKGND = 0x0014;
            private const int WM_MOUSEWHEEL = 0x020A;

            internal int HoverIndex = -1;
            internal bool KeyboardActive;   // 最近一次操作来自键盘：此时才高亮键盘选中的行

            internal PickerList()
            {
                DrawMode = DrawMode.OwnerDrawFixed;
                BorderStyle = BorderStyle.None;
                IntegralHeight = false;
                SelectionMode = SelectionMode.One;
                BackColor = Theme.Card;
                ForeColor = Theme.TextPrimary;
                Font = Theme.BodyFont;
            }

            /// <summary>坐标所在的行；落在最后一行以下的空白处返回 -1。</summary>
            internal int HitRow(Point p)
            {
                int index = IndexFromPoint(p);
                if (index < 0 || index >= Items.Count) return -1;
                return GetItemRectangle(index).Contains(p) ? index : -1;
            }

            internal void InvalidateRow(int index)
            {
                if (index < 0 || index >= Items.Count || !IsHandleCreated) return;
                Invalidate(GetItemRectangle(index));
            }

            internal void ResetHover()
            {
                HoverIndex = -1;
                KeyboardActive = false;
            }

            private void SetHover(int index)
            {
                if (index == HoverIndex) return;
                int old = HoverIndex;
                HoverIndex = index;
                InvalidateRow(old);
                InvalidateRow(index);
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                if (KeyboardActive)
                {
                    KeyboardActive = false;
                    InvalidateRow(SelectedIndex);
                }
                SetHover(HitRow(e.Location));
                base.OnMouseMove(e);
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                SetHover(-1);
                base.OnMouseLeave(e);
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                if (!KeyboardActive)
                {
                    KeyboardActive = true;
                    InvalidateRow(SelectedIndex);
                }
                base.OnKeyDown(e);
            }

            protected override void OnKeyPress(KeyPressEventArgs e)
            {
                // 条目是对象而非文本：屏蔽列表自带的“按首字母跳转”，否则按键会让选中行乱跳
                e.Handled = true;
                base.OnKeyPress(e);
            }

            protected override void OnSelectedIndexChanged(EventArgs e)
            {
                base.OnSelectedIndexChanged(e);
                Invalidate();   // 自绘行不随选中变化自动重绘
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg != WM_ERASEBKGND || m.WParam == IntPtr.Zero)
                {
                    base.WndProc(ref m);
                    // 滚轮的实际滚动由默认窗口过程完成，晚于 OnMouseWheel：滚完再按鼠标位置更新悬停行
                    if (m.Msg == WM_MOUSEWHEEL && IsHandleCreated) SetHover(HitRow(PointToClient(MousePosition)));
                    return;
                }
                // 只擦最后一行以下的空白：行区域交给 DrawItem，避免先擦后画造成闪烁
                int bottom = Items.Count == 0 ? 0 : GetItemRectangle(Items.Count - 1).Bottom;
                if (bottom < ClientSize.Height)
                {
                    using (Graphics g = Graphics.FromHdc(m.WParam))
                    using (SolidBrush back = new SolidBrush(BackColor))
                        g.FillRectangle(back, 0, Math.Max(0, bottom), ClientSize.Width, ClientSize.Height - Math.Max(0, bottom));
                }
                m.Result = (IntPtr)1;
            }
        }
    }
}

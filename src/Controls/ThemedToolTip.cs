using System;
using System.Drawing;
using System.Windows.Forms;
using AppManager;

namespace AppManager.Controls
{
    /// <summary>
    /// 统一样式的悬停提示：深色底、白字，与查看器里的悬浮提示同一套颜色；长文字按最大宽度自动换行。
    /// 用法与 ToolTip 相同（SetToolTip），只是外观改为自绘。
    /// </summary>
    internal sealed class ThemedToolTip : ToolTip
    {
        private const int MaxWidth = 420;   // 96 DPI 下的最大宽度，超出换行
        private const int PadX = 10, PadY = 6;
        private const TextFormatFlags Flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;

        // 提示窗口是系统画的矩形：底色不透明，取主题悬浮卡的颜色
        private static readonly Color Back = Color.FromArgb(Theme.TooltipBg.R, Theme.TooltipBg.G, Theme.TooltipBg.B);

        // 用 Show(文字, 窗口, …) 直接弹出的提示不挂在控件上，GetToolTip 取不到文字：记下最近一次要显示的文字
        private string shownText;

        public ThemedToolTip()
        {
            OwnerDraw = true;
            Popup += OnPopupMeasure;
            Draw += OnDrawTip;
        }

        private static int Scale(Control control, int value)
        {
            return DpiHelper.Scale(value, control == null ? 96 : DpiHelper.ForControl(control));
        }

        private void OnPopupMeasure(object sender, PopupEventArgs e)
        {
            string text = e.AssociatedControl == null ? null : GetToolTip(e.AssociatedControl);
            if (string.IsNullOrEmpty(text)) text = shownText;
            if (string.IsNullOrEmpty(text)) { e.Cancel = true; return; }
            Control c = e.AssociatedControl;
            Size size = TextRenderer.MeasureText(text, Theme.CaptionFont, new Size(Scale(c, MaxWidth), int.MaxValue), Flags);
            e.ToolTipSize = new Size(size.Width + Scale(c, PadX) * 2, size.Height + Scale(c, PadY) * 2);
        }

        /// <summary>在窗口内指定位置弹出提示，duration 毫秒后自动收起。</summary>
        public new void Show(string text, IWin32Window window, int x, int y, int duration)
        {
            shownText = text;
            base.Show(text, window, x, y, duration);
        }

        public new void Show(string text, IWin32Window window, int x, int y)
        {
            shownText = text;
            base.Show(text, window, x, y);
        }

        private void OnDrawTip(object sender, DrawToolTipEventArgs e)
        {
            using (SolidBrush back = new SolidBrush(Back)) e.Graphics.FillRectangle(back, e.Bounds);
            Control c = e.AssociatedControl;
            Rectangle text = Rectangle.Inflate(e.Bounds, -Scale(c, PadX), -Scale(c, PadY));
            TextRenderer.DrawText(e.Graphics, e.ToolTipText, Theme.CaptionFont, text, Color.White, Back, Flags);
        }
    }
}

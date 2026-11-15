using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AppManager.Controls;

namespace AppManager
{
    /// <summary>
    /// 自绘提示框：与确认框同一张卡片。参数顺序、按钮组合和返回值与 MessageBox.Show 相同，可以直接替换。
    /// 不在界面线程上调用、或用到这里没有的按钮组合时，退回系统提示框，行为与替换前一致。
    /// </summary>
    internal static class MessageDialog
    {
        internal static DialogResult Show(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            return Show(null, text, caption, buttons, icon, MessageBoxDefaultButton.Button1);
        }

        internal static DialogResult Show(IWin32Window owner, string text, string caption, MessageBoxButtons buttons,
            MessageBoxIcon icon)
        {
            return Show(owner, text, caption, buttons, icon, MessageBoxDefaultButton.Button1);
        }

        internal static DialogResult Show(IWin32Window owner, string text, string caption, MessageBoxButtons buttons,
            MessageBoxIcon icon, MessageBoxDefaultButton defaultButton)
        {
            string[] labels;
            DialogResult[] results;
            DialogResult escape;
            Control ownerControl = owner as Control;
            if (!Describe(buttons, out labels, out results, out escape) || !OnUiThread(ownerControl))
            {
                return owner == null ? MessageBox.Show(text, caption, buttons, icon, defaultButton)
                    : MessageBox.Show(owner, text, caption, buttons, icon, defaultButton);
            }
            int index = defaultButton == MessageBoxDefaultButton.Button2 ? 1
                : (defaultButton == MessageBoxDefaultButton.Button3 ? 2 : 0);
            if (index >= labels.Length) index = 0;
            PlaySound(icon);
            using (MessageForm form = new MessageForm(caption, text, labels, results, index, escape))
            {
                ConfirmDialog.ShowModal(form, ownerControl == null ? null : ownerControl.FindForm());
                return form.Result;
            }
        }

        /// <summary>按钮文字、各自的返回值，以及按 Esc 的返回值（None 表示 Esc 无效，与系统「是/否」提示框一致）。</summary>
        private static bool Describe(MessageBoxButtons buttons, out string[] labels, out DialogResult[] results,
            out DialogResult escape)
        {
            switch (buttons)
            {
                case MessageBoxButtons.OK:
                    labels = new string[] { "知道了" };
                    results = new DialogResult[] { DialogResult.OK };
                    escape = DialogResult.OK;
                    return true;
                case MessageBoxButtons.OKCancel:
                    labels = new string[] { "确定", "取消" };
                    results = new DialogResult[] { DialogResult.OK, DialogResult.Cancel };
                    escape = DialogResult.Cancel;
                    return true;
                case MessageBoxButtons.YesNo:
                    labels = new string[] { "是", "否" };
                    results = new DialogResult[] { DialogResult.Yes, DialogResult.No };
                    escape = DialogResult.None;
                    return true;
                case MessageBoxButtons.YesNoCancel:
                    labels = new string[] { "是", "否", "取消" };
                    results = new DialogResult[] { DialogResult.Yes, DialogResult.No, DialogResult.Cancel };
                    escape = DialogResult.Cancel;
                    return true;
                default:
                    labels = null;
                    results = null;
                    escape = DialogResult.None;
                    return false;
            }
        }

        /// <summary>自绘窗体只能在界面线程上显示：有归属控件就看它，否则看已打开的第一个窗口。</summary>
        private static bool OnUiThread(Control owner)
        {
            // 线程池等非 STA 线程上不能建自绘窗体（剪贴板等也会出错）
            if (System.Threading.Thread.CurrentThread.GetApartmentState() != System.Threading.ApartmentState.STA) return false;
            // 归属控件还没建句柄时 InvokeRequired 恒为 false，看不出线程：改看已打开的窗口
            if (owner != null && !owner.IsDisposed && owner.IsHandleCreated) return !owner.InvokeRequired;
            try
            {
                FormCollection open = Application.OpenForms;
                return open.Count == 0 || !open[0].InvokeRequired;
            }
            catch (ArgumentOutOfRangeException) { return false; }   // 别的线程正好在关窗口：当作不在界面线程
        }

        /// <summary>保留系统提示框的提示音：后台操作出错时用户不一定正看着窗口。</summary>
        private static void PlaySound(MessageBoxIcon icon)
        {
            if (icon == MessageBoxIcon.Error) System.Media.SystemSounds.Hand.Play();
            else if (icon == MessageBoxIcon.Warning) System.Media.SystemSounds.Exclamation.Play();
            else if (icon == MessageBoxIcon.Question) System.Media.SystemSounds.Question.Play();
            else if (icon == MessageBoxIcon.Information) System.Media.SystemSounds.Asterisk.Play();
        }
    }

    /// <summary>
    /// 提示框和确认框共用的窗体：标题、自动换行的正文、右下角一排按钮。
    /// 大小按正文在实际 DPI 下量出来，正文很长时加宽，再长就按屏幕高度截断并以省略号结尾。
    /// </summary>
    internal sealed class MessageForm : BufferedDialogForm
    {
        // 以下均为 96 DPI 下的设计尺寸
        private const int Pad = 24;
        private const int TextTop = 58;
        private const int MinTextHeight = 40;
        private const int TextGap = 22;          // 正文到按钮
        private const int ButtonHeight = 34;
        private const int ButtonMinWidth = 84;
        private const int ButtonGap = 10;
        private const int BaseWidth = 400;
        private const int WideWidth = 560;
        private const int WidenAbove = 132;      // 正文超过约 6 行时改用加宽版

        private static readonly StringFormat TextFormat = MakeTextFormat();

        private readonly string title;
        private readonly string message;
        private readonly RoundButton[] buttons;
        private readonly DialogResult[] results;
        private readonly int defaultIndex;
        private readonly DialogResult escapeResult;
        private Rectangle textRect;

        /// <summary>用户的选择；没点按钮就关掉时取 Esc 的结果，Esc 无效的组合取最后一个按钮（「否」）。</summary>
        internal DialogResult Result { get; private set; }

        internal MessageForm(string title, string message, string[] labels, DialogResult[] results, int defaultIndex,
            DialogResult escapeResult)
        {
            this.title = title ?? string.Empty;
            this.message = message ?? string.Empty;
            this.results = results;
            this.defaultIndex = defaultIndex;
            this.escapeResult = escapeResult;
            Result = escapeResult != DialogResult.None ? escapeResult : results[results.Length - 1];

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            BackColor = Theme.Card;
            KeyPreview = true;
            Text = this.title;
            ClientSize = new Size(BaseWidth, 200);   // 占位，真正的大小在 OnDpiScaleChanged 里按正文计算

            buttons = new RoundButton[labels.Length];
            for (int i = 0; i < labels.Length; i++)
            {
                DialogResult result = results[i];
                buttons[i] = ConfirmDialog.MakeBtn(labels[i], i == defaultIndex);
                buttons[i].Click += delegate { Choose(result); };
                Controls.Add(buttons[i]);
            }
            ActiveControl = buttons[defaultIndex];
        }

        private static StringFormat MakeTextFormat()
        {
            StringFormat format = new StringFormat();
            format.Trimming = StringTrimming.EllipsisCharacter;
            format.FormatFlags = StringFormatFlags.LineLimit;
            return format;
        }

        private void Choose(DialogResult result)
        {
            Result = result;
            DialogResult = result;
            Close();
        }

        private const int WM_DPICHANGED = 0x02E0;
        private bool laidOut;

        protected override void OnDpiScaleChanged()
        {
            // 首次（显示前）按实际 DPI 排版并居中；之后的 DPI 变化等系统建议的边界生效后再重排，见 WndProc
            if (!laidOut)
            {
                ApplyLayout(true);
                laidOut = true;
            }
            base.OnDpiScaleChanged();
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            // 基类处理 DPI 变化时最后会套用系统建议的边界，按钮位置随之失准：此后按新 DPI 重排，保持窗口中心不动
            if (m.Msg == WM_DPICHANGED && laidOut && !IsDisposed) ApplyLayout(false);
        }

        /// <summary>按当前 DPI 量出正文和按钮，定下窗口大小；recenter 为 false 时保持窗口中心位置不变。</summary>
        private void ApplyLayout(bool recenter)
        {
            Point center = new Point(Left + Width / 2, Top + Height / 2);
            Form anchor = Owner != null && Owner.Visible && Owner.WindowState != FormWindowState.Minimized ? Owner : null;
            Rectangle work = Screen.FromControl(anchor ?? (Control)this).WorkingArea;
            int pad = Dpi(Pad);
            int[] widths = new int[buttons.Length];
            int row = Dpi(ButtonGap) * (buttons.Length - 1);
            int width, textHeight;
            using (Graphics g = CreateGraphics())
            {
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                for (int i = 0; i < buttons.Length; i++)
                {
                    int needed = (int)Math.Ceiling(g.MeasureString(buttons[i].Text, buttons[i].Font).Width) + Dpi(28);
                    widths[i] = Math.Max(Dpi(ButtonMinWidth), needed);
                    row += widths[i];
                }
                width = Math.Max(Dpi(BaseWidth), row + pad * 2);
                int wide = Math.Min(Dpi(WideWidth), work.Width - Dpi(48));
                textHeight = MeasureText(g, width - pad * 2);
                if (textHeight > Dpi(WidenAbove) && wide > width)
                {
                    width = wide;
                    textHeight = MeasureText(g, width - pad * 2);
                }
            }
            int chrome = Dpi(TextTop) + Dpi(TextGap) + Dpi(ButtonHeight) + pad;
            int room = Math.Max(Dpi(MinTextHeight), work.Height - Dpi(48) - chrome);
            textHeight = Math.Max(Dpi(MinTextHeight), Math.Min(textHeight, room));

            ClientSize = new Size(width, chrome + textHeight);
            textRect = new Rectangle(pad, Dpi(TextTop), width - pad * 2, textHeight);
            int x = width - pad;
            int y = ClientSize.Height - pad - Dpi(ButtonHeight);
            for (int i = buttons.Length - 1; i >= 0; i--)
            {
                x -= widths[i];
                buttons[i].Bounds = new Rectangle(x, y, widths[i], Dpi(ButtonHeight));
                x -= Dpi(ButtonGap);
            }
            // 窗口归属的那一层不在屏幕上（最小化或已隐藏）时，以它为准居中会跑到屏幕外
            if (recenter)
            {
                if (anchor != null) CenterToParent(); else CenterToScreen();
                return;
            }
            Rectangle area = Screen.FromPoint(center).WorkingArea;
            Location = new Point(Math.Max(area.Left, Math.Min(center.X - Width / 2, area.Right - Width)),
                Math.Max(area.Top, Math.Min(center.Y - Height / 2, area.Bottom - Height)));
        }

        private int MeasureText(Graphics g, int width)
        {
            if (message.Length == 0) return 0;
            return (int)Math.Ceiling(g.MeasureString(message, Theme.BodyFont, new SizeF(width, 100000F), TextFormat).Height) + 1;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            // 白底由窗体背景色铺好，描边由 BufferedDialogForm 统一画
            Graphics g = e.Graphics;
            using (SolidBrush text = new SolidBrush(Theme.TextPrimary))
            {
                Draw.DrawStringLeft(g, title, Theme.DialogTitleFont, text, new Rectangle(Dpi(Pad), Dpi(18), Width - Dpi(Pad) * 2, Dpi(30)));
                if (message.Length == 0) return;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                g.DrawString(message, Theme.BodyFont, text, textRect, TextFormat);
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                if (escapeResult != DialogResult.None) Choose(escapeResult);
            }
            else if (e.KeyCode == Keys.Enter)
            {
                // 自绘按钮不是系统默认按钮：焦点在哪个按钮上回车就选哪个，否则选默认按钮
                e.Handled = true;
                int focused = Array.IndexOf(buttons, ActiveControl as RoundButton);
                Choose(results[focused < 0 ? defaultIndex : focused]);
            }
            else if (e.Control && e.KeyCode == Keys.C)
            {
                // 与系统提示框一样可以整段复制，方便把报错原文发给别人
                e.Handled = true;
                try { Clipboard.SetText(title.Length == 0 ? message : title + "\r\n" + message); }
                catch (ExternalException) { }   // 剪贴板被别的程序占用：这里没有可提示的位置，再按一次即可
                catch (ArgumentException) { }   // 标题和正文都是空的
            }
            base.OnKeyDown(e);
        }
    }
}

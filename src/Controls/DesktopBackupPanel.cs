using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace AppManager.Controls
{
    /// <summary>
    /// 设置页右侧的「桌面备份清单」面板：顶上一行左边是数量或上一次操作的结果，右边是「全部还原」，下面是清单。
    /// 面板只负责展示和发出请求；还原、移除在后台搬动文件并保存配置，由主窗口执行（见 MainForm.DesktopBackup.cs），
    /// 执行期间主窗口把面板置为忙碌，设置页据此挡住「返回」和目录切换。
    /// </summary>
    internal sealed class DesktopBackupPanel : Control, ISettingsPanel
    {
        private const int ToolbarH = 34;
        private const int Shadow = 8;   // 与设置页约定：左右各多留 8px 放卡片阴影，按钮和文字向内缩进同样距离

        private readonly DesktopBackupListControl list = new DesktopBackupListControl();
        private readonly RoundButton restoreAll = new RoundButton();
        private bool busy;
        private int count;
        private string status = string.Empty;   // 上一次操作的结果；为空时这一行显示数量
        private bool statusWarning;

        public event Action<DesktopBackupEntry> RestoreRequested;
        public event Action<DesktopBackupEntry> RemoveRequested;
        public event Action RestoreAllRequested;
        /// <summary>面板显示出来（切到这个目录）时触发：宿主把最新的清单交进来。</summary>
        public event Action RefreshRequested;
        public event EventHandler BusyChanged;

        public DesktopBackupPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Theme.Background;

            restoreAll.Text = "全部还原";
            restoreAll.Enabled = false;
            restoreAll.Click += delegate
            {
                if (busy || count == 0) return;
                Action handler = RestoreAllRequested;
                if (handler != null) handler();
            };
            Controls.Add(restoreAll);

            list.AccessibleName = "桌面备份清单";
            list.RestoreRequested += delegate(DesktopBackupEntry entry)
            {
                Action<DesktopBackupEntry> handler = RestoreRequested;
                if (!busy && handler != null) handler(entry);
            };
            list.RemoveRequested += delegate(DesktopBackupEntry entry)
            {
                Action<DesktopBackupEntry> handler = RemoveRequested;
                if (!busy && handler != null) handler(entry);
            };
            Controls.Add(list);
        }

        public bool Busy { get { return busy; } }

        /// <summary>换上最新的清单；数量为 0 时「全部还原」停用。</summary>
        public void SetItems(ICollection<DesktopBackupEntry> items)
        {
            count = items == null ? 0 : items.Count;
            list.SetItems(items ?? new DesktopBackupEntry[0]);
            restoreAll.Enabled = !busy && count > 0;
            Invalidate();
        }

        /// <summary>后台正在还原或移除：清单和按钮停用，设置页挡住返回与目录切换。</summary>
        public void SetBusy(bool value)
        {
            if (busy == value) return;
            busy = value;
            list.SetBusy(value);
            restoreAll.Enabled = !busy && count > 0;
            EventHandler handler = BusyChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        /// <summary>在顶上一行显示上一次操作的结果（成功是绿色，需要留意的是橙色）；传空恢复为显示数量。</summary>
        public void SetStatus(string text, bool warning)
        {
            status = text ?? string.Empty;
            statusWarning = warning;
            Invalidate();
        }

        private int Dpi(int value)
        {
            return DpiHelper.Scale(value, DpiHelper.ForControl(this));
        }

        /// <summary>子控件位置由 OnResize 按当前 DPI 计算，不叠加框架缩放。</summary>
        protected override bool ScaleChildren { get { return false; } }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            int w = Dpi(96);
            restoreAll.Bounds = new Rectangle(Math.Max(0, Width - Dpi(Shadow) - w), 0, w, Dpi(ToolbarH));
            int top = Dpi(ToolbarH) + Dpi(10);
            list.Bounds = new Rectangle(0, top, Width, Math.Max(0, Height - top));
            Invalidate();
        }

        /// <summary>每次显示时请宿主重新交一份清单：收纳、还原可能在别处改过它。上一次操作的结果不带到下一次打开。</summary>
        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!Visible || !IsHandleCreated) return;
            if (!busy) status = string.Empty;
            Action handler = RefreshRequested;
            if (handler != null) handler();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Rectangle rect = new Rectangle(Dpi(Shadow), 0, Math.Max(0, restoreAll.Left - Dpi(Shadow) - Dpi(12)), Dpi(ToolbarH));
            bool hasStatus = status.Length > 0;
            Color color = !hasStatus ? Theme.TextSecondary : (statusWarning ? Theme.WarningText : Theme.SuccessText);
            using (SolidBrush brush = new SolidBrush(color))
                Draw.DrawStringLeft(e.Graphics, hasStatus ? status : "共 " + count + " 项", Theme.CaptionFont, brush, rect);
        }

        public bool ScrollByWheel(int delta)
        {
            list.ScrollByWheel(delta);
            return true;
        }

        /// <summary>指针停在按钮或提示行上时，滚轮同样滚动清单。</summary>
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            list.ScrollByWheel(e.Delta);
        }
    }
}

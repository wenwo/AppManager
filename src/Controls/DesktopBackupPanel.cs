using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace AppManager.Controls
{
    /// <summary>
    /// 设置页右侧的「桌面收纳」面板：上方是收纳设置和操作，下面是物理文件清单。
    /// 面板只负责展示和发出请求；还原、移除在后台搬动文件并保存配置，由主窗口执行（见 MainForm.DesktopBackup.cs），
    /// 执行期间主窗口把面板置为忙碌，设置页据此挡住「返回」和目录切换。
    /// </summary>
    internal sealed class DesktopBackupPanel : Control, ISettingsPanel
    {
        private const int ToolbarH = 34;
        private const int Shadow = 8;   // 与设置页约定：左右各多留 8px 放卡片阴影，按钮和文字向内缩进同样距离

        private readonly DesktopBackupListControl list = new DesktopBackupListControl();
        private readonly Panel settings = new Panel { BackColor = Theme.Card };
        private readonly Label modeLabel = Caption("收纳方式");
        private readonly Label scopeLabel = Caption("收纳范围（至少一项）");
        private readonly Label autoLabel = Caption("自动收纳桌面新快捷方式");
        private readonly Label reason = Caption(string.Empty);
        private readonly RadioButton move = ModeButton("搬家");
        private readonly RadioButton copy = ModeButton("副本");
        private readonly CheckRow shortcuts = new CheckRow { Text = "快捷方式" };
        private readonly CheckRow files = new CheckRow { Text = "文件" };
        private readonly CheckRow folders = new CheckRow { Text = "文件夹" };
        private readonly ToggleSwitch automatic = new ToggleSwitch { AccessibleName = "自动收纳桌面新快捷方式", AccessibleRole = AccessibleRole.CheckButton, TabStop = true };
        private readonly RoundButton organize = new RoundButton { Text = "立即整理", Icon = UiIconKind.Organize, Accent = true };
        private readonly RoundButton update = new RoundButton { Text = "更新备份清单" };
        private bool binding, scan;
        private readonly RoundButton restoreAll = new RoundButton();
        private bool busy;
        private int count;
        private string status = string.Empty;   // 上一次操作的结果；为空时这一行显示数量
        private bool statusWarning;

        public event Action<DesktopBackupEntry> RestoreRequested;
        public event Action<DesktopBackupEntry> RemoveRequested;
        public event Action RestoreAllRequested;
        public event Action OrganizeRequested;
        public event Action UpdateRequested;
        public event Action<DesktopOrganizeOptions, bool> OptionsChanged;
        /// <summary>面板显示出来（切到这个目录）时触发：宿主把最新的清单交进来。</summary>
        public event Action RefreshRequested;
        public event EventHandler BusyChanged;

        public DesktopBackupPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Theme.Background;

            settings.Controls.AddRange(new Control[] { modeLabel, move, copy, scopeLabel, shortcuts, files, folders, autoLabel, automatic, reason });
            Controls.Add(settings);
            Controls.Add(organize);
            Controls.Add(update);
            reason.Font = Theme.CaptionFont;
            move.CheckedChanged += delegate { if (move.Checked) ChangeOptions(); };
            copy.CheckedChanged += delegate { if (copy.Checked) ChangeOptions(); };
            shortcuts.CheckedChanged += delegate { ChangeOptions(); };
            files.CheckedChanged += delegate { ChangeOptions(); };
            folders.CheckedChanged += delegate { ChangeOptions(); };
            automatic.CheckedChanged += delegate { ChangeOptions(); };
            automatic.KeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.KeyData != Keys.Space || !automatic.Enabled) return;
                automatic.Checked = !automatic.Checked;
                args.SuppressKeyPress = true;
            };
            autoLabel.Click += delegate { if (automatic.Enabled) automatic.Checked = !automatic.Checked; };
            organize.Click += delegate { if (organize.Enabled && OrganizeRequested != null) OrganizeRequested(); };
            update.Click += delegate { if (update.Enabled && UpdateRequested != null) UpdateRequested(); };

            restoreAll.Text = "全部还原";
            restoreAll.Enabled = false;
            restoreAll.Click += delegate
            {
                if (busy || count == 0) return;
                Action handler = RestoreAllRequested;
                if (handler != null) handler();
            };
            Controls.Add(restoreAll);

            list.AccessibleName = "桌面收纳清单";
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

        private static Label Caption(string text)
        {
            return new Label { Text = text, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Theme.TextPrimary, Font = Theme.BodyFont, UseMnemonic = false };
        }

        private static RadioButton ModeButton(string text)
        {
            RadioButton button = new RadioButton { Text = text, Appearance = Appearance.Button,
                FlatStyle = FlatStyle.Flat, TextAlign = ContentAlignment.MiddleCenter, Font = Theme.ButtonFont };
            button.FlatAppearance.BorderColor = Theme.ControlBorder;
            button.FlatAppearance.CheckedBackColor = Theme.SelectionBg;
            return button;
        }

        internal void Bind(AppConfig config)
        {
            binding = true;
            try
            {
                scan = config.AutoScan;
                move.Checked = !config.OrganizeCopy;
                copy.Checked = config.OrganizeCopy;
                shortcuts.Checked = config.OrganizeShortcut;
                files.Checked = config.OrganizeFile;
                folders.Checked = config.OrganizeDir;
                automatic.Checked = config.AutoOrganizeDesktop;
            }
            finally { binding = false; }
            RefreshOptions();
        }

        private DesktopOrganizeOptions Options
        {
            get { return new DesktopOrganizeOptions(copy.Checked, shortcuts.Checked, files.Checked, folders.Checked); }
        }

        private void ChangeOptions()
        {
            if (binding || busy) return;
            RefreshOptions();
            if (OptionsChanged != null) OptionsChanged(Options, automatic.Checked);
        }

        private void RefreshOptions()
        {
            string blocked = Options.AutoDisabledReason(scan);
            automatic.Enabled = blocked == null;
            reason.Text = blocked ?? (automatic.Checked ? "已开启" : "已关闭");
            reason.ForeColor = blocked == null ? Theme.TextSecondary : Theme.WarningText;
            shortcuts.Enabled = !shortcuts.Checked || files.Checked || folders.Checked;
            files.Enabled = !files.Checked || shortcuts.Checked || folders.Checked;
            folders.Enabled = !folders.Checked || shortcuts.Checked || files.Checked;
            organize.Enabled = !busy && (shortcuts.Checked || files.Checked || folders.Checked);
        }

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
            settings.Enabled = update.Enabled = !busy;
            RefreshOptions();
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
            int pad = Dpi(Shadow), inner = Dpi(14), row = Dpi(32);
            settings.SetBounds(pad, 0, Math.Max(0, Width - pad * 2), Dpi(146));
            int start = Math.Min(Dpi(190), settings.Width / 3);
            int slot = Math.Min(Dpi(100), Math.Max(0, settings.Width - start - inner) / 3);
            modeLabel.SetBounds(inner, Dpi(8), Math.Max(0, start - inner), row);
            move.SetBounds(start, Dpi(8), slot, row);
            copy.SetBounds(start + slot, Dpi(8), slot, row);
            scopeLabel.SetBounds(inner, Dpi(47), Math.Max(0, start - inner), row);
            shortcuts.SetBounds(start, Dpi(47), slot, row);
            files.SetBounds(start + slot, Dpi(47), slot, row);
            folders.SetBounds(start + 2 * slot, Dpi(47), slot, row);
            autoLabel.SetBounds(inner, Dpi(85), Math.Max(0, start - inner), row);
            automatic.SetBounds(start, Dpi(87), Dpi(46), Dpi(28));
            reason.SetBounds(inner, Dpi(116), Math.Max(0, settings.Width - 2 * inner), Dpi(24));
            int bar = settings.Bottom + Dpi(10);
            organize.SetBounds(pad, bar, Dpi(106), Dpi(ToolbarH));
            update.SetBounds(organize.Right + pad, bar, Dpi(116), Dpi(ToolbarH));
            restoreAll.SetBounds(update.Right + pad, bar, Dpi(96), Dpi(ToolbarH));
            int top = organize.Bottom + Dpi(32);
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
            Rectangle rect = new Rectangle(Dpi(Shadow), organize.Bottom, Math.Max(0, Width - Dpi(Shadow) * 2), Dpi(32));
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

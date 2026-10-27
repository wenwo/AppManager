using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using System.Windows.Forms;
using AppManager.Controls;

namespace AppManager
{
    /// <summary>
    /// 自绘上下文菜单：无边框圆角，悬停高亮，点击执行回调并关闭，失焦自动关闭。
    /// 支持悬停联动的级联子菜单：父项右侧带箭头，鼠标移入即展开、移出即收起。
    /// </summary>
    public class MenuWindow : Form
    {
        /// <summary>菜单项。</summary>
        public class MenuItem
        {
            public string Text = string.Empty;
            public Action Action;
            public bool Separator;
            public bool Disabled;
            /// <summary>勾选态：在菜单项左侧绘制 iOS 风格 checkmark（替代文本「√ 」前缀 hack）。</summary>
            public bool Checked;
            /// <summary>非空即视为父项：悬停时在右侧展开级联子菜单，父项自身不响应点击执行。</summary>
            public List<MenuItem> SubItems;
        }

        private readonly List<MenuItem> items;
        private int hover = -1;
        private const int ItemH = 32;
        private const int PadX = 14;
        private const int ArrowW = 16;      // 父项右侧箭头占位宽度
        private const int ArrowGap = 2;     // 子菜单与父菜单之间的水平间距
        private const int SubWidth = 200;   // 级联子菜单宽度
        private readonly Font font;
        private readonly int width;

        /// <summary>本菜单展开的子菜单（级联）。</summary>
        private MenuWindow child;
        /// <summary>已展开子菜单对应的父项索引；-1 表示无。</summary>
        private int childIndex = -1;
        /// <summary>打开本菜单的上级菜单；根菜单为 null。</summary>
        private MenuWindow parentMenu;
        /// <summary>子菜单标记：不抢占焦点，避免父菜单因失焦而误关。</summary>
        private readonly bool isSubmenu;
        /// <summary>根菜单专用的延时收起定时器（仅当存在已展开子菜单时运行）。</summary>
        private Timer closeTimer;
        /// <summary>抑制失焦关闭：子菜单 Show 期间根菜单会瞬时失焦，避免误关整条链。</summary>
        private bool suppressDeactivate;

        public MenuWindow(List<MenuItem> items, int width)
            : this(items, width, false)
        {
        }

        private MenuWindow(List<MenuItem> items, int width, bool isSubmenu)
        {
            this.items = items;
            this.width = width;
            this.isSubmenu = isSubmenu;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            // 不设 TopMost：菜单经 Show(owner) 归属宿主，z 序跟随主窗口（主窗口被其他应用
            // 覆盖时菜单同样被覆盖），避免悬浮于别的应用之上；失焦关闭由 OnDeactivate 负责。
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.MenuBg;
            DoubleBuffered = true;
            font = Theme.FontOf(9F);
            int h = 8;
            for (int i = 0; i < items.Count; i++)
                h += items[i].Separator ? 9 : ItemH;
            ClientSize = new Size(width, h);
            try { Region = new Region(Draw.RoundedPath(ClientRectangle, 12)); } catch { }
            if (!isSubmenu)
            {
                // 仅根菜单持有收起定时器：光标离开级联链后延时收起子菜单
                closeTimer = new Timer();
                closeTimer.Interval = 120;
                closeTimer.Tick += delegate { OnCloseTick(); };
            }
        }

        /// <summary>子菜单不抢焦点：显示时不激活窗口。</summary>
        protected override bool ShowWithoutActivation
        {
            get { return isSubmenu; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                if (isSubmenu) cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            // 子菜单不参与激活：命中测试直接回 MA_NOACTIVATE(3)，禁止系统切换激活窗口。
            // 否则点击子菜单会把激活权移出级联链，根菜单随即收到 WM_ACTIVATE(WA_INACTIVE)
            // 触发 OnDeactivate → CloseChain()，在点击完成前销毁窗口，导致菜单项动作永不执行。
            if (isSubmenu && m.Msg == 0x0021)
            {
                m.Result = (IntPtr)3;   // MA_NOACTIVATE：不激活，但保留鼠标消息
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.MenuBg);
            // 1px 描边：菜单悬浮于内容之上，用边界线明确「菜单层」与底层内容的分界
            using (Pen border = new Pen(Theme.Separator, 1))
                Draw.DrawRoundedRect(g, border, new Rectangle(0, 0, width - 1, ClientSize.Height - 1), 12);
            int y = 4;
            for (int i = 0; i < items.Count; i++)
            {
                MenuItem it = items[i];
                if (it.Separator)
                {
                    using (Pen pen = new Pen(Theme.Separator, 1))
                        g.DrawLine(pen, PadX, y + 4, width - PadX, y + 4);
                    y += 9;
                    continue;
                }
                Rectangle r = new Rectangle(4, y, width - 8, ItemH);
                bool hov = (i == hover) && !it.Disabled;
                if (hov)
                {
                    using (SolidBrush b = new SolidBrush(Theme.MenuHover))
                        Draw.FillRoundedRect(g, b, r, 6);
                }
                bool hasSub = !it.Disabled && it.SubItems != null && it.SubItems.Count > 0;
                bool hasCheck = it.Checked;
                // iOS 上下文菜单：悬停浅灰高亮，文字保持深色（选中态不反白）
                Color tc = it.Disabled ? Theme.TextSecondary : Theme.TextPrimary;
                // 勾选态在左侧预留 checkmark 占位，文字整体右移
                int leftPad = PadX + (hasCheck ? 18 : 0);
                int tw = r.Width - leftPad - PadX - (hasSub ? ArrowW : 0);
                using (SolidBrush br = new SolidBrush(tc))
                    Draw.DrawStringLeft(g, it.Text, font, br,
                        new Rectangle(r.X + leftPad, r.Y, tw, r.Height));
                if (hasCheck)
                    DrawCheckmark(g, r);
                if (hasSub)
                    DrawChevron(g, r, Theme.TextSecondary);
                y += ItemH;
            }
        }

        /// <summary>菜单项左侧的 iOS 风格勾选标记（用线条绘制，比「√」字形更清晰锐利）。</summary>
        private static void DrawCheckmark(Graphics g, Rectangle r)
        {
            int cx = r.X + PadX + 4;
            int cy = r.Y + r.Height / 2;
            using (Pen p = new Pen(Theme.Accent, 1.8F))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                p.LineJoin = LineJoin.Round;
                g.DrawLines(p, new Point[]
                {
                    new Point(cx, cy),
                    new Point(cx + 3, cy + 3),
                    new Point(cx + 8, cy - 4)
                });
            }
        }

        /// <summary>父项右侧的「›」箭头（用线条绘制，比字形更清晰）。</summary>
        private static void DrawChevron(Graphics g, Rectangle r, Color color)
        {
            int cx = r.Right - PadX - 4;
            int cy = r.Y + r.Height / 2;
            using (Pen p = new Pen(color, 1.6F))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                p.LineJoin = LineJoin.Round;
                g.DrawLines(p, new Point[]
                {
                    new Point(cx - 4, cy - 4),
                    new Point(cx, cy),
                    new Point(cx - 4, cy + 4)
                });
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (IsDisposed) return;
            CancelClose();
            int idx = IndexAt(e.Y);
            if (idx != hover) { hover = idx; Invalidate(); }
            UpdateChild(idx);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (IsDisposed) return;
            // 子菜单展开时保持父项高亮，避免光标移向子菜单时父项高亮闪断
            hover = (child != null) ? childIndex : -1;
            Invalidate();
            ArmCloseCheck();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (IsDisposed) return;
            int idx = IndexAt(e.Y);
            if (idx < 0 || idx >= items.Count) { CloseChain(); return; }
            MenuItem it = items[idx];
            if (it.Separator || it.Disabled) return;
            if (it.SubItems != null && it.SubItems.Count > 0)
            {
                // 父项：点击仅展开/保持子菜单，不执行动作（联动展开由悬停负责）
                UpdateChild(idx);
                return;
            }
            Action a = it.Action;
            CloseChain();
            if (a != null) a();
        }

        protected override void OnDeactivate(EventArgs e)
        {
            // 子菜单不抢焦点，故只有根菜单会因失焦而需要关闭整条链；
            // suppressDeactivate 期间（子菜单 Show 中）不关闭，避免释放正在显示的窗口。
            if (!isSubmenu && !suppressDeactivate) CloseChain();
            base.OnDeactivate(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            CloseChild();
            if (closeTimer != null)
            {
                closeTimer.Stop();
                closeTimer.Dispose();
                closeTimer = null;
            }
            base.OnFormClosed(e);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { CloseChain(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        /// <summary>按当前悬停项联动：父项展开对应子菜单，其余情况收起子菜单。</summary>
        private void UpdateChild(int idx)
        {
            if (idx >= 0 && idx < items.Count && !items[idx].Separator && !items[idx].Disabled
                && items[idx].SubItems != null && items[idx].SubItems.Count > 0)
            {
                OpenChild(idx);
                return;
            }
            CloseChild();
        }

        /// <summary>在父项右侧展开子菜单（与父项顶部对齐；右侧空间不足则翻到左侧）。</summary>
        private void OpenChild(int index)
        {
            if (IsDisposed || index < 0 || index >= items.Count) return;
            if (child != null && childIndex == index) return;
            CloseChild();
            MenuItem it = items[index];
            if (it.SubItems == null || it.SubItems.Count == 0) return;
            MenuWindow c = new MenuWindow(it.SubItems, SubWidth, true);
            c.parentMenu = this;
            int leftX = Location.X + Width + ArrowGap;
            int topY = Location.Y + ItemTop(index);
            Rectangle wa = Screen.GetWorkingArea(new Point(leftX, topY));
            if (leftX + c.Width > wa.Right) leftX = Location.X - c.Width - ArrowGap;
            if (leftX < wa.Left) leftX = wa.Left;
            int top = topY;
            if (top + c.Height > wa.Bottom) top = wa.Bottom - c.Height;
            if (top < wa.Top) top = wa.Top;
            c.Location = new Point(leftX, top);
            // 子菜单 Show 期间根菜单会瞬时失焦：临时抑制失焦关闭，避免关闭正在显示的窗口。
            MenuWindow r = Root();
            bool prev = r.suppressDeactivate;
            r.suppressDeactivate = true;
            try
            {
                c.Show(this);
            }
            catch
            {
                try { if (!c.IsDisposed) c.Dispose(); } catch { }
                return;
            }
            finally
            {
                r.suppressDeactivate = prev;
            }
            child = c;
            childIndex = index;
        }

        /// <summary>收起当前子菜单（并递归关闭其后代）。</summary>
        private void CloseChild()
        {
            if (child == null) return;
            MenuWindow c = child;
            child = null;
            childIndex = -1;
            try { c.CloseChild(); if (!c.IsDisposed) c.Close(); } catch { }
        }

        /// <summary>从根菜单关闭整条级联链。</summary>
        private void CloseChain()
        {
            MenuWindow r = Root();
            r.CloseChild();
            try { if (!r.IsDisposed) r.Close(); } catch { }
        }

        /// <summary>光标仍在级联链内时，取消延时收起。</summary>
        private void CancelClose()
        {
            MenuWindow r = Root();
            if (r.closeTimer != null) r.closeTimer.Stop();
        }

        /// <summary>仅当根菜单存在已展开子菜单时启动延时收起（普通菜单行为完全不受影响）。</summary>
        private void ArmCloseCheck()
        {
            MenuWindow r = Root();
            if (r.closeTimer == null || r.child == null) return;
            r.closeTimer.Stop();
            r.closeTimer.Start();
        }

        private void OnCloseTick()
        {
            if (closeTimer != null) closeTimer.Stop();
            if (child == null) return;
            if (InChain(Cursor.Position)) return;   // 光标仍在链内（含菜单间移动），交由后续移出再收起
            CloseChild();
            hover = -1;
            Invalidate();
        }

        /// <summary>判断屏幕坐标点是否落在本菜单或其后代子菜单内。</summary>
        private bool InChain(Point screenPoint)
        {
            if (Bounds.Contains(screenPoint)) return true;
            return child != null && child.InChain(screenPoint);
        }

        private MenuWindow Root()
        {
            MenuWindow m = this;
            while (m.parentMenu != null) m = m.parentMenu;
            return m;
        }

        /// <summary>父项在客户区内的顶部 Y 坐标（与 OnPaint / IndexAt 的累加规则一致）。</summary>
        private int ItemTop(int index)
        {
            int y = 4;
            for (int i = 0; i < index && i < items.Count; i++)
                y += items[i].Separator ? 9 : ItemH;
            return y;
        }

        private int IndexAt(int y)
        {
            int yy = y - 4;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Separator)
                {
                    yy -= 9;
                    if (yy < 0) return -1;
                }
                else
                {
                    if (yy < ItemH) return i;
                    yy -= ItemH;
                }
            }
            return -1;
        }
    }

    /// <summary>
    /// 自绘确认对话框（是/否），圆角卡片 + 自绘按钮。模态且归属当前窗口，随应用一同置前/退后。
    /// </summary>
    public static class ConfirmDialog
    {
        public static bool Show(string title, string message)
        {
            bool result = false;
            Form f = new Form();
            f.FormBorderStyle = FormBorderStyle.None;
            f.StartPosition = FormStartPosition.CenterParent;
            f.ClientSize = new Size(360, 168);
            f.BackColor = Theme.Background;
            f.ShowInTaskbar = false;
            try { f.Region = new Region(Draw.RoundedPath(f.ClientRectangle, 18)); } catch { }
            f.Paint += delegate(object s, PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (SolidBrush b = new SolidBrush(Theme.Card))
                    Draw.FillRoundedRect(g, b, f.ClientRectangle, 18);
                using (SolidBrush tb = new SolidBrush(Theme.TextPrimary))
                    Draw.DrawStringLeft(g, title, Theme.FontOf(11F, FontStyle.Bold), tb, new Rectangle(22, 18, f.Width - 44, 28));
                using (SolidBrush mb = new SolidBrush(Theme.TextPrimary))
                    Draw.DrawStringLeft(g, message, Theme.FontOf(9.5F), mb, new Rectangle(22, 54, f.Width - 44, 50));
            };
            RoundButton ok = MakeBtn("确定", true);
            ok.Bounds = new Rectangle(f.Width / 2 - 130, 124, 120, 32);
            ok.Click += delegate { result = true; f.DialogResult = DialogResult.OK; f.Close(); };
            RoundButton cancel = MakeBtn("取消", false);
            cancel.Bounds = new Rectangle(f.Width / 2 + 10, 124, 120, 32);
            cancel.Click += delegate { result = false; f.DialogResult = DialogResult.Cancel; f.Close(); };
            f.Controls.Add(ok);
            f.Controls.Add(cancel);
            ShowModal(f);
            f.Dispose();
            return result;
        }

        internal static RoundButton MakeBtn(string text, bool accent)
        {
            RoundButton b = new RoundButton();
            b.Accent = accent;
            b.Text = text;
            b.Cursor = Cursors.Hand;
            return b;
        }

        /// <summary>
        /// 以模态方式显示并归属当前窗口：先在宿主之上盖一层半透明遮罩，再显示对话框——
        /// 弹窗区域外整体压暗，明确「弹窗层 / 内容层」的视觉层级（Fluent/macOS 模态惯例）。
        /// 遮罩与宿主同边界同圆角，不抢焦点；无宿主时回退为无归属显示（无遮罩）。
        /// </summary>
        internal static void ShowModal(Form f)
        {
            Form owner = ResolveOwner(f);
            Form scrim = null;
            try
            {
                if (owner != null)
                {
                    scrim = CreateScrim(owner);
                    scrim.Show(owner);
                    f.ShowDialog(scrim);   // CenterParent 以遮罩为基准，边界与宿主一致，定位不变
                    return;
                }
                f.ShowDialog();
            }
            finally
            {
                if (scrim != null) { try { scrim.Dispose(); } catch { } }
            }
        }

        /// <summary>模态遮罩层：与宿主同边界同圆角，半透明黑罩压暗底层内容；NOACTIVATE 不抢焦点。</summary>
        private static Form CreateScrim(Form owner)
        {
            ScrimForm scrim = new ScrimForm();
            scrim.StartPosition = FormStartPosition.Manual;
            scrim.Bounds = owner.Bounds;
            MainForm.ApplyRoundedCorners(scrim);   // 与宿主同圆角，避免直角遮罩在圆角窗口外露出
            return scrim;
        }

        /// <summary>遮罩窗体：黑底 + 主题透明度；不激活、不进任务栏，点击不夺走对话框焦点。</summary>
        private sealed class ScrimForm : Form
        {
            public ScrimForm()
            {
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                BackColor = Color.Black;
                Opacity = Theme.ModalScrimOpacity;
            }

            protected override bool ShowWithoutActivation
            {
                get { return true; }
            }

            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams cp = base.CreateParams;
                    cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                    return cp;
                }
            }
        }

        /// <summary>解析宿主窗口：优先当前活动窗体，其次首个可见窗体；均无则返回 null。
        /// 跳过上下文菜单（MenuWindow）：菜单触发动作瞬间尚未完全关闭，不能成为模态宿主。</summary>
        private static Form ResolveOwner(Form self)
        {
            Form active = Form.ActiveForm;
            if (active != null && active != self && !(active is MenuWindow)) return active;
            foreach (Form open in Application.OpenForms)
                if (open != null && open != self && open.Visible && !(open is MenuWindow)) return open;
            return null;
        }
    }

    /// <summary>
    /// 自绘输入对话框（单行文本），回车确认；输入框为 iOS 风格圆角灰底、无边框，聚焦时白底 + 强调色描边。
    /// </summary>
    public static class InputDialog
    {
        public static string Show(string title, string prompt)
        {
            return Show(title, prompt, null);
        }

        public static string Show(string title, string prompt, string defaultValue)
        {
            string result = null;
            Form f = new Form();
            f.FormBorderStyle = FormBorderStyle.None;
            f.StartPosition = FormStartPosition.CenterParent;
            f.ClientSize = new Size(360, 184);
            f.BackColor = Theme.Background;
            f.ShowInTaskbar = false;
            try { f.Region = new Region(Draw.RoundedPath(f.ClientRectangle, 18)); } catch { }
            f.Paint += delegate(object s, PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (SolidBrush b = new SolidBrush(Theme.Card))
                    Draw.FillRoundedRect(g, b, f.ClientRectangle, 18);
                using (SolidBrush tb = new SolidBrush(Theme.TextPrimary))
                    Draw.DrawStringLeft(g, title, Theme.FontOf(11F, FontStyle.Bold), tb, new Rectangle(22, 16, f.Width - 44, 26));
                using (SolidBrush mb = new SolidBrush(Theme.TextSecondary))
                    Draw.DrawStringLeft(g, prompt, Theme.FontOf(9.5F), mb, new Rectangle(22, 50, f.Width - 44, 22));
            };
            TextBox box = MakeField(f, new Rectangle(22, 80, f.ClientSize.Width - 44, 36), defaultValue);
            RoundButton ok = ConfirmDialog.MakeBtn("确定", true);
            ok.Bounds = new Rectangle(f.Width / 2 - 130, 128, 120, 32);
            ok.Click += delegate { result = box.Text; f.DialogResult = DialogResult.OK; f.Close(); };
            RoundButton cancel = ConfirmDialog.MakeBtn("取消", false);
            cancel.Bounds = new Rectangle(f.Width / 2 + 10, 128, 120, 32);
            cancel.Click += delegate { result = null; f.DialogResult = DialogResult.Cancel; f.Close(); };
            f.Controls.Add(ok);
            f.Controls.Add(cancel);
            box.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { result = box.Text; f.DialogResult = DialogResult.OK; f.Close(); }
                if (e.KeyCode == Keys.Escape) { result = null; f.DialogResult = DialogResult.Cancel; f.Close(); }
            };
            box.SelectAll();
            f.ActiveControl = box;
            ConfirmDialog.ShowModal(f);
            f.Dispose();
            return result;
        }

        /// <summary>
        /// 构建 iOS 风格输入框：圆角容器（灰底无边框，聚焦时白底 + 强调色描边）+ 内嵌无框 TextBox。
        /// 与主界面搜索框保持一致的视觉语言。internal 供集合编辑对话框复用同款字段。
        /// </summary>
        internal static TextBox MakeField(Form parent, Rectangle bounds, string value)
        {
            TextBox box = new TextBox();
            box.BorderStyle = BorderStyle.None;
            box.Font = Theme.FontOf(10F);
            box.ForeColor = Theme.TextPrimary;
            box.BackColor = Theme.SearchBg;
            if (!string.IsNullOrEmpty(value)) box.Text = value;

            Panel field = new Panel();
            field.BackColor = Theme.Card;   // 与卡片同色，圆角之外无缝
            field.Bounds = bounds;
            field.Paint += delegate(object s, PaintEventArgs pe)
            {
                Graphics g = pe.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle r = new Rectangle(0, 0, field.Width - 1, field.Height - 1);
                Color bg = box.Focused ? Color.White : Theme.SearchBg;
                using (SolidBrush b = new SolidBrush(bg)) Draw.FillRoundedRect(g, b, r, 10);
                if (box.Focused)
                {
                    using (Pen p = new Pen(Theme.Accent, 1.5F))
                        Draw.DrawRoundedRect(g, p, r, 10);
                }
            };
            box.Location = new Point(12, (bounds.Height - box.PreferredHeight) / 2);
            box.Width = bounds.Width - 24;
            box.GotFocus += delegate { box.BackColor = Color.White; field.Invalidate(); };
            box.LostFocus += delegate { box.BackColor = Theme.SearchBg; field.Invalidate(); };
            field.Controls.Add(box);
            parent.Controls.Add(field);
            return box;
        }
    }

    /// <summary>
    /// 集合新建对话框：仅名称（单行）/ 简介（多行）；封面在集合信息页内编辑。
    /// 确定（名称非空）返回填充后的 ResourceCollection；取消返回 null。
    /// </summary>
    public static class CollectionDialog
    {
        /// <summary>新建集合。initial 为 null 表示各字段为空。</summary>
        public static ResourceCollection Show(string title, ResourceCollection initial)
        {
            string name0 = initial == null ? string.Empty : (initial.Name ?? string.Empty);
            string desc0 = initial == null ? string.Empty : (initial.Description ?? string.Empty);
            string coverPath = initial == null ? string.Empty : (initial.Cover ?? string.Empty);   // 新建不编辑封面，仅沿用旧值
            ResourceCollection result = null;

            Form f = new Form();
            f.FormBorderStyle = FormBorderStyle.None;
            f.StartPosition = FormStartPosition.CenterParent;
            f.ClientSize = new Size(400, 260);
            f.BackColor = Theme.Background;
            f.ShowInTaskbar = false;
            try { f.Region = new Region(Draw.RoundedPath(f.ClientRectangle, 18)); } catch { }
            f.Paint += delegate(object s, PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (SolidBrush b = new SolidBrush(Theme.Card))
                    Draw.FillRoundedRect(g, b, f.ClientRectangle, 18);
                using (SolidBrush tb = new SolidBrush(Theme.TextPrimary))
                    Draw.DrawStringLeft(g, title, Theme.FontOf(11F, FontStyle.Bold), tb,
                        new Rectangle(22, 16, f.Width - 44, 26));
                using (SolidBrush lb = new SolidBrush(Theme.TextSecondary))
                {
                    Draw.DrawStringLeft(g, "名称", Theme.FontOf(9F), lb, new Rectangle(22, 50, 120, 20));
                    Draw.DrawStringLeft(g, "简介", Theme.FontOf(9F), lb, new Rectangle(22, 114, 120, 20));
                }
            };

            TextBox nameBox = InputDialog.MakeField(f, new Rectangle(22, 72, f.ClientSize.Width - 44, 34), name0);

            // 简介：多行文本，套圆角容器
            TextBox descBox = new TextBox();
            descBox.Multiline = true;
            descBox.BorderStyle = BorderStyle.None;
            descBox.Font = Theme.FontOf(9.5F);
            descBox.ForeColor = Theme.TextPrimary;
            descBox.BackColor = Theme.SearchBg;
            descBox.ScrollBars = ScrollBars.Vertical;
            descBox.Text = desc0;
            Panel descPanel = new Panel();
            descPanel.BackColor = Theme.Card;
            descPanel.Bounds = new Rectangle(22, 136, f.ClientSize.Width - 44, 62);
            descPanel.Paint += delegate(object s, PaintEventArgs pe)
            {
                Graphics g = pe.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle r = new Rectangle(0, 0, descPanel.Width - 1, descPanel.Height - 1);
                using (SolidBrush b = new SolidBrush(descBox.Focused ? Color.White : Theme.SearchBg))
                    Draw.FillRoundedRect(g, b, r, 10);
                if (descBox.Focused)
                    using (Pen p = new Pen(Theme.Accent, 1.5F)) Draw.DrawRoundedRect(g, p, r, 10);
            };
            descBox.Location = new Point(12, 8);
            descBox.Size = new Size(descPanel.Width - 24, descPanel.Height - 16);
            descBox.GotFocus += delegate { descBox.BackColor = Color.White; descPanel.Invalidate(); };
            descBox.LostFocus += delegate { descBox.BackColor = Theme.SearchBg; descPanel.Invalidate(); };
            descPanel.Controls.Add(descBox);
            f.Controls.Add(descPanel);

            RoundButton ok = ConfirmDialog.MakeBtn("确定", true);
            ok.Bounds = new Rectangle(f.Width / 2 - 130, f.Height - 52, 120, 32);
            ok.Click += delegate
            {
                string name = (nameBox.Text ?? string.Empty).Trim();
                if (name.Length == 0)
                {
                    MessageBox.Show("请输入集合名称。", title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                result = new ResourceCollection();
                result.Name = name;
                result.Description = (descBox.Text ?? string.Empty).Trim();
                result.Cover = coverPath;
                f.DialogResult = DialogResult.OK;
                f.Close();
            };
            RoundButton cancel = ConfirmDialog.MakeBtn("取消", false);
            cancel.Bounds = new Rectangle(f.Width / 2 + 10, f.Height - 52, 120, 32);
            cancel.Click += delegate { result = null; f.DialogResult = DialogResult.Cancel; f.Close(); };
            f.Controls.Add(ok);
            f.Controls.Add(cancel);
            // 表单级 Esc：焦点在简介框等任意位置都能取消（原先仅在名称框内生效）
            f.KeyPreview = true;
            f.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Escape) return;
                result = null;
                f.DialogResult = DialogResult.Cancel;
                f.Close();
            };

            f.ActiveControl = nameBox;
            ConfirmDialog.ShowModal(f);
            f.Dispose();
            return result;
        }
    }

    /// <summary>
    /// 资源属性对话框：左侧缩略图（照片显示真实预览，其余类型显示文件图标），右侧名称与元数据
    /// （类型/大小/拍摄时间/尺寸/地点/位置/创建与修改时间）。
    /// 信息组织参考 Windows「属性」、macOS「显示简介」与相册详情面板；照片的拍摄时间与 GPS 地点取自 EXIF。
    /// </summary>
    public static class PropertiesDialog
    {
        private const int DlgW = 520;
        private const int Pad = 22;
        private const int PreviewW = 148;
        private const int RowH = 26;
        private const int LabelW = 84;

        public static void Show(AppInfo app)
        {
            if (app == null) return;
            string path = app.TargetPath;
            bool folder = !string.IsNullOrEmpty(path) && Directory.Exists(path);
            bool file = !string.IsNullOrEmpty(path) && File.Exists(path);
            if (!folder && !file) return;

            Bitmap preview;
            List<string[]> rows = BuildRows(app, path, folder, out preview);
            int rowsBottom = 82 + rows.Count * RowH;
            int clientH = rowsBottom + 12 + 32 + 22;

            Form f = new Form();
            f.FormBorderStyle = FormBorderStyle.None;
            f.StartPosition = FormStartPosition.CenterParent;
            f.ClientSize = new Size(DlgW, clientH);
            f.BackColor = Theme.Background;
            f.ShowInTaskbar = false;
            try { f.Region = new Region(Draw.RoundedPath(f.ClientRectangle, 18)); } catch { }

            // 克隆图标：模态期间后台静默重扫可能释放原图标，直接引用会画到已释放位图；
            // 图标尚未加载时按需提取一次，避免属性页预览区空白
            Bitmap icon = null;
            try
            {
                icon = app.Icon != null ? new Bitmap(app.Icon) : Icons.Extract(app, Theme.IconSize);
            }
            catch { }
            int rightX = Pad + PreviewW + 16;
            int rightW = DlgW - rightX - Pad;
            f.Paint += delegate(object s, PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (SolidBrush b = new SolidBrush(Theme.Card))
                    Draw.FillRoundedRect(g, b, f.ClientRectangle, 18);
                using (SolidBrush b = new SolidBrush(Theme.TextSecondary))
                    Draw.DrawStringLeft(g, "属性", Theme.FontOf(9F), b, new Rectangle(Pad, 14, DlgW - Pad * 2, 20));

                // 预览：照片用缩略图铺满圆角方块，其余类型居中绘制文件图标
                Rectangle pv = new Rectangle(Pad, 46, PreviewW, PreviewW);
                using (GraphicsPath p = Draw.RoundedPath(pv, 12))
                {
                    using (SolidBrush b = new SolidBrush(Theme.ChipBg))
                        g.FillPath(b, p);
                    GraphicsState st = g.Save();
                    g.SetClip(p);
                    if (preview != null) DrawCover(g, preview, pv);
                    else if (icon != null)
                        g.DrawImage(icon, new Rectangle(pv.X + (pv.Width - Theme.IconSize) / 2,
                            pv.Y + (pv.Height - Theme.IconSize) / 2, Theme.IconSize, Theme.IconSize));
                    g.Restore(st);
                }

                using (SolidBrush nb = new SolidBrush(Theme.TextPrimary))
                    Draw.DrawStringLeft(g, app.DisplayName(), Theme.FontOf(11F, FontStyle.Bold), nb,
                        new Rectangle(rightX, 46, rightW, 24));

                for (int i = 0; i < rows.Count; i++)
                {
                    Rectangle lr = new Rectangle(rightX, 82 + i * RowH, LabelW, RowH);
                    Rectangle vr = new Rectangle(lr.Right + 8, lr.Y, rightW - LabelW - 8, RowH);
                    using (SolidBrush lb = new SolidBrush(Theme.TextSecondary))
                        Draw.DrawStringLeft(g, rows[i][0], Theme.FontOf(9F), lb, lr);
                    using (SolidBrush vb = new SolidBrush(Theme.TextPrimary))
                        Draw.DrawStringLeft(g, rows[i][1], Theme.FontOf(9.5F), vb, vr);
                }
            };

            RoundButton done = ConfirmDialog.MakeBtn("完成", true);
            done.Bounds = new Rectangle(DlgW - Pad - 96, clientH - 22 - 32, 96, 32);
            done.Click += delegate { f.DialogResult = DialogResult.OK; f.Close(); };
            f.Controls.Add(done);
            f.KeyPreview = true;
            f.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape || e.KeyCode == Keys.Enter)
                {
                    f.DialogResult = DialogResult.OK;
                    f.Close();
                }
            };

            // 模态结束后统一释放位图（无论对话框如何关闭）
            try { ConfirmDialog.ShowModal(f); }
            finally
            {
                if (icon != null) icon.Dispose();
                if (preview != null) preview.Dispose();
                f.Dispose();
            }
        }

        /// <summary>构建属性行：类型/大小 +（照片）拍摄时间/尺寸/地点 + 位置/创建与修改时间。</summary>
        private static List<string[]> BuildRows(AppInfo app, string path, bool folder, out Bitmap preview)
        {
            preview = null;
            List<string[]> rows = new List<string[]>();

            string typeText;
            if (folder) typeText = "文件夹";
            else
            {
                string ext = Path.GetExtension(path);
                string typeName = MediaRules.IsImage(path) ? "照片"
                    : MediaRules.IsVideo(path) ? "视频"
                    : MediaRules.IsAudio(path) ? "音乐" : "文件";
                typeText = string.IsNullOrEmpty(ext)
                    ? typeName
                    : typeName + " · " + ext.TrimStart('.').ToUpperInvariant();
            }
            rows.Add(new string[] { "类型", typeText });

            string size = "—";
            if (!folder)
            {
                try { size = HumanSize(new FileInfo(path).Length); } catch { }
            }
            rows.Add(new string[] { "大小", size });

            int width = 0, height = 0;
            string taken = null, geo = null;
            if (!folder && MediaRules.IsImage(path))
                ReadImageInfo(path, out width, out height, out taken, out geo, out preview);

            if (!string.IsNullOrEmpty(taken)) rows.Add(new string[] { "拍摄时间", taken });
            if (width > 0) rows.Add(new string[] { "尺寸", width + " × " + height });
            if (!string.IsNullOrEmpty(geo)) rows.Add(new string[] { "地点", geo });

            string dir = string.Empty;
            try
            {
                string p = folder ? path.TrimEnd(Path.DirectorySeparatorChar) : path;
                dir = Path.GetDirectoryName(p) ?? string.Empty;
            }
            catch { }
            rows.Add(new string[] { "位置", dir.Length == 0 ? path : dir });

            try
            {
                DateTime created = folder ? Directory.GetCreationTime(path) : File.GetCreationTime(path);
                DateTime modified = folder ? Directory.GetLastWriteTime(path) : File.GetLastWriteTime(path);
                rows.Add(new string[] { "创建时间", created.ToString("yyyy-MM-dd HH:mm:ss") });
                rows.Add(new string[] { "修改时间", modified.ToString("yyyy-MM-dd HH:mm:ss") });
            }
            catch { }
            return rows;
        }

        /// <summary>读取图片尺寸、EXIF 拍摄时间/GPS 与缩略图；任一解析失败均静默降级。
        /// 属性项编号采用 EXIF 标准（0x9003 拍摄时间、0x0001~0x0004 GPS 经纬度与半球）。</summary>
        private static void ReadImageInfo(string path, out int width, out int height,
            out string taken, out string geo, out Bitmap preview)
        {
            width = 0; height = 0; taken = null; geo = null; preview = null;
            try
            {
                using (Image img = Image.FromFile(path))
                {
                    width = img.Width;
                    height = img.Height;
                    string dto = null, dtd = null, dt = null, latRef = null, lonRef = null;
                    double lat = double.NaN, lon = double.NaN;
                    int orientation = 1;
                    PropertyItem[] props = img.PropertyItems;
                    for (int i = 0; i < props.Length; i++)
                    {
                        PropertyItem p = props[i];
                        switch (p.Id)
                        {
                            case 0x9003: dto = ExifDate(p.Value); break;   // DateTimeOriginal
                            case 0x9004: dtd = ExifDate(p.Value); break;   // DateTimeDigitized
                            case 0x0132: dt = ExifDate(p.Value); break;    // DateTime
                            case 0x0001: lat = GpsDegree(p.Value); break;  // GPSLatitude
                            case 0x0002: latRef = ExifAscii(p.Value); break; // GPSLatitudeRef
                            case 0x0003: lon = GpsDegree(p.Value); break;  // GPSLongitude
                            case 0x0004: lonRef = ExifAscii(p.Value); break; // GPSLongitudeRef
                            case 0x0112:
                                if (p.Value != null && p.Value.Length >= 2)
                                    orientation = BitConverter.ToUInt16(p.Value, 0);
                                break;
                        }
                    }
                    taken = !string.IsNullOrEmpty(dto) ? dto : (!string.IsNullOrEmpty(dtd) ? dtd : dt);
                    if (!double.IsNaN(lat) && !double.IsNaN(lon)) geo = FormatGeo(lat, latRef, lon, lonRef);
                    preview = MakeThumb(img, orientation);
                }
            }
            catch { }
        }

        /// <summary>生成 2 倍尺寸方形缩略图（148 方块在高分屏下清晰），并按 EXIF 方向校正。
        /// 旋转只作用于缩略图画布，不对原图整体重采样，避免大图打开属性时多一次全图处理。</summary>
        private static Bitmap MakeThumb(Image src, int orientation)
        {
            const int size = 296;
            Bitmap bmp = new Bitmap(size, size);
            try
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    switch (orientation)
                    {
                        case 3: g.TranslateTransform(size, size); g.RotateTransform(180); break;
                        case 6: g.TranslateTransform(size, 0); g.RotateTransform(90); break;
                        case 8: g.TranslateTransform(0, size); g.RotateTransform(270); break;
                    }
                    DrawCover(g, src, new Rectangle(0, 0, size, size));
                }
                return bmp;
            }
            catch
            {
                bmp.Dispose();
                return null;
            }
        }

        /// <summary>等比裁剪填满目标区域（居中裁剪，类似 CSS cover）。</summary>
        private static void DrawCover(Graphics g, Image src, Rectangle dest)
        {
            if (src == null || src.Width <= 0 || src.Height <= 0) return;
            Rectangle srcRect;
            if ((double)src.Width / src.Height > (double)dest.Width / dest.Height)
            {
                int w = (int)(src.Height * dest.Width / (double)dest.Height);
                srcRect = new Rectangle((src.Width - w) / 2, 0, w, src.Height);
            }
            else
            {
                int h = (int)(src.Width * dest.Height / (double)dest.Width);
                srcRect = new Rectangle(0, (src.Height - h) / 2, src.Width, h);
            }
            g.DrawImage(src, dest, srcRect, GraphicsUnit.Pixel);
        }

        /// <summary>EXIF ASCII 字段（如时间/半球）：去掉补零后返回。</summary>
        private static string ExifAscii(byte[] value)
        {
            if (value == null || value.Length == 0) return string.Empty;
            return Encoding.ASCII.GetString(value).Trim('\0', ' ');
        }

        /// <summary>EXIF 时间统一为「yyyy-MM-dd HH:mm:ss」；解析失败时保留原始文本。</summary>
        private static string ExifDate(byte[] value)
        {
            string s = ExifAscii(value);
            DateTime t;
            if (DateTime.TryParseExact(s, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out t))
                return t.ToString("yyyy-MM-dd HH:mm:ss");
            return s;
        }

        /// <summary>GPS 度分秒（3 组有理数）换算为十进制度；非法数据返回 NaN。</summary>
        private static double GpsDegree(byte[] value)
        {
            if (value == null || value.Length < 24) return double.NaN;
            double result = 0;
            for (int i = 0; i < 3; i++)
            {
                uint num = BitConverter.ToUInt32(value, i * 8);
                uint den = BitConverter.ToUInt32(value, i * 8 + 4);
                if (den == 0) return double.NaN;
                result += num / (double)den / Math.Pow(60, i);
            }
            return result;
        }

        /// <summary>经纬度文本：南纬/西经取负并按半球标注，保留 6 位小数。</summary>
        private static string FormatGeo(double lat, string latRef, double lon, string lonRef)
        {
            double la = string.Equals(latRef, "S", StringComparison.OrdinalIgnoreCase) ? -lat : lat;
            double lo = string.Equals(lonRef, "W", StringComparison.OrdinalIgnoreCase) ? -lon : lon;
            return Math.Abs(la).ToString("0.######", CultureInfo.InvariantCulture)
                + "° " + (la >= 0 ? "N" : "S")
                + " · " + Math.Abs(lo).ToString("0.######", CultureInfo.InvariantCulture)
                + "° " + (lo >= 0 ? "E" : "W");
        }

        /// <summary>文件大小文本：B/KB/MB/GB，保留至多两位小数。</summary>
        private static string HumanSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            double kb = bytes / 1024.0;
            if (kb < 1024) return kb.ToString("0.#") + " KB";
            double mb = kb / 1024.0;
            if (mb < 1024) return mb.ToString("0.#") + " MB";
            return (mb / 1024.0).ToString("0.##") + " GB";
        }
    }
}

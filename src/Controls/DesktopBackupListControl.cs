using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;
using AppManager;

namespace AppManager.Controls
{
    /// <summary>
    /// 「桌面收纳」的清单：样式与设置页一致，所有条目装在一张白卡片里，每个条目一行（图标、名称、搬家/副本 + 原位置），
    /// 指针所在或键盘选中的那一行右侧出现「还原」「移除」。只负责展示和发起请求，确认与执行由窗体处理。
    /// </summary>
    internal sealed class DesktopBackupListControl : ScrollListBase
    {
        internal enum RowAction { Restore, Remove }

        // 尺寸均为 96 DPI 下的逻辑像素，使用处统一经 Dpi() 缩放
        private const int SidePad = 8;         // 卡片左右留白（容纳阴影与滚动条）
        private const int TopPad = 4;          // 卡片上方留白（容纳阴影）
        private const int RowH = 64;           // 行高，与设置页的行一致
        private const int RowPad = 18;         // 行内右侧留白，与设置页的卡片一致
        private const int CardRadius = Theme.RadiusCard;
        private const int IconTile = 36;
        private const int ButtonH = 30;
        private const int ButtonW = 60;

        // 图标字形（Segoe Fluent Icons / MDL2 Assets 共有的码位）；系统缺少图标字体时图标块留空
        private const string GlyphFolder = "";
        private const string GlyphFile = "";
        private const string GlyphLink = "";

        private readonly List<DesktopBackupEntry> items = new List<DesktopBackupEntry>();
        private int hoverCard = -1;
        private int selected = -1;
        // 选中行是否画出底色和按钮：用键盘选中时才画；左键点过的行不留底色，只有指针所在的行变浅灰
        private bool selectionShown;
        private int hoverAction = -1;      // 指针所在的行内按钮：0 还原，1 移除，-1 无
        private int pressedCard = -1;
        private int pressedAction = -1;
        private bool busy;
        private bool enterRepeat;          // 最近一次回车按下是按住不放产生的重复
        private bool enterArmed;           // 回车在本控件上按下过：松开时才发起还原

        public event Action<DesktopBackupEntry> RestoreRequested;
        public event Action<DesktopBackupEntry> RemoveRequested;

        public DesktopBackupListControl()
        {
            BackColor = Theme.Background;
        }

        private static Font TitleFont { get { return Theme.FontOf(10F); } }

        // ===== 数据与状态 =====

        internal int Count { get { return items.Count; } }

        /// <summary>换入新的清单，保持原来的选中位置（超出末尾时落到最后一项）。</summary>
        public void SetItems(IEnumerable<DesktopBackupEntry> source)
        {
            items.Clear();
            if (source != null)
                foreach (DesktopBackupEntry entry in source) if (entry != null) items.Add(entry);
            if (selected >= items.Count) selected = items.Count - 1;
            pressedCard = -1;
            pressedAction = -1;
            SetContentHeight(ContentHeight());
            UpdateHover();
            Invalidate();
        }

        /// <summary>后台操作进行中：行内按钮收起，不再响应点击。</summary>
        public void SetBusy(bool value)
        {
            busy = value;
            pressedCard = -1;
            pressedAction = -1;
            UpdateHover();
            Invalidate();
        }

        // ===== 显示文字（纯函数，便于测试）=====

        internal static string DisplayName(DesktopBackupEntry entry)
        {
            string path = (entry.CurrentPath ?? string.Empty).TrimEnd('\\', '/');
            string name;
            try { name = Path.GetFileName(path); }
            catch (ArgumentException) { name = path; }   // 清单来自配置文件：路径里可能有非法字符
            return string.IsNullOrEmpty(name) ? path : name;
        }

        internal static string OriginText(DesktopBackupEntry entry)
        {
            return string.IsNullOrEmpty(entry.OriginalPath) ? "原位置未知" : "原位置  " + entry.OriginalPath;
        }

        /// <summary>名称下面的一行说明：搬家还是副本，后面接原位置。</summary>
        internal static string SummaryText(DesktopBackupEntry entry)
        {
            return (entry.IsCopy ? "副本" : "搬家") + " · " + OriginText(entry);
        }

        // ===== 布局（内容坐标）=====

        private int ContentHeight()
        {
            return items.Count == 0 ? 0 : Dpi(TopPad) + items.Count * Dpi(RowH) + Dpi(8);
        }

        /// <summary>第 index 行的位置；所有行上下相接，合起来就是那张白卡片。</summary>
        private Rectangle CardBounds(int index)
        {
            return new Rectangle(Dpi(SidePad), Dpi(TopPad) + index * Dpi(RowH), Math.Max(0, Width - Dpi(SidePad) * 2), Dpi(RowH));
        }

        private Rectangle ActionBounds(int index, int action)
        {
            Rectangle card = CardBounds(index);
            int right = card.Right - Dpi(RowPad) - (action == 0 ? Dpi(ButtonW) + Dpi(8) : 0);
            return new Rectangle(right - Dpi(ButtonW), card.Top + (card.Height - Dpi(ButtonH)) / 2, Dpi(ButtonW), Dpi(ButtonH));
        }

        /// <summary>这一行当前是否露出行内按钮：指针所在或键盘选中的那一行，且没有后台操作。</summary>
        private bool ActionsShown(int index)
        {
            return !busy && index >= 0 && (index == hoverCard || (index == selected && selectionShown));
        }

        private int CardAt(Point client)
        {
            if (!ClientRectangle.Contains(client)) return -1;
            int y = client.Y + ScrollOffset - Dpi(TopPad);
            if (y < 0) return -1;
            int index = y / Dpi(RowH);
            return index < items.Count && CardBounds(index).Contains(new Point(client.X, client.Y + ScrollOffset)) ? index : -1;
        }

        private int ActionAt(Point client, int card)
        {
            if (!ActionsShown(card)) return -1;
            Point p = new Point(client.X, client.Y + ScrollOffset);
            for (int action = 0; action < 2; action++)
                if (ActionBounds(card, action).Contains(p)) return action;
            return -1;
        }

        /// <summary>测试用：卡片上行内按钮的位置（客户区坐标）。</summary>
        internal Rectangle ActionRect(int index, RowAction action)
        {
            Rectangle r = ActionBounds(index, action == RowAction.Restore ? 0 : 1);
            r.Offset(0, -ScrollOffset);
            return r;
        }

        private void UpdateHover()
        {
            if (!IsHandleCreated || !Visible || OnScrollBar)
            {
                hoverCard = -1;
                hoverAction = -1;
                return;
            }
            Point p = PointToClient(Cursor.Position);
            hoverCard = CardAt(p);
            hoverAction = ActionAt(p, hoverCard);
        }

        protected override void OnScrolled()
        {
            UpdateHover();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            SetContentHeight(ContentHeight());
        }

        private void SetSelected(int index)
        {
            if (index < 0 || index >= items.Count) return;
            Rectangle card = CardBounds(index);
            EnsureVisible(card.Top - Dpi(TopPad), card.Bottom + Dpi(8));
            if (index == selected) return;
            selected = index;
            Invalidate();
        }

        // ===== 绘制 =====

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (SolidBrush back = new SolidBrush(BackColor)) g.FillRectangle(back, ClientRectangle);
            SetContentHeight(ContentHeight());
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (items.Count == 0)
            {
                Draw.DrawEmptyState(g, ClientRectangle, GlyphFolder, "清单是空的", new string[]
                {
                    "收纳过的桌面条目都已还原或移除。",
                    "用「整理桌面」收纳之后，收走的条目会列在这里，可以逐个还原。"
                }, DpiHelper.ForControl(this));
                return;
            }
            GraphicsState state = g.Save();
            try
            {
                g.TranslateTransform(0, -ScrollOffset);
                Draw.DrawListCard(g, new Rectangle(Dpi(SidePad), Dpi(TopPad), Math.Max(0, Width - Dpi(SidePad) * 2), items.Count * Dpi(RowH)),
                    Dpi(CardRadius), ScrollOffset, ScrollOffset + Height);
                int first = Math.Max(0, (ScrollOffset - Dpi(TopPad)) / Dpi(RowH));
                for (int i = first; i < items.Count; i++)
                {
                    Rectangle row = CardBounds(i);
                    if (row.Top > ScrollOffset + Height) break;   // 只画视口内的行
                    PaintRow(g, i, row);
                }
            }
            finally { g.Restore(state); }
            PaintScrollBar(g);
        }

        private void PaintRow(Graphics g, int index, Rectangle b)
        {
            DesktopBackupEntry entry = items[index];
            bool marked = index == selected && selectionShown;
            bool hot = index == hoverCard && !busy;
            bool last = index == items.Count - 1;

            // 行底色：指针所在的行浅灰，键盘选中的行稍深；第一行、最后一行的底色跟随卡片圆角
            if (marked || hot)
            {
                using (SolidBrush fill = new SolidBrush(marked ? Theme.RowHover : Theme.CardHover))
                using (GraphicsPath path = Draw.RowPath(b, Dpi(CardRadius), index == 0, last))
                    g.FillPath(fill, path);
            }

            Rectangle tile = new Rectangle(b.Left + Dpi(16), b.Top + (b.Height - Dpi(IconTile)) / 2, Dpi(IconTile), Dpi(IconTile));
            using (SolidBrush tb = new SolidBrush(Draw.Mix(Theme.Accent, Theme.Card, 0.87f)))
                Draw.FillRoundedRect(g, tb, tile, Dpi(9));
            Draw.DrawGlyph(g, entry.IsDir ? GlyphFolder : (entry.IsShortcut ? GlyphLink : GlyphFile),
                Rectangle.Inflate(tile, -Dpi(9), -Dpi(9)), Theme.Accent);

            int textX = tile.Right + Dpi(14);
            // 行分隔线：从文字起始处画到卡片右侧留白
            if (!last)
            {
                using (Pen sep = new Pen(Theme.Separator, 1))
                    g.DrawLine(sep, textX, b.Bottom - 1, b.Right - Dpi(RowPad), b.Bottom - 1);
            }

            // 名称与说明作为一个整体在行内垂直居中；露出按钮的那一行，文字让出按钮的位置
            int textRight = ActionsShown(index) ? ActionBounds(index, 0).Left - Dpi(16) : b.Right - Dpi(RowPad);
            int textW = Math.Max(0, textRight - textX);
            int titleH = Dpi(22), subH = Dpi(18);
            int blockTop = b.Top + (b.Height - titleH - subH) / 2;
            using (SolidBrush nameBrush = new SolidBrush(Theme.TextPrimary))
                Draw.DrawStringLeft(g, DisplayName(entry), TitleFont, nameBrush, new Rectangle(textX, blockTop, textW, titleH));
            using (SolidBrush subBrush = new SolidBrush(Theme.TextSecondary))
                Draw.DrawStringLeft(g, SummaryText(entry), Theme.CaptionFont, subBrush, new Rectangle(textX, blockTop + titleH, textW, subH));

            if (!ActionsShown(index)) return;
            for (int action = 0; action < 2; action++) PaintButton(g, index, action);
        }

        /// <summary>行内按钮与设置页的一致：白底、灰边、黑字，指针移上去边框带一点强调色。</summary>
        private void PaintButton(Graphics g, int index, int action)
        {
            Rectangle r = ActionBounds(index, action);
            bool hot = index == hoverCard && action == hoverAction && (pressedAction < 0 || (pressedCard == index && pressedAction == action));
            bool pressed = hot && pressedCard == index && pressedAction == action;
            Color border = hot ? Draw.Mix(Theme.Accent, Theme.ControlBorder, 0.5f) : Theme.ControlBorder;
            using (SolidBrush fb = new SolidBrush(pressed ? Theme.RowHover : Theme.Card)) Draw.FillRoundedRect(g, fb, r, Dpi(6));
            using (Pen pen = new Pen(border, 1F))
                Draw.DrawRoundedRect(g, pen, new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1), Dpi(6));
            using (SolidBrush tb = new SolidBrush(Theme.TextPrimary))
                Draw.DrawStringCenter(g, action == 0 ? "还原" : "移除", Theme.ButtonFont, tb, r);
        }

        // ===== 交互 =====

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int card = -1, action = -1;
            if (!ScrollMouseMove(e))
            {
                card = CardAt(e.Location);
                action = ActionAt(e.Location, card);
            }
            if (card == hoverCard && action == hoverAction) return;
            hoverCard = card;
            hoverAction = action;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (hoverCard >= 0 || hoverAction >= 0)
            {
                hoverCard = -1;
                hoverAction = -1;
                Invalidate();
            }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (ScrollMouseDown(e) || e.Button != MouseButtons.Left) return;
            int card = CardAt(e.Location);
            // 左键点过的行不留选中底色；按钮只在指针所在的行露出，先把指针所在行记准再判断按钮
            selectionShown = false;
            hoverCard = card;
            hoverAction = pressedAction = ActionAt(e.Location, card);
            pressedCard = pressedAction >= 0 ? card : -1;
            SetSelected(card);
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (ScrollMouseUp(e) || e.Button != MouseButtons.Left) return;
            int card = pressedCard, action = pressedAction;
            pressedCard = -1;
            pressedAction = -1;
            Invalidate();
            // 按下后移出按钮再松开：视为取消
            if (action < 0 || card != CardAt(e.Location) || action != ActionAt(e.Location, card)) return;
            Raise(action == 0 ? RestoreRequested : RemoveRequested, items[card]);
        }

        /// <summary>鼠标捕获丢失（弹出确认框等）时收不到 MouseUp：在这里结束按下状态。</summary>
        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (Capture || pressedAction < 0) return;
            pressedCard = -1;
            pressedAction = -1;
            Invalidate();
        }

        private static void Raise(Action<DesktopBackupEntry> handler, DesktopBackupEntry entry)
        {
            if (handler != null) handler(entry);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Up:
                case Keys.Down:
                case Keys.Home:
                case Keys.End:
                case Keys.PageUp:
                case Keys.PageDown:
                case Keys.Enter:
                    return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled || e.Modifiers != Keys.None || items.Count == 0) return;
            switch (e.KeyCode)
            {
                case Keys.Down: SetSelected(selected < 0 ? 0 : Math.Min(selected + 1, items.Count - 1)); break;
                case Keys.Up: SetSelected(selected < 0 ? 0 : Math.Max(selected - 1, 0)); break;
                case Keys.Home: SetSelected(0); break;
                case Keys.End: SetSelected(items.Count - 1); break;
                case Keys.PageDown: SetScroll(ScrollOffset + Height); break;
                case Keys.PageUp: SetScroll(ScrollOffset - Height); break;
                case Keys.Delete:
                    if (!busy && selected >= 0) Raise(RemoveRequested, items[selected]);
                    break;
                case Keys.Enter:
                    // 还原在松开回车时才发起（见 OnKeyUp），按住不放的重复按键不算：
                    // 否则重复的回车会落到随后弹出的确认框上，等于没看就确认了还原
                    if (!enterRepeat) enterArmed = !busy && selected >= 0;
                    break;
                default: return;
            }
            // 用键盘操作时把选中的那一行标出来：接下来的回车、删除都作用在它上面
            if (!selectionShown)
            {
                selectionShown = true;
                Invalidate();
            }
            e.Handled = true;
        }

        /// <summary>记下这次回车是不是按住不放产生的重复（消息 lParam 第 30 位：按键此前已处于按下状态）。</summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Enter) enterRepeat = (msg.LParam.ToInt64() & 0x40000000L) != 0;
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (e.KeyCode != Keys.Enter || !enterArmed) return;
            enterArmed = false;
            if (!busy && selected >= 0 && selected < items.Count) Raise(RestoreRequested, items[selected]);
            e.Handled = true;
        }

        protected override void OnLostFocus(EventArgs e)
        {
            enterArmed = false;
            base.OnLostFocus(e);
        }
    }
}

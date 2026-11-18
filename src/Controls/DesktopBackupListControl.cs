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
    /// 「桌面收纳」的卡片列表：每个收纳条目一张卡片（图标、名称、搬家/副本标签、原位置），
    /// 指针所在或选中的卡片右侧出现「还原」「移除」。只负责展示和发起请求，确认与执行由窗体处理。
    /// </summary>
    internal sealed class DesktopBackupListControl : ScrollListBase
    {
        internal enum RowAction { Restore, Remove }

        // 尺寸均为 96 DPI 下的逻辑像素，使用处统一经 Dpi() 缩放
        private const int SidePad = 8;         // 卡片左右留白（容纳阴影与滚动条）
        private const int CardH = 60;
        private const int CardGap = 8;
        private const int CardRadius = 10;
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

        private static Font TitleFont { get { return Theme.FontOf(10F, FontStyle.Bold); } }

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

        // ===== 布局（内容坐标）=====

        private int ContentHeight()
        {
            return items.Count == 0 ? 0 : items.Count * Dpi(CardH + CardGap) - Dpi(CardGap) + Dpi(8);
        }

        private Rectangle CardBounds(int index)
        {
            return new Rectangle(Dpi(SidePad), Dpi(4) + index * Dpi(CardH + CardGap), Math.Max(0, Width - Dpi(SidePad) * 2), Dpi(CardH));
        }

        private Rectangle ActionBounds(int index, int action)
        {
            Rectangle card = CardBounds(index);
            int right = card.Right - Dpi(16) - (action == 0 ? Dpi(ButtonW) + Dpi(8) : 0);
            return new Rectangle(right - Dpi(ButtonW), card.Top + (card.Height - Dpi(ButtonH)) / 2, Dpi(ButtonW), Dpi(ButtonH));
        }

        /// <summary>这张卡片当前是否露出行内按钮：指针所在或选中的那张，且没有后台操作。</summary>
        private bool ActionsShown(int index)
        {
            return !busy && index >= 0 && (index == hoverCard || index == selected);
        }

        private int CardAt(Point client)
        {
            if (!ClientRectangle.Contains(client)) return -1;
            int y = client.Y + ScrollOffset - Dpi(4);
            if (y < 0) return -1;
            int index = y / Dpi(CardH + CardGap);
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
            EnsureVisible(card.Top - Dpi(4), card.Bottom + Dpi(8));
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
                int first = Math.Max(0, (ScrollOffset - Dpi(4)) / Dpi(CardH + CardGap));
                for (int i = first; i < items.Count; i++)
                {
                    Rectangle card = CardBounds(i);
                    if (card.Top > ScrollOffset + Height) break;   // 只画视口内的卡片
                    PaintCard(g, i, card);
                }
            }
            finally { g.Restore(state); }
            PaintScrollBar(g);
        }

        private void PaintCard(Graphics g, int index, Rectangle b)
        {
            DesktopBackupEntry entry = items[index];
            bool isSelected = index == selected;
            bool hot = index == hoverCard && !busy;

            Draw.DrawShadow(g, b, Dpi(CardRadius), 0.18f);
            using (SolidBrush fill = new SolidBrush(isSelected ? Theme.SelectedCard : Theme.Card))
                Draw.FillRoundedRect(g, fill, b, Dpi(CardRadius));
            if (isSelected || hot)
            {
                Color border = isSelected ? Theme.Accent : Draw.Mix(Theme.Accent, Theme.ControlBorder, 0.55f);
                using (Pen pen = new Pen(border, isSelected ? Dpi(15) / 10F : 1F))
                    Draw.DrawRoundedRect(g, pen, new Rectangle(b.X, b.Y, b.Width - 1, b.Height - 1), Dpi(CardRadius));
            }

            Rectangle tile = new Rectangle(b.Left + Dpi(14), b.Top + (b.Height - Dpi(IconTile)) / 2, Dpi(IconTile), Dpi(IconTile));
            using (SolidBrush tb = new SolidBrush(Draw.Mix(Theme.Accent, Theme.Card, 0.87f)))
                Draw.FillRoundedRect(g, tb, tile, Dpi(9));
            Draw.DrawGlyph(g, entry.IsDir ? GlyphFolder : (entry.IsShortcut ? GlyphLink : GlyphFile),
                Rectangle.Inflate(tile, -Dpi(9), -Dpi(9)), Theme.Accent);

            int textX = tile.Right + Dpi(12);
            int textRight = ActionsShown(index) ? ActionBounds(index, 0).Left - Dpi(12) : b.Right - Dpi(16);

            // 第一行：名称 + 搬家/副本标签（标签紧跟名称，名称过长时省略）
            string tag = entry.IsCopy ? "副本" : "搬家";
            Font tagFont = Theme.FontOf(8.5F);
            int tagW = (int)Math.Ceiling(g.MeasureString(tag, tagFont).Width) + Dpi(12);
            string name = DisplayName(entry);
            int nameMax = Math.Max(0, textRight - textX - tagW - Dpi(8));
            int nameW = Math.Min(nameMax, (int)Math.Ceiling(g.MeasureString(name, TitleFont).Width) + Dpi(2));
            using (SolidBrush nameBrush = new SolidBrush(Theme.TextPrimary))
                Draw.DrawStringLeft(g, name, TitleFont, nameBrush, new Rectangle(textX, b.Top + Dpi(9), nameW, Dpi(22)));
            Rectangle chip = new Rectangle(textX + nameW + Dpi(8), b.Top + Dpi(10), tagW, Dpi(20));
            if (chip.Right <= textRight)
            {
                using (SolidBrush chipBack = new SolidBrush(Theme.ChipBg)) Draw.FillRoundedRect(g, chipBack, chip, chip.Height / 2);
                using (SolidBrush chipText = new SolidBrush(Theme.TextSecondary)) Draw.DrawStringCenter(g, tag, tagFont, chipText, chip);
            }
            using (SolidBrush originBrush = new SolidBrush(Theme.TextTertiary))
                Draw.DrawStringLeft(g, OriginText(entry), Theme.CaptionFont, originBrush,
                    new Rectangle(textX, b.Top + Dpi(32), Math.Max(0, textRight - textX), Dpi(18)));

            if (!ActionsShown(index)) return;
            for (int action = 0; action < 2; action++) PaintButton(g, index, action);
        }

        private void PaintButton(Graphics g, int index, int action)
        {
            Rectangle r = ActionBounds(index, action);
            bool hot = index == hoverCard && action == hoverAction && (pressedAction < 0 || (pressedCard == index && pressedAction == action));
            bool pressed = hot && pressedCard == index && pressedAction == action;
            Color fill, border, text;
            if (action == 0)
            {
                fill = pressed ? Draw.Mix(Theme.Accent, Theme.Card, 0.82f) : (hot ? Draw.Mix(Theme.Accent, Theme.Card, 0.91f) : Theme.Card);
                border = Draw.Mix(Theme.Accent, Theme.Card, hot ? 0.3f : 0.55f);
                text = Theme.AccentText;
            }
            else
            {
                fill = pressed ? Theme.RowHover : (hot ? Theme.CardHover : Theme.Card);
                border = hot ? Draw.Mix(Theme.Accent, Theme.ControlBorder, 0.5f) : Theme.ControlBorder;
                text = Theme.TextPrimary;
            }
            using (SolidBrush fb = new SolidBrush(fill)) Draw.FillRoundedRect(g, fb, r, Dpi(6));
            using (Pen pen = new Pen(border, 1F))
                Draw.DrawRoundedRect(g, pen, new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1), Dpi(6));
            using (SolidBrush tb = new SolidBrush(text))
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
            // 先判断按钮再改选中：按钮只在指针所在或已选中的卡片上露出
            pressedAction = ActionAt(e.Location, card);
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

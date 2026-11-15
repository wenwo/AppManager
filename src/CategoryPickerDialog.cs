using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using AppManager.Controls;

namespace AppManager
{
    /// <summary>
    /// 「导入到…」对话框：可选的分类带图标平铺出来，点一下选中，双击或回车确认。
    /// 分类很多时网格内部滚动，窗口最多露出 MaxRows 行。
    /// </summary>
    internal sealed class CategoryPickerDialog : BufferedDialogForm
    {
        private const int Pad = 24;
        private const int DialogWidth = 520;
        private const int Columns = 3;
        private const int TileH = 44;
        private const int TileGap = 8;
        private const int MaxRows = 6;

        private readonly TileList tiles;
        private readonly RoundButton cancel = new RoundButton();

        /// <summary>确认选中的分类；取消时为 null。</summary>
        internal string Picked { get; private set; }

        /// <summary>显示对话框并返回选中的分类；取消或没有可选分类时返回 null。</summary>
        internal static string Show(Form owner, string title, string hint, IList<string> names,
            IList<CategoryIconKind> icons, string preferred)
        {
            using (CategoryPickerDialog dialog = new CategoryPickerDialog(title, hint, names, icons, preferred))
            {
                ConfirmDialog.ShowModal(dialog, owner);
                return dialog.Picked;
            }
        }

        private CategoryPickerDialog(string title, string hint, IList<string> names, IList<CategoryIconKind> icons, string preferred)
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Background;
            ShowInTaskbar = false;
            Text = title;

            Label titleLabel = new Label();
            titleLabel.Text = title;
            titleLabel.Font = Theme.DialogTitleFont;
            titleLabel.ForeColor = Theme.TextPrimary;
            titleLabel.AutoSize = false;
            titleLabel.UseMnemonic = false;
            titleLabel.SetBounds(Pad, 18, DialogWidth - Pad * 2, 28);
            Controls.Add(titleLabel);

            Label hintLabel = new Label();
            hintLabel.Text = names.Count == 0 ? "没有能放下这些内容的分类。" : hint;
            hintLabel.Font = Theme.CaptionFont;
            hintLabel.ForeColor = Theme.TextSecondary;
            hintLabel.AutoSize = false;
            hintLabel.AutoEllipsis = true;
            hintLabel.UseMnemonic = false;
            hintLabel.SetBounds(Pad, 48, DialogWidth - Pad * 2, 22);
            Controls.Add(hintLabel);

            int rows = Math.Max(1, Math.Min(MaxRows, (names.Count + Columns - 1) / Columns));
            int gridHeight = rows * TileH + (rows - 1) * TileGap;
            int index = names.IndexOf(preferred);
            tiles = new TileList(names, icons, names.Count == 0 ? -1 : Math.Max(0, index));
            tiles.SetBounds(Pad, 82, DialogWidth - Pad * 2, gridHeight);
            tiles.AccessibleName = "目标分类";
            tiles.Confirmed += Confirm;
            Controls.Add(tiles);

            int buttonTop = 82 + gridHeight + 20;
            cancel.Text = "取消";
            cancel.SetBounds(DialogWidth - Pad - 84, buttonTop, 84, 34);
            cancel.Click += delegate { Close(); };
            RoundButton ok = new RoundButton();
            ok.Text = "确定";
            ok.Accent = names.Count > 0;
            ok.Enabled = names.Count > 0;
            ok.SetBounds(cancel.Left - 10 - 84, buttonTop, 84, 34);
            ok.Click += delegate { Confirm(); };
            Controls.Add(ok);
            Controls.Add(cancel);

            ClientSize = new Size(DialogWidth, buttonTop + 34 + Pad);
            ActiveControl = tiles;
        }

        private void Confirm()
        {
            string name = tiles.SelectedName;
            if (name == null) return;
            Picked = name;
            DialogResult = DialogResult.OK;
            Close();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            if (keyData == Keys.Enter)
            {
                // 自绘按钮不是系统默认按钮：焦点在「取消」上时回车就是取消，其余情况等同「确定」
                if (cancel.Focused) Close(); else Confirm();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        /// <summary>分类方块网格：单选，方向键移动选中，双击发起确认。</summary>
        private sealed class TileList : ScrollListBase
        {
            private readonly IList<string> names;
            private readonly IList<CategoryIconKind> icons;
            private int selected;
            private int hover = -1;

            internal event Action Confirmed;

            internal TileList(IList<string> names, IList<CategoryIconKind> icons, int selected)
            {
                this.names = names;
                this.icons = icons;
                this.selected = selected;
                BackColor = Theme.Background;
            }

            internal string SelectedName
            {
                get { return selected >= 0 && selected < names.Count ? names[selected] : null; }
            }

            private int RowCount { get { return (names.Count + Columns - 1) / Columns; } }

            private int ContentHeight()
            {
                int rows = RowCount;
                return rows == 0 ? 0 : rows * Dpi(TileH) + (rows - 1) * Dpi(TileGap);
            }

            /// <summary>方块位置（内容坐标）。需要滚动时右侧让出滚动条的位置。</summary>
            private Rectangle TileRect(int index)
            {
                int gap = Dpi(TileGap);
                int room = Width - (ContentHeight() > Height ? Dpi(10) : 0);
                int width = Math.Max(1, (room - gap * (Columns - 1)) / Columns);
                return new Rectangle((index % Columns) * (width + gap), (index / Columns) * (Dpi(TileH) + gap), width, Dpi(TileH));
            }

            private int IndexAt(Point client)
            {
                if (!ClientRectangle.Contains(client)) return -1;
                Point p = new Point(client.X, client.Y + ScrollOffset);
                for (int i = 0; i < names.Count; i++)
                    if (TileRect(i).Contains(p)) return i;
                return -1;
            }

            private void SetHover(int index)
            {
                if (index == hover) return;
                hover = index;
                Invalidate();
            }

            private void Select(int index)
            {
                if (index < 0 || index >= names.Count) return;
                Rectangle r = TileRect(index);
                EnsureVisible(r.Top, r.Bottom);
                if (index == selected) return;
                selected = index;
                Invalidate();
            }

            protected override void OnResize(EventArgs e)
            {
                base.OnResize(e);
                SetContentHeight(ContentHeight());
                // 默认选中的分类可能排在露出的几行之后：大小定下来后把它滚进视口
                if (selected >= 0 && selected < names.Count)
                {
                    Rectangle r = TileRect(selected);
                    EnsureVisible(r.Top, r.Bottom);
                }
            }

            protected override void OnScrolled()
            {
                hover = IsHandleCreated ? IndexAt(PointToClient(Cursor.Position)) : -1;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                using (SolidBrush back = new SolidBrush(BackColor)) g.FillRectangle(back, ClientRectangle);
                SetContentHeight(ContentHeight());
                g.SmoothingMode = SmoothingMode.AntiAlias;
                GraphicsState state = g.Save();
                try
                {
                    g.TranslateTransform(0, -ScrollOffset);
                    for (int i = 0; i < names.Count; i++)
                    {
                        Rectangle r = TileRect(i);
                        if (r.Bottom < ScrollOffset || r.Top > ScrollOffset + Height) continue;
                        PaintTile(g, i, r);
                    }
                }
                finally { g.Restore(state); }
                PaintScrollBar(g);
            }

            private void PaintTile(Graphics g, int index, Rectangle r)
            {
                bool isSelected = index == selected;
                bool hot = index == hover && !OnScrollBar;
                using (SolidBrush fill = new SolidBrush(isSelected ? Theme.SelectionBg : (hot ? Theme.CardHover : Theme.Card)))
                    Draw.FillRoundedRect(g, fill, r, Dpi(8));
                using (Pen border = new Pen(isSelected ? Theme.Accent : Theme.ControlBorder, isSelected ? Dpi(15) / 10F : 1F))
                    Draw.DrawRoundedRect(g, border, new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1), Dpi(8));

                int icon = Dpi(20);
                CategoryIcons.DrawIcon(g, icons[index], new Rectangle(r.X + Dpi(12), r.Y + (r.Height - icon) / 2, icon, icon),
                    isSelected ? Theme.AccentText : Theme.IconColor);
                int textX = r.X + Dpi(12) + icon + Dpi(8);
                int textRight = r.Right - Dpi(isSelected ? 30 : 10);
                using (SolidBrush text = new SolidBrush(isSelected ? Theme.AccentText : Theme.TextPrimary))
                    Draw.DrawStringLeft(g, names[index], Theme.BodyFont, text, new Rectangle(textX, r.Y, Math.Max(0, textRight - textX), r.Height));
                if (!isSelected) return;
                // 选中标记：右侧一个对勾
                float size = Dpi(14);
                float x = r.Right - Dpi(12) - size, y = r.Y + (r.Height - size) / 2F;
                using (Pen mark = new Pen(Theme.Accent, Math.Max(1.6F, Dpi(2))))
                {
                    mark.StartCap = LineCap.Round;
                    mark.EndCap = LineCap.Round;
                    mark.LineJoin = LineJoin.Round;
                    g.DrawLines(mark, new PointF[]
                    {
                        new PointF(x + size * 0.12F, y + size * 0.52F),
                        new PointF(x + size * 0.40F, y + size * 0.80F),
                        new PointF(x + size * 0.90F, y + size * 0.22F)
                    });
                }
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                Focus();
                if (ScrollMouseDown(e)) return;
                if (e.Button == MouseButtons.Left) Select(IndexAt(e.Location));
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                SetHover(ScrollMouseMove(e) ? -1 : IndexAt(e.Location));
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                ScrollMouseUp(e);
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                SetHover(-1);
                base.OnMouseLeave(e);
            }

            protected override void OnMouseDoubleClick(MouseEventArgs e)
            {
                base.OnMouseDoubleClick(e);
                int index = IndexAt(e.Location);
                if (e.Button == MouseButtons.Left && index >= 0 && index == selected && Confirmed != null) Confirmed();
            }

            protected override bool IsInputKey(Keys keyData)
            {
                switch (keyData)
                {
                    case Keys.Left:
                    case Keys.Right:
                    case Keys.Up:
                    case Keys.Down:
                    case Keys.Home:
                    case Keys.End:
                        return true;
                }
                return base.IsInputKey(keyData);
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                base.OnKeyDown(e);
                if (e.Handled || e.Modifiers != Keys.None || names.Count == 0) return;
                int target;
                switch (e.KeyCode)
                {
                    case Keys.Left: target = selected - 1; break;
                    case Keys.Right: target = selected + 1; break;
                    case Keys.Up: target = selected - Columns; break;
                    case Keys.Down: target = selected + Columns; break;
                    case Keys.Home: target = 0; break;
                    case Keys.End: target = names.Count - 1; break;
                    default: return;
                }
                // 越界的移动不换行也不回绕：留在原处
                if (target >= 0 && target < names.Count) Select(target);
                e.Handled = true;
            }
        }
    }
}

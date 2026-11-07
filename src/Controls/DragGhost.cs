using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AppManager;

namespace AppManager.Controls
{
    /// <summary>落点语义：Accept 表示会执行动作，Neutral 表示可停留但无动作，Reject 表示目标不接受。</summary>
    internal enum DragDropIntent
    {
        Neutral,
        Accept,
        Reject
    }

    /// <summary>
    /// 一次拖动会话：只持有独立图标快照和数字，不持有 AppInfo 或图标缓存引用。
    /// 会话号用于忽略上一次拖动遗留的动画 Tick。
    /// </summary>
    internal sealed class DragGhostSession
    {
        internal readonly int Id;
        internal int Count;
        internal int Dpi;
        internal Bitmap Icon;
        internal readonly bool UseSmallIcon;
        internal Point PointerOffset;
        internal readonly Stopwatch Clock = Stopwatch.StartNew();

        internal Point PointerScreen;
        internal DragDropIntent State = DragDropIntent.Neutral;
        internal bool External;
        internal bool Ending;
        internal bool Finished;
        internal bool FailureLogged;
        internal bool EntryComplete;
        internal bool DpiRebuilt;
        internal long EndStartedMs;
        internal float Scale = 1F;
        internal byte Alpha = 255;

        internal Bitmap RenderBitmap;
        internal IntPtr RenderHBitmap;

        internal DragGhostSession(int id, int count, int dpi, Bitmap icon, bool useSmallIcon, Point pointerScreen)
        {
            Id = id;
            Count = count;
            Dpi = dpi;
            Icon = icon;
            UseSmallIcon = useSmallIcon;
            PointerScreen = pointerScreen;
            int offset = DpiHelper.Scale(12, dpi);
            PointerOffset = new Point(offset, offset);
        }

        internal void Dispose()
        {
            Finished = true;
            if (RenderHBitmap != IntPtr.Zero)
            {
                DragGhostForm.DeleteHBitmap(RenderHBitmap);
                RenderHBitmap = IntPtr.Zero;
            }
            if (RenderBitmap != null)
            {
                RenderBitmap.Dispose();
                RenderBitmap = null;
            }
            if (Icon != null)
            {
                Icon.Dispose();
                Icon = null;
            }
        }
    }

    /// <summary>
    /// 单个透明拖影浮层：逐像素 Alpha、无激活、命中穿透。移动时只更新分层窗口位置，
    /// 仅在缩放/落点状态变化时重新合成位图。
    /// </summary>
    internal sealed class DragGhostController : IDisposable
    {
        private const int GhostAnimationMs = 100;
        private const int GhostFadeOutMs = 90;

        private readonly Form owner;
        private readonly Timer animationTimer;
        private DragGhostForm window;
        private DragGhostSession current;
        private int nextSessionId;
        private bool disposed;

        internal DragGhostController(Form owner)
        {
            this.owner = owner;
            animationTimer = new Timer();
            animationTimer.Interval = 15;
            animationTimer.Tick += OnAnimationTick;
        }

        internal DragGhostSession CurrentSession
        {
            get { return current; }
        }

        internal bool HasVisibleSession
        {
            get { return current != null && !current.Finished && window != null && window.Visible; }
        }

        /// <summary>供回归测试确认复用同一分层窗口，且结束后不再残留定时器。</summary>
        internal bool TimerRunning { get { return animationTimer != null && animationTimer.Enabled; } }
        internal IntPtr WindowHandle
        {
            get { return window != null && !window.IsDisposed && window.IsHandleCreated ? window.Handle : IntPtr.Zero; }
        }

        /// <summary>
        /// 开始一轮拖影。items 只在此调用期间读取；图标立即克隆到会话中。
        /// 视觉失败返回 null，调用方继续原拖放流程。
        /// </summary>
        internal DragGhostSession Begin(IList<AppInfo> items, bool useSmallIcon, Point screen)
        {
            if (disposed || items == null || items.Count == 0) return null;
            if (current != null) Finish(current);

            int dpi = owner != null && !owner.IsDisposed ? DpiHelper.ForControl(owner) : 96;
            Bitmap icon = null;
            DragGhostSession session = null;
            try
            {
                AppInfo first = items[0];
                if (first == null) return null;
                icon = CreateIconSnapshot(first, useSmallIcon, dpi);
                session = new DragGhostSession(++nextSessionId, items.Count, dpi, icon, useSmallIcon, screen);
                current = session;
                EnsureWindow();
                bool animate = VisibleAnimationsEnabled();
                session.Scale = animate ? 0.92F : 1F;
                session.Alpha = animate ? (byte)0 : (byte)255;
                Render(session);
                Apply(session);
                if (animate) animationTimer.Start();
                else session.EntryComplete = true;
                return session;
            }
            catch (Exception ex)
            {
                if (session != null) session.Dispose();
                else if (icon != null) icon.Dispose();
                current = null;
                HideWindow();
                Paths.LogError(ex);
                return null;
            }
        }

        /// <summary>更新指针位置。稳定阶段不重建位图，只移动分层窗口。</summary>
        internal void Move(DragGhostSession session, Point screen)
        {
            if (!Usable(session) || session.Ending) return;
            try
            {
                session.PointerScreen = screen;
                UpdateDpi(session, screen);
                Apply(session);
            }
            catch (Exception ex)
            {
                Fail(session, ex);
            }
        }

        /// <summary>OLE 载荷按实际可导出路径数更新；不改变已经开始的会话或图标。</summary>
        internal void SetPayloadCount(DragGhostSession session, int count)
        {
            if (!Usable(session) || session.Ending || count <= 0 || session.Count == count) return;
            try
            {
                session.Count = count;
                Render(session);
                Apply(session);
            }
            catch (Exception ex)
            {
                Fail(session, ex);
            }
        }

        /// <summary>更新落点状态；只在状态实际变化时重绘。</summary>
        internal void SetState(DragGhostSession session, DragDropIntent state)
        {
            if (!Usable(session) || session.Ending || session.State == state) return;
            try
            {
                session.State = state;
                Render(session);
                Apply(session);
            }
            catch (Exception ex)
            {
                Fail(session, ex);
            }
        }

        /// <summary>OLE 接管后由 GiveFeedback 继续驱动跟随；视觉层不结束会话。</summary>
        internal void EnterExternal(DragGhostSession session)
        {
            if (!Usable(session)) return;
            session.External = true;
        }

        /// <summary>幂等结束；正常拖放保留短暂淡出，不使用调用方线程等待动画。</summary>
        internal void End(DragGhostSession session)
        {
            if (!Usable(session)) return;
            if (!VisibleAnimationsEnabled())
            {
                Finish(session);
                return;
            }
            session.Ending = true;
            session.EndStartedMs = session.Clock.ElapsedMilliseconds;
            animationTimer.Start();
        }

        private bool Usable(DragGhostSession session)
        {
            return !disposed && session != null && !session.Finished && ReferenceEquals(session, current);
        }

        private void OnAnimationTick(object sender, EventArgs e)
        {
            DragGhostSession session = current;
            if (session == null || session.Finished)
            {
                animationTimer.Stop();
                return;
            }
            try
            {
                bool needsMore = false;
                long elapsed = session.Clock.ElapsedMilliseconds;

                if (!session.EntryComplete)
                {
                    float t = Math.Min(1F, elapsed / (float)GhostAnimationMs);
                    float eased = EaseOut(t);
                    session.Scale = 0.92F + 0.08F * eased;
                    session.Alpha = (byte)Math.Round(255F * eased);
                    if (t >= 1F)
                    {
                        session.EntryComplete = true;
                        session.Scale = 1F;
                        session.Alpha = 255;
                    }
                    Render(session);
                    Apply(session);
                    if (!session.EntryComplete) needsMore = true;
                }

                if (session.Ending)
                {
                    float t = Math.Min(1F,
                        (elapsed - session.EndStartedMs) / (float)GhostFadeOutMs);
                    session.Alpha = (byte)Math.Round(255F * (1F - EaseOut(t)));
                    Apply(session);
                    if (t >= 1F)
                    {
                        Finish(session);
                        return;
                    }
                    needsMore = true;
                }

                if (!needsMore) animationTimer.Stop();
            }
            catch (Exception ex)
            {
                Fail(session, ex);
            }
        }

        private void EnsureWindow()
        {
            if (window != null && !window.IsDisposed) return;
            window = new DragGhostForm();
            window.CreateControl();
        }

        private void Render(DragGhostSession session)
        {
            if (session.RenderHBitmap != IntPtr.Zero)
            {
                DragGhostForm.DeleteHBitmap(session.RenderHBitmap);
                session.RenderHBitmap = IntPtr.Zero;
            }
            if (session.RenderBitmap != null)
            {
                session.RenderBitmap.Dispose();
                session.RenderBitmap = null;
            }

            Bitmap bitmap = DragGhostPainter.Render(session);
            session.RenderBitmap = bitmap;
            session.RenderHBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
        }

        private void Apply(DragGhostSession session)
        {
            if (session.RenderBitmap == null || session.RenderHBitmap == IntPtr.Zero) return;
            EnsureWindow();
            float invalidFactor = session.State == DragDropIntent.Reject ? 0.55F : 1F;
            byte alpha = (byte)Math.Max(0, Math.Min(255,
                Math.Round(session.Alpha * invalidFactor)));

            int scaledW = session.RenderBitmap.Width;
            int scaledH = session.RenderBitmap.Height;
            int shiftX = (int)Math.Round((1F - session.Scale) * scaledW / 2F);
            int shiftY = (int)Math.Round((1F - session.Scale) * scaledH / 2F);
            Point location = new Point(
                session.PointerScreen.X + session.PointerOffset.X - shiftX,
                session.PointerScreen.Y + session.PointerOffset.Y - shiftY);

            if (!window.Visible) window.ShowNoActivate();
            window.UpdateLayered(session.RenderHBitmap, scaledW, scaledH, location, alpha);
        }

        /// <summary>拖到不同 DPI 的显示器时只重建一次位图；普通移动仍只更新分层窗口位置。</summary>
        private void UpdateDpi(DragGhostSession session, Point screen)
        {
            if (session.DpiRebuilt) return;
            int dpi = DpiForPoint(screen);
            if (dpi <= 0 || dpi == session.Dpi) return;

            int oldDpi = session.Dpi;
            Bitmap replacement = null;
            try
            {
                if (session.Icon != null)
                    replacement = ScaleIconSnapshot(session.Icon, oldDpi, dpi);
            }
            catch (Exception ex)
            {
                if (!session.FailureLogged)
                {
                    session.FailureLogged = true;
                    Paths.LogError(ex);
                }
            }

            Bitmap previous = session.Icon;
            if (replacement != null) session.Icon = replacement;
            session.Dpi = dpi;
            session.PointerOffset = new Point(DpiHelper.Scale(12, dpi), DpiHelper.Scale(12, dpi));
            session.DpiRebuilt = true;
            if (replacement != null && previous != null) previous.Dispose();
            Render(session);
        }

        private static Bitmap ScaleIconSnapshot(Bitmap source, int oldDpi, int newDpi)
        {
            if (source == null || oldDpi <= 0 || newDpi <= 0 || oldDpi == newDpi) return null;
            int width = Math.Max(1, (int)Math.Round(source.Width * (newDpi / (float)oldDpi)));
            int height = Math.Max(1, (int)Math.Round(source.Height * (newDpi / (float)oldDpi)));
            Bitmap scaled = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            try
            {
                using (Graphics g = Graphics.FromImage(scaled))
                {
                    g.Clear(Color.Transparent);
                    g.CompositingMode = CompositingMode.SourceOver;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(source, new Rectangle(0, 0, width, height));
                }
                return scaled;
            }
            catch
            {
                scaled.Dispose();
                throw;
            }
        }

        private static int DpiForPoint(Point screen)
        {
            try
            {
                POINT point = new POINT { X = screen.X, Y = screen.Y };
                IntPtr monitor = MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);
                if (monitor == IntPtr.Zero) return 0;
                uint dpiX, dpiY;
                if (GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out dpiX, out dpiY) != 0) return 0;
                return dpiX > 0 ? (int)dpiX : 0;
            }
            catch { return 0; }
        }

        private void Fail(DragGhostSession session, Exception ex)
        {
            if (session != null && !session.FailureLogged)
            {
                session.FailureLogged = true;
                Paths.LogError(ex);
            }
            Finish(session);
        }

        private void Finish(DragGhostSession session)
        {
            if (session == null) return;
            if (ReferenceEquals(session, current)) current = null;
            session.Dispose();
            if (current == null && animationTimer != null) animationTimer.Stop();
            if (current == null) HideWindow();
        }

        private void HideWindow()
        {
            if (window == null || window.IsDisposed) return;
            try { window.Hide(); }
            catch { }
        }

        private static float EaseOut(float t)
        {
            float inv = 1F - t;
            return 1F - inv * inv * inv;
        }

        private static string GetDisplayName(AppInfo app)
        {
            if (app == null) return string.Empty;
            try { return app.DisplayName(); }
            catch { return app.Name ?? string.Empty; }
        }

        private static Bitmap CreateIconSnapshot(AppInfo app, bool useSmallIcon, int dpi)
        {
            Bitmap source = useSmallIcon ? (app.SmallIcon ?? app.Icon) : (app.Icon ?? app.SmallIcon);
            if (source != null)
            {
                try
                {
                    Bitmap clone = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppPArgb);
                    using (Graphics g = Graphics.FromImage(clone))
                    {
                        g.Clear(Color.Transparent);
                        g.CompositingMode = CompositingMode.SourceOver;
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(source, new Rectangle(0, 0, source.Width, source.Height));
                    }
                    return clone;
                }
                catch { }
            }

            int size = DpiHelper.Scale(useSmallIcon ? Theme.ListIconSize : Theme.IconSize, dpi);
            if (size < 16) size = 16;
            Bitmap placeholder = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(placeholder))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (SolidBrush bg = new SolidBrush(Theme.SegmentBg))
                using (SolidBrush fg = new SolidBrush(Theme.TextSecondary))
                {
                    Rectangle rect = new Rectangle(0, 0, size, size);
                    Draw.FillRoundedRect(g, bg, rect, Math.Max(4, size / 4));
                    string text = GetDisplayName(app);
                    if (!string.IsNullOrEmpty(text)) text = text.Substring(0, 1).ToUpperInvariant();
                    Draw.DrawStringCenter(g, text, Theme.FontPx(Math.Max(8F, size * 0.42F), FontStyle.Bold),
                        fg, rect);
                }
            }
            return placeholder;
        }

        private static bool VisibleAnimationsEnabled()
        {
            int enabled = 0;
            try
            {
                if (!SystemParametersInfo(0x1042, 0, ref enabled, 0)) enabled = 0;
            }
            catch { enabled = 0; }
            return enabled != 0;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        private const uint MONITOR_DEFAULTTONEAREST = 2;
        private const int MDT_EFFECTIVE_DPI = 0;

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT point, uint flags);

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(int action, int param, ref int value, int winIni);

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (current != null) Finish(current);
            if (animationTimer != null)
            {
                animationTimer.Stop();
                animationTimer.Dispose();
            }
            if (window != null)
            {
                if (!window.IsDisposed) window.Dispose();
                window = null;
            }
        }
    }

    /// <summary>拖影位图合成：真实图标、圆角卡片、阴影和多选数量。</summary>
    internal static class DragGhostPainter
    {
        internal static Bitmap Render(DragGhostSession session)
        {
            int dpi = session.Dpi;
            int pad = DpiHelper.Scale(8, dpi);
            int gap = DpiHelper.Scale(4, dpi);
            int margin = DpiHelper.Scale(10, dpi);
            int layers = Math.Max(1, Math.Min(3, session.Count));
            int iconW = session.Icon != null ? session.Icon.Width : DpiHelper.Scale(16, dpi);
            int iconH = session.Icon != null ? session.Icon.Height : DpiHelper.Scale(16, dpi);
            int cardW = Math.Max(DpiHelper.Scale(32, dpi), iconW + pad * 2);
            int cardH = Math.Max(DpiHelper.Scale(32, dpi), iconH + pad * 2);
            int stack = layers > 1 ? gap : 0;
            int badge = session.Count > 1 ? DpiHelper.Scale(22, dpi) : 0;

            int baseW = cardW + stack * (layers - 1) + margin * 2 + badge / 2;
            int baseH = cardH + stack * (layers - 1) + margin * 2;
            int w = Math.Max(DpiHelper.Scale(8, dpi), (int)Math.Ceiling(baseW * session.Scale));
            int h = Math.Max(DpiHelper.Scale(8, dpi), (int)Math.Ceiling(baseH * session.Scale));

            Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingMode = CompositingMode.SourceOver;
                g.ScaleTransform(session.Scale, session.Scale);

                int frontX = margin;
                int frontY = margin;
                int radius = DpiHelper.Scale(Theme.CornerRadius, dpi);
                for (int i = layers - 1; i >= 0; i--)
                {
                    Rectangle card = new Rectangle(
                        frontX + i * stack, frontY + i * stack, cardW, cardH);
                    using (GraphicsPath path = Draw.RoundedPath(card, radius))
                    {
                        if (i == 0) Draw.DrawShadow(g, card, radius, 0.9F);
                        using (SolidBrush bg = new SolidBrush(
                            i == 0 ? Theme.Card : Color.FromArgb(238, Theme.Card)))
                            g.FillPath(bg, path);

                        Color border = session.State == DragDropIntent.Accept
                            ? Theme.Accent
                            : session.State == DragDropIntent.Reject
                                ? Theme.TextTertiary
                                : Theme.ControlBorder;
                        using (Pen pen = new Pen(border, DpiHelper.Scale(
                            session.State == DragDropIntent.Accept ? 2 : 1, dpi)))
                            g.DrawPath(pen, path);
                    }
                }

                if (session.Icon != null)
                {
                    int ix = frontX + (cardW - iconW) / 2;
                    int iy = frontY + (cardH - iconH) / 2;
                    g.DrawImage(session.Icon, new Rectangle(ix, iy, iconW, iconH));
                }

                if (session.Count > 1)
                {
                    int bx = frontX + cardW + stack * (layers - 1) - badge / 2;
                    int by = frontY + stack * (layers - 1) - badge / 2;
                    Rectangle br = new Rectangle(bx, by, badge, badge);
                    using (SolidBrush bg = new SolidBrush(Theme.Accent))
                        g.FillEllipse(bg, br);
                    string text = session.Count > 99 ? "99+" : session.Count.ToString();
                    Draw.DrawStringCenter(g, text,
                        Theme.FontPx(session.Count > 99 ? 9F : 10F, FontStyle.Bold),
                        Brushes.White, br);
                }
            }
            return bmp;
        }
    }

    /// <summary>透明分层窗口：不激活、不进入任务栏、不参与鼠标命中测试。</summary>
    internal sealed class DragGhostForm : Form
    {
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WM_NCHITTEST = 0x0084;
        private const int WM_MOUSEACTIVATE = 0x0021;
        private const int HTTRANSPARENT = -1;
        private const int MA_NOACTIVATE = 3;
        private const int SW_SHOWNOACTIVATE = 4;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const int ULW_ALPHA = 0x00000002;
        private const byte AC_SRC_OVER = 0;
        private const byte AC_SRC_ALPHA = 1;

        internal DragGhostForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.Black;
            Size = new Size(1, 1);
            SetStyle(ControlStyles.Opaque, false);
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
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST)
            {
                m.Result = (IntPtr)HTTRANSPARENT;
                return;
            }
            if (m.Msg == WM_MOUSEACTIVATE)
            {
                m.Result = (IntPtr)MA_NOACTIVATE;
                return;
            }
            base.WndProc(ref m);
        }

        /// <summary>显式按 SW_SHOWNOACTIVATE 显示，避免 Show() 在特殊桌面环境中抢占前台焦点。</summary>
        internal void ShowNoActivate()
        {
            if (!Visible) Show();
            ShowWindow(Handle, SW_SHOWNOACTIVATE);
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        internal void UpdateLayered(IntPtr hBitmap, int width, int height, Point location, byte alpha)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr old = IntPtr.Zero;
            try
            {
                if (screenDc == IntPtr.Zero || memDc == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                old = SelectObject(memDc, hBitmap);
                if (old == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                POINT dst = new POINT { X = location.X, Y = location.Y };
                POINT src = new POINT { X = 0, Y = 0 };
                SIZE size = new SIZE { CX = width, CY = height };
                BLENDFUNCTION blend = new BLENDFUNCTION
                {
                    BlendOp = AC_SRC_OVER,
                    BlendFlags = 0,
                    SourceConstantAlpha = alpha,
                    AlphaFormat = AC_SRC_ALPHA
                };
                if (!UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally
            {
                if (old != IntPtr.Zero) SelectObject(memDc, old);
                if (memDc != IntPtr.Zero) DeleteDC(memDc);
                if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        internal static void DeleteHBitmap(IntPtr handle)
        {
            if (handle != IntPtr.Zero) DeleteObject(handle);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE
        {
            public int CX;
            public int CY;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int command);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter,
            int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst,
            ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc,
            int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hDc);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hDc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hDc, IntPtr hObject);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);
    }
}

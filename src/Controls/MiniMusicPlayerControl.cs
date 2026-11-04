using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace AppManager.Controls
{
    public class MiniMusicPlayerControl : Control
    {
        public event Action<Point> CollectionRequested;
        public event Action DefaultPlayRequested;
        public event Action<int> ExpandRequested;
        public event Action<double> SeekRequested;

        private MusicPlaybackSession session;
        private bool ownsSession;
        private List<string> paths = new List<string>();
        private string title = "尚未播放";
        private string collectionKey;
        private int index;
        private bool playing;
        private double position, duration;
        private int volume = 60;
        private bool muted, seeking, volumeDrag;
        private int hover = -1;
        private Rectangle collectionRect, previousRect, playRect, nextRect, titleRect;
        private Rectangle seekRect, elapsedRect, durationRect, muteRect, volumeRect;

        public string CollectionKey { get { return collectionKey; } }
        public int CurrentIndex { get { return session == null ? index : session.Index; } }
        public double Position { get { return session == null ? position : session.Position; } }
        public bool IsPlaying { get { return session == null ? playing : session.IsPlaying; } }
        public MusicPlaybackSession Session { get { return EnsureSession(); } }
        /// <summary>当前播放列表快照：展开到完整播放器时复用同一顺序，避免重新查询导致下标错位。</summary>
        public List<string> SongPaths { get { return session == null ? new List<string>(paths) : session.PathsSnapshot(); } }

        public MiniMusicPlayerControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        public void LoadPlaylist(List<string> songs, string selectedName, string selectedKey, int startIndex)
        {
            List<string> list = songs ?? new List<string>();
            collectionKey = selectedKey ?? "";
            EnsureSession().Open(list, startIndex, 0, false, PlaybackMode.RepeatAll);
            if (list.Count == 0)
            {
                title = "集合中暂无音乐";
                position = duration = 0;
            }
            Invalidate();
        }

        /// <summary>文件改名或移动后更新播放列表里的路径（含文件夹内的曲目）；不打断正在播放的曲目。</summary>
        public void RebasePath(string oldPath, string newPath)
        {
            if (session != null) session.RebasePath(oldPath, newPath);
        }

        public void Stop()
        {
            if (session != null) session.Stop();
            playing = false;
            position = duration = 0;
            Capture = seeking = volumeDrag = false;
            Invalidate();
        }

        internal void AttachSession(MusicPlaybackSession value)
        {
            if (object.ReferenceEquals(session, value)) { SyncFromSession(true); return; }
            DetachSession();
            session = value;
            ownsSession = false;
            if (session != null)
            {
                session.StateChanged += Session_StateChanged;
                session.TrackChanged += Session_TrackChanged;
            }
            SyncFromSession(true);
        }

        private MusicPlaybackSession EnsureSession()
        {
            if (session != null) return session;
            session = new MusicPlaybackSession();
            ownsSession = true;
            session.StateChanged += Session_StateChanged;
            session.TrackChanged += Session_TrackChanged;
            SyncFromSession(true);
            return session;
        }

        private void DetachSession()
        {
            if (session == null) return;
            session.StateChanged -= Session_StateChanged;
            session.TrackChanged -= Session_TrackChanged;
            if (ownsSession) session.Dispose();
            session = null;
            ownsSession = false;
        }

        private void Session_StateChanged()
        {
            SyncFromSession(false);
            Invalidate();
        }

        private void Session_TrackChanged()
        {
            SyncFromSession(true);
            Invalidate();
        }

        private void SyncFromSession(bool trackChanged)
        {
            if (session == null) return;
            bool changed = trackChanged || paths.Count != session.PathCount || index != session.Index;
            if (changed)
            {
                paths = session.PathsSnapshot();
                index = session.Index;
                string current = session.CurrentTitle;
                title = string.IsNullOrEmpty(session.Failure) ? (string.IsNullOrEmpty(current) ? "尚未播放" : current) : session.Failure;
            }
            else if (!string.IsNullOrEmpty(session.Failure)) title = session.Failure;
            if (!seeking) position = session.Position;
            duration = session.Duration;
            volume = session.Volume;
            muted = session.Muted;
            playing = session.IsPlaying;
        }

        private void TogglePlay()
        {
            if (session == null || !session.HasTracks)
            {
                Action defaultPlay = DefaultPlayRequested;
                if (defaultPlay != null) defaultPlay();
                else RequestCollection();
                return;
            }
            session.TogglePause();
        }

        private void PlayNext() { if (session != null) session.Next(); }

        private void PlayPrevious() { if (session != null) session.Previous(); }

        private int Dpi(int value) { return DpiHelper.Scale(value, DpiHelper.ForControl(this)); }

        private void LayoutPlayer()
        {
            previousRect = new Rectangle(Dpi(4), Dpi(4), Dpi(27), Height - Dpi(8));
            playRect = new Rectangle(previousRect.Right + Dpi(1), (Height - Dpi(26)) / 2, Dpi(26), Dpi(26));
            nextRect = new Rectangle(playRect.Right + Dpi(2), Dpi(4), Dpi(27), Height - Dpi(8));
            collectionRect = new Rectangle(Width - Dpi(34), Dpi(3), Dpi(30), Height - Dpi(6));
            titleRect = new Rectangle(nextRect.Right + Dpi(7), Dpi(3),
                Math.Max(0, collectionRect.Left - nextRect.Right - Dpi(14)), Height - Dpi(6));
            seekRect = elapsedRect = durationRect = muteRect = volumeRect = Rectangle.Empty;
            if (Width < Dpi(520))
            {
                Capture = seeking = volumeDrag = false;
                return;
            }
            titleRect.Width = Math.Min(Dpi(180), (int)(titleRect.Width * 0.32));
            volumeRect = new Rectangle(collectionRect.Left - Dpi(68), (Height - Dpi(18)) / 2, Dpi(58), Dpi(18));
            muteRect = new Rectangle(volumeRect.Left - Dpi(32), Dpi(3), Dpi(28), Height - Dpi(6));
            int timeWidth = Dpi(duration >= 60000 ? 48 : 40);
            elapsedRect = new Rectangle(titleRect.Right + Dpi(8), 0, timeWidth, Height);
            durationRect = new Rectangle(muteRect.Left - Dpi(8) - timeWidth, 0, timeWidth, Height);
            seekRect = new Rectangle(elapsedRect.Right + Dpi(4), (Height - Dpi(18)) / 2,
                Math.Max(0, durationRect.Left - elapsedRect.Right - Dpi(8)), Dpi(18));
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutPlayer();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle card = new Rectangle(0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
            using (Brush brush = new SolidBrush(Theme.SegmentBg)) Draw.FillRoundedRect(g, brush, card, 9);
            using (Pen pen = new Pen(Theme.ControlBorder, 1F)) Draw.DrawRoundedRect(g, pen, card, 9);
            LayoutPlayer();
            DrawHover(g, collectionRect, 0);
            DrawHover(g, previousRect, 1);
            DrawHover(g, nextRect, 3);
            DrawPreviousNext(g, previousRect, false);
            using (Brush brush = new SolidBrush(Theme.Accent)) g.FillEllipse(brush, playRect);
            if (playing) using (Brush brush = new SolidBrush(Color.White))
            {
                g.FillRectangle(brush, playRect.X + Dpi(9), playRect.Y + Dpi(7), Dpi(3), Dpi(12));
                g.FillRectangle(brush, playRect.X + Dpi(15), playRect.Y + Dpi(7), Dpi(3), Dpi(12));
            }
            else using (Brush brush = new SolidBrush(Color.White)) g.FillPolygon(brush,
                new Point[] { new Point(playRect.X + Dpi(10), playRect.Y + Dpi(7)), new Point(playRect.X + Dpi(10), playRect.Y + Dpi(19)),
                    new Point(playRect.X + Dpi(19), playRect.Y + Dpi(13)) });
            DrawPreviousNext(g, nextRect, true);
            if (titleRect.Width > 0) TextRenderer.DrawText(g, title, Theme.CaptionFont, titleRect,
                Theme.TextPrimary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            Draw.DrawIcon(g, UiIconKind.List, new Rectangle(collectionRect.X + Dpi(5), (Height - Dpi(20)) / 2, Dpi(20), Dpi(20)), Theme.IconColor);
            if (!seekRect.IsEmpty)
            {
                TextFormatFlags timeFlags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
                TextRenderer.DrawText(g, FormatTime(position), Theme.CaptionFont, elapsedRect, Theme.TextSecondary, timeFlags);
                TextRenderer.DrawText(g, FormatTime(duration), Theme.CaptionFont, durationRect, Theme.TextSecondary, timeFlags);
                DrawSlider(g, seekRect, duration > 0 ? position / duration : 0, Theme.Accent);
                DrawHover(g, muteRect, 5);
                DrawVolumeIcon(g);
                DrawSlider(g, volumeRect, muted ? 0 : volume / 100.0, Theme.TextSecondary);
            }
        }

        private static string FormatTime(double seconds)
        {
            if (seconds < 0 || double.IsNaN(seconds) || double.IsInfinity(seconds)) seconds = 0;
            int total = (int)seconds;
            return (total / 60).ToString("00") + ":" + (total % 60).ToString("00");
        }

        private void DrawSlider(Graphics g, Rectangle rect, double ratio, Color color)
        {
            int left = rect.Left + Dpi(4), right = rect.Right - Dpi(4), y = rect.Top + rect.Height / 2;
            int x = left + (int)((right - left) * Math.Max(0, Math.Min(1, ratio)));
            using (Pen pen = new Pen(Theme.ControlBorder, Dpi(3)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(pen, left, y, right, y);
            using (Pen pen = new Pen(color, Dpi(3)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(pen, left, y, x, y);
            using (Brush brush = new SolidBrush(color)) g.FillEllipse(brush, x - Dpi(3), y - Dpi(3), Dpi(6), Dpi(6));
        }

        private void DrawVolumeIcon(Graphics g)
        {
            int x = muteRect.Left + Dpi(3), y = Height / 2;
            using (Brush brush = new SolidBrush(Theme.IconColor)) g.FillPolygon(brush,
                new Point[] { new Point(x, y - Dpi(3)), new Point(x + Dpi(4), y - Dpi(3)),
                    new Point(x + Dpi(9), y - Dpi(7)), new Point(x + Dpi(9), y + Dpi(7)),
                    new Point(x + Dpi(4), y + Dpi(3)), new Point(x, y + Dpi(3)) });
            using (Pen pen = new Pen(muted || volume == 0 ? Theme.DeleteRed : Theme.IconColor, Dpi(1)))
            {
                if (muted || volume == 0)
                {
                    g.DrawLine(pen, x + Dpi(13), y - Dpi(4), x + Dpi(19), y + Dpi(4));
                    g.DrawLine(pen, x + Dpi(19), y - Dpi(4), x + Dpi(13), y + Dpi(4));
                }
                else g.DrawArc(pen, x + Dpi(9), y - Dpi(6), Dpi(10), Dpi(12), -65, 130);
            }
        }

        private void DrawHover(Graphics g, Rectangle rect, int id)
        {
            if (hover != id) return;
            using (Brush brush = new SolidBrush(Theme.RowHover)) Draw.FillRoundedRect(g, brush, rect, 7);
        }

        private void DrawPreviousNext(Graphics g, Rectangle rect, bool next)
        {
            int direction = next ? 1 : -1;
            int center = rect.X + rect.Width / 2, y = rect.Y + rect.Height / 2;
            using (Brush brush = new SolidBrush(Theme.IconColor))
            {
                g.FillPolygon(brush, new Point[] { new Point(center - direction * Dpi(4), y - Dpi(5)),
                    new Point(center - direction * Dpi(4), y + Dpi(5)), new Point(center + direction * Dpi(3), y) });
                g.FillRectangle(brush, next ? center - Dpi(6) : center + Dpi(4), y - Dpi(5), Dpi(2), Dpi(10));
            }
        }

        private int HitTest(Point point)
        {
            LayoutPlayer();
            if (collectionRect.Contains(point)) return 0;
            if (previousRect.Contains(point)) return 1;
            if (playRect.Contains(point)) return 2;
            if (nextRect.Contains(point)) return 3;
            if (seekRect.Contains(point)) return 4;
            if (muteRect.Contains(point)) return 5;
            if (volumeRect.Contains(point)) return 6;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (seeking) { UpdateSeek(e.X); return; }
            if (volumeDrag) { UpdateVolume(e.X); return; }
            int current = HitTest(e.Location);
            if (current == hover) return;
            hover = current;
            Cursor = current >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button == MouseButtons.Left && titleRect.Contains(e.Location)
                && session != null && session.HasTracks)
            {
                Action<int> handler = ExpandRequested;
                if (handler != null) handler(index);
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hover = -1;
            Cursor = Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            bool dragged = seeking || volumeDrag;
            if (seeking) { UpdateSeek(e.X); seeking = false; CommitSeek(); }
            if (volumeDrag) UpdateVolume(e.X);
            Capture = volumeDrag = false;
            if (dragged) { Invalidate(); return; }
            int action = HitTest(e.Location);
            if (action == 0) RequestCollection();
            else if (action == 1) PlayPrevious();
            else if (action == 2) TogglePlay();
            else if (action == 3) PlayNext();
            else if (action == 5)
            {
                if (session != null) session.ToggleMute();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            int action = HitTest(e.Location);
            if (action == 4 && duration > 0) { seeking = true; Capture = true; UpdateSeek(e.X); }
            else if (action == 6) { volumeDrag = true; Capture = true; UpdateVolume(e.X); }
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture) seeking = volumeDrag = false;
        }

        private void UpdateSeek(int x)
        {
            if (duration <= 0 || seekRect.IsEmpty) return;
            position = duration * SliderRatio(seekRect, x);
            Invalidate();
        }

        private void CommitSeek()
        {
            if (duration <= 0) return;
            if (session != null) session.Seek(position);
            Action<double> handler = SeekRequested;
            if (handler != null) handler(position);
        }

        private double SliderRatio(Rectangle rect, int x)
        {
            return Math.Max(0, Math.Min(1, (x - rect.Left - Dpi(4)) / (double)Math.Max(1, rect.Width - Dpi(8))));
        }

        private void UpdateVolume(int x)
        {
            if (volumeRect.IsEmpty) return;
            volume = (int)Math.Round(SliderRatio(volumeRect, x) * 100);
            if (volume > 0) muted = false;
            if (session != null) session.SetVolume(volume);
            Invalidate();
        }

        private void RequestCollection()
        {
            Action<Point> handler = CollectionRequested;
            if (handler != null) handler(PointToScreen(new Point(collectionRect.Right, Height + 4)));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) DetachSession();
            base.Dispose(disposing);
        }

    }
}

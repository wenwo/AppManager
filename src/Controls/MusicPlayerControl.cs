using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace AppManager.Controls
{
    public class MusicPlayerControl : Control
    {
        public event Action CloseRequested;
        public event Action PlaybackStarting;
        private MusicPlaybackSession session;
        private bool ownsSession;
        private List<string> paths = new List<string>();
        private int index, volume = 60, queueFirst, hover = -1, pressed = -1;
        private MusicTrackInfo track;
        private Bitmap scaledCover;
        private Image scaledCoverSource;
        private int scaledCoverWidth, scaledCoverHeight;
        private List<LyricLine> lyrics = new List<LyricLine>();
        private bool seeking, volumeDrag;
        private bool playing, queueVisible, muted;
        private string failure = "";
        private double duration, position;
        private float titleScroll;
        private int metadataGeneration;
        private PlaybackMode mode = PlaybackMode.Sequential;
        private Rectangle mainCard, playerCard, backRect, coverRect;
        private Rectangle lyricTabRect, queueTabRect, contentRect, seekRect;
        private Rectangle songRect, modeRect, previousRect, playRect, nextRect, muteRect, volumeRect;

        public MusicPlayerControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.Selectable, true);
            BackColor = Theme.Background;
            Visible = false;
            TabStop = true;
        }

        public string CurrentPath { get { return session == null ? null : session.CurrentPath; } }
        public MusicPlaybackSession Session { get { return EnsureSession(); } }

        public void ToggleFromMiniPlayer() { Session.TogglePause(); }

        public double Position { get { return session == null ? 0 : session.Position; } }
        public double Duration { get { return session == null ? 0 : session.Duration; } }
        public int Volume { get { return session == null ? volume : session.Volume; } }
        public bool Muted { get { return session == null ? muted : session.Muted; } }
        public bool HasPlaybackSession { get { return session != null && session.HasSession; } }

        public void SeekFromMiniPlayer(double seconds) { Session.Seek(seconds); }
        public void SetVolumeFromMiniPlayer(int value) { Session.SetVolume(value); }
        public void ToggleMuteFromMiniPlayer() { Session.ToggleMute(); }

        public void StepFromMiniPlayer(int step)
        {
            if (step > 0) Session.Next();
            else Session.Previous();
        }

        /// <summary>文件改名或移动后更新播放列表里的路径；不打断正在播放的曲目。</summary>
        public void RebasePath(string oldPath, string newPath)
        {
            if (session != null) session.RebasePath(oldPath, newPath);
        }

        internal void AttachSession(MusicPlaybackSession value)
        {
            if (object.ReferenceEquals(session, value)) { SyncFromSession(true); return; }
            DetachSession();
            session = value;
            ownsSession = false;
            SubscribeSession();
            SyncFromSession(true);
        }

        private MusicPlaybackSession EnsureSession()
        {
            if (session != null) return session;
            session = new MusicPlaybackSession();
            ownsSession = true;
            SubscribeSession();
            SyncFromSession(true);
            return session;
        }

        private void SubscribeSession()
        {
            if (session == null) return;
            session.StateChanged += Session_StateChanged;
            session.TrackChanged += Session_TrackChanged;
            session.PlaybackStarting += Session_PlaybackStarting;
        }

        private void DetachSession()
        {
            if (session == null) return;
            session.StateChanged -= Session_StateChanged;
            session.TrackChanged -= Session_TrackChanged;
            session.PlaybackStarting -= Session_PlaybackStarting;
            if (ownsSession) session.Dispose();
            session = null;
            ownsSession = false;
        }

        private void Session_StateChanged()
        {
            string oldFailure = failure;
            bool oldPlaying = playing;
            SyncFromSession(false);
            if (playing) titleScroll += 3F;
            if (!string.Equals(oldFailure, failure, StringComparison.Ordinal) || oldPlaying != playing)
            {
                Invalidate();
                return;
            }
            Rectangle dirty = Rectangle.Union(playerCard, contentRect);
            dirty.Inflate(4, 4);
            Invalidate(dirty);
        }

        private void Session_TrackChanged()
        {
            SyncFromSession(true);
            Invalidate();
        }

        private void Session_PlaybackStarting()
        {
            Action handler = PlaybackStarting;
            if (handler != null) handler();
        }

        private void SyncFromSession(bool trackChanged)
        {
            if (session == null) return;
            bool changed = trackChanged || paths.Count != session.PathCount || index != session.Index;
            if (changed)
            {
                paths = session.PathsSnapshot();
                index = session.Index;
                titleScroll = 0;
                if (trackChanged) queueFirst = Math.Max(0, index - 4);
                else EnsureQueueCurrentVisible();
                ClearTrack();
                string path = session.CurrentPath;
                if (!string.IsNullOrEmpty(path)) LoadMetadataAsync(path);
            }
            if (!seeking) position = session.Position;
            duration = session.Duration;
            volume = session.Volume;
            muted = session.Muted;
            playing = session.IsPlaying;
            failure = session.Failure;
            mode = session.Mode;
        }

        public void Open(List<string> audioPaths, int startIndex)
        {
            Open(audioPaths, startIndex, 0, false);
        }

        /// <summary>打开一份新列表；共享会话下仅切换会话内容，不重建内核。</summary>
        public void Open(List<string> audioPaths, int startIndex, double startPosition, bool startPaused)
        {
            List<string> songs = audioPaths ?? new List<string>();
            if (songs.Count == 0) { RequestClose(); return; }
            queueVisible = false;
            queueFirst = Math.Max(0, Math.Min(startIndex, songs.Count - 1) - 4);
            Visible = true;
            Focus();
            EnsureSession().Open(songs, startIndex, startPosition, startPaused);
        }

        private void LoadMetadataAsync(string path)
        {
            int generation = metadataGeneration;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                MusicTrackInfo loaded = null;
                List<LyricLine> loadedLyrics = null;
                try
                {
                    loaded = MusicMetadata.Read(path);
                    loadedLyrics = MusicMetadata.ParseLyrics(loaded.Lyrics);
                }
                catch
                {
                    if (loaded != null) loaded.Dispose();
                    return;
                }
                try
                {
                    if (IsDisposed)
                    {
                        loaded.Dispose();
                        return;
                    }
                    BeginInvoke((Action)delegate
                    {
                        if (IsDisposed || generation != metadataGeneration
                            || !string.Equals(CurrentPath, path, StringComparison.OrdinalIgnoreCase))
                        {
                            loaded.Dispose();
                            return;
                        }
                        if (track != null) track.Dispose();
                        ClearScaledCover();
                        track = loaded;
                        lyrics = loadedLyrics;
                        Invalidate();
                    });
                }
                catch { loaded.Dispose(); }
            });
        }

        private void TogglePlay() { Session.TogglePause(); }

        private void PlayNext() { Session.Next(); }

        private void PlayPrevious() { Session.Previous(); }

        private void SeekTo(double value) { Session.Seek(value); }

        private void SetVolume(int value) { Session.SetVolume(value); }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Background);
            LayoutPlayer();
            DrawHeader(g);
            using (Brush shadow = new SolidBrush(Color.FromArgb(10, 30, 45, 65)))
            {
                Draw.FillRoundedRect(g, shadow, new Rectangle(mainCard.X, mainCard.Y + 2, mainCard.Width, mainCard.Height), 16);
                Draw.FillRoundedRect(g, shadow, new Rectangle(playerCard.X, playerCard.Y + 2, playerCard.Width, playerCard.Height), 16);
            }
            using (Brush card = new SolidBrush(Theme.Card))
            {
                Draw.FillRoundedRect(g, card, mainCard, 16);
                Draw.FillRoundedRect(g, card, playerCard, 16);
            }
            DrawCoverAndInfo(g);
            if (queueVisible) DrawQueue(g); else DrawLyrics(g);
            DrawTransport(g);
        }

        private void LayoutPlayer()
        {
            int width = Math.Max(320, ClientSize.Width);
            int height = Math.Max(300, ClientSize.Height);
            backRect = new Rectangle(16, 8, 86, 34);
            playerCard = new Rectangle(16, height - 118, width - 32, 102);
            mainCard = new Rectangle(16, 48, width - 32, Math.Max(150, playerCard.Top - 60));
            int leftWidth = Math.Max(230, (int)(mainCard.Width * 0.40));
            contentRect = new Rectangle(mainCard.X + leftWidth, mainCard.Y + 56,
                Math.Max(160, mainCard.Width - leftWidth - 20), Math.Max(80, mainCard.Height - 72));
            lyricTabRect = new Rectangle(contentRect.Right - 166, mainCard.Y + 14, 76, 30);
            queueTabRect = new Rectangle(contentRect.Right - 86, mainCard.Y + 14, 86, 30);
            int coverSize = Math.Max(96, Math.Min(300, Math.Min(leftWidth - 64, mainCard.Height - 112)));
            coverRect = new Rectangle(mainCard.X + (leftWidth - coverSize) / 2, mainCard.Y + 24, coverSize, coverSize);
            seekRect = new Rectangle(playerCard.X + 82, playerCard.Y + 10, playerCard.Width - 164, 18);
            int center = playerCard.X + (int)(playerCard.Width * 0.46);
            playRect = new Rectangle(center - 23, playerCard.Y + 44, 46, 46);
            previousRect = new Rectangle(center - 91, playerCard.Y + 49, 40, 36);
            nextRect = new Rectangle(center + 51, playerCard.Y + 49, 40, 36);
            songRect = new Rectangle(playerCard.X + 22, playerCard.Y + 49, (int)(playerCard.Width * 0.26), 36);
            volumeRect = new Rectangle(playerCard.Right - 102, playerCard.Y + 59, 74, 16);
            muteRect = new Rectangle(playerCard.Right - 140, playerCard.Y + 49, 32, 36);
            int modeWidth = playerCard.Width < 620 ? 42 : 104;
            modeRect = new Rectangle(muteRect.Left - modeWidth - 10, playerCard.Y + 49, modeWidth, 36);
        }

        private void DrawHeader(Graphics g)
        {
            DrawButtonBackground(g, backRect, 3, 8);
            Draw.DrawIcon(g, UiIconKind.Back, new Rectangle(backRect.X + 8, backRect.Y + 7, 20, 20), Theme.IconColor);
            TextRenderer.DrawText(g, "返回", Theme.ButtonFont, new Rectangle(backRect.X + 30, backRect.Y, 48, backRect.Height),
                Theme.TextPrimary, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            TextRenderer.DrawText(g, "正在播放", Theme.SectionTitleFont, new Rectangle(116, 8, 180, 34),
                Theme.TextPrimary, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            TextRenderer.DrawText(g, (Math.Max(0, index) + 1) + " / " + paths.Count, Theme.CaptionFont,
                new Rectangle(Width - 116, 8, 100, 34), Theme.TextSecondary,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
        }

        private void DrawCoverAndInfo(Graphics g)
        {
            DrawCover(g);
            int left = mainCard.X + 24;
            int right = contentRect.Left - 24;
            int top = coverRect.Bottom + 14;
            string title = track == null ? (session == null ? "" : session.CurrentTitle) : track.Title;
            string artist = track == null ? "未知艺术家" : track.Artist + "  ·  " + track.Album;
            TextRenderer.DrawText(g, title, Theme.FontOf(13F, FontStyle.Bold), new Rectangle(left, top, right - left, 28),
                Theme.TextPrimary, TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, artist, Theme.CaptionFont, new Rectangle(left, top + 28, right - left, 22),
                Theme.TextSecondary, TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
            if (failure != "") TextRenderer.DrawText(g, failure, Theme.CaptionFont,
                new Rectangle(left, top + 50, right - left, 24), Theme.DeleteRed, TextFormatFlags.HorizontalCenter);
        }

        private void DrawCover(Graphics g)
        {
            GraphicsState state = g.Save();
            using (GraphicsPath path = Draw.RoundedPath(coverRect, 18))
            {
                g.SetClip(path);
                if (track != null && track.Cover != null)
                {
                    EnsureScaledCover();
                    if (scaledCover != null) g.DrawImage(scaledCover, coverRect);
                }
                else
                {
                    using (LinearGradientBrush brush = new LinearGradientBrush(coverRect,
                        Color.FromArgb(37, 48, 73), Color.FromArgb(78, 106, 148), 45F)) g.FillRectangle(brush, coverRect);
                    int disk = (int)(coverRect.Width * 0.68);
                    Rectangle diskRect = new Rectangle(coverRect.X + (coverRect.Width - disk) / 2,
                        coverRect.Y + (coverRect.Height - disk) / 2, disk, disk);
                    using (Brush brush = new SolidBrush(Color.FromArgb(110, 8, 13, 24))) g.FillEllipse(brush, diskRect);
                    using (Pen pen = new Pen(Color.FromArgb(80, 255, 255, 255), 1))
                    {
                        g.DrawEllipse(pen, diskRect);
                        g.DrawEllipse(pen, Rectangle.Inflate(diskRect, -disk / 5, -disk / 5));
                    }
                    using (Brush brush = new SolidBrush(Theme.Accent)) g.FillEllipse(brush,
                        new Rectangle(diskRect.X + disk / 2 - 8, diskRect.Y + disk / 2 - 8, 16, 16));
                    DrawNote(g, new Rectangle(coverRect.X + coverRect.Width / 2 - 17,
                        coverRect.Y + coverRect.Height / 2 - 32, 40, 48), Color.White);
                }
            }
            g.Restore(state);
            using (Pen pen = new Pen(Color.FromArgb(25, 0, 0, 0))) Draw.DrawRoundedRect(g, pen, coverRect, 18);
        }

        /// <summary>按当前封面区域缓存高质量缩放结果，避免 150ms 轮询时反复重采样大图。</summary>
        private void EnsureScaledCover()
        {
            Image source = track == null ? null : track.Cover;
            if (source == null)
            {
                ClearScaledCover();
                return;
            }
            if (scaledCover != null && scaledCoverSource == source
                && scaledCoverWidth == coverRect.Width && scaledCoverHeight == coverRect.Height) return;
            ClearScaledCover();
            if (coverRect.Width <= 0 || coverRect.Height <= 0) return;
            try
            {
                Bitmap result = new Bitmap(coverRect.Width, coverRect.Height, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(result))
                {
                    g.Clear(Color.Transparent);
                    float scale = Math.Max(coverRect.Width / (float)source.Width,
                        coverRect.Height / (float)source.Height);
                    int width = (int)(source.Width * scale + 0.5F);
                    int height = (int)(source.Height * scale + 0.5F);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(source, new Rectangle((coverRect.Width - width) / 2,
                        (coverRect.Height - height) / 2, width, height));
                }
                scaledCover = result;
                scaledCoverSource = source;
                scaledCoverWidth = coverRect.Width;
                scaledCoverHeight = coverRect.Height;
            }
            catch { ClearScaledCover(); }
        }

        private void ClearScaledCover()
        {
            if (scaledCover != null)
            {
                scaledCover.Dispose();
                scaledCover = null;
            }
            scaledCoverSource = null;
            scaledCoverWidth = scaledCoverHeight = 0;
        }

        private void DrawTabs(Graphics g)
        {
            Rectangle strip = new Rectangle(lyricTabRect.X, lyricTabRect.Y,
                queueTabRect.Right - lyricTabRect.X, lyricTabRect.Height);
            using (Brush brush = new SolidBrush(Theme.SearchBg))
                Draw.FillRoundedRect(g, brush, strip, 8);
            DrawTab(g, lyricTabRect, "歌词", !queueVisible);
            DrawTab(g, queueTabRect, "播放列表 " + paths.Count, queueVisible);
        }

        private static void DrawTab(Graphics g, Rectangle rect, string text, bool selected)
        {
            if (selected) using (Brush brush = new SolidBrush(Color.White))
                Draw.FillRoundedRect(g, brush, Rectangle.Inflate(rect, -2, -2), 6);
            TextRenderer.DrawText(g, text, Theme.CaptionFont, rect, selected ? Theme.AccentText : Theme.TextSecondary,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        private void DrawLyrics(Graphics g)
        {
            DrawTabs(g);
            Rectangle clip = contentRect;
            GraphicsState state = g.Save();
            g.SetClip(clip);
            if (lyrics.Count == 0)
            {
                string message = track != null && !string.IsNullOrWhiteSpace(track.Lyrics)
                    ? track.Lyrics : "暂无歌词\n\n将同名 .lrc 文件放在音乐旁\n即可显示同步歌词";
                TextRenderer.DrawText(g, message, Theme.FontOf(10.5F), clip, Theme.TextTertiary,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                g.Restore(state);
                return;
            }
            int active = 0;
            for (int i = 0; i < lyrics.Count; i++) if (lyrics[i].Time <= position + 0.08) active = i; else break;
            int lineHeight = 42;
            int centerY = clip.Y + clip.Height / 2;
            int first = Math.Max(0, active - clip.Height / lineHeight / 2 - 1);
            int last = Math.Min(lyrics.Count - 1, active + clip.Height / lineHeight / 2 + 1);
            for (int i = first; i <= last; i++)
            {
                Rectangle row = new Rectangle(clip.X + 8, centerY + (i - active) * lineHeight - 17, clip.Width - 16, 34);
                string text = lyrics[i].Text == "" ? "♪" : lyrics[i].Text;
                TextRenderer.DrawText(g, text, i == active ? Theme.FontOf(12F, FontStyle.Bold) : Theme.FontOf(10F), row,
                    i == active ? Theme.AccentText : Theme.TextTertiary,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
            g.Restore(state);
        }

        private void DrawQueue(Graphics g)
        {
            DrawTabs(g);
            int rowHeight = 42;
            int visible = Math.Max(1, contentRect.Height / rowHeight);
            queueFirst = Math.Max(0, Math.Min(queueFirst, Math.Max(0, paths.Count - visible)));
            GraphicsState state = g.Save();
            g.SetClip(contentRect);
            for (int row = 0; row < visible && queueFirst + row < paths.Count; row++)
            {
                int item = queueFirst + row;
                Rectangle rect = new Rectangle(contentRect.X + 4, contentRect.Y + row * rowHeight, contentRect.Width - 8, rowHeight - 4);
                if (item == index) using (Brush brush = new SolidBrush(Theme.SelectionBg)) Draw.FillRoundedRect(g, brush, rect, 8);
                else if (hover == 100 + item) using (Brush brush = new SolidBrush(Theme.CardHover)) Draw.FillRoundedRect(g, brush, rect, 8);
                Rectangle numberRect = new Rectangle(rect.X + 4, rect.Y, 30, rect.Height);
                if (item == index && playing) DrawEqualizer(g, numberRect);
                else TextRenderer.DrawText(g, (item + 1).ToString("00"), Theme.CaptionFont, numberRect,
                    item == index ? Theme.AccentText : Theme.TextTertiary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                string name = item == index && track != null ? track.Title : Path.GetFileNameWithoutExtension(paths[item]);
                TextRenderer.DrawText(g, name, item == index ? Theme.ButtonFont : Theme.BodyFont,
                    new Rectangle(rect.X + 38, rect.Y, rect.Width - 44, rect.Height),
                    item == index ? Theme.AccentText : Theme.TextPrimary,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            }
            g.Restore(state);
        }

        private void DrawTransport(Graphics g)
        {
            string elapsed = FormatTime(position);
            string total = FormatTime(duration);
            TextRenderer.DrawText(g, elapsed, Theme.CaptionFont, new Rectangle(playerCard.X + 18, playerCard.Y + 6, 58, 24),
                Theme.TextSecondary, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
            TextRenderer.DrawText(g, total, Theme.CaptionFont, new Rectangle(playerCard.Right - 76, playerCard.Y + 6, 58, 24),
                Theme.TextSecondary, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            DrawSlider(g, seekRect, duration > 0 ? position / duration : 0, Theme.Accent);
            DrawCurrentSong(g);
            DrawButtonBackground(g, modeRect, 0, 8);
            int modeIconX = modeRect.Width < 60 ? modeRect.X + (modeRect.Width - 20) / 2 : modeRect.X + 5;
            DrawMode(g, new Rectangle(modeIconX, modeRect.Y + 8, 20, 20));
            if (modeRect.Width >= 60)
                TextRenderer.DrawText(g, ModeText(), Theme.CaptionFont, new Rectangle(modeRect.X + 27, modeRect.Y, 74, modeRect.Height),
                    Theme.TextSecondary, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            DrawButtonBackground(g, previousRect, 1, 8);
            DrawPreviousNext(g, previousRect, false);
            using (Brush brush = new SolidBrush(hover == 2 ? Color.FromArgb(9, 101, 224) : Theme.Accent)) g.FillEllipse(brush, playRect);
            if (playing)
            {
                using (Brush brush = new SolidBrush(Color.White))
                {
                    g.FillRectangle(brush, playRect.X + 17, playRect.Y + 14, 4, 18);
                    g.FillRectangle(brush, playRect.X + 26, playRect.Y + 14, 4, 18);
                }
            }
            else using (Brush brush = new SolidBrush(Color.White))
                g.FillPolygon(brush, new Point[] { new Point(playRect.X + 18, playRect.Y + 13),
                    new Point(playRect.X + 18, playRect.Y + 33), new Point(playRect.X + 33, playRect.Y + 23) });
            DrawButtonBackground(g, nextRect, 4, 8);
            DrawPreviousNext(g, nextRect, true);
            DrawButtonBackground(g, muteRect, 5, 8);
            DrawVolumeIcon(g, muteRect);
            DrawSlider(g, volumeRect, muted ? 0 : volume / 100.0, Theme.TextSecondary);
        }

        private void DrawCurrentSong(Graphics g)
        {
            string title = track == null ? (session == null ? "" : session.CurrentTitle) : track.Title;
            Font font = Theme.BodyFont;
            Size measured = TextRenderer.MeasureText(g, title, font, new Size(int.MaxValue, songRect.Height),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            if (measured.Width <= songRect.Width)
            {
                TextRenderer.DrawText(g, title, font, songRect, Theme.TextPrimary,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                return;
            }
            float cycle = measured.Width + 42F;
            float offset = titleScroll % cycle;
            GraphicsState state = g.Save();
            g.SetClip(songRect);
            for (int copy = 0; copy < 2; copy++)
            {
                int x = songRect.X + (int)(copy * cycle - offset);
                TextRenderer.DrawText(g, title, font, new Rectangle(x, songRect.Y, measured.Width + 2, songRect.Height),
                    Theme.TextPrimary, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            g.Restore(state);
        }

        private static string FormatTime(double seconds)
        {
            if (seconds < 0 || double.IsNaN(seconds) || double.IsInfinity(seconds)) seconds = 0;
            int total = (int)seconds;
            return (total / 60).ToString("00") + ":" + (total % 60).ToString("00");
        }

        private void DrawButtonBackground(Graphics g, Rectangle rect, int id, int radius)
        {
            if (hover != id && pressed != id) return;
            using (Brush brush = new SolidBrush(pressed == id ? Theme.SelectionBg : Theme.CardHover))
                Draw.FillRoundedRect(g, brush, rect, radius);
        }

        private static void DrawSlider(Graphics g, Rectangle rect, double ratio, Color color)
        {
            ratio = Math.Max(0, Math.Min(1, ratio));
            int y = rect.Y + rect.Height / 2;
            using (Pen pen = new Pen(Theme.SegmentBg, 4) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(pen, rect.X + 2, y, rect.Right - 2, y);
            int x = rect.X + 2 + (int)((rect.Width - 4) * ratio);
            using (Pen pen = new Pen(color, 4) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(pen, rect.X + 2, y, x, y);
            using (Brush brush = new SolidBrush(Color.White)) g.FillEllipse(brush, x - 6, y - 6, 12, 12);
            using (Pen pen = new Pen(color, 2)) g.DrawEllipse(pen, x - 5, y - 5, 10, 10);
        }

        private static void DrawPreviousNext(Graphics g, Rectangle rect, bool next)
        {
            int direction = next ? 1 : -1;
            int center = rect.X + rect.Width / 2;
            int y = rect.Y + rect.Height / 2;
            using (Brush brush = new SolidBrush(Theme.IconColor))
            {
                Point[] triangle = { new Point(center - direction * 6, y - 7), new Point(center - direction * 6, y + 7), new Point(center + direction * 4, y) };
                g.FillPolygon(brush, triangle);
                g.FillRectangle(brush, next ? center - 8 : center + 6, y - 7, 2, 14);
            }
        }

        private void DrawMode(Graphics g, Rectangle rect)
        {
            using (Pen pen = new Pen(Theme.IconColor, 1.7F) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                if (mode == PlaybackMode.Shuffle)
                {
                    g.DrawLines(pen, new Point[] { new Point(rect.X + 2, rect.Y + 5), new Point(rect.X + 6, rect.Y + 5),
                        new Point(rect.X + 14, rect.Y + 15), new Point(rect.Right - 4, rect.Y + 15) });
                    g.DrawLines(pen, new Point[] { new Point(rect.X + 2, rect.Bottom - 5), new Point(rect.X + 6, rect.Bottom - 5),
                        new Point(rect.X + 14, rect.Top + 5), new Point(rect.Right - 4, rect.Top + 5) });
                }
                else
                {
                    g.DrawArc(pen, rect.X + 2, rect.Y + 2, 16, 14, 35, 270);
                    if (mode == PlaybackMode.RepeatOne)
                    {
                        using (Brush brush = new SolidBrush(Theme.AccentText))
                        using (StringFormat format = new StringFormat())
                        {
                            format.Alignment = StringAlignment.Center;
                            format.LineAlignment = StringAlignment.Center;
                            g.DrawString("1", Theme.FontPx(9, FontStyle.Bold), brush,
                                new RectangleF(rect.X + 5, rect.Y + 2, 10, 14), format);
                        }
                    }
                }
            }
        }

        private static void DrawNote(Graphics g, Rectangle rect, Color color)
        {
            using (Pen pen = new Pen(color, 3) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(pen, rect.X + 12, rect.Y + 8, rect.X + 12, rect.Bottom - 12);
                g.DrawLine(pen, rect.X + 12, rect.Y + 8, rect.Right - 6, rect.Y + 3);
                g.DrawLine(pen, rect.Right - 6, rect.Y + 3, rect.Right - 6, rect.Bottom - 18);
            }
            using (Brush brush = new SolidBrush(color))
            {
                g.FillEllipse(brush, rect.X + 2, rect.Bottom - 18, 14, 11);
                g.FillEllipse(brush, rect.Right - 16, rect.Bottom - 24, 14, 11);
            }
        }

        private void DrawVolumeIcon(Graphics g, Rectangle rect)
        {
            int x = rect.X + 7, y = rect.Y + rect.Height / 2;
            using (Brush brush = new SolidBrush(Theme.IconColor)) g.FillPolygon(brush,
                new Point[] { new Point(x, y - 4), new Point(x + 5, y - 4), new Point(x + 11, y - 9),
                    new Point(x + 11, y + 9), new Point(x + 5, y + 4), new Point(x, y + 4) });
            if (muted || volume == 0) using (Pen pen = new Pen(Theme.DeleteRed, 1.8F))
            {
                g.DrawLine(pen, x + 15, y - 5, x + 22, y + 5);
                g.DrawLine(pen, x + 22, y - 5, x + 15, y + 5);
            }
        }

        private static void DrawEqualizer(Graphics g, Rectangle rect)
        {
            using (Brush brush = new SolidBrush(Theme.Accent))
                for (int i = 0; i < 3; i++) g.FillRectangle(brush, rect.X + 8 + i * 5, rect.Y + 13 + (i % 2) * 4, 3, 13 - (i % 2) * 4);
        }

        private string ModeText()
        {
            if (mode == PlaybackMode.RepeatAll) return "列表循环";
            if (mode == PlaybackMode.RepeatOne) return "单曲循环";
            if (mode == PlaybackMode.Shuffle) return "随机播放";
            return "顺序播放";
        }

        private int HitTest(Point point)
        {
            if (backRect.Contains(point)) return 3;
            if (previousRect.Contains(point)) return 1;
            if (playRect.Contains(point)) return 2;
            if (nextRect.Contains(point)) return 4;
            if (muteRect.Contains(point)) return 5;
            if (modeRect.Contains(point)) return 0;
            if (lyricTabRect.Contains(point)) return 6;
            if (queueTabRect.Contains(point)) return 7;
            if (seekRect.Contains(point)) return 8;
            if (volumeRect.Contains(point)) return 9;
            if (queueVisible && contentRect.Contains(point))
            {
                int item = queueFirst + (point.Y - contentRect.Y) / 42;
                if (item >= 0 && item < paths.Count) return 100 + item;
            }
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (seeking) { UpdateSeek(e.X); return; }
            if (volumeDrag) { UpdateVolume(e.X); return; }
            int current = HitTest(e.Location);
            if (current != hover) { hover = current; Invalidate(); }
            Cursor = Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hover = -1;
            Cursor = Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            pressed = HitTest(e.Location);
            if (pressed == 8) { seeking = true; Capture = true; UpdateSeek(e.X); }
            else if (pressed == 9) { volumeDrag = true; Capture = true; UpdateVolume(e.X); }
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            int action = pressed;
            pressed = -1;
            if (seeking) { seeking = false; UpdateSeek(e.X); SeekTo(position); Invalidate(); return; }
            if (volumeDrag) { volumeDrag = false; UpdateVolume(e.X); return; }
            if (action < 0 || action != HitTest(e.Location)) { Invalidate(); return; }
            if (action == 0) { Session.CycleMode(); Invalidate(); }
            else if (action == 1) PlayPrevious();
            else if (action == 2) TogglePlay();
            else if (action == 3) RequestClose();
            else if (action == 4) PlayNext();
            else if (action == 5) ToggleMuteFromMiniPlayer();
            else if (action == 6 || action == 7) { queueVisible = action == 7; EnsureQueueCurrentVisible(); Invalidate(); }
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture) seeking = volumeDrag = false;
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            int action = HitTest(e.Location);
            if (action < 100) return;
            Session.PlayAt(action - 100);
        }

        private void UpdateSeek(int x)
        {
            if (duration <= 0) return;
            double ratio = Math.Max(0, Math.Min(1, (x - seekRect.X - 2) / (double)Math.Max(1, seekRect.Width - 4)));
            position = ratio * duration;
            Invalidate();
        }

        private void UpdateVolume(int x)
        {
            SetVolume((int)Math.Round(Math.Max(0, Math.Min(1,
                (x - volumeRect.X - 2) / (double)Math.Max(1, volumeRect.Width - 4))) * 100));
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (queueVisible && contentRect.Contains(e.Location))
            {
                int visible = Math.Max(1, contentRect.Height / 42);
                queueFirst = Math.Max(0, Math.Min(Math.Max(0, paths.Count - visible), queueFirst - Math.Sign(e.Delta) * 3));
            }
            else SetVolume(volume + Math.Sign(e.Delta) * 5);
            Invalidate();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (!Visible) return base.ProcessCmdKey(ref msg, keyData);
            if (keyData == Keys.Escape) { RequestClose(); return true; }
            if (keyData == Keys.Space) { TogglePlay(); return true; }
            if (keyData == Keys.Left) { SeekTo(position - 5); return true; }
            if (keyData == Keys.Right) { SeekTo(position + 5); return true; }
            if (keyData == Keys.Up) { SetVolume(volume + 5); return true; }
            if (keyData == Keys.Down) { SetVolume(volume - 5); return true; }
            if (keyData == (Keys.Control | Keys.Left)) { PlayPrevious(); return true; }
            if (keyData == (Keys.Control | Keys.Right)) { PlayNext(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        public void PausePlayback() { Session.Pause(); }

        public void StopPlayback()
        {
            if (session != null) session.Stop();
            playing = false;
            position = duration = 0;
            seeking = volumeDrag = false;
            paths = new List<string>();
            index = -1;
            failure = "";
            ClearTrack();
            Invalidate();
        }

        private void ClearTrack()
        {
            metadataGeneration++;
            ClearScaledCover();
            if (track != null) { track.Dispose(); track = null; }
            lyrics = new List<LyricLine>();
        }

        private void EnsureQueueCurrentVisible()
        {
            int visible = Math.Max(1, contentRect.Height / 42);
            if (index < queueFirst) queueFirst = index;
            else if (index >= queueFirst + visible) queueFirst = index - visible + 1;
        }

        private void RequestClose()
        {
            Action handler = CloseRequested;
            if (handler != null) handler();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DetachSession();
            }
            base.Dispose(disposing);
        }

    }
}

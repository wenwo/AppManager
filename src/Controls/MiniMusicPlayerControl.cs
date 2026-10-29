using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace AppManager.Controls
{
    public class MiniMusicPlayerControl : Control
    {
        public event Action<Point> CollectionRequested;
        public event Action PlaybackStarting;
        public event Action DefaultPlayRequested;
        public event Action<int> ExpandRequested;
        public event Action ToggleRequested;
        public event Action<int> TrackStepRequested;
        public event Action<double> SeekRequested;
        public event Action<int> VolumeRequested;
        public event Action MuteRequested;

        private readonly Timer pollTimer = new Timer { Interval = 250 };
        private List<string> paths = new List<string>();
        private MiniMpvEngine engine;
        private string title = "尚未播放";
        private string collectionKey;
        private int index;
        private bool playing;
        private bool externalPlayerActive;
        private double position, duration;
        private int volume = 60;
        private bool muted, seeking, volumeDrag;
        private int hover = -1;
        private Rectangle collectionRect, previousRect, playRect, nextRect, titleRect;
        private Rectangle seekRect, elapsedRect, durationRect, muteRect, volumeRect;

        public string CollectionKey { get { return collectionKey; } }
        public int CurrentIndex { get { return index; } }

        public MiniMusicPlayerControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            pollTimer.Tick += Poll;
        }

        public void LoadPlaylist(List<string> songs, string selectedName, string selectedKey, int startIndex)
        {
            Stop();
            paths = songs ?? new List<string>();
            collectionKey = selectedKey ?? "";
            index = paths.Count == 0 ? 0 : Math.Max(0, Math.Min(startIndex, paths.Count - 1));
            title = paths.Count == 0 ? "集合中暂无音乐" : Path.GetFileNameWithoutExtension(paths[0]);
            if (paths.Count > 0) LoadCurrent();
            Invalidate();
        }

        public void Stop()
        {
            pollTimer.Stop();
            playing = false;
            externalPlayerActive = false;
            position = duration = 0;
            Capture = seeking = volumeDrag = false;
            if (engine != null)
            {
                try { engine.Dispose(); } catch { }
                engine = null;
            }
            Invalidate();
        }

        private void LoadCurrent()
        {
            if (index < 0 || index >= paths.Count) return;
            Action starting = PlaybackStarting;
            if (starting != null) starting();
            position = duration = 0;
            try
            {
                if (engine == null) engine = new MiniMpvEngine();
                engine.SetVolume(volume);
                engine.SetMute(muted);
                title = Path.GetFileNameWithoutExtension(paths[index]);
                engine.LoadFile(paths[index]);
                engine.SetPause(false);
                playing = true;
                pollTimer.Start();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("迷你音乐播放失败: " + ex.Message);
                title = "播放组件不可用";
                playing = false;
            }
            Invalidate();
        }

        private void Poll(object sender, EventArgs e)
        {
            if (engine == null) return;
            bool ended, error;
            engine.DrainEvents(out ended, out error);
            if (error)
            {
                playing = false;
                title = "无法播放此音乐";
                pollTimer.Stop();
            }
            else if (ended) { PlayNext(); return; }
            double value;
            if (engine.TryGetDouble("duration", out value) && value >= 0) duration = value;
            if (!seeking && engine.TryGetDouble("time-pos", out value) && value >= 0) position = value;
            Invalidate();
        }

        public void SyncExternalState(string songPath, bool isPlaying,
            double currentPosition, double totalDuration, int currentVolume, bool isMuted)
        {
            externalPlayerActive = true;
            int current = paths.FindIndex(delegate(string path)
            {
                return string.Equals(path, songPath, StringComparison.OrdinalIgnoreCase);
            });
            if (current >= 0) index = current;
            title = string.IsNullOrEmpty(songPath) ? title : Path.GetFileNameWithoutExtension(songPath);
            playing = isPlaying;
            if (!seeking) position = currentPosition;
            duration = totalDuration;
            volume = currentVolume;
            muted = isMuted;
            Invalidate();
        }

        public void ClearExternalState()
        {
            externalPlayerActive = false;
            playing = false;
            position = duration = 0;
            Invalidate();
        }

        private void TogglePlay()
        {
            if (externalPlayerActive)
            {
                Action toggle = ToggleRequested;
                if (toggle != null) toggle();
                return;
            }
            if (paths.Count == 0)
            {
                Action defaultPlay = DefaultPlayRequested;
                if (defaultPlay != null) defaultPlay();
                else RequestCollection();
                return;
            }
            if (engine == null) { LoadCurrent(); return; }
            playing = !playing;
            engine.SetPause(!playing);
            if (playing)
            {
                Action starting = PlaybackStarting;
                if (starting != null) starting();
            }
            Invalidate();
        }

        private void PlayNext()
        {
            if (RequestExternalTrackStep(1)) return;
            if (paths.Count == 0) return;
            index = (index + 1) % paths.Count;
            LoadCurrent();
        }

        private void PlayPrevious()
        {
            if (RequestExternalTrackStep(-1)) return;
            if (paths.Count == 0) return;
            index = (index - 1 + paths.Count) % paths.Count;
            LoadCurrent();
        }

        private bool RequestExternalTrackStep(int step)
        {
            if (!externalPlayerActive) return false;
            Action<int> handler = TrackStepRequested;
            if (handler != null) handler(step);
            return true;
        }

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
                && index >= 0 && index < paths.Count)
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
            if (seeking) UpdateSeek(e.X);
            if (volumeDrag) UpdateVolume(e.X);
            Capture = seeking = volumeDrag = false;
            if (dragged) return;
            int action = HitTest(e.Location);
            if (action == 0) RequestCollection();
            else if (action == 1) PlayPrevious();
            else if (action == 2) TogglePlay();
            else if (action == 3) PlayNext();
            else if (action == 5)
            {
                muted = !muted;
                if (externalPlayerActive) { if (MuteRequested != null) MuteRequested(); }
                else if (engine != null) engine.SetMute(muted);
                Invalidate();
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
            if (externalPlayerActive) { if (SeekRequested != null) SeekRequested(position); }
            else if (engine != null) engine.Seek(position);
            Invalidate();
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
            if (externalPlayerActive) { if (VolumeRequested != null) VolumeRequested(volume); }
            else if (engine != null) { engine.SetVolume(volume); engine.SetMute(muted); }
            Invalidate();
        }

        private void RequestCollection()
        {
            Action<Point> handler = CollectionRequested;
            if (handler != null) handler(PointToScreen(new Point(collectionRect.Right, Height + 4)));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { pollTimer.Dispose(); Stop(); }
            base.Dispose(disposing);
        }

        private sealed class MiniMpvEngine : IDisposable
        {
            private const string Dll = "libmpv-2.dll";
            private IntPtr context;

            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr mpv_create();
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_initialize(IntPtr context);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] private static extern int mpv_set_option_string(IntPtr context, string name, string value);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] private static extern int mpv_set_property_string(IntPtr context, string name, string value);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_get_property", CharSet = CharSet.Ansi)]
            private static extern int mpv_get_property_double(IntPtr context, string name, int format, ref double value);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_command(IntPtr context, IntPtr args);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr mpv_wait_event(IntPtr context, double timeout);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern void mpv_terminate_destroy(IntPtr context);

            [StructLayout(LayoutKind.Sequential)]
            private struct MpvEvent { public int EventId, Error; public ulong ReplyUserdata; public IntPtr Data; }

            [StructLayout(LayoutKind.Sequential)]
            private struct MpvEventEndFile
            {
                public int Reason, Error;
                public long PlaylistEntryId, PlaylistInsertId;
                public int PlaylistInsertNumEntries;
            }

            public MiniMpvEngine()
            {
                context = mpv_create();
                if (context == IntPtr.Zero) throw new InvalidOperationException("无法创建 mpv 实例");
                try
                {
                    SetOption("vo", "null");
                    SetOption("video", "no");
                    SetOption("idle", "yes");
                    SetOption("keep-open", "yes");
                    SetOption("terminal", "no");
                    SetOption("config", "no");
                    SetOption("osc", "no");
                    SetOption("input-default-bindings", "no");
                    if (mpv_initialize(context) < 0) throw new InvalidOperationException("mpv 初始化失败");
                }
                catch { Dispose(); throw; }
            }

            public void LoadFile(string path) { Command("loadfile", path); }
            public void SetPause(bool paused) { mpv_set_property_string(context, "pause", paused ? "yes" : "no"); }
            public void SetVolume(int value) { mpv_set_property_string(context, "volume", value.ToString(CultureInfo.InvariantCulture)); }
            public void SetMute(bool mute) { mpv_set_property_string(context, "mute", mute ? "yes" : "no"); }
            public void Seek(double seconds) { Command("seek", seconds.ToString("0.###", CultureInfo.InvariantCulture), "absolute"); }

            public bool TryGetDouble(string name, out double value)
            {
                value = 0;
                return context != IntPtr.Zero && mpv_get_property_double(context, name, 5, ref value) >= 0;
            }

            public void DrainEvents(out bool ended, out bool error)
            {
                ended = error = false;
                for (int i = 0; i < 64 && context != IntPtr.Zero; i++)
                {
                    IntPtr pointer = mpv_wait_event(context, 0);
                    if (pointer == IntPtr.Zero) return;
                    MpvEvent mediaEvent = (MpvEvent)Marshal.PtrToStructure(pointer, typeof(MpvEvent));
                    if (mediaEvent.EventId == 0) return;
                    if (mediaEvent.EventId != 7 || mediaEvent.Data == IntPtr.Zero) continue;
                    MpvEventEndFile end = (MpvEventEndFile)Marshal.PtrToStructure(mediaEvent.Data, typeof(MpvEventEndFile));
                    if (end.Reason == 0) ended = true;
                    else if (end.Reason == 4) error = true;
                }
            }

            private void SetOption(string name, string value) { mpv_set_option_string(context, name, value); }

            private void Command(params string[] args)
            {
                IntPtr[] pointers = new IntPtr[args.Length + 1];
                try
                {
                    for (int i = 0; i < args.Length; i++) pointers[i] = AllocUtf8(args[i]);
                    IntPtr argv = Marshal.AllocHGlobal(IntPtr.Size * pointers.Length);
                    try { Marshal.Copy(pointers, 0, argv, pointers.Length); mpv_command(context, argv); }
                    finally { Marshal.FreeHGlobal(argv); }
                }
                finally
                {
                    for (int i = 0; i < pointers.Length; i++) if (pointers[i] != IntPtr.Zero) Marshal.FreeHGlobal(pointers[i]);
                }
            }

            private static IntPtr AllocUtf8(string text)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                IntPtr pointer = Marshal.AllocHGlobal(bytes.Length + 1);
                Marshal.Copy(bytes, 0, pointer, bytes.Length);
                Marshal.WriteByte(pointer, bytes.Length, 0);
                return pointer;
            }

            public void Dispose()
            {
                if (context == IntPtr.Zero) return;
                mpv_terminate_destroy(context);
                context = IntPtr.Zero;
            }
        }
    }
}

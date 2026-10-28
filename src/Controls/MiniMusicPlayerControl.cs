using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
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

        private readonly Timer pollTimer = new Timer { Interval = 250 };
        private List<string> paths = new List<string>();
        private MiniMpvEngine engine;
        private string title = "尚未播放";
        private string collectionKey;
        private int index;
        private bool playing;
        private bool externalPlayerActive;
        private int hover = -1;
        private Rectangle collectionRect, previousRect, playRect, nextRect, titleRect;

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
            try
            {
                if (engine == null) engine = new MiniMpvEngine();
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
            else if (ended) PlayNext();
            Invalidate();
        }

        public void SyncExternalState(string songPath, bool isPlaying)
        {
            externalPlayerActive = true;
            int current = paths.FindIndex(delegate(string path)
            {
                return string.Equals(path, songPath, StringComparison.OrdinalIgnoreCase);
            });
            if (current >= 0) index = current;
            title = string.IsNullOrEmpty(songPath) ? title : Path.GetFileNameWithoutExtension(songPath);
            playing = isPlaying;
            Invalidate();
        }

        public void ClearExternalState()
        {
            externalPlayerActive = false;
            playing = false;
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

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle card = new Rectangle(0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
            using (Brush brush = new SolidBrush(Theme.SegmentBg)) Draw.FillRoundedRect(g, brush, card, 9);
            using (Pen pen = new Pen(Theme.ControlBorder, 1F)) Draw.DrawRoundedRect(g, pen, card, 9);
            previousRect = new Rectangle(4, 4, 27, Height - 8);
            playRect = new Rectangle(previousRect.Right + 1, (Height - 26) / 2, 26, 26);
            nextRect = new Rectangle(playRect.Right + 2, 4, 27, Height - 8);
            collectionRect = new Rectangle(Width - 34, 3, 30, Height - 6);
            titleRect = new Rectangle(nextRect.Right + 7, 3, Math.Max(0, collectionRect.Left - nextRect.Right - 14), Height - 6);
            DrawHover(g, collectionRect, 0);
            DrawHover(g, previousRect, 1);
            DrawHover(g, nextRect, 3);
            DrawPreviousNext(g, previousRect, false);
            using (Brush brush = new SolidBrush(Theme.Accent)) g.FillEllipse(brush, playRect);
            if (playing) using (Brush brush = new SolidBrush(Color.White))
            {
                g.FillRectangle(brush, playRect.X + 9, playRect.Y + 7, 3, 12);
                g.FillRectangle(brush, playRect.X + 15, playRect.Y + 7, 3, 12);
            }
            else using (Brush brush = new SolidBrush(Color.White)) g.FillPolygon(brush,
                new Point[] { new Point(playRect.X + 10, playRect.Y + 7), new Point(playRect.X + 10, playRect.Y + 19),
                    new Point(playRect.X + 19, playRect.Y + 13) });
            DrawPreviousNext(g, nextRect, true);
            if (titleRect.Width > 0) TextRenderer.DrawText(g, title, Theme.CaptionFont, titleRect,
                Theme.TextPrimary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            Draw.DrawIcon(g, UiIconKind.List, new Rectangle(collectionRect.X + 5, collectionRect.Y + 3, 20, 20), Theme.IconColor);
        }

        private void DrawHover(Graphics g, Rectangle rect, int id)
        {
            if (hover != id) return;
            using (Brush brush = new SolidBrush(Theme.RowHover)) Draw.FillRoundedRect(g, brush, rect, 7);
        }

        private static void DrawPreviousNext(Graphics g, Rectangle rect, bool next)
        {
            int direction = next ? 1 : -1;
            int center = rect.X + rect.Width / 2, y = rect.Y + rect.Height / 2;
            using (Brush brush = new SolidBrush(Theme.IconColor))
            {
                g.FillPolygon(brush, new Point[] { new Point(center - direction * 4, y - 5),
                    new Point(center - direction * 4, y + 5), new Point(center + direction * 3, y) });
                g.FillRectangle(brush, next ? center - 6 : center + 4, y - 5, 2, 10);
            }
        }

        private int HitTest(Point point)
        {
            if (collectionRect.Contains(point)) return 0;
            if (previousRect.Contains(point)) return 1;
            if (playRect.Contains(point)) return 2;
            if (nextRect.Contains(point)) return 3;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
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
            int action = HitTest(e.Location);
            if (action == 0) RequestCollection();
            else if (action == 1) PlayPrevious();
            else if (action == 2) TogglePlay();
            else if (action == 3) PlayNext();
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
                    mpv_set_property_string(context, "volume", "60");
                }
                catch { Dispose(); throw; }
            }

            public void LoadFile(string path) { Command("loadfile", path); }
            public void SetPause(bool paused) { mpv_set_property_string(context, "pause", paused ? "yes" : "no"); }

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

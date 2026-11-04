using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace AppManager.Controls
{
    internal enum AudioEngineEventKind { StartFile, FileLoaded, EndFile }

    internal struct AudioEngineEvent
    {
        public AudioEngineEventKind Kind;
        public long PlaylistEntryId;
        public int EndReason;
    }

    internal sealed class MpvAudioEngine : IDisposable
    {
        private const string Dll = "libmpv-2.dll";
        private const int EventStartFile = 6, EventEndFile = 7, EventFileLoaded = 8;
        private const int FormatInt64 = 4, FormatDouble = 5, FormatFlag = 3;
        private IntPtr context;

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr mpv_create();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int mpv_initialize(IntPtr context);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int mpv_set_option_string(IntPtr context, string name, string value);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int mpv_set_property_string(IntPtr context, string name, string value);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_get_property", CharSet = CharSet.Ansi)]
        private static extern int mpv_get_property_double(IntPtr context, string name, int format, ref double data);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_get_property", CharSet = CharSet.Ansi)]
        private static extern int mpv_get_property_flag(IntPtr context, string name, int format, ref int data);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_get_property", CharSet = CharSet.Ansi)]
        private static extern int mpv_get_property_long(IntPtr context, string name, int format, ref long data);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int mpv_command(IntPtr context, IntPtr args);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr mpv_wait_event(IntPtr context, double timeout);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void mpv_terminate_destroy(IntPtr context);

        [StructLayout(LayoutKind.Sequential)]
        private struct MpvEvent
        {
            public int EventId;
            public int Error;
            public ulong ReplyUserdata;
            public IntPtr Data;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MpvEventStartFile
        {
            public long PlaylistEntryId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MpvEventEndFile
        {
            public int Reason;
            public int Error;
            public long PlaylistEntryId;
            public long PlaylistInsertId;
            public int PlaylistInsertNumEntries;
        }
        public MpvAudioEngine()
        {
            context = mpv_create();
            if (context == IntPtr.Zero) throw new InvalidOperationException("\u65E0\u6CD5\u521B\u5EFA mpv \u5B9E\u4F8B");
            try
            {
                SetOption("vo", "null");
                SetOption("video", "no");
                SetOption("keep-open", "yes");
                SetOption("idle", "yes");
                SetOption("terminal", "no");
                SetOption("config", "no");
                SetOption("osc", "no");
                SetOption("osd-level", "0");
                SetOption("input-default-bindings", "no");
                SetOption("input-media-keys", "no");
                SetOption("gapless-audio", "yes");
                if (mpv_initialize(context) < 0) throw new InvalidOperationException("mpv \u521D\u59CB\u5316\u5931\u8D25");
            }
            catch
            {
                mpv_terminate_destroy(context);
                context = IntPtr.Zero;
                throw;
            }
        }
        public bool IsAvailable { get { return context != IntPtr.Zero; } }

        public void LoadFile(string path) { Command("loadfile", path); }
        public void AppendFile(string path) { Command("loadfile", path, "append"); }
        public void PlaylistNext() { Command("playlist-next"); }
        public void RemovePlaylistIndex(int index)
        {
            Command("playlist-remove", index.ToString(CultureInfo.InvariantCulture));
        }
        public void SetPause(bool paused) { SetString("pause", paused ? "yes" : "no"); }
        public void SetVolume(int value) { SetString("volume", value.ToString(CultureInfo.InvariantCulture)); }
        public void SetMute(bool mute) { SetString("mute", mute ? "yes" : "no"); }
        public void SetLoopFile(bool loop) { SetString("loop-file", loop ? "inf" : "no"); }
        public void Seek(double seconds)
        {
            Command("seek", seconds.ToString("0.###", CultureInfo.InvariantCulture), "absolute");
        }
        public void Stop() { Command("stop"); }

        public bool TryGetDouble(string name, out double value)
        {
            value = 0;
            return context != IntPtr.Zero && mpv_get_property_double(context, name, FormatDouble, ref value) >= 0;
        }

        public bool TryGetLong(string name, out long value)
        {
            value = 0;
            return context != IntPtr.Zero && mpv_get_property_long(context, name, FormatInt64, ref value) >= 0;
        }

        public bool TryGetFlag(string name, out bool value)
        {
            value = false;
            int raw = 0;
            if (context == IntPtr.Zero || mpv_get_property_flag(context, name, FormatFlag, ref raw) < 0) return false;
            value = raw != 0;
            return true;
        }
        /// <summary>丢弃命令切换前遗留的事件，避免旧曲目的事件被新加载误认。</summary>
        public void DiscardEvents()
        {
            List<AudioEngineEvent> ignored = new List<AudioEngineEvent>();
            DrainEvents(ignored);
        }

        public void DrainEvents(List<AudioEngineEvent> events)
        {
            events.Clear();
            if (context == IntPtr.Zero) return;
            for (int i = 0; i < 128; i++)
            {
                IntPtr pointer = mpv_wait_event(context, 0);
                if (pointer == IntPtr.Zero) return;
                MpvEvent mediaEvent = (MpvEvent)Marshal.PtrToStructure(pointer, typeof(MpvEvent));
                if (mediaEvent.EventId == 0) return;
                if (mediaEvent.EventId == EventStartFile && mediaEvent.Data != IntPtr.Zero)
                {
                    MpvEventStartFile start = (MpvEventStartFile)Marshal.PtrToStructure(
                        mediaEvent.Data, typeof(MpvEventStartFile));
                    events.Add(new AudioEngineEvent
                    {
                        Kind = AudioEngineEventKind.StartFile,
                        PlaylistEntryId = start.PlaylistEntryId
                    });
                }
                else if (mediaEvent.EventId == EventFileLoaded)
                {
                    events.Add(new AudioEngineEvent { Kind = AudioEngineEventKind.FileLoaded });
                }
                else if (mediaEvent.EventId == EventEndFile && mediaEvent.Data != IntPtr.Zero)
                {
                    MpvEventEndFile end = (MpvEventEndFile)Marshal.PtrToStructure(
                        mediaEvent.Data, typeof(MpvEventEndFile));
                    events.Add(new AudioEngineEvent
                    {
                        Kind = AudioEngineEventKind.EndFile,
                        PlaylistEntryId = end.PlaylistEntryId,
                        EndReason = end.Reason
                    });
                }
            }
        }
        private int SetOption(string name, string value)
        {
            return mpv_set_option_string(context, name, value);
        }

        private void SetString(string name, string value)
        {
            if (context != IntPtr.Zero) mpv_set_property_string(context, name, value);
        }

        private int Command(params string[] args)
        {
            if (context == IntPtr.Zero) return -1;
            IntPtr[] pointers = new IntPtr[args.Length + 1];
            try
            {
                for (int i = 0; i < args.Length; i++) pointers[i] = AllocUtf8(args[i]);
                IntPtr argv = Marshal.AllocHGlobal(IntPtr.Size * pointers.Length);
                try
                {
                    Marshal.Copy(pointers, 0, argv, pointers.Length);
                    return mpv_command(context, argv);
                }
                finally { Marshal.FreeHGlobal(argv); }
            }
            finally
            {
                for (int i = 0; i < pointers.Length; i++)
                    if (pointers[i] != IntPtr.Zero) Marshal.FreeHGlobal(pointers[i]);
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
            IntPtr old = context;
            context = IntPtr.Zero;
            if (old != IntPtr.Zero) mpv_terminate_destroy(old);
        }
    }
}

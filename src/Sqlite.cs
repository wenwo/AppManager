using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AppManager
{
    // Windows 10/11 自带 SQLite；UTF-8 参数绑定，不拼接用户输入。
    internal sealed class Sqlite : IDisposable
    {
        private IntPtr handle;
        private const string Library = "winsqlite3.dll";
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_open_v2(byte[] path, out IntPtr db, int flags, IntPtr vfs);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close_v2(IntPtr db);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_errmsg(IntPtr db);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_busy_timeout(IntPtr db, int milliseconds);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int length, out IntPtr statement, IntPtr tail);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_step(IntPtr statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_finalize(IntPtr statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_reset(IntPtr statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_clear_bindings(IntPtr statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_bind_null(IntPtr statement, int index);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_bind_int64(IntPtr statement, int index, long value);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_bind_text(IntPtr statement, int index, byte[] value, int length, IntPtr destructor);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_count(IntPtr statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_bytes(IntPtr statement, int column);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_type(IntPtr statement, int column);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern long sqlite3_last_insert_rowid(IntPtr db);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_changes(IntPtr db);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_backup_init(IntPtr destination, byte[] destName, IntPtr source, byte[] sourceName);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_backup_step(IntPtr backup, int pages);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_backup_finish(IntPtr backup);

        internal Sqlite(string path, bool readOnly)
        {
            int rc = sqlite3_open_v2(Utf8(path), out handle, (readOnly ? 1 : 2 | 4) | 0x10000, IntPtr.Zero);
            if (rc != 0) { string error = Error; Dispose(); throw new IOException("SQLite open: " + error); }
            try
            {
                sqlite3_busy_timeout(handle, 5000);
                Execute("PRAGMA foreign_keys=ON");
                if (!readOnly)
                {
                    Execute("PRAGMA journal_mode=WAL");
                    Execute("PRAGMA synchronous=FULL");
                    Execute("PRAGMA wal_autocheckpoint=1000");
                }
            }
            catch { Dispose(); throw; }
        }
        private static byte[] Utf8(string value) { return Encoding.UTF8.GetBytes(value + "\0"); }
        private string Error
        {
            get
            {
                IntPtr ptr = sqlite3_errmsg(handle);
                int length = 0; while (Marshal.ReadByte(ptr, length) != 0) length++;
                byte[] bytes = new byte[length]; Marshal.Copy(ptr, bytes, 0, length);
                return Encoding.UTF8.GetString(bytes);
            }
        }
        internal long LastId { get { return sqlite3_last_insert_rowid(handle); } }
        internal int Changes { get { return sqlite3_changes(handle); } }
        internal void Rollback() { try { Execute("ROLLBACK"); } catch { } }
        internal Statement Prepare(string sql) { return new Statement(this, sql); }
        internal void Execute(string sql, params object[] args)
        {
            using (Statement s = Prepare(sql)) { s.Bind(args); while (s.Read()) { } }
        }
        internal List<string[]> Query(string sql, params object[] args)
        {
            List<string[]> rows = new List<string[]>();
            using (Statement s = Prepare(sql))
            {
                s.Bind(args);
                while (s.Read()) rows.Add(s.Row());
            }
            return rows;
        }
        internal string Scalar(string sql, params object[] args)
        {
            using (Statement s = Prepare(sql)) { s.Bind(args); return s.Read() ? s.Row()[0] : null; }
        }
        internal void Backup(string path)
        {
            if (File.Exists(path)) throw new IOException("备份目标已存在，请选择新文件。");
            using (Sqlite destination = new Sqlite(path, false))
            {
                IntPtr backup = sqlite3_backup_init(destination.handle, Utf8("main"), handle, Utf8("main"));
                if (backup == IntPtr.Zero) throw new IOException("SQLite backup init: " + destination.Error);
                int rc;
                try { rc = sqlite3_backup_step(backup, -1); }
                finally { sqlite3_backup_finish(backup); }
                if (rc != 101) throw new IOException("SQLite backup: " + rc);
                if (destination.Scalar("PRAGMA quick_check") != "ok") throw new IOException("备份完整性检查失败。");
                destination.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
                destination.Execute("PRAGMA journal_mode=DELETE");
            }
        }
        internal void Restore(string path)
        {
            using (Sqlite source = new Sqlite(path, true))
            {
                if (source.Scalar("PRAGMA application_id") != Scalar("PRAGMA application_id") ||
                    source.Scalar("PRAGMA user_version") != Scalar("PRAGMA user_version") ||
                    source.Scalar("PRAGMA integrity_check") != "ok" || source.Query("PRAGMA foreign_key_check").Count != 0)
                    throw new IOException("恢复文件格式或完整性检查失败。");
                IntPtr backup = sqlite3_backup_init(handle, Utf8("main"), source.handle, Utf8("main"));
                if (backup == IntPtr.Zero) throw new IOException("SQLite restore init: " + Error);
                int rc;
                try { rc = sqlite3_backup_step(backup, -1); }
                finally { sqlite3_backup_finish(backup); }
                if (rc != 101) throw new IOException("SQLite restore: " + rc);
            }
        }
        public void Dispose() { if (handle != IntPtr.Zero) { sqlite3_close_v2(handle); handle = IntPtr.Zero; } }

        internal sealed class Statement : IDisposable
        {
            private readonly Sqlite owner;
            private IntPtr handle;
            internal Statement(Sqlite db, string sql)
            {
                owner = db; byte[] bytes = Utf8(sql);
                if (sqlite3_prepare_v2(db.handle, bytes, bytes.Length, out handle, IntPtr.Zero) != 0)
                    throw new IOException("SQLite prepare: " + db.Error);
            }
            internal void Bind(params object[] values)
            {
                sqlite3_reset(handle); sqlite3_clear_bindings(handle);
                for (int i = 0; i < values.Length; i++)
                {
                    object value = values[i]; int rc;
                    if (value == null) rc = sqlite3_bind_null(handle, i + 1);
                    else if (value is int || value is long || value is bool)
                        rc = sqlite3_bind_int64(handle, i + 1, Convert.ToInt64(value));
                    else
                    {
                        byte[] text = Encoding.UTF8.GetBytes(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
                        rc = sqlite3_bind_text(handle, i + 1, text, text.Length, new IntPtr(-1));
                    }
                    if (rc != 0) throw new IOException("SQLite bind: " + owner.Error);
                }
            }
            internal bool Read()
            {
                int rc = sqlite3_step(handle);
                if (rc == 100) return true;
                if (rc == 101) return false;
                throw new IOException("SQLite step: " + owner.Error);
            }
            internal string[] Row()
            {
                string[] values = new string[sqlite3_column_count(handle)];
                for (int i = 0; i < values.Length; i++)
                {
                    if (sqlite3_column_type(handle, i) == 5) continue;
                    int size = sqlite3_column_bytes(handle, i);
                    byte[] bytes = new byte[size];
                    if (size > 0) Marshal.Copy(sqlite3_column_text(handle, i), bytes, 0, size);
                    values[i] = Encoding.UTF8.GetString(bytes);
                }
                return values;
            }
            public void Dispose() { if (handle != IntPtr.Zero) { sqlite3_finalize(handle); handle = IntPtr.Zero; } }
        }
    }
}

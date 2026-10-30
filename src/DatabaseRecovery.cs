using System;
using System.Collections.Generic;
using System.IO;

namespace AppManager
{
    internal static class DatabaseRecovery
    {
        // 仅在单实例锁内、数据库尚未打开时使用；损坏原件及 WAL/SHM 一并留底。
        internal static string Restore(string databasePath, string sourcePath)
        {
            if (string.Equals(Path.GetFullPath(databasePath), Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase))
                throw new IOException("请选择独立的完整备份。");
            string temporary = databasePath + ".recover-" + Guid.NewGuid().ToString("N");
            using (Sqlite source = new Sqlite(sourcePath, true))
            {
                if (source.Scalar("PRAGMA application_id") != "1095584594" || source.Scalar("PRAGMA user_version") != "1" ||
                    source.Scalar("PRAGMA integrity_check") != "ok" || source.Query("PRAGMA foreign_key_check").Count != 0)
                    throw new IOException("备份格式或完整性检查失败，原数据未变更。");
                source.Backup(temporary);
            }
            string safety = databasePath + ".before-recovery-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            List<string[]> moved = new List<string[]>(); bool replaced = false;
            try
            {
                if (File.Exists(databasePath)) using (FileStream exclusive = new FileStream(databasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                foreach (string suffix in new string[] { "-wal", "-shm" })
                    if (File.Exists(databasePath + suffix))
                    { File.Move(databasePath + suffix, safety + suffix); moved.Add(new string[] { databasePath + suffix, safety + suffix }); }
                if (File.Exists(databasePath)) File.Replace(temporary, databasePath, safety);
                else File.Move(temporary, databasePath);
                replaced = true;
                ConfigDatabase store = new ConfigDatabase(databasePath);
                store.NoteRecovery(safety);
                return safety;
            }
            catch
            {
                if (!replaced)
                    foreach (string[] file in moved) if (!File.Exists(file[0]) && File.Exists(file[1])) File.Move(file[1], file[0]);
                throw;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace AppManager
{
    // 复制必须完整成功；拒绝链接和超限目录，不能跳过后仍删除搬运源。
    internal static class SafeDirectory
    {
        internal sealed class Budget
        {
            private readonly Stopwatch elapsed = Stopwatch.StartNew();
            private readonly int maxEntries;
            private readonly long maxBytes;
            private readonly int maxMilliseconds;
            private int entries;
            internal long Bytes;

            internal Budget() : this(200000, 2L * 1024 * 1024 * 1024, 30000) { }
            internal Budget(int entries, long bytes, int milliseconds)
            {
                maxEntries = entries;
                maxBytes = bytes;
                maxMilliseconds = milliseconds;
            }

            internal void Add(int depth, long bytes)
            {
                if (depth > 64 || ++entries > maxEntries || bytes > maxBytes - Bytes
                    || elapsed.ElapsedMilliseconds >= maxMilliseconds)
                    throw new IOException("目录超过安全扫描上限，操作已取消；原文件保持不变。");
                Bytes += bytes;
            }
        }

        internal static void EnsureNoLinks(string path)
        {
            string current = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current))
            {
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("目录包含重解析点，操作已取消：" + current);
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                current = Path.GetDirectoryName(current);
            }
        }

        private static void Visit(string path, int depth, Budget budget, List<string> entries)
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("目录包含重解析点，操作已取消：" + path);
            bool directory = (attributes & FileAttributes.Directory) != 0;
            budget.Add(depth, directory ? 0 : new FileInfo(path).Length);
            if (entries != null) entries.Add(path);
            if (directory)
                foreach (string child in Directory.EnumerateFileSystemEntries(path))
                    Visit(child, depth + 1, budget, entries);
        }

        internal static long Measure(string path, Budget budget)
        {
            EnsureNoLinks(path);
            long before = budget.Bytes;
            Visit(path, 0, budget, null);
            return budget.Bytes - before;
        }

        internal static void Copy(string source, string target, bool overwrite)
        {
            Copy(source, target, overwrite, new Budget());
        }

        internal static void Copy(string source, string target, bool overwrite, Budget budget)
        {
            source = DesktopOrganizer.NormalizeRoot(Path.GetFullPath(source));
            target = DesktopOrganizer.NormalizeRoot(Path.GetFullPath(target));
            if (MainForm.IsUnder(source, target) || MainForm.IsUnder(target, source))
                throw new IOException("复制源和目标不能互相包含。");
            EnsureNoLinks(source);
            EnsureNoLinks(target);
            List<string> entries = new List<string>();
            Visit(source, 0, budget, entries); // 写入前完成预检，错误不能变成不完整的成功快照。
            Budget copyBudget = new Budget(200000, 2L * 1024 * 1024 * 1024, 600000);
            foreach (string path in entries)
            {
                string relative = path.Substring(source.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string dest = Path.Combine(target, relative);
                EnsureNoLinks(path);
                EnsureNoLinks(dest);
                bool directory = (File.GetAttributes(path) & FileAttributes.Directory) != 0;
                copyBudget.Add(0, directory ? 0 : new FileInfo(path).Length);
                if (directory) Directory.CreateDirectory(dest);
                else File.Copy(path, dest, overwrite);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;

namespace AppManager
{
    /// <summary>
    /// 后台扫描资源路径中的失效记录。只把“文件不存在且父目录仍可访问”视为已移走，
    /// 移动盘拔出、网络断开或父目录无权限时一律跳过，避免误清理。
    /// </summary>
    internal static class MissingResourceCleanup
    {
        internal static List<string> FindMissingWithReachableParent(IEnumerable<string> paths)
        {
            List<string> result = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (paths == null) return result;
            foreach (string path in paths)
            {
                if (string.IsNullOrWhiteSpace(path) || !seen.Add(path)) continue;
                string full;
                try
                {
                    if (!Path.IsPathRooted(path)) continue;
                    full = Path.GetFullPath(path);
                }
                catch (Exception) { continue; }
                if (File.Exists(full) || Directory.Exists(full)) continue;
                string parent;
                try { parent = Path.GetDirectoryName(full.TrimEnd('\\', '/')); }
                catch (Exception) { continue; }
                if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent)) result.Add(path);
            }
            return result;
        }
    }
}

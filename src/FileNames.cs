using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace AppManager
{
    /// <summary>
    /// 文件名与落点的通用规则：清洗成合法名称、同名时编号「名称 (2)」、收集仍在磁盘上的不重复路径。
    /// 分类存储目录、桌面备份还原、集合导出、复制到剪贴板共用这里，规则只维护一份。
    /// </summary>
    internal static class FileNames
    {
        private static readonly string[] ReservedNames = {
            "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

        /// <summary>
        /// 把任意文字变成可用的文件或文件夹名：不能用作文件名的字符换成 _，去掉首尾空格和首尾的点，
        /// 设备名（CON、NUL、COM1…，含「NUL.txt」这种写法）在主名后补 _；清洗后为空时用 fallback。
        /// </summary>
        internal static string SafeName(string name, string fallback)
        {
            if (string.IsNullOrEmpty(name)) return fallback;
            char[] invalid = Path.GetInvalidFileNameChars();
            char[] chars = name.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';
            string value = new string(chars).Trim().Trim('.');
            if (value.Length == 0) return fallback;
            int dot = value.IndexOf('.');
            string stem = (dot < 0 ? value : value.Substring(0, dot)).TrimEnd(' ');
            foreach (string reserved in ReservedNames)
                if (string.Equals(stem, reserved, StringComparison.OrdinalIgnoreCase))
                    return dot < 0 ? value + "_" : stem + "_" + value.Substring(dot);
            return value;
        }

        /// <summary>第 number 个同名候选（number ≥ 2）：「名称 (2).扩展名」；keepExtension 为 false 时整个名字后加「 (2)」。</summary>
        internal static string Numbered(string name, int number, bool keepExtension)
        {
            if (!keepExtension) return name + " (" + number + ")";
            return Path.GetFileNameWithoutExtension(name) + " (" + number + ")" + Path.GetExtension(name);
        }

        /// <summary>path 已被文件或文件夹占用时，依次找「名称 (2)」「名称 (3)」…中第一个空位，不合并、不覆盖。</summary>
        internal static string UniquePath(string path, bool keepExtension)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return path;
            string directory = Path.GetDirectoryName(path), name = Path.GetFileName(path);
            for (int i = 2; ; i++)
            {
                string candidate = Path.Combine(directory, Numbered(name, i, keepExtension));
                if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
            }
        }

        /// <summary>同一文件夹内占用一个名字（不区分大小写）；已被占用时按 Numbered 编号。</summary>
        internal static string Claim(HashSet<string> used, string name, bool keepExtension)
        {
            string candidate = name;
            for (int i = 2; !used.Add(candidate); i++) candidate = Numbered(name, i, keepExtension);
            return candidate;
        }

        /// <summary>
        /// 仍在磁盘上的实体路径：转成完整路径、去重并保持原顺序，按磁盘判断是文件夹（true）还是文件（false）。
        /// 空路径、格式无效或已不存在的计入 missing。会访问磁盘，量大时应在后台调用。
        /// </summary>
        internal static List<KeyValuePair<string, bool>> Existing(IEnumerable<string> paths, CancellationToken cancel, out int missing)
        {
            missing = 0;
            List<KeyValuePair<string, bool>> result = new List<KeyValuePair<string, bool>>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
            {
                cancel.ThrowIfCancellationRequested();
                string full;
                try
                {
                    if (string.IsNullOrEmpty(path)) { missing++; continue; }
                    full = Path.GetFullPath(path);
                }
                catch (Exception ex)
                {
                    if (!(ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException
                        || ex is System.Security.SecurityException)) throw;
                    missing++;
                    continue;
                }
                if (!seen.Add(full)) continue;
                if (Directory.Exists(full)) result.Add(new KeyValuePair<string, bool>(full, true));
                else if (File.Exists(full)) result.Add(new KeyValuePair<string, bool>(full, false));
                else missing++;
            }
            return result;
        }
    }
}

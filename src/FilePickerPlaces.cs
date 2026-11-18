using System;
using System.Collections.Generic;
using System.IO;

namespace AppManager
{
    internal enum FilePickerPlaceKind
    {
        LastUsed,
        Current,
        Pictures,
        Music,
        Videos,
        ProgramFiles,
        ProgramFilesX86,
        Desktop,
        Downloads,
        Documents
    }

    internal sealed class FilePickerPlace
    {
        internal FilePickerPlaceKind Kind;
        internal string Label;
        internal string Path;
    }

    /// <summary>四个文件选择界面共用的常用位置顺序、名称和去重规则。</summary>
    internal static class FilePickerPlaces
    {
        private static readonly Guid DownloadsFolder = new Guid("374DE290-123F-4565-9164-39C4925E467B");

        internal static List<FilePickerPlace> Build(string lastPath, string currentPath, string typeName,
            bool includeCurrent)
        {
            List<FilePickerPlace> result = new List<FilePickerPlace>();
            Add(result, FilePickerPlaceKind.LastUsed, "上次使用", lastPath);
            if (includeCurrent) Add(result, FilePickerPlaceKind.Current, "当前位置", currentPath);
            if (typeName == "照片") Add(result, FilePickerPlaceKind.Pictures, "图片", Special(Environment.SpecialFolder.MyPictures));
            else if (typeName == "音乐") Add(result, FilePickerPlaceKind.Music, "音乐", Special(Environment.SpecialFolder.MyMusic));
            else if (typeName == "视频") Add(result, FilePickerPlaceKind.Videos, "视频", Special(Environment.SpecialFolder.MyVideos));
            else if (typeName == "软件")
            {
                Add(result, FilePickerPlaceKind.ProgramFiles, "程序文件夹", Special(Environment.SpecialFolder.ProgramFiles));
                Add(result, FilePickerPlaceKind.ProgramFilesX86, "程序文件夹 (x86)", Special(Environment.SpecialFolder.ProgramFilesX86));
            }
            Add(result, FilePickerPlaceKind.Desktop, "桌面", Special(Environment.SpecialFolder.DesktopDirectory));
            string downloads = null;
            try { downloads = Scanner.KnownFolderPath(DownloadsFolder); } catch (Exception ex) { Paths.LogError(ex); }
            if (string.IsNullOrWhiteSpace(downloads))
            {
                string profile = Special(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrWhiteSpace(profile)) downloads = Path.Combine(profile, "Downloads");
            }
            Add(result, FilePickerPlaceKind.Downloads, "下载", downloads);
            Add(result, FilePickerPlaceKind.Documents, "文档", Special(Environment.SpecialFolder.MyDocuments));
            return result;
        }

        private static string Special(Environment.SpecialFolder folder)
        {
            string value = null;
            try { value = Environment.GetFolderPath(folder); }
            catch (PlatformNotSupportedException) { }
            if (!string.IsNullOrWhiteSpace(value)) return value;
            string profile;
            try { profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); }
            catch (PlatformNotSupportedException) { return value; }
            if (string.IsNullOrWhiteSpace(profile)) return value;
            switch (folder)
            {
                case Environment.SpecialFolder.MyPictures: return Path.Combine(profile, "Pictures");
                case Environment.SpecialFolder.MyMusic: return Path.Combine(profile, "Music");
                case Environment.SpecialFolder.MyVideos: return Path.Combine(profile, "Videos");
                default: return value;
            }
        }

        private static void Add(List<FilePickerPlace> result, FilePickerPlaceKind kind, string label, string path)
        {
            string full = LocalScanScope.NormalizePath(path);
            if (string.IsNullOrEmpty(full) || !IsQuickLocal(full)) return;
            try { if (!Directory.Exists(full)) return; } catch { return; }
            foreach (FilePickerPlace item in result)
                if (string.Equals(item.Path, full, StringComparison.OrdinalIgnoreCase)) return;
            result.Add(new FilePickerPlace { Kind = kind, Label = label, Path = full });
        }

        private static bool IsQuickLocal(string full)
        {
            if (full == null || full.Length < 3 || full[1] != ':') return false;
            try
            {
                DriveType type = new DriveInfo(full.Substring(0, 3)).DriveType;
                return type == DriveType.Fixed || type == DriveType.Removable;
            }
            catch (ArgumentException) { return false; }
        }
    }
}

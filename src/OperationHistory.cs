using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;

namespace AppManager
{
    /// <summary>一处改动的类型。</summary>
    internal enum ChangeKind { Added, Modified, Removed, Unknown }

    /// <summary>操作记录里的一处改动：改动前后的字段值，以及给人看的三段文字。</summary>
    internal sealed class ChangeRow
    {
        internal long Id;
        internal string Entity = string.Empty;
        internal ChangeKind Kind = ChangeKind.Unknown;
        /// <summary>改动前、改动后的字段值（列名 → 值，数据库里的 NULL 记为 null）；新增没有 Before，删除没有 After。</summary>
        internal Dictionary<string, string> Before, After;
        /// <summary>改的是哪类东西、具体是哪一个、怎么改的。</summary>
        internal string Subject = string.Empty, Title = string.Empty, Detail = string.Empty;

        public override string ToString() { return OperationHistory.RowText(this); }
    }

    /// <summary>一次操作的改动明细（一页）和整次操作的新增 / 修改 / 删除数量。</summary>
    internal sealed class OperationDetails
    {
        internal int Added, Modified, Removed;
        internal readonly List<ChangeRow> Rows = new List<ChangeRow>();
        /// <summary>后面还有没列出的改动。</summary>
        internal bool More;
    }

    /// <summary>
    /// 把操作记录里的内部名称、计数和改动语句翻译成给人看的文字（纯逻辑，供「数据维护与操作记录」窗口和回归测试使用）。
    /// 数据库内容按不可信输入处理：认不出的名称原样显示，解析不了的语句只标记为无法解读，不抛异常。
    /// </summary>
    internal static class OperationHistory
    {
        private static readonly Dictionary<string, string> Titles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "physical.files.import", "导入文件" },
            { "physical.files.move", "移动文件" },
            { "physical.files.create", "新建文件或文件夹" },
            { "physical.files.delete", "删除文件" },
            { "physical.files.rename", "重命名文件" },
            { "physical.files.legacy", "整理旧版资源文件" },
            { "physical.desktop.autostash", "自动收纳桌面" },
            { "physical.desktop.organize", "整理桌面" },
            { "physical.desktop.restore", "还原桌面文件" },
            { "physical.desktop.remove", "移除桌面收纳记录" },
            { "physical.desktop.update", "更新桌面收纳记录" },
            { "physical.layout.restore", "恢复布局备份" },
            { "migration.categories.split", "数据升级：拆分同名分类" },
            { "migration.software.files", "数据升级：软件里的文件转为资源" },
            { "migration.entries.case", "数据升级：合并大小写重复的项目" },
            { "import.ambak", "导入备份包" },
            { "phone.music.import", "导入手机音乐" },
            { "cleanup.missing.music", "清理失效的音乐" },
            { "music.cleanup.invalid", "清理失效的音乐" },
            { "catalog.discover", "扫描登记新项目" },
            { "catalog.rebuild", "重建项目目录" },
            { "catalog.repair", "修复项目目录" },
            { "restore.snapshot", "恢复数据库" },
            { "rollback", "撤销操作" },
            { "migration.xml", "首次建立数据库" },
            { "config.save", "保存配置" },
            // 以下五个是旧版本用过的内部名称，老记录里仍会出现
            { "CommitImport", "导入文件" },
            { "OnCreateCollection", "新建集合" },
            { "DeleteEntriesDisk", "删除文件" },
            { "RenameAddedEntry", "修改显示名称" },
            { "MakeSetCat", "修改所属分类" },
            { "AddItemsToCategory", "添加到分类或集合" },
            { "RemoveItemsFromCategory", "从分类或集合移除" },
            { "RemoveTargetsFromCategory", "从分类或集合移除" },
            { "RemoveFromCustom", "从自定义分类移除" },
            { "SetHardCategory", "修改所属分类" },
            { "OnAddCategory", "新建分类" },
            { "ApplyCategoryRename", "重命名分类或集合" },
            { "ChangeCategoryIcon", "更换分类图标" },
            { "DeleteCategory", "删除分类" },
            { "CreateCollectionUnder", "新建集合" },
            { "OnCollectionEditSave", "编辑集合" },
            { "MoveCollection", "移动集合" },
            { "DeleteCollection", "删除集合" },
            { "DeleteAddedEntries", "移除添加的项目" },
            { "RenameDisplayName", "修改显示名称" },
            { "SyncDisplayName", "同步显示名称" },
            { "ToggleFavorite", "收藏或取消收藏" },
            { "SetHidden", "隐藏或取消隐藏" },
            { "OnGridReorder", "调整项目顺序" },
            { "OnSidebarReorder", "调整侧栏顺序" },
            { "CheckMoveOuts", "同步已移出的文件" },
            { "CommitStoreLocations", "修改存储位置" },
            { "SetCloseToTray", "修改设置" },
            { "ToggleOrganizeCopy", "修改整理桌面设置" },
            { "ToggleOrganizeScope", "修改整理桌面设置" },
            { "desktop.options", "修改桌面收纳设置" },
            { "BuildControls", "修改设置" }
        };

        private static readonly Dictionary<string, string> Settings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "AutoOrganizeDesktop", "自动收纳桌面" },
            { "AutoScan", "启动时自动扫描" },
            { "CloseToTray", "关闭时最小化到托盘" },
            { "FileStorePath", "文件存放位置" },
            { "FolderStorePath", "文件夹存放位置" },
            { "ManageRootPath", "管理目录" },
            { "ResourceStorePath", "资源存放位置" },
            { "ResourceStoreLocations", "资源存储位置" },
            { "OrganizeCopy", "整理桌面时保留原件" },
            { "OrganizeDir", "整理桌面包含文件夹" },
            { "OrganizeFile", "整理桌面包含文件" },
            { "OrganizeShortcut", "整理桌面包含快捷方式" },
            { "ShowHidden", "显示隐藏的项目" },
            { "ViewGrid", "默认显示方式" }
        };

        /// <summary>按条存放的内容类别（内部名称 → 叫法）；类别为空时数据库里只有一条同名的占位记录。</summary>
        private static readonly Dictionary<string, string> Lists = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "ItemCategories", "归属" },
            { "ExcludedCategories", "排除" },
            { "ItemOrders", "排序" },
            { "Favorites", "收藏" },
            { "Hidden", "隐藏" },
            { "CustomCategories", "自定义分类" },
            { "ResourceCategories", "资源分类" },
            { "SidebarOrder", "侧栏顺序" },
            { "Collections", "集合" },
            { "CategoryOverrides", "所属分类" },
            { "CategoryViewModes", "显示方式" },
            { "CategoryIconNames", "分类图标" },
            { "AddedItems", "添加的项目" },
            { "DesktopBackup", "桌面收纳" }
        };

        // ===== 操作 =====

        /// <summary>操作的中文名称；认不出的内部名称显示为「修改数据」，原名由 IsKnown 为 false 时另行展示。</summary>
        internal static string Title(string action)
        {
            string title;
            if (!string.IsNullOrEmpty(action) && Titles.TryGetValue(action, out title)) return title;
            if (!string.IsNullOrEmpty(action) && action.StartsWith("migration.", StringComparison.Ordinal)) return "数据升级";
            return "修改数据";
        }

        internal static bool IsKnown(string action)
        {
            return !string.IsNullOrEmpty(action)
                && (Titles.ContainsKey(action) || action.StartsWith("migration.", StringComparison.Ordinal));
        }

        /// <summary>扫描时自动产生、与用户操作无关的记录；窗口默认不列出。范围与 LatestBusinessSql 一致：也不挡撤销。</summary>
        internal static bool IsSystem(string action)
        {
            return action != null && action.StartsWith("catalog.", StringComparison.Ordinal);
        }

        /// <summary>这类操作为什么不能撤销；可以撤销时返回 null。判定与 ConfigDatabase.Undo 保持一致。</summary>
        internal static string UndoBlock(string action)
        {
            if (action == null) return null;
            if (action.StartsWith("migration.", StringComparison.Ordinal) || action == "restore.snapshot")
                return "数据升级和整库恢复不能单独撤销；需要回退请用「数据库维护」里的「从备份恢复」。";
            if (action.StartsWith("physical.", StringComparison.OrdinalIgnoreCase))
                return "这次操作同时改动了磁盘上的文件，不能在这里撤销；需要还原请用「布局备份」。";
            return null;
        }

        /// <summary>摘要里的 rows=N：这次操作一共改动了多少处；没有记录时返回 -1。</summary>
        internal static int ChangeCount(string summary)
        {
            if (string.IsNullOrEmpty(summary)) return -1;
            int start = summary.LastIndexOf("rows=", StringComparison.Ordinal);
            if (start < 0) return -1;
            int value;
            return int.TryParse(summary.Substring(start + 5).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value) ? value : -1;
        }

        /// <summary>左侧列表第二行的说明：把内部摘要换成一句话。titleOf 用来把「撤销了哪条」换成那条操作的名称，可为 null。</summary>
        internal static string SummaryText(string action, string summary, long reverts, Func<long, string> titleOf)
        {
            summary = summary ?? string.Empty;
            if (reverts > 0)
            {
                string target = titleOf == null ? null : titleOf(reverts);
                return string.IsNullOrEmpty(target) ? "撤销了之前的一次操作" : "撤销了「" + target + "」";
            }
            if (summary.StartsWith("safety=", StringComparison.Ordinal)) return "恢复前的数据已留底";
            if (summary.StartsWith("new identities=", StringComparison.Ordinal))
                return "登记 " + summary.Substring("new identities=".Length).Trim() + " 个新项目";
            int count = ChangeCount(summary);
            if (count >= 0) return "改动 " + count + " 处";
            return summary.Trim();
        }

        /// <summary>完整性检查结果的说明；ok 表示没有发现问题。原始结果形如 integrity=ok; foreign_key_errors=0; records=12; items=3。</summary>
        internal static string CheckText(string raw, out bool ok)
        {
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string part in (raw ?? string.Empty).Split(';'))
            {
                int cut = part.IndexOf('=');
                if (cut > 0) values[part.Substring(0, cut).Trim()] = part.Substring(cut + 1).Trim();
            }
            string integrity, foreign, records, items;
            values.TryGetValue("integrity", out integrity);
            values.TryGetValue("foreign_key_errors", out foreign);
            values.TryGetValue("records", out records);
            values.TryGetValue("items", out items);
            ok = integrity == "ok" && foreign == "0";
            if (ok)
                return "检查通过，数据库没有损坏"
                    + (records != null && items != null ? "（" + items + " 个项目，" + records + " 条配置记录）" : string.Empty) + "。";
            if (integrity != null && integrity != "ok")
                return "数据库文件有损坏，建议先做一次完整备份留底，再用「从备份恢复」换回之前的备份。";
            if (foreign != null && foreign != "0")
                return "发现 " + foreign + " 处数据对不上，建议先做一次完整备份留底，再用「从备份恢复」换回之前的备份。";
            return "没有得到检查结果，请稍后再试。";
        }

        // ===== 改动语句的解析 =====

        /// <summary>
        /// 解析审计触发器生成的语句：INSERT INTO t(列…) VALUES(值…) 或 UPDATE t SET 列=值,… WHERE id=值，返回 列名 → 值。
        /// DELETE 语句没有字段值；它和任何不符合上述形式的文本都返回 null。
        /// </summary>
        internal static Dictionary<string, string> ParseValues(string sql)
        {
            if (string.IsNullOrEmpty(sql)) return null;
            try
            {
                if (sql.StartsWith("INSERT INTO ", StringComparison.Ordinal)) return ParseInsert(sql);
                if (sql.StartsWith("UPDATE ", StringComparison.Ordinal)) return ParseUpdate(sql);
            }
            catch (ArgumentOutOfRangeException) { }
            catch (IndexOutOfRangeException) { }
            return null;
        }

        private static Dictionary<string, string> ParseInsert(string sql)
        {
            int open = sql.IndexOf('(');
            int close = open < 0 ? -1 : sql.IndexOf(')', open);
            const string marker = " VALUES(";
            if (close < 0 || string.CompareOrdinal(sql, close + 1, marker, 0, marker.Length) != 0) return null;
            string[] columns = sql.Substring(open + 1, close - open - 1).Split(',');
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
            int at = close + 1 + marker.Length;
            for (int i = 0; i < columns.Length; i++)
            {
                string value;
                if (!ReadValue(sql, ref at, out value)) return null;
                values[columns[i].Trim()] = value;
                if (at >= sql.Length || sql[at] != (i == columns.Length - 1 ? ')' : ',')) return null;
                at++;
            }
            return at == sql.Length ? values : null;
        }

        private static Dictionary<string, string> ParseUpdate(string sql)
        {
            const string set = " SET ", where = " WHERE id=";
            int at = sql.IndexOf(set, StringComparison.Ordinal);
            if (at < 0) return null;
            at += set.Length;
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
            while (true)
            {
                int equals = sql.IndexOf('=', at);
                if (equals <= at) return null;
                string column = sql.Substring(at, equals - at);
                at = equals + 1;
                string value;
                if (!ReadValue(sql, ref at, out value)) return null;
                values[column.Trim()] = value;
                if (at < sql.Length && sql[at] == ',') { at++; continue; }
                if (string.CompareOrdinal(sql, at, where, 0, where.Length) != 0) return null;
                at += where.Length;
                if (!ReadValue(sql, ref at, out value)) return null;
                values["id"] = value;
                return at == sql.Length ? values : null;
            }
        }

        /// <summary>读一个 SQLite quote() 输出的值：NULL、数字、'文本'（内部单引号写成两个）、X'十六进制'。</summary>
        private static bool ReadValue(string sql, ref int at, out string value)
        {
            value = null;
            if (at >= sql.Length) return false;
            if (string.CompareOrdinal(sql, at, "NULL", 0, 4) == 0) { at += 4; return true; }
            bool blob = sql[at] == 'X' && at + 1 < sql.Length && sql[at + 1] == '\'';
            if (blob) at++;
            if (sql[at] == '\'')
            {
                StringBuilder text = new StringBuilder();
                at++;
                while (true)
                {
                    int quote = sql.IndexOf('\'', at);
                    if (quote < 0) return false;
                    text.Append(sql, at, quote - at);
                    at = quote + 1;
                    if (at < sql.Length && sql[at] == '\'') { text.Append('\''); at++; continue; }
                    break;
                }
                value = blob ? "（二进制数据）" : text.ToString();
                return true;
            }
            int start = at;
            while (at < sql.Length && (char.IsDigit(sql[at]) || sql[at] == '-' || sql[at] == '+' || sql[at] == '.' || sql[at] == 'e' || sql[at] == 'E')) at++;
            if (at == start) return false;
            value = sql.Substring(start, at - start);
            return true;
        }

        /// <summary>由一条改动记录的撤销语句和重做语句得到改动类型和前后字段值；解析不了时类型为 Unknown。</summary>
        internal static ChangeRow Parse(long id, string entity, string undoSql, string redoSql)
        {
            ChangeRow row = new ChangeRow();
            row.Id = id;
            row.Entity = entity ?? string.Empty;
            redoSql = redoSql ?? string.Empty;
            if (redoSql.StartsWith("INSERT INTO ", StringComparison.Ordinal))
            {
                row.After = ParseValues(redoSql);
                if (row.After != null) row.Kind = ChangeKind.Added;
            }
            else if (redoSql.StartsWith("DELETE FROM ", StringComparison.Ordinal))
            {
                row.Before = ParseValues(undoSql);
                if (row.Before != null) row.Kind = ChangeKind.Removed;
            }
            else if (redoSql.StartsWith("UPDATE ", StringComparison.Ordinal))
            {
                row.Before = ParseValues(undoSql);
                row.After = ParseValues(redoSql);
                if (row.Before != null && row.After != null) row.Kind = ChangeKind.Modified;
            }
            return row;
        }

        // ===== 改动的说明文字 =====

        internal static string KindText(ChangeKind kind)
        {
            switch (kind)
            {
                case ChangeKind.Added: return "新增";
                case ChangeKind.Modified: return "修改";
                case ChangeKind.Removed: return "删除";
                default: return "其它";
            }
        }

        /// <summary>一处改动的完整文字（复制用）。</summary>
        internal static string RowText(ChangeRow row)
        {
            return KindText(row.Kind) + " " + row.Subject + "：" + row.Title
                + (string.IsNullOrEmpty(row.Detail) ? string.Empty : "\r\n" + row.Detail);
        }

        /// <summary>
        /// 填写一处改动的三段文字。groups：分组编号 → 分组键（分类名或 col: 开头的集合键）；entries：项目编号 → 项目键。
        /// 查不到的编号说明该分类或项目后来已被删除。
        /// </summary>
        internal static void Describe(ChangeRow row, Dictionary<long, string> groups, Dictionary<long, string> entries)
        {
            Dictionary<string, string> now = row.After ?? row.Before;
            if (row.Kind == ChangeKind.Unknown || now == null)
            {
                row.Subject = "其它";
                row.Title = "无法解读的改动";
                row.Detail = "完整内容可用「导出明细」查看。";
                return;
            }
            Dictionary<string, string> old = row.Kind == ChangeKind.Modified ? row.Before : null;
            switch (row.Entity)
            {
                case "groups": DescribeGroup(row, now, old); break;
                case "entries":
                    row.Subject = "项目登记";
                    row.Title = EntryName(Get(now, "key"));
                    row.Detail = EntryPath(Get(now, "key"));
                    if (old != null && Get(old, "key") != Get(now, "key"))
                    {
                        string before = EntryPath(Get(old, "key"));
                        row.Detail = "原为：" + (before.Length > 0 ? before : EntryName(Get(old, "key")))
                            + (row.Detail.Length > 0 ? " → " + row.Detail : string.Empty);
                    }
                    break;
                case "added_items": DescribeAdded(row, now, old, groups); break;
                case "records": DescribeRecord(row, now, old, groups, entries); break;
                default:
                    row.Subject = "其它";
                    row.Title = row.Entity;
                    break;
            }
            row.Title = Clip(row.Title, 200);
            row.Detail = Clip(row.Detail, 400);
        }

        private static void DescribeGroup(ChangeRow row, Dictionary<string, string> now, Dictionary<string, string> old)
        {
            string key = Get(now, "key");
            row.Subject = CollectionKeys.IsCollectionKey(key) ? "集合" : "分类";
            row.Title = GroupName(key);
            if (old == null) return;
            if (Get(old, "name") != Get(now, "name")) row.Detail = "原名：" + Get(old, "name");
            else if (Get(old, "key") != key) row.Detail = "原位置：" + GroupName(Get(old, "key"));
        }

        private static void DescribeAdded(ChangeRow row, Dictionary<string, string> now, Dictionary<string, string> old, Dictionary<long, string> groups)
        {
            bool folder = Get(now, "folder") == "1";
            row.Subject = Get(now, "resource") == "1" ? (folder ? "文件夹" : "文件") : (folder ? "添加的文件夹" : "添加的项目");
            string path = Get(now, "path");
            row.Title = string.IsNullOrEmpty(Get(now, "name")) ? LastSegment(path) : Get(now, "name");
            if (old == null)
            {
                string place = GroupOf(groups, Get(now, "group_id"));
                row.Detail = path + (place == null ? string.Empty : "  ·  所在：" + place);
                return;
            }
            List<string> parts = new List<string>();
            if (Get(old, "name") != Get(now, "name")) parts.Add("名称：" + Get(old, "name") + " → " + Get(now, "name"));
            if (Get(old, "path") != path) parts.Add("位置：" + Get(old, "path") + " → " + path);
            if (Get(old, "group_id") != Get(now, "group_id"))
                parts.Add("所在：" + (GroupOf(groups, Get(old, "group_id")) ?? "无") + " → " + (GroupOf(groups, Get(now, "group_id")) ?? "无"));
            row.Detail = parts.Count == 0 ? path : string.Join("  ·  ", parts.ToArray());
        }

        private static void DescribeRecord(ChangeRow row, Dictionary<string, string> now, Dictionary<string, string> old,
            Dictionary<long, string> groups, Dictionary<long, string> entries)
        {
            string section = Get(now, "section");
            string group = GroupOf(groups, Get(now, "group_id")), oldGroup = old == null ? null : GroupOf(groups, Get(old, "group_id"));
            string entryKey = EntryOf(entries, Get(now, "item_id"));
            string item = entryKey == null ? "已不存在的项目" : EntryName(entryKey);
            string place = group ?? "已删除的分类或集合";
            switch (section)
            {
                case "Settings":
                    string slot = Get(now, "slot"), name;
                    if (Lists.TryGetValue(slot, out name))
                    {
                        // 某类内容一条都没有时，程序存一条空的占位记录；有了第一条内容就删掉它
                        row.Subject = "内部标记";
                        row.Title = "「" + name + "」为空的占位记录";
                        row.Detail = "程序自用，不代表内容有增减";
                        break;
                    }
                    row.Subject = "设置";
                    row.Title = Settings.TryGetValue(slot, out name) ? name : slot;
                    string value = SettingValue(Get(now, "payload"));
                    row.Detail = old == null ? value : SettingValue(Get(old, "payload")) + " → " + value;
                    break;
                case "ItemCategories":
                    row.Subject = "归属";
                    row.Title = item;
                    row.Detail = (row.Kind == ChangeKind.Removed ? "移出 " : "加入 ") + place;
                    break;
                case "ExcludedCategories":
                    row.Subject = "排除";
                    row.Title = item;
                    row.Detail = (row.Kind == ChangeKind.Removed ? "恢复在 " + place + " 中显示" : "不在 " + place + " 中显示");
                    break;
                case "CategoryOverrides":
                    row.Subject = "所属分类";
                    row.Title = item;
                    row.Detail = old != null && oldGroup != group ? (oldGroup ?? "无") + " → " + place
                        : (row.Kind == ChangeKind.Removed ? "不再指定为 " : "归入 ") + place;
                    break;
                case "ItemOrders":
                    row.Subject = "排序";
                    row.Title = item;
                    row.Detail = place + " 中" + (old == null ? Position(now) : Position(old) + " → " + Position(now));
                    break;
                case "Favorites": row.Subject = "收藏"; row.Title = item; break;
                case "Hidden": row.Subject = "隐藏"; row.Title = item; break;
                case "CustomCategories": row.Subject = "自定义分类"; row.Title = place; break;
                case "ResourceCategories": row.Subject = "资源分类"; row.Title = place; break;
                case "SidebarOrder":
                    row.Subject = "侧栏顺序";
                    row.Title = place;
                    row.Detail = old == null ? Position(now) : Position(old) + " → " + Position(now);
                    break;
                case "Collections":
                    row.Subject = "集合信息";
                    row.Title = place;
                    row.Detail = old == null ? string.Empty : "封面、说明或顺序有调整";
                    break;
                case "CategoryViewModes": row.Subject = "显示方式"; row.Title = place; break;
                case "CategoryIconNames": row.Subject = "分类图标"; row.Title = place; break;
                case "AddedItems":
                    row.Subject = "添加的项目";
                    row.Title = item;
                    row.Detail = entryKey == null ? string.Empty : EntryPath(entryKey);
                    break;
                case "DesktopBackup":
                    string[] paths = Get(now, "slot").Split('\n');
                    row.Subject = "桌面收纳";
                    row.Title = LastSegment(paths[0]);
                    row.Detail = "原位置：" + paths[0] + (paths.Length > 1 && paths[1].Length > 0 ? "  ·  收纳到：" + paths[1] : string.Empty);
                    break;
                default:
                    row.Subject = "配置";
                    row.Title = section;
                    row.Detail = Get(now, "slot");
                    break;
            }
            // 修改但上面没有专门说明的：位置变了就写位置，否则只说明内容有调整
            if (old != null && string.IsNullOrEmpty(row.Detail))
                row.Detail = Get(old, "ordinal") != Get(now, "ordinal") ? Position(old) + " → " + Position(now) : "内容有调整";
            if (entryKey != null && string.IsNullOrEmpty(row.Detail) && row.Title == item) row.Detail = EntryPath(entryKey);
        }

        private static string Get(Dictionary<string, string> values, string column)
        {
            string value;
            return values.TryGetValue(column, out value) && value != null ? value : string.Empty;
        }

        private static string Position(Dictionary<string, string> values)
        {
            long ordinal;
            return long.TryParse(Get(values, "ordinal"), NumberStyles.Integer, CultureInfo.InvariantCulture, out ordinal)
                ? "第 " + (ordinal + 1) + " 位" : "位置未知";
        }

        /// <summary>分组键的可读形式：分类直接是名称，集合写成「照片 / 旅行」。</summary>
        internal static string GroupName(string key)
        {
            return string.IsNullOrEmpty(key) ? "（未命名）" : CollectionKeys.DisplayPath(key);
        }

        private static string GroupOf(Dictionary<long, string> groups, string id)
        {
            long number;
            string key;
            if (!long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) return null;
            return groups != null && groups.TryGetValue(number, out key) ? GroupName(key) : "已删除的分类或集合";
        }

        private static string EntryOf(Dictionary<long, string> entries, string id)
        {
            long number;
            string key;
            if (!long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) return null;
            return entries != null && entries.TryGetValue(number, out key) ? key : null;
        }

        /// <summary>
        /// 项目键的显示名。按路径登记的项目（add:路径、lnk:快捷方式路径）取路径最后一段，快捷方式不带扩展名；
        /// 其余（af:名称、旧版的 reg:名称）去掉类型前缀；没有前缀的原样显示。
        /// </summary>
        internal static string EntryName(string key)
        {
            if (string.IsNullOrEmpty(key)) return "（未命名）";
            string path = EntryPath(key);
            if (path.Length > 0)
            {
                string name = LastSegment(path);
                return key.StartsWith("lnk:", StringComparison.OrdinalIgnoreCase) && name.Length > 4
                    && name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 4) : name;
            }
            // 前缀是 2~4 个字母；单个字母加冒号是盘符，不是前缀
            int cut = key.IndexOf(':');
            return cut >= 2 && cut <= 4 && cut < key.Length - 1 ? key.Substring(cut + 1) : key;
        }

        /// <summary>按路径登记的项目（add:、lnk:）的完整路径；其它项目没有路径，返回空串。</summary>
        internal static string EntryPath(string key)
        {
            return key != null && key.Length > 4 && (key.StartsWith("add:", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("lnk:", StringComparison.OrdinalIgnoreCase)) ? key.Substring(4) : string.Empty;
        }

        /// <summary>路径最后一段。内容不可信，不用会对非法字符抛异常的 Path.GetFileName。</summary>
        internal static string LastSegment(string path)
        {
            if (string.IsNullOrEmpty(path)) return "（未命名）";
            string trimmed = path.TrimEnd('\\', '/');
            int cut = trimmed.LastIndexOfAny(new char[] { '\\', '/' });
            string name = cut >= 0 ? trimmed.Substring(cut + 1) : trimmed;
            return name.Length == 0 ? path : name;
        }

        /// <summary>设置项的值：开关写成 开 / 关，单个值原样显示，含多项内容的不展开。</summary>
        internal static string SettingValue(string payload)
        {
            if (string.IsNullOrEmpty(payload)) return "（空）";
            try
            {
                XmlDocument fragment = new XmlDocument();
                fragment.XmlResolver = null;
                // 内容来自数据库，不可信：禁止 DTD，避免实体展开
                XmlReaderSettings safe = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
                using (XmlReader reader = XmlReader.Create(new StringReader(payload), safe)) fragment.Load(reader);
                XmlElement root = fragment.DocumentElement;
                foreach (XmlNode child in root.ChildNodes) if (child is XmlElement) return "（多项内容）";
                string text = root.InnerText.Trim();
                if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase)) return "开";
                if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase)) return "关";
                return text.Length == 0 ? "（空）" : text;
            }
            catch (XmlException) { return "（无法读取）"; }
        }

        private static string Clip(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            text = text.Replace("\r", string.Empty).Replace('\n', ' ');
            return text.Length <= max ? text : text.Substring(0, max) + "…";
        }
    }

    internal sealed partial class ConfigDatabase
    {
        /// <summary>
        /// 只有编号最大的业务操作可以撤销；catalog.*（扫描登记、重建目录、启动时修复目录）只改可重建的目录投影和标识注册表，
        /// 撤销后会按需重建，不算业务操作、不挡撤销。Undo 与操作记录窗口共用。
        /// </summary>
        private const string LatestBusinessSql = "SELECT max(id) FROM operations WHERE action NOT LIKE 'catalog.%'";

        /// <summary>当前唯一可能被撤销的操作编号（是否真的可撤销还取决于操作类型）；没有任何业务操作时为 0。</summary>
        internal long LatestBusinessOperation()
        {
            using (Sqlite db = Open()) return Number(db.Scalar(LatestBusinessSql));
        }

        /// <summary>
        /// 按编号从新到旧分页读取操作记录：before 为 0 表示从最新开始，否则只取编号更小的。
        /// 每行依次为 编号、UTC 时间、内部名称、摘要、被它撤销的操作编号。includeSystem 为 false 时不含扫描自动产生的记录。
        /// </summary>
        internal List<string[]> HistoryPage(long before, int limit, bool includeSystem)
        {
            if (before < 0) throw new ArgumentOutOfRangeException("before");
            if (limit < 1 || limit > 1000) throw new ArgumentOutOfRangeException("limit");
            using (Sqlite db = Open())
                return db.Query("SELECT id,utc,action,summary,reverts FROM operations WHERE (?=0 OR id<?)"
                    + (includeSystem ? string.Empty : " AND action NOT LIKE 'catalog.%'") + " ORDER BY id DESC LIMIT ?", before, before, limit);
        }

        /// <summary>
        /// 读取一次操作的一页改动明细（编号大于 after 的前 limit 处），并换成给人看的文字。
        /// withCounts 为 true 时同时统计整次操作的新增 / 修改 / 删除数量（只在读第一页时需要）。
        /// </summary>
        internal OperationDetails ChangePage(long operation, long after, int limit, bool withCounts)
        {
            if (limit < 1 || limit > 5000) throw new ArgumentOutOfRangeException("limit");
            OperationDetails result = new OperationDetails();
            using (Sqlite db = Open())
            {
                if (withCounts)
                    foreach (string[] count in db.Query("SELECT substr(redo_sql,1,6),count(*) FROM changes WHERE operation_id=? GROUP BY 1", operation))
                    {
                        int number = Convert.ToInt32(count[1], CultureInfo.InvariantCulture);
                        if (count[0] == "INSERT") result.Added += number;
                        else if (count[0] == "UPDATE") result.Modified += number;
                        else if (count[0] == "DELETE") result.Removed += number;
                    }
                using (Sqlite.Statement rows = db.Prepare("SELECT id,entity,undo_sql,redo_sql FROM changes WHERE operation_id=? AND id>? ORDER BY id LIMIT ?"))
                {
                    rows.Bind(operation, after, limit + 1);
                    while (rows.Read())
                    {
                        if (result.Rows.Count == limit) { result.More = true; break; }
                        string[] r = rows.Row();
                        result.Rows.Add(OperationHistory.Parse(Number(r[0]), r[1], r[2], r[3]));
                    }
                }

                // 本页改动里出现的分组和项目自带名称（含这次操作里被删除的）；其余编号再按当前数据库补查。
                Dictionary<long, string> groupKeys = new Dictionary<long, string>(), entryKeys = new Dictionary<long, string>();
                HashSet<long> groupIds = new HashSet<long>(), entryIds = new HashSet<long>();
                foreach (ChangeRow row in result.Rows)
                    foreach (Dictionary<string, string> values in new Dictionary<string, string>[] { row.Before, row.After })
                    {
                        if (values == null) continue;
                        if (row.Entity == "groups" || row.Entity == "entries")
                        {
                            long id; string key;
                            if (values.TryGetValue("key", out key) && key != null && TryNumber(values, "id", out id))
                                (row.Entity == "groups" ? groupKeys : entryKeys)[id] = key;
                            continue;
                        }
                        long reference;
                        if (TryNumber(values, "group_id", out reference)) groupIds.Add(reference);
                        if (TryNumber(values, "item_id", out reference)) entryIds.Add(reference);
                    }
                LookUpKeys(db, "groups", groupIds, groupKeys);
                LookUpKeys(db, "entries", entryIds, entryKeys);
                foreach (ChangeRow row in result.Rows) OperationHistory.Describe(row, groupKeys, entryKeys);
            }
            return result;
        }

        private static bool TryNumber(Dictionary<string, string> values, string column, out long number)
        {
            string text;
            number = 0;
            return values.TryGetValue(column, out text) && text != null
                && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number);
        }

        /// <summary>按编号补查键；table 只会是代码里写死的 groups 或 entries，编号全部走参数绑定。</summary>
        private static void LookUpKeys(Sqlite db, string table, HashSet<long> ids, Dictionary<long, string> keys)
        {
            List<object> batch = new List<object>();
            Action flush = delegate
            {
                if (batch.Count == 0) return;
                string marks = string.Join(",", new string('?', batch.Count).ToCharArray());
                foreach (string[] row in db.Query("SELECT id,key FROM " + table + " WHERE id IN(" + marks + ")", batch.ToArray()))
                    keys[Number(row[0])] = row[1];
                batch.Clear();
            };
            foreach (long id in ids)
            {
                if (keys.ContainsKey(id)) continue;
                batch.Add(id);
                if (batch.Count == 200) flush();
            }
            flush();
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace AppManager
{
    internal enum ImportDuplicateKind
    {
        BatchPathOverlap,
        ExistingPathCurrent,
        ExistingPathOther,
        ExistingIdentity,
        SameCategoryContent,
        OtherCategoryContent,
        BatchContentDuplicate,
        Unchecked
    }

    internal sealed class ImportDuplicateFinding
    {
        internal ImportItem Item;
        internal string Source, MatchPath, Message, ExpectedHash;
        internal ImportDuplicateKind Kind;
        internal bool CanKeep, Keep;
    }

    internal sealed class ImportDuplicateReport
    {
        internal readonly List<ImportDuplicateFinding> Findings = new List<ImportDuplicateFinding>();
        internal readonly Dictionary<ImportItem, string> ExpectedHashes = new Dictionary<ImportItem, string>();

        internal int Count(ImportDuplicateKind kind) { return Findings.Count(f => f.Kind == kind); }
        internal int BatchPathOverlap { get { return Count(ImportDuplicateKind.BatchPathOverlap); } }
        internal int ExistingPathCurrent { get { return Count(ImportDuplicateKind.ExistingPathCurrent); } }
        internal int ExistingPathOther { get { return Count(ImportDuplicateKind.ExistingPathOther); } }
        internal int ExistingIdentity { get { return Count(ImportDuplicateKind.ExistingIdentity); } }
        internal int SameCategoryContent { get { return Count(ImportDuplicateKind.SameCategoryContent); } }
        internal int OtherCategoryContent { get { return Count(ImportDuplicateKind.OtherCategoryContent); } }
        internal int BatchContentDuplicate { get { return Count(ImportDuplicateKind.BatchContentDuplicate); } }
        internal int Unchecked { get { return Count(ImportDuplicateKind.Unchecked); } }

        internal int DuplicateCount
        {
            get { return BatchPathOverlap + ExistingPathCurrent + ExistingIdentity + SameCategoryContent + BatchContentDuplicate; }
        }

        internal int ReuseCount
        {
            get { return ExistingPathOther + OtherCategoryContent; }
        }

        internal bool NeedsPrompt
        {
            get
            {
                return BatchPathOverlap > 0 || ExistingPathCurrent > 0 || ExistingPathOther > 0 || ExistingIdentity > 0
                    || SameCategoryContent > 0 || OtherCategoryContent > 0 || BatchContentDuplicate > 0 || Unchecked > 0;
            }
        }

        internal void Add(ImportDuplicateKind kind, ImportItem item, string source, string matchPath, string message,
            bool canKeep, string expectedHash)
        {
            Findings.Add(new ImportDuplicateFinding
            {
                Kind = kind, Item = item, Source = source, MatchPath = matchPath, Message = message,
                CanKeep = canKeep, ExpectedHash = expectedHash
            });
        }

        internal string Summary()
        {
            List<string> parts = new List<string>();
            if (BatchPathOverlap > 0) parts.Add("同批路径重复 " + BatchPathOverlap);
            if (ExistingPathCurrent > 0) parts.Add("当前分类已存在 " + ExistingPathCurrent);
            if (ExistingPathOther > 0) parts.Add("其他分类复用 " + ExistingPathOther);
            if (ExistingIdentity > 0) parts.Add("同一文件身份 " + ExistingIdentity);
            if (SameCategoryContent > 0) parts.Add("同分类内容重复 " + SameCategoryContent);
            if (OtherCategoryContent > 0) parts.Add("跨分类内容复用 " + OtherCategoryContent);
            if (BatchContentDuplicate > 0) parts.Add("同批内容重复 " + BatchContentDuplicate);
            if (Unchecked > 0) parts.Add("未检查 " + Unchecked);
            return parts.Count == 0 ? "未发现重复" : string.Join("，", parts.ToArray());
        }
    }

    internal sealed class ImportDuplicateDecision
    {
        internal readonly HashSet<string> KeepSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, string> KeepHashes =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> SkipSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, string> SkipHashes =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> ReviewedFindings = new HashSet<string>(StringComparer.Ordinal);

        internal static ImportDuplicateDecision FromReport(ImportDuplicateReport report)
        {
            ImportDuplicateDecision decision = new ImportDuplicateDecision();
            if (report == null) return decision;
            foreach (ImportDuplicateFinding finding in report.Findings)
            {
                if (!string.IsNullOrEmpty(finding.Source)) decision.ReviewedFindings.Add(ReviewKey(finding));
                if (finding.Keep && !string.IsNullOrEmpty(finding.Source))
                {
                    decision.KeepSources.Add(finding.Source);
                    decision.KeepHashes[finding.Source] = finding.ExpectedHash ?? "";
                }
                if (finding.Kind == ImportDuplicateKind.BatchContentDuplicate && !finding.Keep && !string.IsNullOrEmpty(finding.Source))
                {
                    decision.SkipSources.Add(finding.Source);
                    if (!string.IsNullOrEmpty(finding.ExpectedHash)) decision.SkipHashes[finding.Source] = finding.ExpectedHash;
                }
            }
            return decision;
        }

        internal static string ReviewKey(ImportDuplicateFinding finding)
        {
            if (finding == null) return "";
            return (finding.Source ?? "").ToUpperInvariant() + "|" + (int)finding.Kind + "|"
                + (finding.MatchPath ?? "").ToUpperInvariant() + "|" + (finding.ExpectedHash ?? "");
        }
    }

    internal sealed class ImportDuplicateContext
    {
        internal string DatabaseFile;
        internal IList<AddedEntry> MemoryEntries;
        internal ImportDuplicateDecision Decision;
        internal Func<ImportDuplicateReport, ICollection<string>> Confirm;
    }

    /// <summary>
    /// 导入前的统一重复检查。路径/来源优先于内容；不同文件身份必须完整 SHA-256 相同才会按内容判重。
    /// 文件夹自身只检查路径/来源，不递归检查内部文件。
    /// </summary>
    internal static class ImportDuplicateInspector
    {
        private const int QuickBytes = 64 * 1024;
        private const int CacheLimit = 8192;
        private static readonly object cacheLock = new object();
        private static readonly Dictionary<string, CachedHash> hashCache =
            new Dictionary<string, CachedHash>(StringComparer.OrdinalIgnoreCase);

        private sealed class CachedHash { internal string Quick, Full; }
        private sealed class FileState { internal string Path, Identity, Quick, Full; internal long Length, Modified; }

        private sealed class KnownEntry
        {
            internal long RecordId;
            internal string Path, OriginPath;
            internal bool Folder, Resource;
            internal HashSet<string> Categories = new HashSet<string>(StringComparer.Ordinal);
        }

        private sealed class KnownIndex
        {
            internal readonly List<KnownEntry> Entries = new List<KnownEntry>();
            internal IEnumerable<KnownEntry> Matching(string path, bool resource)
            {
                if (string.IsNullOrEmpty(path)) return Enumerable.Empty<KnownEntry>();
                return Entries.Where(entry => entry.Resource == resource
                    && (string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(entry.OriginPath, path, StringComparison.OrdinalIgnoreCase)));
            }
        }

        private sealed class Candidate { internal string Path; internal FileState State; }

        internal static ImportDuplicateReport InspectPaths(IEnumerable<string> paths, string category, bool resource,
            ImportDuplicateContext context, CancellationToken cancel)
        {
            List<string> selectedAll = NormalizeAll(paths);
            List<string> selected = EffectiveSelection(selectedAll);
            ImportBatch batch = new ImportBatch { Category = category, Resource = resource };
            foreach (string path in selected)
            {
                try
                {
                    string full = Path.GetFullPath(path);
                    batch.Items.Add(new ImportItem { Source = full, Origin = full, Folder = Directory.Exists(full) });
                }
                catch { }
            }
            return InspectCore(batch, selectedAll, category, resource, context, cancel);
        }

        internal static bool InspectAndApply(ImportBatch batch, IEnumerable<string> selectedPaths, string category,
            bool resource, ImportDuplicateContext context, CancellationToken cancel)
        {
            ImportDuplicateReport report = InspectCore(batch, selectedPaths, category, resource, context, cancel);
            ApplyDecisionKeep(report, context == null ? null : context.Decision);
            if (context != null && context.Confirm != null && NeedsConfirmation(report, context.Decision))
            {
                ICollection<string> keep = context.Confirm(report);
                if (keep == null) return false;
                ApplyKeep(report, keep);
            }
            Apply(batch, report);
            batch.DuplicateReport = report;
            return true;
        }

        private static bool NeedsConfirmation(ImportDuplicateReport report, ImportDuplicateDecision decision)
        {
            if (report == null || !report.NeedsPrompt) return false;
            if (decision == null || decision.ReviewedFindings.Count == 0) return true;
            return report.Findings.Any(finding => finding.Item != null && !string.IsNullOrEmpty(finding.Source)
                && !decision.ReviewedFindings.Contains(ImportDuplicateDecision.ReviewKey(finding)));
        }

        private static void ApplyDecisionKeep(ImportDuplicateReport report, ImportDuplicateDecision decision)
        {
            foreach (ImportDuplicateFinding finding in report.Findings)
            {
                if (!finding.CanKeep || string.IsNullOrEmpty(finding.Source) || decision == null
                    || !decision.KeepSources.Contains(finding.Source)) continue;
                string expected;
                if (!decision.KeepHashes.TryGetValue(finding.Source, out expected)
                    || string.Equals(expected, finding.ExpectedHash ?? "", StringComparison.Ordinal)) finding.Keep = true;
            }
        }

        private static void ApplyKeep(ImportDuplicateReport report, ICollection<string> keep)
        {
            HashSet<string> set = new HashSet<string>(keep ?? new string[0], StringComparer.OrdinalIgnoreCase);
            foreach (ImportDuplicateFinding finding in report.Findings)
                if (finding.CanKeep && !string.IsNullOrEmpty(finding.Source)) finding.Keep = set.Contains(finding.Source);
        }

        private static void Apply(ImportBatch batch, ImportDuplicateReport report)
        {
            foreach (ImportDuplicateFinding finding in report.Findings)
            {
                ImportItem item = finding.Item;
                if (item == null || item.State == FileImport.Failed || item.State == FileImport.Skipped) continue;
                switch (finding.Kind)
                {
                    case ImportDuplicateKind.BatchContentDuplicate:
                        if (!finding.Keep)
                        {
                            item.State = FileImport.Skipped;
                            item.Error = "同一次选择中已存在相同内容，已跳过；本次选择的原文件保留。";
                        }
                        else if (!string.IsNullOrEmpty(finding.ExpectedHash)) report.ExpectedHashes[item] = finding.ExpectedHash;
                        break;
                    case ImportDuplicateKind.SameCategoryContent:
                        if (finding.Keep)
                        {
                            if (!string.IsNullOrEmpty(finding.ExpectedHash)) report.ExpectedHashes[item] = finding.ExpectedHash;
                        }
                        else Reuse(item, finding.MatchPath);
                        break;
                    case ImportDuplicateKind.ExistingPathCurrent:
                    case ImportDuplicateKind.ExistingPathOther:
                    case ImportDuplicateKind.ExistingIdentity:
                    case ImportDuplicateKind.OtherCategoryContent:
                        Reuse(item, finding.MatchPath);
                        break;
                }
            }
            foreach (KeyValuePair<ImportItem, string> expected in report.ExpectedHashes)
            {
                if (!expected.Key.Reference && expected.Key.State != FileImport.Failed && expected.Key.State != FileImport.Skipped)
                    expected.Key.ExpectedHash = expected.Value;
            }
        }

        private static void Reuse(ImportItem item, string target)
        {
            if (item == null || string.IsNullOrEmpty(target)) return;
            item.Reference = true;
            item.Source = target;
            item.Target = target;
            item.Error = null;
        }
        private static ImportDuplicateReport InspectCore(ImportBatch batch, IEnumerable<string> selectedPaths, string category,
            bool resource, ImportDuplicateContext context, CancellationToken cancel)
        {
            ImportDuplicateReport report = new ImportDuplicateReport();
            if (batch == null) return report;
            context = context ?? new ImportDuplicateContext();
            ImportDuplicateDecision decision = context.Decision ?? new ImportDuplicateDecision();
            List<string> selectedAll = NormalizeAll(selectedPaths);
            List<string> selected = EffectiveSelection(selectedAll);
            MarkBatchOverlap(report, batch, selectedAll);

            List<string> lookup = new List<string>();
            foreach (ImportItem item in batch.Items.Where(i => i != null && i.State != FileImport.Failed && i.State != FileImport.Skipped))
            {
                AddDistinct(lookup, item.Source);
                AddDistinct(lookup, item.Origin);
            }
            KnownIndex known = LoadKnown(context, lookup, cancel);

            List<KeyValuePair<ImportItem, FileState>> contentItems = new List<KeyValuePair<ImportItem, FileState>>();
            foreach (ImportItem item in batch.Items)
            {
                cancel.ThrowIfCancellationRequested();
                if (item == null || item.State == FileImport.Failed || item.State == FileImport.Skipped) continue;
                string source = item.Origin ?? item.Source;
                if (string.IsNullOrEmpty(source)) continue;

                KnownEntry match;
                bool current;
                if (TryResolveKnown(known, source, item.Source, item.Folder, category, resource, out match, out current))
                {
                    report.Add(current ? ImportDuplicateKind.ExistingPathCurrent : ImportDuplicateKind.ExistingPathOther,
                        item, source, match.Path, current ? "当前分类中已存在该路径。" : "其他分类中已有该文件，将复用已有目标。",
                        false, null);
                    continue;
                }

                if (item.Folder || !File.Exists(item.Source)) continue;
                FileState state = null;
                string reason = null;
                bool hashed = false;
                if (decision.SkipSources.Contains(source) && !decision.KeepSources.Contains(source))
                {
                    string expected;
                    if (!decision.SkipHashes.TryGetValue(source, out expected) || string.IsNullOrEmpty(expected))
                    {
                        report.Add(ImportDuplicateKind.BatchContentDuplicate, item, source, null,
                            "同一次选择中已存在相同内容，已跳过；首项失败时不会自动补救。", true, null);
                        continue;
                    }
                    hashed = TryGetHashes(item.Source, true, out state, out reason, cancel);
                    if (hashed && string.Equals(state.Full, expected, StringComparison.Ordinal))
                    {
                        report.Add(ImportDuplicateKind.BatchContentDuplicate, item, source, null,
                            "同一次选择中已存在相同内容，已跳过；首项失败时不会自动补救。", true, expected);
                        continue;
                    }
                    // 预检查后文件已变化：不再沿用旧的“跳过”决定，按当前内容重新判定。
                }
                if (!hashed && !TryGetHashes(item.Source, true, out state, out reason, cancel))
                {
                    report.Add(ImportDuplicateKind.Unchecked, item, source, null, reason, false, null);
                    continue;
                }
                contentItems.Add(new KeyValuePair<ImportItem, FileState>(item, state));
            }

            if (contentItems.Count == 0) return report;
            HashSet<long> lengths = new HashSet<long>(contentItems.Select(pair => pair.Value.Length));
            List<string> candidatePaths = LoadCandidatePaths(context, lengths, resource, cancel);
            HashSet<string> selectedSet = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
            foreach (ImportItem item in batch.Items)
            {
                if (item == null) continue;
                if (!string.IsNullOrEmpty(item.Source)) selectedSet.Add(item.Source);
                if (!string.IsNullOrEmpty(item.Origin)) selectedSet.Add(item.Origin);
            }
            Dictionary<long, Dictionary<string, List<Candidate>>> candidates =
                BuildCandidateIndex(candidatePaths, selectedSet, lengths, cancel);
            Dictionary<string, KeyValuePair<ImportItem, FileState>> batchHashes =
                new Dictionary<string, KeyValuePair<ImportItem, FileState>>(StringComparer.Ordinal);
            Dictionary<ImportItem, List<Candidate>> matched = new Dictionary<ImportItem, List<Candidate>>();

            foreach (KeyValuePair<ImportItem, FileState> pair in contentItems)
            {
                cancel.ThrowIfCancellationRequested();
                ImportItem item = pair.Key;
                FileState source = pair.Value;
                string sourcePath = item.Origin ?? item.Source;
                KeyValuePair<ImportItem, FileState> first;
                if (batchHashes.TryGetValue(source.Full, out first))
                {
                    report.Add(ImportDuplicateKind.BatchContentDuplicate, item, sourcePath, first.Key.Origin ?? first.Key.Source,
                        "同一次选择中已存在相同内容，已跳过；本次选择的原文件保留。", true, source.Full);
                    continue;
                }
                batchHashes[source.Full] = pair;

                Dictionary<string, List<Candidate>> byQuick;
                List<Candidate> sameQuick;
                if (!candidates.TryGetValue(source.Length, out byQuick) || !byQuick.TryGetValue(source.Quick, out sameQuick)) continue;
                foreach (Candidate candidate in sameQuick)
                {
                    cancel.ThrowIfCancellationRequested();
                    if (string.Equals(candidate.Path, sourcePath, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(candidate.Path, item.Source, StringComparison.OrdinalIgnoreCase)) continue;
                    FileState target;
                    string reason;
                    if (!TryGetHashes(candidate.Path, true, out target, out reason, cancel)) continue;
                    if (target.Length != source.Length || target.Full != source.Full) continue;
                    List<Candidate> list;
                    if (!matched.TryGetValue(item, out list)) matched[item] = list = new List<Candidate>();
                    list.Add(new Candidate { Path = candidate.Path, State = target });
                }
            }

            HashSet<ImportItem> handled = new HashSet<ImportItem>();
            if (matched.Count > 0)
            {
                KnownIndex matchedIndex = LoadKnown(context, matched.Values.SelectMany(list => list).Select(value => value.Path)
                    .Distinct(StringComparer.OrdinalIgnoreCase), cancel);
                foreach (KeyValuePair<ImportItem, FileState> pair in contentItems)
                {
                    List<Candidate> list;
                    if (!matched.TryGetValue(pair.Key, out list) || list.Count == 0) continue;
                    Candidate chosen = list.FirstOrDefault(value => matchedIndex.Matching(value.Path, resource)
                        .Any(entry => !entry.Folder && entry.Categories.Contains(category))) ?? list[0];
                    bool current = matchedIndex.Matching(chosen.Path, resource)
                        .Any(entry => !entry.Folder && entry.Categories.Contains(category));
                    bool identity = string.Equals(pair.Value.Identity, chosen.State.Identity, StringComparison.OrdinalIgnoreCase);
                    report.Add(identity ? ImportDuplicateKind.ExistingIdentity : current ? ImportDuplicateKind.SameCategoryContent
                            : ImportDuplicateKind.OtherCategoryContent,
                        pair.Key, pair.Key.Origin ?? pair.Key.Source, chosen.Path,
                        current ? "当前分类中已有相同内容。" : "其他分类中已有相同内容，将复用已有目标。",
                        !identity && current, pair.Value.Full);
                    handled.Add(pair.Key);
                }
            }

            foreach (KeyValuePair<ImportItem, FileState> pair in contentItems)
                if (!handled.Contains(pair.Key)) report.ExpectedHashes[pair.Key] = pair.Value.Full;
            return report;
        }

        private static void MarkBatchOverlap(ImportDuplicateReport report, ImportBatch batch, List<string> selected)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> folders = selected.Where(path => Directory.Exists(path)).ToList();
            foreach (string path in selected)
            {
                string match = null;
                if (!seen.Add(path))
                    match = selected.FirstOrDefault(value => string.Equals(value, path, StringComparison.OrdinalIgnoreCase));
                else
                {
                    foreach (string folder in folders)
                    {
                        if (string.Equals(folder, path, StringComparison.OrdinalIgnoreCase)) continue;
                        try { if (FileImport.Within(folder, path)) { match = folder; break; } }
                        catch { }
                    }
                }
                if (match != null)
                    report.Add(ImportDuplicateKind.BatchPathOverlap, null, path, match,
                        "同一次选择中路径重复或被外层文件夹包含，只处理最外层/第一项。", false, null);
            }
        }

        private static List<string> NormalizeAll(IEnumerable<string> paths)
        {
            List<string> result = new List<string>();
            foreach (string path in paths ?? Enumerable.Empty<string>())
            {
                string full = NormalizeOne(path);
                if (!string.IsNullOrEmpty(full)) result.Add(full);
            }
            return result;
        }

        /// <summary>与导入预处理一致：去掉重复路径，并只保留最外层文件夹或第一项文件。</summary>
        private static List<string> EffectiveSelection(List<string> paths)
        {
            List<string> result = new List<string>();
            if (paths == null || paths.Count == 0) return result;
            List<string> folders = paths.Where(path => Directory.Exists(path)).ToList();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
            {
                if (!seen.Add(path)) continue;
                bool nested = false;
                foreach (string folder in folders)
                {
                    if (string.Equals(folder, path, StringComparison.OrdinalIgnoreCase)) continue;
                    try { if (FileImport.Within(folder, path)) { nested = true; break; } }
                    catch { }
                }
                if (!nested) result.Add(path);
            }
            return result;
        }

        private static string NormalizeOne(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try { return Path.GetFullPath(path); }
            catch { return path; }
        }

        private static void AddDistinct(List<string> list, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            if (!list.Contains(value, StringComparer.OrdinalIgnoreCase)) list.Add(value);
        }

        private static bool TryResolveKnown(KnownIndex known, string origin, string source, bool folder, string category, bool resource,
            out KnownEntry match, out bool current)
        {
            match = null; current = false;
            List<KnownEntry> entries = known.Matching(origin, resource).Concat(known.Matching(source, resource)).Distinct()
                .Where(entry => entry.Folder == folder && PathExists(entry.Path)).OrderBy(entry => entry.RecordId).ToList();
            if (entries.Count == 0) return false;
            match = entries.FirstOrDefault(entry => entry.Categories.Contains(category)) ?? entries[0];
            current = match.Categories.Contains(category);
            return true;
        }

        private static bool PathExists(string path)
        {
            return !string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path));
        }
        private static KnownIndex LoadKnown(ImportDuplicateContext context, IEnumerable<string> paths, CancellationToken cancel)
        {
            KnownIndex index = new KnownIndex();
            if (context == null || paths == null) return index;
            List<string> distinct = paths.Where(path => !string.IsNullOrEmpty(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (distinct.Count == 0) return index;
            if (!string.IsNullOrEmpty(context.DatabaseFile))
                LoadKnownDatabase(index, context.DatabaseFile, distinct, cancel);
            else if (context.MemoryEntries != null)
            {
                foreach (AddedEntry entry in context.MemoryEntries)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.Path)) continue;
                    KnownEntry value = new KnownEntry { Path = entry.Path, OriginPath = entry.OriginPath,
                        Folder = entry.IsFolder, Resource = entry.IsResource };
                    if (!string.IsNullOrEmpty(entry.Category)) value.Categories.Add(entry.Category);
                    index.Entries.Add(value);
                }
            }
            return index;
        }

        private static void LoadKnownDatabase(KnownIndex index, string databaseFile, List<string> paths, CancellationToken cancel)
        {
            List<string> keys = paths.Select(path => path.ToUpperInvariant()).Distinct(StringComparer.Ordinal).ToList();
            List<string[]> rows = new List<string[]>();
            using (Sqlite db = new Sqlite(databaseFile, true))
            {
                for (int offset = 0; offset < keys.Count; offset += 256)
                {
                    cancel.ThrowIfCancellationRequested();
                    List<string> chunk = keys.GetRange(offset, Math.Min(256, keys.Count - offset));
                    List<string> marks = chunk.ConvertAll(value => "?");
                    object[] args = chunk.ConvertAll(value => (object)value).ToArray();
                    rows.AddRange(db.Query("SELECT a.id,a.item_id,a.path,a.folder,a.resource,ifnull(a.origin,''),ifnull(g.key,'')"
                        + " FROM added_items a LEFT JOIN groups g ON g.id=a.group_id"
                        + " WHERE a.path_key IN (" + string.Join(",", marks.ToArray()) + ")"
                        + " OR a.origin_key IN (" + string.Join(",", marks.ToArray()) + ") ORDER BY a.id",
                        args.Concat(args).ToArray()));
                }
                if (rows.Count == 0) return;
                List<string> ids = rows.Select(row => row[1]).Distinct(StringComparer.Ordinal).ToList();
                Dictionary<string, List<string>> extras = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                Dictionary<string, string> overrides = new Dictionary<string, string>(StringComparer.Ordinal);
                Dictionary<string, HashSet<string>> excluded = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                HashSet<string> resourceCategories = new HashSet<string>(
                    db.Query("SELECT g.key FROM records r JOIN groups g ON g.id=r.group_id WHERE r.section='ResourceCategories'")
                        .Select(row => row[0] ?? ""), StringComparer.Ordinal);
                for (int offset = 0; offset < ids.Count; offset += 256)
                {
                    cancel.ThrowIfCancellationRequested();
                    List<string> chunk = ids.GetRange(offset, Math.Min(256, ids.Count - offset));
                    List<string> marks = chunk.ConvertAll(value => "?");
                    object[] args = chunk.ConvertAll(value => (object)value).ToArray();
                    foreach (string[] row in db.Query("SELECT r.item_id,r.section,ifnull(g.key,'') FROM records r"
                        + " LEFT JOIN groups g ON g.id=r.group_id WHERE r.item_id IN (" + string.Join(",", marks.ToArray()) + ")"
                        + " AND r.section IN ('ItemCategories','CategoryOverrides','ExcludedCategories') ORDER BY r.id", args))
                    {
                        string id = row[0], section = row[1], category = row[2] ?? "";
                        if (string.IsNullOrEmpty(category)) continue;
                        if (section == "CategoryOverrides") { if (!overrides.ContainsKey(id)) overrides[id] = category; }
                        else if (section == "ItemCategories")
                        {
                            List<string> list;
                            if (!extras.TryGetValue(id, out list)) extras[id] = list = new List<string>();
                            if (!list.Contains(category)) list.Add(category);
                        }
                        else
                        {
                            HashSet<string> set;
                            if (!excluded.TryGetValue(id, out set)) excluded[id] = set = new HashSet<string>(StringComparer.Ordinal);
                            set.Add(category);
                        }
                    }
                }
                foreach (string[] row in rows)
                {
                    string id = row[1], baseCategory = row[6] ?? "";
                    string overrideCategory;
                    if (!overrides.TryGetValue(id, out overrideCategory)) overrideCategory = null;
                    HashSet<string> categories = new HashSet<string>(StringComparer.Ordinal);
                    bool resource = row[4] == "1";
                    if (!string.IsNullOrEmpty(baseCategory) && (string.IsNullOrEmpty(overrideCategory)
                        || IsResourceCategory(overrideCategory, resourceCategories) != resource)) categories.Add(baseCategory);
                    if (!string.IsNullOrEmpty(overrideCategory) && IsResourceCategory(overrideCategory, resourceCategories) == resource)
                        categories.Add(overrideCategory);
                    List<string> extra;
                    if (extras.TryGetValue(id, out extra)) foreach (string value in extra) categories.Add(value);
                    HashSet<string> skip;
                    if (excluded.TryGetValue(id, out skip)) categories.RemoveWhere(skip.Contains);
                    index.Entries.Add(new KnownEntry
                    {
                        RecordId = Convert.ToInt64(row[0]), Path = row[2], Folder = row[3] == "1", Resource = resource,
                        OriginPath = row[5] ?? "", Categories = categories
                    });
                }
            }
        }

        private static bool IsResourceCategory(string category, HashSet<string> custom)
        {
            if (string.IsNullOrEmpty(category)) return false;
            return Array.IndexOf(CategoryRules.ResourceBuiltIn, category) >= 0 || category == "全部资源"
                || (custom != null && custom.Contains(category));
        }

        private static List<string> LoadCandidatePaths(ImportDuplicateContext context, HashSet<long> lengths, bool resource,
            CancellationToken cancel)
        {
            List<string> result = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (context != null && !string.IsNullOrEmpty(context.DatabaseFile) && lengths.Count > 0)
            {
                using (Sqlite db = new Sqlite(context.DatabaseFile, true))
                {
                    bool catalogInvalid = db.Scalar("SELECT value FROM metadata WHERE key='catalog_invalid'") == "1";
                    if (catalogInvalid)
                    {
                        foreach (string[] row in db.Query("SELECT DISTINCT a.path FROM added_items a"
                            + " WHERE a.resource=? AND a.folder=0 ORDER BY a.id", resource ? 1 : 0))
                            AddDistinct(result, seen, row[0]);
                    }
                    else
                    {
                        List<long> values = lengths.ToList();
                        for (int offset = 0; offset < values.Count; offset += 200)
                        {
                            cancel.ThrowIfCancellationRequested();
                            List<long> chunk = values.GetRange(offset, Math.Min(200, values.Count - offset));
                            List<string> marks = chunk.ConvertAll(value => "?");
                            object[] args = chunk.ConvertAll(value => (object)value).ToArray();
                            object[] queryArgs = new object[] { resource ? 1 : 0 }.Concat(args).ToArray();
                            foreach (string[] row in db.Query("SELECT DISTINCT a.path FROM added_items a LEFT JOIN catalog c ON c.item_id=a.item_id"
                                + " WHERE a.resource=? AND a.folder=0"
                                + " AND (c.size IS NULL OR c.size<0 OR c.size IN (" + string.Join(",", marks.ToArray()) + "))"
                                + " ORDER BY a.id", queryArgs))
                                AddDistinct(result, seen, row[0]);
                        }
                    }
                }
            }
            else if (context != null && context.MemoryEntries != null)
            {
                foreach (AddedEntry entry in context.MemoryEntries)
                    if (entry != null && entry.IsResource == resource && !entry.IsFolder) AddDistinct(result, seen, entry.Path);
            }
            return result;
        }

        private static void AddDistinct(List<string> list, HashSet<string> seen, string value)
        {
            if (!string.IsNullOrEmpty(value) && seen.Add(value)) list.Add(value);
        }

        private static Dictionary<long, Dictionary<string, List<Candidate>>> BuildCandidateIndex(List<string> paths,
            HashSet<string> selected, HashSet<long> lengths, CancellationToken cancel)
        {
            Dictionary<long, Dictionary<string, List<Candidate>>> result =
                new Dictionary<long, Dictionary<string, List<Candidate>>>();
            foreach (string path in paths)
            {
                cancel.ThrowIfCancellationRequested();
                if (selected.Contains(path)) continue;
                try
                {
                    FileInfo info = new FileInfo(path);
                    if (!info.Exists || (info.Attributes & FileAttributes.Directory) != 0 || !lengths.Contains(info.Length)) continue;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    if (!IsFileSystemError(ex)) throw;
                    continue;
                }
                FileState state;
                string reason;
                if (!TryGetHashes(path, false, out state, out reason, cancel) || !lengths.Contains(state.Length)) continue;
                Dictionary<string, List<Candidate>> byQuick;
                if (!result.TryGetValue(state.Length, out byQuick))
                    result[state.Length] = byQuick = new Dictionary<string, List<Candidate>>(StringComparer.Ordinal);
                List<Candidate> list;
                if (!byQuick.TryGetValue(state.Quick, out list)) byQuick[state.Quick] = list = new List<Candidate>();
                list.Add(new Candidate { Path = path, State = state });
            }
            return result;
        }
        private static bool TryGetHashes(string path, bool includeFull, out FileState state, out string reason, CancellationToken cancel)
        {
            state = null; reason = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                cancel.ThrowIfCancellationRequested();
                FileState before;
                if (!TrySnapshot(path, out before, out reason)) return false;
                string key = CacheKey(before);
                CachedHash cached = null;
                lock (cacheLock) hashCache.TryGetValue(key, out cached);
                string quick = cached == null ? null : cached.Quick;
                string full = cached == null ? null : cached.Full;
                try
                {
                    if (quick == null)
                    {
                        quick = QuickHash(path, cancel);
                        Remember(key, quick, full);
                    }
                    if (includeFull && full == null)
                    {
                        full = FileImport.HashFile(path, cancel);
                        Remember(key, quick, full);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    if (!IsFileSystemError(ex)) throw;
                    reason = "无法读取文件内容，未检查：" + ex.Message;
                    return false;
                }
                FileState after;
                string afterReason;
                if (TrySnapshot(path, out after, out afterReason) && SameFile(before, after))
                {
                    before.Quick = quick; before.Full = full; state = before; return true;
                }
                lock (cacheLock) hashCache.Remove(key);
                reason = "文件在检查期间发生变化，未检查。";
            }
            return false;
        }

        private static bool TrySnapshot(string path, out FileState state, out string reason)
        {
            state = null; reason = null;
            try
            {
                FileInfo info = new FileInfo(path);
                if (!info.Exists) { reason = "源文件不存在，未检查。"; return false; }
                FileAttributes attributes = info.Attributes;
                if ((attributes & FileAttributes.Directory) != 0) { reason = "文件夹不递归检查内容。"; return false; }
                if (MusicFileClassifier.IsCloudPlaceholder(attributes))
                { reason = "云端占位文件未检查，避免触发下载；默认正常导入。"; return false; }
                if (IsNetworkPath(path))
                { reason = "网络路径未检查；默认正常导入。"; return false; }
                if (FileImport.IsNameSurrogateReparsePoint(path, attributes))
                { reason = "链接或联接点未检查；默认正常导入。"; return false; }
                if (info.Length <= 0)
                { reason = "空文件未参与内容判重；默认正常导入。"; return false; }
                if (FileImport.HasAlternateDataStreams(path))
                { reason = "文件含备用数据流（ADS），未按主数据流判重；默认正常导入。"; return false; }
                state = new FileState { Path = path, Length = info.Length, Modified = info.LastWriteTimeUtc.Ticks,
                    Identity = FileImport.Identity(path) };
                if (string.IsNullOrEmpty(state.Identity))
                { reason = "无法确认文件身份，未检查。"; state = null; return false; }
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                if (!IsFileSystemError(ex)) throw;
                reason = "无法读取文件信息，未检查：" + ex.Message;
                state = null;
                return false;
            }
        }

        private static bool IsNetworkPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;
            try
            {
                string root = Path.GetPathRoot(path);
                return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network;
            }
            catch { return false; }
        }

        private static bool SameFile(FileState left, FileState right)
        {
            return left.Length == right.Length && left.Modified == right.Modified
                && string.Equals(left.Identity, right.Identity, StringComparison.OrdinalIgnoreCase);
        }

        private static string CacheKey(FileState state)
        {
            return state.Path.ToUpperInvariant() + "|" + state.Identity + "|" + state.Length + "|" + state.Modified;
        }

        private static void Remember(string key, string quick, string full)
        {
            lock (cacheLock)
            {
                if (hashCache.Count >= CacheLimit) hashCache.Clear();
                hashCache[key] = new CachedHash { Quick = quick, Full = full };
            }
        }

        private static string QuickHash(string path, CancellationToken cancel)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                1 << 16, FileOptions.SequentialScan))
            using (SHA256 sha = SHA256.Create())
            {
                byte[] buffer = new byte[QuickBytes];
                int wanted = (int)Math.Min(stream.Length, QuickBytes);
                int read = ReadAtMost(stream, buffer, wanted, cancel);
                sha.TransformBlock(buffer, 0, read, null, 0);
                if (stream.Length > QuickBytes)
                {
                    stream.Seek(-QuickBytes, SeekOrigin.End);
                    read = ReadAtMost(stream, buffer, QuickBytes, cancel);
                    sha.TransformBlock(buffer, 0, read, null, 0);
                }
                sha.TransformFinalBlock(buffer, 0, 0);
                return Convert.ToBase64String(sha.Hash);
            }
        }

        private static int ReadAtMost(Stream stream, byte[] buffer, int count, CancellationToken cancel)
        {
            int total = 0;
            while (total < count)
            {
                cancel.ThrowIfCancellationRequested();
                int read = stream.Read(buffer, total, count - total);
                if (read == 0) break;
                total += read;
            }
            return total;
        }

        private static bool IsFileSystemError(Exception ex)
        {
            return ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException
                || ex is ArgumentException || ex is NotSupportedException || ex is CryptographicException
                || ex is System.ComponentModel.Win32Exception;
        }
    }
}

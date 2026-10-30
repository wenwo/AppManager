using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace AppManager
{
    // Compile against both the pre-SQLite revision and current sources; fixtures stay in TEMP.
    internal static class DatabasePerformanceProbe
    {
        private static int count, sink;
        private static AppConfig config;
        private static string xml, backend;
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        public static int Main(string[] args)
        {
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
            try
            {
                string mode = args[0], folder = Path.GetFullPath(args[1]);
                count = int.Parse(args[2]);
                Directory.CreateDirectory(folder);
                Paths.DataDirOverride = folder; Paths.ConfigFileOverride = xml = Path.Combine(folder, "config.xml");
#if DATABASE
                backend = "SQLite";
#else
                backend = "XML";
#endif
#if DATABASE
                if (mode == "snapshot")
                {
                    using (Sqlite live = new Sqlite(Path.GetFullPath(args[3]), true)) live.Backup(ConfigDatabase.DatabasePath(xml));
                    config = Load(); if (config.LoadFailed) throw new Exception(config.DatabaseError);
                    if (!config.SaveTo(xml)) throw new Exception("Snapshot XML export failed");
                    Console.WriteLine("SNAPSHOT count=" + config.AddedItems.Count); return 0;
                }
#endif
                if (mode == "generate")
                {
                    config = new AppConfig(); config.ResourceCategories.Add("照片");
                    config.Collections.Add(new ResourceCollection { Parent = "照片", Name = "精选" });
                    bool dense = args.Length > 3 && args[3] == "dense";
                    for (int i = 0; i < count; i++)
                    {
                        string path = "Z:\\benchmark\\photo" + i.ToString("D6") + ".jpg";
                        config.AddedItems.Add(new AddedEntry { Path = path, Name = "photo" + i.ToString("D6"), Category = "照片", IsResource = true });
                        if (dense || i % 100 == 0) config.ItemCategories["add:" + path] = new List<string> { "col:照片/精选" };
                    }
                    if (!config.SaveTo(xml)) throw new Exception("Fixture export failed");
                    Console.WriteLine("FIXTURE count=" + count + "; dense=" + dense + "; xml_bytes=" + new FileInfo(xml).Length);
                    return 0;
                }
#if DATABASE
                if (mode == "migrate")
                {
                    double elapsed = Time(delegate { config = Load(); });
                    CheckConfig();
                    Console.WriteLine("MIGRATION count=" + count + "; ms=" + elapsed.ToString("F3", Invariant) + "; db_bytes=" + new FileInfo(config.Database.FilePath).Length);
                    return 0;
                }
#endif
                Metric("first_load", delegate { config = Load(); CheckConfig(); }, 1);
                Metric("reload", delegate { config = Load(); CheckConfig(); }, 3);
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                Console.WriteLine("MEMORY backend=" + backend + "; count=" + count + "; managed_bytes=" + GC.GetTotalMemory(false));
                if (mode != "user")
                {
                    Metric("first_category_page", Page, 1);
                    Metric("category_page", Page, 5);
                    Metric("path_lookup", Lookup, 5);
                }
                List<string> targets = mode == "user" ? config.AddedItems.Take(5).Select(a => "add:" + a.Path).ToList() : null;
                int targetIndex = 0;
                Metric("single_relation_save", delegate
                {
                    string key = targets == null ? "add:Z:\\benchmark\\photo" + (++sink).ToString("D6") + ".jpg" : targets[targetIndex++];
                    if (config.Favorites.Contains(key)) config.Favorites.Remove(key); else config.Favorites.Add(key);
#if DATABASE
                    if (!config.Save()) throw new Exception(config.DatabaseError);
#else
                    if (!config.SaveTo(xml)) throw new Exception("XML write failed");
#endif
                }, 5);
#if DATABASE
                using (Sqlite db = new Sqlite(config.Database.FilePath, true))
                {
                    long op = Convert.ToInt64(config.Database.History(1)[0][0]);
                    if (Convert.ToInt32(db.Scalar("SELECT count(*) FROM changes WHERE operation_id=?", op)) > 2) throw new Exception("Single relation rewrote unrelated data");
                }
#endif
                return 0;
            }
            catch (Exception ex) { Console.WriteLine("FAIL " + ex); return 1; }
            finally { Paths.DataDirOverride = null; Paths.ConfigFileOverride = null; }
        }

        private static AppConfig Load()
        {
#if DATABASE
            return ConfigDatabase.Load(xml);
#else
            return AppConfig.LoadFrom(xml);
#endif
        }
        private static void CheckConfig() { if (config.LoadFailed || config.AddedItems.Count != count) throw new Exception("Wrong fixture count"); }
        private static void Page()
        {
#if DATABASE
            CatalogPage page = config.Database.Page("照片", true, false, "", 200, null, true, false);
            if (page.Items.Count != 200 || page.Total != count || page.Items[0].Name != "photo000000") throw new Exception("Page mismatch");
#else
            // Equivalent in-memory category filtering/sorting; excludes disk probing, icons and painting.
            List<AddedEntry> entries = config.AddedItems.Where(e => e.IsResource && e.Category == "照片").OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Take(200).ToList();
            if (entries.Count != 200 || entries[0].Name != "photo000000") throw new Exception("Page mismatch");
#endif
        }
        private static void Lookup()
        {
            string path = "Z:\\benchmark\\photo" + (count - 1 - sink++).ToString("D6") + ".jpg";
#if DATABASE
            List<AddedEntry> entries = config.AddedItems.ByPath(path);
#else
            List<AddedEntry> entries = config.AddedItems.Where(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)).ToList();
#endif
            if (entries.Count != 1) throw new Exception("Lookup mismatch");
        }
        private static double Time(Action action)
        { Stopwatch timer = Stopwatch.StartNew(); action(); return timer.Elapsed.TotalMilliseconds; }
        private static void Metric(string name, Action action, int rounds)
        {
            List<double> values = new List<double>();
            for (int i = 0; i < rounds; i++) values.Add(Time(action));
            Console.WriteLine("METRIC," + backend + "," + count + "," + name + "," + string.Join(";", values.Select(v => v.ToString("F3", Invariant)).ToArray()));
        }
    }
}

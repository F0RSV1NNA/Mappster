using CASCLib;
using DBCD;
using DBCD.Providers;

namespace MapExtract;

/// Locates a DB2 by table name without a listfile: scan the fdid block where DB2s live,
/// keep the WDC files, and let DBCD's layout check say which one matches the definition.
public static class FindTable
{
    public static void Run(string table, int from = 1_330_000, int to = 1_400_000)
    {
        var casc = CASCHandler.OpenLocalStorage(@"E:\Games\World of Warcraft", "wow", null);
        ((WowRootHandler)casc.Root).SetFlags(LocaleFlags.enUS, false, false, createTree: false);
        string build = casc.Config.GetBuildInfoVariable("Version")!;
        var dbd = new GithubDBDProvider();

        Console.WriteLine($"scanning fdid {from:N0}..{to:N0} for {table}.db2 (build {build})");
        var candidates = new List<int>();
        var head = new byte[4];

        for (int fdid = from; fdid <= to; fdid++)
        {
            if (!casc.FileExists(fdid)) continue;
            try
            {
                using var s = casc.OpenFile(fdid);
                if (s.Read(head, 0, 4) != 4) continue;
                if (head[0] == 'W' && head[1] == 'D' && head[2] == 'C') candidates.Add(fdid);
            }
            catch { }
        }
        Console.WriteLine($"{candidates.Count:N0} WDC files in range; testing against the {table} definition");

        // a layout-hash collision will still load and still show the definition's column
        // names, so print real row content and judge by that
        foreach (string name in table.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            Console.WriteLine($"\n-- {name}");
            foreach (int fdid in candidates)
            {
                try
                {
                    var storage = new DBCD.DBCD(new One(casc, fdid), dbd).Load(name, build);
                    if (storage.Count == 0) continue;

                    // Layout hashes collide often, so score by whether the content is real:
                    // a genuine FileDataID column points at files that exist in storage.
                    string? fdCol = storage.AvailableColumns.FirstOrDefault(c => c == "FileDataID");
                    string verdict = "";
                    if (fdCol != null)
                    {
                        int ok = 0, n = 0;
                        foreach (DBCDRow r in storage.Values.Take(200))
                        {
                            int v = Convert.ToInt32(r[fdCol]);
                            if (v <= 0) continue;
                            n++; if (casc.FileExists(v)) ok++;
                        }
                        verdict = n == 0 ? "  (no fdids)" : $"  fdids valid {100.0 * ok / n:F0}% of {n}";
                        if (n > 0 && ok * 2 < n) continue;      // mostly bogus — not this table
                    }
                    Console.WriteLine($"  fdid {fdid,-9} {storage.Count,7:N0} rows{verdict}");
                }
                catch { }
            }
        }
    }

    /// Print a table's shape and first rows, resolved by name through the listfile.
    public static void Dump(string table, int rows = 6)
    {
        var casc = CASCHandler.OpenLocalStorage(@"E:\Games\World of Warcraft", "wow", null);
        ((WowRootHandler)casc.Root).SetFlags(LocaleFlags.enUS, false, false, createTree: false);
        string build = casc.Config.GetBuildInfoVariable("Version")!;

        int fdid = Names.Db2(table);
        if (fdid == 0) { Console.WriteLine($"{table}: not in listfile index"); return; }
        if (!casc.FileExists(fdid)) { Console.WriteLine($"{table} (fdid {fdid}): not installed locally"); return; }

        var storage = new DBCD.DBCD(new One(casc, fdid), new GithubDBDProvider()).Load(table, build);
        Console.WriteLine($"{table}  fdid {fdid}  {storage.Count:N0} rows");
        Console.WriteLine($"  columns: {string.Join(", ", storage.AvailableColumns)}");
        foreach (DBCDRow r in storage.Values.Take(rows))
            Console.WriteLine("    " + string.Join("  ", storage.AvailableColumns.Take(7)
                .Select(c => { try { return $"{c}={Fmt(r[c])}"; } catch { return c + "=?"; } })));

        static string Fmt(object? v) => v switch
        {
            null => "-",
            Array a => "[" + string.Join(",", a.Cast<object>().Take(4).Select(x => x is float f ? f.ToString("F1") : x.ToString())) + "]",
            float f => f.ToString("F1"),
            _ => v.ToString() ?? "-",
        };
    }

    /// What kind of files does a candidate table's FileDataID column actually point at?
    public static void Inspect(int fdid, string table)
    {
        var casc = CASCHandler.OpenLocalStorage(@"E:\Games\World of Warcraft", "wow", null);
        ((WowRootHandler)casc.Root).SetFlags(LocaleFlags.enUS, false, false, createTree: false);
        string build = casc.Config.GetBuildInfoVariable("Version")!;
        var storage = new DBCD.DBCD(new One(casc, fdid), new GithubDBDProvider()).Load(table, build);

        Console.WriteLine($"fdid {fdid} as {table}: {storage.Count:N0} rows");
        Console.WriteLine($"columns: {string.Join(", ", storage.AvailableColumns)}\n");

        var kinds = new Dictionary<string, int>();
        int shown = 0;
        foreach (DBCDRow r in storage.Values)
        {
            int f = Convert.ToInt32(r["FileDataID"]);
            if (f <= 0 || !casc.FileExists(f)) continue;
            string kind;
            try
            {
                using var s = casc.OpenFile(f);
                var h = new byte[4];
                s.ReadExactly(h);
                kind = System.Text.Encoding.ASCII.GetString(h);
            }
            catch { kind = "(unreadable)"; }
            kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
            if (shown++ < 8) Console.WriteLine($"  id {r.ID,-8} fdid {f,-9} magic '{kind}'");
        }
        Console.WriteLine("\nfile kinds referenced:");
        foreach (var k in kinds.OrderByDescending(k => k.Value))
            Console.WriteLine($"  '{k.Key}'  {k.Value:N0}");
    }

    /// Sanity-check the tile -> zone name lookup on known ground.
    public static void Zones()
    {
        var casc = CASCHandler.OpenLocalStorage(@"E:\Games\World of Warcraft", "wow", null);
        ((WowRootHandler)casc.Root).SetFlags(LocaleFlags.enUS, false, false, createTree: false);
        string build = casc.Config.GetBuildInfoVariable("Version")!;
        var dbd = new GithubDBDProvider();

        var areas = new DBCD.DBCD(new One(casc, 1353545), dbd).Load("AreaTable", build);
        var names = areas.Values.ToDictionary(r => r.ID, r => r["AreaName_lang"]?.ToString() ?? "");
        var maps = new DBCD.DBCD(new One(casc, 1349477), dbd).Load("Map", build);

        using var w = casc.OpenFile(Convert.ToInt32(maps[0]["WdtFileDataID"]));
        var tiles = Wdt.ReadTiles(w).ToDictionary(t => (t.X, t.Y));

        Console.WriteLine($"{names.Count:N0} area names loaded\n");
        foreach (var (x, y, expect) in new[]
        {
            (48, 30, "Stormwind"), (41, 33, "Ironforge"), (58, 31, "Booty Bay"),
            (28, 31, "Undercity"), (49, 31, "Elwynn"), (47, 30, "Westfall"),
        })
        {
            if (!tiles.TryGetValue((x, y), out var t)) { Console.WriteLine($"  [{x},{y}] missing"); continue; }
            using var s = casc.OpenFile((int)t.Root);
            int area = Adt.DominantAreaId(s);
            Console.WriteLine($"  [{x,2},{y,2}]  area {area,-6} {(names.TryGetValue(area, out var n) ? n : "?"),-28} (near {expect})");
        }
    }

    sealed class One(CASCHandler casc, int fdid) : IDBCProvider
    {
        public Stream StreamForTableName(string tableName, string build)
        {
            var ms = new MemoryStream();
            using (var s = casc.OpenFile(fdid)) s.CopyTo(ms);
            ms.Position = 0; return ms;
        }
    }
}

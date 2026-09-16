using CASCLib;
using DBCD;
using DBCD.Providers;

namespace MapExtract;

public static class Cli
{
    // usage: MapExtract --cli <installPath> <product> <mapId> <outDir> [maxTiles] [--yup] [--solid-only]
    public static void Run(string[] args)
    {
        string install = args.ElementAtOrDefault(0) ?? @"E:\Games\World of Warcraft";
        string product = args.ElementAtOrDefault(1) ?? "wow";
        int mapId      = int.Parse(args.ElementAtOrDefault(2) ?? "0");
        string outDir  = args.ElementAtOrDefault(3) ?? Path.Combine(AppContext.BaseDirectory, "out");
        int maxTiles   = int.Parse(args.ElementAtOrDefault(4) ?? "8");
        bool yUp       = args.Contains("--yup");
        bool solidOnly = args.Contains("--solid-only");

        int mapFdid    = int.Parse(Environment.GetEnvironmentVariable("MAPDB2_FDID") ?? "1349477");
        int liquidFdid = int.Parse(Environment.GetEnvironmentVariable("LIQUIDTYPE_FDID") ?? "1371380");

        var casc = CASCHandler.OpenLocalStorage(install, product, null);
        ((WowRootHandler)casc.Root).SetFlags(LocaleFlags.enUS, false, false, createTree: false);
        string build = casc.Config.GetBuildInfoVariable("Version")!;

        var dbd = new GithubDBDProvider();
        var maps = new DBCD.DBCD(new Db2(casc, mapFdid), dbd).Load("Map", build);
        var liquidTypes = new DBCD.DBCD(new Db2(casc, liquidFdid), dbd).Load("LiquidType", build);

        var soundBank = liquidTypes.Values.ToDictionary(r => (ushort)r.ID, r => (LiquidClass)Convert.ToByte(r["SoundBank"]));
        LiquidClass Classify(ushort id) => soundBank.TryGetValue(id, out var c) ? c : LiquidClass.Unknown;

        var row = maps[mapId];
        Console.WriteLine($"{product} {build}  map {mapId} \"{row["MapName_lang"]}\"  " +
                          $"{(yUp ? "y-up" : "z-up")}{(solidOnly ? ", solid only" : "")}");
        Directory.CreateDirectory(outDir);

        using var w = casc.OpenFile(Convert.ToInt32(row["WdtFileDataID"]));
        var tiles = Wdt.ReadTiles(w);
        Console.WriteLine($"{tiles.Count} populated tiles, extracting {Math.Min(maxTiles, tiles.Count)}\n");

        var ex = new Extractor(casc, Classify);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long solid = 0;

        foreach (var t in tiles.Skip(tiles.Count / 3).Take(maxTiles))
        {
            var soup = ex.BuildTile(t);
            string name = $"map{mapId}_{t.X}_{t.Y}";
            Extractor.WriteObj(soup, Path.Combine(outDir, name + ".obj"), yUp, includeLiquid: !solidOnly);
            Extractor.WriteBinary(soup, Path.Combine(outDir, name + ".wmesh"), yUp);
            solid += soup.TriCount;

            Console.WriteLine($"  {name,-20} {soup.TriCount,9:N0} solid  {soup.LiquidTris,8:N0} liquid  " +
                              string.Join(" ", soup.LiquidTriClass.GroupBy(c => c).OrderBy(g => g.Key)
                                                  .Select(g => $"{g.Key}={g.Count():N0}")));
        }

        Console.WriteLine($"\n{solid:N0} solid tris in {sw.Elapsed.TotalSeconds:F1}s -> {outDir}");
    }

    sealed class Db2(CASCHandler casc, int fdid) : IDBCProvider
    {
        public Stream StreamForTableName(string tableName, string build)
        {
            var ms = new MemoryStream();
            using (var s = casc.OpenFile(fdid)) s.CopyTo(ms);
            ms.Position = 0; return ms;
        }
    }
}

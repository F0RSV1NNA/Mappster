using CASCLib;
using DBCD;
using DBCD.Providers;

namespace MapExtract.Gui;

public record MapInfo(int Id, string Name, string Directory, int WdtFdid, int Expansion, int InstanceType)
{
    public int TileCount = -1;        // -1 until the background pass reaches it
    public bool GlobalWmo;            // whole map is one WMO: most classic dungeons
}

/// Owns the open CASC storage and everything read out of it. All storage access is
/// serialised: CASCHandler is shared by the UI thread and the export/scan workers.
public sealed class Session : IDisposable
{
    readonly object _casc = new();
    CASCHandler? _handler;
    Extractor? _extractor;

    public string Build { get; private set; } = "";
    public string Product { get; private set; } = "";
    public List<MapInfo> Maps { get; private set; } = [];
    public bool Ready => _handler != null;

    // resolved from the listfile, with the known-good ids as fallback
    public static int MapFdid => Names.Db2("Map", 1349477);
    public static int LiquidFdid => Names.Db2("LiquidType", 1371380);
    public static int AreaFdid => Names.Db2("AreaTable", 1353545);

    /// areaId -> zone name, for labelling tiles.
    public Dictionary<int, string> AreaNames { get; private set; } = [];

    /// Products listed in the install's .build.info, e.g. wow, wow_classic_era.
    public static List<string> Products(string install)
    {
        var path = Path.Combine(install, ".build.info");
        if (!File.Exists(path)) return [];
        var lines = File.ReadAllLines(path);
        if (lines.Length < 2) return [];

        var head = lines[0].Split('|').Select(h => h.Split('!')[0]).ToList();
        int col = head.IndexOf("Product");
        if (col < 0) return [];

        return lines.Skip(1)
                    .Select(l => l.Split('|'))
                    .Where(p => p.Length > col)
                    .Select(p => p[col])
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Distinct().ToList();
    }

    public void Open(string install, string product)
    {
        Close();
        lock (_casc)
        {
            _handler = CASCHandler.OpenLocalStorage(install, product, null);
            ((WowRootHandler)_handler.Root).SetFlags(LocaleFlags.enUS, false, false, createTree: false);
            Build = _handler.Config.GetBuildInfoVariable("Version") ?? "?";
            Product = product;

            var dbd = new GithubDBDProvider();
            var maps = new DBCD.DBCD(new Db2(_handler, MapFdid), dbd).Load("Map", Build);
            var liquid = new DBCD.DBCD(new Db2(_handler, LiquidFdid), dbd).Load("LiquidType", Build);

            try
            {
                var areas = new DBCD.DBCD(new Db2(_handler, AreaFdid), dbd).Load("AreaTable", Build);
                AreaNames = areas.Values.ToDictionary(r => r.ID, r => r["AreaName_lang"]?.ToString() ?? "");
            }
            catch { AreaNames = []; }   // zone names are a nicety, not a dependency

            var bank = liquid.Values.ToDictionary(r => (ushort)r.ID, r => (LiquidClass)Convert.ToByte(r["SoundBank"]));
            _extractor = new Extractor(_handler, id => bank.TryGetValue(id, out var c) ? c : LiquidClass.Unknown);

            Maps = maps.Values
                .Select(r => new MapInfo(r.ID, r["MapName_lang"].ToString() ?? "", r["Directory"].ToString() ?? "",
                                         Convert.ToInt32(r["WdtFileDataID"]), Convert.ToInt32(r["ExpansionID"]),
                                         Convert.ToInt32(r["InstanceType"])))
                .Where(m => m.WdtFdid != 0)
                .OrderBy(m => m.Id).ToList();
        }
    }

    public void Close()
    {
        lock (_casc) { _handler = null; _extractor = null; Maps = []; Build = ""; }
    }

    public List<TileFiles> Tiles(int wdtFdid)
    {
        lock (_casc)
        {
            if (_handler == null || !_handler.FileExists(wdtFdid)) return [];
            try { using var s = _handler.OpenFile(wdtFdid); return Wdt.ReadTiles(s); }
            catch { return []; }
        }
    }

    public Extractor.Soup? BuildTile(TileFiles tile, int detailStep = 1, bool objects = true)
    {
        lock (_casc)
        {
            if (_extractor == null) return null;
            try { return _extractor.BuildTile(tile, detailStep, objects); }
            catch { return null; }
        }
    }

    /// The single WMO that makes up a tile-less map, if it has one.
    public WmoPlacement? GlobalWmo(int wdtFdid)
    {
        lock (_casc)
        {
            if (_handler == null || !_handler.FileExists(wdtFdid)) return null;
            try
            {
                using var s = _handler.OpenFile(wdtFdid);
                var g = Wdt.ReadGlobalWmo(s);
                return g != null && _handler.FileExists((int)g.NameId) ? g : null;
            }
            catch { return null; }
        }
    }

    /// A transport model in its own space: placed at the origin, unrotated.
    public Extractor.Soup? BuildTransport(int modelFdid)
    {
        lock (_casc)
        {
            if (_extractor == null || _handler == null || !_handler.FileExists(modelFdid)) return null;
            var at0 = new WmoPlacement((uint)modelFdid, System.Numerics.Vector3.Zero,
                                       System.Numerics.Vector3.Zero, System.Numerics.Vector3.Zero,
                                       System.Numerics.Vector3.Zero, 0, 1f);
            try { return _extractor.BuildGlobalWmo(at0); }
            catch { return null; }
        }
    }

    public Extractor.Soup? BuildGlobal(WmoPlacement m)
    {
        lock (_casc)
        {
            if (_extractor == null) return null;
            try { return _extractor.BuildGlobalWmo(m); }
            catch { return null; }
        }
    }

    /// Zone name for one tile, from the area id most of its chunks carry.
    public string TileZone(TileFiles tile)
    {
        lock (_casc)
        {
            if (_handler == null || !_handler.FileExists((int)tile.Root)) return "";
            try
            {
                using var s = _handler.OpenFile((int)tile.Root);
                int area = Adt.DominantAreaId(s);
                return AreaNames.TryGetValue(area, out var n) ? n : "";
            }
            catch { return ""; }
        }
    }

    /// Fills TileCount for every map, newest-looking first, so the list populates while browsing.
    public void ScanTileCounts(CancellationToken ct)
    {
        foreach (var m in Maps.ToList())
        {
            if (ct.IsCancellationRequested) return;
            if (m.TileCount >= 0) continue;
            m.TileCount = Tiles(m.WdtFdid).Count;
            if (m.TileCount == 0) m.GlobalWmo = GlobalWmo(m.WdtFdid) != null;
        }
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

    public void Dispose() => Close();
}

/// Background export of one map's tiles, with progress the UI can poll.
public sealed class ExportJob
{
    public volatile int Done, Total;
    public volatile string Status = "";
    public volatile bool Running, Cancelled;
    public string MapName = "";

    readonly CancellationTokenSource _cts = new();
    public void Cancel() { Cancelled = true; _cts.Cancel(); }

    public static ExportJob Start(Session s, MapInfo map, string outDir, bool yUp, bool writeObj)
    {
        var job = new ExportJob { Running = true, MapName = map.Name, Status = "reading WDT…" };
        Task.Run(() =>
        {
            try
            {
                var tiles = s.Tiles(map.WdtFdid);
                Directory.CreateDirectory(outDir);

                // a tile-less map is one WMO — export it as a single file
                if (tiles.Count == 0)
                {
                    job.Total = 1;
                    var g = s.GlobalWmo(map.WdtFdid);
                    var gs = g == null ? null : s.BuildGlobal(g);
                    if (gs != null)
                    {
                        string stem = Path.Combine(outDir, $"map{map.Id}_global");
                        Extractor.WriteBinary(gs, stem + ".wmesh", yUp);
                        if (writeObj) Extractor.WriteObj(gs, stem + ".obj", yUp);
                        job.Done = 1;
                        job.Status = $"done — global WMO, {gs.TriCount:N0} tris";
                    }
                    else job.Status = "no geometry for this map";
                    return;
                }

                job.Total = tiles.Count;
                foreach (var t in tiles)
                {
                    if (job._cts.IsCancellationRequested) { job.Status = "cancelled"; break; }
                    var soup = s.BuildTile(t);
                    if (soup != null)
                    {
                        string stem = Path.Combine(outDir, $"map{map.Id}_{t.X}_{t.Y}");
                        Extractor.WriteBinary(soup, stem + ".wmesh", yUp);
                        if (writeObj) Extractor.WriteObj(soup, stem + ".obj", yUp);
                    }
                    job.Done++;
                    job.Status = $"tile {t.X},{t.Y}";
                }
                if (!job._cts.IsCancellationRequested) job.Status = $"done — {job.Done} tiles";
            }
            catch (Exception e) { job.Status = "failed: " + e.Message; }
            finally { job.Running = false; }
        });
        return job;
    }
}

using System.Numerics;
using CASCLib;
using DBCD;
using DBCD.Providers;

namespace MapExtract;

/// Measurement passes that inform what to filter and how to transform.
public static class Analyze
{
    static readonly int[] Maps = [0, 1, 2444, 2552, 2912, 2601, 2822];

    static (CASCHandler Casc, IDBCDStorage MapDb) Open()
    {
        Gui.Session.EnsureCdnConfig(@"E:\Games\World of Warcraft", "wow");
        var casc = CASCHandler.OpenLocalStorage(@"E:\Games\World of Warcraft", "wow", null);
        ((WowRootHandler)casc.Root).SetFlags(LocaleFlags.enUS, false, false, createTree: false);
        string build = casc.Config.GetBuildInfoVariable("Version")!;
        var maps = new DBCD.DBCD(new One(casc, 1349477), new GithubDBDProvider()).Load("Map", build);
        return (casc, maps);
    }

    static IEnumerable<TileFiles> TilesOf(CASCHandler casc, IDBCDStorage maps, int mapId)
    {
        if (!maps.TryGetValue(mapId, out DBCDRow? row) || row == null) yield break;
        int wdt = Convert.ToInt32(row["WdtFileDataID"]);
        if (wdt == 0 || !casc.FileExists(wdt)) yield break;
        List<TileFiles> tiles;
        using (var s = casc.OpenFile(wdt)) tiles = Wdt.ReadTiles(s);
        foreach (var t in tiles) yield return t;
    }

    // ---- 1. MOGP flag census -------------------------------------------
    public static void Groups()
    {
        var (casc, maps) = Open();
        var seen = new HashSet<uint>();
        var groupCount = new Dictionary<int, int>();
        var groupTris = new Dictionary<int, long>();
        int totalGroups = 0; long totalTris = 0;

        foreach (int mapId in Maps)
            foreach (var t in TilesOf(casc, maps, mapId).Where(t => t.Obj0 != 0).Take(120))
            {
                List<WmoPlacement> wmos;
                using (var os = casc.OpenFile((int)t.Obj0)) (_, wmos, _) = Obj0.Read(os);

                foreach (var m in wmos)
                {
                    if (!seen.Add(m.NameId) || !casc.FileExists((int)m.NameId)) continue;
                    Wmo.Root root;
                    using (var rs = casc.OpenFile((int)m.NameId)) root = Wmo.ReadRoot(rs);

                    foreach (uint g in root.GroupIds)
                    {
                        if (g == 0 || !casc.FileExists((int)g)) continue;
                        Wmo.GroupData gd;
                        using (var gs = casc.OpenFile((int)g)) gd = Wmo.ReadGroup(gs);

                        totalGroups++; totalTris += gd.Solid.TriCount;
                        for (int bit = 0; bit < 32; bit++)
                            if ((gd.Flags & (1u << bit)) != 0)
                            {
                                groupCount[bit] = groupCount.GetValueOrDefault(bit) + 1;
                                groupTris[bit] = groupTris.GetValueOrDefault(bit) + gd.Solid.TriCount;
                            }
                    }
                }
            }

        Console.WriteLine($"{seen.Count:N0} distinct WMOs, {totalGroups:N0} groups, {totalTris:N0} collision tris\n");
        Console.WriteLine($"{"bit",-6} {"flag",-14} {"groups",9} {"% grp",7} {"tris",12} {"% tris",8}");
        foreach (var bit in groupCount.Keys.OrderBy(b => b))
        {
            uint mask = 1u << bit;
            string name = Enum.IsDefined(typeof(Wmo.GroupFlags), mask) ? ((Wmo.GroupFlags)mask).ToString() : "";
            Console.WriteLine($"0x{mask,-8:X} {name,-14} {groupCount[bit],9:N0} {100.0 * groupCount[bit] / totalGroups,6:F1}% " +
                              $"{groupTris[bit],12:N0} {100.0 * groupTris[bit] / totalTris,7:F2}%");
        }
    }

    // ---- 2. tilted WMO placements --------------------------------------
    public static void Tilted()
    {
        var (casc, maps) = Open();
        var samples = new List<WmoPlacement>();

        foreach (int mapId in Maps)
        {
            foreach (var t in TilesOf(casc, maps, mapId).Where(t => t.Obj0 != 0))
            {
                List<WmoPlacement> wmos;
                using (var os = casc.OpenFile((int)t.Obj0)) (_, wmos, _) = Obj0.Read(os);
                foreach (var m in wmos)
                    if ((MathF.Abs(m.RotDeg.X) > 3f || MathF.Abs(m.RotDeg.Z) > 3f) && casc.FileExists((int)m.NameId))
                        samples.Add(m);
                if (samples.Count > 4000) break;
            }
            if (samples.Count > 4000) break;
        }

        Console.WriteLine($"{samples.Count:N0} WMO placements with |rot.X| or |rot.Z| > 3 degrees");
        if (samples.Count == 0) { Console.WriteLine("  nothing tilted found — ordering stays undetermined"); return; }
        Console.WriteLine($"  rot.X range {samples.Min(s => s.RotDeg.X):F1} .. {samples.Max(s => s.RotDeg.X):F1}");
        Console.WriteLine($"  rot.Z range {samples.Min(s => s.RotDeg.Z):F1} .. {samples.Max(s => s.RotDeg.Z):F1}\n");

        static Matrix4x4 Rx(float d) => Matrix4x4.CreateRotationX(d * MathF.PI / 180f);
        static Matrix4x4 Ry(float d) => Matrix4x4.CreateRotationY(d * MathF.PI / 180f);
        static Matrix4x4 Rz(float d) => Matrix4x4.CreateRotationZ(d * MathF.PI / 180f);

        // At zero rotation MODF extents equal the transformed MOHD box exactly, so the
        // right transform should score ~0. Test axis *assignment* as well as order:
        // rot.Y is established, but which placement axis rot.X and rot.Z drive is not.
        (string Name, Func<Vector3, Matrix4x4> M)[] orders =
        [
            ("Rz(z)Ry(y)Rx(x)",   r => Rz(r.Z) * Ry(r.Y) * Rx(r.X)),
            ("Rx(x)Ry(y)Rz(z)",   r => Rx(r.X) * Ry(r.Y) * Rz(r.Z)),
            ("Rz(x)Ry(y)Rx(z)",   r => Rz(r.X) * Ry(r.Y) * Rx(r.Z)),
            ("Rx(z)Ry(y)Rz(x)",   r => Rx(r.Z) * Ry(r.Y) * Rz(r.X)),
            ("Ry(y)Rz(x)Rx(z)",   r => Ry(r.Y) * Rz(r.X) * Rx(r.Z)),
            ("Ry(y)Rx(z)Rz(x)",   r => Ry(r.Y) * Rx(r.Z) * Rz(r.X)),
            ("Rz(-x)Ry(y)Rx(-z)", r => Rz(-r.X) * Ry(r.Y) * Rx(-r.Z)),
            ("Rx(-z)Ry(y)Rz(-x)", r => Rx(-r.Z) * Ry(r.Y) * Rz(-r.X)),
            ("Ry only",           r => Ry(r.Y)),
        ];
        // "inside the AABB" cannot separate these — a cube's box fits any rotation.
        // Score how closely the transformed box *matches* the stored one instead.
        var err = new double[orders.Length];
        var errBox = new double[orders.Length];
        long tested = 0;
        int used = 0;
        var cache = new Dictionary<uint, (List<Vector3> Verts, Vector3 Lo, Vector3 Hi)>();

        foreach (var m in samples)
        {
            if (!cache.TryGetValue(m.NameId, out var entry))
            {
                var vs = new List<Vector3>();
                Wmo.Root root;
                using (var rs = casc.OpenFile((int)m.NameId)) root = Wmo.ReadRoot(rs);
                foreach (uint g in root.GroupIds.Take(8))
                {
                    if (g == 0 || !casc.FileExists((int)g)) continue;
                    using var gs = casc.OpenFile((int)g);
                    vs.AddRange(Wmo.ReadGroup(gs).Solid.Verts);
                }
                entry = (vs, root.BoxMin, root.BoxMax);
                cache[m.NameId] = entry;
            }
            var verts = entry.Verts;
            if (verts.Count < 8) continue;

            // the 8 corners of the WMO's own MOHD bounding box
            var corners = new Vector3[8];
            for (int c = 0; c < 8; c++)
                corners[c] = new Vector3((c & 1) == 0 ? entry.Lo.X : entry.Hi.X,
                                         (c & 2) == 0 ? entry.Lo.Y : entry.Hi.Y,
                                         (c & 4) == 0 ? entry.Lo.Z : entry.Hi.Z);

            var size = m.ExtMax - m.ExtMin;
            if (size.X <= 0 || size.Y <= 0 || size.Z <= 0 || size.Length() > 5000) continue;
            var pad = size * 0.02f;
            used++;

            // a bounding box needs the extremes, so this must see every vertex
            for (int i = 0; i < orders.Length; i++)
            {
                var mat = orders[i].M(m.RotDeg);
                var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
                foreach (var v in verts)
                {
                    var p = Vector3.Transform(new Vector3(v.Y, v.Z, v.X) * m.Scale, mat) + m.Pos;
                    lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p);
                }
                var d = Vector3.Abs(lo - m.ExtMin) + Vector3.Abs(hi - m.ExtMax);
                err[i] += (d.X / size.X + d.Y / size.Y + d.Z / size.Z) / 6.0;

                var blo = new Vector3(float.MaxValue); var bhi = new Vector3(float.MinValue);
                foreach (var c in corners)
                {
                    var p = Vector3.Transform(new Vector3(c.Y, c.Z, c.X) * m.Scale, mat) + m.Pos;
                    blo = Vector3.Min(blo, p); bhi = Vector3.Max(bhi, p);
                }
                var db = Vector3.Abs(blo - m.ExtMin) + Vector3.Abs(bhi - m.ExtMax);
                errBox[i] += (db.X / size.X + db.Y / size.Y + db.Z / size.Z) / 6.0;
            }
            tested += verts.Count;
        }

        Console.WriteLine($"{used:N0} usable placements, {tested:N0} vertices\n");
        Console.WriteLine($"  {"order",-10} {"from verts",12} {"from MOHD box",14}   (0 = exact match to MODF extents)");
        foreach (var (o, i) in orders.Select((o, i) => (o, i)).OrderBy(x => errBox[x.i]))
            Console.WriteLine($"  {o.Name,-10} {err[i] / Math.Max(used, 1),12:F4} {errBox[i] / Math.Max(used, 1),14:F4}");

        // The oracle cannot separate the top two, so measure what the choice costs:
        // how far apart do the two orderings actually put the geometry?
        double sumDist = 0, worst = 0; long pts = 0;
        int winA = 0, winB = 0, winAfar = 0, winBfar = 0, far = 0;

        foreach (var m in samples)
        {
            if (!cache.TryGetValue(m.NameId, out var e) || e.Verts.Count < 8) continue;
            var size = m.ExtMax - m.ExtMin;
            if (size.X <= 0 || size.Y <= 0 || size.Z <= 0 || size.Length() > 5000) continue;

            var a = Rz(m.RotDeg.Z) * Ry(m.RotDeg.Y) * Rx(m.RotDeg.X);
            var b = Rx(m.RotDeg.X) * Ry(m.RotDeg.Y) * Rz(m.RotDeg.Z);

            double d2 = 0; int n2 = 0;
            foreach (var v in e.Verts.Where((_, i) => i % 23 == 0))
            {
                var model = new Vector3(v.Y, v.Z, v.X) * m.Scale;
                float d = Vector3.Distance(Vector3.Transform(model, a), Vector3.Transform(model, b));
                sumDist += d; worst = Math.Max(worst, d); pts++; d2 += d; n2++;
            }
            bool separated = n2 > 0 && d2 / n2 > 5.0;   // orderings disagree meaningfully here
            if (separated) far++;

            // score each ordering on this one placement
            double Err(Matrix4x4 mat)
            {
                var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
                for (int c = 0; c < 8; c++)
                {
                    var corner = new Vector3((c & 1) == 0 ? e.Lo.X : e.Hi.X,
                                             (c & 2) == 0 ? e.Lo.Y : e.Hi.Y,
                                             (c & 4) == 0 ? e.Lo.Z : e.Hi.Z);
                    var p = Vector3.Transform(new Vector3(corner.Y, corner.Z, corner.X) * m.Scale, mat) + m.Pos;
                    lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p);
                }
                var dd = Vector3.Abs(lo - m.ExtMin) + Vector3.Abs(hi - m.ExtMax);
                return dd.X / size.X + dd.Y / size.Y + dd.Z / size.Z;
            }

            double ea = Err(a), eb = Err(b);
            if (Math.Abs(ea - eb) < 1e-4) continue;
            if (ea < eb) { winA++; if (separated) winAfar++; } else { winB++; if (separated) winBfar++; }
        }

        Console.WriteLine($"\n  Rz*Ry*Rx vs Rx*Ry*Rz displacement over {pts:N0} verts:" +
                          $"  mean {sumDist / Math.Max(pts, 1):F2} yd,  worst {worst:F1} yd");
        Console.WriteLine($"  per-placement wins (lower MOHD-box error):");
        Console.WriteLine($"    all {winA + winB:N0} decided   Rz*Ry*Rx {winA,6:N0} ({100.0 * winA / Math.Max(winA + winB, 1):F1}%)" +
                          $"   Rx*Ry*Rz {winB,6:N0} ({100.0 * winB / Math.Max(winA + winB, 1):F1}%)");
        Console.WriteLine($"    of the {far:N0} where they disagree by >5yd:" +
                          $"   Rz*Ry*Rx {winAfar,6:N0} ({100.0 * winAfar / Math.Max(winAfar + winBfar, 1):F1}%)" +
                          $"   Rx*Ry*Rz {winBfar,6:N0} ({100.0 * winBfar / Math.Max(winAfar + winBfar, 1):F1}%)");

        // A coin flip means the file data cannot settle this. Hand over the placements
        // where the two orderings differ most, so they can be checked against the game.
        Console.WriteLine("\n  worst disagreements — fly to these and compare with the client:");
        var ranked = samples
            .Where(m => cache.TryGetValue(m.NameId, out var e) && e.Verts.Count >= 8)
            .Select(m =>
            {
                var e = cache[m.NameId];
                var a = Rz(m.RotDeg.Z) * Ry(m.RotDeg.Y) * Rx(m.RotDeg.X);
                var b = Rx(m.RotDeg.X) * Ry(m.RotDeg.Y) * Rz(m.RotDeg.Z);
                double d = e.Verts.Where((_, i) => i % 37 == 0)
                    .Select(v => new Vector3(v.Y, v.Z, v.X) * m.Scale)
                    .Select(mv => (double)Vector3.Distance(Vector3.Transform(mv, a), Vector3.Transform(mv, b)))
                    .DefaultIfEmpty(0).Average();
                return (m, d);
            })
            .OrderByDescending(x => x.d).Take(6);

        foreach (var (m, d) in ranked)
        {
            var world = new Vector3(Terrain.Origin - m.Pos.Z, Terrain.Origin - m.Pos.X, m.Pos.Y);
            Console.WriteLine($"    wmo {m.NameId,-9} world {world.X,8:F0},{world.Y,8:F0},{world.Z,7:F0}  " +
                              $"tile [{(int)MathF.Floor(32 - world.X / 533.33333f),2},{(int)MathF.Floor(32 - world.Y / 533.33333f),2}]  " +
                              $"rot({m.RotDeg.X,6:F1},{m.RotDeg.Y,6:F1},{m.RotDeg.Z,6:F1})  differ {d,6:F1} yd");
        }
    }

    // ---- 3. where the triangles actually come from ----------------------
    public static void Polys()
    {
        var (casc, maps) = Open();
        var m2Tris = new List<int>();
        int m2None = 0;
        var wmoGroupTris = new List<int>();
        var seenM2 = new HashSet<uint>();
        var seenWmo = new HashSet<uint>();

        foreach (var t in TilesOf(casc, maps, 0).Where(t => t.Obj0 != 0).Skip(400).Take(150))
        {
            List<DoodadPlacement> dd; List<WmoPlacement> wm;
            using (var os = casc.OpenFile((int)t.Obj0)) (dd, wm, _) = Obj0.Read(os);

            foreach (var d in dd)
            {
                if (!seenM2.Add(d.NameId) || !casc.FileExists((int)d.NameId)) continue;
                using var s = casc.OpenFile((int)d.NameId);
                int n = M2.ReadCollision(s).TriCount;
                if (n == 0) m2None++; else m2Tris.Add(n);
            }
            foreach (var m in wm)
            {
                if (!seenWmo.Add(m.NameId) || !casc.FileExists((int)m.NameId)) continue;
                Wmo.Root root;
                using (var rs = casc.OpenFile((int)m.NameId)) root = Wmo.ReadRoot(rs);
                foreach (uint g in root.GroupIds)
                {
                    if (g == 0 || !casc.FileExists((int)g)) continue;
                    using var gs = casc.OpenFile((int)g);
                    wmoGroupTris.Add(Wmo.ReadGroup(gs).Solid.TriCount);
                }
            }
        }

        Console.WriteLine("TERRAIN");
        Console.WriteLine("  MCVT is 145 verts per 33.3yd chunk — 8x8 cells, 4 tris each = 256 tris/chunk.");
        Console.WriteLine("  That is the game's real terrain resolution; there is no finer data in the files.\n");

        Console.WriteLine($"M2 DOODAD COLLISION ({seenM2.Count:N0} distinct models)");
        Console.WriteLine($"  no collision mesh at all : {m2None,6:N0}  ({100.0 * m2None / Math.Max(seenM2.Count, 1):F0}%)");
        if (m2Tris.Count > 0)
        {
            m2Tris.Sort();
            Console.WriteLine($"  with collision           : {m2Tris.Count,6:N0}");
            Console.WriteLine($"  tris  min {m2Tris[0]}  median {m2Tris[m2Tris.Count / 2]}  " +
                              $"p90 {m2Tris[(int)(m2Tris.Count * 0.9)]}  max {m2Tris[^1]}");
        }

        Console.WriteLine($"\nWMO GROUPS ({seenWmo.Count:N0} distinct WMOs, {wmoGroupTris.Count:N0} groups)");
        if (wmoGroupTris.Count > 0)
        {
            wmoGroupTris.Sort();
            Console.WriteLine($"  tris  min {wmoGroupTris[0]}  median {wmoGroupTris[wmoGroupTris.Count / 2]}  " +
                              $"p90 {wmoGroupTris[(int)(wmoGroupTris.Count * 0.9)]}  max {wmoGroupTris[^1]:N0}");
            Console.WriteLine($"  total {wmoGroupTris.Sum(x => (long)x):N0}");
        }
    }

    /// Bake a few tiles and report what Recast costs and produces.
    public static void Bake(int mapId, int count)
    {
        var session = new Gui.Session();
        session.Open(@"E:\Games\World of Warcraft", "wow");
        var m = session.Maps.FirstOrDefault(x => x.Id == mapId);
        if (m == null) { Console.WriteLine($"map {mapId} not found"); return; }

        var settings = new NavBake.Settings();
        Console.WriteLine($"map {m.Id} \"{m.Name}\"   agent r={settings.AgentRadius} h={settings.AgentHeight} " +
                          $"climb={settings.AgentMaxClimb} slope={settings.AgentMaxSlope}  cell={settings.CellSize}\n");

        var tiles = session.Tiles(m.WdtFdid);
        var work = new List<(string Label, Extractor.Soup Soup)>();

        if (tiles.Count == 0)
        {
            var g = session.GlobalWmo(m.WdtFdid);
            var soup = g == null ? null : session.BuildGlobal(g);
            if (soup != null) work.Add(("global WMO", soup));
        }
        else
        {
            foreach (var t in tiles.Skip(tiles.Count / 3).Where(t => t.Obj0 != 0).Take(count))
            {
                var soup = session.BuildTile(t);
                if (soup != null) work.Add(($"[{t.X},{t.Y}]", soup));
            }
        }

        Console.WriteLine($"{"tile",-12} {"in tris",9} {"polys",8} {"raster",9} {"recast",9} {"detour",8} {"total",9} {"tile KB",8}");
        double sum = 0;
        foreach (var (label, soup) in work)
        {
            var r = NavBake.Bake(soup, settings);
            sum += r.TotalMs;
            if (!r.Ok) { Console.WriteLine($"{label,-12} {r.InputTris,9:N0}  FAILED: {r.Message}"); continue; }
            Console.WriteLine($"{label,-12} {r.InputTris,9:N0} {r.PolyCount,8:N0} " +
                              $"{r.RasterizeMs,8:F0}ms {r.BuildMs,8:F0}ms {r.DetourMs,7:F0}ms {r.TotalMs,8:F0}ms " +
                              $"{r.NavDataBytes / 1024.0,8:F0}");
        }
        Console.WriteLine($"\n{work.Count} tiles baked in {sum / 1000.0:F1}s  ({sum / Math.Max(work.Count, 1):F0}ms each)");
    }

    /// Bake one named tile, optionally with neighbours, to reproduce specific failures.
    public static void BakeTile(int mapId, int tx, int ty, int radius)
    {
        var session = new Gui.Session();
        session.Open(@"E:\Games\World of Warcraft", "wow");
        var m = session.Maps.First(x => x.Id == mapId);
        var tiles = session.Tiles(m.WdtFdid).ToDictionary(t => (t.X, t.Y));

        var clip = NavBake.Clip.ForAdt(tx, ty);
        const float band = 8f;
        var soup = new Extractor.Soup();
        int n = 0;
        for (int dy = -radius; dy <= radius; dy++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                if (!tiles.TryGetValue((tx + dx, ty + dy), out var t)) continue;
                var s = session.BuildTile(t);
                if (s == null) continue;
                if (dx == 0 && dy == 0) soup.Append(s);
                else soup.AppendClipped(s, clip.MinX - band, clip.MinY - band,
                                           clip.MaxX + band, clip.MaxY + band);
                n++;
            }

        Console.WriteLine($"map {mapId} tile [{tx},{ty}] r={radius}: {n} tiles, " +
                          $"{soup.TriCount:N0} solid + {soup.LiquidTris:N0} liquid tris, " +
                          $"{soup.Verts.Count + soup.LiquidVerts.Count:N0} verts");

        // clip to the target ADT, exactly as the per-ADT job does
        var settings0 = new NavBake.Settings();
        if (Environment.GetEnvironmentVariable("FORCE_AREA") is { Length: > 0 } fa)
            settings0.DebugForceArea = int.Parse(fa);
        settings0.DebugSpanTally = Environment.GetEnvironmentVariable("SPAN_TALLY") == "1";
        var r = NavBake.Bake(soup, settings0, default, clip);
        if (!r.Ok) { Console.WriteLine($"  FAILED: {r.Message}"); return; }

        Console.WriteLine($"  OK  {r.TileCount} detour tiles, {r.PolyCount:N0} polys, " +
                          $"max mesh verts {r.MaxMeshVerts:N0} / 65,535, " +
                          $"{r.TotalMs:F0}ms, {r.NavDataBytes / 1024.0:F0} KB" +
                          (r.FailedTiles > 0 ? $"  ({r.FailedTiles} tiles failed)" : ""));
        foreach (var t in r.Tiles)
            Console.WriteLine($"      row {t.Row} col {t.Col}  grid {t.GridX},{t.GridZ}  " +
                              $"{t.Polys:N0} polys  {t.MeshVerts:N0} verts  {t.Bytes / 1024.0:F0} KB");
        if (r.SpanTally.Length > 0) Console.WriteLine("  spans by area: " + r.SpanTally);
        Console.WriteLine("  polys by area: " + string.Join("  ", r.PolysByArea.OrderByDescending(k => k.Value)
            .Select(k => $"{NavBake.AreaName(k.Key)}({k.Key}) {k.Value:N0}")));

        string dir = Path.Combine(AppContext.BaseDirectory, "mmaps");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);   // else the round-trip counts stale files
        var (files, bytes) = NavWriter.WriteAll(dir, mapId, r);
        Console.WriteLine($"  wrote {files} .mmtile + 1 .mmap ({bytes / 1024.0:F0} KB) -> {dir}");

        var (lt, lp, err) = NavWriter.LoadBack(dir, mapId);
        Console.WriteLine(err.Length == 0
            ? $"  round-trip: loaded {lt} tiles / {lp:N0} polys back into a live navmesh"
            : $"  round-trip FAILED: {err}");
    }

    /// Bake a handful of transports and report what comes out.
    public static void TransportBake(int limit)
    {
        var session = new Gui.Session();
        session.Open(@"E:\Games\World of Warcraft", "wow");
        var models = Gui.TransportJob.Load();
        string dir = Path.Combine(AppContext.BaseDirectory, "mmaps", "transports");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);

        Console.WriteLine($"{models.Count} transport models; baking {Math.Min(limit, models.Count)}\n");
        Console.WriteLine($"{"model",-46} {"tris",8} {"polys",7} {"tiles",6} {"KB",7}  areas");

        foreach (var m in models.Take(limit))
        {
            var soup = session.BuildTransport(m.Fdid);
            if (soup == null || soup.TriCount == 0) { Console.WriteLine($"{m.Name,-46}  no geometry"); continue; }
            var r = NavBake.Bake(soup, new NavBake.Settings());
            if (!r.Ok) { Console.WriteLine($"{m.Name,-46} {soup.TriCount,8:N0}  FAILED: {r.Message}"); continue; }
            var (files, bytes) = NavWriter.WriteAll(dir, m.Fdid, r);
            Console.WriteLine($"{m.Name,-46} {soup.TriCount,8:N0} {r.PolyCount,7:N0} {files,6} {bytes / 1024.0,7:F0}  " +
                              string.Join(" ", r.PolysByArea.Select(k => $"{NavBake.AreaName(k.Key)}={k.Value}")));
        }

        var first = models.Take(limit).FirstOrDefault();
        if (first.Fdid != 0)
        {
            var (lt, lp, err) = NavWriter.LoadBack(dir, first.Fdid);
            Console.WriteLine(err.Length == 0
                ? $"\nround-trip on {first.Name}: {lt} tiles / {lp:N0} polys into a live navmesh"
                : $"\nround-trip FAILED: {err}");
        }
    }

    /// Where to stand in game to settle the Rx/Rz rotation order.
    ///
    /// Deliberately cheap: the angular difference between the two orderings comes from
    /// the matrices alone, and MODF already carries each placement's extents, so the
    /// expected displacement is size x angle with no WMO geometry loaded at all.
    public static void TiltedSpots(int top, float minSize = 0f, float maxSize = 600f)
    {
        var (casc, maps) = Open();
        string build = casc.Config.GetBuildInfoVariable("Version")!;
        var dbd = new GithubDBDProvider();
        var areas = new DBCD.DBCD(new One(casc, Names.Db2("AreaTable", 1353545)), dbd).Load("AreaTable", build);
        var areaName = areas.Values.ToDictionary(r => r.ID, r => r["AreaName_lang"]?.ToString() ?? "");

        static Matrix4x4 Rx(float d) => Matrix4x4.CreateRotationX(d * MathF.PI / 180f);
        static Matrix4x4 Ry(float d) => Matrix4x4.CreateRotationY(d * MathF.PI / 180f);
        static Matrix4x4 Rz(float d) => Matrix4x4.CreateRotationZ(d * MathF.PI / 180f);

        var found = new List<(double Disp, int Map, string MapName, Vector3 World, uint Fdid,
                              Vector3 Rot, float Size, int Row, int Col, int Area)>();

        foreach (DBCDRow row in maps.Values)
        {
            int mapId = row.ID;
            if (mapId is not (0 or 1 or 2444 or 2552 or 2912 or 1 or 530 or 571)) continue;

            foreach (var t in TilesOf(casc, maps, mapId).Where(t => t.Obj0 != 0))
            {
                List<WmoPlacement> wmos;
                try { using var os = casc.OpenFile((int)t.Obj0); (_, wmos, _) = Obj0.Read(os); }
                catch { continue; }

                foreach (var m in wmos)
                {
                    if (MathF.Abs(m.RotDeg.X) < 8f && MathF.Abs(m.RotDeg.Z) < 8f) continue;

                    var a = Rz(m.RotDeg.Z) * Ry(m.RotDeg.Y) * Rx(m.RotDeg.X);
                    var b = Rx(m.RotDeg.X) * Ry(m.RotDeg.Y) * Rz(m.RotDeg.Z);

                    // worst-case deviation of a unit axis under the two rotations
                    double dev = 0;
                    foreach (var axis in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
                        dev = Math.Max(dev, Vector3.Distance(Vector3.Transform(axis, a), Vector3.Transform(axis, b)));

                    var size = m.ExtMax - m.ExtMin;
                    float radius = size.Length() * 0.5f;
                    float diam = radius * 2f;
                    if (radius < 3f || diam < minSize || diam > maxSize) continue;

                    double disp = dev * radius;
                    if (disp < 4.0) continue;

                    var world = new Vector3(Terrain.Origin - m.Pos.Z, Terrain.Origin - m.Pos.X, m.Pos.Y);
                    found.Add((disp, mapId, row["MapName_lang"].ToString() ?? "", world, m.NameId,
                               m.RotDeg, radius * 2, NavBake.RowOf(world.X), NavBake.ColOf(world.Y), 0));
                }
            }
        }

        var zones = new Zones(casc, build);

        Console.WriteLine($"\n{found.Count:N0} tilted placements where the two orderings visibly disagree\n");
        Console.WriteLine("Fly to each and check WHICH WAY IT LEANS, not where it sits — position is");
        Console.WriteLine("nearly identical under both, only the tilt axis changes.\n");
        Console.WriteLine($"{"#",-3} {"zone",-26} {"zone x,y",-14} {"size",5} {"off",6}  " +
                          $"{"map",-18} {"world X, Y, Z",-25} rot(X,Y,Z)");

        int n = 0;
        var seen = new HashSet<uint>();
        foreach (var f in found.OrderByDescending(f => f.Disp))
        {
            if (!seen.Add(f.Fdid)) continue;          // one example per distinct model
            if (++n > top) break;
            var z = zones.Locate(f.Map, f.World);
            string where = z is { } s ? $"{s.X,5:F1},{s.Y,5:F1}" : "   — no sheet";
            Console.WriteLine($"{n,-3} {(z?.Zone ?? "?"),-26} {where,-14} {f.Size,4:F0}y {f.Disp,5:F0}y  " +
                              $"{f.MapName,-18} {f.World.X,7:F0},{f.World.Y,7:F0},{f.World.Z,6:F0}  " +
                              $"({f.Rot.X,6:F1},{f.Rot.Y,6:F1},{f.Rot.Z,6:F1})  wmo {f.Fdid}");
        }
    }

    /// Does the placement transform reproduce MODF's stored bounding box *at all*?
    ///
    /// The ordering pass compares candidates against each other, which says nothing if
    /// they are all wrong. This measures each against zero, bucketed by how tilted the
    /// placement is. A sound transform with a bad rotation shows a clean floor among the
    /// untilted placements and error that grows with tilt. A floor that is already high
    /// means the mistake is in the base transform, and no ordering will fix it.
    public static void Extents(int limit = 6000)
    {
        var (casc, maps) = Open();

        static Matrix4x4 Rx(float d) => Matrix4x4.CreateRotationX(d * MathF.PI / 180f);
        static Matrix4x4 Ry(float d) => Matrix4x4.CreateRotationY(d * MathF.PI / 180f);
        static Matrix4x4 Rz(float d) => Matrix4x4.CreateRotationZ(d * MathF.PI / 180f);

        (string Name, Func<Vector3, Matrix4x4> M)[] cand =
        [
            ("identity",        _ => Matrix4x4.Identity),
            ("Ry(y)",           r => Ry(r.Y)),
            ("Rz(z)Ry(y)Rx(x)", r => Rz(r.Z) * Ry(r.Y) * Rx(r.X)),
            ("Rz(z)Rx(x)Ry(y)", r => Rz(r.Z) * Rx(r.X) * Ry(r.Y)),
        ];

        float[] edge = [0.5f, 5f, 20f, 60f, 181f];
        var sum = new double[edge.Length, cand.Length];
        var cnt = new int[edge.Length];

        var box = new Dictionary<uint, (Vector3 Lo, Vector3 Hi)>();
        int used = 0;

        foreach (int mapId in Maps)
        {
            foreach (var t in TilesOf(casc, maps, mapId).Where(t => t.Obj0 != 0))
            {
                List<WmoPlacement> wmos;
                try { using var os = casc.OpenFile((int)t.Obj0); (_, wmos, _) = Obj0.Read(os); }
                catch { continue; }

                foreach (var m in wmos)
                {
                    if (!box.TryGetValue(m.NameId, out var b))
                    {
                        if (!casc.FileExists((int)m.NameId)) continue;
                        try { using var rs = casc.OpenFile((int)m.NameId); var r = Wmo.ReadRoot(rs); b = (r.BoxMin, r.BoxMax); }
                        catch { continue; }
                        box[m.NameId] = b;
                    }

                    var size = m.ExtMax - m.ExtMin;
                    if (size.X <= 0 || size.Y <= 0 || size.Z <= 0 || size.Length() > 5000) continue;

                    float tilt = MathF.Max(MathF.Abs(m.RotDeg.X), MathF.Abs(m.RotDeg.Z));
                    int bucket = 0;
                    while (bucket < edge.Length - 1 && tilt >= edge[bucket]) bucket++;

                    for (int i = 0; i < cand.Length; i++)
                    {
                        var mat = cand[i].M(m.RotDeg);
                        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
                        for (int c = 0; c < 8; c++)
                        {
                            var corner = new Vector3((c & 1) == 0 ? b.Lo.X : b.Hi.X,
                                                     (c & 2) == 0 ? b.Lo.Y : b.Hi.Y,
                                                     (c & 4) == 0 ? b.Lo.Z : b.Hi.Z);
                            var p = Vector3.Transform(new Vector3(corner.Y, corner.Z, corner.X) * m.Scale, mat) + m.Pos;
                            lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p);
                        }
                        var d = Vector3.Abs(lo - m.ExtMin) + Vector3.Abs(hi - m.ExtMax);
                        sum[bucket, i] += (d.X / size.X + d.Y / size.Y + d.Z / size.Z) / 6.0;
                    }
                    cnt[bucket]++;
                    if (++used >= limit) goto done;
                }
            }
        }
    done:
        Console.WriteLine($"\n{used:N0} placements, MOHD box vs MODF extents (0 = exact)\n");
        Console.Write($"  {"tilt",-12} {"count",7}");
        foreach (var c in cand) Console.Write($" {c.Name,17}");
        Console.WriteLine();

        string[] label = ["none", "<5deg", "5-20deg", "20-60deg", "60deg+"];
        for (int bk = 0; bk < edge.Length; bk++)
        {
            if (cnt[bk] == 0) continue;
            Console.Write($"  {label[bk],-12} {cnt[bk],7:N0}");
            for (int i = 0; i < cand.Length; i++) Console.Write($" {sum[bk, i] / cnt[bk],17:F4}");
            Console.WriteLine();
        }
    }

    /// Search the whole rotation space against MODF extents.
    ///
    /// --extents established that untilted placements land on 0.0001, so the oracle is
    /// sound and the base transform is right; only the tilt is wrong. rot.Y is yaw about
    /// Y and is not in question. That leaves which axis each of rot.X and rot.Z drives,
    /// the sign of each, and the order of the three — 48 combinations, all cheap.
    public static void RotSearch(int limit = 3000)
    {
        var (casc, maps) = Open();

        static Matrix4x4 R(int axis, float deg) => axis switch
        {
            0 => Matrix4x4.CreateRotationX(deg * MathF.PI / 180f),
            1 => Matrix4x4.CreateRotationY(deg * MathF.PI / 180f),
            _ => Matrix4x4.CreateRotationZ(deg * MathF.PI / 180f),
        };
        string[] axisName = ["Rx", "Ry", "Rz"];

        var cand = new List<(string Name, Func<Vector3, Matrix4x4> M)>();
        foreach (int xAxis in new[] { 0, 2 })
        {
            int zAxis = xAxis == 0 ? 2 : 0;
            foreach (int sx in new[] { 1, -1 })
                foreach (int sz in new[] { 1, -1 })
                    foreach (var order in new[] { "xyz", "xzy", "yxz", "yzx", "zxy", "zyx" })
                    {
                        int ax = xAxis, az = zAxis, six = sx, siz = sz;
                        string nx = $"{axisName[ax]}({(six < 0 ? "-" : "")}x)";
                        string ny = "Ry(y)";
                        string nz = $"{axisName[az]}({(siz < 0 ? "-" : "")}z)";
                        string o = order;

                        Matrix4x4 Build(Vector3 r)
                        {
                            var m = Matrix4x4.Identity;
                            foreach (char c in o)
                                m *= c switch
                                {
                                    'x' => R(ax, six * r.X),
                                    'y' => R(1, r.Y),
                                    _ => R(az, siz * r.Z),
                                };
                            return m;
                        }
                        cand.Add((string.Join("*", o.Select(c => c == 'x' ? nx : c == 'y' ? ny : nz)), Build));
                    }
        }

        var err = new double[cand.Count];
        int used = 0;
        var box = new Dictionary<uint, (Vector3 Lo, Vector3 Hi)>();

        foreach (int mapId in Maps)
        {
            foreach (var t in TilesOf(casc, maps, mapId).Where(t => t.Obj0 != 0))
            {
                List<WmoPlacement> wmos;
                try { using var os = casc.OpenFile((int)t.Obj0); (_, wmos, _) = Obj0.Read(os); }
                catch { continue; }

                foreach (var m in wmos)
                {
                    if (MathF.Max(MathF.Abs(m.RotDeg.X), MathF.Abs(m.RotDeg.Z)) < 5f) continue;
                    if (!box.TryGetValue(m.NameId, out var b))
                    {
                        if (!casc.FileExists((int)m.NameId)) continue;
                        try { using var rs = casc.OpenFile((int)m.NameId); var r = Wmo.ReadRoot(rs); b = (r.BoxMin, r.BoxMax); }
                        catch { continue; }
                        box[m.NameId] = b;
                    }

                    var size = m.ExtMax - m.ExtMin;
                    if (size.X <= 0 || size.Y <= 0 || size.Z <= 0 || size.Length() > 5000) continue;

                    for (int i = 0; i < cand.Count; i++)
                    {
                        var mat = cand[i].M(m.RotDeg);
                        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
                        for (int c = 0; c < 8; c++)
                        {
                            var corner = new Vector3((c & 1) == 0 ? b.Lo.X : b.Hi.X,
                                                     (c & 2) == 0 ? b.Lo.Y : b.Hi.Y,
                                                     (c & 4) == 0 ? b.Lo.Z : b.Hi.Z);
                            var p = Vector3.Transform(new Vector3(corner.Y, corner.Z, corner.X) * m.Scale, mat) + m.Pos;
                            lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p);
                        }
                        var d = Vector3.Abs(lo - m.ExtMin) + Vector3.Abs(hi - m.ExtMax);
                        err[i] += (d.X / size.X + d.Y / size.Y + d.Z / size.Z) / 6.0;
                    }
                    if (++used >= limit) goto done;
                }
            }
        }
    done:
        Console.WriteLine($"\n{used:N0} tilted placements, {cand.Count} candidates");
        Console.WriteLine("untilted placements score 0.0001, so that is what a correct answer looks like\n");
        foreach (var (c, i) in cand.Select((c, i) => (c, i)).OrderBy(p => err[p.i]).Take(12))
            Console.WriteLine($"  {err[i] / Math.Max(used, 1),9:F5}  {c.Name}");
    }

    /// What zone is a world position in, and what does the in-game map call it? Also
    /// prints the ADT's own area name, which comes from a different table entirely — if
    /// the two disagree the UiMap rectangle picked is the wrong one.
    public static void Where(int mapId, float x, float y, float z)
    {
        var (casc, maps) = Open();
        string build = casc.Config.GetBuildInfoVariable("Version")!;
        var zones = new Zones(casc, build);
        var world = new Vector3(x, y, z);

        var spot = zones.Locate(mapId, world);
        Console.WriteLine($"\nmap {mapId}  world {x:F0}, {y:F0}, {z:F0}");
        Console.WriteLine(spot is { } s
            ? $"  zone map : {s.Zone}  ({s.X:F1}, {s.Y:F1})"
            : "  zone map : no UiMap sheet covers this point");

        int row = NavBake.RowOf(x), col = NavBake.ColOf(y);
        Console.WriteLine($"  tile    : [{row},{col}]");

        var tile = TilesOf(casc, maps, mapId).FirstOrDefault(t => t.X == row && t.Y == col);
        if (tile == null || tile.Root == 0) return;

        var dbd = new GithubDBDProvider();
        var areas = new DBCD.DBCD(new One(casc, Names.Db2("AreaTable", 1353545)), dbd).Load("AreaTable", build);
        using var rs = casc.OpenFile((int)tile.Root);
        int area = Adt.DominantAreaId(rs);
        Console.WriteLine($"  adt area: {area} " +
                          $"\"{(areas.TryGetValue(area, out DBCDRow? ar) && ar != null ? ar["AreaName_lang"] : "?")}\"");
    }

    /// Bake every map with geometry. Resumable, so re-running continues.
    public static void BakeAll(string outDir, int threads, int mapLimit)
    {
        var session = new Gui.Session();
        session.Open(@"E:\Games\World of Warcraft", "wow");
        Console.WriteLine($"build {session.Build}, {session.Maps.Count:N0} maps — finding those with geometry…");

        var bakeable = Gui.BakeAllJob.Bakeable(session);
        if (mapLimit > 0 && bakeable.Count > mapLimit) bakeable = bakeable.Take(mapLimit).ToList();
        Console.WriteLine($"{bakeable.Count} maps to bake, {threads} threads -> {outDir}\n");

        var job = Gui.BakeAllJob.Start(session, bakeable, outDir, new NavBake.Settings(), threads);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int lastDone = -1;

        while (job.Running)
        {
            Thread.Sleep(2000);
            if (job.AdtsDone == lastDone) continue;
            lastDone = job.AdtsDone;
            double frac = job.AdtsTotal > 0 ? (double)job.AdtsDone / job.AdtsTotal : 0;
            double eta = frac > 0.001 ? sw.Elapsed.TotalSeconds / frac - sw.Elapsed.TotalSeconds : 0;
            Console.WriteLine($"  [{job.MapsDone}/{job.MapsTotal} maps] {job.AdtsDone:N0}/{job.AdtsTotal:N0} adts  " +
                              $"{job.TilesWritten:N0} tiles  {job.Bytes / 1048576.0:F0} MB  " +
                              $"skipped {job.Skipped:N0}  failed {job.Failed}  " +
                              $"{sw.Elapsed.TotalMinutes:F0}m elapsed, ~{eta / 60:F0}m left   {job.CurrentMap}");
            // stdout is block-buffered when redirected to a file, which hides progress
            // for the entire run — exactly when you most want to check on it
            Console.Out.Flush();
        }
        Console.WriteLine($"\n{job.Status}  ({sw.Elapsed.TotalMinutes:F1} minutes)");
    }

    /// Bake a map one ADT at a time, exactly as the GUI job does.
    public static void BakeMap(int mapId, int limit)
    {
        var session = new Gui.Session();
        session.Open(@"E:\Games\World of Warcraft", "wow");
        var m = session.Maps.First(x => x.Id == mapId);
        string dir = Path.Combine(AppContext.BaseDirectory, "mmaps");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);

        var tiles = session.Tiles(m.WdtFdid);
        var byPos = tiles.ToDictionary(t => (t.X, t.Y));
        var settings = new NavBake.Settings();
        Console.WriteLine($"map {mapId} \"{m.Name}\": {tiles.Count} ADTs, baking {Math.Min(limit, tiles.Count)}\n");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        int written = 0; long bytes = 0; bool paramsWritten = false;

        foreach (var t in tiles.Skip(tiles.Count / 3).Take(limit))
        {
            var clip = NavBake.Clip.ForAdt(t.X, t.Y);
            const float band = 8f;
            var soup = new Extractor.Soup();
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (!byPos.TryGetValue((t.X + dx, t.Y + dy), out var n)) continue;
                    var s = session.BuildTile(n);
                    if (s == null) continue;
                    if (dx == 0 && dy == 0) soup.Append(s);
                    else soup.AppendClipped(s, clip.MinX - band, clip.MinY - band,
                                               clip.MaxX + band, clip.MaxY + band);
                }

            var t0 = System.Diagnostics.Stopwatch.StartNew();
            var r = NavBake.Bake(soup, settings, default, clip);
            if (!r.Ok) { Console.WriteLine($"  [{t.X},{t.Y}]  {r.Message}"); continue; }

            if (!paramsWritten) { NavWriter.WriteMapParams(dir, mapId, r.MeshParams); paramsWritten = true; }
            long b = 0;
            foreach (var nt in r.Tiles) b += NavWriter.WriteTile(dir, mapId, nt);
            written += r.Tiles.Count; bytes += b;

            Console.WriteLine($"  [{t.X,2},{t.Y,2}]  {soup.TriCount + soup.LiquidTris,9:N0} tris -> " +
                              $"{r.Tiles.Count,3} tiles, {r.PolyCount,7:N0} polys, {t0.Elapsed.TotalSeconds,5:F1}s, {b / 1024.0,7:F0} KB");
        }

        Console.WriteLine($"\n{written} .mmtile, {bytes / 1048576.0:F1} MB in {sw.Elapsed.TotalSeconds:F1}s");
        var (lt, lp, err) = NavWriter.LoadBack(dir, mapId);
        Console.WriteLine(err.Length == 0
            ? $"round-trip: {lt} tiles / {lp:N0} polys loaded into a live navmesh"
            : $"round-trip FAILED: {err}");
        foreach (var f in Directory.GetFiles(dir).Take(4)) Console.WriteLine("  " + Path.GetFileName(f));
    }

    /// Exercise the exact path the GUI uses for a tile-less map.
    public static void SessionTest(int mapId)
    {
        var session = new Gui.Session();
        session.Open(@"E:\Games\World of Warcraft", "wow");
        Console.WriteLine($"build {session.Build}, {session.Maps.Count:N0} maps");

        var m = session.Maps.FirstOrDefault(x => x.Id == mapId);
        if (m == null) { Console.WriteLine($"map {mapId} not in list"); return; }
        Console.WriteLine($"map {m.Id} \"{m.Name}\"  dir={m.Directory}  wdt={m.WdtFdid}");

        var tiles = session.Tiles(m.WdtFdid);
        Console.WriteLine($"  Tiles() -> {tiles.Count}");

        var g = session.GlobalWmo(m.WdtFdid);
        Console.WriteLine($"  GlobalWmo() -> {(g == null ? "null" : $"fdid {g.NameId}, scale {g.Scale}")}");
        if (g == null) return;

        var soup = session.BuildGlobal(g);
        Console.WriteLine($"  BuildGlobal() -> {(soup == null ? "null" : $"{soup.TriCount:N0} tris, {soup.Verts.Count:N0} verts")}");
        if (soup == null || soup.Verts.Count == 0) return;

        var batch = new Gui.Renderer.Batch();
        batch.Add(soup);
        Console.WriteLine($"  Batch     -> {batch.Tris:N0} tris, bounds " +
                          $"X[{batch.Lo.X:F0}..{batch.Hi.X:F0}] Y[{batch.Lo.Y:F0}..{batch.Hi.Y:F0}] Z[{batch.Lo.Z:F0}..{batch.Hi.Z:F0}]");
        for (int c = 0; c < batch.Buckets.Length; c++)
            if (batch.Buckets[c].Count > 0)
                Console.WriteLine($"     bucket {c} ({Gui.Renderer.ClassName[c]}): {batch.Buckets[c].Count / 18:N0} tris");
    }

    /// What transports exist, and can we get their collision geometry?
    public static void Transports()
    {
        var (casc, maps) = Open();
        string build = casc.Config.GetBuildInfoVariable("Version")!;
        var dbd = new GithubDBDProvider();

        Console.WriteLine($"listfile index: {Names.Count:N0} db2 names\n");

        var gobs = new DBCD.DBCD(new One(casc, Names.Db2("GameObjects")), dbd).Load("GameObjects", build);
        var disp = new DBCD.DBCD(new One(casc, Names.Db2("GameObjectDisplayInfo")), dbd).Load("GameObjectDisplayInfo", build);
        Console.WriteLine($"GameObjects {gobs.Count:N0} rows, GameObjectDisplayInfo {disp.Count:N0} rows");
        Console.WriteLine($"GameObjects columns: {string.Join(", ", gobs.AvailableColumns)}\n");

        var model = new Dictionary<int, int>();
        foreach (DBCDRow r in disp.Values) model[r.ID] = Convert.ToInt32(r["FileDataID"]);

        // GAMEOBJECT_TYPE 11 = TRANSPORT (elevators/tram), 15 = MO_TRANSPORT (boats/zeppelins)
        var byType = new Dictionary<int, int>();
        var transports = new List<(int Id, string Name, int Type, int Map, int Display)>();

        foreach (DBCDRow r in gobs.Values)
        {
            int type = Convert.ToInt32(r["TypeID"]);
            byType[type] = byType.GetValueOrDefault(type) + 1;
            if (type is 11 or 15)
                transports.Add((r.ID, r["Name_lang"]?.ToString() ?? "", type,
                                Convert.ToInt32(r["OwnerID"]), Convert.ToInt32(r["DisplayID"])));
        }

        Console.WriteLine("client-side gameobject spawns by type:");
        foreach (var kv in byType.OrderByDescending(k => k.Value).Take(8))
            Console.WriteLine($"  type {kv.Key,-3} {kv.Value,7:N0}");

        Console.WriteLine($"\n{transports.Count:N0} transport spawns of type 11/15 in the client tables");
        Console.WriteLine("(transports are server-side spawns; the client file only holds decorative objects)\n");

        // Geometry is still reachable — transport models all live under world/wmo/transports/.
        (string Name, int Fdid)[] known =
        [
            ("Horde zeppelin",      116315), ("Alliance transport ship", 116317),
            ("Night elf ship",      116322), ("Pirate ship",             116326),
            ("Undead ship",         116334), ("Goblin zeppelin",         116342),
            ("Icebreaker",          116306), ("Passenger ship",          116310),
            ("Tuskarr boat",        116344), ("Vrykul gondola",          116352),
        ];

        Console.WriteLine($"{"transport model",-26} {"fdid",8} {"groups",7} {"tris",9}  local bounds (yd)");
        var ex = new Extractor(casc, _ => LiquidClass.Water);

        foreach (var (name, fdid) in known)
        {
            if (!casc.FileExists(fdid)) { Console.WriteLine($"{name,-26} {fdid,8}  not installed"); continue; }

            // placed at the origin with no rotation: this is the transport's own space,
            // which is exactly what a per-transport navmesh needs
            var placement = new WmoPlacement((uint)fdid, Vector3.Zero, Vector3.Zero,
                                             Vector3.Zero, Vector3.Zero, 0, 1f);
            var soup = ex.BuildGlobalWmo(placement);
            if (soup.Verts.Count == 0) { Console.WriteLine($"{name,-26} {fdid,8}  no geometry"); continue; }

            using var rs = casc.OpenFile(fdid);
            int groups = Wmo.ReadRoot(rs).GroupIds.Length;
            var lo = soup.Verts.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
            var hi = soup.Verts.Aggregate(new Vector3(float.MinValue), Vector3.Max);
            Console.WriteLine($"{name,-26} {fdid,8} {groups,7} {soup.TriCount,9:N0}  " +
                              $"{hi.X - lo.X,6:F0} x {hi.Y - lo.Y,6:F0} x {hi.Z - lo.Z,5:F0}");
        }
    }

    /// Build the maps that are a single global WMO and check they land where dungeons live.
    public static void Dungeons()
    {
        var (casc, maps) = Open();
        string build = casc.Config.GetBuildInfoVariable("Version")!;
        var liquid = new DBCD.DBCD(new One(casc, 1371380), new GithubDBDProvider()).Load("LiquidType", build);
        var bank = liquid.Values.ToDictionary(r => (ushort)r.ID, r => (LiquidClass)Convert.ToByte(r["SoundBank"]));
        var ex = new Extractor(casc, id => bank.TryGetValue(id, out var c) ? c : LiquidClass.Unknown);

        int withGlobal = 0, empty = 0;
        Console.WriteLine($"{"map",-6} {"name",-30} {"tris",10} {"doodads",8}  world bounds");

        foreach (DBCDRow row in maps.Values)
        {
            int wdt = Convert.ToInt32(row["WdtFileDataID"]);
            if (wdt == 0 || !casc.FileExists(wdt)) continue;

            List<TileFiles> tiles;
            WmoPlacement? global;
            using (var s = casc.OpenFile(wdt)) tiles = Wdt.ReadTiles(s);
            if (tiles.Count > 0) continue;                     // terrain maps handled already
            using (var s = casc.OpenFile(wdt)) global = Wdt.ReadGlobalWmo(s);
            if (global == null || !casc.FileExists((int)global.NameId)) { empty++; continue; }

            withGlobal++;
            if (withGlobal > 12) continue;

            var soup = ex.BuildGlobalWmo(global);
            if (soup.Verts.Count == 0) continue;
            var lo = soup.Verts.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
            var hi = soup.Verts.Aggregate(new Vector3(float.MinValue), Vector3.Max);
            Console.WriteLine($"{row.ID,-6} {row["MapName_lang"],-30} {soup.TriCount,10:N0} {soup.InteriorTris,8:N0}  " +
                              $"X[{lo.X,6:F0}..{hi.X,6:F0}] Y[{lo.Y,6:F0}..{hi.Y,6:F0}] Z[{lo.Z,6:F0}..{hi.Z,6:F0}]");
        }

        Console.WriteLine($"\n{withGlobal} maps have a global WMO, {empty} have neither tiles nor a global WMO");
    }

    /// Maps that report 0 tiles: are they empty, or is their geometry a global WMO?
    public static void WdtChunks()
    {
        var (casc, maps) = Open();
        foreach (int mapId in new[] { 34, 43, 90, 36, 0 })
        {
            if (!maps.TryGetValue(mapId, out DBCDRow? row) || row == null) continue;
            int wdt = Convert.ToInt32(row["WdtFileDataID"]);
            Console.WriteLine($"\nmap {mapId} \"{row["MapName_lang"]}\"  wdt {wdt}");
            if (wdt == 0 || !casc.FileExists(wdt)) { Console.WriteLine("  no WDT"); continue; }

            using var s = casc.OpenFile(wdt);
            var br = new BinaryReader(s);
            while (br.BaseStream.Position + 8 <= br.BaseStream.Length)
            {
                var magic = Chunk.Magic(br);
                uint size = br.ReadUInt32();
                long body = br.BaseStream.Position;
                Console.Write($"  {magic} {size,8:N0}");

                if (magic == "MODF" && size >= 64)
                {
                    uint nameId = br.ReadUInt32(); br.ReadUInt32();
                    float px = br.ReadSingle(), py = br.ReadSingle(), pz = br.ReadSingle();
                    float rx = br.ReadSingle(), ry = br.ReadSingle(), rz = br.ReadSingle();
                    float lx = br.ReadSingle(), ly = br.ReadSingle(), lz = br.ReadSingle();
                    float hx = br.ReadSingle(), hy = br.ReadSingle(), hz = br.ReadSingle();
                    Console.WriteLine($"   global WMO fdid {nameId} present={casc.FileExists((int)nameId)}");
                    Console.WriteLine($"        pos ({px:F1}, {py:F1}, {pz:F1})  rot ({rx:F1}, {ry:F1}, {rz:F1})");
                    Console.Write($"        extents ({lx:F0},{ly:F0},{lz:F0}) .. ({hx:F0},{hy:F0},{hz:F0})");

                    if (casc.FileExists((int)nameId))
                    {
                        using var ws = casc.OpenFile((int)nameId);
                        var root = Wmo.ReadRoot(ws);
                        Console.Write($"\n        MOHD box ({root.BoxMin.X:F0},{root.BoxMin.Y:F0},{root.BoxMin.Z:F0})" +
                                      $" .. ({root.BoxMax.X:F0},{root.BoxMax.Y:F0},{root.BoxMax.Z:F0})" +
                                      $"  {root.GroupIds.Length} groups");
                    }
                }
                else if (magic == "MWID" || magic == "MWMO") Console.Write("   (global WMO reference)");
                Console.WriteLine();
                br.BaseStream.Position = body + size;
            }
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

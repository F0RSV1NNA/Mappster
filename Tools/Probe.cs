using System.Numerics;
using CASCLib;
using DBCD;
using DBCD.Providers;

namespace MapExtract;

/// Re-validates the coordinate conventions against ground truth outside our own parsing.
public static class Probe
{
    // Long-known Eastern Kingdoms world coordinates (X, Y).
    static readonly (string Name, float X, float Y)[] Landmarks =
    [
        ("Stormwind",  -8913f,  554f),
        ("Ironforge",  -4981f, -881f),
        ("Booty Bay", -14297f,  518f),
        ("Undercity",   1633f,  240f),
    ];

    const float Tile = 533.33333f;
    const float Origin = 32f * Tile;

    public static void Run()
    {
        Gui.Session.EnsureCdnConfig(@"E:\Games\World of Warcraft", "wow");
        var casc = CASCHandler.OpenLocalStorage(@"E:\Games\World of Warcraft", "wow", null);
        ((WowRootHandler)casc.Root).SetFlags(LocaleFlags.enUS, false, false, createTree: false);
        string build = casc.Config.GetBuildInfoVariable("Version")!;

        var dbd = new GithubDBDProvider();
        var maps = new DBCD.DBCD(new Db2(casc, 1349477), dbd).Load("Map", build);
        var liquid = new DBCD.DBCD(new Db2(casc, 1371380), dbd).Load("LiquidType", build);
        var bank = liquid.Values.ToDictionary(r => (ushort)r.ID, r => (LiquidClass)Convert.ToByte(r["SoundBank"]));

        using var w = casc.OpenFile(Convert.ToInt32(maps[0]["WdtFileDataID"]));
        var tileList = Wdt.ReadTiles(w);
        var tiles = tileList.ToDictionary(t => (t.X, t.Y));
        var ex = new Extractor(casc, id => bank.TryGetValue(id, out var c) ? c : LiquidClass.Unknown);

        Console.WriteLine($"build {build}, map 0, {tiles.Count} tiles\n");

        // --- 1. landmarks -------------------------------------------------
        Console.WriteLine("LANDMARKS (city WMO triangles on the expected tile vs its transpose)");
        foreach (var (name, wx, wy) in Landmarks)
        {
            int tx = (int)MathF.Floor(32 - wx / Tile);
            int ty = (int)MathF.Floor(32 - wy / Tile);
            Console.WriteLine($"  {name,-11} [{tx,2},{ty,2}] {WmoTris(tiles, ex, tx, ty),11:N0}    " +
                              $"[{ty,2},{tx,2}] {WmoTris(tiles, ex, ty, tx),11:N0}");
        }

        // --- 2. MCVT orientation ------------------------------------------
        int mA = 0, tA = 0, mB = 0, tB = 0;
        double upA = 0, upB = 0; int n = 0;
        foreach (var t in tileList.Skip(tileList.Count / 3).Take(12))
        {
            using var s = casc.OpenFile((int)t.Root);
            var chunks = Adt.ReadTerrain(s).Chunks;
            if (chunks.Count < 200) continue;
            var (a, at) = Terrain.EdgeContinuity(chunks, false);
            var (b, bt) = Terrain.EdgeContinuity(chunks, true);
            mA += a; tA += at; mB += b; tB += bt;
            upA += NormalZ(chunks[0], false); upB += NormalZ(chunks[0], true); n++;
        }
        Console.WriteLine($"\nMCVT ORIENTATION");
        Console.WriteLine($"  row-major   {100.0 * mA / Math.Max(tA, 1),5:F1}% edges match, mean normal Z {upA / n,6:F3}");
        Console.WriteLine($"  transposed  {100.0 * mB / Math.Max(tB, 1),5:F1}% edges match, mean normal Z {upB / n,6:F3}");

        // --- 3. placement transform ---------------------------------------
        (string Name, Func<Vector3, Vector3> F, float Det)[] cands =
        [
            ("(O-p.X, O-p.Z, p.Y)", p => new Vector3(Origin - p.X, Origin - p.Z, p.Y), -1),
            ("(O-p.Z, O-p.X, p.Y)", p => new Vector3(Origin - p.Z, Origin - p.X, p.Y), +1),
        ];
        var hits = new int[cands.Length];
        int total = 0;
        foreach (var t in tileList.Skip(tileList.Count / 3).Where(t => t.Obj0 != 0).Take(25))
        {
            using var ts = casc.OpenFile((int)t.Root);
            var chunks = Adt.ReadTerrain(ts).Chunks;
            if (chunks.Count == 0) continue;
            float xMax = chunks.Max(c => c.PosX), xMin = chunks.Min(c => c.PosX) - Tile / 16f;
            float yMax = chunks.Max(c => c.PosY), yMin = chunks.Min(c => c.PosY) - Tile / 16f;

            using var os = casc.OpenFile((int)t.Obj0);
            var (dd, wm, _) = Obj0.Read(os);
            var pts = dd.Select(d => d.Pos).Concat(wm.Select(m => m.Pos)).ToList();
            total += pts.Count;
            for (int i = 0; i < cands.Length; i++)
                foreach (var p in pts)
                {
                    var v = cands[i].F(p);
                    if (v.X >= xMin && v.X <= xMax && v.Y >= yMin && v.Y <= yMax) hits[i]++;
                }
        }
        Console.WriteLine($"\nPLACEMENT TRANSFORM ({total:N0} placements)");
        for (int i = 0; i < cands.Length; i++)
            Console.WriteLine($"  {cands[i].Name}  det {cands[i].Det,+2:F0}   {100.0 * hits[i] / Math.Max(total, 1),5:F1}% in tile");

        // --- 4. liquid grid orientation, scored by how far water sinks into terrain ---
        double penA = 0, penB = 0; int nA = 0, nB = 0;
        foreach (var t in tileList.Skip(tileList.Count / 3).Take(25))
        {
            using var ts = casc.OpenFile((int)t.Root);
            var terrain = Adt.ReadTerrain(ts);
            if (terrain.Mh2o.Length == 0 || terrain.Chunks.Count < 200) continue;

            var ground = new Dictionary<(int, int), float>();
            var gv = new List<Vector3>(); var gi = new List<int>();
            foreach (var c in terrain.Chunks) Terrain.Emit(c, gv, gi, transpose: false);
            foreach (var v in gv)
            {
                var k = ((int)MathF.Round(v.X / Terrain.Unit), (int)MathF.Round(v.Y / Terrain.Unit));
                ground[k] = ground.TryGetValue(k, out float z) ? MathF.Min(z, v.Z) : v.Z;
            }

            foreach (bool tr in new[] { false, true })
            {
                var lv = new List<Vector3>(); var li = new List<int>(); var lc = new List<LiquidClass>();
                Liquid.EmitMh2o(terrain.Mh2o, terrain.Chunks, lv, li, lc, _ => LiquidClass.Water, tr);
                if (lv.Count == 0) continue;
                if (lv.Max(v => v.Z) - lv.Min(v => v.Z) < 1.0f) continue;   // flat ocean cannot tell
                foreach (var v in lv)
                {
                    var k = ((int)MathF.Round(v.X / Terrain.Unit), (int)MathF.Round(v.Y / Terrain.Unit));
                    if (!ground.TryGetValue(k, out float gz)) continue;
                    float pen = MathF.Max(0, gz - v.Z);
                    if (tr) { nB++; penB += pen; } else { nA++; penA += pen; }
                }
            }
        }
        Console.WriteLine($"\nMH2O GRID ORIENTATION (mean burial under terrain, lower is right)");
        Console.WriteLine($"  row-major   {penA / Math.Max(nA, 1),6:F3} yd over {nA:N0} verts");
        Console.WriteLine($"  transposed  {penB / Math.Max(nB, 1),6:F3} yd over {nB:N0} verts");
    }

    static double NormalZ(Adt.Mcnk k, bool transpose)
    {
        var v = new List<Vector3>(); var idx = new List<int>();
        Terrain.Emit(k, v, idx, transpose);
        double sum = 0; int n = 0;
        for (int i = 0; i < idx.Count; i += 3)
        {
            var nn = Vector3.Cross(v[idx[i + 1]] - v[idx[i]], v[idx[i + 2]] - v[idx[i]]);
            if (nn.Length() > 1e-6f) { sum += Vector3.Normalize(nn).Z; n++; }
        }
        return n == 0 ? 0 : sum / n;
    }

    static long WmoTris(Dictionary<(int, int), TileFiles> tiles, Extractor ex, int x, int y)
    {
        long total = 0;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (!tiles.TryGetValue((x + dx, y + dy), out var t)) continue;
                try { total += ex.BuildTile(t).WmoTris; } catch { }
            }
        return total;
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

using System.Numerics;
using CASCLib;

namespace MapExtract;

/// Builds a world-space collision triangle soup for one ADT tile.
public sealed class Extractor(CASCHandler casc, Func<ushort, LiquidClass> classify)
{
    readonly Dictionary<uint, (Wmo.Mesh Solid, Wmo.Mesh Liquid, uint GroupLiquid, Wmo.Root Root)> _wmoCache = [];
    readonly Dictionary<uint, Wmo.Mesh> _m2Cache = [];

    public int SkippedGroups { get; private set; }
    public int SkippedGroupTris { get; private set; }

    public sealed class Soup
    {
        public List<Vector3> Verts = [];
        public List<int> Indices = [];
        public List<Vector3> LiquidVerts = [];
        public List<int> LiquidIndices = [];
        public List<LiquidClass> LiquidTriClass = [];
        public int TerrainTris, WmoTris, DoodadTris, InteriorTris;
        public int TriCount => Indices.Count / 3;
        public int LiquidTris => LiquidIndices.Count / 3;

        /// Fold in only the triangles overlapping a world-space XY rect.
        ///
        /// A neighbouring ADT is loaded purely to give Recast a border ring about
        /// 1.3 yd wide. Appending all 533 yd of it is ~400x more geometry than the
        /// border needs, and on the densest ground that reached 13.5M triangles and
        /// killed the process. Keeping the band costs nothing and bakes the same.
        public void AppendClipped(Soup other, float minX, float minY, float maxX, float maxY)
        {
            Filter(other.Verts, other.Indices, Verts, Indices, null, null, minX, minY, maxX, maxY);
            Filter(other.LiquidVerts, other.LiquidIndices, LiquidVerts, LiquidIndices,
                   other.LiquidTriClass, LiquidTriClass, minX, minY, maxX, maxY);
        }

        static void Filter(List<Vector3> srcV, List<int> srcI, List<Vector3> dstV, List<int> dstI,
                           List<LiquidClass>? srcC, List<LiquidClass>? dstC,
                           float minX, float minY, float maxX, float maxY)
        {
            var remap = new Dictionary<int, int>();
            for (int t = 0; t < srcI.Count / 3; t++)
            {
                int a = srcI[t * 3], b = srcI[t * 3 + 1], c = srcI[t * 3 + 2];
                Vector3 va = srcV[a], vb = srcV[b], vc = srcV[c];

                // triangle AABB vs rect: a vertex test would drop long triangles
                // that cross the band without landing a corner in it
                if (MathF.Max(va.X, MathF.Max(vb.X, vc.X)) < minX) continue;
                if (MathF.Min(va.X, MathF.Min(vb.X, vc.X)) > maxX) continue;
                if (MathF.Max(va.Y, MathF.Max(vb.Y, vc.Y)) < minY) continue;
                if (MathF.Min(va.Y, MathF.Min(vb.Y, vc.Y)) > maxY) continue;

                dstI.Add(Map(a)); dstI.Add(Map(b)); dstI.Add(Map(c));
                if (srcC != null && dstC != null && t < srcC.Count) dstC.Add(srcC[t]);
            }

            int Map(int idx)
            {
                if (remap.TryGetValue(idx, out int mapped)) return mapped;
                mapped = dstV.Count;
                dstV.Add(srcV[idx]);
                remap[idx] = mapped;
                return mapped;
            }
        }

        /// Fold another tile in. Tiles are already in world space, so this is a
        /// concatenation with index offsets and nothing else.
        public void Append(Soup other)
        {
            int vb = Verts.Count, lb = LiquidVerts.Count;
            Verts.AddRange(other.Verts);
            foreach (int i in other.Indices) Indices.Add(i + vb);
            LiquidVerts.AddRange(other.LiquidVerts);
            foreach (int i in other.LiquidIndices) LiquidIndices.Add(i + lb);
            LiquidTriClass.AddRange(other.LiquidTriClass);
            TerrainTris += other.TerrainTris; WmoTris += other.WmoTris;
            InteriorTris += other.InteriorTris; DoodadTris += other.DoodadTris;
        }
    }

    /// All transforms below were derived empirically against MODF extents, inter-chunk
    /// edge continuity and doodad uprightness; see notes before changing them.
    /// Rx/Rz ordering is UNRESOLVED: MODF extents cannot separate Rz*Ry*Rx from
    /// Rx*Ry*Rz (53.5% vs 46.5% across 4,002 tilted placements), yet the two differ
    /// by 12.7 yd on average and up to 252 yd. Flip this and compare a tilted WMO
    /// against the game to settle it. Affects only placements with non-zero rot.X/Z.
    public static bool RotationXFirst;

    static Matrix4x4 Rotation(Vector3 deg)
    {
        var rx = Matrix4x4.CreateRotationX(deg.X * MathF.PI / 180f);
        var ry = Matrix4x4.CreateRotationY(deg.Y * MathF.PI / 180f);
        var rz = Matrix4x4.CreateRotationZ(deg.Z * MathF.PI / 180f);
        return RotationXFirst ? rx * ry * rz : rz * ry * rx;
    }

    /// detailStep 1 is full resolution; higher values decimate terrain for overviews.
    /// objects: WMOs and doodads, which dominate the triangle count on busy tiles.
    public Soup BuildTile(TileFiles tile, int detailStep = 1, bool objects = true)
    {
        var soup = new Soup();

        using (var ts = casc.OpenFile((int)tile.Root))
        {
            var terrain = Adt.ReadTerrain(ts);
            foreach (var chunk in terrain.Chunks)
                Terrain.Emit(chunk, soup.Verts, soup.Indices, transpose: false, detailStep);
            soup.TerrainTris = soup.TriCount;

            if (terrain.Mh2o.Length > 0)
                Liquid.EmitMh2o(terrain.Mh2o, terrain.Chunks, soup.LiquidVerts, soup.LiquidIndices,
                                soup.LiquidTriClass, classify, transpose: false, detailStep);
        }

        if (!objects || tile.Obj0 == 0 || !casc.FileExists((int)tile.Obj0)) return soup;

        List<DoodadPlacement> doodads;
        List<WmoPlacement> wmos;
        using (var os = casc.OpenFile((int)tile.Obj0)) (doodads, wmos, _) = Obj0.Read(os);

        foreach (var m in wmos) AddWmo(soup, m, Terrain.Origin);
        soup.WmoTris = soup.TriCount - soup.TerrainTris;

        // interior doodads: the global set (0) plus whichever set this instance selects
        foreach (var m in wmos)
        {
            var (_, _, _, root) = LoadWmo(m.NameId);
            var xform = Instance(m.Pos, m.RotDeg, m.Scale, Terrain.Origin);
            foreach (var def in SelectedDoodads(root, m.DoodadSet))
            {
                var mesh = LoadM2(def.Fdid);
                if (mesh.Indices.Count == 0) continue;
                var local = Matrix4x4.CreateScale(def.Scale) *
                            Matrix4x4.CreateFromQuaternion(def.Rot) *
                            Matrix4x4.CreateTranslation(def.Pos);
                Append(soup.Verts, soup.Indices, mesh, local * xform);
            }
        }
        soup.InteriorTris = soup.TriCount - soup.TerrainTris - soup.WmoTris;

        foreach (var d in doodads)
            Append(soup.Verts, soup.Indices, LoadM2(d.NameId), Instance(d.Pos, d.RotDeg, d.Scale, Terrain.Origin));
        soup.DoodadTris = soup.TriCount - soup.TerrainTris - soup.WmoTris - soup.InteriorTris;

        return soup;
    }

    static IEnumerable<Wmo.DoodadDef> SelectedDoodads(Wmo.Root root, ushort setIndex)
    {
        foreach (int s in setIndex == 0 ? new[] { 0 } : [0, setIndex])
        {
            if (s >= root.Sets.Count) continue;
            var set = root.Sets[s];
            for (uint i = set.Start; i < set.Start + set.Count && i < root.Doodads.Count; i++)
                yield return root.Doodads[(int)i];
        }
    }

    /// Model space -> world, for one placed instance. The axis swap and the world flip
    /// are permutations expressed as matrices so a WMO-local doodad transform composes.
    /// origin is 0 for a WDT global WMO, whose space is already centred on zero.
    static Matrix4x4 Instance(Vector3 pos, Vector3 rotDeg, float scale, float origin)
    {
        var swap = new Matrix4x4(0, 0, 1, 0,
                                 1, 0, 0, 0,
                                 0, 1, 0, 0,
                                 0, 0, 0, 1);
        // (origin - p.Z, origin - p.X, p.Y). Determinant +1 — the sibling that takes
        // p.X into world X is a reflection and mirrors every town layout.
        var flip = new Matrix4x4( 0, -1, 0, 0,
                                  0,  0, 1, 0,
                                 -1,  0, 0, 0,
                                  origin, origin, 0, 1);
        return swap * Matrix4x4.CreateScale(scale) * Rotation(rotDeg) *
               Matrix4x4.CreateTranslation(pos) * flip;
    }

    /// A whole map that is one WMO: dungeons, raids, and most pre-Cata instances.
    public Soup BuildGlobalWmo(WmoPlacement m)
    {
        var soup = new Soup();
        AddWmo(soup, m, origin: 0f);
        soup.WmoTris = soup.TriCount;

        var (_, _, _, root) = LoadWmo(m.NameId);
        var xform = Instance(m.Pos, m.RotDeg, m.Scale, 0f);
        foreach (var def in SelectedDoodads(root, m.DoodadSet))
        {
            var mesh = LoadM2(def.Fdid);
            if (mesh.Indices.Count == 0) continue;
            var local = Matrix4x4.CreateScale(def.Scale) *
                        Matrix4x4.CreateFromQuaternion(def.Rot) *
                        Matrix4x4.CreateTranslation(def.Pos);
            Append(soup.Verts, soup.Indices, mesh, local * xform);
        }
        soup.InteriorTris = soup.TriCount - soup.WmoTris;
        return soup;
    }

    void AddWmo(Soup soup, WmoPlacement m, float origin)
    {
        var (solid, liquid, groupLiquid, _) = LoadWmo(m.NameId);
        var xform = Instance(m.Pos, m.RotDeg, m.Scale, origin);
        Append(soup.Verts, soup.Indices, solid, xform);

        int before = soup.LiquidIndices.Count / 3;
        Append(soup.LiquidVerts, soup.LiquidIndices, liquid, xform);
        var cls = classify((ushort)groupLiquid);
        for (int i = before; i < soup.LiquidIndices.Count / 3; i++) soup.LiquidTriClass.Add(cls);
    }

    static void Append(List<Vector3> verts, List<int> indices, Wmo.Mesh mesh, Matrix4x4 m)
    {
        if (mesh.Indices.Count == 0) return;
        int b = verts.Count;
        foreach (var v in mesh.Verts) verts.Add(Vector3.Transform(v, m));
        foreach (int i in mesh.Indices) indices.Add(b + i);
    }

    (Wmo.Mesh, Wmo.Mesh, uint, Wmo.Root) LoadWmo(uint fdid)
    {
        if (_wmoCache.TryGetValue(fdid, out var cached)) return cached;

        var verts = new List<Vector3>(); var idx = new List<int>();
        var lverts = new List<Vector3>(); var lidx = new List<int>();
        var root = new Wmo.Root([], default, default, [], []);
        uint groupLiquid = 0;

        if (casc.FileExists((int)fdid))
        {
            using (var rs = casc.OpenFile((int)fdid)) root = Wmo.ReadRoot(rs);
            foreach (uint g in root.GroupIds)
            {
                if (g == 0 || !casc.FileExists((int)g)) continue;
                using var gs = casc.OpenFile((int)g);
                var (solid, liquid, gl, flags) = Wmo.ReadGroup(gs);

                // Antiportals are invisible occlusion volumes and unreachable groups are
                // sealed off; both would bake as phantom floors and walls. They are ~11% of
                // groups but only 0.04% of triangles, so this is about correctness, not size.
                var f = (Wmo.GroupFlags)flags;
                if ((f & (Wmo.GroupFlags.Antiportal | Wmo.GroupFlags.Unreachable)) != 0)
                {
                    SkippedGroups++;
                    SkippedGroupTris += solid.TriCount;
                    continue;
                }

                if (gl != 0) groupLiquid = gl;
                int b = verts.Count;
                verts.AddRange(solid.Verts);
                foreach (int i in solid.Indices) idx.Add(b + i);
                int lb = lverts.Count;
                lverts.AddRange(liquid.Verts);
                foreach (int i in liquid.Indices) lidx.Add(lb + i);
            }
        }
        return _wmoCache[fdid] = (new Wmo.Mesh(verts, idx), new Wmo.Mesh(lverts, lidx), groupLiquid, root);
    }

    Wmo.Mesh LoadM2(uint fdid)
    {
        if (_m2Cache.TryGetValue(fdid, out var cached)) return cached;
        var mesh = Wmo.Mesh.Empty;
        if (fdid != 0 && casc.FileExists((int)fdid))
        {
            using var s = casc.OpenFile((int)fdid);
            mesh = M2.ReadCollision(s);
        }
        return _m2Cache[fdid] = mesh;
    }

    /// Surface classes carried all the way through to the bake, so water can be swum
    /// and magma/slime avoided rather than everything collapsing into "not ground".
    public enum Surface : byte
    {
        Terrain = 0, Wmo = 1, Interior = 2, Doodad = 3,
        Water = 4, Ocean = 5, Magma = 6, Slime = 7, UnknownLiquid = 8,
    }

    static Surface Of(LiquidClass c) => c switch
    {
        LiquidClass.Water => Surface.Water,
        LiquidClass.Ocean => Surface.Ocean,
        LiquidClass.Magma => Surface.Magma,
        LiquidClass.Slime => Surface.Slime,
        _ => Surface.UnknownLiquid,
    };

    /// Solid triangles are emitted terrain -> wmo -> interior -> doodad, so the counts
    /// slice them back apart without tracking a class per triangle.
    static IEnumerable<(Surface S, int Start, int Count)> SolidRanges(Soup s)
    {
        int o = 0;
        yield return (Surface.Terrain, o, s.TerrainTris); o += s.TerrainTris;
        yield return (Surface.Wmo, o, s.WmoTris); o += s.WmoTris;
        yield return (Surface.Interior, o, s.InteriorTris); o += s.InteriorTris;
        yield return (Surface.Doodad, o, s.DoodadTris);
    }

    /// One OBJ group per surface class. yUp rotates WoW's Z-up world into the Y-up
    /// convention Recast and most viewers expect.
    public static void WriteObj(Soup soup, string path, bool yUp, bool includeLiquid = true)
    {
        Vector3 C(Vector3 v) => yUp ? new Vector3(v.X, v.Z, -v.Y) : v;

        using var w = new StreamWriter(path);
        w.WriteLine($"# {soup.TriCount} solid ({soup.TerrainTris} terrain, {soup.WmoTris} wmo, " +
                    $"{soup.InteriorTris} interior, {soup.DoodadTris} doodad), {soup.LiquidTris} liquid" +
                    $"{(yUp ? ", y-up" : ", z-up")}");

        foreach (var v in soup.Verts) { var p = C(v); w.WriteLine($"v {p.X:F3} {p.Y:F3} {p.Z:F3}"); }
        if (includeLiquid)
            foreach (var v in soup.LiquidVerts) { var p = C(v); w.WriteLine($"v {p.X:F3} {p.Y:F3} {p.Z:F3}"); }

        foreach (var (surface, start, count) in SolidRanges(soup))
        {
            if (count == 0) continue;
            w.WriteLine($"g {surface.ToString().ToLowerInvariant()}");
            for (int t = start; t < start + count; t++)
            {
                int i = t * 3;
                w.WriteLine($"f {soup.Indices[i] + 1} {soup.Indices[i + 1] + 1} {soup.Indices[i + 2] + 1}");
            }
        }

        if (!includeLiquid) return;
        int off = soup.Verts.Count;
        foreach (var cls in soup.LiquidTriClass.Distinct().OrderBy(c => c))
        {
            w.WriteLine($"g {Of(cls).ToString().ToLowerInvariant()}");
            for (int t = 0; t < soup.LiquidTriClass.Count; t++)
            {
                if (soup.LiquidTriClass[t] != cls) continue;
                int i = t * 3;
                w.WriteLine($"f {soup.LiquidIndices[i] + 1 + off} {soup.LiquidIndices[i + 1] + 1 + off} {soup.LiquidIndices[i + 2] + 1 + off}");
            }
        }
    }

    /// Compact binary twin of the OBJ. A 2.6M-triangle tile is ~180MB as OBJ text
    /// and ~50MB here, which is the difference between viewable and not.
    public static void WriteBinary(Soup soup, string path, bool yUp)
    {
        Vector3 C(Vector3 v) => yUp ? new Vector3(v.X, v.Z, -v.Y) : v;

        int nv = soup.Verts.Count + soup.LiquidVerts.Count;
        int nt = soup.TriCount + soup.LiquidTris;

        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write(0x48534D57);                     // "WMSH"
        w.Write(1);
        w.Write(nv); w.Write(nt);

        foreach (var v in soup.Verts.Concat(soup.LiquidVerts))
        { var p = C(v); w.Write(p.X); w.Write(p.Y); w.Write(p.Z); }

        foreach (int i in soup.Indices) w.Write(i);
        int off = soup.Verts.Count;
        foreach (int i in soup.LiquidIndices) w.Write(i + off);

        foreach (var (surface, _, count) in SolidRanges(soup))
            for (int t = 0; t < count; t++) w.Write((byte)surface);
        foreach (var cls in soup.LiquidTriClass) w.Write((byte)Of(cls));
    }
}

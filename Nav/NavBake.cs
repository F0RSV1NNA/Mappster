using System.Numerics;
using DotRecast.Core;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using DotRecast.Recast;
using DotRecast.Recast.Geom;

namespace MapExtract;

/// Bakes a triangle soup into Detour navmesh tiles.
///
/// The bake is tiled, and has to be: Recast stores polymesh vertex indices as 16 bit,
/// so one mesh cannot exceed 65,535 vertices. A single dense ADT (Stormwind) reaches
/// ~52k on its own, so baking a whole tile as one mesh sits just under a hard wall and
/// anything larger overflows. Tiles are indexed on a global grid derived from the map
/// origin, so tiles baked from different ADTs still line up.
///
/// Recast is Y-up and our world is Z-up, so geometry is rotated on the way in and the
/// debug mesh back out. Per-triangle areas make water swimmable and magma/slime a
/// distinct hazard rather than collapsing into walkable-or-not.
public static class NavBake
{
    // Area ids keep TrinityCore's meanings for ground / water / magma so existing
    // consumers keep working, and add ocean and slime, which the extractor already
    // tells apart. Detour stores the area in 6 bits, so 0..63 are all available.
    //
    //   11 ground  0x01     TC
    //   10 —       0x02     TC's steep ground; we do not emit it
    //    9 water   0x04     TC   fresh water: lakes, rivers, pools
    //    8 magma   0x08     TC
    //    7 slime   0x10     new
    //    6 ocean   0x20     new
    //
    // Suggested filter: ground | water | ocean = 0x25, leaving magma and slime out
    // so a path never routes through them. The old TC mask 0x0D excludes ocean, so
    // a consumer using it will refuse to swim the sea until it is widened.
    public const int AreaNull = 0;
    public const int AreaGround = 11;
    public const int AreaWater = 9;
    public const int AreaMagma = 8;
    public const int AreaSlime = 7;
    public const int AreaOcean = 6;

    public const int FlagGround = 0x01;
    public const int FlagWater = 0x04;
    public const int FlagMagma = 0x08;
    public const int FlagSlime = 0x10;
    public const int FlagOcean = 0x20;

    /// Everything a swimmer may enter; hazards deliberately absent.
    public const int FlagsWalkable = FlagGround | FlagWater | FlagOcean;
    public const int FlagsHazard = FlagMagma | FlagSlime;

    public const float AdtSize = 533.33333f;

    /// Recast X/Z origin for the whole world. Tile indices in the dtMeshHeader are
    /// measured from here, which is what dtNavMesh::getTileAt resolves against.
    public const float WorldOrigin = -32f * AdtSize;

    public sealed class Settings
    {
        // Defaults match the staged TrinityCore tiles exactly: cell 0.2666,
        // radius 0.5333, height 2.133, climb 1.067. The nav DLL's edge-margin
        // logic is tuned against that erosion, so changing it changes steering.
        public float CellSize = 0.26666665f;
        public float CellHeight = 0.26666665f;
        public float AgentHeight = 2.1333332f;
        public float AgentRadius = 0.5333333f;
        public float AgentMaxClimb = 1.0666666f;
        public float AgentMaxSlope = 50f;
        public float MinRegionArea = 2f;
        public float MergeRegionArea = 8f;
        public float EdgeMaxLen = 12f;
        public float EdgeMaxError = 1.3f;
        public int VertsPerPoly = 6;
        public float DetailSampleDist = 6f;
        public float DetailSampleMaxError = 1f;
        /// Diagnostic: force every triangle to this area id. 0 = off.
        public int DebugForceArea;
        /// Diagnostic: tally heightfield spans by area after rasterization.
        public bool DebugSpanTally;

        /// Cells across one ADT. One Detour tile per ADT is what the loader expects.
        public int TileSizeCells => (int)MathF.Ceiling(AdtSize / CellSize);
    }

    /// Row/Col are the ADT indices the nav DLL derives from a world position and uses
    /// to build the filename. GridX/GridZ are the Detour grid indices stored inside the
    /// tile header, measured from WorldOrigin. They are deliberately different numbers.
    public sealed record NavTile(int Row, int Col, int GridX, int GridZ,
                                 DtMeshData Data, int Polys, int Bytes, int MeshVerts);

    public sealed class Result
    {
        public bool Ok;
        public string Message = "";
        public double RasterizeMs, BuildMs, DetourMs;
        public int InputTris, PolyCount, TileCount, FailedTiles;
        /// Recast's polymesh index is 16 bit; anything near 65,535 is about to overflow.
        public int MaxMeshVerts;
        /// Why the first failing tile failed — otherwise a blown tile is silent.
        public string FailMessage = "";
        /// Polygons per Recast area id, so the liquid split is visible not assumed.
        public readonly Dictionary<int, int> PolysByArea = [];
        public string SpanTally = "";
        public long NavDataBytes;
        public List<NavTile> Tiles = [];
        public DtNavMeshParams MeshParams;

        public List<Vector3> DebugVerts = [];
        public List<int> DebugIndices = [];
        public List<int> DebugAreas = [];
        public double TotalMs => RasterizeMs + BuildMs + DetourMs;
    }

    /// World (Z-up) -> Recast, matching the nav DLL's wowToRc: a pure cyclic swap with
    /// no negation. Verified against the staged tiles: ADT[25,30] covers wowX 3200..3733
    /// and wowY 533..1067, and its header reads bmin (533.3, _, 3200.0).
    static RcVec3f ToRc(Vector3 v) => new(v.Y, v.Z, v.X);
    static Vector3 FromRc(float x, float y, float z) => new(z, x, y);

    public static int RowOf(float worldX) => (int)MathF.Floor(32f - worldX / AdtSize);
    public static int ColOf(float worldY) => (int)MathF.Floor(32f - worldY / AdtSize);

    static int AreaOfLiquid(LiquidClass c) => c switch
    {
        LiquidClass.Ocean => AreaOcean,
        LiquidClass.Magma => AreaMagma,
        LiquidClass.Slime => AreaSlime,
        _ => AreaWater,                   // Water, and Unknown treated as water
    };

    public static int FlagOfArea(int area) => area switch
    {
        AreaWater => FlagWater,
        AreaOcean => FlagOcean,
        AreaMagma => FlagMagma,
        AreaSlime => FlagSlime,
        AreaNull => 0,
        _ => FlagGround,
    };

    public static string AreaName(int area) => area switch
    {
        AreaGround => "ground", AreaWater => "water", AreaOcean => "ocean",
        AreaMagma => "magma", AreaSlime => "slime", _ => "none",
    };

    /// World-space (Z-up) rectangle a bake is allowed to emit tiles for.
    public readonly record struct Clip(float MinX, float MinY, float MaxX, float MaxY)
    {
        /// The world rect of one ADT tile.
        public static Clip ForAdt(int tileX, int tileY)
        {
            const float T = 533.33333f;
            return new Clip((32 - tileX - 1) * T, (32 - tileY - 1) * T, (32 - tileX) * T, (32 - tileY) * T);
        }
    }

    public static Result Bake(Extractor.Soup soup, Settings s, CancellationToken ct = default,
                              Clip? clip = null)
    {
        var r = new Result();
        int solidTris = soup.TriCount, liquidTris = soup.LiquidTris;
        r.InputTris = solidTris + liquidTris;
        if (r.InputTris == 0) { r.Message = "no geometry"; return r; }

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // ---- flatten to Recast arrays, in Y-up ------------------------------
        int nv = soup.Verts.Count + soup.LiquidVerts.Count;
        var verts = new float[nv * 3];
        int w = 0;
        foreach (var v in soup.Verts) { var p = ToRc(v); verts[w++] = p.X; verts[w++] = p.Y; verts[w++] = p.Z; }
        foreach (var v in soup.LiquidVerts) { var p = ToRc(v); verts[w++] = p.X; verts[w++] = p.Y; verts[w++] = p.Z; }

        var tris = new int[r.InputTris * 3];
        var areas = new int[r.InputTris];
        for (int i = 0; i < solidTris * 3; i++) tris[i] = soup.Indices[i];
        int liquidBase = soup.Verts.Count;
        for (int i = 0; i < liquidTris * 3; i++) tris[solidTris * 3 + i] = soup.LiquidIndices[i] + liquidBase;

        float slopeCos = MathF.Cos(s.AgentMaxSlope * MathF.PI / 180f);
        for (int t = 0; t < solidTris; t++)
            areas[t] = SlopeOk(verts, tris, t, slopeCos) ? AreaGround : AreaNull;
        for (int t = 0; t < liquidTris; t++)
            areas[solidTris + t] = AreaOfLiquid(soup.LiquidTriClass[t]);

        if (s.DebugForceArea != 0)
            for (int t = 0; t < areas.Length; t++) areas[t] = s.DebugForceArea;

        var bmin = new RcVec3f(float.MaxValue, float.MaxValue, float.MaxValue);
        var bmax = new RcVec3f(float.MinValue, float.MinValue, float.MinValue);
        for (int i = 0; i < nv; i++)
        {
            bmin = new RcVec3f(MathF.Min(bmin.X, verts[i * 3]), MathF.Min(bmin.Y, verts[i * 3 + 1]), MathF.Min(bmin.Z, verts[i * 3 + 2]));
            bmax = new RcVec3f(MathF.Max(bmax.X, verts[i * 3]), MathF.Max(bmax.Y, verts[i * 3 + 1]), MathF.Max(bmax.Z, verts[i * 3 + 2]));
        }
        r.RasterizeMs = sw.Elapsed.TotalMilliseconds;

        // ---- config -----------------------------------------------------------
        int border = RcConfig.CalcBorder(s.AgentRadius, s.CellSize);
        var cfg = new RcConfig(
            true, s.TileSizeCells, s.TileSizeCells, border, RcPartition.WATERSHED,
            s.CellSize, s.CellHeight, s.AgentMaxSlope, s.AgentHeight, s.AgentRadius, s.AgentMaxClimb,
            s.MinRegionArea, s.MergeRegionArea, s.EdgeMaxLen, s.EdgeMaxError, s.VertsPerPoly,
            s.DetailSampleDist, s.DetailSampleMaxError, true, true, true,
            new RcAreaModification(AreaGround), true);

        // One Detour tile per ADT, on the grid the nav DLL resolves against.
        const float tws = AdtSize;
        int tx0 = (int)MathF.Floor((bmin.X - WorldOrigin) / tws);
        int tx1 = (int)MathF.Floor((bmax.X - WorldOrigin) / tws);
        int tz0 = (int)MathF.Floor((bmin.Z - WorldOrigin) / tws);
        int tz1 = (int)MathF.Floor((bmax.Z - WorldOrigin) / tws);

        r.MeshParams = new DtNavMeshParams
        {
            orig = new RcVec3f(WorldOrigin, 0, WorldOrigin),
            tileWidth = tws,
            tileHeight = tws,
            maxTiles = 1 << 14,
            maxPolys = 1 << 16,
        };

        var geom = new SoupGeom(verts, tris, bmin, bmax);
        var ctx = new RcContext();
        var builder = new RcBuilder();

        // ---- per tile ----------------------------------------------------------
        sw.Restart();
        double detourMs = 0;

        for (int tz = tz0; tz <= tz1; tz++)
            for (int tx = tx0; tx <= tx1; tx++)
            {
                if (ct.IsCancellationRequested) { r.Message = "cancelled"; return r; }

                // When baking one ADT at a time we still load its neighbours so the
                // border ring has geometry, but only emit tiles centred on this ADT —
                // otherwise adjacent bakes would fight over the same tile index.
                if (clip is { } c)
                {
                    // tile centre back in world (Z-up): rc.X is wowY, rc.Z is wowX
                    float wy = WorldOrigin + (tx + 0.5f) * tws;
                    float wx = WorldOrigin + (tz + 0.5f) * tws;
                    if (wx < c.MinX || wx >= c.MaxX || wy < c.MinY || wy >= c.MaxY) continue;
                }

                float minX = WorldOrigin + tx * tws, minZ = WorldOrigin + tz * tws;
                var tmin = new RcVec3f(minX, bmin.Y, minZ);
                var tmax = new RcVec3f(minX + tws, bmax.Y, minZ + tws);

                // the border ring lets neighbouring tiles agree on their shared edge
                float pad = border * s.CellSize;
                var hfMin = new RcVec3f(tmin.X - pad, tmin.Y, tmin.Z - pad);
                var hfMax = new RcVec3f(tmax.X + pad, tmax.Y, tmax.Z + pad);
                int hw = s.TileSizeCells + border * 2, hh = s.TileSizeCells + border * 2;

                var solid = new RcHeightfield(hw, hh, hfMin, hfMax, s.CellSize, s.CellHeight, border);

                // Rasterize every triangle; Recast clips to the heightfield bounds.
                //
                // Do NOT route this through RcTriMesh.GetChunksOverlappingRect: the chunky
                // mesh reorders triangles, so node.i does not index our per-triangle area
                // array and every area comes out wrong. Ground dominates, so the damage
                // was invisible — it just silently erased every water and lava surface.
                // With one tile per ADT there is nothing to cull anyway.
                int rasterized = r.InputTris;
                RcRasterizations.RasterizeTriangles(ctx, verts, tris, areas, r.InputTris, solid, cfg.WalkableClimb);
                if (rasterized == 0) continue;

                if (s.DebugSpanTally)
                {
                    var tally = new Dictionary<int, int>();
                    for (int i = 0; i < solid.spans.Length; i++)
                        for (var sp = solid.spans[i]; sp != null; sp = sp.next)
                            tally[sp.area] = tally.GetValueOrDefault(sp.area) + 1;
                    r.SpanTally = string.Join("  ", tally.OrderByDescending(k => k.Value)
                        .Select(k => $"{AreaName(k.Key)}({k.Key}) {k.Value:N0}"));
                }

                RcBuilderResult built;
                try { built = builder.Build(ctx, tx, tz, geom, cfg, solid, false); }
                catch (Exception e)
                {
                    r.FailedTiles++;
                    if (r.FailMessage.Length == 0) r.FailMessage = $"{e.GetType().Name}: {e.Message}";
                    continue;
                }

                var pmesh = built.Mesh;
                var dmesh = built.MeshDetail;
                if (pmesh == null || pmesh.npolys == 0) continue;

                var t0 = System.Diagnostics.Stopwatch.StartNew();
                var data = ToDetour(pmesh, dmesh, s, tx, tz, tmin, tmax);
                detourMs += t0.Elapsed.TotalMilliseconds;
                if (data == null) { r.FailedTiles++; continue; }

                // filename indices the nav DLL derives from a world position
                float wyc = WorldOrigin + (tx + 0.5f) * tws;
                float wxc = WorldOrigin + (tz + 0.5f) * tws;
                int row = RowOf(wxc), col = ColOf(wyc);

                int bytes = EstimateBytes(data);
                r.Tiles.Add(new NavTile(row, col, tx, tz, data, pmesh.npolys, bytes, pmesh.nverts));
                r.PolyCount += pmesh.npolys;
                r.MaxMeshVerts = Math.Max(r.MaxMeshVerts, pmesh.nverts);
                for (int i = 0; i < pmesh.npolys; i++)
                {
                    int a = pmesh.areas[i];
                    r.PolysByArea[a] = r.PolysByArea.GetValueOrDefault(a) + 1;
                }
                r.NavDataBytes += bytes;
                AppendDebug(pmesh, dmesh, r);
            }

        r.BuildMs = sw.Elapsed.TotalMilliseconds - detourMs;
        r.DetourMs = detourMs;
        r.TileCount = r.Tiles.Count;

        if (r.TileCount == 0)
        {
            r.Message = r.FailMessage.Length > 0
                ? $"{r.FailedTiles} tile(s) failed — {r.FailMessage}"
                : "no walkable polygons";
            return r;
        }
        r.Ok = true;
        r.Message = $"{r.TileCount} tiles, {r.PolyCount:N0} polys";
        return r;
    }

    static DtMeshData? ToDetour(RcPolyMesh pmesh, RcPolyMeshDetail? dmesh, Settings s,
                                int tx, int tz, RcVec3f tmin, RcVec3f tmax)
    {
        var flags = new int[pmesh.npolys];
        for (int i = 0; i < pmesh.npolys; i++) flags[i] = FlagOfArea(pmesh.areas[i]);

        var option = new DtNavMeshCreateParams
        {
            verts = pmesh.verts, vertCount = pmesh.nverts,
            polys = pmesh.polys, polyAreas = pmesh.areas, polyFlags = flags,
            polyCount = pmesh.npolys, nvp = pmesh.nvp,
            detailMeshes = dmesh?.meshes, detailVerts = dmesh?.verts,
            detailVertsCount = dmesh?.nverts ?? 0,
            detailTris = dmesh?.tris, detailTriCount = dmesh?.ntris ?? 0,
            walkableHeight = s.AgentHeight, walkableRadius = s.AgentRadius, walkableClimb = s.AgentMaxClimb,
            bmin = pmesh.bmin, bmax = pmesh.bmax,
            cs = s.CellSize, ch = s.CellHeight,
            tileX = tx, tileZ = tz, tileLayer = 0,
            buildBvTree = true,
        };

        try { return DtNavMeshBuilder.CreateNavMeshData(option); }
        catch { return null; }
    }

    static bool SlopeOk(float[] verts, int[] tris, int t, float slopeCos)
    {
        int a = tris[t * 3] * 3, b = tris[t * 3 + 1] * 3, c = tris[t * 3 + 2] * 3;
        float e0x = verts[b] - verts[a], e0y = verts[b + 1] - verts[a + 1], e0z = verts[b + 2] - verts[a + 2];
        float e1x = verts[c] - verts[a], e1y = verts[c + 1] - verts[a + 1], e1z = verts[c + 2] - verts[a + 2];
        float nx = e0y * e1z - e0z * e1y, ny = e0z * e1x - e0x * e1z, nz = e0x * e1y - e0y * e1x;
        float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
        if (len < 1e-9f) return false;
        return ny / len > slopeCos;     // Y is up inside Recast
    }

    static int EstimateBytes(DtMeshData d) =>
        (d.verts?.Length ?? 0) * 4 + (d.polys?.Length ?? 0) * 32 +
        (d.detailVerts?.Length ?? 0) * 4 + (d.detailTris?.Length ?? 0) +
        (d.bvTree?.Length ?? 0) * 16;

    static void AppendDebug(RcPolyMesh pmesh, RcPolyMeshDetail? dmesh, Result r)
    {
        if (dmesh == null || dmesh.nmeshes == 0) return;
        for (int i = 0; i < dmesh.nmeshes; i++)
        {
            int bverts = dmesh.meshes[i * 4];
            int btris = dmesh.meshes[i * 4 + 2];
            int ntris = dmesh.meshes[i * 4 + 3];
            int area = i < pmesh.npolys ? pmesh.areas[i] : AreaGround;

            for (int j = 0; j < ntris; j++)
            {
                int b = r.DebugVerts.Count;
                for (int k = 0; k < 3; k++)
                {
                    int v = (bverts + dmesh.tris[(btris + j) * 4 + k]) * 3;
                    r.DebugVerts.Add(FromRc(dmesh.verts[v], dmesh.verts[v + 1], dmesh.verts[v + 2]));
                }
                r.DebugIndices.Add(b); r.DebugIndices.Add(b + 1); r.DebugIndices.Add(b + 2);
                r.DebugAreas.Add(area);
            }
        }
    }

    /// Bounds, convex volumes and off-mesh links. Rasterization is done by hand above
    /// so that each triangle keeps its surface class.
    sealed class SoupGeom(float[] verts, int[] tris, RcVec3f bmin, RcVec3f bmax) : IRcInputGeomProvider
    {
        // Built lazily: RcTriMesh constructs a chunky spatial index, which on a dense
        // ADT is millions of triangles of work we no longer need now that rasterization
        // is a single pass.
        RcTriMesh? _mesh;
        readonly List<RcConvexVolume> _volumes = [];
        readonly List<RcOffMeshConnection> _offMesh = [];

        public RcTriMesh GetMesh() => _mesh ??= new RcTriMesh(verts, tris);
        public RcVec3f GetMeshBoundsMin() => bmin;
        public RcVec3f GetMeshBoundsMax() => bmax;
        public IEnumerable<RcTriMesh> Meshes() => [GetMesh()];
        public IList<RcConvexVolume> ConvexVolumes() => _volumes;
        public void AddConvexVolume(RcConvexVolume convexVolume) => _volumes.Add(convexVolume);
        public List<RcOffMeshConnection> GetOffMeshConnections() => _offMesh;
        public void AddOffMeshConnection(RcVec3f start, RcVec3f end, float radius, bool bidir, int area, int flags) =>
            _offMesh.Add(new RcOffMeshConnection(start, end, radius, bidir, area, flags));
        public void RemoveOffMeshConnections(Predicate<RcOffMeshConnection> filter) => _offMesh.RemoveAll(filter);
    }
}

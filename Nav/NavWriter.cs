using DotRecast.Core;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using DotRecast.Detour.Io;

namespace MapExtract;

/// Writes baked tiles in TrinityCore's mmap layout.
///
///   {mapId:000}.mmap              dtNavMeshParams for the whole map
///   {mapId:000}{tx:00}{ty:00}.mmtile   header + one dtMeshData blob
///
/// The tile payload is Detour's own C-struct serialisation (DtMeshDataWriter with
/// cCompatibility), which is byte-identical to what a C++ dtNavMesh expects, so these
/// load in anything that reads TrinityCore mmaps.
///
/// One difference worth knowing: TrinityCore puts exactly one Detour tile per ADT.
/// We subdivide, because a dense ADT overruns Recast's 16-bit polymesh vertex limit.
/// So a single ADT produces several .mmtile files, named by Detour tile index.
public static class NavWriter
{
    const uint MmapMagic = 0x4D4D4150;      // 'MMAP'
    const uint DtVersion = 7;               // DT_NAVMESH_VERSION
    const uint MmapVersion = 16;            // matches the mmaps in C:\cmods\mmaps

    public static string MapFile(int mapId) => $"{mapId:0000}.mmap";
    public static string TileFile(int mapId, int tx, int tz) => $"{mapId:0000}_{tx:00}_{tz:00}.mmtile";

    /// magic + version + dtNavMeshParams + padding = 40 bytes, same as a TC .mmap.
    public static void WriteMapParams(string dir, int mapId, DtNavMeshParams p)
    {
        Directory.CreateDirectory(dir);
        using var fs = File.Create(Path.Combine(dir, MapFile(mapId)));
        using var w = new BinaryWriter(fs);
        w.Write(MmapMagic);
        w.Write(MmapVersion);
        w.Write(p.orig.X); w.Write(p.orig.Y); w.Write(p.orig.Z);
        w.Write(p.tileWidth);
        w.Write(p.tileHeight);
        w.Write(p.maxTiles);
        w.Write(p.maxPolys);
        w.Write(0);                          // padding, as TC has
    }

    /// One tile. Returns the bytes written.
    public static int WriteTile(string dir, int mapId, NavBake.NavTile tile, bool usesLiquids = true)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, TileFile(mapId, tile.Row, tile.Col));

        // serialise the tile first: the header needs its size
        byte[] payload;
        using (var ms = new MemoryStream())
        {
            using var bw = new BinaryWriter(ms);
            new DtMeshDataWriter().Write(bw, tile.Data, RcByteOrder.LITTLE_ENDIAN, true);
            payload = ms.ToArray();
        }

        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write(MmapMagic);
        w.Write(DtVersion);
        w.Write(MmapVersion);
        w.Write((uint)payload.Length);
        w.Write(usesLiquids);
        w.Write(new byte[3]);                 // padding to 4-byte alignment
        w.Write(payload);
        return payload.Length + 20;
    }

    /// Read the files back and assemble a real navmesh, proving the output is loadable
    /// rather than merely written. Returns (tiles loaded, polygons, any error).
    public static (int Tiles, int Polys, string Error) LoadBack(string dir, int mapId)
    {
        var mapPath = Path.Combine(dir, MapFile(mapId));
        if (!File.Exists(mapPath)) return (0, 0, "no .mmap file");

        DtNavMeshParams p;
        using (var fs = File.OpenRead(mapPath))
        using (var br = new BinaryReader(fs))
        {
            if (br.ReadUInt32() != MmapMagic) return (0, 0, "bad .mmap magic");
            br.ReadUInt32();                 // version
            p = new DtNavMeshParams
            {
                orig = new RcVec3f(br.ReadSingle(), br.ReadSingle(), br.ReadSingle()),
                tileWidth = br.ReadSingle(),
                tileHeight = br.ReadSingle(),
                maxTiles = br.ReadInt32(),
                maxPolys = br.ReadInt32(),
            };
        }

        var navMesh = new DtNavMesh();
        var st = navMesh.Init(in p, 6);
        if (st.Failed()) return (0, 0, "navmesh init failed");

        int tiles = 0, polys = 0;
        var reader = new DtMeshDataReader();

        foreach (var file in Directory.GetFiles(dir, $"{mapId:0000}_*.mmtile"))
        {
            using var fs = File.OpenRead(file);
            using var br = new BinaryReader(fs);
            if (br.ReadUInt32() != MmapMagic) return (tiles, polys, $"bad magic in {Path.GetFileName(file)}");
            br.ReadUInt32();                       // dt version
            br.ReadUInt32();                       // mmap version
            br.ReadUInt32();                       // payload size
            br.ReadBoolean(); br.ReadBytes(3);     // usesLiquids + padding

            DtMeshData data;
            try { data = reader.Read(br, 6); }
            catch (Exception e) { return (tiles, polys, $"{Path.GetFileName(file)}: {e.Message}"); }

            long refs = 0;
            if (navMesh.AddTile(data, 0, 0, out refs).Failed())
                return (tiles, polys, $"AddTile rejected {Path.GetFileName(file)}");

            tiles++;
            polys += data.header?.polyCount ?? 0;
        }
        return (tiles, polys, "");
    }

    public static (int Files, long Bytes) WriteAll(string dir, int mapId, NavBake.Result r)
    {
        WriteMapParams(dir, mapId, r.MeshParams);
        long total = 0;
        foreach (var t in r.Tiles) total += WriteTile(dir, mapId, t);
        return (r.Tiles.Count, total);
    }
}

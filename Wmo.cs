using System.Numerics;
using System.Text;

namespace MapExtract;

public static class Wmo
{
    /// MOPY per-triangle material flags.
    [Flags]
    enum Mopy : byte
    {
        NoCamCollide = 0x01,
        Detail       = 0x02,
        NoCollision  = 0x04,
        Hint         = 0x08,
        Render       = 0x10,
        CollideHit   = 0x40,
    }

    public record Mesh(List<Vector3> Verts, List<int> Indices)
    {
        public static Mesh Empty => new([], []);
        public int TriCount => Indices.Count / 3;
    }

    /// One interior doodad instance, in WMO model space.
    public record DoodadDef(uint Fdid, Vector3 Pos, Quaternion Rot, float Scale);
    public record DoodadSet(uint Start, uint Count);

    public record Root(uint[] GroupIds, Vector3 BoxMin, Vector3 BoxMax,
                       List<DoodadSet> Sets, List<DoodadDef> Doodads);

    /// Group file ids come from the root's GFID chunk (name-based lookup is long gone).
    public static Root ReadRoot(Stream rootWmo, bool quatWFirst = false)
    {
        var br = new BinaryReader(rootWmo);
        uint nGroups = 0;
        uint[] gfids = [];
        Vector3 lo = default, hi = default;
        var sets = new List<DoodadSet>();
        var defs = new List<DoodadDef>();
        uint[] modi = [];

        while (br.BaseStream.Position + 8 <= br.BaseStream.Length)
        {
            var magic = Chunk.Magic(br);
            uint size = br.ReadUInt32();
            long next = br.BaseStream.Position + size;

            switch (magic)
            {
                case "MOHD":
                    br.ReadUInt32(); nGroups = br.ReadUInt32();
                    br.BaseStream.Position += 28;               // through wmoID
                    lo = Vec3(br); hi = Vec3(br);
                    break;
                case "GFID":
                    gfids = new uint[size / 4];
                    for (int i = 0; i < gfids.Length; i++) gfids[i] = br.ReadUInt32();
                    break;
                case "MODI":
                    modi = new uint[size / 4];
                    for (int i = 0; i < modi.Length; i++) modi[i] = br.ReadUInt32();
                    break;
                case "MODS":
                    for (int i = 0; i < size / 32; i++)
                    {
                        br.BaseStream.Position += 20;           // set name
                        sets.Add(new DoodadSet(br.ReadUInt32(), br.ReadUInt32()));
                        br.ReadUInt32();                        // padding
                    }
                    break;
                case "MODD":
                    for (int i = 0; i < size / 40; i++)
                    {
                        uint packed = br.ReadUInt32();
                        uint nameIndex = packed & 0xFFFFFF;     // top byte is flags
                        Vector3 pos = Vec3(br);
                        float a = br.ReadSingle(), b = br.ReadSingle(), c = br.ReadSingle(), d = br.ReadSingle();
                        var q = quatWFirst ? new Quaternion(b, c, d, a) : new Quaternion(a, b, c, d);
                        float scale = br.ReadSingle();
                        br.ReadUInt32();                        // colour
                        defs.Add(new DoodadDef(nameIndex, pos, q, scale));
                    }
                    break;
            }

            br.BaseStream.Position = next;
        }

        // MODD carries an index into MODI now that names are gone.
        for (int i = 0; i < defs.Count; i++)
            defs[i] = defs[i] with { Fdid = defs[i].Fdid < modi.Length ? modi[defs[i].Fdid] : 0 };

        // GFID covers every doodad set variant; the first nGroups entries are the base set.
        return new Root(nGroups > 0 && gfids.Length >= nGroups ? gfids[..(int)nGroups] : gfids,
                        lo, hi, sets, defs);
    }

    static Vector3 Vec3(BinaryReader br) => new(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());

    /// MOGP header flags that matter for collision.
    [Flags]
    public enum GroupFlags : uint
    {
        Bsp          = 0x1,
        Exterior     = 0x8,
        Unreachable  = 0x80,
        HasLiquid    = 0x1000,
        Interior     = 0x2000,
        AlwaysDraw   = 0x10000,
        ShowSkybox   = 0x40000,
        IsOcean      = 0x80000,
        Antiportal   = 0x4000000,
    }

    public record GroupData(Mesh Solid, Mesh Liquid, uint GroupLiquid, uint Flags);

    /// Collision triangles from one group file, in WMO model space, plus any interior liquid.
    public static GroupData ReadGroup(Stream group)
    {
        var br = new BinaryReader(group);
        Mopy[] mopy = [];
        ushort[] movi = [];
        var movt = new List<Vector3>();
        var liquid = Mesh.Empty;
        uint groupLiquid = 0, flags = 0;

        while (br.BaseStream.Position + 8 <= br.BaseStream.Length)
        {
            var magic = Chunk.Magic(br);
            uint size = br.ReadUInt32();
            long next = br.BaseStream.Position + size;

            switch (magic)
            {
                case "MOGP":
                    long h = br.BaseStream.Position;
                    br.BaseStream.Position = h + 0x08;
                    flags = br.ReadUInt32();
                    br.BaseStream.Position = h + 0x34;
                    groupLiquid = br.ReadUInt32();
                    br.BaseStream.Position = h + 68;  // header, then subchunks follow inline
                    next = br.BaseStream.Position;    // re-enter the loop inside MOGP
                    break;
                case "MOPY":
                    mopy = new Mopy[size / 2];
                    for (int i = 0; i < mopy.Length; i++) { mopy[i] = (Mopy)br.ReadByte(); br.ReadByte(); }
                    break;
                case "MOVI":
                    movi = new ushort[size / 2];
                    for (int i = 0; i < movi.Length; i++) movi[i] = br.ReadUInt16();
                    break;
                case "MOVT":
                    for (int i = 0; i < size / 12; i++)
                        movt.Add(new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle()));
                    break;
                case "MLIQ":
                    liquid = Liquid.ReadMliq(br.ReadBytes((int)size));
                    break;
            }

            br.BaseStream.Position = next;
        }

        if (movi.Length == 0 || movt.Count == 0) return new GroupData(Mesh.Empty, liquid, groupLiquid, flags);

        var indices = new List<int>(movi.Length);
        for (int t = 0; t < movi.Length / 3; t++)
        {
            if (t < mopy.Length && (mopy[t] & Mopy.NoCollision) != 0) continue;
            indices.Add(movi[t * 3]); indices.Add(movi[t * 3 + 1]); indices.Add(movi[t * 3 + 2]);
        }
        return new GroupData(new Mesh(movt, indices), liquid, groupLiquid, flags);
    }
}

public static class Chunk
{
    public static string Magic(BinaryReader br) =>
        Encoding.ASCII.GetString(br.ReadBytes(4).Reverse().ToArray());
}

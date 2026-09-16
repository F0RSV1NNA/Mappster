using System.Numerics;
using System.Text;

namespace MapExtract;

/// One tile's split-ADT file ids, as stored in the WDT's MAID chunk.
public record TileFiles(int X, int Y, uint Root, uint Obj0, uint Obj1, uint Tex0, uint Lod);

public static class Wdt
{
    /// Maps with no ADT tiles (most classic dungeons and raids) are a single WMO
    /// placed by a MODF in the WDT itself. Its coordinates are centred on zero
    /// rather than the 0..34133 placement space ADT MODFs use.
    public static WmoPlacement? ReadGlobalWmo(Stream wdt)
    {
        var br = new BinaryReader(wdt);

        while (br.BaseStream.Position + 8 <= br.BaseStream.Length)
        {
            var magic = Encoding.ASCII.GetString(br.ReadBytes(4).Reverse().ToArray());
            uint size = br.ReadUInt32();
            long next = br.BaseStream.Position + size;

            if (magic == "MODF" && size >= 64)
            {
                uint nameId = br.ReadUInt32();
                br.ReadUInt32();                                  // uniqueId
                Vector3 pos = Vec3(br), rot = Vec3(br), lo = Vec3(br), hi = Vec3(br);
                br.ReadUInt16();                                  // flags
                ushort doodadSet = br.ReadUInt16();
                br.ReadUInt16();                                  // nameSet
                ushort rawScale = br.ReadUInt16();
                return new WmoPlacement(nameId, pos, rot, lo, hi, doodadSet,
                                        rawScale == 0 ? 1f : rawScale / 1024f);
            }

            br.BaseStream.Position = next;
        }
        return null;
    }

    static Vector3 Vec3(BinaryReader br) => new(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());

    /// Reads MAID and returns every tile that actually has a root ADT.
    public static List<TileFiles> ReadTiles(Stream wdt)
    {
        var br = new BinaryReader(wdt);
        List<TileFiles> tiles = new();

        while (br.BaseStream.Position + 8 <= br.BaseStream.Length)
        {
            // chunk magic is stored reversed on disk
            var magic = Encoding.ASCII.GetString(br.ReadBytes(4).Reverse().ToArray());
            uint size = br.ReadUInt32();
            long next = br.BaseStream.Position + size;

            if (magic == "MAID")
            {
                // stride grows as Blizzard appends fields; derive it instead of hardcoding
                int uintsPerTile = (int)(size / 4096 / 4);
                for (int y = 0; y < 64; y++)
                    for (int x = 0; x < 64; x++)
                    {
                        var ids = new uint[uintsPerTile];
                        for (int i = 0; i < uintsPerTile; i++) ids[i] = br.ReadUInt32();
                        // MAID's outer index is the world-X row, so it becomes TileFiles.X
                        if (ids[0] != 0)
                            tiles.Add(new TileFiles(y, x, ids[0],
                                uintsPerTile > 1 ? ids[1] : 0, uintsPerTile > 2 ? ids[2] : 0,
                                uintsPerTile > 3 ? ids[3] : 0, uintsPerTile > 4 ? ids[4] : 0));
                    }
            }

            br.BaseStream.Position = next;
        }
        return tiles;
    }
}

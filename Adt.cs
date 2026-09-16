using System.Text;

namespace MapExtract;

public static class Adt
{
    const float TileSize = 533.33333f;
    const float ChunkSize = TileSize / 16f;
    const float MapOrigin = 32f * TileSize;

    /// One terrain chunk: 145 heights (9x9 interleaved with 8x8) plus its hole mask.
    public record Mcnk(int IndexX, int IndexY, float PosX, float PosY, float PosZ, float[] Heights, ulong Holes);

    public record TerrainData(List<Mcnk> Chunks, byte[] Mh2o);

    /// The area id most chunks on this tile belong to. Reads MCNK headers only —
    /// scanning a whole continent for zone names does not need the heightmaps.
    public static int DominantAreaId(Stream adt)
    {
        var br = new BinaryReader(adt);
        var counts = new Dictionary<int, int>();

        while (br.BaseStream.Position + 8 <= br.BaseStream.Length)
        {
            var magic = Magic(br);
            uint size = br.ReadUInt32();
            long next = br.BaseStream.Position + size;

            if (magic == "MCNK" && size >= 128)
            {
                var h = br.ReadBytes(128);
                int area = BitConverter.ToInt32(h, 0x34);
                if (area > 0) counts[area] = counts.GetValueOrDefault(area) + 1;
            }

            br.BaseStream.Position = next;
        }
        return counts.Count == 0 ? 0 : counts.MaxBy(k => k.Value).Key;
    }

    public static TerrainData ReadTerrain(Stream adt)
    {
        var br = new BinaryReader(adt);
        var result = new List<Mcnk>();
        byte[] mh2o = [];

        while (br.BaseStream.Position + 8 <= br.BaseStream.Length)
        {
            var magic = Magic(br);
            uint size = br.ReadUInt32();
            long body = br.BaseStream.Position, next = body + size;

            if (magic == "MCNK") result.Add(ReadMcnk(br, body, next));
            else if (magic == "MH2O") mh2o = br.ReadBytes((int)size);

            br.BaseStream.Position = next;
        }
        return new TerrainData(result, mh2o);
    }

    static Mcnk ReadMcnk(BinaryReader br, long body, long end)
    {
        var h = br.ReadBytes(128);
        int ix = BitConverter.ToInt32(h, 0x04), iy = BitConverter.ToInt32(h, 0x08);
        ulong holes = BitConverter.ToUInt64(h, 0x14);           // holes_high_res in Cata+
        ushort holesLow = BitConverter.ToUInt16(h, 0x3C);
        // 0x68 is world X, 0x6C is world Y — verified against Stormwind/Ironforge/Booty Bay
        float px = BitConverter.ToSingle(h, 0x68), py = BitConverter.ToSingle(h, 0x6C), pz = BitConverter.ToSingle(h, 0x70);

        float[] heights = new float[145];
        while (br.BaseStream.Position + 8 <= end)              // subchunks follow the header
        {
            var sub = Magic(br);
            uint ssize = br.ReadUInt32();
            long snext = br.BaseStream.Position + ssize;
            if (sub == "MCVT")
                for (int i = 0; i < 145; i++) heights[i] = br.ReadSingle();
            br.BaseStream.Position = snext;
        }

        if (holes == 0 && holesLow != 0) holes = ExpandLowResHoles(holesLow);
        return new Mcnk(ix, iy, px, py, pz, heights, holes);
    }

    /// Low-res holes are 4x4; the high-res mask the rest of the pipeline uses is 8x8.
    static ulong ExpandLowResHoles(ushort low)
    {
        ulong hi = 0;
        for (int i = 0; i < 16; i++)
        {
            if ((low & (1 << i)) == 0) continue;
            int qx = (i % 4) * 2, qy = (i / 4) * 2;
            for (int dy = 0; dy < 2; dy++)
                for (int dx = 0; dx < 2; dx++)
                    hi |= 1UL << ((qy + dy) * 8 + (qx + dx));
        }
        return hi;
    }

    static string Magic(BinaryReader br) => Encoding.ASCII.GetString(br.ReadBytes(4).Reverse().ToArray());

    /// World position of an outer vertex. WoW's axes are inverted relative to tile indices.
    public static (float x, float y) VertexWorldXY(Mcnk c, int row, int col) =>
        (c.PosX - row * (ChunkSize / 8f), c.PosY - col * (ChunkSize / 8f));
}

using System.Numerics;
using System.Text;

namespace MapExtract;

public record DoodadPlacement(uint NameId, Vector3 Pos, Vector3 RotDeg, float Scale);
public record WmoPlacement(uint NameId, Vector3 Pos, Vector3 RotDeg, Vector3 ExtMin, Vector3 ExtMax, ushort DoodadSet, float Scale);

/// Placements live in the _obj0 split file. Since Legion the name ids are FileDataIDs
/// directly, so MMDX/MWMO are absent; we detect that rather than trusting an MPHD flag.
public static class Obj0
{
    public static (List<DoodadPlacement> Doodads, List<WmoPlacement> Wmos, bool NamesAreFdids) Read(Stream s)
    {
        var br = new BinaryReader(s);
        var doodads = new List<DoodadPlacement>();
        var wmos = new List<WmoPlacement>();
        bool sawNameTables = false;

        while (br.BaseStream.Position + 8 <= br.BaseStream.Length)
        {
            var magic = Encoding.ASCII.GetString(br.ReadBytes(4).Reverse().ToArray());
            uint size = br.ReadUInt32();
            long next = br.BaseStream.Position + size;

            switch (magic)
            {
                case "MMDX" or "MWMO" when size > 0:
                    sawNameTables = true;
                    break;

                case "MDDF":
                    for (int i = 0; i < size / 36; i++)
                    {
                        uint nameId = br.ReadUInt32();
                        br.ReadUInt32();                        // uniqueId
                        Vector3 pos = Vec3(br), rot = Vec3(br);
                        float scale = br.ReadUInt16() / 1024f;
                        br.ReadUInt16();                        // flags
                        doodads.Add(new DoodadPlacement(nameId, pos, rot, scale));
                    }
                    break;

                case "MODF":
                    for (int i = 0; i < size / 64; i++)
                    {
                        uint nameId = br.ReadUInt32();
                        br.ReadUInt32();                        // uniqueId
                        Vector3 pos = Vec3(br), rot = Vec3(br), lo = Vec3(br), hi = Vec3(br);
                        br.ReadUInt16();                        // flags
                        ushort doodadSet = br.ReadUInt16();
                        br.ReadUInt16();                        // nameSet
                        ushort rawScale = br.ReadUInt16();      // 1024 == 1.0, 0 on older WMOs
                        wmos.Add(new WmoPlacement(nameId, pos, rot, lo, hi, doodadSet,
                                                  rawScale == 0 ? 1f : rawScale / 1024f));
                    }
                    break;
            }

            br.BaseStream.Position = next;
        }
        return (doodads, wmos, !sawNameTables);
    }

    static Vector3 Vec3(BinaryReader br) => new(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
}

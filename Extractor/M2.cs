using System.Numerics;
using System.Text;

namespace MapExtract;

/// M2s are chunked since Legion: the legacy MD20 blob lives inside an MD21 chunk,
/// and every offset in its header is relative to the start of that blob.
public static class M2
{
    // MD20 header, version >= 264 (no ofsViews field).
    const int OfsBoundingTriangles = 0xD8;
    const int OfsBoundingVertices  = 0xE0;

    public static Wmo.Mesh ReadCollision(Stream s)
    {
        var ms = new MemoryStream();
        s.CopyTo(ms);
        byte[] data = ms.ToArray();
        if (data.Length < 8) return Wmo.Mesh.Empty;

        int md20 = 0;
        if (Encoding.ASCII.GetString(data, 0, 4) == "MD21") md20 = 8;
        else if (Encoding.ASCII.GetString(data, 0, 4) != "MD20") return Wmo.Mesh.Empty;

        if (md20 + 0xE8 > data.Length) return Wmo.Mesh.Empty;

        int nTris = BitConverter.ToInt32(data, md20 + OfsBoundingTriangles);
        int oTris = BitConverter.ToInt32(data, md20 + OfsBoundingTriangles + 4);
        int nVerts = BitConverter.ToInt32(data, md20 + OfsBoundingVertices);
        int oVerts = BitConverter.ToInt32(data, md20 + OfsBoundingVertices + 4);

        // Most doodads carry no collision mesh at all; that is the filter we want.
        if (nTris <= 0 || nVerts <= 0) return Wmo.Mesh.Empty;
        if (!InRange(md20 + oTris, nTris * 2, data.Length) ||
            !InRange(md20 + oVerts, nVerts * 12, data.Length)) return Wmo.Mesh.Empty;

        var verts = new List<Vector3>(nVerts);
        for (int i = 0; i < nVerts; i++)
        {
            int o = md20 + oVerts + i * 12;
            verts.Add(new Vector3(BitConverter.ToSingle(data, o),
                                  BitConverter.ToSingle(data, o + 4),
                                  BitConverter.ToSingle(data, o + 8)));
        }

        var idx = new List<int>(nTris);
        for (int i = 0; i < nTris; i++)
        {
            int v = BitConverter.ToUInt16(data, md20 + oTris + i * 2);
            if (v >= nVerts) return Wmo.Mesh.Empty;   // header layout wrong for this build
            idx.Add(v);
        }

        return new Wmo.Mesh(verts, idx);
    }

    static bool InRange(int start, int len, int total) => start >= 0 && len >= 0 && start + len <= total;
}

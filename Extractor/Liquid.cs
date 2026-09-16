using System.Numerics;

namespace MapExtract;

/// LiquidType.db2 SoundBank: what the nav bake needs to distinguish swimmable
/// from lethal.
public enum LiquidClass : byte { Water = 0, Ocean = 1, Magma = 2, Slime = 3, Unknown = 4 }

/// Liquid surfaces, kept separate from walkable geometry so the nav bake can
/// mark them as swim or hazard rather than ground.
public static class Liquid
{
    /// MH2O: 256 SMLiquidChunk headers, then instances; every offset is relative
    /// to the start of the chunk body.
    public static void EmitMh2o(byte[] mh2o, List<Adt.Mcnk> chunks,
                                List<Vector3> verts, List<int> indices, List<LiquidClass> triClass,
                                Func<ushort, LiquidClass> classify, bool transpose, int step = 1)
    {
        if (mh2o.Length < 256 * 12) return;
        step = Math.Clamp(step, 1, 8);

        for (int i = 0; i < Math.Min(256, chunks.Count); i++)
        {
            uint ofsInstances = BitConverter.ToUInt32(mh2o, i * 12);
            uint layerCount = BitConverter.ToUInt32(mh2o, i * 12 + 4);
            if (layerCount == 0 || ofsInstances == 0) continue;

            for (int L = 0; L < layerCount; L++)
            {
                int o = (int)ofsInstances + L * 24;
                if (o + 24 > mh2o.Length) break;

                ushort liquidType = BitConverter.ToUInt16(mh2o, o);
                ushort lvf = BitConverter.ToUInt16(mh2o, o + 2);
                var cls = classify(liquidType);
                float minH = BitConverter.ToSingle(mh2o, o + 4);
                float maxH = BitConverter.ToSingle(mh2o, o + 8);
                int xo = mh2o[o + 12], yo = mh2o[o + 13], w = mh2o[o + 14], h = mh2o[o + 15];
                uint ofsExists = BitConverter.ToUInt32(mh2o, o + 16);
                uint ofsVerts = BitConverter.ToUInt32(mh2o, o + 20);
                if (w == 0 || h == 0) continue;

                // lvf >= 42 is a LiquidObject.db2 id; fall back to a flat surface
                bool hasHeights = lvf < 42 && lvf != 2 && ofsVerts != 0;
                int nVerts = (w + 1) * (h + 1);
                if (hasHeights && ofsVerts + nVerts * 4 > mh2o.Length) hasHeights = false;

                float HeightAt(int vx, int vy)
                {
                    if (!hasHeights) return minH;
                    int idx = vy * (w + 1) + vx;
                    float z = BitConverter.ToSingle(mh2o, (int)ofsVerts + idx * 4);
                    return z is > -10000f and < 10000f ? z : minH;
                }

                bool Exists(int cx, int cy)
                {
                    if (ofsExists == 0) return true;
                    int bit = cy * w + cx;
                    int by = (int)ofsExists + bit / 8;
                    return by < mh2o.Length && (mh2o[by] & (1 << (bit % 8))) != 0;
                }

                var k = chunks[i];
                for (int cy = 0; cy < h; cy += step)
                    for (int cx = 0; cx < w; cx += step)
                    {
                        int ex = Math.Min(cx + step, w), ey = Math.Min(cy + step, h);

                        // a coarse quad survives if any cell it covers has liquid
                        bool any = false;
                        for (int y2 = cy; y2 < ey && !any; y2++)
                            for (int x2 = cx; x2 < ex; x2++)
                                if (Exists(x2, y2)) { any = true; break; }
                        if (!any) continue;

                        int b = verts.Count;
                        verts.Add(CellVert(k, xo + cx, yo + cy, HeightAt(cx, cy), transpose));
                        verts.Add(CellVert(k, xo + ex, yo + cy, HeightAt(ex, cy), transpose));
                        verts.Add(CellVert(k, xo + cx, yo + ey, HeightAt(cx, ey), transpose));
                        verts.Add(CellVert(k, xo + ex, yo + ey, HeightAt(ex, ey), transpose));
                        // wound to face up, matching the terrain winding
                        indices.AddRange([b + 0, b + 3, b + 1, b + 0, b + 2, b + 3]);
                        triClass.Add(cls); triClass.Add(cls);
                    }
            }
        }
    }

    /// Grid vertex on a chunk, using the same axis convention as the terrain mesh.
    static Vector3 CellVert(Adt.Mcnk k, int gx, int gy, float z, bool transpose) =>
        transpose ? new Vector3(k.PosX - gx * Terrain.Unit, k.PosY - gy * Terrain.Unit, z)
                  : new Vector3(k.PosX - gy * Terrain.Unit, k.PosY - gx * Terrain.Unit, z);

    /// MLIQ inside a WMO group, in WMO model space (Z up).
    public static Wmo.Mesh ReadMliq(byte[] d)
    {
        if (d.Length < 30) return Wmo.Mesh.Empty;
        int xv = BitConverter.ToInt32(d, 0), yv = BitConverter.ToInt32(d, 4);
        int xt = BitConverter.ToInt32(d, 8), yt = BitConverter.ToInt32(d, 12);
        var base_ = new Vector3(BitConverter.ToSingle(d, 16), BitConverter.ToSingle(d, 20), BitConverter.ToSingle(d, 24));
        if (xv <= 0 || yv <= 0 || xt <= 0 || yt <= 0) return Wmo.Mesh.Empty;

        int vertBase = 30, flagBase = vertBase + xv * yv * 8;
        if (flagBase + xt * yt > d.Length) return Wmo.Mesh.Empty;

        var verts = new List<Vector3>(xv * yv);
        for (int y = 0; y < yv; y++)
            for (int x = 0; x < xv; x++)
                verts.Add(base_ + new Vector3(x * Terrain.Unit, y * Terrain.Unit,
                                              BitConverter.ToSingle(d, vertBase + (y * xv + x) * 8 + 4)));

        var idx = new List<int>();
        for (int y = 0; y < yt; y++)
            for (int x = 0; x < xt; x++)
            {
                if ((d[flagBase + y * xt + x] & 0x0F) == 0x0F) continue;   // 0x0F == no liquid here
                int a = y * xv + x, b = a + 1, c = a + xv, e = c + 1;
                idx.AddRange([a, b, e, a, e, c]);
            }

        return new Wmo.Mesh(verts, idx);
    }
}

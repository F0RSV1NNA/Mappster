using System.Numerics;

namespace MapExtract;

public static class Terrain
{
    public const float TileSize = 533.33333f;
    public const float ChunkSize = TileSize / 16f;
    public const float Unit = ChunkSize / 8f;
    public const float Origin = 32f * TileSize;

    /// MCVT is 9x9 outer verts interleaved with 8x8 inner verts, row by row.
    static int Outer(int r, int c) => r * 17 + c;
    static int Inner(int r, int c) => r * 17 + 9 + c;

    public static Vector3 OuterPos(Adt.Mcnk k, int r, int c, bool transpose) =>
        transpose ? new(k.PosX - c * Unit, k.PosY - r * Unit, k.PosZ + k.Heights[Outer(r, c)])
                  : new(k.PosX - r * Unit, k.PosY - c * Unit, k.PosZ + k.Heights[Outer(r, c)]);

    public static Vector3 InnerPos(Adt.Mcnk k, int r, int c, bool transpose) =>
        transpose ? new(k.PosX - (c + 0.5f) * Unit, k.PosY - (r + 0.5f) * Unit, k.PosZ + k.Heights[Inner(r, c)])
                  : new(k.PosX - (r + 0.5f) * Unit, k.PosY - (c + 0.5f) * Unit, k.PosZ + k.Heights[Inner(r, c)]);

    /// step 1 is full detail: 4 triangles per cell around the inner vertex. Larger steps
    /// skip outer vertices and emit 2 triangles per coarse cell, for whole-map overviews.
    public static void Emit(Adt.Mcnk k, List<Vector3> verts, List<int> indices, bool transpose, int step = 1)
    {
        if (step <= 1)
        {
            for (int r = 0; r < 8; r++)
                for (int c = 0; c < 8; c++)
                {
                    if ((k.Holes & (1UL << (r * 8 + c))) != 0) continue;

                    int b = verts.Count;
                    verts.Add(OuterPos(k, r, c, transpose));         // 0 top-left
                    verts.Add(OuterPos(k, r, c + 1, transpose));     // 1 top-right
                    verts.Add(OuterPos(k, r + 1, c + 1, transpose)); // 2 bottom-right
                    verts.Add(OuterPos(k, r + 1, c, transpose));     // 3 bottom-left
                    verts.Add(InnerPos(k, r, c, transpose));         // 4 center

                    // wound so face normals point up; Recast rejects down-facing triangles
                    foreach (var (a, bb) in new[] { (1, 0), (2, 1), (3, 2), (0, 3) })
                    { indices.Add(b + a); indices.Add(b + bb); indices.Add(b + 4); }
                }
            return;
        }

        step = Math.Min(step, 8);
        for (int r = 0; r + step <= 8; r += step)
            for (int c = 0; c + step <= 8; c += step)
            {
                // a coarse cell is dropped only if every cell it covers is a hole
                bool allHoles = true;
                for (int dr = 0; dr < step && allHoles; dr++)
                    for (int dc = 0; dc < step; dc++)
                        if ((k.Holes & (1UL << ((r + dr) * 8 + c + dc))) == 0) { allHoles = false; break; }
                if (allHoles) continue;

                int b = verts.Count;
                verts.Add(OuterPos(k, r, c, transpose));
                verts.Add(OuterPos(k, r, c + step, transpose));
                verts.Add(OuterPos(k, r + step, c + step, transpose));
                verts.Add(OuterPos(k, r + step, c, transpose));
                indices.AddRange([b, b + 2, b + 1, b, b + 3, b + 2]);
            }
    }

    /// Height on the edge a chunk shares with its -X neighbour. Which MCVT index runs
    /// along that edge is exactly what the two conventions disagree about.
    static float EdgeZ(Adt.Mcnk k, int i, bool farSide, bool transpose) =>
        k.PosZ + k.Heights[transpose ? Outer(i, farSide ? 8 : 0) : Outer(farSide ? 8 : 0, i)];

    /// Shared edges between neighbouring chunks must line up; used to pick the MCVT order.
    public static (int matched, int total) EdgeContinuity(List<Adt.Mcnk> chunks, bool transpose)
    {
        var byPos = chunks.ToDictionary(k => (MathF.Round(k.PosX, 1), MathF.Round(k.PosY, 1)));
        int matched = 0, total = 0;

        foreach (var k in chunks)
        {
            var key = (MathF.Round(k.PosX - ChunkSize, 1), MathF.Round(k.PosY, 1));
            if (!byPos.TryGetValue(key, out var nb)) continue;
            for (int i = 0; i <= 8; i++)
            {
                total++;
                if (MathF.Abs(EdgeZ(k, i, true, transpose) - EdgeZ(nb, i, false, transpose)) < 0.05f) matched++;
            }
        }
        return (matched, total);
    }
}

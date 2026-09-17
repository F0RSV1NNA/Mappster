using System.Numerics;
using CASCLib;
using DBCD;
using DBCD.Providers;

namespace MapExtract;

/// Turns a world position into the zone name and 0-100 map coordinates the game shows,
/// so a place found in the files can actually be walked to.
///
/// UiMapAssignment carries each UI map's world rectangle in Region — min x,y,z then
/// max x,y,z — and the slice of the map sheet it fills in UiMin/UiMax. World +X is north
/// and +Y is west, while the displayed pair runs east then south, so both axes invert
/// and swap on the way out.
public sealed class Zones
{
    public readonly record struct Spot(string Zone, float X, float Y);

    sealed record Assign(string Name, int Type, float[] R, float[] Lo, float[] Hi);

    readonly Dictionary<int, List<Assign>> _byMap = [];

    public Zones(CASCHandler casc, string build)
    {
        var dbd = new GithubDBDProvider();
        var uiMap = new DBCD.DBCD(new One(casc, Names.Db2("UiMap", 1957206)), dbd).Load("UiMap", build);
        var assign = new DBCD.DBCD(new One(casc, Names.Db2("UiMapAssignment", 1957219)), dbd)
                         .Load("UiMapAssignment", build);

        var sheet = uiMap.Values.ToDictionary(r => r.ID,
            r => (Name: r["Name_lang"]?.ToString() ?? "", Type: Convert.ToInt32(r["Type"])));

        foreach (DBCDRow r in assign.Values)
        {
            int mapId = Convert.ToInt32(r["MapID"]);
            if (mapId < 0) continue;

            var region = Floats(r["Region"]);
            if (region.Length < 6 || region[3] <= region[0] || region[4] <= region[1]) continue;
            if (!sheet.TryGetValue(Convert.ToInt32(r["UiMapID"]), out var s)) continue;

            if (!_byMap.TryGetValue(mapId, out var list)) _byMap[mapId] = list = [];
            list.Add(new Assign(s.Name, s.Type, region, Floats(r["UiMin"]), Floats(r["UiMax"])));
        }
    }

    static float[] Floats(object? v) => v switch
    {
        float[] f => f,
        System.Collections.IEnumerable e => e.Cast<object>().Select(Convert.ToSingle).ToArray(),
        _ => [],
    };

    /// The smallest zone-level map covering the point, or the smallest of any type if the
    /// point falls outside every zone sheet.
    public Spot? Locate(int mapId, Vector3 world)
    {
        if (!_byMap.TryGetValue(mapId, out var list)) return null;

        Assign? best = null;
        double bestArea = 0;
        int bestRank = int.MaxValue;

        foreach (var a in list)
        {
            if (world.X < a.R[0] || world.X > a.R[3] || world.Y < a.R[1] || world.Y > a.R[4]) continue;
            int rank = a.Type == 3 ? 0 : 1;              // 3 = Zone, the sheet a player has open
            double area = (a.R[3] - a.R[0]) * (double)(a.R[4] - a.R[1]);
            if (rank > bestRank || (rank == bestRank && area >= bestArea)) continue;
            best = a; bestRank = rank; bestArea = area;
        }
        if (best == null) return null;

        float east = (best.R[4] - world.Y) / (best.R[4] - best.R[1]);
        float south = (best.R[3] - world.X) / (best.R[3] - best.R[0]);
        float x = best.Lo.Length > 0 ? best.Lo[0] + east * (best.Hi[0] - best.Lo[0]) : east;
        float y = best.Lo.Length > 1 ? best.Lo[1] + south * (best.Hi[1] - best.Lo[1]) : south;
        return new Spot(best.Name, x * 100f, y * 100f);
    }

    sealed class One(CASCHandler casc, int fdid) : IDBCProvider
    {
        public Stream StreamForTableName(string tableName, string build)
        {
            var ms = new MemoryStream();
            using (var s = casc.OpenFile(fdid)) s.CopyTo(ms);
            ms.Position = 0; return ms;
        }
    }
}

namespace MapExtract.Gui;

/// Bakes a map's navmesh one ADT tile at a time, writing each tile's output before
/// moving on. Doing it per ADT is what makes a continent feasible: peak memory stays
/// at one neighbourhood, and an interrupted run leaves everything it already wrote.
public sealed class NavJob
{
    public volatile int Done, Total, TilesWritten, Failed;
    public volatile string Status = "";
    public volatile bool Running;
    public long Bytes;
    public string MapName = "";
    public string OutDir = "";

    readonly CancellationTokenSource _cts = new();
    public void Cancel() => _cts.Cancel();

    public static NavJob Start(Session session, MapInfo map, string outDir, NavBake.Settings settings)
    {
        var job = new NavJob { Running = true, MapName = map.Name, OutDir = outDir, Status = "reading tiles…" };

        Task.Run(() =>
        {
            try
            {
                var tiles = session.Tiles(map.WdtFdid);
                Directory.CreateDirectory(outDir);

                // tile-less maps are one WMO; bake it as a single unclipped region
                if (tiles.Count == 0)
                {
                    job.Total = 1;
                    var g = session.GlobalWmo(map.WdtFdid);
                    var soup = g == null ? null : session.BuildGlobal(g);
                    if (soup == null) { job.Status = "no geometry"; return; }

                    var gr = NavBake.Bake(soup, settings, job._cts.Token);
                    if (!gr.Ok) { job.Status = "bake failed: " + gr.Message; return; }
                    var (gf, gb) = NavWriter.WriteAll(outDir, map.Id, gr);
                    job.TilesWritten = gf; job.Bytes = gb; job.Done = 1;
                    job.Status = $"done — {gf} tiles, {gb / 1048576.0:F1} MB";
                    return;
                }

                var byPos = tiles.ToDictionary(t => (t.X, t.Y));
                job.Total = tiles.Count;

                bool paramsWritten = false;
                foreach (var t in tiles)
                {
                    if (job._cts.IsCancellationRequested) { job.Status = "cancelled"; break; }
                    job.Status = $"tile {t.X},{t.Y}";

                    // The target ADT in full, plus a thin band of its neighbours so
                    // Recast's border ring has real geometry and tiles link up.
                    var clip = NavBake.Clip.ForAdt(t.X, t.Y);
                    const float band = 8f;
                    var soup = new Extractor.Soup();
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (!byPos.TryGetValue((t.X + dx, t.Y + dy), out var n)) continue;
                            var s = session.BuildTile(n);
                            if (s == null) continue;
                            if (dx == 0 && dy == 0) soup.Append(s);
                            else soup.AppendClipped(s, clip.MinX - band, clip.MinY - band,
                                                       clip.MaxX + band, clip.MaxY + band);
                        }

                    if (soup.TriCount + soup.LiquidTris > 0)
                    {
                        var r = NavBake.Bake(soup, settings, job._cts.Token, clip);
                        if (r.Ok)
                        {
                            if (!paramsWritten)
                            {
                                NavWriter.WriteMapParams(outDir, map.Id, r.MeshParams);
                                paramsWritten = true;
                            }
                            foreach (var nt in r.Tiles)
                            {
                                job.Bytes += NavWriter.WriteTile(outDir, map.Id, nt);
                                job.TilesWritten++;
                            }
                        }
                        else if (r.Message != "no walkable polygons") job.Failed++;
                    }

                    job.Done++;
                }

                if (!job._cts.IsCancellationRequested)
                    job.Status = $"done — {job.TilesWritten} tiles, {job.Bytes / 1048576.0:F1} MB" +
                                 (job.Failed > 0 ? $", {job.Failed} ADTs failed" : "");
            }
            catch (Exception e) { job.Status = "failed: " + e.Message; }
            finally { job.Running = false; }
        });

        return job;
    }

}

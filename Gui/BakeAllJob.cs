using System.Collections.Concurrent;

namespace MapExtract.Gui;

/// Bakes many maps to .mmap/.mmtile, one ADT at a time.
///
/// Two properties make a multi-hour run survivable:
///   RESUMABLE  — an ADT whose .mmtile already exists is skipped, so an interrupted
///                run continues instead of restarting.
///   PARALLEL   — reading geometry is serialised (CASC is not thread safe) but the
///                bake, which dominates at ~3-13s per ADT, runs on all cores.
public sealed class BakeAllJob
{
    public volatile int MapsDone, MapsTotal, AdtsDone, AdtsTotal, TilesWritten, Skipped, Failed;
    public volatile string Status = "";
    public volatile string CurrentMap = "";
    public volatile bool Running;
    long _bytes;
    public long Bytes => Interlocked.Read(ref _bytes);

    readonly CancellationTokenSource _cts = new();
    public void Cancel() => _cts.Cancel();

    readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
    public TimeSpan Elapsed => _sw.Elapsed;

    /// 0..1 over ADT tiles, the unit of work.
    public float Fraction => AdtsTotal > 0 ? (float)AdtsDone / AdtsTotal : 0f;

    /// Estimated time remaining, from the rate achieved so far. Zero until enough
    /// tiles have finished for the rate to mean anything.
    public TimeSpan Eta
    {
        get
        {
            int done = AdtsDone - Skipped;         // skipped tiles cost nothing, so exclude them
            if (done < 8 || AdtsTotal <= 0) return TimeSpan.Zero;
            double perTile = _sw.Elapsed.TotalSeconds / done;
            return TimeSpan.FromSeconds(perTile * Math.Max(0, AdtsTotal - AdtsDone));
        }
    }

    /// mapId -> whether it has any geometry worth baking.
    public static List<MapInfo> Bakeable(Session session)
    {
        var list = new List<MapInfo>();
        foreach (var m in session.Maps)
        {
            if (session.Tiles(m.WdtFdid).Count > 0) { list.Add(m); continue; }
            if (session.GlobalWmo(m.WdtFdid) != null) { m.GlobalWmo = true; list.Add(m); }
        }
        return list;
    }

    /// Scans for bakeable maps inside the job, because that walk alone takes about a
    /// minute and should not freeze the UI thread.
    public static BakeAllJob StartAll(Session session, string outDir,
                                      NavBake.Settings settings, int threads, bool resume = true)
        => Start(session, null, outDir, settings, threads, resume);

    public static BakeAllJob Start(Session session, List<MapInfo>? maps, string outDir,
                                   NavBake.Settings settings, int threads, bool resume = true)
    {
        var job = new BakeAllJob { Running = true, Status = "finding maps with geometry…" };

        Task.Run(() =>
        {
            try
            {
                maps ??= Bakeable(session);
                job.MapsTotal = maps.Count;
                job.Status = "counting tiles…";

                Directory.CreateDirectory(outDir);
                var work = new List<(MapInfo Map, TileFiles? Tile)>();
                foreach (var m in maps)
                {
                    var tiles = session.Tiles(m.WdtFdid);
                    if (tiles.Count == 0) work.Add((m, null));
                    else foreach (var t in tiles) work.Add((m, t));
                }
                job.AdtsTotal = work.Count;
                job._sw.Restart();          // exclude the scan from the rate

                var byMap = work.GroupBy(x => x.Map.Id).ToDictionary(g => g.Key, g => g.ToList());
                var paramsWritten = new ConcurrentDictionary<int, bool>();

                foreach (var m in maps)
                {
                    if (job._cts.IsCancellationRequested) break;
                    job.CurrentMap = m.Name;
                    var items = byMap[m.Id];

                    var opts = new ParallelOptions
                    {
                        MaxDegreeOfParallelism = Math.Max(1, threads),
                        CancellationToken = job._cts.Token,
                    };

                    try
                    {
                        Parallel.ForEach(items, opts, item =>
                        {
                            var (map, tile) = item;
                            try { BakeOne(session, map, tile, outDir, settings, job, paramsWritten, resume); }
                            catch (Exception e)
                            {
                                job.Failed++;
                                job.Status = $"{map.Name}: {e.Message}";
                            }
                            job.AdtsDone++;
                        });
                    }
                    catch (OperationCanceledException) { break; }

                    job.MapsDone++;
                }

                job.Status = job._cts.IsCancellationRequested
                    ? $"cancelled — {job.TilesWritten} tiles written"
                    : $"done — {job.TilesWritten} tiles, {job.Bytes / 1048576.0:F0} MB, {job.Skipped} skipped";
            }
            catch (Exception e) { job.Status = "failed: " + e.Message; }
            finally { job.Running = false; }
        });

        return job;
    }

    static void BakeOne(Session session, MapInfo map, TileFiles? tile, string outDir,
                        NavBake.Settings settings, BakeAllJob job,
                        ConcurrentDictionary<int, bool> paramsWritten, bool resume)
    {
        NavBake.Result r;

        if (tile == null)
        {
            // whole map is one WMO
            var g = session.GlobalWmo(map.WdtFdid);
            var soup = g == null ? null : session.BuildGlobal(g);
            if (soup == null) return;
            r = NavBake.Bake(soup, settings, job._cts.Token);
        }
        else
        {
            if (resume && File.Exists(Path.Combine(outDir, NavWriter.TileFile(map.Id, tile.X, tile.Y))))
            { job.Skipped++; return; }

            var clip = NavBake.Clip.ForAdt(tile.X, tile.Y);
            const float band = 8f;
            var soup = new Extractor.Soup();
            var neighbours = session.Tiles(map.WdtFdid).ToDictionary(t => (t.X, t.Y));

            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (!neighbours.TryGetValue((tile.X + dx, tile.Y + dy), out var n)) continue;
                    var s = session.BuildTile(n);
                    if (s == null) continue;
                    if (dx == 0 && dy == 0) soup.Append(s);
                    else soup.AppendClipped(s, clip.MinX - band, clip.MinY - band,
                                               clip.MaxX + band, clip.MaxY + band);
                }

            if (soup.TriCount + soup.LiquidTris == 0) return;
            r = NavBake.Bake(soup, settings, job._cts.Token, clip);
        }

        if (!r.Ok)
        {
            if (r.FailedTiles > 0) job.Failed++;
            return;
        }

        if (paramsWritten.TryAdd(map.Id, true))
            NavWriter.WriteMapParams(outDir, map.Id, r.MeshParams);

        foreach (var nt in r.Tiles)
        {
            Interlocked.Add(ref job._bytes, NavWriter.WriteTile(outDir, map.Id, nt));
            job.TilesWritten++;
        }
    }
}

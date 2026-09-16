namespace MapExtract.Gui;

/// Bakes each transport model into its own navmesh, in the model's own space.
///
/// Transports move, so their geometry cannot live in a world tile. The runtime
/// resolves a transport's DisplayID through GameObjectDisplayInfo to a model
/// FileDataID, loads the navmesh baked here for that id, converts the player's
/// world position into the transport's local frame, paths, and converts back.
/// Placement is a live-client question; only the mesh is bakeable ahead of time.
///
/// Output mirrors the map layout so the same loader works, with the model's
/// FileDataID standing in for a map id:
///     transports\{fdid}.mmap  +  {fdid}_{row}_{col}.mmtile
public sealed class TransportJob
{
    public volatile int Done, Total, Written, Skipped, Failed;
    public volatile string Status = "", Current = "";
    public volatile bool Running;
    public long Bytes;

    readonly CancellationTokenSource _cts = new();
    public void Cancel() => _cts.Cancel();

    public readonly record struct Model(int Fdid, string Name);

    /// Transport models from transports.csv, the world/wmo/transports slice of the
    /// listfile with group files removed.
    public static List<Model> Load()
    {
        var list = new List<Model>();
        foreach (var dir in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory(), @"E:\Hacking\wowmaps" })
        {
            var path = Path.Combine(dir, "transports.csv");
            if (!File.Exists(path)) continue;
            foreach (var line in File.ReadLines(path))
            {
                int semi = line.IndexOf(';');
                if (semi <= 0 || !int.TryParse(line.AsSpan(0, semi), out int fdid)) continue;
                list.Add(new Model(fdid, line[(semi + 1)..].Trim()));
            }
            break;
        }
        return list;
    }

    public static TransportJob Start(Session session, List<Model> models, string outDir,
                                     NavBake.Settings settings, bool resume = true)
    {
        var job = new TransportJob { Running = true, Total = models.Count, Status = "baking transports…" };

        Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(outDir);
                foreach (var m in models)
                {
                    if (job._cts.IsCancellationRequested) { job.Status = "cancelled"; break; }
                    job.Current = m.Name;

                    try
                    {
                        if (resume && File.Exists(Path.Combine(outDir, NavWriter.MapFile(m.Fdid))))
                        { job.Skipped++; continue; }

                        var soup = session.BuildTransport(m.Fdid);
                        if (soup == null || soup.TriCount == 0) { job.Failed++; continue; }

                        // model space is centred near the origin, so no world clip
                        var r = NavBake.Bake(soup, settings, job._cts.Token);
                        if (!r.Ok) { job.Failed++; continue; }

                        var (files, bytes) = NavWriter.WriteAll(outDir, m.Fdid, r);
                        job.Written += files;
                        job.Bytes += bytes;
                    }
                    catch { job.Failed++; }
                    finally { job.Done++; }
                }

                if (!job._cts.IsCancellationRequested)
                    job.Status = $"done — {job.Written} tiles for {job.Done - job.Failed - job.Skipped} transports, " +
                                 $"{job.Bytes / 1048576.0:F1} MB" +
                                 (job.Skipped > 0 ? $", {job.Skipped} skipped" : "") +
                                 (job.Failed > 0 ? $", {job.Failed} failed" : "");
            }
            catch (Exception e) { job.Status = "failed: " + e.Message; }
            finally { job.Running = false; }
        });

        return job;
    }
}

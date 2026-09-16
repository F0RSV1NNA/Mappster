using MapExtract;
using MapExtract.Gui;

if (args.Length > 0 && args[0] == "--cli")
{
    Cli.Run(args.Skip(1).ToArray());
    return;
}

if (args.Length > 0 && args[0] == "--probe")
{
    Probe.Run();
    return;
}

if (args.Length > 0 && args[0] == "--makeindex")
{
    var dir = args.ElementAtOrDefault(1) ?? AppContext.BaseDirectory;
    var (d, t) = await Names.Rebuild(dir, Console.WriteLine);
    Console.WriteLine($"wrote db2index.csv ({d:N0} tables) and transports.csv ({t:N0} models) -> {dir}");
    return;
}

if (args.Length > 0 && args[0] == "--groups") { Analyze.Groups(); return; }
if (args.Length > 0 && args[0] == "--tilted") { Analyze.Tilted(); return; }
if (args.Length > 0 && args[0] == "--tiltedspots")
{
    Analyze.TiltedSpots(args.Length > 1 ? int.Parse(args[1]) : 12,
                        args.Length > 2 ? float.Parse(args[2]) : 0f,
                        args.Length > 3 ? float.Parse(args[3]) : 600f);
    return;
}
if (args.Length > 0 && args[0] == "--polys")  { Analyze.Polys();  return; }
if (args.Length > 0 && args[0] == "--api")    { ApiProbe.Run(args.Length > 1 ? args[1] : ""); return; }
if (args.Length > 0 && args[0] == "--wdt")    { Analyze.WdtChunks(); return; }
if (args.Length > 0 && args[0] == "--dungeons") { Analyze.Dungeons(); return; }
if (args.Length > 0 && args[0] == "--transports") { Analyze.Transports(); return; }
if (args.Length > 0 && args[0] == "--transportbake") { Analyze.TransportBake(args.Length > 1 ? int.Parse(args[1]) : 8); return; }
if (args.Length > 0 && args[0] == "--bakeall")
{
    Analyze.BakeAll(args.ElementAtOrDefault(1) ?? Path.Combine(AppContext.BaseDirectory, "mmaps"),
                    args.Length > 2 ? int.Parse(args[2]) : Environment.ProcessorCount / 2,
                    args.Length > 3 ? int.Parse(args[3]) : 0);
    return;
}

if (args.Length > 1 && args[0] == "--bakemap")
{
    Analyze.BakeMap(int.Parse(args[1]), args.Length > 2 ? int.Parse(args[2]) : 4);
    return;
}

if (args.Length > 3 && args[0] == "--baketile")
{
    Analyze.BakeTile(int.Parse(args[1]), int.Parse(args[2]), int.Parse(args[3]),
                     args.Length > 4 ? int.Parse(args[4]) : 0);
    return;
}

if (args.Length > 0 && args[0] == "--bake")
{
    Analyze.Bake(args.Length > 1 ? int.Parse(args[1]) : 0,
                 args.Length > 2 ? int.Parse(args[2]) : 5);
    return;
}

if (args.Length > 0 && args[0] == "--sessiontest")
{
    Analyze.SessionTest(args.Length > 1 ? int.Parse(args[1]) : 34);
    return;
}

if (args.Length > 0 && args[0] == "--zones")
{
    FindTable.Zones();
    return;
}

if (args.Length > 1 && args[0] == "--dump")
{
    foreach (var t in args[1].Split(',')) { FindTable.Dump(t); Console.WriteLine(); }
    return;
}

if (args.Length > 2 && args[0] == "--inspect")
{
    FindTable.Inspect(int.Parse(args[1]), args[2]);
    return;
}

if (args.Length > 1 && args[0] == "--findtable")
{
    FindTable.Run(args[1],
                  args.Length > 2 ? int.Parse(args[2]) : 1_330_000,
                  args.Length > 3 ? int.Parse(args[3]) : 1_400_000);
    return;
}

new App().Run();

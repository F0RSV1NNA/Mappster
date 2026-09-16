using System.Numerics;
using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ImGui;
using Silk.NET.Windowing;

namespace MapExtract.Gui;

public sealed class App
{
    const float Tile = 533.33333f;
    int _panelWidth = 380;

    IWindow _window = null!;
    GL _gl = null!;
    ImGuiController _imgui = null!;
    IInputContext _input = null!;

    readonly Session _session = new();
    readonly FlyCamera _camera = new();
    Renderer _renderer = null!;

    string _install = @"E:\Games\World of Warcraft";
    string _outDir = Path.Combine(AppContext.BaseDirectory, "out");
    string _filter = "";
    string _status = "Pick an install folder and press Open.";
    List<string> _products = [];
    int _productIdx;
    bool _yUp = true, _writeObj;

    MapInfo? _selectedMap;
    List<TileFiles> _tiles = [];
    Dictionary<(int, int), TileFiles> _tileAt = [];
    readonly HashSet<(int, int)> _loaded = [];
    readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), string> _zones = new();
    volatile bool _zoneScanDone;
    CancellationTokenSource? _zoneScan;
    string _tileFilter = "";
    string _meshLabel = "";

    // 0 = full, 1 = half, 2 = quarter, 3 = one quad per chunk
    static readonly int[] DetailStep = [1, 2, 4, 8];
    static readonly string[] DetailName = ["Full", "Half", "Quarter", "Chunk only"];
    int _detail;
    bool _loadObjects = true;
    int _radius;

    Vector2 _jump = new(0, 0);
    (int X, int Y)? _loadedCentre;

    ExportJob? _job;
    CancellationTokenSource? _scan;

    /// Above this the GL upload and the working set stop being sane on a desktop GPU.
    const int TriangleBudget = 14_000_000;

    sealed class LoadResult
    {
        public Renderer.Batch Batch = null!;
        public string Label = "";
        public List<(int, int)> Tiles = [];
        public bool Refocus;
    }
    volatile LoadResult? _pending;
    volatile bool _loading;
    volatile int _loadDone, _loadTotal;

    readonly NavBake.Settings _nav = new();
    volatile NavBake.Result? _navPending;
    volatile NavBake.Result? _lastBake;
    volatile bool _baking;
    string _navStatus = "";
    Extractor.Soup? _lastSoup;
    NavJob? _navJob;
    BakeAllJob? _bakeAll;
    TransportJob? _transportJob;
    List<TransportJob.Model>? _transportModels;
    int _bakeThreads = Math.Max(1, Environment.ProcessorCount / 2);
    bool _bakeResume = true;

    NavBake.Settings CopySettings() => new()
    {
        CellSize = _nav.CellSize, CellHeight = _nav.CellHeight,
        AgentHeight = _nav.AgentHeight, AgentRadius = _nav.AgentRadius,
        AgentMaxClimb = _nav.AgentMaxClimb, AgentMaxSlope = _nav.AgentMaxSlope,
    };

    Vector2 _lastMouse;
    bool _looking;

    public void Run()
    {
        // Silk.NET finds its backend by scanning assemblies in the app folder, which
        // finds nothing in a single-file publish. Register GLFW explicitly.
        Silk.NET.Windowing.Glfw.GlfwWindowing.RegisterPlatform();
        Silk.NET.Input.Glfw.GlfwInput.RegisterPlatform();

        var opts = WindowOptions.Default with
        {
            Size = new Vector2D<int>(1600, 980),
            Title = "wowmaps — extractor + tile viewer",
            API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.Default, new APIVersion(3, 3)),
            Samples = 4,
            VSync = true,
        };
        _window = Window.Create(opts);
        _window.Load += OnLoad;
        _window.Render += OnRender;
        _window.FramebufferResize += s => _gl.Viewport(s);
        _window.Closing += OnClose;
        _window.Run();
    }

    void OnLoad()
    {
        _gl = _window.CreateOpenGL();
        _input = _window.CreateInput();
        _imgui = new ImGuiController(_gl, _window, _input);
        _renderer = new Renderer(_gl);
        _renderer.Init();
        Style();

        foreach (var m in _input.Mice)
        {
            m.MouseDown += (_, _) => { if (!ImGui.GetIO().WantCaptureMouse) _looking = true; };
            m.MouseUp += (_, _) => _looking = false;
            m.MouseMove += (_, p) =>
            {
                var d = p - _lastMouse; _lastMouse = p;
                if (_looking) _camera.Look(d.X, d.Y);
            };
            m.Scroll += (_, s) => { if (!ImGui.GetIO().WantCaptureMouse) _camera.Dolly(s.Y); };
        }

        _products = Session.Products(_install);
        if (_products.Count == 0) _products = ["wow"];
    }

    /// WASD fly, Space/Ctrl for altitude, Shift to sprint. Ignored while typing in a field.
    void Fly(double dt)
    {
        if (_input.Keyboards.Count == 0 || ImGui.GetIO().WantCaptureKeyboard) return;
        var kb = _input.Keyboards[0];

        var move = Vector3.Zero;
        if (kb.IsKeyPressed(Key.W)) move.Y += 1;
        if (kb.IsKeyPressed(Key.S)) move.Y -= 1;
        if (kb.IsKeyPressed(Key.D)) move.X += 1;
        if (kb.IsKeyPressed(Key.A)) move.X -= 1;
        if (kb.IsKeyPressed(Key.Space)) move.Z += 1;
        if (kb.IsKeyPressed(Key.ControlLeft) || kb.IsKeyPressed(Key.C)) move.Z -= 1;

        if (move != Vector3.Zero)
            _camera.Move(Vector3.Normalize(move), (float)dt,
                         kb.IsKeyPressed(Key.ShiftLeft) || kb.IsKeyPressed(Key.ShiftRight));

        if (kb.IsKeyPressed(Key.F) && _renderer.HasMesh)
            _camera.Frame(_renderer.BoundsMin, _renderer.BoundsMax);
    }

    void OnClose()
    {
        _scan?.Cancel();
        _zoneScan?.Cancel();
        _job?.Cancel();
        _renderer.Dispose();
        _session.Dispose();
    }

    void OnRender(double dt)
    {
        _imgui.Update((float)dt);
        Fly(dt);

        if (_pending is { } r)
        {
            _pending = null;
            _renderer.Upload(r.Batch.Buckets, r.Batch.Lo, r.Batch.Hi, r.Batch.Tris, r.Batch.Verts);
            _loaded.Clear();
            foreach (var t in r.Tiles) _loaded.Add(t);
            _meshLabel = r.Label;
            if (r.Refocus) _camera.Frame(r.Batch.Lo, r.Batch.Hi);
        }

        if (_navPending is { } nr)
        {
            _navPending = null;
            if (nr.Ok) _renderer.UploadNav(nr.DebugVerts, nr.DebugIndices, nr.DebugAreas);
            else _renderer.ClearNav();
        }

        var fb = _window.FramebufferSize;
        _gl.Enable(EnableCap.DepthTest);
        _gl.ClearColor(0.055f, 0.063f, 0.075f, 1f);
        _gl.Viewport(0, 0, (uint)fb.X, (uint)fb.Y);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        _panelWidth = Math.Clamp(_panelWidth, 300, Math.Max(320, fb.X - 240));
        int vw = Math.Max(1, fb.X - _panelWidth);
        _gl.Viewport(_panelWidth, 0, (uint)vw, (uint)fb.Y);
        _renderer.Draw(_camera.Mvp(fb.Y == 0 ? 1 : (float)vw / fb.Y));
        _gl.Viewport(0, 0, (uint)fb.X, (uint)fb.Y);

        DrawPanel(fb.Y);
        DrawSplitter(fb.Y);
        DrawOverlay(fb.X);

        _imgui.Render();
    }

    // ---- panel ------------------------------------------------------------
    void DrawPanel(int height)
    {
        ImGui.SetNextWindowPos(Vector2.Zero);
        ImGui.SetNextWindowSize(new Vector2(_panelWidth, height));
        ImGui.Begin("panel", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
                             ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse |
                             ImGuiWindowFlags.NoBringToFrontOnFocus);

        Head("INSTALLATION");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##install", ref _install, 512))
        {
            var p = Session.Products(_install);
            if (p.Count > 0) { _products = p; _productIdx = 0; }
        }
        ImGui.SetNextItemWidth(150);
        ImGui.Combo("##product", ref _productIdx, string.Join('\0', _products) + '\0');
        ImGui.SameLine();
        if (ImGui.Button("Open", new Vector2(90, 0))) OpenStorage();
        ImGui.SameLine();
        if (ImGui.Button("Rescan", new Vector2(90, 0)))
        {
            var p = Session.Products(_install);
            _products = p.Count > 0 ? p : ["wow"];
            _productIdx = 0;
            _status = $"{_products.Count} product(s) in .build.info";
        }
        ImGui.TextWrapped(_status);
        if (_session.Ready)
            ImGui.TextColored(new Vector4(0.55f, 0.72f, 0.45f, 1), $"{_session.Product}  build {_session.Build}");

        Gap();
        Head("EXPORT");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##out", ref _outDir, 512);
        ImGui.Checkbox("Y-up", ref _yUp);
        ImGui.SameLine();
        ImGui.Checkbox("also write .obj", ref _writeObj);

        bool busy = _job is { Running: true };
        ImGui.BeginDisabled(_selectedMap == null || busy);
        if (ImGui.Button(_selectedMap == null ? "Export — select a map first" : $"Export \"{_selectedMap.Name}\"",
                         new Vector2(-1, 28)) && _selectedMap != null)
            _job = ExportJob.Start(_session, _selectedMap, _outDir, _yUp, _writeObj);
        ImGui.EndDisabled();

        if (_job != null)
        {
            float frac = _job.Total > 0 ? (float)_job.Done / _job.Total : 0f;
            ImGui.ProgressBar(frac, new Vector2(-1, 18), $"{_job.Done} / {_job.Total}");
            ImGui.TextWrapped($"{_job.MapName} — {_job.Status}");
            if (busy && ImGui.Button("Cancel", new Vector2(-1, 0))) _job.Cancel();
        }

        Gap();
        Head("MAPS");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##filter", "filter by name or id", ref _filter, 128);
        DrawMapTable();
        DrawMapList();

        Gap();
        DrawViewSection();

        Gap();
        DrawNavSection();

        Gap();
        DrawDetails();

        ImGui.End();

        static void Head(string s) => ImGui.TextColored(new Vector4(0.88f, 0.55f, 0.32f, 1), s);
        static void Gap() { ImGui.Dummy(new Vector2(0, 6)); ImGui.Separator(); }
    }

    void DrawMapTable()
    {
        var rows = _session.Maps.Where(Match).ToList();
        ImGui.BeginChild("maps", new Vector2(0, 190), ImGuiChildFlags.Border);
        if (ImGui.BeginTable("maptable", 3,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupColumn("id", ImGuiTableColumnFlags.WidthFixed, 44);
            ImGui.TableSetupColumn("name", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("tiles", ImGuiTableColumnFlags.WidthFixed, 46);
            ImGui.TableHeadersRow();
            foreach (var m in rows)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                if (ImGui.Selectable($"{m.Id}##m{m.Id}", _selectedMap?.Id == m.Id, ImGuiSelectableFlags.SpanAllColumns))
                    SelectMap(m);
                ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted(m.Name);
                ImGui.TableSetColumnIndex(2);
                ImGui.TextUnformatted(m.TileCount < 0 ? "…" : m.GlobalWmo ? "wmo" : m.TileCount.ToString());
            }
            ImGui.EndTable();
        }
        ImGui.EndChild();
        ImGui.TextDisabled($"{rows.Count} of {_session.Maps.Count} maps");
    }

    /// Recast parameters and the bake trigger for whatever is currently loaded.
    void DrawNavSection()
    {
        if (!ImGui.CollapsingHeader("NAVMESH", ImGuiTreeNodeFlags.DefaultOpen)) return;

        ImGui.SetNextItemWidth(130);
        ImGui.SliderFloat("agent radius", ref _nav.AgentRadius, 0.1f, 3f, "%.2f yd");
        ImGui.SetNextItemWidth(130);
        ImGui.SliderFloat("agent height", ref _nav.AgentHeight, 0.5f, 5f, "%.2f yd");
        ImGui.SetNextItemWidth(130);
        ImGui.SliderFloat("max climb", ref _nav.AgentMaxClimb, 0.1f, 3f, "%.2f yd");
        ImGui.SetNextItemWidth(130);
        ImGui.SliderFloat("max slope", ref _nav.AgentMaxSlope, 10f, 80f, "%.0f deg");
        ImGui.SetNextItemWidth(130);
        ImGui.SliderFloat("cell size", ref _nav.CellSize, 0.1f, 1.5f, "%.2f yd");
        ImGui.TextDisabled($"tile = 1 ADT ({NavBake.AdtSize:F0} yd, {_nav.TileSizeCells} cells)");

        ImGui.BeginDisabled(_baking || _lastSoup == null);
        if (ImGui.Button(_baking ? "baking…" : "Bake navmesh", new Vector2(-1, 26))) StartBake();
        ImGui.EndDisabled();

        ImGui.BeginDisabled(_baking || _lastBake is not { Ok: true } || _selectedMap == null);
        if (ImGui.Button("Write .mmap / .mmtile", new Vector2(-1, 24)) &&
            _lastBake is { Ok: true } bake && _selectedMap != null)
        {
            try
            {
                string dir = Path.Combine(_outDir, "mmaps");
                var (files, bytes) = NavWriter.WriteAll(dir, _selectedMap.Id, bake);
                var (lt, _, err) = NavWriter.LoadBack(dir, _selectedMap.Id);
                _navStatus = err.Length == 0
                    ? $"wrote {files} .mmtile + 1 .mmap ({bytes / 1024.0:F0} KB); {lt} reload cleanly"
                    : $"wrote {files} files but reload failed: {err}";
            }
            catch (Exception e) { _navStatus = "write failed: " + e.Message; }
        }
        ImGui.EndDisabled();

        ImGui.Dummy(new Vector2(0, 4));
        bool navBusy = _navJob is { Running: true };
        ImGui.BeginDisabled(navBusy || _selectedMap == null);
        if (ImGui.Button(_selectedMap == null ? "Bake whole map — select one first"
                                              : $"Bake whole map \"{_selectedMap.Name}\"", new Vector2(-1, 26))
            && _selectedMap != null)
            _navJob = NavJob.Start(_session, _selectedMap, Path.Combine(_outDir, "mmaps"), CopySettings());
        ImGui.EndDisabled();

        if (_navJob != null)
        {
            float frac = _navJob.Total > 0 ? (float)_navJob.Done / _navJob.Total : 0;
            ImGui.ProgressBar(frac, new Vector2(-1, 18), $"{_navJob.Done} / {_navJob.Total} ADTs");
            ImGui.PushTextWrapPos(0);
            ImGui.TextUnformatted($"{_navJob.TilesWritten} tiles · {_navJob.Bytes / 1048576.0:F1} MB · {_navJob.Status}");
            ImGui.PopTextWrapPos();
            if (navBusy && ImGui.Button("Cancel bake", new Vector2(-1, 0))) _navJob.Cancel();
        }

        DrawBakeAll();
        DrawTransports();

        if (_navStatus.Length > 0)
        {
            ImGui.PushTextWrapPos(0);
            ImGui.TextUnformatted(_navStatus);
            ImGui.PopTextWrapPos();
        }
        if (_renderer.HasNav)
        {
            bool show = _renderer.ShowNav;
            if (ImGui.Checkbox("show navmesh", ref show)) _renderer.ShowNav = show;
            ImGui.SameLine();
            if (ImGui.SmallButton("clear")) { _renderer.ClearNav(); _navStatus = ""; }
        }
    }

    /// Every map with geometry, in one run. Hours of work, so it reports tiles done,
    /// throughput and a time estimate, and can be stopped and resumed.
    void DrawBakeAll()
    {
        ImGui.Dummy(new Vector2(0, 6));
        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.88f, 0.55f, 0.32f, 1), "BAKE EVERYTHING");

        bool busy = _bakeAll is { Running: true };

        ImGui.SetNextItemWidth(130);
        ImGui.SliderInt("threads", ref _bakeThreads, 1, Environment.ProcessorCount);
        ImGui.SameLine();
        ImGui.TextDisabled($"of {Environment.ProcessorCount}");
        ImGui.Checkbox("skip tiles already written", ref _bakeResume);

        ImGui.BeginDisabled(busy || !_session.Ready);
        if (ImGui.Button("Bake ALL maps", new Vector2(-1, 30)))
            _bakeAll = BakeAllJob.StartAll(_session, Path.Combine(_outDir, "mmaps"),
                                           CopySettings(), _bakeThreads, _bakeResume);
        ImGui.EndDisabled();

        if (_bakeAll is not { } job) return;

        ImGui.ProgressBar(job.Fraction, new Vector2(-1, 22),
                          $"{job.AdtsDone:N0} / {job.AdtsTotal:N0} tiles");

        var eta = job.Eta;
        string etaText = eta > TimeSpan.Zero
            ? (eta.TotalHours >= 1 ? $"{(int)eta.TotalHours}h {eta.Minutes}m left" : $"{eta.Minutes}m {eta.Seconds}s left")
            : "estimating…";
        var el = job.Elapsed;
        string elText = el.TotalHours >= 1 ? $"{(int)el.TotalHours}h {el.Minutes}m" : $"{el.Minutes}m {el.Seconds}s";

        ImGui.TextUnformatted($"{job.MapsDone} / {job.MapsTotal} maps   ·   {elText} elapsed   ·   {etaText}");
        ImGui.TextDisabled($"{job.TilesWritten:N0} written · {job.Bytes / 1048576.0:F0} MB · " +
                           $"{job.Skipped:N0} skipped · {job.Failed} failed");
        ImGui.PushTextWrapPos(0);
        ImGui.TextDisabled(job.CurrentMap.Length > 0 ? job.CurrentMap : job.Status);
        ImGui.PopTextWrapPos();

        if (busy && ImGui.Button("Stop", new Vector2(-1, 0))) job.Cancel();
        else if (!busy) ImGui.TextUnformatted(job.Status);
    }

    /// Boats, zeppelins, elevators and trams, each baked in its own model space.
    void DrawTransports()
    {
        ImGui.Dummy(new Vector2(0, 6));
        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.88f, 0.55f, 0.32f, 1), "TRANSPORTS");

        _transportModels ??= TransportJob.Load();
        bool busy = _transportJob is { Running: true };

        ImGui.TextDisabled($"{_transportModels.Count} models from transports.csv");
        ImGui.BeginDisabled(busy || !_session.Ready || _transportModels.Count == 0);
        if (ImGui.Button("Bake transports", new Vector2(-1, 26)))
            _transportJob = TransportJob.Start(_session, _transportModels,
                                               Path.Combine(_outDir, "mmaps", "transports"),
                                               CopySettings());
        ImGui.EndDisabled();

        if (_transportJob is not { } tj) return;
        float f = tj.Total > 0 ? (float)tj.Done / tj.Total : 0;
        ImGui.ProgressBar(f, new Vector2(-1, 18), $"{tj.Done} / {tj.Total}");
        ImGui.PushTextWrapPos(0);
        ImGui.TextDisabled(tj.Running ? tj.Current : tj.Status);
        ImGui.PopTextWrapPos();
        if (busy && ImGui.Button("Stop transports", new Vector2(-1, 0))) tj.Cancel();
    }

    void StartBake()
    {
        var soup = _lastSoup;
        if (soup == null || _baking) return;
        _baking = true;
        _navStatus = "baking…";

        // copy the settings so the sliders can move while this runs
        var s = new NavBake.Settings
        {
            CellSize = _nav.CellSize, CellHeight = _nav.CellHeight,
            AgentHeight = _nav.AgentHeight, AgentRadius = _nav.AgentRadius,
            AgentMaxClimb = _nav.AgentMaxClimb, AgentMaxSlope = _nav.AgentMaxSlope,
            };

        Task.Run(() =>
        {
            try
            {
                var r = NavBake.Bake(soup, s);
                _navPending = r;
                _lastBake = r.Ok ? r : null;
                _navStatus = r.Ok
                    ? $"{r.TileCount} detour tiles · {r.PolyCount:N0} polys from {r.InputTris:N0} tris · " +
                      $"{r.TotalMs / 1000.0:F1}s · {r.NavDataBytes / 1024.0:F0} KB"
                    : "failed: " + r.Message;
            }
            catch (Exception e) { _navStatus = "failed: " + e.Message; }
            finally { _baking = false; }
        });
    }

    /// Drag handle on the panel's right edge.
    void DrawSplitter(int height)
    {
        ImGui.SetNextWindowPos(new Vector2(_panelWidth - 3, 0));
        ImGui.SetNextWindowSize(new Vector2(7, height));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.Begin("splitter", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
                                ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar |
                                ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoBringToFrontOnFocus);

        ImGui.InvisibleButton("##drag", new Vector2(7, height));
        bool hot = ImGui.IsItemHovered() || ImGui.IsItemActive();
        if (hot) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);
        if (ImGui.IsItemActive()) _panelWidth += (int)ImGui.GetIO().MouseDelta.X;

        var p = ImGui.GetWindowPos();
        ImGui.GetWindowDrawList().AddRectFilled(p + new Vector2(2, 0), p + new Vector2(4, height),
                                                hot ? 0xFF2F92E0u : 0xFF3A4048u);
        ImGui.End();
        ImGui.PopStyleVar();
    }

    /// Alphabetical companion to the id-ordered table above, for finding a map by name.
    void DrawMapList()
    {
        if (!ImGui.CollapsingHeader($"LIST — by name ({_session.Maps.Count})")) return;

        ImGui.BeginChild("maplist", new Vector2(0, 220), ImGuiChildFlags.Border);
        foreach (var m in _session.Maps.Where(Match).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (ImGui.Selectable($"{m.Name}##l{m.Id}", _selectedMap?.Id == m.Id)) SelectMap(m);
            ImGui.SameLine(250);
            ImGui.TextDisabled(m.TileCount < 0 ? "…" : $"{m.TileCount}");
        }
        ImGui.EndChild();
    }

    void DrawDetails()
    {
        if (_selectedMap is not { } m) { ImGui.TextDisabled("No map selected."); return; }
        if (!ImGui.CollapsingHeader($"DETAILS — {m.Name}", ImGuiTreeNodeFlags.DefaultOpen)) return;

        Row("id", m.Id.ToString());
        Row("directory", m.Directory);
        Row("wdt fdid", m.WdtFdid.ToString());
        Row("expansion", m.Expansion.ToString());
        Row("instance type", m.InstanceType.ToString());
        Row("tiles", _tiles.Count.ToString());
        Row("loaded", _loaded.Count.ToString());

        if (_tiles.Count == 0)
        {
            ImGui.TextDisabled(m.GlobalWmo
                ? "This map has no tiles — its geometry is a single WMO, already loaded."
                : "This map has no geometry in the client files.");
            return;
        }

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##tilefilter", "filter tiles by zone or x,y", ref _tileFilter, 64);

        ImGui.BeginChild("tilelist", new Vector2(0, 220), ImGuiChildFlags.Border);
        if (ImGui.BeginTable("tiles", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupColumn("tile", ImGuiTableColumnFlags.WidthFixed, 56);
            ImGui.TableSetupColumn("zone", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("world X,Y", ImGuiTableColumnFlags.WidthFixed, 108);
            ImGui.TableSetupColumn("obj", ImGuiTableColumnFlags.WidthFixed, 28);
            ImGui.TableHeadersRow();

            foreach (var t in _tiles)
            {
                string zone = _zones.TryGetValue((t.X, t.Y), out var z) ? z : "";
                if (_tileFilter.Length > 0 &&
                    !zone.Contains(_tileFilter, StringComparison.OrdinalIgnoreCase) &&
                    !$"{t.X},{t.Y}".StartsWith(_tileFilter)) continue;

                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                if (ImGui.Selectable($"{t.X,2},{t.Y,2}##t{t.X}_{t.Y}", _loaded.Contains((t.X, t.Y)),
                                     ImGuiSelectableFlags.SpanAllColumns) && !_loading)
                    LoadAround(t.X, t.Y);

                ImGui.TableSetColumnIndex(1);
                if (zone.Length > 0) ImGui.TextUnformatted(zone);
                else ImGui.TextDisabled(_zoneScanDone ? "—" : "…");

                ImGui.TableSetColumnIndex(2);
                ImGui.TextDisabled($"{(32 - t.X) * Tile,7:F0},{(32 - t.Y) * Tile,7:F0}");

                ImGui.TableSetColumnIndex(3);
                if (t.Obj0 != 0) ImGui.TextDisabled("•");
            }
            ImGui.EndTable();
        }
        ImGui.EndChild();

        static void Row(string k, string v)
        {
            ImGui.TextDisabled(k); ImGui.SameLine(130); ImGui.TextUnformatted(v);
        }
    }

    bool Match(MapInfo m) =>
        _filter.Length == 0 ||
        m.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase) ||
        m.Directory.Contains(_filter, StringComparison.OrdinalIgnoreCase) ||
        m.Id.ToString() == _filter;

    void DrawViewSection()
    {
        if (_selectedMap is not { } map) { ImGui.TextDisabled("No map selected."); return; }

        ImGui.TextColored(new Vector4(0.88f, 0.55f, 0.32f, 1), "VIEW");

        // a tile-less map is one WMO from the WDT: no tile grid, no LOD, nothing to pick
        if (_tiles.Count == 0)
        {
            if (map.GlobalWmo)
            {
                ImGui.TextDisabled($"{map.Directory} · wdt {map.WdtFdid}");
                ImGui.TextColored(new Vector4(0.55f, 0.72f, 0.45f, 1), "single global WMO — no tiles");
                if (_loading) ImGui.TextDisabled("building…");
                else if (ImGui.Button("Reload", new Vector2(-1, 26))) SelectMap(map);
            }
            else
            {
                ImGui.TextDisabled($"{map.Directory} · wdt {map.WdtFdid}");
                ImGui.TextColored(new Vector4(0.85f, 0.45f, 0.30f, 1), "no tiles and no global WMO — nothing to show");
            }
            return;
        }

        ImGui.TextDisabled($"{map.Directory} · wdt {map.WdtFdid} · {_tiles.Count} tiles");

        ImGui.SetNextItemWidth(120);
        ImGui.Combo("detail", ref _detail, string.Join('\0', DetailName) + '\0');
        ImGui.SameLine();
        ImGui.Checkbox("objects", ref _loadObjects);

        ImGui.SetNextItemWidth(120);
        ImGui.SliderInt("neighbours", ref _radius, 0, 8, _radius == 0 ? "single tile" : $"{_radius * 2 + 1}x{_radius * 2 + 1}");

        long est = EstimateTris(_tiles.Count, DetailStep[_detail], _loadObjects);
        bool tooBig = est > TriangleBudget;

        ImGui.BeginDisabled(_loading || tooBig || _tiles.Count == 0);
        if (ImGui.Button($"Load whole map  (~{est / 1_000_000.0:F1}M tris)", new Vector2(-1, 26)))
            LoadTiles(_tiles, $"{map.Name} — all {_tiles.Count} tiles", true);
        ImGui.EndDisabled();

        if (tooBig)
        {
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(new Vector4(0.85f, 0.45f, 0.30f, 1),
                $"Too large for one load (budget {TriangleBudget / 1_000_000}M). " +
                "Drop detail to Chunk only and untick objects for a continent overview.");
            ImGui.PopTextWrapPos();
        }

        if (_loading)
        {
            float f = _loadTotal > 0 ? (float)_loadDone / _loadTotal : 0;
            ImGui.ProgressBar(f, new Vector2(-1, 18), $"building {_loadDone} / {_loadTotal}");
        }

        // world position -> tile, so you can line the viewer up with where you're standing
        ImGui.SetNextItemWidth(160);
        ImGui.InputFloat2("##jump", ref _jump, "%.0f");
        ImGui.SameLine();
        if (ImGui.Button("Go to world X,Y", new Vector2(-1, 0))) JumpToWorld(_jump.X, _jump.Y);

        // Unresolved: see Extractor.RotationXFirst. Flipping reloads so the change shows.
        bool xFirst = Extractor.RotationXFirst;
        if (ImGui.Checkbox("rotation Rx*Ry*Rz", ref xFirst))
        {
            Extractor.RotationXFirst = xFirst;
            if (_loadedCentre is { } lc) LoadAround(lc.X, lc.Y);
        }
        ImGui.SameLine();
        ImGui.TextDisabled(xFirst ? "(alternative)" : "(default Rz*Ry*Rx)");

        DrawTileGrid();
    }

    /// 64x64 map of populated tiles. Click loads, and the neighbours slider widens it.
    void DrawTileGrid()
    {
        ImGui.TextDisabled("click a tile · loaded tiles are lit");
        var dl = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        const float cell = 5.2f;
        float side = cell * 64;

        ImGui.InvisibleButton("grid", new Vector2(side, side));
        bool hovered = ImGui.IsItemHovered();
        var mouse = ImGui.GetIO().MousePos;

        dl.AddRectFilled(origin, origin + new Vector2(side, side), 0xFF14171A);

        // north up, east right: tile Y runs across, tile X runs down
        uint colPop = 0xFF4A5560, colLoaded = 0xFF2F92E0, colHover = 0xFFFFFFFF;
        foreach (var t in _tiles)
        {
            var p = origin + new Vector2(t.Y * cell, t.X * cell);
            bool isLoaded = _loaded.Contains((t.X, t.Y));
            dl.AddRectFilled(p, p + new Vector2(cell - 1, cell - 1), isLoaded ? colLoaded : colPop);
        }

        if (hovered)
        {
            int ty = (int)((mouse.X - origin.X) / cell), tx = (int)((mouse.Y - origin.Y) / cell);
            if (tx is >= 0 and < 64 && ty is >= 0 and < 64)
            {
                var p = origin + new Vector2(ty * cell, tx * cell);
                dl.AddRect(p, p + new Vector2(cell - 1, cell - 1), colHover);
                if (_tileAt.ContainsKey((tx, ty)))
                {
                    ImGui.SetTooltip($"tile {tx}, {ty}\nworld X {(32 - tx) * Tile:F0}  Y {(32 - ty) * Tile:F0}");
                    if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !_loading) LoadAround(tx, ty);
                }
                else ImGui.SetTooltip($"tile {tx}, {ty}\n(no terrain)");
            }
        }
    }

    void DrawOverlay(int width)
    {
        if (!_renderer.HasMesh) return;
        ImGui.SetNextWindowPos(new Vector2(width - 296, 12));
        ImGui.SetNextWindowSize(new Vector2(284, 0));
        ImGui.Begin("layers", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
                              ImGuiWindowFlags.NoMove | ImGuiWindowFlags.AlwaysAutoResize);

        ImGui.TextUnformatted(_meshLabel);
        ImGui.TextDisabled($"{_renderer.TotalTris:N0} tris  {_renderer.TotalVerts:N0} verts  {_loaded.Count} tiles");
        ImGui.Separator();

        for (int c = 0; c < Renderer.ClassName.Length; c++)
        {
            if (_renderer.TriCount[c] == 0) continue;
            var col = Renderer.ClassColor[c];
            ImGui.ColorButton($"##c{c}", new Vector4(col.X, col.Y, col.Z, 1),
                              ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoPicker, new Vector2(12, 12));
            ImGui.SameLine();
            bool v = _renderer.Visible[c];
            if (ImGui.Checkbox($"{Renderer.ClassName[c]}##v{c}", ref v)) _renderer.Visible[c] = v;
            ImGui.SameLine(150);
            ImGui.TextDisabled($"{_renderer.TriCount[c]:N0}");
        }

        ImGui.Separator();
        var p = _camera.Position;
        ImGui.TextUnformatted($"{p.X:F0}, {p.Y:F0}, {p.Z:F0}");
        ImGui.TextDisabled($"tile {(int)MathF.Floor(32 - p.X / Tile)}, {(int)MathF.Floor(32 - p.Y / Tile)}" +
                           $"   facing {(_camera.Yaw * 180f / MathF.PI + 360f) % 360f:F0}°");

        float speed = _camera.Speed;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.SliderFloat("##speed", ref speed, 5f, 4000f, "%.0f yd/s", ImGuiSliderFlags.Logarithmic))
            _camera.Speed = speed;

        if (ImGui.Button("Frame  (F)", new Vector2(-1, 0))) _camera.Frame(_renderer.BoundsMin, _renderer.BoundsMax);
        ImGui.PushTextWrapPos(0);
        ImGui.TextDisabled("WASD fly, drag to look, Space/Ctrl for altitude, Shift to sprint, wheel to dolly.");
        ImGui.PopTextWrapPos();
        ImGui.End();
    }

    // ---- actions ----------------------------------------------------------
    void OpenStorage()
    {
        try
        {
            _status = "opening storage…";
            _session.Open(_install, _products[_productIdx]);
            _selectedMap = null; _tiles = []; _tileAt = []; _loaded.Clear();
            _renderer.Clear();
            _status = $"{_session.Maps.Count} maps";

            _scan?.Cancel();
            _scan = new CancellationTokenSource();
            var ct = _scan.Token;
            Task.Run(() => _session.ScanTileCounts(ct), ct);
        }
        catch (Exception e) { _status = "failed: " + e.Message; }
    }

    void SelectMap(MapInfo m)
    {
        _selectedMap = m;
        _tiles = _session.Tiles(m.WdtFdid);
        _tileAt = _tiles.ToDictionary(t => (t.X, t.Y));
        m.TileCount = _tiles.Count;
        _tileFilter = "";

        // tile-less maps (most classic dungeons) are a single WMO in the WDT
        if (_tiles.Count == 0)
        {
            var g = _session.GlobalWmo(m.WdtFdid);
            m.GlobalWmo = g != null;
            _zones.Clear(); _zoneScanDone = true;
            if (g == null) { _renderer.Clear(); _status = $"{m.Name} has no tiles and no global WMO"; return; }

            _loading = true; _loadDone = 0; _loadTotal = 1;
            Task.Run(() =>
            {
                try
                {
                    var soup = _session.BuildGlobal(g);
                    if (soup == null) { _status = "could not build global WMO"; return; }
                    var batch = new Renderer.Batch();
                    batch.Add(soup);
                    _lastSoup = soup;
                    _loadDone = 1;
                    _pending = new LoadResult { Batch = batch, Label = $"{m.Name} — global WMO", Tiles = [], Refocus = true };
                    _status = $"global WMO, {soup.TriCount:N0} tris";
                }
                finally { _loading = false; }
            });
            return;
        }

        // zone names come from each tile's MCNK area ids; fill them in the background
        _zoneScan?.Cancel();
        _zones.Clear();
        _zoneScanDone = false;
        _zoneScan = new CancellationTokenSource();
        var ct = _zoneScan.Token;
        var snapshot = _tiles.ToList();
        Task.Run(() =>
        {
            foreach (var t in snapshot)
            {
                if (ct.IsCancellationRequested) return;
                _zones[(t.X, t.Y)] = _session.TileZone(t);
            }
            _zoneScanDone = true;
        }, ct);

        if (_tiles.Count == 0) { _renderer.Clear(); return; }

        // prefer a tile that actually has objects on it; open ocean is a dull first look
        int mid = _tiles.Count / 2, best = mid;
        for (int d = 0; d < _tiles.Count; d++)
        {
            int lo = mid - d, hi = mid + d;
            if (lo >= 0 && _tiles[lo].Obj0 != 0) { best = lo; break; }
            if (hi < _tiles.Count && _tiles[hi].Obj0 != 0) { best = hi; break; }
        }
        LoadAround(_tiles[best].X, _tiles[best].Y);
    }

    void LoadAround(int cx, int cy)
    {
        _loadedCentre = (cx, cy);
        var want = new List<TileFiles>();
        for (int dy = -_radius; dy <= _radius; dy++)
            for (int dx = -_radius; dx <= _radius; dx++)
                if (_tileAt.TryGetValue((cx + dx, cy + dy), out var t)) want.Add(t);
        if (want.Count == 0) return;

        string label = _radius == 0
            ? $"{_selectedMap?.Name}  [{cx},{cy}]"
            : $"{_selectedMap?.Name}  [{cx},{cy}] +{_radius}  ({want.Count} tiles)";
        LoadTiles(want, label, true);
    }

    /// Rough per-tile cost, used to gate the whole-map button before it wedges the app.
    static long EstimateTris(int tileCount, int step, bool objects)
    {
        int cellsPerChunk = step <= 1 ? 64 : (8 / step) * (8 / step);
        long terrain = 256L * cellsPerChunk * (step <= 1 ? 4 : 2);
        long liquid = 256L * cellsPerChunk * 2 / 4;      // most tiles are only part water
        long objs = objects ? 90_000 : 0;                // measured average across EK
        return tileCount * (terrain + liquid + objs);
    }

    void LoadTiles(List<TileFiles> tiles, string label, bool refocus)
    {
        if (_loading || tiles.Count == 0) return;
        _loading = true; _loadDone = 0; _loadTotal = tiles.Count;
        int step = DetailStep[_detail];
        bool objects = _loadObjects;

        Task.Run(() =>
        {
            try
            {
                var batch = new Renderer.Batch();
                var kept = new List<(int, int)>();
                bool truncated = false;

                // Keep the geometry for baking, but only while it is a sane size —
                // a whole continent held twice over is how we OOMed before.
                var merged = tiles.Count <= 64 ? new Extractor.Soup() : null;

                foreach (var t in tiles)
                {
                    var s = _session.BuildTile(t, step, objects);
                    if (s != null)
                    {
                        batch.Add(s);                    // fold in and drop; never hold them all
                        merged?.Append(s);
                        kept.Add((t.X, t.Y));
                    }
                    _loadDone++;

                    if (batch.Tris > TriangleBudget)
                    {
                        truncated = true;
                        _status = $"stopped at {kept.Count} of {tiles.Count} tiles — " +
                                  $"{batch.Tris:N0} tris hit the budget. Lower detail or turn off objects.";
                        break;
                    }
                }

                _lastSoup = merged;
                _renderer.ClearNav();
                _navStatus = "";
                _pending = new LoadResult
                {
                    Batch = batch,
                    Label = truncated ? label + "  (truncated)" : label,
                    Tiles = kept,
                    Refocus = refocus,
                };
                if (!truncated) _status = $"{kept.Count} tiles, {batch.Tris:N0} tris";

                // a whole-continent load churns through ~1200 decompressed files;
                // the buffers are garbage by now but the heap has grown to fit them
                if (tiles.Count > 64) GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            }
            catch (Exception e) { _status = "load failed: " + e.Message; }
            finally { _loading = false; }
        });
    }

    /// Drop the camera at a world position and load the tile under it.
    void JumpToWorld(float wx, float wy)
    {
        int tx = (int)MathF.Floor(32 - wx / Tile), ty = (int)MathF.Floor(32 - wy / Tile);
        _status = $"world {wx:F0},{wy:F0} is tile {tx},{ty}";
        if (!_tileAt.ContainsKey((tx, ty))) { _status += " — not present in this map"; return; }

        LoadAround(tx, ty);
        _camera.Position = new Vector3(wx, wy, 250);
        _camera.Pitch = -0.6f;
        _camera.Speed = 80;
    }

    static void Style()
    {
        var s = ImGui.GetStyle();
        s.WindowRounding = 0; s.FrameRounding = 3; s.GrabRounding = 3;
        s.WindowPadding = new Vector2(12, 12);
        s.ItemSpacing = new Vector2(8, 6);
        s.ScrollbarSize = 12;

        var c = s.Colors;
        c[(int)ImGuiCol.WindowBg] = new Vector4(0.106f, 0.118f, 0.133f, 1f);
        c[(int)ImGuiCol.ChildBg] = new Vector4(0.078f, 0.086f, 0.098f, 1f);
        c[(int)ImGuiCol.FrameBg] = new Vector4(0.157f, 0.173f, 0.192f, 1f);
        c[(int)ImGuiCol.FrameBgHovered] = new Vector4(0.204f, 0.224f, 0.247f, 1f);
        c[(int)ImGuiCol.Button] = new Vector4(0.204f, 0.224f, 0.247f, 1f);
        c[(int)ImGuiCol.ButtonHovered] = new Vector4(0.706f, 0.361f, 0.184f, 1f);
        c[(int)ImGuiCol.ButtonActive] = new Vector4(0.784f, 0.412f, 0.212f, 1f);
        c[(int)ImGuiCol.Header] = new Vector4(0.706f, 0.361f, 0.184f, 0.55f);
        c[(int)ImGuiCol.HeaderHovered] = new Vector4(0.706f, 0.361f, 0.184f, 0.75f);
        c[(int)ImGuiCol.PlotHistogram] = new Vector4(0.706f, 0.443f, 0.184f, 1f);
        c[(int)ImGuiCol.Text] = new Vector4(0.902f, 0.894f, 0.871f, 1f);
        c[(int)ImGuiCol.TextDisabled] = new Vector4(0.478f, 0.498f, 0.529f, 1f);
        c[(int)ImGuiCol.Separator] = new Vector4(0.204f, 0.224f, 0.247f, 1f);
        c[(int)ImGuiCol.TableHeaderBg] = new Vector4(0.137f, 0.153f, 0.169f, 1f);
        c[(int)ImGuiCol.TableRowBgAlt] = new Vector4(1f, 1f, 1f, 0.025f);
    }
}

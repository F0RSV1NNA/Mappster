using System.Numerics;
using Silk.NET.OpenGL;

namespace MapExtract.Gui;

/// One flat-shaded VBO per surface class, so layers toggle without rebuilding.
public sealed unsafe class Renderer(GL gl) : IDisposable
{
    const int NClass = 9;

    public static readonly string[] ClassName =
        ["Terrain", "WMO", "Interior", "Doodad", "Water", "Ocean", "Magma", "Slime", "Unknown"];

    public static readonly Vector3[] ClassColor =
    [
        new(0.49f, 0.54f, 0.32f), new(0.36f, 0.44f, 0.56f), new(0.56f, 0.53f, 0.71f),
        new(0.66f, 0.46f, 0.25f), new(0.25f, 0.60f, 0.66f), new(0.18f, 0.37f, 0.55f),
        new(0.76f, 0.27f, 0.12f), new(0.50f, 0.68f, 0.17f), new(0.48f, 0.48f, 0.48f),
    ];

    static bool IsLiquid(int c) => c >= 4;

    readonly uint[] _vao = new uint[NClass];
    readonly uint[] _vbo = new uint[NClass];
    public readonly int[] TriCount = new int[NClass];
    public readonly bool[] Visible = Enumerable.Repeat(true, NClass).ToArray();

    public Vector3 BoundsMin, BoundsMax;
    public int TotalTris, TotalVerts;
    public bool HasMesh;

    uint _prog;
    int _uMvp, _uColor;

    const string Vert = """
        #version 330 core
        layout(location=0) in vec3 aPos;
        layout(location=1) in vec3 aNormal;
        uniform mat4 uMVP;
        out vec3 vN;
        void main() { vN = aNormal; gl_Position = uMVP * vec4(aPos, 1.0); }
        """;

    const string Frag = """
        #version 330 core
        in vec3 vN;
        uniform vec4 uColor;
        out vec4 frag;
        void main() {
            vec3 L = normalize(vec3(0.40, 1.0, 0.30));
            // abs(): terrain and WMO interiors are double-sided, both faces should light
            float d = abs(dot(normalize(vN), L));
            frag = vec4(uColor.rgb * (0.38 + 0.72 * d), uColor.a);
        }
        """;

    public void Init()
    {
        uint vs = Compile(ShaderType.VertexShader, Vert);
        uint fs = Compile(ShaderType.FragmentShader, Frag);
        _prog = gl.CreateProgram();
        gl.AttachShader(_prog, vs); gl.AttachShader(_prog, fs);
        gl.LinkProgram(_prog);
        gl.GetProgram(_prog, ProgramPropertyARB.LinkStatus, out int ok);
        if (ok == 0) throw new Exception("shader link failed: " + gl.GetProgramInfoLog(_prog));
        gl.DeleteShader(vs); gl.DeleteShader(fs);
        _uMvp = gl.GetUniformLocation(_prog, "uMVP");
        _uColor = gl.GetUniformLocation(_prog, "uColor");
    }

    uint Compile(ShaderType type, string src)
    {
        uint s = gl.CreateShader(type);
        gl.ShaderSource(s, src);
        gl.CompileShader(s);
        gl.GetShader(s, ShaderParameterName.CompileStatus, out int ok);
        if (ok == 0) throw new Exception($"{type} failed: {gl.GetShaderInfoLog(s)}");
        return s;
    }

    public void Clear()
    {
        for (int c = 0; c < NClass; c++)
        {
            if (_vbo[c] != 0) { gl.DeleteBuffer(_vbo[c]); _vbo[c] = 0; }
            if (_vao[c] != 0) { gl.DeleteVertexArray(_vao[c]); _vao[c] = 0; }
            TriCount[c] = 0;
        }
        HasMesh = false;
    }

    /// Tiles are already in absolute world coordinates, so several of them concatenate
    /// into one buffer set and land in the right place on their own. Soups are folded in
    /// one at a time and then dropped — holding a whole continent's worth is what OOMs.
    public sealed class Batch
    {
        public readonly List<float>[] Buckets = new List<float>[NClass];
        public Vector3 Lo = new(float.MaxValue), Hi = new(float.MinValue);
        public int Tris, Verts;

        public Batch() { for (int c = 0; c < NClass; c++) Buckets[c] = []; }

        public void Add(Extractor.Soup soup)
        {
            Tris += soup.TriCount + soup.LiquidTris;
            Verts += soup.Verts.Count + soup.LiquidVerts.Count;

            void Emit(List<Vector3> verts, List<int> idx, int tri, int cls)
            {
                Vector3 a = verts[idx[tri * 3]], b = verts[idx[tri * 3 + 1]], c2 = verts[idx[tri * 3 + 2]];
                var n = Vector3.Cross(b - a, c2 - a);
                n = n.LengthSquared() > 1e-18f ? Vector3.Normalize(n) : Vector3.UnitZ;
                var buf = Buckets[cls];
                foreach (var v in stackalloc[] { a, b, c2 })
                { buf.Add(v.X); buf.Add(v.Y); buf.Add(v.Z); buf.Add(n.X); buf.Add(n.Y); buf.Add(n.Z); }
            }

            int o = 0;
            foreach (var (surface, count) in new[]
                     { (0, soup.TerrainTris), (1, soup.WmoTris), (2, soup.InteriorTris), (3, soup.DoodadTris) })
            {
                for (int t = 0; t < count; t++) Emit(soup.Verts, soup.Indices, o + t, surface);
                o += count;
            }
            for (int t = 0; t < soup.LiquidTris; t++)
                Emit(soup.LiquidVerts, soup.LiquidIndices, t, 4 + Math.Min((int)soup.LiquidTriClass[t], 4));

            foreach (var v in soup.Verts) { Lo = Vector3.Min(Lo, v); Hi = Vector3.Max(Hi, v); }
            foreach (var v in soup.LiquidVerts) { Lo = Vector3.Min(Lo, v); Hi = Vector3.Max(Hi, v); }
        }
    }

    public void Upload(List<float>[] buckets, Vector3 lo, Vector3 hi, int totalTris, int totalVerts)
    {
        Clear();
        TotalTris = totalTris;
        TotalVerts = totalVerts;
        BoundsMin = lo; BoundsMax = hi;

        for (int c = 0; c < NClass; c++)
        {
            var data = buckets[c];
            TriCount[c] = data.Count / 18;
            if (data.Count == 0) continue;

            _vao[c] = gl.GenVertexArray();
            gl.BindVertexArray(_vao[c]);
            _vbo[c] = gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo[c]);
            var arr = data.ToArray();
            fixed (float* p = arr)
                gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(arr.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), (void*)0);
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), (void*)(3 * sizeof(float)));
        }
        gl.BindVertexArray(0);
        HasMesh = true;
    }

    // ---- navmesh overlay ---------------------------------------------------
    const int NavBuckets = 5;
    public static readonly string[] NavName = ["Nav ground", "Nav water", "Nav ocean", "Nav magma", "Nav slime"];
    public static readonly Vector3[] NavColor =
    [
        new(0.20f, 0.72f, 0.95f),   // ground  blue
        new(0.35f, 0.95f, 0.80f),   // water   teal
        new(0.25f, 0.55f, 0.95f),   // ocean   deeper blue
        new(0.98f, 0.45f, 0.15f),   // magma   ember
        new(0.70f, 0.95f, 0.20f),   // slime   acid
    ];

    readonly uint[] _navVao = new uint[NavBuckets];
    readonly uint[] _navVbo = new uint[NavBuckets];
    public readonly int[] NavTriCount = new int[NavBuckets];
    public bool ShowNav = true;
    public bool HasNav { get; private set; }

    static int NavBucket(int area) => area switch
    {
        NavBake.AreaWater => 1,
        NavBake.AreaOcean => 2,
        NavBake.AreaMagma => 3,
        NavBake.AreaSlime => 4,
        _ => 0,
    };

    public void ClearNav()
    {
        for (int i = 0; i < NavBuckets; i++)
        {
            if (_navVbo[i] != 0) { gl.DeleteBuffer(_navVbo[i]); _navVbo[i] = 0; }
            if (_navVao[i] != 0) { gl.DeleteVertexArray(_navVao[i]); _navVao[i] = 0; }
            NavTriCount[i] = 0;
        }
        HasNav = false;
    }

    /// The navmesh sits on the surface it was built from, so lift it slightly or it
    /// z-fights with the ground everywhere.
    public void UploadNav(List<Vector3> verts, List<int> indices, List<int> areas)
    {
        ClearNav();
        if (indices.Count == 0) return;

        var buckets = new List<float>[NavBuckets];
        for (int i = 0; i < NavBuckets; i++) buckets[i] = [];

        for (int t = 0; t < indices.Count / 3; t++)
        {
            var buf = buckets[NavBucket(t < areas.Count ? areas[t] : NavBake.AreaGround)];
            Vector3 a = verts[indices[t * 3]], b = verts[indices[t * 3 + 1]], c = verts[indices[t * 3 + 2]];
            var n = Vector3.Cross(b - a, c - a);
            n = n.LengthSquared() > 1e-18f ? Vector3.Normalize(n) : Vector3.UnitZ;
            foreach (var v in stackalloc[] { a, b, c })
            {
                var p = v + new Vector3(0, 0, 0.25f);
                buf.Add(p.X); buf.Add(p.Y); buf.Add(p.Z);
                buf.Add(n.X); buf.Add(n.Y); buf.Add(n.Z);
            }
        }

        for (int i = 0; i < NavBuckets; i++)
        {
            NavTriCount[i] = buckets[i].Count / 18;
            if (buckets[i].Count == 0) continue;
            _navVao[i] = gl.GenVertexArray();
            gl.BindVertexArray(_navVao[i]);
            _navVbo[i] = gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, _navVbo[i]);
            var arr = buckets[i].ToArray();
            fixed (float* p = arr)
                gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(arr.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), (void*)0);
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), (void*)(3 * sizeof(float)));
        }
        gl.BindVertexArray(0);
        HasNav = true;
    }

    void DrawNav()
    {
        if (!HasNav || !ShowNav) return;
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        for (int i = 0; i < NavBuckets; i++)
        {
            if (NavTriCount[i] == 0) continue;
            var c = NavColor[i];
            gl.Uniform4(_uColor, c.X, c.Y, c.Z, 0.78f);
            gl.BindVertexArray(_navVao[i]);
            gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)(NavTriCount[i] * 3));
        }
        gl.Disable(EnableCap.Blend);
    }

    public void Draw(Matrix4x4 mvp)
    {
        if (!HasMesh) return;
        gl.UseProgram(_prog);
        gl.UniformMatrix4(_uMvp, 1, false, (float*)&mvp);
        // ReSharper disable once CommentTypo
        gl.Enable(EnableCap.DepthTest);
        gl.Disable(EnableCap.CullFace);

        for (int pass = 0; pass < 2; pass++)
        {
            if (pass == 1)
            {
                gl.Enable(EnableCap.Blend);
                gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                gl.DepthMask(false);
            }
            for (int c = 0; c < NClass; c++)
            {
                if (!Visible[c] || TriCount[c] == 0 || IsLiquid(c) != (pass == 1)) continue;
                var col = ClassColor[c];
                gl.Uniform4(_uColor, col.X, col.Y, col.Z, pass == 1 ? 0.55f : 1.0f);
                gl.BindVertexArray(_vao[c]);
                gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)(TriCount[c] * 3));
            }
            if (pass == 1) { gl.DepthMask(true); gl.Disable(EnableCap.Blend); }
        }
        DrawNav();
        gl.BindVertexArray(0);
    }

    public void Dispose() { Clear(); ClearNav(); if (_prog != 0) gl.DeleteProgram(_prog); }
}

/// Free-fly camera in WoW world space: +X north, +Y west, +Z up. Rendering in world
/// space rather than a Y-up copy means the readouts are real in-game coordinates.
public sealed class FlyCamera
{
    public Vector3 Position;
    public float Yaw, Pitch;          // radians
    public float Speed = 80f;         // yards per second
    public float Fov = 60f;

    public Vector3 Forward => new(
        MathF.Cos(Pitch) * MathF.Cos(Yaw),
        MathF.Cos(Pitch) * MathF.Sin(Yaw),
        MathF.Sin(Pitch));

    // facing north, right hand points east (-Y in WoW's axes)
    public Vector3 Right => Vector3.Normalize(Vector3.Cross(Forward, Vector3.UnitZ));
    public Vector3 Up => Vector3.Cross(Right, Forward);

    public void Look(float dx, float dy)
    {
        Yaw -= dx * 0.004f;
        Pitch = Math.Clamp(Pitch - dy * 0.004f, -1.553f, 1.553f);
    }

    /// local: X right, Y forward, Z world-up.
    public void Move(Vector3 local, float dt, bool boost)
    {
        float v = Speed * (boost ? 6f : 1f) * dt;
        Position += Right * (local.X * v) + Forward * (local.Y * v) + Vector3.UnitZ * (local.Z * v);
    }

    /// Wheel dollies along the view direction, which reads as zoom.
    public void Dolly(float wheel) => Position += Forward * (wheel * Speed * 0.35f);

    /// Drop the camera south-east of the tile and above it, looking at the centre.
    public void Frame(Vector3 lo, Vector3 hi)
    {
        var centre = (lo + hi) * 0.5f;
        var size = hi - lo;
        float span = MathF.Max(MathF.Max(size.X, size.Y), 50f);

        Position = centre + new Vector3(-span * 0.62f, -span * 0.62f, span * 0.55f);
        var d = centre - Position;
        Yaw = MathF.Atan2(d.Y, d.X);
        Pitch = MathF.Atan2(d.Z, MathF.Sqrt(d.X * d.X + d.Y * d.Y));
        Speed = MathF.Max(20f, span * 0.16f);
    }

    public Matrix4x4 Mvp(float aspect)
    {
        var view = Matrix4x4.CreateLookAt(Position, Position + Forward, Vector3.UnitZ);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(
            Fov * MathF.PI / 180f, aspect <= 0 ? 1 : aspect, 0.4f, 40000f);
        return view * proj;   // row-vector convention; uploaded untransposed
    }
}

// wmeshview - native viewer for extracted WoW map tiles (.wmesh / .obj).
// Drag a tile onto the window, or pass one on the command line.

#include <SDL.h>
#include <SDL_ttf.h>
#include <SDL_opengl.h>
#include <GL/glu.h>

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

// ---- minimal VBO entry points (MinGW's opengl32 only exports GL 1.1) -------
typedef ptrdiff_t GLsizeiptrARB;
static void (APIENTRY *glGenBuffersX)(GLsizei, GLuint *);
static void (APIENTRY *glBindBufferX)(GLenum, GLuint);
static void (APIENTRY *glBufferDataX)(GLenum, GLsizeiptrARB, const void *, GLenum);
static void (APIENTRY *glDeleteBuffersX)(GLsizei, const GLuint *);
#define GL_ARRAY_BUFFER_X 0x8892
#define GL_STATIC_DRAW_X  0x88E4

static bool loadGLExts()
{
    glGenBuffersX    = (void (APIENTRY *)(GLsizei, GLuint *))SDL_GL_GetProcAddress("glGenBuffers");
    glBindBufferX    = (void (APIENTRY *)(GLenum, GLuint))SDL_GL_GetProcAddress("glBindBuffer");
    glBufferDataX    = (void (APIENTRY *)(GLenum, GLsizeiptrARB, const void *, GLenum))SDL_GL_GetProcAddress("glBufferData");
    glDeleteBuffersX = (void (APIENTRY *)(GLsizei, const GLuint *))SDL_GL_GetProcAddress("glDeleteBuffers");
    return glGenBuffersX && glBindBufferX && glBufferDataX && glDeleteBuffersX;
}

// ---- surface classes, matching Extractor.Surface byte for byte ------------
enum { NCLASS = 9 };
static const char *CLASS_NAME[NCLASS] = {
    "Terrain", "WMO", "Interior", "Doodad", "Water", "Ocean", "Magma", "Slime", "Unknown"
};
static const float CLASS_COLOR[NCLASS][3] = {
    { 0.49f, 0.54f, 0.32f },   // terrain   olive
    { 0.36f, 0.44f, 0.56f },   // wmo       slate
    { 0.56f, 0.53f, 0.71f },   // interior  periwinkle
    { 0.66f, 0.46f, 0.25f },   // doodad    tan
    { 0.25f, 0.60f, 0.66f },   // water     teal
    { 0.18f, 0.37f, 0.55f },   // ocean     deep blue
    { 0.76f, 0.27f, 0.12f },   // magma     ember
    { 0.50f, 0.68f, 0.17f },   // slime     acid
    { 0.48f, 0.48f, 0.48f },   // unknown
};
static bool isLiquid(int c) { return c >= 4; }

struct Layer {
    GLuint vbo = 0;
    int tris = 0;
    bool visible = true;
};

struct Scene {
    Layer layer[NCLASS];
    float lo[3] = { 0, 0, 0 }, hi[3] = { 0, 0, 0 };
    int totalTris = 0, totalVerts = 0;
    std::string name = "(no tile loaded)";
};

static Scene g;

// ---- loading --------------------------------------------------------------
struct RawMesh {
    std::vector<float> pos;      // xyz per vertex
    std::vector<uint32_t> idx;   // 3 per triangle
    std::vector<uint8_t> cls;    // 1 per triangle
};

static bool loadWmesh(const char *path, RawMesh &m)
{
    FILE *f = fopen(path, "rb");
    if (!f) return false;
    char magic[4];
    int32_t version = 0, nv = 0, nt = 0;
    if (fread(magic, 1, 4, f) != 4 || memcmp(magic, "WMSH", 4) != 0) { fclose(f); return false; }
    size_t ok = fread(&version, 4, 1, f) + fread(&nv, 4, 1, f) + fread(&nt, 4, 1, f);
    if (ok != 3 || nv <= 0 || nt <= 0) { fclose(f); return false; }

    m.pos.resize((size_t)nv * 3);
    m.idx.resize((size_t)nt * 3);
    m.cls.resize((size_t)nt);
    bool good = fread(m.pos.data(), 4, m.pos.size(), f) == m.pos.size()
             && fread(m.idx.data(), 4, m.idx.size(), f) == m.idx.size()
             && fread(m.cls.data(), 1, m.cls.size(), f) == m.cls.size();
    fclose(f);
    return good;
}

static bool loadObj(const char *path, RawMesh &m)
{
    FILE *f = fopen(path, "rb");
    if (!f) return false;
    char line[512];
    int cur = 0;
    while (fgets(line, sizeof(line), f)) {
        if (line[0] == 'v' && line[1] == ' ') {
            float x, y, z;
            if (sscanf(line + 2, "%f %f %f", &x, &y, &z) == 3) { m.pos.push_back(x); m.pos.push_back(y); m.pos.push_back(z); }
        } else if (line[0] == 'f' && line[1] == ' ') {
            int a, b, c;
            if (sscanf(line + 2, "%d %d %d", &a, &b, &c) == 3) {
                m.idx.push_back(a - 1); m.idx.push_back(b - 1); m.idx.push_back(c - 1);
                m.cls.push_back((uint8_t)cur);
            }
        } else if (line[0] == 'g' && line[1] == ' ') {
            std::string n(line + 2);
            while (!n.empty() && (n.back() == '\n' || n.back() == '\r' || n.back() == ' ')) n.pop_back();
            for (char &ch : n) ch = (char)tolower((unsigned char)ch);
            cur = 0;
            for (int i = 0; i < NCLASS; i++) {
                std::string k = CLASS_NAME[i];
                for (char &ch : k) ch = (char)tolower((unsigned char)ch);
                if (i == 8) k = "unknownliquid";
                if (n == k) { cur = i; break; }
            }
        }
    }
    fclose(f);
    return !m.idx.empty();
}

// Flat-shaded, de-indexed, one VBO per surface class.
static void buildScene(RawMesh &m, const char *name)
{
    for (int c = 0; c < NCLASS; c++) {
        if (g.layer[c].vbo) glDeleteBuffersX(1, &g.layer[c].vbo);
        g.layer[c] = Layer();
    }

    g.name = name;
    g.totalTris = (int)m.cls.size();
    g.totalVerts = (int)(m.pos.size() / 3);
    for (int k = 0; k < 3; k++) { g.lo[k] = 1e30f; g.hi[k] = -1e30f; }
    for (size_t i = 0; i < m.pos.size(); i += 3)
        for (int k = 0; k < 3; k++) {
            g.lo[k] = std::min(g.lo[k], m.pos[i + k]);
            g.hi[k] = std::max(g.hi[k], m.pos[i + k]);
        }

    for (int c = 0; c < NCLASS; c++) {
        std::vector<float> buf;   // interleaved position(3) + normal(3)
        for (size_t t = 0; t < m.cls.size(); t++) {
            if (m.cls[t] != c) continue;
            const float *v[3];
            bool bad = false;
            for (int k = 0; k < 3; k++) {
                uint32_t vi = m.idx[t * 3 + k];
                if ((size_t)vi * 3 + 2 >= m.pos.size()) { bad = true; break; }
                v[k] = &m.pos[(size_t)vi * 3];
            }
            if (bad) continue;

            float e1[3], e2[3], n[3];
            for (int k = 0; k < 3; k++) { e1[k] = v[1][k] - v[0][k]; e2[k] = v[2][k] - v[0][k]; }
            n[0] = e1[1] * e2[2] - e1[2] * e2[1];
            n[1] = e1[2] * e2[0] - e1[0] * e2[2];
            n[2] = e1[0] * e2[1] - e1[1] * e2[0];
            float len = std::sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
            if (len > 1e-9f) { n[0] /= len; n[1] /= len; n[2] /= len; } else { n[0] = 0; n[1] = 1; n[2] = 0; }

            for (int k = 0; k < 3; k++) {
                buf.push_back(v[k][0]); buf.push_back(v[k][1]); buf.push_back(v[k][2]);
                buf.push_back(n[0]); buf.push_back(n[1]); buf.push_back(n[2]);
            }
        }
        g.layer[c].tris = (int)(buf.size() / 18);
        if (buf.empty()) continue;
        glGenBuffersX(1, &g.layer[c].vbo);
        glBindBufferX(GL_ARRAY_BUFFER_X, g.layer[c].vbo);
        glBufferDataX(GL_ARRAY_BUFFER_X, (GLsizeiptrARB)(buf.size() * sizeof(float)), buf.data(), GL_STATIC_DRAW_X);
    }
    glBindBufferX(GL_ARRAY_BUFFER_X, 0);
}

// ---- camera ---------------------------------------------------------------
struct Orbit { float tx = 0, ty = 0, tz = 0, radius = 900, theta = 0.9f, phi = 1.02f; } cam;

static void frameAll()
{
    for (int k = 0; k < 3; k++) { }
    cam.tx = (g.lo[0] + g.hi[0]) * 0.5f;
    cam.ty = (g.lo[1] + g.hi[1]) * 0.5f;
    cam.tz = (g.lo[2] + g.hi[2]) * 0.5f;
    float sx = g.hi[0] - g.lo[0], sy = g.hi[1] - g.lo[1], sz = g.hi[2] - g.lo[2];
    cam.radius = std::max(std::max(sx, sy), sz) * 1.3f;
    if (!(cam.radius > 1.0f)) cam.radius = 900.0f;
    cam.theta = 0.9f; cam.phi = 1.02f;
}

// ---- text overlay ---------------------------------------------------------
static TTF_Font *font = nullptr;

static void drawText(int x, int y, const char *s, float r, float gg, float b, float a)
{
    if (!font || !s || !*s) return;
    SDL_Color col = { (Uint8)(r * 255), (Uint8)(gg * 255), (Uint8)(b * 255), (Uint8)(a * 255) };
    SDL_Surface *surf = TTF_RenderUTF8_Blended(font, s, col);
    if (!surf) return;
    SDL_Surface *conv = SDL_ConvertSurfaceFormat(surf, SDL_PIXELFORMAT_ABGR8888, 0);
    SDL_FreeSurface(surf);
    if (!conv) return;

    GLuint tex = 0;
    glGenTextures(1, &tex);
    glBindTexture(GL_TEXTURE_2D, tex);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
    glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA, conv->w, conv->h, 0, GL_RGBA, GL_UNSIGNED_BYTE, conv->pixels);

    glEnable(GL_TEXTURE_2D);
    glColor4f(1, 1, 1, 1);
    glBegin(GL_QUADS);
    glTexCoord2f(0, 0); glVertex2i(x, y);
    glTexCoord2f(1, 0); glVertex2i(x + conv->w, y);
    glTexCoord2f(1, 1); glVertex2i(x + conv->w, y + conv->h);
    glTexCoord2f(0, 1); glVertex2i(x, y + conv->h);
    glEnd();
    glDisable(GL_TEXTURE_2D);

    glDeleteTextures(1, &tex);
    SDL_FreeSurface(conv);
}

int main(int argc, char **argv)
{
    if (SDL_Init(SDL_INIT_VIDEO) != 0) { fprintf(stderr, "SDL_Init: %s\n", SDL_GetError()); return 1; }
    TTF_Init();

    SDL_GL_SetAttribute(SDL_GL_DEPTH_SIZE, 24);
    SDL_GL_SetAttribute(SDL_GL_DOUBLEBUFFER, 1);
    SDL_GL_SetAttribute(SDL_GL_MULTISAMPLEBUFFERS, 1);
    SDL_GL_SetAttribute(SDL_GL_MULTISAMPLESAMPLES, 4);

    SDL_Window *win = SDL_CreateWindow("wmeshview - drag a .wmesh or .obj tile onto this window",
                                       SDL_WINDOWPOS_CENTERED, SDL_WINDOWPOS_CENTERED,
                                       1400, 880, SDL_WINDOW_OPENGL | SDL_WINDOW_RESIZABLE);
    if (!win) { fprintf(stderr, "CreateWindow: %s\n", SDL_GetError()); return 1; }
    SDL_GLContext ctx = SDL_GL_CreateContext(win);
    SDL_GL_SetSwapInterval(1);

    if (!loadGLExts()) { fprintf(stderr, "this GPU/driver has no VBO support\n"); return 1; }

    const char *fontPaths[] = { "C:/Windows/Fonts/consola.ttf", "C:/Windows/Fonts/segoeui.ttf" };
    for (int i = 0; i < 2 && !font; i++) font = TTF_OpenFont(fontPaths[i], 15);

    SDL_EventState(SDL_DROPFILE, SDL_ENABLE);

    auto loadPath = [&](const char *path) {
        RawMesh m;
        size_t n = strlen(path);
        bool ok = (n > 4 && _stricmp(path + n - 4, ".obj") == 0) ? loadObj(path, m) : loadWmesh(path, m);
        if (!ok) { printf("could not read %s\n", path); return; }
        const char *base = strrchr(path, '\\');
        base = base ? base + 1 : path;
        buildScene(m, base);
        frameAll();
        printf("\n%s  %d tris  %d verts\n", base, g.totalTris, g.totalVerts);
        for (int c = 0; c < NCLASS; c++)
            if (g.layer[c].tris) printf("  %-9s %8d\n", CLASS_NAME[c], g.layer[c].tris);
        fflush(stdout);
    };

    if (argc > 1) loadPath(argv[1]);

    bool running = true, wire = false;
    bool dragRot = false, dragPan = false;
    while (running) {
        SDL_Event e;
        while (SDL_PollEvent(&e)) {
            if (e.type == SDL_QUIT) running = false;
            else if (e.type == SDL_DROPFILE) { loadPath(e.drop.file); SDL_free(e.drop.file); }
            else if (e.type == SDL_MOUSEBUTTONDOWN) {
                const Uint8 *ks = SDL_GetKeyboardState(nullptr);
                if (e.button.button == SDL_BUTTON_LEFT && !ks[SDL_SCANCODE_LSHIFT]) dragRot = true;
                else dragPan = true;
            }
            else if (e.type == SDL_MOUSEBUTTONUP) { dragRot = dragPan = false; }
            else if (e.type == SDL_MOUSEMOTION) {
                if (dragRot) {
                    cam.theta -= e.motion.xrel * 0.006f;
                    cam.phi = std::max(0.05f, std::min(3.09f, cam.phi - e.motion.yrel * 0.006f));
                } else if (dragPan) {
                    float k = cam.radius * 0.0016f;
                    float st = std::sin(cam.theta), ct = std::cos(cam.theta);
                    cam.tx += (-e.motion.xrel * k) * ct;
                    cam.tz -= (-e.motion.xrel * k) * st;
                    cam.ty += e.motion.yrel * k;
                }
            }
            else if (e.type == SDL_MOUSEWHEEL) {
                cam.radius = std::max(2.0f, std::min(60000.0f, cam.radius * std::exp(-e.wheel.y * 0.12f)));
            }
            else if (e.type == SDL_KEYDOWN) {
                SDL_Keycode k = e.key.keysym.sym;
                if (k == SDLK_ESCAPE) running = false;
                else if (k == SDLK_r) frameAll();
                else if (k == SDLK_w) wire = !wire;
                else if (k >= SDLK_1 && k <= SDLK_9) {
                    int c = k - SDLK_1;
                    g.layer[c].visible = !g.layer[c].visible;
                }
                else if (k == SDLK_0) for (int c = 0; c < NCLASS; c++) g.layer[c].visible = true;
                else if (k == SDLK_l) for (int c = 4; c < NCLASS; c++) g.layer[c].visible = !g.layer[4].visible;
            }
        }

        int W, H;
        SDL_GetWindowSize(win, &W, &H);
        glViewport(0, 0, W, H);
        glClearColor(0.055f, 0.063f, 0.075f, 1);
        glClear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT);

        glEnable(GL_DEPTH_TEST);
        glEnable(GL_MULTISAMPLE);
        glMatrixMode(GL_PROJECTION); glLoadIdentity();
        gluPerspective(55.0, H ? (double)W / H : 1.0, std::max(0.5f, cam.radius * 0.001f), cam.radius * 12.0f + 5000.0f);

        float ex = cam.tx + cam.radius * std::sin(cam.phi) * std::sin(cam.theta);
        float ey = cam.ty + cam.radius * std::cos(cam.phi);
        float ez = cam.tz + cam.radius * std::sin(cam.phi) * std::cos(cam.theta);
        glMatrixMode(GL_MODELVIEW); glLoadIdentity();
        gluLookAt(ex, ey, ez, cam.tx, cam.ty, cam.tz, 0, 1, 0);

        glEnable(GL_LIGHTING); glEnable(GL_LIGHT0);
        GLfloat lp[4] = { 0.4f, 1.0f, 0.3f, 0.0f };
        GLfloat la[4] = { 0.42f, 0.42f, 0.46f, 1.0f };
        GLfloat ld[4] = { 0.85f, 0.85f, 0.82f, 1.0f };
        glLightfv(GL_LIGHT0, GL_POSITION, lp);
        glLightfv(GL_LIGHT0, GL_AMBIENT, la);
        glLightfv(GL_LIGHT0, GL_DIFFUSE, ld);
        glEnable(GL_COLOR_MATERIAL);
        glColorMaterial(GL_FRONT_AND_BACK, GL_AMBIENT_AND_DIFFUSE);
        glDisable(GL_CULL_FACE);
        glPolygonMode(GL_FRONT_AND_BACK, wire ? GL_LINE : GL_FILL);

        glEnableClientState(GL_VERTEX_ARRAY);
        glEnableClientState(GL_NORMAL_ARRAY);

        // solids first, then translucent liquids over the top
        for (int pass = 0; pass < 2; pass++) {
            if (pass == 1) { glEnable(GL_BLEND); glBlendFunc(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA); glDepthMask(GL_FALSE); }
            for (int c = 0; c < NCLASS; c++) {
                if (!g.layer[c].visible || !g.layer[c].vbo) continue;
                if (isLiquid(c) != (pass == 1)) continue;
                glColor4f(CLASS_COLOR[c][0], CLASS_COLOR[c][1], CLASS_COLOR[c][2], pass == 1 ? 0.55f : 1.0f);
                glBindBufferX(GL_ARRAY_BUFFER_X, g.layer[c].vbo);
                glVertexPointer(3, GL_FLOAT, 6 * sizeof(float), (void *)0);
                glNormalPointer(GL_FLOAT, 6 * sizeof(float), (void *)(3 * sizeof(float)));
                glDrawArrays(GL_TRIANGLES, 0, g.layer[c].tris * 3);
            }
            if (pass == 1) { glDepthMask(GL_TRUE); glDisable(GL_BLEND); }
        }
        glBindBufferX(GL_ARRAY_BUFFER_X, 0);
        glDisableClientState(GL_VERTEX_ARRAY);
        glDisableClientState(GL_NORMAL_ARRAY);
        glPolygonMode(GL_FRONT_AND_BACK, GL_FILL);

        // ---- overlay ----
        glDisable(GL_LIGHTING); glDisable(GL_DEPTH_TEST);
        glMatrixMode(GL_PROJECTION); glLoadIdentity();
        glOrtho(0, W, H, 0, -1, 1);
        glMatrixMode(GL_MODELVIEW); glLoadIdentity();
        glEnable(GL_BLEND); glBlendFunc(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA);

        glColor4f(0.055f, 0.063f, 0.075f, 0.82f);
        glBegin(GL_QUADS);
        glVertex2i(0, 0); glVertex2i(268, 0); glVertex2i(268, 62 + NCLASS * 22 + 96); glVertex2i(0, 62 + NCLASS * 22 + 96);
        glEnd();

        char buf[256];
        drawText(14, 12, g.name.c_str(), 0.91f, 0.90f, 0.88f, 1);
        snprintf(buf, sizeof(buf), "%d tris / %d verts", g.totalTris, g.totalVerts);
        drawText(14, 32, buf, 0.55f, 0.57f, 0.60f, 1);

        int y = 62;
        for (int c = 0; c < NCLASS; c++) {
            bool on = g.layer[c].visible && g.layer[c].tris > 0;
            float dim = g.layer[c].tris ? (on ? 1.0f : 0.40f) : 0.22f;
            glColor4f(CLASS_COLOR[c][0] * dim, CLASS_COLOR[c][1] * dim, CLASS_COLOR[c][2] * dim, 1);
            glBegin(GL_QUADS);
            glVertex2i(14, y + 4); glVertex2i(26, y + 4); glVertex2i(26, y + 16); glVertex2i(14, y + 16);
            glEnd();
            if (!on) {
                glColor4f(0.35f, 0.36f, 0.38f, 1);
                glBegin(GL_LINE_LOOP);
                glVertex2i(14, y + 4); glVertex2i(26, y + 4); glVertex2i(26, y + 16); glVertex2i(14, y + 16);
                glEnd();
            }
            snprintf(buf, sizeof(buf), "%d  %-9s %8d", c + 1, CLASS_NAME[c], g.layer[c].tris);
            drawText(36, y, buf, dim * 0.95f, dim * 0.95f, dim * 0.93f, 1);
            y += 22;
        }

        y += 12;
        snprintf(buf, sizeof(buf), "X %.0f .. %.0f", g.lo[0], g.hi[0]);
        drawText(14, y, buf, 0.55f, 0.57f, 0.60f, 1); y += 20;
        snprintf(buf, sizeof(buf), "Y %.0f .. %.0f (height)", g.lo[1], g.hi[1]);
        drawText(14, y, buf, 0.55f, 0.57f, 0.60f, 1); y += 20;
        snprintf(buf, sizeof(buf), "Z %.0f .. %.0f", g.lo[2], g.hi[2]);
        drawText(14, y, buf, 0.55f, 0.57f, 0.60f, 1); y += 24;
        drawText(14, y, "1-9 toggle  0 all  L liquids  W wire  R reset", 0.45f, 0.47f, 0.50f, 1);

        glDisable(GL_BLEND);
        SDL_GL_SwapWindow(win);
    }

    if (font) TTF_CloseFont(font);
    TTF_Quit();
    SDL_GL_DeleteContext(ctx);
    SDL_DestroyWindow(win);
    SDL_Quit();
    return 0;
}

# wowmaps

Extracts collision geometry from a **live World of Warcraft retail install** and bakes it into
Detour navmesh tiles in TrinityCore's `.mmap` / `.mmtile` format.

Reads CASC directly — no MPQ extraction, no listfile required for geometry, no intermediate
export step. Terrain, buildings, doodads, dungeons and liquid all come out of the installed
client and go straight into a navmesh you can path on.

It also ships a 3D viewer, because every coordinate convention in this pipeline is the kind of
thing that produces a plausible-looking but subtly wrong mesh, and numbers alone won't tell you.

---

## Status

Built and tested against **retail 12.1.0.69587** (Midnight). It reads whatever product is in
your `.build.info`, so it should follow the live client forward — but format changes do happen,
and the notes below say which parts are most likely to break.

| | |
|---|---|
| Terrain, WMOs, doodads, WMO interior doodads | working |
| Liquid, classified water / ocean / magma / slime | working |
| Dungeons and raids (global-WMO maps) | working |
| Navmesh bake → `.mmap` / `.mmtile` | working, round-trips into a live `dtNavMesh` |
| Transports (boats, zeppelins, elevators) | **mesh only** — see [Transports](#transports) |
| WMO `Rx`/`Rz` rotation order | **unresolved** — see [Known issues](#known-issues) |
| Server-side gameobjects (doors, gates) | not included, and not in client files |

Nothing here has been validated against a running bot. Everything is verified against this
project's own reader, which is not the same thing.

---

## Requirements

- **Windows** (CASC path handling and the GUI backend are Windows-only as written)
- **.NET 10 SDK** — `winget install --id Microsoft.DotNet.SDK.10`
- A **World of Warcraft installation** (retail; classic products work for extraction too)
- **CascLib source** checked out beside this repo:
  ```
  git clone https://github.com/WoW-Tools/CascLib.git ../CascLib-src
  ```

> CascLib is built **from source on purpose**. The NuGet package (1.0.23) cannot read a 12.x
> root manifest — it throws `block.LocaleFlags == LocaleFlags.None` and nothing loads.

---

## Building

```
build                build Debug
build release        build Release
build run            build and launch
build publish        single-file dist\MapExtract.exe
build clean          wipe bin and obj first
build refresh        re-fetch the listfile-derived index files
build viewer         also build the standalone native .wmesh viewer (needs MSYS2)
```

The first build also fetches `db2index.csv` and `transports.csv` — see
[Reading the client](#reading-the-client). That step needs network access and takes a minute;
everything after is offline.

`build.bat` checks for each prerequisite and tells you how to install anything missing. It also
refuses to build while `MapExtract.exe` is running — a running instance holds the output file
open, the build fails on the copy step, and the symptom is *"my code changes did nothing"*.

`build publish` produces four files: the exe, `glfw3.dll`, `cimgui.dll` and `db2index.csv`.
The native libraries stay loose deliberately — Silk.NET resolves natives itself and does not
look inside .NET's single-file extraction directory.

---

## Using it

### GUI

Run `MapExtract.exe`. Set your install path, pick a product, press **Open**.

- **MAPS / LIST** — every map, filterable, with tile counts. Dungeons show `wmo`.
- **VIEW** — load a tile, its neighbours, or a whole map at reduced detail. Fly with WASD,
  drag to look, Space/Ctrl for altitude, Shift to sprint.
- **NAVMESH** — agent radius/height/climb/slope and cell size, bake, and draw the result over
  the source geometry colour-coded by area.
- **BAKE EVERYTHING** — every map with geometry, resumable, with a tile progress bar and ETA.
- **TRANSPORTS** — bake each transport model in its own space.

Export defaults to `out\` beside the exe.

### Command line

```
MapExtract --cli <install> <product> <mapId> <outDir> [maxTiles] [--yup] [--solid-only]
MapExtract --bakemap <mapId> [adtCount]      bake one map, ADT at a time
MapExtract --baketile <mapId> <x> <y> [r]    bake one ADT, optionally with neighbours
MapExtract --bakeall <outDir> [threads] [maps]
MapExtract --transportbake [count]
```

Diagnostics, all of which print rather than assume:

```
--tiltedspots <n> <minSize> <maxSize>   where to stand in game to settle Rx/Rz
--dungeons        global-WMO maps and their world bounds
--transports      transport models and their collision sizes
--groups          MOGP flag census across many WMOs
--polys           where the triangles actually come from
--zones           tile → zone name sanity check
--findtable <Name> [from] [to]          locate a DB2 by scanning fdids
--dump <Name>     a DB2's columns and first rows
```

---

## How it works

### Reading the client

```
CASC storage (local, product from .build.info)
  └ Map.db2                    map list + WdtFileDataID
     └ WDT
        ├ MAID                 per-tile split-ADT FileDataIDs
        │   ├ root ADT         MCNK heightmaps, holes, MH2O liquid
        │   └ _obj0 ADT        MDDF / MODF placements (FileDataIDs since Legion)
        │       ├ WMO          root → GFID groups, MOPY collision filter, MODS/MODI/MODD
        │       └ M2           collision hull only (~29% of doodads have none)
        └ MODF                 global WMO — the whole map, for most classic dungeons
```

Only the **root** and **_obj0** ADTs are read. `_tex0`, `_obj1` and `_lod` are irrelevant to
collision and are the bulk of the bytes.

DB2 tables are resolved by name through `db2index.csv`, the `dbfilesclient/` slice of the
[community listfile](https://github.com/wowdev/wow-listfile). Scanning FileDataIDs and matching
DBD layouts does **not** work reliably — an audio table matched the `GameObjectDisplayInfo`
definition cleanly, with 100% valid file references.

The listfile is wowdev's, not ours, so it is not vendored. `build.bat` fetches it after the
first build and writes the two indexes it needs:

```
db2index.csv      ~1,300 DB2 tables by name
transports.csv    ~143 transport models
```

It is **streamed and filtered, never saved** — the full 146 MB file is over GitHub's limit and
nothing reads it at runtime. Regenerate after a patch moves FileDataIDs:

```
build refresh
```

or directly, which is what `build.bat` calls:

```
MapExtract --makeindex <dir>
```

### Baking

Geometry → per-triangle Recast areas → one Detour tile per ADT → `.mmtile`.

The bake is per-ADT and resumable. Each ADT is baked with a **thin band of its neighbours'
geometry** (8 yd) so Recast's border ring has something real to work with and adjacent tiles
link up. Loading whole neighbouring ADTs for a ~1.3 yd border is ~400× more geometry than
needed and will exhaust memory on dense ground.

---

## Coordinate conventions

Every one of these was derived empirically against an in-file oracle and then checked against
external ground truth. Changing any of them silently produces a plausible but wrong mesh.

| transform | value |
|---|---|
| ADT placement → world | `(O − p.Z, O − p.X, p.Y)`, `O = 32 × 533.33333` — determinant **+1** |
| model → placement (WMO and M2) | `(v.Y, v.Z, v.X)` |
| world → Recast | `(wowY, wowZ, wowX)` — cyclic swap, **no negation** |
| MCNK position | `0x68` is world **X**, `0x6C` is world **Y** |
| MCVT index order | row-major; first index runs along X |
| tile row / col | `row = floor(32 − worldX/533.33333)`, `col = floor(32 − worldY/533.33333)` |
| WMO rotation | `Rz(z) · Ry(y) · Rx(x)`, degrees — **see Known issues** |
| MODD doodad quaternion | `(x, y, z, w)` |
| global WMO (dungeons) | same, but **without** the `O −` translation; its space is centred on zero |

The det **+1** note matters: the sibling transform that takes `p.X` into world X has determinant
−1. It is a reflection, it passes an axis-aligned-bounding-box test, and it mirrors every town
layout in the game. Verified against Stormwind, Ironforge, Booty Bay and Undercity.

---

## Output format

Matches TrinityCore's layout, so anything that reads TC mmaps reads these.

```
0000.mmap                 'MMAP' + version 16 + dtNavMeshParams + pad   (40 bytes)
0000_48_30.mmtile         MmapTileHeader (20 bytes) + native dtMeshData
transports/116315.mmap    transports use the model FileDataID as the map id
```

`MmapTileHeader` is `{ u32 'MMAP', u32 dtVersion=7, u32 mmapVersion=16, u32 size, u8
usesLiquids, u8 pad[3] }`, then the payload begins at `dtMeshHeader` magic `DNAV`. The payload
is Detour's own C-struct serialisation, byte-identical to what a C++ `dtNavMesh` expects — no
translation needed on load.

Filenames use **ADT row/col**. The `x`/`y` inside the tile header are Detour grid indices
measured from the `.mmap` origin, and are deliberately different numbers.

### Areas and flags

Ground, water and magma keep TrinityCore's ids so existing consumers keep working. Ocean and
slime are added, because the extractor can tell them apart and collapsing them loses real
information.

| area | id | flag | |
|---|---|---|---|
| ground | 11 | `0x01` | TC |
| *(steep ground)* | 10 | `0x02` | TC — not emitted |
| water | 9 | `0x04` | TC — lakes, rivers, pools |
| magma | 8 | `0x08` | TC |
| slime | 7 | `0x10` | new |
| ocean | 6 | `0x20` | new |

**Suggested query filter: `0x25`** (ground | water | ocean), leaving magma and slime out so a
path never routes through them.

> If your consumer uses TrinityCore's old `0x0D` mask it will **exclude ocean** (so it won't
> swim the sea) and **include magma** (so it will happily path through lava). Widen it.

Liquid keeps its class regardless of slope — a flat water surface is still swimmable.

---

## Transports

Transports move, so their geometry cannot live in a world tile. Their **models** are baked here,
each in its own space, into `transports/<modelFdid>.mmap` + `.mmtile`.

**Placement is a live-client question and is not baked.** `GameObjects.db2` holds only
decorative objects — there are zero type 11/15 entries — so transport spawns come from the
server. That is fine for a client-side consumer: the runtime sees the transport in the object
manager with its DisplayID and position.

What a consumer still has to implement:

1. Resolve the boarded transport's DisplayID → model FileDataID via `GameObjectDisplayInfo`
   (fdid `1266277`)
2. Load `transports/<fdid>.mmap`
3. Transform the player's world position into the transport's local frame using the live
   object's position and rotation
4. Path in that frame, transform waypoints back

`TransportAnimation.db2` and `TransportRotation.db2` carry the canonical paths if you want to
predict motion, but the live object is authoritative.

---

## Known issues

### WMO Rx/Rz rotation order — unresolved

For placements with non-zero `rot.X` or `rot.Z`, it is not established whether the correct
order is `Rz·Ry·Rx` or `Rx·Ry·Rz`. **`Rz·Ry·Rx` is currently used.**

The file data cannot settle it. MODF extents were tested three ways — containment, bounding-box
match from vertices, and bounding-box match from the MOHD box — plus nine candidate orderings
including axis-swapped and negated variants. Per-placement, `Rz·Ry·Rx` wins **53.5%** of the
time. That is a coin flip.

It is not negligible: the two orderings displace geometry by **12.7 yd on average and up to
252 yd**, across ~4,000 placements.

**To settle it**, run `--tiltedspots` for a list of tilted objects with world coordinates, fly
to one in game, then load the same tile in the viewer and toggle **`rotation Rx*Ry*Rz`**.
Compare *which way it leans*, not where it sits — position is nearly identical under both, only
the tilt axis changes.

### Recast's 16-bit vertex ceiling

A polymesh cannot exceed **65,535 vertices**, and one Detour tile per ADT is what the TC layout
requires. Dense retail ground gets close: the worst tile measured (Voidspire) reached **58,869
— 90% of the limit**. A denser zone will overflow and that tile will simply be missing.

`MaxMeshVerts` is reported on every bake so you can watch the headroom. If you hit it, raising
cell size is the lever.

### Patching mid-bake

A client patch rewrites CASC underneath a running bake. Because the bake resumes by filename,
stale tiles from the old build are kept and silently mixed with new ones. **If the client
patches during a run, delete the output and start over.**

### Other

- Tilted WMOs aside, `MODF` extents are authored rather than recomputed, so they do not match
  transformed geometry exactly (~0.048 normalised residual).
- Antiportal and unreachable WMO groups are filtered out. They are ~11% of groups but only
  0.04% of triangles — this is a correctness fix (phantom floors and walls), not a size one.
- M2 doodads contribute their **collision hull**, not their render mesh. A tree that looks
  detailed in game is often a 12-triangle box. This is correct and is what the client uses.
- Terrain is genuinely low-poly: MCVT is 145 vertices per 33.3 yd chunk, ~4.2 yd spacing. There
  is nothing finer in the files.

---

## Third party

| | |
|---|---|
| [CascLib](https://github.com/WoW-Tools/CascLib) (TOM_RUS) | CASC storage — built from source |
| [DotRecast](https://github.com/ikpil/DotRecast) | C# port of Recast/Detour |
| [DBCD](https://github.com/wowdev/DBCD) | DB2 reading, with definitions from WoWDBDefs |
| [Silk.NET](https://github.com/dotnet/Silk.NET) + [ImGui.NET](https://github.com/ImGuiNET/ImGui.NET) | viewer |
| [wow-listfile](https://github.com/wowdev/wow-listfile) | source of `db2index.csv` and `transports.csv` |

File format documentation throughout comes from [wowdev.wiki](https://wowdev.wiki).

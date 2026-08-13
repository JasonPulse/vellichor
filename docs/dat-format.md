# Legacy Client DAT Archive Format

Implementation-ready specification for building a C# reader of the legacy client's on-disk
data archives (the "DAT" files), targeting a Godot client. This documents **file formats
only**; it ships no copyrighted assets. It is intended for reading a retail install the
user legally owns.

All claims are cited inline to primary reverse-engineering sources. Where a fact could
not be verified from source, it is called out explicitly in
[§8 Known gaps](#8-known-gaps--reverse-engineer-yourself).

Endianness note: the format is little-endian throughout (PS2/x86 origin). All
multi-byte integers below are **little-endian** unless stated otherwise.

---

## 1. FTABLE.DAT / VTABLE.DAT — file-id resolution (PRIMARY DELIVERABLE)

A game resource is addressed by a single integer **file id** (`FileNumber`). Resolving
it to an on-disk `.DAT` file is a two-table lookup. The authoritative implementation is
POLUtils `PlayOnline.FFXI/FFXI.cs`, method `GetFilePath(int FileNumber, out byte App,
out short Dir, out byte File)`.
Source: <https://github.com/Windower/POLUtils/blob/master/PlayOnline.FFXI/FFXI.cs>

### 1.1 The table pair, per ROM set

Each ROM set `i` (1..N) has its own table pair:

| ROM set `i` | Folder    | Table files                    |
|-------------|-----------|--------------------------------|
| 1           | `ROM`     | `VTABLE.DAT`,  `FTABLE.DAT`    |
| 2           | `ROM2`    | `VTABLE2.DAT`, `FTABLE2.DAT`   |
| 3           | `ROM3`    | `VTABLE3.DAT`, `FTABLE3.DAT`   |
| …           | …         | …                              |
| n           | `ROM{n}`  | `VTABLE{n}.DAT`,`FTABLE{n}.DAT`|

The suffix equals the ROM number and is **empty for ROM set 1** (base `ROM`). The table
files live inside their own ROM folder (e.g. `ROM2/VTABLE2.DAT`). POLUtils scans
`i = 1 .. 19`; only the ROM sets physically present on disk are used.
Source (loop and filename construction): FFXI.cs `GetFilePath`.

Retail installs historically ship `ROM` + `ROM2..ROM9`. Later expansions added more;
XIPivot added redirection support for `ROM10`–`ROM13` in release v4.1.104, so a robust
reader should enumerate every `ROM{n}` present rather than hard-coding 9.
Source: <https://github.com/HealsCodes/XIPivot/releases/tag/v4.1.104>

### 1.2 VTABLE — "which ROM set owns this file id"

`VTABLE{suffix}.DAT` is a flat array of **1 byte per file id**, indexed directly by
`FileNumber`. The byte value is the owning ROM-set number. A given ROM set's VTABLE holds
non-zero only for the ids it owns, which is why POLUtils opens each ROM's VTABLE in turn
and tests whether the byte equals that loop index:

```csharp
// FFXI.cs (POLUtils) — abridged
VBR.BaseStream.Seek(FileNumber, SeekOrigin.Begin);   // 1 byte per id
if (VBR.ReadByte() == i)                              // i == this ROM set number
{
    // this ROM set owns FileNumber -> read FTABLE for its location
    App = (byte)(i - 1);
    ...
}
```

- Entry size: **1 byte**, offset = `FileNumber`.
- Value: ROM-set index that owns the file (`== i` when found).
- `App = i - 1` (0-based ROM-set index used to rebuild the path in §1.4).

Source: FFXI.cs `GetFilePath`.

### 1.3 FTABLE — "directory + file number on disk"

`FTABLE{suffix}.DAT` is a flat array of **2 bytes (uint16, little-endian) per file id**,
indexed by `FileNumber`. The 16-bit value is bit-packed into a directory number and a
file number:

```csharp
// FFXI.cs (POLUtils)
FBR.BaseStream.Seek(2 * FileNumber, SeekOrigin.Begin);
ushort FileDir = FBR.ReadUInt16();
Dir  = (short)(FileDir / 0x80);   // high 9 bits  -> directory number
File = (byte)(FileDir % 0x80);    // low  7 bits  -> file number within directory
```

- Entry size: **2 bytes**, offset = `2 * FileNumber`.
- Encoding of the uint16 `FileDir`:
  - `Dir  = FileDir >> 7`   (equivalently `FileDir / 0x80`)
  - `File = FileDir & 0x7F` (equivalently `FileDir % 0x80`)
- So each on-disk directory holds at most **128** (`0x80`) files (0..127).

Source: FFXI.cs `GetFilePath`. Cross-checked against TeoTwawki `DatLoader_old.cpp`,
which performs the identical `Dir = FileDir / 0x80`, `File = FileDir % 0x80` split on a
`ReadUInt16` at `2 * FileNumber`.
Source: <https://github.com/TeoTwawki/ffxi-dat-hacking/blob/master/DatLoader/DatLoader/DatLoader_old.cpp>

### 1.4 Building the on-disk path

Once `(App, Dir, File)` are known (POLUtils second `GetFilePath` overload):

```csharp
string ROMDir = "Rom";
if (App > 0) { ++App; ROMDir += App.ToString(); }   // App 0 -> "Rom", 1 -> "Rom2", ...
path = <FFXI install root> / ROMDir / Dir.ToString() / (File.ToString() + ".dat");
```

Result: `<install>/ROM[n]/<Dir>/<File>.DAT`, e.g. `ROM/17/24.DAT` or `ROM2/5/91.DAT`.
(`App = i - 1`, then `++App` restores `i`, so ROM set 1 → `ROM`, set 2 → `ROM2`, etc.)
Source: FFXI.cs `GetFilePath(byte App, short Dir, byte File)`.

### 1.5 Reference C# resolver (drop-in shape)

```csharp
// Returns absolute path to the .DAT for a numeric file id, or null if unmapped.
public static string ResolveFileId(string installRoot, int fileId)
{
    for (int i = 1; i < 20; i++)
    {
        string suffix = i == 1 ? "" : i.ToString();
        string romDir = i == 1 ? "ROM" : $"ROM{i}";
        string vtable = Path.Combine(installRoot, romDir, $"VTABLE{suffix}.DAT");
        string ftable = Path.Combine(installRoot, romDir, $"FTABLE{suffix}.DAT");
        if (!File.Exists(vtable) || !File.Exists(ftable)) continue;

        using var v = new FileStream(vtable, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (fileId >= v.Length) continue;
        v.Seek(fileId, SeekOrigin.Begin);
        if (v.ReadByte() != i) continue;                 // not owned by this ROM set

        using var f = new FileStream(ftable, FileMode.Open, FileAccess.Read, FileShare.Read);
        f.Seek(2L * fileId, SeekOrigin.Begin);
        Span<byte> b = stackalloc byte[2];
        f.ReadExactly(b);
        ushort fileDir = (ushort)(b[0] | (b[1] << 8)); // little-endian
        int dir  = fileDir >> 7;
        int file = fileDir & 0x7F;
        return Path.Combine(installRoot, romDir, dir.ToString(), $"{file}.DAT");
    }
    return null;
}
```

Windows paths on disk are case-insensitive; on macOS/Linux the real folders are usually
uppercase `ROM`/`ROM2` and uppercase `.DAT`. Normalize case for cross-platform reads.

---

## 2. ROM folder / DAT layout on disk

- Install root contains `ROM/`, `ROM2/` … `ROM9/` (and, on modern clients, higher).
  Each ROM set is one expansion/patch bucket.
- Each ROM set folder contains its own `VTABLE{n}.DAT` and `FTABLE{n}.DAT` plus numbered
  subdirectories `0/`, `1/`, `2/`, … Each subdirectory holds up to 128 files named
  `0.DAT` … `127.DAT`.
- Full path template: `ROM[n]/<Dir>/<File>.DAT`, with `Dir = FileDir>>7`,
  `File = FileDir & 0x7F` from §1.3.
- The mapping from file id → `(ROM set, Dir, File)` is **not derivable arithmetically**;
  it must go through VTABLE/FTABLE. The tables are the only source of truth.

Note: TeoTwawki's newer `DatLoader.cpp` demonstrates an alternative "flat" addressing for
some tooling where `fileId >= 1000000` encodes `dir = fileId / 1000000`, but that is a
tool convenience, **not** the client's on-disk scheme; use VTABLE/FTABLE.
Source: <https://github.com/TeoTwawki/ffxi-dat-hacking/blob/master/DatLoader/DatLoader/DatLoader_old.cpp>

---

## 3. Identifying a resolved file's TYPE

There is **no universal whole-file magic** that says "this DAT is a zone" vs "a model"
vs "a string table." Two mechanisms are used in practice:

### 3.1 By file-id / directory (external lookup)

The client knows what each id is because id ranges are conventional and documented in
community lookup tables. There is no header to read for the top-level classification —
you either know the id maps to a zone, or you sniff the contents (§3.2).
Zone → dat mappings exist as community data:
- xurion `ffxi-map-dats` `ZONES.md` (zone name ↔ `ROM/<dir>/<file>.DAT`; early zones live
  in `ROM/17/…`, cities/expansions in `ROM/18/…`, newer content in `ROM/282+`).
  Source: <https://github.com/xurion/ffxi-map-dats/blob/master/ZONES.md>
- Codecomp gist "FFXI zone DAT file locations" (zone id ↔ file id ↔ spawn coords).
  Source: <https://gist.github.com/Codecomp/00a75f8a65f045bc24057a7726c4251f>
- POLUtils resolves and classifies many id ranges (items, strings, NPC lists, models);
  its `PlayOnline.FFXI` project is the broadest classified map.
  Source: <https://github.com/Windower/POLUtils/tree/master/PlayOnline.FFXI>

### 3.2 By chunk contents (content sniffing)

Most binary DATs are a **chunked container**. Each chunk carries a 7-bit **type** in its
header (§4.1). You classify a file by the chunk types it contains:

- A **visual zone** DAT contains `MZB` (type `0x1c`, object placement/collision),
  `MMB` (type `0x2e`, meshes), and `IMG` (type `0x20`, textures) chunks.
- Texture-only, model-only, event, etc. contain their corresponding chunk types.

Source (chunk-type switch): galkareeve `TDWMap.cpp`
<https://github.com/galkareeve/ffxi/blob/master/DatLoader/DatLoader/TDWMap.cpp>

### 3.3 Non-chunked / text DATs

String tables, item data, dialog, and similar are **not** in the chunk container; they
are fixed-record binary blobs (e.g. the NPC/Monster list DATs are always a multiple of
`0x1C` = 28 bytes per record). Type here is purely by file id + known record layout.
Source: <https://github.com/Windower/POLUtils/blob/master/Wiki/NPCListFormat.wiki>

---

## 4. Zone visual mesh DAT format

Primary parser used below: galkareeve `ffxi` (a working C++/D3D zone map viewer).
Source: <https://github.com/galkareeve/ffxi> (see `DatLoader/DatLoader/TDWMap.cpp` and
`TDWAnalysis.h`). Type structs quoted from `TDWAnalysis.h`.

### 4.1 The chunk container header (`DATHEAD`)

A zone DAT is a linear sequence of 16-byte-aligned chunks. On-disk header per chunk
(from `TDWAnalysis.h`, `#pragma pack(1)`):

```c
typedef struct {
  char  name[4];        // 4-byte resource tag/name
  // next 4 bytes are a little-endian packed bitfield:
  long  type:7;         // bits  0..6   chunk type (see table)
  long  next:19;        // bits  7..25  chunk length in 16-byte units
  long  is_shadow:1;    // bit   26
  long  is_extracted:1; // bit   27
  long  ver_num:3;      // bits  28..30
  long  is_virtual:1;   // bit   31
  // (parent / nextblock are runtime pointers, NOT on disk)
} DATHEAD;              // on-disk header occupies 16 bytes; payload begins at +16
```

Iteration (galkareeve `FFXIFile::NextData`):

```c
unsigned int next = phd->next;
next = (next & 0x7ffff) * 16;   // 19-bit length, in 16-byte units -> byte length
pData += next;                  // advance to next chunk
```

So, for a C# reader:
- Read 4-byte `name`.
- Read a `uint32` little-endian; `type = value & 0x7F`, `next = (value >> 7) & 0x7FFFF`.
- Chunk byte length = `next * 16`. Payload starts at `chunkStart + 16`.
- Advance `chunkStart += next * 16` until you run off the end.

Source: `TDWAnalysis.h` struct + `TDWAnalysis.cpp` `NextData`; dispatch in `TDWMap.cpp`.

### 4.2 Chunk types relevant to a zone

| `type` | Tag  | Contents                                                        |
|--------|------|-----------------------------------------------------------------|
| `0x1c` | MZB  | Map object placement + collision. Object count = `*(int*)(payload+4) & 0xFFFFFF`; object array (`SMZBBlock100`, 100 bytes each) begins at `payload+32`. Each entry has a transform and a **16-byte id** that references an MMB. |
| `0x2e` | MMB  | Mesh/model geometry (see §4.3). Each MMB's 16-byte id (at `payload+16` region) is matched by MZB entries to place it. |
| `0x20` | IMG  | Texture image (palettized or DXT1/DXT3 DDS). Carries a 16-byte texture id; MMB meshes reference textures by matching this 16-byte id. |

Source (switch on `hd.type` with these exact constants, MZB object count/offset, MMB
list, IMG add): `TDWMap.cpp` lines ~164–200; texture id match via `memcmp(...,16)`.

### 4.3 MMB geometry payload

From galkareeve `DrawMMB` (`TDWMap.cpp`). Within an MMB mesh block:

```
offset (relative to mesh block start P):
  P + 0 .. +15         : 16-byte MMB block header (id lives here; matched by MZB)
  P + 16               : int16  vertexCount   (nVer)
  P + 16 + 4           : vertex array, stride 36 bytes each, nVer entries
  P + 16 + 4 + nVer*36 : int32  indexCount    (nIdx)
  P + 16 + 4 + nVer*36 + 4 : index array, uint16 each (D3DFMT_INDEX16), nIdx entries
```

- Vertex stride is **36 bytes** (passed as the stride to `DrawIndexedPrimitiveUP`).
- Indices are **16-bit**, drawn as **triangle strips** (`D3DPT_TRIANGLESTRIP`).
- Textures are bound per mesh by matching a 16-byte texture id against loaded IMG chunks.

Source: `TDWMap.cpp` `DrawMMB` — `nVer = *(short*)(p+16)`,
`nIdx = *(int*)(p+16+4+nVer*36)`, index array at `p+16+4+nVer*36+4`, stride `36`,
`D3DFMT_INDEX16`, `D3DPT_TRIANGLESTRIP`.

The exact internal layout of the 36-byte vertex is **not spelled out** in that source
(it is fed straight to a fixed-function D3D FVF). The conventional/most-cited layout is:

```
float3 position (12) | float3 normal (12) | uint32 color/BGRA (4) | float2 uv (8) = 36
```

Treat this as the working hypothesis to confirm against the corpus (see §8). An MMB may
contain multiple such mesh blocks plus its own sub-header (`SMMBHEAD`/`SMMBHEAD2` in
`TDWAnalysis.h`, carrying `MMBSize:24` and an 8-char name) — you walk mesh blocks inside
the MMB, not just one.

### 4.4 IMG texture payload

`TDWAnalysis.h` defines several IMG variants (`IMGINFO`, `IMGINFO05`, `IMGINFOB1`,
`IMGINFOA1`, `IMGINFO81_DDS`). Common shape: a `flg` byte, a 16-byte `id`, width/height
(`imgx`,`imgy`), a `widthbyte`, then either a 256-entry palette (`palet[0x100]`, 8-bit
paletted) or a DDS block (`ddsType[4]` = e.g. "DXT1"/"DXT3", `size`, `noBlock`).
Decode paletted → RGBA, or hand the embedded DDS to a DXT decoder.
Source: `TDWAnalysis.h` IMG structs + DXT handling in `TDWAnalysis.cpp`.

### 4.5 M0 difficulty assessment

To render one zone you must, in order:
1. Resolve the zone's file id → path (§1). **Trivial.**
2. Walk the chunk container (§4.1). **Easy** — 16-byte header, `type`/`next` bit unpack.
3. Parse MZB object placements (transform + 16-byte MMB id). **Medium** — need the exact
   `SMZBBlock100` (100-byte) transform layout; positions/rotation/scale offsets must be
   confirmed (see §8).
4. Parse MMB meshes: vertexCount/vertices/indexCount/indices (§4.3). **Easy-medium** —
   offsets are known; the 36-byte vertex field layout needs one verification pass.
5. Decode IMG textures (paletted or DXT). **Medium** — DXT is standard; paletted is
   simple; matching 16-byte ids is trivial.
6. Assemble: instance each MMB at each MZB transform, apply textures. **Medium.**

Net: an M0 "decode one zone and render static geometry (untextured or single-texture)" is
achievable from the cited sources. The two things you will most likely have to
reverse-engineer/confirm against real files are the **36-byte vertex field layout** and
the **`SMZBBlock100` transform layout**.

---

## 5. Recommended smallest/simplest starting zone for M0

The sources do not name a single canonical "simplest" zone, but they do point at the
easiest-to-obtain, smallest cluster:

- The `ffxi-map-dats` project deliberately started with the **earliest/simplest zones in
  `ROM/17/`** (files `ROM/17/24.DAT` … `ROM/17/72.DAT`), i.e. the starter areas
  (West/East Ronfaure and neighbors) and small early dungeons (Giddeus, etc.). These are
  single-file outdoor zones — one DAT, modest object count.
  Source: <https://github.com/xurion/ffxi-map-dats/blob/master/ZONES.md>

Recommendation: pick a **single-file outdoor starter zone from `ROM/17/`** (e.g. an early
Ronfaure/Gustaberg/Sarutabaruta field). Rationale: one DAT, one MZB, a manageable set of
MMB objects, and community reference imagery exists for visual diffing. Avoid multi-floor
dungeons (e.g. Pso'Xja spans 16+ DATs) and the big cities (Jeuno/San d'Oria) for M0 —
they are many files and far more objects.

---

## 6. Source index (primary, code-first)

- POLUtils (Windower fork) — authoritative FTABLE/VTABLE resolver, broad id classification:
  <https://github.com/Windower/POLUtils> · key file
  <https://github.com/Windower/POLUtils/blob/master/PlayOnline.FFXI/FFXI.cs>
- TeoTwawki `ffxi-dat-hacking` — C++ loader, confirms FTABLE split, plus Blender tooling:
  <https://github.com/TeoTwawki/ffxi-dat-hacking>
- TeoTwawki `DatFFXITool` — extract/repackage reference:
  <https://github.com/TeoTwawki/DatFFXITool>
- galkareeve `ffxi` — working zone map viewer; chunk container + MZB/MMB/IMG parsing:
  <https://github.com/galkareeve/ffxi> (`DatLoader/DatLoader/TDWMap.cpp`, `TDWAnalysis.h`)
- adamharmstrong `FFXI_Modding` — per-type dat parsers (`Dat21/29/30/34/49/54/69/96`),
  Blender export notes:
  <https://github.com/adamharmstrong/FFXI_Modding>
- HealsCodes `XIPivot` — runtime DAT redirection; documents ROM10–13 + VTABLE/FTABLE
  redirection: <https://github.com/HealsCodes/XIPivot> · release
  <https://github.com/HealsCodes/XIPivot/releases/tag/v4.1.104>
- InoUno `xi-tinkerer` — modern Rust dat encode/decode (formats derived from POLUtils);
  good clean-room reference for record layouts:
  <https://github.com/InoUno/xi-tinkerer>
- xurion `ffxi-map-dats` — zone ↔ dat path map, starter-zone list:
  <https://github.com/xurion/ffxi-map-dats/blob/master/ZONES.md>
- Codecomp gist — zone id ↔ file id ↔ coordinates:
  <https://gist.github.com/Codecomp/00a75f8a65f045bc24057a7726c4251f>

---

## 7. Ashita / Noesis notes

- **Ashita (atom0s)**: Ashita's public repos focus on the addon/injection runtime and
  memory structures rather than a documented standalone DAT-format spec; its resource
  code overlaps POLUtils' understanding. Use POLUtils/xi-tinkerer as the code-of-record
  for on-disk layout. (Not independently quoted here — see §8.)
- **Noesis FFXI plugin**: Noesis can open zone DATs directly and export OBJ/FBX, and its
  "FF11 Optimize Geometry" load option merges duplicate/degenerate verts. This confirms
  the MMB → triangle-strip → per-object-instance model above but the plugin's Python
  source was not retrieved here; it is a strong secondary confirmation target.
  Source: <http://ffximodding.blogspot.com/2016/01/noesis-3d-model-viewer-and-extraction.html>

---

## 8. Known gaps — reverse-engineer yourself

These were **not** nailed down from the sources fetched and must be confirmed against real
files from your legal install:

1. **36-byte MMB vertex field layout.** Stride 36 is confirmed; the internal breakdown
   (position/normal/color/uv order, whether color is BGRA `uint32`, whether a second UV
   or tangent exists) is the conventional guess in §4.3, not proven from the cited code.
   Verify by dumping a known-good mesh and checking bounds/UVs.
2. **`SMZBBlock100` (100-byte MZB object) exact layout.** Confirmed: id is a 16-byte MMB
   reference, entries are 100 bytes, count = `*(int*)(payload+4) & 0xFFFFFF`, array at
   `payload+32`. The exact offsets of position (float3), rotation, and scale inside the
   100 bytes were not extracted — reverse them (galkareeve `SMZBBlock100` /
   `MMBTransform` in `DatLoader.h`/`.cpp` is the place to read next).
3. **MMB multi-mesh sub-structure.** An MMB holds a header (`SMMBHEAD`/`SMMBHEAD2`,
   `MMBSize:24` + 8-char name) then one or more mesh blocks; the exact walk between mesh
   blocks and material/texture-id association per block needs confirmation in
   `decode_mmb`.
4. **IMG variant discrimination.** Multiple IMG structs exist (paletted vs DXT, 57/61/69
   byte header variants keyed off the leading `flg` byte); the precise `flg` → variant
   mapping must be read from `TDWAnalysis.cpp` / tested against files.
5. **Complete file-id → content-type map.** No single authoritative table was found;
   assemble from POLUtils' classified ranges + `ffxi-map-dats` + the Codecomp gist, and
   fall back to chunk-type sniffing (§3.2).
6. **Whole-DAT vs per-chunk type.** Confirmed there is no top-level file magic; classify
   by id lookup or by scanning chunk types. If you need a fast "is this a zone?" check,
   scan for the presence of MZB(`0x1c`)+MMB(`0x2e`) chunks.
7. **Ashita and Noesis source** were not directly quoted; if you want a third
   cross-check on the mesh layout, pull the Noesis FFXI Python plugin and Ashita's
   resource code and diff against §4.

---

## 9. Corpus findings — verified against this install (tools/probe)

Empirical results from the `Vellichor.Dat` reader + `tools/probe` against a full local
copy of the ROM tree. These correct the stale file-location hints in §5.

- **§1 resolver: CONFIRMED.** 54,108 file ids swept, 100% resolved to a `.DAT` that
  exists on disk, across ROM sets 1–9. Note the base set's `VTABLE.DAT`/`FTABLE.DAT` live
  at the install **root**, not inside `ROM/` (the common reference resolver gets this
  wrong — `DatArchive` special-cases it).
- **§4.1 chunk container: CONFIRMED.** `DATHEAD` unpack (`type = packed & 0x7F`,
  `next = (packed >> 7) & 0x7FFFF`, len = `next*16`) tiles every chunked file cleanly to a
  terminating `end.`/`0x00` chunk. Non-chunked DATs are correctly detected (they don't
  tile) rather than mis-parsed.
- **§5 starting zone is STALE for a modern client.** `ROM/17/` here is **per-zone map
  images** (`m_NN`, one big `0x20` image + `0x31`), not terrain. `ROM/0/1.DAT` is the
  **menu/UI** set (`menu`, mostly `0x30`). The old `ffxi-map-dats` `ROM/17` layout no
  longer holds — SE repacked the ROMs.
- **Real terrain location (this install):** zone geometry is spread across `ROM/0`,
  `ROM2/0`, `ROM3/0`, `ROM4/0`, `ROM5/0` (and dungeon dirs like `ROMn/5`, `ROMn/12`).
  Tree-wide, **1,270 files contain MMB (`0x2e`)** and **647 contain MZB (`0x1c`)** — so
  the §4 type constants are correct; only the location hint was wrong.
- **Zone-code naming (from chunk name tags):** the leading resource name encodes the
  zone: **`f_xx` = outdoor field**, **`d_xx` = dungeon** (e.g. `f_ro` Ronfaure, `d_ba`,
  `d_pa`). Sub-resources within a zone: `mode` (models), `soun`, `effe`, `e0NN`/`evNN`
  (events), `wt_`/`wd_`/`ws_` (weather/water).
- **Confirmed M0 target: `ROM5/0/11.DAT` (`f_ro`, Ronfaure, 15 MB).** Composition:
  **1× MZB** (`0x1c`, 8.2 MB whole-zone placement/collision) + **308× MMB** (`0x2e`
  meshes) + **81× IMG** (`0x20` textures, e.g. `cl`, `go_k`, `juno`, `kusa`). A
  self-contained outdoor field — the right first zone to decode and render.

**To find any zone's file** on this install: `tools/probe hunt <corpus>` lists large
chunked files with their zone-name tags; pick the `f_`/`d_` file you want.

### Geometry decode — VERIFIED

- **MMB/MZB payloads are obfuscated on disk** — the crux blocker. They must be run
  through `decode_mmb` → `decode_mmb2` and `decode_mzb` (keyed XOR + DWORD-swap + per-id
  `^0x55`, two 256-byte key tables) before the struct layout applies. Ported verbatim in
  `Vellichor.Dat/DatCrypt.cs`. Without this, every field is garbage (NaN positions).
- **MMB decode: 308/308 chunks clean** on Ronfaure → 647 meshes, 101,337 verts, 69,861
  tris; 36-byte vertex (pos/normal/BGRA/uv), triangle-strip when header `kind` ∈ {1,3}.
- **MZB decode: 12,060 instances, 215 readable ids** (`pl_moa_coi1_m`…), transforms =
  pos + euler-radians (X,Y,Z) + scale, 100-byte `SMZBBlock100`.
- **Full zone assembles in ~106 ms** (read 3 / decode 34 / build ~69) via `Render/
  ZoneLoader`; 11,865/12,060 instances resolve to a mesh (~195 unresolved ids likely live
  in a shared/character DAT — M1 follow-up).
- **Still to verify visually (M0 close-out):** Y-flip (FFXI Y-down → Godot Y-up), rotation
  euler order (XYZ hypothesis), winding/culling. These can only be confirmed on-screen.

---

## 7. Character / NPC / creature models (0x29 skeleton, 0x2a mesh, 0x2b anim)

Character/NPC/creature models use the **same DATHEAD container** but are **PLAINTEXT** — NO DatCrypt
(unlike zone MMB/MZB). A monster/NPC is **one self-contained DAT**; a player character is assembled
from a race skeleton + per-slot equipment DATs + a face DAT (harder, deferred).

Chunk types (verified): `0x01` name header · `0x07` init/pop scripts (ignore) · **`0x29` skeleton** ·
**`0x2a` skinned mesh** · **`0x2b` animation** (idl0/wlk0/run0/btl0) · `0x45` info · `0x20` texture.
Find model DATs with `tools/probe models <ROMdir>` (MMB-free, i.e. not zones). Good samples:
`ROM9/0/70.DAT` (tiny monster), `ROM9/2/8.DAT` (`npc_`, textured, `gold` skeleton + `hh_b` mesh + walk/run/idle).

### 0x29 skeleton — DECODED + rendering (Vellichor.Dat/ModelDecoder.cs)
Array of **30-byte BONE records** after a small header (**hdr=4** on the samples; auto-detected by
max unit-quaternion count). Per record: `parent u8 @0`, `flags u8 @1`, `quat(x,y,z,w) 4×f32 @2`,
`trans(x,y,z) 3×f32 @18`. LOCAL bind pose → compose down `parent`. `tools/probe skel <file>` reports
it; `VELLICHOR_MODEL=<file>` renders the joints. Verified: `ROM9/2/8` `gold` → a coherent quadruped
skeleton (head/spine/4 legs/tail). ~a few records per skeleton are garbage — skip non-unit-quat /
|trans|>20 bones (ModelViewer.BuildSkeleton does this). `clea` (ROM9/0/70) is degenerate with this
layout (2/155) — a special small model; revisit.

### 0x2a skinned mesh — HEADER CONFIRMED, vertex packing = REMAINING RE
`DAT2AHeader` (0x40 bytes) is a **directory of 6 contiguous sections** (each `offset u32, size u16`),
verified: each section's offset == previous end. Fields: `ver u8@0, type u16@2 (0=normal,1=cloth),
flip u16@4, Poly(off@6,sz@0xA), BoneTbl(@0xC,@0x10), Weight(@0x12,@0x16), Bone(@0x18,@0x1C),
Vertex(@0x1E,@0x22), PolyLoad(@0x24,@0x28)`. Example (hh_b): Poly@32 sz32876, BoneTbl sz113,
Weight sz2, Bone sz2976, Vertex sz36672.
⚠️ The section BODIES are NOT plain float arrays — Poly starts with a sub-header (u16s incl. the
Vertex size + counts), Bone shows repeating bone-index patterns (e.g. 0x3F), Vertex floats read as
garbage at the raw offset. Positions are likely **int16 fixed-point** with per-bone sub-runs, at
possibly-unaligned offsets. NEXT: dump each section on the small hh_d mesh, find the fixed-point
scale + the MODELVERTEX1(rigid 1-bone)/MODELVERTEX2(blended 2-bone) field layout + how Bone/Weight/
BoneTbl partition vertices per bone; feed pos/normal/uv/boneIdx/weight into the VERIFIED
`Render/SkinnedMeshBuilder` (Skeleton3D + skin) to render bind pose. Struct lineage: galkareeve
TDWAnalysis.h (BONE, DAT2AHeader, MODELVERTEX1/2, TEXLIST). Player-char assembly + which-file-id
tables via AltanaViewer List/PC/<Race>/<Slot>.csv (equipment modelid = row index → ROM path).

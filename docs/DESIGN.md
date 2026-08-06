# Vellichor — Design & Plan

A modern C#/Godot client for a legacy 2002-era MMO, connecting to a self-hosted
**LSB** private server. This document is the durable record of *why this is feasible*,
*what's already done*, and *the honest path forward*. Franchise-neutral by intent — the
game-specific linkage lives only in code/config, never in repo metadata.

## Vision & scope

**Goal:** a playable client for our own server, with the load pipeline the retail client
never had — threaded/streaming asset loads, a decode-once parsed-asset cache, and
speculative neighbor-zone preloading, so **zoning is near-instant**.

**Non-goal: retail parity.** Full parity (every effect, cutscene, UI corner, and zone
quirk) is a perpetual tail and is what kills these projects. Vellichor targets "playable
on the zones and jobs we actually run." Polish beyond that is optional, never a deadline.

## Why this is feasible (the foundation already exists)

The two hardest, highest-risk subsystems are already solved in sibling projects:

| Layer | Status | Source |
|-------|--------|--------|
| Wire protocol (crypto, compression, lobby+map session, packet parse, WorldState) | **Done, battle-tested** | headless client (`C#/Personal/XiHeadless`) |
| Event bytecode | **Extracted + disassembled (99.4% clean over 207k programs)** | `Lua/Personal/UpdateExtractor/xidat` |
| Data archives (names / dialog / items / mobs / quests / missions) | **Decoded to YAML/XML** | `UpdateExtractor` (POLUtils lineage) |
| Raw graphics archives (mesh / model / animation / texture) | **Present & readable**, not yet decoded | retail install (see Corpus) |
| Graphics *decoders* | **To write** — but formats are externally documented | Noesis / POLUtils / community viewers |

The key insight: the on-disk archives split into a **data** half and a **graphics** half.
A *server* only needs the data half — which is exactly and only what the existing
toolchain decodes. A *client* additionally needs the graphics half, which is 0% done but
**externally documented**, so the remaining decoders are a port-and-validate against
known references, not novel reverse engineering. The one axis where these projects
normally hit unknown-unknowns — the network protocol — is fully in hand.

## Architecture

`WorldState` (from the shared protocol stack) is the authoritative model. The renderer is
a **view** over it; input drives the same action/movement packets the headless client
already builds.

- **Dat/** — the archive index (FTABLE/VTABLE: file-id → archive + path) and binary
  format decoders (zone mesh, skeletal model, animation, texture/DXT).
- **Import/** — decoded structures → Godot resources (`ArrayMesh`, `Skeleton3D`,
  `Animation`, `ImageTexture`). This is where the threaded/streaming loader lives.
- **Render/** — scene, camera, entity presentation; subscribes to `WorldState`.
- **Net/** — bridge to the shared protocol library; owns the live session + WorldState.

## Integration decision (protocol stack sharing)

The headless client is a net9.0 **exe**; Vellichor is a net8.0 Godot assembly. Do **not**
fork the protocol code (violates the single-source-of-truth rule both projects live by).
Instead: **extract `Net/` + `Game/` from the headless client into a shared class library**
(`XiProtocol`) targeting net8.0 (or netstandard2.1 to serve both). Both the headless
client and Vellichor reference it. Wire it into `Vellichor.csproj` with a `ProjectReference`
plus the Wanehollow glob-exclusion idiom (Godot.NET.Sdk globs `**/*.cs` and would
otherwise compile the sibling's sources into this assembly). Until that extraction lands,
Vellichor develops the DAT/render layers standalone — they need no server.

## Milestones & honest timeline

Assumes agent-heavy, Jason directing, evenings/weekends (≈40% faster near-full-time).
The real bottleneck is the visual-correctness verify loop (human eyeballs), not code
volume — so M2/M4 dominate the schedule.

| # | Done means | Focused effort | Risk |
|---|-----------|----------------|------|
| **M0** | Login-less: mount archives, decode one zone mesh+textures, render + fly camera; **zone once and time it** | 4–6 wk | Med (mesh decoder is net-new) |
| **M1** | All zone geometry/objects render; entities placed from WorldState (placeholder models) | 3–6 wk | Low |
| **M2** | Skeletal models (race + equipment layers) + animation playback/blending | 6–12 wk | **High (greenfield)** |
| **M3** | Combat presentation: cast/WS/ability anims, damage numbers, targeting, HP bars | 4–8 wk | Med |
| **M4** | Event VM *executor* — play cutscenes/NPC menus/dialog (camera + actor motion) | 6–10 wk | Med-High |
| **M5** | Modern Godot UI: chat, inventory, equip, map, macros, config | 6–12 wk | Low (broad) |
| **M6** | Long tail: particles/effects, weather/shaders, audio, streaming-loader tuning | perpetual | open |

- **Rendered-zoning spike (M0): ~1–1.5 months.** Directly proves the zoning thesis.
- **Genuinely playable (through M5): ~1 year part-time.**
- **Parity (M6): perpetual — do not chase it.**

## Addon & mod compatibility (first-class requirement)

Vellichor must run **both** Windower 4 and Ashita v4 **Lua addons** — the addon Jason
relies on are split across the two ecosystems (some only ship for one), so both are in
scope by design, not as an afterthought. This shapes M1+ surfaces: `WorldState`, the
packet stream, input, and the render layer must expose clean, addon-facing APIs.

**Architecture — one host, two facades (don't build two stacks):** Windower and Ashita
addons need the same underlying services (game state, packet stream, input, on-screen
text/primitives, resources, config, event callbacks). So:

- **`AddonHost`** — a Lua runtime + those shared services, built once over the existing
  `WorldState`/packet layer.
- **Two thin API facades** on top: `windower.*` and the Ashita ADK object model. Each is a
  translation layer, not a reimplementation of the service beneath it.

**The hard limit (honest):** compiled C++ `.dll` plugins (both frameworks) **cannot be
loaded** — they hook the retail process's memory/D3D8, which doesn't exist here. Their
*capabilities* are reimplemented as native host services instead:

- **XIPivot (DAT/texture/model overlays)** → already native in `DatArchive` (overlay roots,
  checked before base, XIPivot layout). ✅ done.
- Core packet/resource/text-drawing plugins → native `AddonHost` services both facades expose.
- A Lua addon that merely *depends on* such a plugin works once that plugin's service exists.
- Need Jason's actual addon/plugin list to prioritize which plugin capabilities to reimplement.

**Milestone (post render-core, needs stable state/render/input surfaces):**

| # | Done means | Risk |
|---|-----------|------|
| **M-Addon** | `AddonHost` + Lua runtime; `windower.*` and Ashita facades; the target addon set below running | High (broad API surface, two facades) |

### Target addon set (Jason's actual usage — defines "done")

Windower 4. **GearSwap is the linchpin/acid test** — it exercises nearly the whole host
surface; if it runs, most others do.

- **Linchpin:** gearswap.
- **Drawing/UI:** timers, barfiller, enemybar, tparty, xivbar, xivparty, spellbook,
  giltracker, gametime, skillchains.
- **Automation:** autora, ohshi, silence, autoenterkey, boxdestroyer, trusts, lightluggage, roe.
- **Infra (≈ the host itself):** luacore (Lua runtime), config (settings), binder (keybinds),
  ffxidb (resource lookup).
- **Render-side:** dressup (appearance/model override — needs a render-layer hook).
- **Native/moot:** xipivot (✅ native in `DatArchive`), delaymenot (no artificial delays here).
- Also wants a few **Ashita** addons where an equivalent isn't on Windower — hence both facades.

### Derived `AddonHost` service surface (union of the above)

1. **Lua runtime** — real Lua 5.1 / LuaJIT-compatible (KeraLua/NLua or LuaJIT), NOT pure-C#
   MoonSharp — GearSwap's metatable/closure-heavy code + Windower `libs/` need 5.1 fidelity.
2. **State** — player, target, party/alliance, entities + hp%, inventory, equipment, buffs,
   known spells/abilities, gil, zone, Vana'diel time (`VanaTime` exists in XiHeadless).
3. **Resources** — spells / abilities / items / zones (overlaps decoded data DATs + Game/Generated).
4. **Packets** — incoming/outgoing events by id + **injection**.
5. **Events** — load/unload, prerender, incoming|outgoing chunk, incoming text, status/zone/job
   change, action; plus GearSwap's precast/midcast/aftercast/pet_midcast (derived from actions).
6. **Rendering** — text objects + primitives/images on a Godot 2D overlay layer.
7. **Input** — keybinds + command send.
8. **Config** — per-addon settings persistence.
9. **Equipment control** — equip/change gear via packets (XiHeadless `IGear`).
10. **Appearance override** — render-layer hook for dressup.
11. **Macro palettes** — client macro system (macrochanger needs job/zone-change events + macros).

## Corpus (local, not committed)

Full retail install is readable from macOS via the Parallels SMB mount:

```
/Volumes/[C] Windows 11.hidden/Program Files (x86)/PlayOnline/SquareEnix/FINAL FANTASY XI
  FTABLE.DAT, VTABLE.DAT          # master file index (parse these first)
  ROM/ .. ROM9/                   # numbered DAT archives (mesh/model/anim/texture + data)
  sound/ .. sound9/               # audio (M6)
```

**Benchmark caveat:** develop decoders against the SMB mount if convenient, but **run the
zoning benchmark against a local SSD copy** — SMB latency on thousands of small
FTABLE-indirected reads would reintroduce an I/O bottleneck and invalidate the very
number M0 exists to measure.

## Immediate next tasks (M0 critical path)

1. **FTABLE/VTABLE reader** (`Dat/`) — parse the index; resolve file-id → archive path;
   expose a byte-range reader. Validate counts against a known reference tool.
2. **Zone mesh decoder** (`Dat/`) — decode one known zone's geometry + texture refs
   (port from the documented format; pick a small starting zone).
3. **Mesh importer** (`Import/`) — build a Godot `ArrayMesh` + materials from (2).
4. **Render + camera** (`Render/`) — drop the zone in a scene, add a fly camera; run.
5. **Time it** — local-copy zone load timing vs. the retail client, as the M0 payoff.

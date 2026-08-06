# Vellichor

A modern game **client** and asset pipeline, built in C# on **Godot 4.7**, targeting a
self-hosted **LSB** (LandSandBoat) private server for a legacy 2002-era MMO.

The motivation is performance: the original retail client is a single-threaded DirectX 8
application whose zone transitions stall on a serial read → parse → GPU-upload pipeline.
Vellichor owns that pipeline end-to-end, so asset loading can be threaded, cached, and
pre-warmed — the point of the project is **near-instant zoning**.

## What this repo does and does not contain

- **Does:** decoders and importers for the legacy client's on-disk archives, a Godot
  render/view layer, and a bridge to a from-scratch wire-protocol stack.
- **Does not:** ship any copyrighted assets or original game code. It reads the archives
  from a retail installation **you already own**, on your machine, at runtime — the same
  posture as the community addon frameworks and the LSB server itself.

## Requirements

- Godot 4.7.x (mono / .NET build)
- .NET 8 SDK
- A local retail installation (the DAT archives) — path is configured, not bundled
- An LSB server to connect to (for the networked milestones)

## Build & run

```sh
dotnet build Vellichor.csproj      # restore + compile the C# assembly
# then open the project in Godot 4.7 (mono) and run, or:
# "/Applications/Godot_mono.app/Contents/MacOS/Godot" --path . 
```

## Layout

```
Dat/      archive index (FTABLE/VTABLE) + binary format decoders
Import/   decoded DAT -> Godot resources (mesh, model, animation, texture)
Render/   the view layer: scene, camera, entity presentation
Net/      bridge to the shared protocol stack; binds live WorldState
docs/     DESIGN.md — architecture, milestones, and the honest timeline
```

See **[docs/DESIGN.md](docs/DESIGN.md)** for the full plan.

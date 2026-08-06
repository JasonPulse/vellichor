# Dat/ — archive index + binary format decoders

Reads the retail on-disk archives directly (path configured, never bundled).

- **Archive index** — parse `FTABLE.DAT` / `VTABLE.DAT`: file-id → (archive, path),
  plus a byte-range reader. This is the first thing built; everything downstream
  resolves assets through it.
- **Format decoders** — zone mesh, skeletal model, animation, texture (DXT). Ported and
  validated against community references (Noesis / POLUtils / DAT viewers), not guessed.

Pure C#, no Godot dependency — so decoders can be unit-tested headless against the corpus.
Godot-facing conversion lives in `../Import/`.

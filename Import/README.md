# Import/ — decoded DAT structures → Godot resources

Turns the engine-agnostic structs from `../Dat/` into Godot types: `ArrayMesh`,
`Skeleton3D` + `Animation`, `ImageTexture`/compressed textures, materials.

This is where the **performance win** lives: threaded/streaming loads, a decode-once
parsed-asset cache, and speculative neighbor-zone preloading — the pipeline the retail
client never had. Keep decode (CPU, `Dat/`) separable from resource creation (main-thread
GPU upload) so the former can run off-thread.

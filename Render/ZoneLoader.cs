using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Godot;
using Vellichor.Dat;

namespace Vellichor.Render;

/// <summary>
/// Loads a zone DAT into a Godot node tree: decode every MMB once into a shared ArrayMesh,
/// then instance each mesh at every MZB placement (position / rotation-radians / scale).
///
/// Coordinate note (to verify visually): FFXI is right-handed and widely documented as
/// Y-DOWN; Godot is Y-up. We flip Y once at the zone root (scale (1,-1,1)) and render
/// double-sided so the winding flip doesn't hide faces. Rotation euler order is the
/// working hypothesis (XYZ) from the source — expect to tune this against the render.
/// </summary>
public static class ZoneLoader
{
    public static Node3D Load(string datPath, out string report, out Aabb worldBounds)
    {
        var sw = Stopwatch.StartNew();
        var root = new Node3D { Name = "Zone" };
        // The Y-flip is baked into the meshes (ZoneRenderer), so the root is identity — no
        // negative-scale mirror in the scene graph.

        var data = System.IO.File.ReadAllBytes(datPath);
        double readMs = sw.Elapsed.TotalMilliseconds;
        var chunks = ChunkReader.Walk(data);
        // The file's first chunk is a header tag naming the zone (e.g. "f_ro" = field zone).
        string zoneCode = chunks.Count > 0 ? chunks[0].Name : "?";
        string zoneName = ZoneName(zoneCode);

        // Decode + build a shared ArrayMesh list per MMB id. Each mesh keeps its texture id
        // so the right material can be bound per surface.
        // Two variants per mesh: normal, and a pre-negated-normal one for mirrored instances.
        var meshesById = new Dictionary<string, List<(ArrayMesh normal, ArrayMesh flipped, string? tex)>>();
        int models = 0;
        foreach (var c in chunks)
        {
            if (c.Type != 0x2e) continue;
            var payload = data.AsSpan(c.PayloadOffset, c.PayloadLength).ToArray();
            var mmb = MmbDecoder.Decode(payload);
            if (mmb.Meshes.Count == 0) continue;
            meshesById[mmb.MmbId] = mmb.Meshes
                .Select(md => (ZoneRenderer.BuildArrayMesh(md), ZoneRenderer.BuildArrayMesh(md, true), md.TextureId)).ToList();
            models += mmb.Meshes.Count;
        }
        double decodeMs = sw.Elapsed.TotalMilliseconds - readMs;

        bool unlit = System.Environment.GetEnvironmentVariable("VELLICHOR_UNLIT") != null;

        // Decode IMG (0x20) textures and build one material per texture id. Also pick the
        // biggest bright texture as a grass proxy for the collision-ground fill.
        var texMat = new Dictionary<string, StandardMaterial3D>();
        Texture2D? grassTex = null;
        long grassScore = 0;
        foreach (var c in chunks)
        {
            if (c.Type != 0x20) continue;
            ImgTexture? img;
            try { img = ImgDecoder.Decode(data.AsSpan(c.PayloadOffset, c.PayloadLength).ToArray()); }
            catch { continue; } // a malformed texture must never crash the whole zone load
            if (img is null || texMat.ContainsKey(img.Id)) continue;
            var gimg = Image.CreateFromData(img.Width, img.Height, false, Image.Format.Rgba8, img.Rgba);
            var itex = ImageTexture.CreateFromImage(gimg);
            // grass proxy: prefer the largest green-dominant, mid-bright texture.
            long sr = 0, sg = 0, sb = 0; int n = img.Width * img.Height;
            for (int i = 0; i < n; i++) { sr += img.Rgba[i * 4]; sg += img.Rgba[i * 4 + 1]; sb += img.Rgba[i * 4 + 2]; }
            double avgLum = (sr + sg + sb) / (double)(n * 3);
            if (n > 0 && avgLum is > 50 and < 210 && sg > sr * 1.05 && sg > sb * 1.05)
            {
                long score = (long)img.Width * img.Height;
                if (score > grassScore) { grassScore = score; grassTex = itex; }
            }
            texMat[img.Id] = new StandardMaterial3D
            {
                AlbedoTexture = itex,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                ShadingMode = unlit ? BaseMaterial3D.ShadingModeEnum.Unshaded : BaseMaterial3D.ShadingModeEnum.PerPixel,
                // NOTE: alpha cutout deferred — FFXI's 0..128 alpha convention (and DXT3's
                // 4-bit alpha rescale) needs its own pass; scissor at 0.5 erased most texels.
            };
        }

        // Fallback material for meshes with no matching texture.
        var mat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.72f, 0.73f, 0.70f),
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        if (unlit) mat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        StandardMaterial3D MatFor(string? tex) => tex != null && texMat.TryGetValue(tex, out var m) ? m : mat;

        int placed = 0, missing = 0;
        Vector3 min = new(float.MaxValue, float.MaxValue, float.MaxValue);
        Vector3 max = new(float.MinValue, float.MinValue, float.MinValue);
        var mzb = chunks.FirstOrDefault(c => c.Type == 0x1c);
        if (mzb.LengthBytes > 0)
        {
            var payload = data.AsSpan(mzb.PayloadOffset, mzb.PayloadLength).ToArray();
            foreach (var inst in MzbDecoder.Decode(payload))
            {
                if (!meshesById.TryGetValue(inst.Id, out var meshList)) { missing++; continue; }
                // REAL scale (keeps negative-scale tiles' true shape/height). Mirrored
                // (negative-determinant) instances use the pre-negated-normal mesh so Godot's
                // reflection leaves normals pointing outward — correct shape AND correct light.
                var basis = Basis.FromEuler(new Vector3(-inst.RotX, inst.RotY, -inst.RotZ), EulerOrder.Xyz)
                    .Scaled(new Vector3(inst.ScaleX, inst.ScaleY, inst.ScaleZ));
                var node = new Node3D { Transform = new Transform3D(basis, new Vector3(inst.PosX, -inst.PosY, inst.PosZ)) };
                bool mirrored = inst.ScaleX * inst.ScaleY * inst.ScaleZ < 0;
                foreach (var (normal, flipped, tex) in meshList)
                    node.AddChild(new MeshInstance3D { Mesh = mirrored ? flipped : normal, MaterialOverride = MatFor(tex) });
                root.AddChild(node);
                placed++;
                // World-space position (root flips Y): track for camera placement.
                var w = new Vector3(inst.PosX, -inst.PosY, inst.PosZ);
                min = new Vector3(Mathf.Min(min.X, w.X), Mathf.Min(min.Y, w.Y), Mathf.Min(min.Z, w.Z));
                max = new Vector3(Mathf.Max(max.X, w.X), Mathf.Max(max.Y, w.Y), Mathf.Max(max.Z, w.Z));
            }
        }
        // Collision-mesh ground fill: the continuous walkable surface, rendered untextured
        // just below the visual tiles so textures win where they exist and this fills the
        // gaps/paths (no more see-through holes). Already world-space + Y-flipped in the decoder.
        int collVerts = 0;
        if (mzb.LengthBytes > 0 && System.Environment.GetEnvironmentVariable("VELLICHOR_FILL") != null)
        {
            var coll = MzbCollisionDecoder.Decode(data.AsSpan(mzb.PayloadOffset, mzb.PayloadLength).ToArray());
            if (coll is { VertexCount: > 0 })
            {
                collVerts = coll.VertexCount;
                var groundMat = new StandardMaterial3D
                {
                    CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                    ShadingMode = unlit ? BaseMaterial3D.ShadingModeEnum.Unshaded : BaseMaterial3D.ShadingModeEnum.PerPixel,
                };
                if (grassTex is not null)
                {
                    // Triplanar grass so the fill reads as ground following the curved
                    // walkable surface, not a flat coloured patch that pokes up.
                    groundMat.AlbedoTexture = grassTex;
                    groundMat.Uv1Triplanar = true;
                    groundMat.Uv1Scale = new Vector3(0.03f, 0.03f, 0.03f);
                }
                else groundMat.AlbedoColor = new Color(0.42f, 0.44f, 0.34f);

                root.AddChild(new MeshInstance3D
                {
                    Mesh = ZoneRenderer.BuildRawMesh(coll),
                    MaterialOverride = groundMat,
                    Position = new Vector3(0, -0.3f, 0),            // just under the visual tiles
                });
            }
        }

        sw.Stop();

        worldBounds = placed > 0 ? new Aabb(min, max - min) : new Aabb(Vector3.Zero, Vector3.One);

        report = $"{zoneName} [{zoneCode}] loaded in {sw.Elapsed.TotalMilliseconds:0} ms " +
                 $"(read {readMs:0}, decode {decodeMs:0}) — {meshesById.Count} MMB ids / {models} meshes, " +
                 $"{texMat.Count} textures, {placed} instances placed, {missing} unresolved, " +
                 $"{collVerts:N0} collision-ground verts.";
        return root;
    }

    /// <summary>
    /// Friendly zone name from its 4-char code. Provisional stub — replace with the full
    /// zone-id↔file↔name table when zone selection lands. Falls back to the raw code.
    /// </summary>
    private static string ZoneName(string code) => code switch
    {
        "f_ro" => "Ronfaure",
        _ => code,
    };
}

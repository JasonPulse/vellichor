using System;
using System.Collections.Generic;
using Godot;
using Vellichor.Dat;

namespace Vellichor.Render;

/// <summary>
/// Renders a standalone model DAT (MMB geometry + IMG textures, no MZB placement) into a Node3D —
/// e.g. a creature/NPC/object model. Same coordinate convention as the zone + entities: Godot =
/// (-X, YSign*Y, Z). Used by the VELLICHOR_MODEL viewer and, later, to render live entity models.
/// </summary>
public static class ModelViewer
{
    public static Node3D Build(byte[] dat, out Aabb bounds, out string report)
    {
        var chunks = ChunkReader.Walk(dat);

        // Textures (IMG 0x20) by id -> material.
        var texMat = new Dictionary<string, StandardMaterial3D>();
        foreach (var c in chunks)
        {
            if (c.Type != 0x20) continue;
            ImgTexture? img;
            try { img = ImgDecoder.Decode(dat.AsSpan(c.PayloadOffset, c.PayloadLength).ToArray()); }
            catch { continue; }
            if (img is null || texMat.ContainsKey(img.Id)) continue;
            var gimg = Image.CreateFromData(img.Width, img.Height, false, Image.Format.Rgba8, img.Rgba);
            gimg.GenerateMipmaps();
            texMat[img.Id] = new StandardMaterial3D
            {
                AlbedoTexture = ImageTexture.CreateFromImage(gimg),
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
            };
        }
        var fallback = new StandardMaterial3D { AlbedoColor = new Color(0.75f, 0.75f, 0.78f), CullMode = BaseMaterial3D.CullModeEnum.Disabled };

        // Geometry (MMB 0x2e). Accumulate per texture; flip to Godot space (-X, YSign*Y, Z).
        var acc = new Dictionary<string, (List<float> pos, List<float> nrm, List<float> uv, List<int> idx)>();
        (List<float>, List<float>, List<float>, List<int>) AccFor(string key)
        {
            if (!acc.TryGetValue(key, out var a)) { a = (new(), new(), new(), new()); acc[key] = a; }
            return a;
        }
        var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        int mmbCount = 0, meshCount = 0;
        float ys = EntityRenderer.YSign;
        foreach (var c in chunks)
        {
            if (c.Type != 0x2e) continue;
            MmbModel mmb;
            try { mmb = MmbDecoder.Decode(dat.AsSpan(c.PayloadOffset, c.PayloadLength).ToArray()); }
            catch { continue; }
            if (!mmb.Ok) continue;
            mmbCount++;
            foreach (var md in mmb.Meshes)
            {
                meshCount++;
                string key = md.TextureId ?? "";
                var (pos, nrm, uv, idx) = AccFor(key);
                int baseIdx = pos.Count / 3;
                for (int v = 0; v < md.VertexCount; v++)
                {
                    float x = -md.Positions[v * 3], y = ys * md.Positions[v * 3 + 1], z = md.Positions[v * 3 + 2];
                    pos.Add(x); pos.Add(y); pos.Add(z);
                    if (md.Normals is { Length: > 0 }) { nrm.Add(-md.Normals[v * 3]); nrm.Add(ys * md.Normals[v * 3 + 1]); nrm.Add(md.Normals[v * 3 + 2]); }
                    uv.Add(md.Uvs is { Length: > 0 } ? md.Uvs[v * 2] : 0);
                    uv.Add(md.Uvs is { Length: > 0 } ? md.Uvs[v * 2 + 1] : 0);
                    if (x < min.X) min.X = x; if (y < min.Y) min.Y = y; if (z < min.Z) min.Z = z;
                    if (x > max.X) max.X = x; if (y > max.Y) max.Y = y; if (z > max.Z) max.Z = z;
                }
                var s = md.Indices;
                for (int t = 0; t + 2 < s.Length; t += 3) { idx.Add(baseIdx + s[t]); idx.Add(baseIdx + s[t + 1]); idx.Add(baseIdx + s[t + 2]); }
            }
        }

        var root = new Node3D();
        foreach (var (tex, a) in acc)
        {
            if (a.pos.Count == 0) continue;
            var md = new MeshData { Positions = a.pos.ToArray(), Uvs = a.uv.ToArray(), Indices = a.idx.ToArray(),
                                    Normals = a.nrm.Count == a.pos.Count ? a.nrm.ToArray() : null };
            var mat = texMat.TryGetValue(tex, out var m) ? m : fallback;
            root.AddChild(new MeshInstance3D { Mesh = ZoneRenderer.BuildRawMesh(md), MaterialOverride = mat });
        }

        bounds = meshCount > 0 ? new Aabb(min, max - min) : new Aabb(Vector3.Zero, Vector3.One);
        report = $"{mmbCount} MMB / {meshCount} meshes, {texMat.Count} textures, bounds {bounds.Size}";
        return root;
    }

    /// Renders a character/NPC SKINNED MODEL (0x29 skeleton + 0x2a meshes + 0x20 textures) in bind
    /// pose. Meshes come from ModelDecoder in FFXI model space; flip to Godot (-X, YSign*Y, Z).
    /// Returns null if there's no 0x2a mesh.
    public static Node3D? BuildCharacter(byte[] dat, out Aabb bounds, out string report)
    {
        bounds = new Aabb(Vector3.Zero, Vector3.One); report = "";
        var meshes = ModelDecoder.DecodeCharacterMeshes(dat);
        if (meshes.Count == 0) return null;

        // Textures (0x20) by id.
        var chunks = ChunkReader.Walk(dat);
        var texMat = new Dictionary<string, StandardMaterial3D>();
        foreach (var c in chunks)
        {
            if (c.Type != 0x20) continue;
            ImgTexture? img;
            try { img = ImgDecoder.Decode(dat.AsSpan(c.PayloadOffset, c.PayloadLength).ToArray()); } catch { continue; }
            if (img is null || texMat.ContainsKey(img.Id)) continue;
            var gi = Image.CreateFromData(img.Width, img.Height, false, Image.Format.Rgba8, img.Rgba);
            gi.GenerateMipmaps();
            texMat[img.Id] = new StandardMaterial3D
            {
                AlbedoTexture = ImageTexture.CreateFromImage(gi),
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
            };
        }
        var fallback = new StandardMaterial3D { AlbedoColor = new Color(0.8f, 0.75f, 0.7f), CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        StandardMaterial3D MatFor(string? tex)
        {
            if (tex is not null)
                foreach (var (id, m) in texMat)
                    if (tex.Contains(id) || id.StartsWith(tex.Length >= 4 ? tex[^Math.Min(4, tex.Length)..] : tex)) return m;
            return fallback;
        }

        var root = new Node3D();
        var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        float ys = EntityRenderer.YSign;
        foreach (var md in meshes)
        {
            var pos = new float[md.Positions.Length];
            var nrm = md.Normals is { Length: > 0 } ? new float[md.Normals.Length] : null;
            for (int i = 0; i < md.VertexCount; i++)
            {
                float x = -md.Positions[i * 3], y = ys * md.Positions[i * 3 + 1], z = md.Positions[i * 3 + 2];
                pos[i * 3] = x; pos[i * 3 + 1] = y; pos[i * 3 + 2] = z;
                if (nrm is not null) { nrm[i * 3] = -md.Normals![i * 3]; nrm[i * 3 + 1] = ys * md.Normals[i * 3 + 1]; nrm[i * 3 + 2] = md.Normals[i * 3 + 2]; }
                if (x < min.X) min.X = x; if (y < min.Y) min.Y = y; if (z < min.Z) min.Z = z;
                if (x > max.X) max.X = x; if (y > max.Y) max.Y = y; if (z > max.Z) max.Z = z;
            }
            var fm = new MeshData { Positions = pos, Normals = nrm, Uvs = md.Uvs, Indices = md.Indices };
            root.AddChild(new MeshInstance3D { Mesh = ZoneRenderer.BuildRawMesh(fm), MaterialOverride = MatFor(md.TextureId) });
        }
        bounds = new Aabb(min, max - min);
        int tris = 0; foreach (var m in meshes) tris += m.Indices.Length / 3;
        report = $"character: {meshes.Count} mesh(es), {tris} tris, textures {texMat.Count}, bounds {bounds.Size}\n         diag: {Vellichor.Dat.ModelDecoder.MeshDiag}";
        return root;
    }

    /// Builds a GPU-skinned character (0x29 skeleton + 0x2a meshes) as a posable Skeleton3D under a root
    /// carrying the FFXI->Godot display flip. Thin wrapper over <see cref="CharacterModel"/> (the shared,
    /// cache-friendly core also used by the live EntityRenderer). Returns null if the DAT isn't a skinned
    /// character. Pose the returned <paramref name="skel"/> or attach an AnimationDriver to it.
    public static Node3D? BuildAnimatedCharacter(byte[] dat, out Skeleton3D? skel, out Aabb bounds, out string report)
    {
        skel = null; bounds = new Aabb(Vector3.Zero, Vector3.One); report = "";
        var model = CharacterModel.Decode(dat);
        if (model is null) return null;
        var (root, s, b) = model.BuildInstance();
        skel = s; bounds = b;
        report = $"animated: {model.BoneCount} bones, clips [{string.Join(",", model.ClipNames)}]";
        return root;
    }

    /// Converts a decoded 0x2b animation into AnimationDriver tracks (rotation-only for now — translation
    /// is left at bind rest to avoid collapsing bones whose stored constant translation may differ from
    /// the skeleton; can be enabled after visual review). Key times are frame/fps seconds.
    // ToTracks moved to AnimationDriver.ToTracks (its output type; shared with the DAT viewer).

    /// Renders a character/NPC skeleton (0x29) as joint spheres + parent bones — for verifying the
    /// skeletal decode. Composes world bind pose in FFXI space, then maps to Godot (-X, YSign*Y, Z).
    /// Garbage records (non-unit quat / absurd translation) are skipped so a few bad bones can't blow
    /// up the pose. Returns null if there's no 0x29 chunk.
    public static Node3D? BuildSkeleton(byte[] dat, out Aabb bounds, out string report)
    {
        bounds = new Aabb(Vector3.Zero, Vector3.One); report = "";
        var chunks = ChunkReader.Walk(dat);
        DatChunk sk = default; bool found = false;
        foreach (var c in chunks) if (c.Type == 0x29) { sk = c; found = true; break; }
        if (!found) return null;

        var skel = ModelDecoder.DecodeSkeleton(dat.AsSpan(sk.PayloadOffset, sk.PayloadLength).ToArray());
        int n = skel.Bones.Length;
        var wq = new System.Numerics.Quaternion[n];
        var wpF = new System.Numerics.Vector3[n]; // world pos in FFXI space
        var ok = new bool[n];
        for (int i = 0; i < n; i++)
        {
            var b = skel.Bones[i];
            var lq = new System.Numerics.Quaternion(b.Qx, b.Qy, b.Qz, b.Qw);
            float qm = lq.Length();
            bool good = qm is > 0.9f and < 1.1f && MathF.Abs(b.Tx) < 20 && MathF.Abs(b.Ty) < 20 && MathF.Abs(b.Tz) < 20;
            var lt = new System.Numerics.Vector3(b.Tx, b.Ty, b.Tz);
            if (good) lq = System.Numerics.Quaternion.Normalize(lq);
            if (b.Parent >= 0 && b.Parent < i && ok[b.Parent])
            {
                wq[i] = wq[b.Parent] * lq;
                wpF[i] = wpF[b.Parent] + System.Numerics.Vector3.Transform(lt, wq[b.Parent]);
            }
            else { wq[i] = lq; wpF[i] = lt; }
            ok[i] = good;
        }

        var root = new Node3D();
        var jointMat = new StandardMaterial3D { AlbedoColor = new Color(1f, 0.4f, 0.2f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
        var boneMat = new StandardMaterial3D { AlbedoColor = new Color(0.4f, 0.8f, 1f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
        float ys = EntityRenderer.YSign;
        Vector3 G(System.Numerics.Vector3 p) => new(-p.X, ys * p.Y, p.Z);
        var gmin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var gmax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        int shown = 0;
        for (int i = 0; i < n; i++)
        {
            if (!ok[i]) continue;
            var gp = G(wpF[i]);
            root.AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.03f, Height = 0.06f }, Position = gp, MaterialOverride = jointMat });
            gmin = gmin.Min(gp); gmax = gmax.Max(gp); shown++;
            int par = skel.Bones[i].Parent;
            if (par >= 0 && par < i && ok[par])
            {
                var gpp = G(wpF[par]);
                var mid = (gp + gpp) * 0.5f; float len = (gp - gpp).Length();
                if (len is > 0.001f and < 5f)
                {
                    var seg = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.012f, 0.012f, len) }, MaterialOverride = boneMat };
                    seg.Position = mid; seg.LookAtFromPosition(mid, gp, Vector3.Up);
                    root.AddChild(seg);
                }
            }
        }
        bounds = shown > 0 ? new Aabb(gmin, gmax - gmin) : new Aabb(Vector3.Zero, Vector3.One);
        report = $"skeleton '{sk.Name}': {skel.Diag}; shown {shown}/{n} joints, bounds {bounds.Size}";
        return root;
    }
}

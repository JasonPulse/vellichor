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

        // Decode + build a shared ArrayMesh list per MMB id (each id may hold several meshes).
        var meshesById = new Dictionary<string, List<ArrayMesh>>();
        int models = 0;
        foreach (var c in chunks)
        {
            if (c.Type != 0x2e) continue;
            var payload = data.AsSpan(c.PayloadOffset, c.PayloadLength).ToArray();
            var mmb = MmbDecoder.Decode(payload);
            if (mmb.Meshes.Count == 0) continue;
            var list = mmb.Meshes.Select(ZoneRenderer.BuildArrayMesh).ToList();
            meshesById[mmb.MmbId] = list;
            models += list.Count;
        }
        double decodeMs = sw.Elapsed.TotalMilliseconds - readMs;

        var mat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.72f, 0.73f, 0.70f),
            CullMode = BaseMaterial3D.CullModeEnum.Disabled, // double-sided while winding is unverified
        };
        if (System.Environment.GetEnvironmentVariable("VELLICHOR_UNLIT") != null)
            mat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded; // diagnostic: geometry coverage

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
                // Meshes are baked to Y-up, so place with the conjugated (flipY · T · flipY)
                // transform: translation Y negated, rotation X/Z negated, scale unchanged.
                var basis = Basis.FromEuler(new Vector3(-inst.RotX, inst.RotY, -inst.RotZ), EulerOrder.Xyz)
                    .Scaled(new Vector3(inst.ScaleX, inst.ScaleY, inst.ScaleZ));
                var node = new Node3D { Transform = new Transform3D(basis, new Vector3(inst.PosX, -inst.PosY, inst.PosZ)) };
                foreach (var mesh in meshList) node.AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = mat });
                root.AddChild(node);
                placed++;
                // World-space position (root flips Y): track for camera placement.
                var w = new Vector3(inst.PosX, -inst.PosY, inst.PosZ);
                min = new Vector3(Mathf.Min(min.X, w.X), Mathf.Min(min.Y, w.Y), Mathf.Min(min.Z, w.Z));
                max = new Vector3(Mathf.Max(max.X, w.X), Mathf.Max(max.Y, w.Y), Mathf.Max(max.Z, w.Z));
            }
        }
        sw.Stop();

        worldBounds = placed > 0 ? new Aabb(min, max - min) : new Aabb(Vector3.Zero, Vector3.One);

        report = $"{zoneName} [{zoneCode}] loaded in {sw.Elapsed.TotalMilliseconds:0} ms " +
                 $"(read {readMs:0}, decode {decodeMs:0}) — {meshesById.Count} MMB ids / {models} meshes, " +
                 $"{placed} instances placed, {missing} unresolved.";
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

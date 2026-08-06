using Godot;
using Vellichor.Dat;

namespace Vellichor.Render;

/// <summary>Converts engine-agnostic <see cref="MeshData"/> into Godot nodes.</summary>
public static class ZoneRenderer
{
    /// <summary>Build a MeshInstance3D from decoded mesh data (fresh mesh; for one-offs).</summary>
    public static MeshInstance3D BuildMesh(MeshData m, Material? material = null)
        => new() { Mesh = BuildArrayMesh(m), MaterialOverride = material };

    /// <summary>Build a shareable ArrayMesh from decoded mesh data (cache + reuse across instances).</summary>
    public static ArrayMesh BuildArrayMesh(MeshData m)
    {
        // Bake the FFXI(Y-down) -> Godot(Y-up) flip into the mesh: negate Y on positions
        // and normals, and reverse triangle winding. This keeps the scene-graph free of any
        // negative-scale (mirror) transform, so Godot lights normal AND mirrored (negative
        // MZB scale) instances correctly. Instances are placed with a conjugated transform
        // (see ZoneLoader).
        var verts = new Vector3[m.VertexCount];
        for (int i = 0; i < verts.Length; i++)
            verts[i] = new Vector3(m.Positions[i * 3], -m.Positions[i * 3 + 1], m.Positions[i * 3 + 2]);

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        if (m.Normals is { Length: > 0 })
        {
            var normals = new Vector3[m.Normals.Length / 3];
            for (int i = 0; i < normals.Length; i++)
                normals[i] = new Vector3(m.Normals[i * 3], -m.Normals[i * 3 + 1], m.Normals[i * 3 + 2]);
            arrays[(int)Mesh.ArrayType.Normal] = normals;
        }
        if (m.Uvs is { Length: > 0 })
        {
            var uvs = new Vector2[m.Uvs.Length / 2];
            for (int i = 0; i < uvs.Length; i++)
                uvs[i] = new Vector2(m.Uvs[i * 2], m.Uvs[i * 2 + 1]);
            arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        }
        // Reverse winding to compensate for the Y mirror.
        var src = m.Indices;
        var idx = new int[src.Length];
        for (int t = 0; t + 2 < src.Length; t += 3) { idx[t] = src[t]; idx[t + 1] = src[t + 2]; idx[t + 2] = src[t + 1]; }
        arrays[(int)Mesh.ArrayType.Index] = idx;

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }
}

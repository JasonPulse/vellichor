using Godot;
using Vellichor.Dat;

namespace Vellichor.Render;

/// <summary>Converts engine-agnostic <see cref="MeshData"/> into Godot nodes.</summary>
public static class ZoneRenderer
{
    /// <summary>Build a MeshInstance3D from decoded mesh data (fresh mesh; for one-offs).</summary>
    public static MeshInstance3D BuildMesh(MeshData m, Material? material = null)
        => new() { Mesh = BuildArrayMesh(m), MaterialOverride = material };

    /// <summary>
    /// Build an ArrayMesh from mesh data that is ALREADY in Godot world space (no Y-flip,
    /// no winding reversal) — used for the collision-ground fill. Positions/indices only.
    /// </summary>
    public static ArrayMesh BuildRawMesh(MeshData m)
    {
        var verts = new Vector3[m.VertexCount];
        for (int i = 0; i < verts.Length; i++)
            verts[i] = new Vector3(m.Positions[i * 3], m.Positions[i * 3 + 1], m.Positions[i * 3 + 2]);
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        if (m.Normals is { Length: > 0 })
        {
            var normals = new Vector3[m.Normals.Length / 3];
            for (int i = 0; i < normals.Length; i++)
                normals[i] = new Vector3(m.Normals[i * 3], m.Normals[i * 3 + 1], m.Normals[i * 3 + 2]);
            arrays[(int)Mesh.ArrayType.Normal] = normals;
        }
        arrays[(int)Mesh.ArrayType.Index] = m.Indices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    /// <summary>
    /// Build a shareable ArrayMesh from decoded mesh data. Bakes the FFXI(Y-down)->Godot(Y-up)
    /// flip into the mesh (negate Y on positions + normals, reverse winding). When
    /// <paramref name="negateNormals"/> is set, all normals are additionally negated: this is
    /// the variant used for NEGATIVE-scale (mirrored) MZB instances, whose reflection makes
    /// Godot flip the mesh normals — pre-negating cancels that so they light outward.
    /// </summary>
    public static ArrayMesh BuildArrayMesh(MeshData m, bool negateNormals = false)
    {
        var verts = new Vector3[m.VertexCount];
        for (int i = 0; i < verts.Length; i++)
            verts[i] = new Vector3(m.Positions[i * 3], -m.Positions[i * 3 + 1], m.Positions[i * 3 + 2]);

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        if (m.Normals is { Length: > 0 })
        {
            float s = negateNormals ? -1f : 1f;
            var normals = new Vector3[m.Normals.Length / 3];
            for (int i = 0; i < normals.Length; i++)
                normals[i] = new Vector3(s * m.Normals[i * 3], s * -m.Normals[i * 3 + 1], s * m.Normals[i * 3 + 2]);
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

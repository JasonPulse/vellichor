using Godot;
using Vellichor.Dat;
using Vellichor.Render;

namespace Vellichor;

/// <summary>
/// M0 render harness. Builds a lit scene with a free-look camera and renders geometry.
/// Right now it shows a PLACEHOLDER mesh so the harness is verifiable before the MMB
/// decoder lands; once decoding works, <see cref="LoadZone"/> swaps in real Ronfaure
/// meshes (corpus/ROM5/0/11.DAT) with no other change to the scene.
/// </summary>
public partial class Main : Node3D
{
    private string? _shot;
    private int _frames;

    public override void _Ready()
    {
        // Debug: if VELLICHOR_SHOT is set, render a few frames, save a PNG, and quit.
        _shot = System.Environment.GetEnvironmentVariable("VELLICHOR_SHOT");

        // Ambient + sun so untextured meshes are visible from any angle.
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            // Diagnostic: bright magenta background under VELLICHOR_MAGENTA so real holes
            // (see-through to background) are unmistakable vs merely dark surfaces.
            BackgroundColor = System.Environment.GetEnvironmentVariable("VELLICHOR_MAGENTA") is null
                ? new Color(0.08f, 0.09f, 0.12f) : new Color(1f, 0f, 1f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.7f, 0.72f, 0.78f),
            AmbientLightEnergy = 1.4f,
        };
        AddChild(new WorldEnvironment { Environment = env });

        // Soft even lighting (no hard shadows) so untextured terrain reads cleanly — hard
        // self-shadowing was creating big dark patches that looked like holes.
        var sun = new DirectionalLight3D { RotationDegrees = new Vector3(-55, -40, 0), ShadowEnabled = false, LightEnergy = 0.7f };
        AddChild(sun);
        var fill = new DirectionalLight3D { RotationDegrees = new Vector3(-30, 140, 0), ShadowEnabled = false, LightEnergy = 0.4f };
        AddChild(fill);

        var cam = new FlyCamera { Speed = 60f };

        GD.Print("Vellichor M0 harness up. Look: ARROW KEYS (or right-drag). Move: WASD/QE. Wheel: speed.");

        // Load the zone. Fall back to the placeholder if the corpus isn't present.
        string zonePath = ProjectSettings.GlobalizePath("res://corpus/ROM5/0/11.DAT");
        if (System.IO.File.Exists(zonePath))
        {
            var zone = ZoneLoader.Load(zonePath, out string report, out Aabb b);
            AddChild(zone);
            GD.Print("Zone: " + report);

            var c = b.GetCenter();
            if (System.Environment.GetEnvironmentVariable("VELLICHOR_GROUND") != null)
            {
                // Shallow field-level view (like the user's screenshots).
                cam.Position = new Vector3(c.X - 150, b.Position.Y + b.Size.Y + 25, c.Z + 250);
                cam.RotationDegrees = new Vector3(-12, -25, 0);
            }
            else
            {
                // Spawn above the zone centre, looking down at it.
                cam.Position = new Vector3(c.X, b.Position.Y + b.Size.Y + 80, c.Z + 80);
                cam.RotationDegrees = new Vector3(-40, 0, 0);
            }
        }
        else
        {
            GD.Print($"corpus zone not found at {zonePath} — showing placeholder.");
            cam.Position = new Vector3(0, 3, 8);
            LoadPlaceholder();
        }

        AddChild(cam);
    }

    public override void _Process(double delta)
    {
        if (_shot is null) return;
        if (++_frames != 10) return;
        var img = GetViewport().GetTexture().GetImage();
        img.SavePng(_shot);
        GD.Print($"saved screenshot -> {_shot}");
        GetTree().Quit();
    }

    private void LoadZone(System.Collections.Generic.IEnumerable<MeshData> meshes)
    {
        var mat = new StandardMaterial3D { AlbedoColor = new Color(0.7f, 0.7f, 0.72f) };
        foreach (var m in meshes)
            AddChild(ZoneRenderer.BuildMesh(m, mat));
    }

    private void LoadPlaceholder()
    {
        // A unit cube as MeshData — exercises the exact ArrayMesh path real meshes will use.
        float[] p =
        {
            -1,-1,-1,  1,-1,-1,  1,1,-1,  -1,1,-1,
            -1,-1, 1,  1,-1, 1,  1,1, 1,  -1,1, 1,
        };
        int[] idx =
        {
            0,1,2, 0,2,3,  5,4,7, 5,7,6,  4,0,3, 4,3,7,
            1,5,6, 1,6,2,  3,2,6, 3,6,7,  4,5,1, 4,1,0,
        };
        LoadZone(new[] { new MeshData { Positions = p, Indices = idx } });
    }
}

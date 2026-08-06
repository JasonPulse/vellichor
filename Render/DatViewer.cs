using System;
using System.IO;
using System.Linq;
using Godot;
using Vellichor.Dat;

namespace Vellichor.Render;

/// <summary>
/// Visual DAT texture browser. Point it at a single .DAT or a folder of them (e.g. a mod
/// pack) via VELLICHOR_VIEW=&lt;path&gt;; it decodes every IMG texture and shows a scrollable grid
/// of thumbnails labelled with the texture's internal id, source file, and size — so you can
/// see what a mod contains and match its internal id to the original DAT. Also logs a
/// name→file line per texture to the console.
/// </summary>
public partial class DatViewer : Control
{
    private readonly string _path;
    public DatViewer(string path) => _path = path;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(new ColorRect { Color = new Color(0.11f, 0.11f, 0.13f), MouseFilter = MouseFilterEnum.Ignore }.Preset());

        var scroll = new ScrollContainer().Preset();
        AddChild(scroll);
        var grid = new GridContainer { Columns = 6, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(grid);

        var files = File.Exists(_path)
            ? new[] { _path }
            : Directory.Exists(_path)
                ? Directory.EnumerateFiles(_path, "*.DAT", SearchOption.AllDirectories).OrderBy(f => f).ToArray()
                : System.Array.Empty<string>();

        // Also export every texture to PNG named by internal id, for browsing in Finder.
        string outDir = System.Environment.GetEnvironmentVariable("VELLICHOR_VIEW_OUT")
            ?? Path.Combine(Path.GetTempPath(), "vellichor_textures");
        Directory.CreateDirectory(outDir);

        int shown = 0;
        const int cap = 600;
        foreach (var f in files)
        {
            if (shown >= cap) break;
            byte[] data;
            try { data = File.ReadAllBytes(f); } catch { continue; }
            foreach (var c in ChunkReader.Walk(data).Where(c => c.Type == 0x20))
            {
                if (shown >= cap) break;
                ImgTexture? t;
                try { t = ImgDecoder.Decode(data.AsSpan(c.PayloadOffset, c.PayloadLength).ToArray()); } catch { continue; }
                if (t is null) continue;

                var gimg = Image.CreateFromData(t.Width, t.Height, false, Image.Format.Rgba8, t.Rgba);
                string safe = string.Concat((t.Id + "_" + Path.GetFileNameWithoutExtension(f)).Select(ch => char.IsLetterOrDigit(ch) ? ch : '_'));
                gimg.SavePng(Path.Combine(outDir, safe + ".png"));
                var box = new VBoxContainer { CustomMinimumSize = new Vector2(150, 180) };
                box.AddChild(new TextureRect
                {
                    Texture = ImageTexture.CreateFromImage(gimg),
                    CustomMinimumSize = new Vector2(140, 140),
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                });
                string rel = Path.GetFileName(Path.GetDirectoryName(f)) + "/" + Path.GetFileName(f);
                box.AddChild(new Label
                {
                    Text = $"'{t.Id}'\n{rel}  {t.Width}x{t.Height}",
                    AutowrapMode = TextServer.AutowrapMode.WordSmart,
                    CustomMinimumSize = new Vector2(140, 0),
                });
                grid.AddChild(box);
                GD.Print($"tex '{t.Id}'  {t.Width}x{t.Height}  {f}");
                shown++;
            }
        }
        GD.Print($"DAT viewer: {shown} textures from {_path} — PNGs written to {outDir}");
    }
}

file static class ControlExt
{
    public static T Preset<T>(this T c) where T : Control { c.SetAnchorsPreset(Control.LayoutPreset.FullRect); return c; }
}

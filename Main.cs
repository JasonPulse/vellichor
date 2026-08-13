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
    private int _shotFrame = 10;

    // Live server bridge (VELLICHOR_ACCOUNT/PASSWORD): connects, renders live entities, then
    // does a timed graceful logout so the session is never left stale.
    private Vellichor.Net.EntityBridge? _bridge;
    private EntityRenderer? _entityRenderer;
    private EntityModelCache? _entityModels;
    private double _liveElapsed;
    private double _liveDuration = 20; // observe seconds before graceful logout (auto mode only)
    private bool _liveLoggingOut;
    private bool _autoMode; // headless VELLICHOR_AUTOLOGIN: timed observe + logout + quit
    private bool _quitting;          // window-close logout in progress
    private volatile bool _readyToQuit; // set by the background logout task when done

    // Zone rendering: which zone is currently built, and the node holding it (so a live zone
    // change can free + rebuild). -1 = nothing loaded yet.
    private FlyCamera? _cam;
    private Node3D? _zoneNode;
    private MeshInstance3D? _water;
    private int _loadedZone = -1;
    private string _corpusDir = "";
    private Vellichor.Dat.DatArchive? _dat; // FTABLE resolver for zone-id → DAT path
    private Aabb _zoneBounds;

    // Player control: the local character is driven by WorldState.X/Z (client-authoritative;
    // BuildPos broadcasts it). _localState lets movement be tested offline (no server).
    private XiHeadless.Game.WorldState? _localState;
    private Node3D? _playerBody; // kinematic: position set directly, ground via raycast (no physics sim)
    private MeshInstance3D? _playerCapsule;   // placeholder shown until the self model resolves
    private Node3D? _playerModel;             // the resolved self character model (from MyLook)
    private Skeleton3D? _playerSkel;
    private Render.AnimationDriver? _playerDriver;
    private Render.CharacterModel? _playerCharModel;
    private string? _playerClip;
    private bool _playerModelTried;
    private Label3D? _playerTag;
    private float _camYaw;
    private float _camPitch = 0.35f; // radians; positive = camera raised, looking down
    private float _camDist = 6.5f; // third-person default (was 14 — the character was a distant speck)
    private bool _orbiting;           // right-mouse held
    private float _moveSpeed = 8f;    // yalms/sec (Shift sprints)
    private bool _autoWalk;   // VELLICHOR_AUTOWALK: simulate holding W (headless movement test)
    private double _entLogAccum;
    private Vellichor.UI.GameHud? _hud;
    private Vellichor.UI.Menus.MenuManager? _menus;
    private Vellichor.UI.Menus.MenuContext? _menuCtx;

    private XiHeadless.Game.WorldState? ActiveState => _bridge?.State ?? _localState;

    public override void _Ready()
    {
        // Debug: if VELLICHOR_SHOT is set, render a few frames, save a PNG, and quit.
        _shot = System.Environment.GetEnvironmentVariable("VELLICHOR_SHOT");
        _autoWalk = System.Environment.GetEnvironmentVariable("VELLICHOR_AUTOWALK") != null;
        XiHeadless.Game.PacketParsers.DebugChat = true; // log every incoming 0x017 raw (chat-reception diagnostic)
        // Position-writing is OPT-IN (safety): observe-only by default so the live character is never
        // moved/stranded by the client. AUTOWALK implies movement (it's the offline test path).
        if (int.TryParse(System.Environment.GetEnvironmentVariable("VELLICHOR_SHOT_FRAME"), out var sf)) _shotFrame = sf;

        // DAT texture browser: VELLICHOR_VIEW=<dat-file-or-folder> shows a thumbnail grid
        // (for inspecting/identifying mod textures) instead of loading the zone.
        string? viewPath = System.Environment.GetEnvironmentVariable("VELLICHOR_VIEW");
        if (viewPath is not null)
        {
            var layer = new CanvasLayer();
            layer.AddChild(new Vellichor.Render.DatViewer(viewPath));
            AddChild(layer);
            return;
        }

        // Ambient + sky/background. Default = a daytime procedural sky (outdoor zones read as an open world
        // and everything picks up soft skylight). VELLICHOR_NOSKY or VELLICHOR_MAGENTA fall back to a flat
        // color background (the magenta hole-diagnostic).
        bool magenta = System.Environment.GetEnvironmentVariable("VELLICHOR_MAGENTA") is not null;
        bool sky = !magenta && System.Environment.GetEnvironmentVariable("VELLICHOR_NOSKY") is null;
        var env = new Godot.Environment { TonemapMode = Godot.Environment.ToneMapper.Filmic };
        if (sky)
        {
            env.BackgroundMode = Godot.Environment.BGMode.Sky;
            env.Sky = new Sky
            {
                SkyMaterial = new ProceduralSkyMaterial
                {
                    SkyTopColor = new Color(0.35f, 0.55f, 0.85f),
                    SkyHorizonColor = new Color(0.75f, 0.80f, 0.85f),
                    GroundHorizonColor = new Color(0.70f, 0.72f, 0.72f),
                    GroundBottomColor = new Color(0.45f, 0.45f, 0.45f),
                },
            };
            env.AmbientLightSource = Godot.Environment.AmbientSource.Sky;
            env.AmbientLightEnergy = 0.7f;
        }
        else
        {
            env.BackgroundMode = Godot.Environment.BGMode.Color;
            env.BackgroundColor = magenta ? new Color(1f, 0f, 1f) : new Color(0.08f, 0.09f, 0.12f);
            env.AmbientLightSource = Godot.Environment.AmbientSource.Color;
            env.AmbientLightColor = new Color(0.7f, 0.72f, 0.78f);
            env.AmbientLightEnergy = 1.4f;
        }
        AddChild(new WorldEnvironment { Environment = env });

        // Soft even lighting (no hard shadows) so untextured terrain reads cleanly — hard
        // self-shadowing was creating big dark patches that looked like holes.
        var sun = new DirectionalLight3D { RotationDegrees = new Vector3(-55, -40, 0), ShadowEnabled = false, LightEnergy = 0.7f };
        AddChild(sun);
        var fill = new DirectionalLight3D { RotationDegrees = new Vector3(-30, 140, 0), ShadowEnabled = false, LightEnergy = 0.4f };
        AddChild(fill);

        _cam = new FlyCamera { Speed = 60f };
        AddChild(_cam);
        // Locate the FFXI install (ROM DATs): saved setting -> OS auto-detect -> repo-local dev corpus.
        // No game data is bundled; the path is resolved from the user's own retail install at runtime.
        string devCorpus = ProjectSettings.GlobalizePath("res://corpus");
        _corpusDir = Vellichor.Dat.InstallLocator.Resolve(devCorpus) ?? "";
        if (Vellichor.Dat.InstallLocator.IsValidInstall(_corpusDir))
        {
            _dat = new Vellichor.Dat.DatArchive(_corpusDir);
            GD.Print($"[install] FFXI data: {_corpusDir}");
        }
        else
        {
            GD.PrintErr("[install] No FINAL FANTASY XI installation found — prompting for its folder.");
            PromptForInstall();
        }

        // Zone-transition benchmark: VELLICHOR_ZONEBENCH="117,235,100,246,230" loads each zone in sequence
        // (the same LoadZoneById the live zone-change path uses) and times each swap — quantifies the headline
        // fast-zoning: full decode + GPU-ready build per zone, vs the retail client's multi-second stall.
        string? bench = System.Environment.GetEnvironmentVariable("VELLICHOR_ZONEBENCH");
        if (bench is not null)
        {
            var ids = new System.Collections.Generic.List<int>();
            foreach (var s in bench.Split(',')) if (int.TryParse(s.Trim(), out var z)) ids.Add(z);
            if (ids.Count == 0) ids.AddRange(new[] { 117, 235, 100, 246, 230, 103 });
            double total = 0; int n = 0;
            foreach (var z in ids)
            {
                ulong t0 = Time.GetTicksUsec();
                LoadZoneById(z);
                double ms = (Time.GetTicksUsec() - t0) / 1000.0;
                total += ms; n++;
                GD.Print($"[zonebench] zone {z} ({ZoneCatalog.NameFor(z) ?? "?"}) swapped in {ms:0.0} ms");
            }
            GD.Print($"[zonebench] {n} zones, avg {total / System.Math.Max(1, n):0.0} ms/transition (retail: multiple seconds)");
            GetTree().Quit();
            return;
        }

        GD.Print("Vellichor M0 harness up. Look: ARROW KEYS (or right-drag). Move: WASD/QE. Wheel: speed.");

        // Skinning pipeline self-test: renders a 2-bone cylinder with the top bone bent, to verify
        // Godot GPU skinning (bones/weights/skin) works before real model decode is wired in.
        if (System.Environment.GetEnvironmentVariable("VELLICHOR_SKINTEST") != null)
        {
            var mat = new StandardMaterial3D { AlbedoColor = new Color(0.9f, 0.8f, 0.3f) };
            var skel = SkinnedMeshBuilder.BuildBendTest(mat);
            AddChild(skel);
            skel.SetBonePoseRotation(1, Quaternion.FromEuler(new Vector3(0, 0, Mathf.DegToRad(55)))); // bend top
            _cam.Active = false;
            _cam.Position = new Vector3(3.5f, 1.4f, 4f);
            _cam.LookAt(new Vector3(0, 1.1f, 0), Vector3.Up);
            return;
        }

        // Animation driver self-test: bends the bend-test skeleton's top bone via an AnimationDriver
        // playing a synthetic 2-key wobble — verifies pose interpolation + application before real 0x2b
        // data is wired in. VELLICHOR_ANIM_SEEK=<seconds> jumps to a fixed time for a deterministic shot.
        if (System.Environment.GetEnvironmentVariable("VELLICHOR_ANIMTEST") != null)
        {
            var mat = new StandardMaterial3D { AlbedoColor = new Color(0.9f, 0.8f, 0.3f) };
            var skel = SkinnedMeshBuilder.BuildBendTest(mat);
            AddChild(skel);
            var driver = new Render.AnimationDriver();
            var wobble = new[]
            {
                new Render.AnimationDriver.Key(0.0f, Quaternion.FromEuler(new Vector3(0, 0, Mathf.DegToRad(-50))), null),
                new Render.AnimationDriver.Key(0.5f, Quaternion.FromEuler(new Vector3(0, 0, Mathf.DegToRad(50))), null),
                new Render.AnimationDriver.Key(1.0f, Quaternion.FromEuler(new Vector3(0, 0, Mathf.DegToRad(-50))), null),
            };
            driver.Setup(skel, new[] { new Render.AnimationDriver.Track { Bone = 1, Keys = wobble } }, numFrames: 30, fps: 30f);
            AddChild(driver);
            if (float.TryParse(System.Environment.GetEnvironmentVariable("VELLICHOR_ANIM_SEEK"), out var sk))
            { driver.Playing = false; driver.Seek(sk); }
            _cam.Active = false;
            _cam.Position = new Vector3(3.5f, 1.4f, 4f);
            _cam.LookAt(new Vector3(0, 1.1f, 0), Vector3.Up);
            return;
        }

        // Animated character viewer: VELLICHOR_MODELANIM=<path> builds the GPU-skinned creature and plays
        // one of its own 0x2b clips (VELLICHOR_ANIM=idl0|wlk0|run0, default first). VELLICHOR_ANIM_SEEK=<s>
        // freezes a frame for a deterministic shot; VELLICHOR_ANIM_FPS overrides playback rate.
        string? animEnv = System.Environment.GetEnvironmentVariable("VELLICHOR_MODELANIM");
        if (animEnv is not null)
        {
            string? path = int.TryParse(animEnv, out var afid)
                ? _dat?.ResolveFileId(afid)
                : System.IO.Path.Combine(_corpusDir, animEnv.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (path is null || !System.IO.File.Exists(path)) { GD.Print($"[modelanim] not found: {animEnv}"); return; }
            var bytes = System.IO.File.ReadAllBytes(path);
            var node = ModelViewer.BuildAnimatedCharacter(bytes, out var skel, out var mb, out string rep);
            if (node is null || skel is null) { GD.Print($"[modelanim] no skinned model in {animEnv}"); return; }
            AddChild(node);
            GD.Print($"[modelanim] {animEnv}: {rep}");

            // Pick a 0x2b clip by chunk name and drive it.
            string want = System.Environment.GetEnvironmentVariable("VELLICHOR_ANIM") ?? "";
            float fps = float.TryParse(System.Environment.GetEnvironmentVariable("VELLICHOR_ANIM_FPS"), out var fp) ? fp : 30f;
            foreach (var c in Vellichor.Dat.ChunkReader.Walk(bytes))
            {
                if (c.Type != 0x2b) continue;
                if (want.Length > 0 && !c.Name.StartsWith(want)) continue;
                var anim = Vellichor.Dat.ModelDecoder.DecodeAnimation(bytes[c.PayloadOffset..(c.PayloadOffset + c.PayloadLength)]);
                var driver = new Render.AnimationDriver();
                driver.Setup(skel, AnimationDriver.ToTracks(anim, fps), anim.NumFrames, fps);
                AddChild(driver);
                GD.Print($"[modelanim] playing '{c.Name}' bones={anim.NumBones} frames={anim.NumFrames} speed={anim.FrameSpeed:0.###}");
                if (float.TryParse(System.Environment.GetEnvironmentVariable("VELLICHOR_ANIM_SEEK"), out var sk))
                { driver.Playing = false; driver.Seek(sk); }
                break;
            }

            var ctr = mb.GetCenter();
            float rad = mb.Size.Length() * 0.7f + 1.5f;
            _cam.Active = false;
            _cam.Position = ctr + new Vector3(0, mb.Size.Y * 0.25f, rad);
            _cam.LookAt(ctr, Vector3.Up);
            return;
        }

        // Assembled PC viewer: VELLICHOR_PC="race[,face]" builds a humanoid from the race skeleton + face +
        // model-0 (naked) equipment via the model tables, and plays its skeleton's clip. Validates the
        // multi-DAT assembly path. race 1=HumeM 2=HumeF 3=ElvM 4=ElvF 5=TaruM 6=TaruF 7=Mithra 8=Galka.
        string? pcEnv = System.Environment.GetEnvironmentVariable("VELLICHOR_PC");
        if (pcEnv is not null)
        {
            var f = pcEnv.Split(',');
            int race = f.Length > 0 && int.TryParse(f[0], out var rr) ? rr : 1;
            int face = f.Length > 1 && int.TryParse(f[1], out var ff) ? ff : 1;
            var resolver = new ModelResolver(_corpusDir, ProjectSettings.GlobalizePath("res://data/models"));
            var look = new XiHeadless.Game.EntityLook { Known = true, Type = 1, Race = (byte)race, Face = (byte)face };
            // Optional worn equipment: VELLICHOR_PC_EQUIP="body=135,head=12,..." to render a dressed PC
            // through this reliable full-viewport path (the DatViewer SubViewport shear-distorts headless shots).
            foreach (var kv in (System.Environment.GetEnvironmentVariable("VELLICHOR_PC_EQUIP") ?? "").Split(',', System.StringSplitOptions.RemoveEmptyEntries))
            {
                var ab = kv.Split('='); if (ab.Length != 2 || !int.TryParse(ab[1], out var mid)) continue;
                switch (ab[0].Trim().ToLowerInvariant())
                {
                    case "head": look.Head = (ushort)mid; break;
                    case "body": look.Body = (ushort)mid; break;
                    case "hands": look.Hands = (ushort)mid; break;
                    case "legs": look.Legs = (ushort)mid; break;
                    case "feet": look.Feet = (ushort)mid; break;
                }
            }
            var recipe = resolver.PcRecipe(look);
            if (recipe is not { } r) { GD.Print($"[pc] no recipe for race {race}"); return; }
            var partBytes = new System.Collections.Generic.List<byte[]>();
            foreach (var p in r.parts) partBytes.Add(System.IO.File.ReadAllBytes(p));
            var model = Render.CharacterModel.DecodeAssembled(System.IO.File.ReadAllBytes(r.skeleton), partBytes);
            if (model is null) { GD.Print("[pc] assembly failed"); return; }
            if (System.Environment.GetEnvironmentVariable("VELLICHOR_MESHDIAG") is not null)
                GD.Print("[meshdiag]" + Vellichor.Dat.ModelDecoder.MeshDiag);
            var (root, skel, mb2) = model.BuildInstance();
            AddChild(root);
            GD.Print($"[pc] race {race} face {face}: parts={r.parts.Count} bones={model.BoneCount} clips=[{string.Join(",", model.ClipNames)}]");
            string want = System.Environment.GetEnvironmentVariable("VELLICHOR_ANIM") ?? "";
            var clipName = want.Length > 0 ? model.FindClip(want) : model.FindClip("idl", "wlk", "");
            if (clipName is not null && model.Clip(clipName) is { } cc)
            {
                var driver = new Render.AnimationDriver(); AddChild(driver);
                if (System.Environment.GetEnvironmentVariable("VELLICHOR_ADDITIVE") is { } addEnv)
                    driver.Additive = addEnv is "1" or "true";
                driver.Setup(skel, cc.tracks, cc.frames, cc.fps);
                if (float.TryParse(System.Environment.GetEnvironmentVariable("VELLICHOR_ANIM_SEEK"), out var sk2))
                { driver.Playing = false; driver.Seek(sk2); }
                GD.Print($"[pc] playing '{clipName}' fps={cc.fps:0.00} frames={cc.frames} tracks={cc.tracks.Length}");
            }
            // Robust framing: BuildInstance's reported bounds under-report skinned meshes, so walk the actual
            // MeshInstance3D descendants and merge their world-space AABBs.
            Aabb wa = default; bool hasA = false;
            var st = new System.Collections.Generic.Stack<Node>(); st.Push(root);
            while (st.Count > 0) { var nd = st.Pop(); foreach (var ch in nd.GetChildren()) st.Push(ch);
                if (nd is MeshInstance3D mi && mi.Mesh is not null) { var gb = mi.GlobalTransform * mi.GetAabb(); if (!hasA) { wa = gb; hasA = true; } else wa = wa.Merge(gb); } }
            var ctr = hasA ? wa.GetCenter() : mb2.GetCenter();
            // Use the full diagonal (not just height) so an outstretched-arm reference pose still fits the frame.
            float rad = (hasA ? wa.Size.Length() : mb2.Size.Length()) * 0.85f + 1.5f;
            if (float.TryParse(System.Environment.GetEnvironmentVariable("VELLICHOR_PC_ZOOM"), out var zf) && zf > 0f) rad /= zf;
            // VELLICHOR_PC_CAM=deg orbits the camera around Y (0=+Z side, 90=+X, 180=-Z, 270=-X) so we can
            // inspect left/right completeness — a side profile hides a missing lateral half.
            float camDeg = float.TryParse(System.Environment.GetEnvironmentVariable("VELLICHOR_PC_CAM"), out var cd) ? cd : 270f; // default: FORWARD-facing (character looks at camera)
            float ca = Mathf.DegToRad(camDeg);
            _cam.Active = false; _cam.Position = ctr + new Vector3(Mathf.Sin(ca) * rad, 0, Mathf.Cos(ca) * rad); _cam.LookAt(ctr, Vector3.Up);
            return;
        }

        // Standalone model viewer: VELLICHOR_MODEL=<fileId or ROM/dir/file.DAT> renders one model DAT
        // (MMB geometry + IMG textures) centered at origin — for inspecting creature/NPC/object models.
        string? modelEnv = System.Environment.GetEnvironmentVariable("VELLICHOR_MODEL");
        if (modelEnv is not null)
        {
            string? path = int.TryParse(modelEnv, out var fid)
                ? _dat?.ResolveFileId(fid)
                : System.IO.Path.Combine(_corpusDir, modelEnv.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (path is null || !System.IO.File.Exists(path)) { GD.Print($"[model] not found: {modelEnv}"); return; }
            var bytes = System.IO.File.ReadAllBytes(path);
            Aabb mb;
            // Character/NPC skinned model (0x29 skeleton + 0x2a meshes) first; else MMB object; else skeleton gizmo.
            var charNode = ModelViewer.BuildCharacter(bytes, out mb, out string crep);
            if (charNode is not null) { AddChild(charNode); GD.Print($"[model] {modelEnv}: {crep}"); }
            else
            {
                var node = ModelViewer.Build(bytes, out mb, out string mrep);
                AddChild(node);
                GD.Print($"[model] {modelEnv} -> {path[(_corpusDir.Length + 1)..]}: {mrep}");
                var skelNode = ModelViewer.BuildSkeleton(bytes, out Aabb sb, out string srep);
                if (skelNode is not null) { AddChild(skelNode); GD.Print($"[model] {srep}"); if (mb.Size.Length() < 0.1f) mb = sb; }
            }
            var ctr = mb.GetCenter();
            float rad = mb.Size.Length() * 0.7f + 1.5f;
            _cam.Active = false;
            _cam.Position = ctr + new Vector3(0, mb.Size.Y * 0.25f, rad);
            _cam.LookAt(ctr, Vector3.Up);
            return;
        }

        // Model gallery: VELLICHOR_GALLERY=<ROMdir rel path> tiles every model DAT (MMB, no MZB) in a
        // grid, each scaled to a cell — validates the model decoder broadly + surfaces creatures.
        string? galEnv = System.Environment.GetEnvironmentVariable("VELLICHOR_GALLERY");
        if (galEnv is not null)
        {
            var dir = System.IO.Path.Combine(_corpusDir, galEnv.Replace('/', System.IO.Path.DirectorySeparatorChar));
            const int cols = 6, maxN = 30; const float cell = 5f;
            int placed = 0;
            foreach (var f in System.IO.Directory.GetFiles(dir, "*.DAT"))
            {
                byte[] bytes; try { bytes = System.IO.File.ReadAllBytes(f); } catch { continue; }
                var ch = Vellichor.Dat.ChunkReader.Walk(bytes);
                if (!Vellichor.Dat.ChunkReader.LooksChunked(bytes)) continue;
                bool hasMmb = false, hasMzb = false;
                foreach (var c in ch) { if (c.Type == 0x2e) hasMmb = true; if (c.Type == 0x1c) hasMzb = true; }
                if (!hasMmb || hasMzb) continue;
                Node3D node; Aabb mb;
                try { node = ModelViewer.Build(bytes, out mb, out _); } catch { continue; }
                if (mb.Size.Length() < 0.01f) { node.QueueFree(); continue; }
                float sc = 3.2f / Mathf.Max(mb.Size.Length(), 0.01f);
                node.Scale = Vector3.One * sc;
                node.Position = -mb.GetCenter() * sc + new Vector3(0, mb.Size.Y * sc * 0.5f, 0);
                int col = placed % cols, r = placed / cols;
                var slot = new Node3D { Position = new Vector3(col * cell, 0, r * cell) };
                slot.AddChild(node);
                AddChild(slot);
                if (++placed >= maxN) break;
            }
            GD.Print($"[gallery] {galEnv}: {placed} models");
            int rows = (placed + cols - 1) / cols;
            var mid = new Vector3((cols - 1) * cell / 2, 0, (rows - 1) * cell / 2);
            _cam.Active = false;
            _cam.Position = mid + new Vector3(0, cols * cell * 0.5f, rows * cell * 0.9f + 6);
            _cam.LookAt(mid, Vector3.Up);
            return;
        }

        // Particle effect test: VELLICHOR_FXTEST renders a fire + smoke plume (GPU billboards).
        if (System.Environment.GetEnvironmentVariable("VELLICHOR_FXTEST") != null)
        {
            var fire = EffectFx.Fire(); fire.Preprocess = 1.0; fire.Position = new Vector3(-1.1f, 0, 0); AddChild(fire);
            var smoke = EffectFx.Smoke(); smoke.Preprocess = 2.0; smoke.Position = new Vector3(1.1f, 0.2f, 0); AddChild(smoke);
            _cam.Active = false;
            _cam.Position = new Vector3(0, 1.6f, 5.5f);
            _cam.LookAt(new Vector3(0, 1.3f, 0), Vector3.Up);
            _shotFrame = System.Math.Max(_shotFrame, 30); // let the particle sim populate before the shot
            return;
        }

        // ---- mode selection -------------------------------------------------------------------
        // OFFLINE diagnostics (no server): VELLICHOR_ZONE=<id>, VELLICHOR_PLAYER_AT, VELLICHOR_ENT_DEMO.
        // LIVE: the login/char-select UI drives the connection; VELLICHOR_AUTOLOGIN skips the UI and
        // auto-connects (headless testing) with a timed graceful logout.
        string? acct = System.Environment.GetEnvironmentVariable("VELLICHOR_ACCOUNT");
        bool entDemo = System.Environment.GetEnvironmentVariable("VELLICHOR_ENT_DEMO") != null;
        bool forceZoneSet = int.TryParse(System.Environment.GetEnvironmentVariable("VELLICHOR_ZONE"), out var forceZone);
        bool playerAtSet = System.Environment.GetEnvironmentVariable("VELLICHOR_PLAYER_AT") != null;
        bool offline = forceZoneSet || entDemo || playerAtSet;

        const string host = "ffxi.network-gnomes.com", clientVer = "30251101_2";
        string resDir = ProjectSettings.GlobalizePath("res://res");

        if (offline)
        {
            _entityRenderer = new EntityRenderer();
            AttachModels(_entityRenderer);
            AddChild(_entityRenderer);
            LoadZoneById(forceZoneSet ? forceZone : ZoneCatalog.DefaultZoneId);
        }
        else
        {
            // Live: renderer + bridge; the server-reported zone id drives geometry (see _Process).
            _entityRenderer = new EntityRenderer();
            AttachModels(_entityRenderer);
            AddChild(_entityRenderer);
            _bridge = new Vellichor.Net.EntityBridge();
            if (double.TryParse(System.Environment.GetEnvironmentVariable("VELLICHOR_LIVE_SECS"), out var s)) _liveDuration = s;

            if (System.Environment.GetEnvironmentVariable("VELLICHOR_AUTOLOGIN") != null && acct is not null)
            {
                _autoMode = true; // timed observe + graceful logout + quit (headless test path)
                string pass = System.Environment.GetEnvironmentVariable("VELLICHOR_PASSWORD") ?? "";
                int attempts = int.TryParse(System.Environment.GetEnvironmentVariable("VELLICHOR_LIVE_ATTEMPTS"), out var a) ? a : 1;
                GD.Print($"[live] auto-login as '{acct}' (no UI; select-existing, no create; attempts={attempts})");
                _ = System.Threading.Tasks.Task.Run(() =>
                    _bridge.ConnectAsync(host, clientVer, acct, pass, resDir, attempts));
            }
            else
            {
                // Interactive login/character-select UI. Intercept window-close AND terminal signals
                // (Ctrl+C/SIGTERM/SIGHUP) so ANY exit runs the graceful logout then a clean SIGKILL.
                GetTree().AutoAcceptQuit = false;
                RegisterSignals();
                var ui = new Vellichor.UI.ClientUi(_bridge, host, clientVer, resDir);
                ui.EnteredWorld += () => GD.Print("[live] entered world — rendering the server-reported zone");
                AddChild(ui);
            }
        }

        // (ENT_DEMO entities are injected into the persistent _localState below so they render every frame.)

        // Offline movement harness: VELLICHOR_PLAYER_AT="x,y,z" (FFXI coords) drops a controllable
        // local player there — WASD works exactly like live, but nothing is sent to a server. Used
        // to iterate on movement/camera over real terrain without a connection.
        string? at = System.Environment.GetEnvironmentVariable("VELLICHOR_PLAYER_AT");
        if (at is not null && _zoneNode is not null)
        {
            if (_entityRenderer is null) { _entityRenderer = new EntityRenderer(); AttachModels(_entityRenderer); AddChild(_entityRenderer); }
            var xyz = at.Split(',');
            if (xyz.Length == 3
                && float.TryParse(xyz[0], out var fx) && float.TryParse(xyz[1], out var fy) && float.TryParse(xyz[2], out var fz))
            {
                _localState = new XiHeadless.Game.WorldState { MyName = "Zenku", X = fx, Y = fy, Z = fz, ZoneId = (ushort)(_loadedZone > 0 ? _loadedZone : 0) };
                _localState.Entities[38] = new XiHeadless.Game.Entity
                { Id = 38, Index = 0x400, Name = "Zenku", X = fx, Y = fy, Z = fz, Allegiance = 1, TypeKnown = true };
                // Sample stats + chat so the HUD is self-verifiable offline (screenshot).
                _localState.MaxHp = 1109; _localState.Hp = 940; _localState.MaxMp = 10; _localState.Mp = 10;
                _localState.Tp = 1250; _localState.MainJob = 21; _localState.MainJobLevel = 73; _localState.SubJob = 22; _localState.Gil = 29963;
                // Offline self-model harness: give ourselves a real look + a model cache so the self renders as
                // a proper character (not a capsule) — lets facing / walk animation be validated offline (AUTOWALK).
                _localState.MyLook = new XiHeadless.Game.EntityLook { Type = 1, Race = 7, Face = 1, Known = true, Head = 1, Body = 5, Hands = 3, Legs = 2, Feet = 4, Main = 20 };
                if (_dat is not null) _entityModels ??= new EntityModelCache(_dat, _corpusDir, ProjectSettings.GlobalizePath("res://data/models"));
                EnsureMenus();
                // Demo inventory + a few known spells so the menu system is screenshot-verifiable offline.
                void Inv(byte slot, ushort id, ushort qty) { _localState.Inventory[((byte)0, slot)] = id; _localState.InventoryQty[((byte)0, slot)] = qty; }
                Inv(1, 4096, 12); Inv(2, 4112, 8); Inv(3, 4128, 3); Inv(4, 4148, 5); Inv(5, 16535, 1); Inv(6, 643, 4); Inv(7, 4300, 2);
                _localState.KnownSpellBits = new byte[160];
                foreach (ushort sid in new ushort[] { 1, 2, 3, 7, 25, 40, 108, 320 })
                    _localState.KnownSpellBits[sid >> 3] |= (byte)(1 << (sid & 7));
                string? demoMenu = System.Environment.GetEnvironmentVariable("VELLICHOR_MENU");
                if (demoMenu is not null) Callable.From(() => OpenDemoMenu(demoMenu)).CallDeferred();
                _localState.AddChat(6, "Oston", "Hello there, adventurer!", 0);
                _localState.AddChat(1, "Vaughnn", "{Seeking party} Tahrongi Canyon, need healer!", 0);
                _localState.AddChat(4, "Lola", "on my way to camp", 0);
                _localState.AddChat(3, "Zeke", "you around later?", 0);
                _localState.AddChat(0, "", "Obtained 320 gil.", 0);
                // demo entities near the player (entity frame: X=self.X, vertical=self.Z(fz), horizZ=self.Y(fy)),
                // each given a REAL look so the resolver/model pipeline renders actual FFXI models in-world:
                // assembled PCs of several races + a self-contained creature (via the creature-band file id).
                var looks = new XiHeadless.Game.EntityLook[]
                {
                    new() { Known = true, Type = 1, Race = 1, Face = 1 }, // Hume male
                    new() { Known = true, Type = 0, ModelId = 236 },      // creature (52795+236 = ROM9/2/8)
                    new() { Known = true, Type = 1, Race = 4, Face = 1 }, // Elvaan female
                    new() { Known = true, Type = 1, Race = 8, Face = 1 }, // Galka
                    new() { Known = true, Type = 1, Race = 7, Face = 1 }, // Mithra
                    default,                                              // no look -> capsule
                };
                var names = new[] { "Hume", "Beast", "Elvaan", "Galka", "Mithra", "Capsule" };
                var hps = new byte[] { 55, 30, 80, 100, 15, 0 };
                for (int k = 0; k < 6; k++)
                    _localState.Entities[(uint)(200 + k)] = new XiHeadless.Game.Entity
                    { Id = (uint)(200 + k), Name = names[k], X = fx + (k - 2) * 2.2f, Y = fz, Z = fy + 5,
                      Allegiance = (byte)(k % 4), TypeKnown = true, Look = looks[k], Hpp = hps[k] };
                // Demo floating combat text (damage/heal/miss) so the feedback pipeline is self-verifiable offline.
                _localState.AddCombatFx(200, 128, 0, 0);   // damage on the Hume
                _localState.AddCombatFx(201, 512, 0, 0);   // big damage on the creature
                _localState.AddCombatFx(202, 240, 1, 0);   // heal on the Elvaan
                _localState.AddCombatFx(203, 0, 2, 0);     // miss on the Galka
            }
        }
    }

    /// <summary>
    /// Player control via a CharacterBody3D: WASD moves camera-relative, MoveAndSlide handles WALLS,
    /// slopes and ground-snapping against the zone's trimesh colliders; gravity keeps us grounded.
    /// Writes WorldState.X/Z/Y/Rotation/Moving (which BuildPos sends) ONLY while actually moving —
    /// an idle client must send back the server's exact position, never relocate the character.
    /// Runs on the physics tick. Camera orbit (←/→, right-drag, wheel) + follow is applied here too.
    private void ControlAndFollow(XiHeadless.Game.WorldState ws, double delta)
    {
        EnsurePlayerBody(ws); // create + spawn on the ground the first time
        UpdatePlayerModel(ws); // swap the placeholder capsule for the real self character when MyLook resolves

        // Suppress movement/camera input while a menu is open or chat is focused.
        bool blockInput = (_menus?.AnyOpen == true) || (_hud is not null && _hud.ChatFocused);

        // Camera-relative move basis on the XZ plane.
        float turn = 1.8f * (float)delta;
        if (!blockInput && Input.IsKeyPressed(Key.Left)) _camYaw += turn;
        if (!blockInput && Input.IsKeyPressed(Key.Right)) _camYaw -= turn;
        // Keyboard zoom (PageUp/= in, PageDown out; wheel also works). `-` is reserved for the menu.
        float zoom = 40f * (float)delta;
        if (!blockInput && (Input.IsKeyPressed(Key.Pageup) || Input.IsKeyPressed(Key.Equal))) _camDist = Mathf.Max(1.5f, _camDist - zoom);
        if (!blockInput && Input.IsKeyPressed(Key.Pagedown)) _camDist = Mathf.Min(80f, _camDist + zoom);
        var fwd = new Vector3(Mathf.Sin(_camYaw), 0, Mathf.Cos(_camYaw));
        var right = new Vector3(-Mathf.Cos(_camYaw), 0, Mathf.Sin(_camYaw)); // camera screen-right

        var body = _playerBody!;

        // Movement (physical WASD, layout-independent; Shift sprints). Round-trip VERIFIED: walk ->
        // logout -> login lands at the same spot on the ground. When IDLE we do NOT write the
        // position, so BuildPos echoes the server's own position unchanged — standing still (or just
        // connecting) can never relocate the character; only actual movement writes a new position.
        var mv = Vector3.Zero;
        bool typing = _hud is not null && _hud.ChatFocused; // don't walk while typing in chat
        if (_navTarget is { } nt)
        {
            // Auto-approach: walk straight toward the nav target (used by auto-engage to close to melee).
            // Any manual key cancels it. Arrived within ~3.5y (XZ) clears it.
            var d = (nt - body.Position) with { Y = 0 };
            if (d.Length() < 3.5f || Input.IsPhysicalKeyPressed(Key.W) || Input.IsPhysicalKeyPressed(Key.S)
                || Input.IsPhysicalKeyPressed(Key.A) || Input.IsPhysicalKeyPressed(Key.D)) _navTarget = null;
            else mv = d.Normalized();
        }
        else if (!typing && !blockInput)
        {
            if (Input.IsPhysicalKeyPressed(Key.W) || _autoWalk) mv += fwd;
            if (Input.IsPhysicalKeyPressed(Key.S)) mv -= fwd;
            if (Input.IsPhysicalKeyPressed(Key.D)) mv += right;
            if (Input.IsPhysicalKeyPressed(Key.A)) mv -= right;
        }
        bool moving = mv != Vector3.Zero;
        ws.Moving = moving;
        if (moving)
        {
            mv = mv.Normalized();
            float speed = _moveSpeed * (Input.IsKeyPressed(Key.Shift) ? 3f : 1f);
            var cur = body.Position + new Vector3(mv.X, 0, mv.Z) * speed * (float)delta;
            if (GroundYAt(cur.X, cur.Z, cur.Y, out var gy)) cur.Y = gy + 0.05f;
            body.Position = cur;
            GodotToSelf(cur, ws);
            float ang = Mathf.Atan2(-mv.X, mv.Z);
            ws.Rotation = (byte)(((int)(ang / Mathf.Tau * 256) % 256 + 256) % 256);
            // Face the walk direction. The built model carries a 180° display flip, so its forward is the
            // NEGATED move vector (Atan2(mv.X,mv.Z) alone made the character moonwalk / face the camera).
            body.Rotation = new Vector3(0, Mathf.Atan2(-mv.X, -mv.Z), 0);
        }
        if (_playerTag is not null && _playerTag.Text != ws.MyName) _playerTag.Text = ws.MyName;

        // Follow camera: orbit (yaw/pitch) behind the character at _camDist, looking at the torso.
        var p = body.GlobalPosition;
        var focus = p + new Vector3(0, 1.2f, 0);
        float cp = Mathf.Cos(_camPitch);
        var back = new Vector3(-Mathf.Sin(_camYaw) * cp, Mathf.Sin(_camPitch), -Mathf.Cos(_camYaw) * cp);
        _cam!.Position = focus + back * _camDist;
        _cam.LookAt(focus, Vector3.Up);
    }

    /// Creates the player CharacterBody3D once, spawned on the ground at the server position, and
    /// switches the camera off free-fly. Logs a spawn sanity line (server pos vs ground hit).
    private void EnsurePlayerBody(XiHeadless.Game.WorldState ws)
    {
        if (_playerBody is not null) return;
        _cam!.Active = false;
        var gpos = SelfToGodot(ws);
        float gx = gpos.X, gz = gpos.Z;
        bool g = GroundYAt(gx, gz, gpos.Y, out var gy);
        float spawnY = g ? gy : gpos.Y;
        GD.Print($"[player] '{ws.MyName}' serverXYZ=({ws.X:0.0},{ws.Y:0.0},{ws.Z:0.0}) godot=({gx:0.0},{gpos.Y:0.0},{gz:0.0}) " +
                 $"groundHit={(g ? gy.ToString("0.0") : "none")}. Move: WASD, orbit: ←/→ or right-drag, sprint: Shift.");

        var body = new Node3D { Position = new Vector3(gx, spawnY, gz) };
        _playerCapsule = new MeshInstance3D
        {
            Mesh = new CapsuleMesh { Radius = 0.4f, Height = 2.0f },
            Position = new Vector3(0, 1f, 0),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(1.0f, 0.95f, 0.30f) },
        };
        body.AddChild(_playerCapsule);
        _playerTag = new Label3D
        {
            Text = ws.MyName, Position = new Vector3(0, 2.4f, 0),
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, FontSize = 48, NoDepthTest = true,
        };
        body.AddChild(_playerTag);
        AddChild(body);
        _playerBody = body;
    }

    /// Once the self appearance (WorldState.MyLook, from the 0x0A zone-in GrapIDTbl) is known, build the
    /// real player character model, attach it to the player body, hide the placeholder capsule, and drive
    /// idle/walk from movement. One-shot upgrade; the capsule stays if the look never resolves.
    private void UpdatePlayerModel(XiHeadless.Game.WorldState ws)
    {
        if (_playerModel is null && _entityModels is not null && ws.MyLook.Known && !_playerModelTried)
        {
            _playerModelTried = true;
            var model = _entityModels.Get(ws.MyLook, ws.MyName);
            if (model is not null)
            {
                var (root, skel, _) = model.BuildInstance();
                _playerBody!.AddChild(root);
                _playerModel = root; _playerSkel = skel; _playerCharModel = model;
                if (model.FindClip("") is not null)
                {
                    _playerDriver = new Render.AnimationDriver();
                    _playerBody.AddChild(_playerDriver);
                    _playerClip = null;
                }
                if (_playerCapsule is not null) { _playerCapsule.Visible = false; }
                GD.Print($"[self-model] built: race={ws.MyLook.Race} face={ws.MyLook.Face} body={ws.MyLook.Body} bones={model.BoneCount}");
            }
            else GD.Print($"[self-model] MyLook known (race={ws.MyLook.Race}) but model unresolved — keeping capsule");
        }
        if (_playerDriver is not null && _playerCharModel is not null && _playerSkel is not null)
        {
            string? want = ws.Moving ? _playerCharModel.FindClip("wlk", "run", "mov") : _playerCharModel.FindClip("idl", "dw0", "brth");
            want ??= _playerCharModel.FindClip("");
            if (want is not null && want != _playerClip && _playerCharModel.Clip(want) is { } c)
            {
                _playerDriver.Setup(_playerSkel, c.tracks, c.frames, c.fps);
                _playerDriver.Loop = true;
                _playerClip = want;
            }
        }
    }

    /// Builds ONE merged trimesh collider for the whole zone (walls/slopes/ground for the player).
    /// A single ConcavePolygonShape3D has an internal BVH, so queries are fast — vs. one collider per
    /// visual mesh, where each per-texture mesh spans the whole zone so their AABBs all overlap the
    /// player and defeat broadphase culling (that was the stutter). One-time per zone load.
    private void BuildColliders(Node zoneRoot)
    {
        if (!GodotObject.IsInstanceValid(zoneRoot)) return; // zone was freed (rapid re-zone) before we ran
        // Collect world-space faces on the main thread (scene access), but move the expensive BVH build
        // (ConcavePolygonShape3D.SetFaces — ~1.3s for a big zone's ~900k tris) OFF the main thread so it
        // never hitches gameplay. Attach the finished shape back on the main thread via CallDeferred.
        var faces = new System.Collections.Generic.List<Vector3>(1 << 16);
        CollectFaces(zoneRoot, faces);
        if (faces.Count == 0) return;
        var arr = faces.ToArray();
        System.Threading.Tasks.Task.Run(() =>
        {
            var shape = new ConcavePolygonShape3D();
            shape.SetFaces(arr);
            Callable.From(() =>
            {
                if (!GodotObject.IsInstanceValid(zoneRoot)) { shape.Dispose(); return; }
                var body = new StaticBody3D();
                body.AddChild(new CollisionShape3D { Shape = shape });
                zoneRoot.AddChild(body);
            }).CallDeferred();
        });
    }

    private static void CollectFaces(Node n, System.Collections.Generic.List<Vector3> faces)
    {
        if (n is MeshInstance3D mi && mi.Mesh is not null)
        {
            var xf = mi.GlobalTransform;
            foreach (var v in mi.Mesh.GetFaces()) faces.Add(xf * v);
        }
        foreach (var c in n.GetChildren()) CollectFaces(c, faces);
    }

    /// Give an entity renderer the shared model cache (created once from the corpus + FTABLE archive), so
    /// live entities can render real animated models with a capsule fallback.
    private void AttachModels(EntityRenderer r)
    {
        _entityModels ??= new EntityModelCache(_dat, _corpusDir, ProjectSettings.GlobalizePath("res://data/models"));
        r.Models = _entityModels;
    }

    // The SELF position packet stores fields as (X, horizontalZ, vertical) — DIFFERENT from entity
    // packets, which store (X, vertical, horizontalZ). So for the local character ws.Y is a HORIZONTAL
    // and ws.Z is the VERTICAL (verified live: self ws vs self-entity had Y/Z swapped, and entity Y is
    // the true vertical since NPCs render grounded). These convert self ws <-> Godot world.
    private static Vector3 SelfToGodot(XiHeadless.Game.WorldState ws)
        => new(-ws.X, EntityRenderer.YSign * ws.Z, ws.Y);

    private static void GodotToSelf(Vector3 g, XiHeadless.Game.WorldState ws)
    {
        // OUTBOUND convention: BuildPos sends ws.Y->@8 (server VERTICAL) and ws.Z->@12 (server
        // HORIZONTAL-Z). This is the OPPOSITE field order from the inbound zone-in parse that
        // SelfToGodot reads (there ws.Z=vertical, ws.Y=horizontalZ) — a latent XiHeadless quirk — so
        // GodotToSelf and SelfToGodot are deliberately NOT inverses. Traced full round-trip
        // (send -> server store -> next-login parse -> SelfToGodot) returns to the same Godot point:
        ws.X = -g.X;                       // server X  (mirror)
        ws.Y = EntityRenderer.YSign * g.Y; // server VERTICAL  (-> BuildPos @8)
        ws.Z = g.Z;                        // server HORIZONTAL-Z  (-> BuildPos @12)
    }

    /// Casts a ray DOWN from just above <paramref name="fromY"/> (the character's current height) to
    /// the first surface below — so we land on the ground at their feet, not the topmost arch/roof
    /// overhead. Small upward margin so a slight step-up still catches.
    private bool GroundYAt(float gx, float gz, float fromY, out float groundY)
    {
        groundY = 0;
        var space = GetWorld3D()?.DirectSpaceState;
        if (space is null) return false;
        var from = new Vector3(gx, fromY + 4f, gz);
        var to = new Vector3(gx, _zoneBounds.Position.Y - 50f, gz);
        var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(from, to));
        if (hit.Count == 0 || !hit.ContainsKey("position")) return false;
        groundY = ((Vector3)hit["position"]).Y;
        return true;
    }

    /// Mouse camera control while in world: right-drag orbits (yaw/pitch), wheel zooms.
    public override void _Input(InputEvent e)
    {
        if (ActiveState is null || _zoneNode is null) return;
        if (_menus?.AnyOpen == true && e is InputEventMouse) return; // let the menu GUI take mouse input
        if (e is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Right)
        {
            _orbiting = mb.Pressed;
            Input.MouseMode = _orbiting ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;
        }
        else if (e is InputEventMouseMotion mm && _orbiting)
        {
            _camYaw -= mm.Relative.X * 0.005f;
            _camPitch = Mathf.Clamp(_camPitch - mm.Relative.Y * 0.005f, -0.4f, 1.4f);
        }
        else if (e is InputEventMouseButton wheel && wheel.Pressed && wheel.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
        {
            if (wheel.ButtonIndex == MouseButton.WheelUp) _camDist = Mathf.Max(1.5f, _camDist / 1.15f);
            else _camDist = Mathf.Min(80f, _camDist * 1.15f);
        }
        else if (e is InputEventMouseButton lc && lc.Pressed && lc.ButtonIndex == MouseButton.Left)
        {
            TargetNearestTo(lc.Position); // click-to-target: pick the entity closest to the cursor
        }
        else if (e is InputEventKey k && k.Pressed && !k.Echo && !(_hud is not null && _hud.ChatFocused))
        {
            HandleGameKey(k);
        }
    }

    /// Retail-style keyboard commands (FFXI is keyboard-first): `-` = main menu, Esc = cancel/deselect,
    /// Ctrl+<letter> = command shortcuts. (Ctrl OR Cmd accepted so it's natural on macOS too.)
    private void HandleGameKey(InputEventKey k)
    {
        bool ctrl = k.CtrlPressed || k.MetaPressed;
        bool menuOpen = _menus?.AnyOpen == true;

        // `-` toggles the main menu whether open or closed (retail).
        if (k.Keycode is Key.Minus or Key.KpSubtract && !ctrl)
        {
            _menus?.Toggle(new Vellichor.UI.Menus.MainMenu(_menuCtx!));
            return;
        }
        // While a menu is open, arrows/Enter/Esc are the MenuManager's; swallow other game keys.
        if (menuOpen) return;

        if (ctrl)
        {
            switch (k.Keycode)
            {
                case Key.M: _menus?.Open(new Vellichor.UI.Menus.MagicMenu(_menuCtx!)); break;        // Magic
                case Key.J: _menus?.Open(new Vellichor.UI.Menus.AbilitiesMenu(_menuCtx!)); break;    // Job abilities
                case Key.W: _menus?.Open(new Vellichor.UI.Menus.WeaponSkillsMenu(_menuCtx!)); break; // Weapon skills
                case Key.I: _menus?.Open(new Vellichor.UI.Menus.ItemsMenu(_menuCtx!)); break;        // Items
                case Key.A: ToggleEngage(); break;   // Attack / engage the target
                case Key.C: CheckTarget(); break;    // /check (con)
                case Key.H: _bridge?.SendRest(); break; // /heal (rest)
            }
            return;
        }

        switch (k.Keycode)
        {
            case Key.Tab: CycleTarget(); break;                          // cycle target
            case Key.Escape: if (ActiveState is { } s) s.CurrentTargetId = 0; break; // cancel / deselect
            case Key.F1: _hud?.ToggleHelp(); break;
        }
    }

    /// Cast a spell on the current target (or self if none): 0x01A category CastMagic, param = spell id.
    private void CastSpell(ushort spellId)
    {
        var ws = ActiveState;
        if (_bridge is null || ws is null) return;
        uint tid = ws.CurrentTargetId != 0 ? ws.CurrentTargetId : ws.MyId;
        ushort tidx = ws.MyIndex;
        if (tid != ws.MyId && ws.Entities.TryGetValue(tid, out var te)) tidx = te.Index;
        _bridge.SendAction(Vellichor.Net.EntityBridge.ActCastMagic, tid, tidx, spellId);
        GD.Print($"[action] cast spell {spellId} on 0x{tid:X}");
    }

    /// Open a menu by name for offline screenshots (VELLICHOR_MENU=main|magic|items|status|equipment).
    private void OpenDemoMenu(string name)
    {
        if (_menus is null || _menuCtx is null) return;
        MenuPanelFor(name);
    }

    private void MenuPanelFor(string name)
    {
        var ctx = _menuCtx!;
        Vellichor.UI.Menus.MenuPanel panel = name.ToLowerInvariant() switch
        {
            "magic" => new Vellichor.UI.Menus.MagicMenu(ctx),
            "items" => new Vellichor.UI.Menus.ItemsMenu(ctx),
            "status" => new Vellichor.UI.Menus.StatusMenu(ctx),
            "equipment" => new Vellichor.UI.Menus.EquipmentMenu(ctx),
            _ => new Vellichor.UI.Menus.MainMenu(ctx),
        };
        _menus!.Open(panel);
    }

    /// Create the menu system once (loads the static game tables, builds the manager + shared context).
    private void EnsureMenus()
    {
        if (_menus is not null) return;
        Vellichor.UI.Menus.GameData.Load(ProjectSettings.GlobalizePath("res://data"));
        _menus = new Vellichor.UI.Menus.MenuManager();
        AddChild(_menus);
        _menuCtx = new Vellichor.UI.Menus.MenuContext
        {
            State = () => ActiveState,
            CastSpell = CastSpell,
            Logout = () => RequestQuit("menu logout"),
            Manager = _menus,
        };
    }

    /// C: /check the current target — arm the con-capture fields, then send 0x0DD. The reply fills
    /// WorldState.ConMobLevel/ConDifficulty (shown on the HUD target line).
    private void CheckTarget()
    {
        var ws = ActiveState;
        if (_bridge is null || ws is null || ws.CurrentTargetId == 0) return;
        if (!ws.Entities.TryGetValue(ws.CurrentTargetId, out var te)) return;
        ws.ConTargetId = te.Id; ws.ConDifficulty = -1; ws.ConMobLevel = 0;
        _bridge.SendCheck(te.Id, te.Index);
        GD.Print($"[action] /check {te.Name}");
    }

    private bool _engaged;
    private bool _autoEngaged;
    private Vector3? _navTarget; // when set, the player auto-walks toward this Godot position (auto-approach)

    /// R: toggle auto-attack on the current target by sending the 0x01A engage/disengage action. Live only.
    private void ToggleEngage()
    {
        var ws = ActiveState;
        if (_bridge is null || ws is null || ws.CurrentTargetId == 0) return;
        if (!ws.Entities.TryGetValue(ws.CurrentTargetId, out var te)) return;
        _engaged = !_engaged;
        _bridge.EngageTarget(te.Id, te.Index, _engaged);
        GD.Print($"[action] {(_engaged ? "engage" : "disengage")} {te.Name} (0x{te.Id:X})");
    }

    /// Tab targeting: advance CurrentTargetId to the next entity ordered by distance from the player
    /// (wrapping around). Skips self; picks the nearest when nothing is targeted yet.
    private void CycleTarget()
    {
        var ws = ActiveState;
        if (ws is null) return;
        XiHeadless.Game.Entity[] ents;
        try { ents = System.Linq.Enumerable.ToArray(ws.Entities.Values); } catch { return; }
        var origin = _playerBody?.Position ?? SelfToGodot(ws);
        var ordered = System.Linq.Enumerable.ToList(System.Linq.Enumerable.OrderBy(
            System.Linq.Enumerable.Where(ents, en => string.IsNullOrEmpty(ws.MyName) || en.Name != ws.MyName),
            en => new Vector3(-en.X, EntityRenderer.YSign * en.Y, en.Z).DistanceTo(origin)));
        if (ordered.Count == 0) return;
        int cur = ordered.FindIndex(en => en.Id == ws.CurrentTargetId);
        var next = ordered[(cur + 1) % ordered.Count];
        ws.CurrentTargetId = next.Id;
        GD.Print($"[target] {next.Name} ({next.Id})");
    }

    /// Left-click targeting: set CurrentTargetId to the on-screen entity nearest the cursor (within a
    /// pixel threshold). The HUD reads CurrentTargetId to show the target's name + HP.
    private void TargetNearestTo(Vector2 screen)
    {
        var ws = ActiveState;
        if (ws is null || _cam is null) return;
        XiHeadless.Game.Entity[] ents;
        try { ents = System.Linq.Enumerable.ToArray(ws.Entities.Values); } catch { return; }
        uint best = 0; float bestD = 48f; // px
        foreach (var en in ents)
        {
            if (!string.IsNullOrEmpty(ws.MyName) && en.Name == ws.MyName) continue;
            var wp = new Vector3(-en.X, EntityRenderer.YSign * en.Y, en.Z) + new Vector3(0, 1f, 0);
            if (_cam.IsPositionBehind(wp)) continue;
            float d = _cam.UnprojectPosition(wp).DistanceTo(screen);
            if (d < bestD) { bestD = d; best = en.Id; }
        }
        if (best != 0) { ws.CurrentTargetId = best; GD.Print($"[target] {best}"); }
    }

    /// <summary>
    /// (Re)builds the rendered zone for a server zone id. id &lt; 0 uses the default zone.
    /// Unmapped ids fall back to the default but are logged so the catalog can be filled in.
    /// </summary>
    private void LoadZoneById(int zoneId)
    {
        _loadedZone = zoneId; // remember the REQUESTED id so we don't reload every frame
        int useId = zoneId >= 0 ? zoneId : ZoneCatalog.DefaultZoneId;
        string name = ZoneCatalog.NameFor(useId) ?? $"zone {useId}";

        // zone id -> zone_data file id (client formula) -> ROM path (FTABLE, repack-proof).
        string? full = _dat?.ResolveFileId(ZoneCatalog.FileIdFor(useId));
        if (full is null || !System.IO.File.Exists(full))
        {
            GD.Print($"[zone] id {zoneId} ({name}) -> file id {ZoneCatalog.FileIdFor(useId)} did not resolve to a DAT — placeholder.");
            _cam!.Position = new Vector3(0, 3, 8);
            LoadPlaceholder();
            return;
        }

        // Free any previously-built zone (live zone change) before rebuilding.
        _zoneNode?.QueueFree();
        _water?.QueueFree();

        var zone = ZoneLoader.Load(full, out string report, out Aabb b);
        AddChild(zone);
        _zoneNode = zone;
        // Collider build (merging every mesh's faces into one ConcavePolygonShape3D) dominates load time for
        // dense zones (8000+ instances) yet isn't needed for the zone to be VISIBLE — defer it so the new zone
        // renders immediately (fast perceived zoning), and collision fills in a frame later. Spawn Y falls back
        // to the server position until then.
        Callable.From(() => BuildColliders(zone)).CallDeferred();
        _zoneBounds = b;
        GD.Print($"Zone: [{useId} {name}] {report}");

        // Water plane: the DAT has no ground under rivers/ponds, so drop a translucent plane near
        // the low point — it shows through no-ground regions as water, occluded by higher terrain.
        // It's a single-level approximation, so it floods zones whose real water isn't one flat
        // level (or a char standing at the zone floor); VELLICHOR_NOWATER skips it.
        if (System.Environment.GetEnvironmentVariable("VELLICHOR_NOWATER") == null)
        {
            var wc = b.GetCenter();
            _water = new MeshInstance3D
            {
                Mesh = new PlaneMesh { Size = new Vector2(b.Size.X, b.Size.Z) },
                Position = new Vector3(wc.X, b.Position.Y + 3f, wc.Z),
                MaterialOverride = new StandardMaterial3D
                {
                    AlbedoColor = new Color(0.12f, 0.26f, 0.38f, 0.78f),
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    Metallic = 0.7f, Roughness = 0.06f,   // smooth+metallic -> reflects the sky (reads as water)
                    CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                },
            };
            AddChild(_water);
        }

        var c = b.GetCenter();
        if (System.Environment.GetEnvironmentVariable("VELLICHOR_GROUND") != null)
        {
            _cam!.Position = new Vector3(c.X, b.Position.Y + b.Size.Y + 45, c.Z + 90);
            _cam.RotationDegrees = new Vector3(-22, 0, 0);
        }
        else
        {
            _cam!.Position = new Vector3(c.X, b.Position.Y + b.Size.Y + 80, c.Z + 80);
            _cam.RotationDegrees = new Vector3(-40, 0, 0);
        }
    }

    public override void _Process(double delta)
    {
        // Background graceful-logout finished (window close) -> now quit on the main thread.
        if (_readyToQuit) { GD.Print("[live] logged out cleanly."); HardExit(); return; }

        // World: the server-reported zone id drives geometry, entities stream in, and the local
        // character is player-controlled with a follow camera. Works for live (_bridge.State) and
        // offline (_localState) alike.
        var ws = ActiveState;
        if (ws is not null)
        {
            int zid = ws.ZoneId;
            if (zid > 0 && zid != _loadedZone) LoadZoneById(zid);
            _entityRenderer?.Update(ws, GetProcessDeltaTime());

            // In-world HUD: create once we're in world, update every frame (headless-auto skips it).
            if (!_autoMode)
            {
                if (_hud is null)
                {
                    _hud = new Vellichor.UI.GameHud();
                    _hud.OnSend = (mode, msg) => _bridge?.SendChat(mode, msg); // live send (flagged for test)
                    _hud.OnLogout = () => RequestQuit("logout button");
                    AddChild(_hud);
                    EnsureMenus();
                }
                _hud.Update(ws, _playerBody?.Position ?? SelfToGodot(ws));
                if (Input.IsKeyPressed(Key.Enter) && !_hud.ChatFocused && _menus?.AnyOpen != true) _hud.FocusChat();
            }

            // Diagnose entity streaming: log the live entity count every ~2s (interactive only).
            if (!_autoMode && _bridge is not null)
            {
                _entLogAccum += delta;
                if (_entLogAccum >= 2.0)
                {
                    _entLogAccum = 0;
                    GD.Print($"[live] fps={Engine.GetFramesPerSecond()} entities={ws.Entities.Count} rendered={_entityRenderer?.Rendered ?? 0} zone={ws.ZoneId} moving={ws.Moving}");
                }
            }
        }

        // Headless auto mode: observe for a fixed window, then graceful logout + quit.
        // Interactive mode logs out on window close instead (see _Notification).
        if (_autoMode && _bridge is not null)
        {
            int prev = (int)_liveElapsed;
            _liveElapsed += delta;
            if ((int)_liveElapsed != prev)
            {
                var wsd = _bridge.State;
                string looks = "";
                if (wsd is not null)
                {
                    try
                    {
                        int shown = 0;
                        foreach (var e in System.Linq.Enumerable.ToArray(wsd.Entities.Values))
                        {
                            if (!e.Look.Known || shown >= 4) continue;
                            looks += e.Look.Type == 0
                                ? $" [{e.Name}:mob model={e.Look.ModelId}]"
                                : $" [{e.Name}:eq race={e.Look.Race} body={e.Look.Body}]";
                            shown++;
                        }
                    }
                    catch { }
                }
                // Diagnostics for the two live-only reports: chat reception + known-spell bitmap.
                int chatN = 0, spellBits = 0; string lastChat = "";
                if (wsd is not null)
                {
                    try { chatN = wsd.ChatLog.Count; if (chatN > 0) { var l = wsd.ChatLog[^1]; lastChat = $"[{l.Kind}]{l.Sender}:{l.Message}"; } } catch { }
                    foreach (var by in wsd.KnownSpellBits) spellBits += System.Numerics.BitOperations.PopCount(by);
                }
                GD.Print($"[live] t={(int)_liveElapsed}s  entities={wsd?.Entities.Count ?? 0}  rendered={_entityRenderer?.Rendered ?? 0}  chat={chatN} (last: {lastChat})  knownSpells={spellBits}  looks:{looks}");

                // Combat validation hook: VELLICHOR_AUTOENGAGE targets the nearest mob a few seconds after
                // zone-in, /checks it, and engages — so a headless run exercises the 0x01A action send and the
                // damage/HP-bar feedback live (no interactive input).
                if (!_autoEngaged && _liveElapsed > 6 && wsd is not null
                    && System.Environment.GetEnvironmentVariable("VELLICHOR_AUTOENGAGE") is not null)
                {
                    XiHeadless.Game.Entity? mob = null; float bd = 1e9f;
                    try
                    {
                        foreach (var e in System.Linq.Enumerable.ToArray(wsd.Entities.Values))
                        {
                            if (e.Id == wsd.MyId || !e.IsMob) continue;
                            float d = new Vector3(e.X - wsd.X, e.Y - wsd.Y, e.Z - wsd.Z).Length();
                            if (d < bd) { bd = d; mob = e; }
                        }
                    }
                    catch { }
                    if (mob is { } m)
                    {
                        wsd.CurrentTargetId = m.Id;
                        if (bd <= 4.5f)   // in melee range -> /check + engage
                        {
                            wsd.ConTargetId = m.Id; wsd.ConDifficulty = -1;
                            _bridge.SendCheck(m.Id, m.Index);
                            _bridge.EngageTarget(m.Id, m.Index, true);
                            _autoEngaged = true; _navTarget = null;
                            GD.Print($"[autoengage] engage '{m.Name}' (0x{m.Id:X}) at {bd:0}y");
                        }
                        else              // auto-approach (tracks the mob each tick until in range)
                        {
                            _navTarget = new Vector3(-m.X, EntityRenderer.YSign * m.Y, m.Z);
                            GD.Print($"[autoengage] approaching '{m.Name}' ({bd:0}y)");
                        }
                    }
                }
            }
            if (!_liveLoggingOut && _liveElapsed >= _liveDuration)
            {
                _liveLoggingOut = true;
                if (_shot is not null) { GetViewport().GetTexture().GetImage().SavePng(_shot); GD.Print($"saved -> {_shot}"); }
                // Disengage before logging out — the server refuses a graceful logout while in combat (would
                // leave a stuck session). Harmless if we weren't engaged.
                if (_autoEngaged && _bridge.State is { CurrentTargetId: var tid and not 0 } st
                    && st.Entities.TryGetValue(tid, out var te)) _bridge.EngageTarget(te.Id, te.Index, false);
                GD.Print($"[live] observe done: {_bridge.Status}; entities={_bridge.State?.Entities.Count ?? 0}. graceful logout (~40s)...");
                _bridge.Shutdown();
                GD.Print("[live] logged out cleanly.");
                GetTree().Quit();
            }
            return;
        }

        // Screenshot-and-quit for offline zone views + UI captures (VELLICHOR_SHOT, non-auto).
        if (_shot is null) return;
        if (++_frames < _shotFrame) return;
        var img = GetViewport()?.GetTexture()?.GetImage();
        if (img is null) { if (_frames < _shotFrame + 120) return; GD.Print("screenshot: viewport image unavailable"); GetTree().Quit(); return; }
        img.SavePng(_shot);
        GD.Print($"saved screenshot -> {_shot}");
        GetTree().Quit();
    }

    /// Physics tick: player movement + collision (MoveAndSlide) runs here so it's stable against the
    /// zone colliders. Camera follow is applied in the same call.
    public override void _PhysicsProcess(double delta)
    {
        var ws = ActiveState;
        if (ws is not null && _zoneNode is not null) ControlAndFollow(ws, delta);
    }

    /// Interactive mode sets AutoAcceptQuit=false so a window close comes here first: if we're in
    /// world, run the graceful ~40s logout (0x0E7) before quitting so the session isn't left stale.
    public override void _Notification(int what)
    {
        if (what == (int)NotificationWMCloseRequest) RequestQuit("window close");
    }

    /// Register terminal-signal handlers so Ctrl+C / SIGTERM / SIGHUP (and terminal close) also run
    /// the graceful logout + clean exit instead of coreCLR's default termination (which hits the
    /// Godot static-destructor abort and skips logout). ctx.Cancel stops the default; we defer the
    /// actual quit to the main thread via _readyToQuit so no Godot API is touched off-thread.
    private void RegisterSignals()
    {
        foreach (var sig in new[] { System.Runtime.InteropServices.PosixSignal.SIGINT,
                                    System.Runtime.InteropServices.PosixSignal.SIGTERM,
                                    System.Runtime.InteropServices.PosixSignal.SIGHUP })
        {
            System.Runtime.InteropServices.PosixSignalRegistration.Create(sig, ctx =>
            {
                ctx.Cancel = true;
                RequestQuit(ctx.Signal.ToString());
            });
        }
    }

    /// Single quit path (window close / Cmd+Q / signal): run the ~40s graceful logout off-thread,
    /// then _Process hard-exits when it's done. Idempotent.
    private void RequestQuit(string reason)
    {
        if (_quitting) return;
        _quitting = true;
        if (_bridge is { InWorld: true })
        {
            GD.Print($"[live] {reason} -> graceful logout (~40s); exits when done…");
            _hud?.StartLogoutCountdown(40); // on-screen countdown so the terminal isn't needed
            System.Threading.Tasks.Task.Run(() => { try { _bridge.Shutdown(); } catch { } _readyToQuit = true; });
        }
        else _readyToQuit = true; // nothing to log out — hard-exit next frame
    }

    /// Godot(.NET) on macOS aborts during C++ static-destructor teardown when a RENDERING session
    /// with background threads exits (`std::mutex::lock` throws `system_error` in __cxa_finalize) —
    /// that's the "Godot quit unexpectedly" dialog. The graceful logout has already completed by the
    /// time we call this, so terminate immediately with SIGKILL to skip the racy teardown entirely.
    private static void HardExit() => OS.Kill(OS.GetProcessId());

    private void LoadZone(System.Collections.Generic.IEnumerable<MeshData> meshes)
    {
        var mat = new StandardMaterial3D { AlbedoColor = new Color(0.7f, 0.7f, 0.72f) };
        foreach (var m in meshes)
            AddChild(ZoneRenderer.BuildMesh(m, mat));
    }

    /// First-run: no install auto-detected, so ask the user to point at their FINAL FANTASY XI folder.
    /// The chosen path is validated, saved to settings (via InstallLocator), and used to init the archive.
    private void PromptForInstall()
    {
        var dlg = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.OpenDir,
            Access = FileDialog.AccessEnum.Filesystem,
            Title = "Select your FINAL FANTASY XI installation folder",
            Size = new Vector2I(820, 560),
            Unresizable = false,
        };
        AddChild(dlg);
        dlg.DirSelected += dir =>
        {
            if (!Vellichor.Dat.InstallLocator.IsValidInstall(dir))
            {
                GD.PrintErr($"[install] '{dir}' has no ROM/VTABLE data — pick the FFXI folder that contains ROM/.");
                dlg.Popup(); // re-prompt
                return;
            }
            Vellichor.Dat.InstallLocator.SaveConfiguredPath(dir);
            _corpusDir = dir;
            _dat = new Vellichor.Dat.DatArchive(_corpusDir);
            GD.Print($"[install] FFXI data set: {_corpusDir} (saved to {Vellichor.Dat.InstallLocator.SettingsPath})");
            dlg.QueueFree();
        };
        dlg.Canceled += () => GD.PrintErr("[install] No installation selected — asset loading is disabled until one is set.");
        dlg.PopupCentered();
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

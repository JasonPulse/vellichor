using Vellichor.Dat;

// Headless probes for the DAT layer. Modes:
//   (default) index   validate file-id -> path resolution against a real install
//   scan  <ROMdir>     walk every N.DAT in a folder, histogram chunk types (find zones)
//   dump  <file.DAT>   list every chunk in one file (name/type/size)
//
// Root order for `index`: arg -> $VELLICHOR_DAT_ROOT -> built-in default.

const string DefaultRoot =
    "/Volumes/[C] Windows 11.hidden/Program Files (x86)/PlayOnline/SquareEnix/FINAL FANTASY XI";

string mode = args.Length > 0 && !Directory.Exists(args[0]) && !File.Exists(args[0]) ? args[0] : "index";

switch (mode)
{
    case "scan": return Scan(args.ElementAtOrDefault(1));
    case "dump": return Dump(args.ElementAtOrDefault(1));
    case "hunt": return Hunt(args.ElementAtOrDefault(1));
    case "hex": return Hex(args);
    case "mmb": return Mmb(args.ElementAtOrDefault(1));
    case "mzb": return Mzb(args.ElementAtOrDefault(1));
    default: return IndexValidate(args);
}

// ---- mode: index ----------------------------------------------------------
int IndexValidate(string[] a)
{
    string root = (a.Length > 0 && a[0] != "index" ? a[0] : null)
        ?? Environment.GetEnvironmentVariable("VELLICHOR_DAT_ROOT") ?? DefaultRoot;
    int maxId = a.Length > 1 && int.TryParse(a[1], out var m) ? m : 60000;
    if (!Directory.Exists(root)) { Console.Error.WriteLine($"!! root not found: {root}"); return 1; }

    var dat = new DatArchive(root);
    Console.WriteLine($"ROM sets: [{string.Join(", ", dat.LoadedSets)}]");
    int resolved = 0, exists = 0;
    for (int id = 0; id < maxId; id++)
    {
        var p = dat.ResolveFileId(id);
        if (p is null) continue;
        resolved++;
        if (File.Exists(p)) exists++;
    }
    Console.WriteLine($"resolved={resolved} exists={exists} rate={(resolved == 0 ? 0 : 100.0 * exists / resolved):0.00}%");
    return 0;
}

// ---- mode: scan -----------------------------------------------------------
int Scan(string? dir)
{
    if (dir is null || !Directory.Exists(dir)) { Console.Error.WriteLine("usage: scan <ROMdir>"); return 1; }
    var files = Directory.GetFiles(dir, "*.DAT")
        .OrderBy(f => int.TryParse(Path.GetFileNameWithoutExtension(f), out var n) ? n : int.MaxValue)
        .ToList();
    Console.WriteLine($"scanning {files.Count} DATs in {dir}\n");
    Console.WriteLine($"{"file",-10}{"chunks",-8}{"chunked",-9}zone?  types (hex:count)");
    foreach (var f in files)
    {
        byte[] data;
        try { data = File.ReadAllBytes(f); } catch { continue; }
        var chunks = ChunkReader.Walk(data);
        var hist = chunks.GroupBy(c => c.Type).OrderBy(g => g.Key)
            .Select(g => $"{g.Key:x2}:{g.Count()}");
        bool chunked = ChunkReader.LooksChunked(data);
        var types = chunks.Select(c => c.Type).ToHashSet();
        bool zoneish = types.Contains(0x1c) || types.Contains(0x2e) || types.Contains(0x20);
        Console.WriteLine($"{Path.GetFileName(f),-10}{chunks.Count,-8}{(chunked ? "yes" : "-"),-9}{(zoneish ? "ZONE" : ""),-7}{string.Join(" ", hist)}");
    }
    return 0;
}

// ---- mode: hunt -----------------------------------------------------------
// Discover where the zone terrain geometry lives, without assuming a chunk-type
// constant. Walks every DAT under a root, aggregates total BYTES per chunk type
// (geometry/textures dominate byte volume), and lists the largest chunked files as
// terrain candidates. Meant to run against a LOCAL copy (SMB would be far too slow).
int Hunt(string? root)
{
    if (root is null || !Directory.Exists(root)) { Console.Error.WriteLine("usage: hunt <ROMtreeRoot>"); return 1; }
    var files = Directory.EnumerateFiles(root, "*.DAT", SearchOption.AllDirectories).ToList();
    Console.WriteLine($"hunting {files.Count} DATs under {root}\n");

    var typeBytes = new Dictionary<int, long>();
    var typeCount = new Dictionary<int, long>();
    int chunked = 0, raw = 0;
    long withMMB = 0, withMZB = 0;
    var candidates = new List<(long size, string rel, int domType, string names)>();

    foreach (var f in files)
    {
        byte[] data;
        try { data = File.ReadAllBytes(f); } catch { continue; }
        var chunks = ChunkReader.Walk(data);
        if (chunks.Count == 0 || !ChunkReader.LooksChunked(data)) { raw++; continue; }
        chunked++;

        var byType = new Dictionary<int, long>();
        bool hasMmb = false, hasMzb = false;
        foreach (var c in chunks)
        {
            typeBytes[c.Type] = typeBytes.GetValueOrDefault(c.Type) + c.LengthBytes;
            typeCount[c.Type] = typeCount.GetValueOrDefault(c.Type) + 1;
            byType[c.Type] = byType.GetValueOrDefault(c.Type) + c.LengthBytes;
            if (c.Type == 0x2e) hasMmb = true;
            if (c.Type == 0x1c) hasMzb = true;
        }
        if (hasMmb) withMMB++;
        if (hasMzb) withMZB++;
        int dom = byType.OrderByDescending(kv => kv.Value).First().Key;
        candidates.Add((data.Length, f[(root.Length + 1)..], dom, string.Join(",", chunks.Take(5).Select(c => c.Name))));
    }

    Console.WriteLine($"chunked={chunked}  raw/other={raw}  files-with-MMB(0x2e)={withMMB}  files-with-MZB(0x1c)={withMZB}\n");
    Console.WriteLine("global chunk types by TOTAL BYTES (terrain geometry should dominate):");
    foreach (var kv in typeBytes.OrderByDescending(kv => kv.Value).Take(15))
        Console.WriteLine($"  0x{kv.Key:x2}  {kv.Value / 1024 / 1024,7} MB   {typeCount[kv.Key],7} chunks");

    Console.WriteLine("\nlargest chunked files (terrain candidates):");
    foreach (var c in candidates.OrderByDescending(c => c.size).Take(20))
        Console.WriteLine($"  {c.size / 1024,8} KB  dom=0x{c.domType:x2}  {c.rel}  [{c.names}]");
    return 0;
}

// ---- mode: mmb ------------------------------------------------------------
// Decode every MMB (0x2e) chunk in a zone file and report health: models, verts,
// tris, clean vs failed, and the overall position bounding box. Sane numbers +
// high clean-parse rate = the MMB layout is correct (before rendering).
int Mmb(string? file)
{
    if (file is null || !File.Exists(file)) { Console.Error.WriteLine("usage: mmb <file.DAT>"); return 1; }
    var data = File.ReadAllBytes(file);
    var chunks = ChunkReader.Walk(data).Where(c => c.Type == 0x2e).ToList();

    int okChunks = 0, failChunks = 0, models = 0, verts = 0, tris = 0;
    int spuriousTris = 0, badNormals = 0;
    float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
    float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
    var sampleDiags = new List<string>();

    foreach (var c in chunks)
    {
        var payload = data.AsSpan(c.PayloadOffset, c.PayloadLength).ToArray();
        var m = MmbDecoder.Decode(payload);
        if (m.Ok) okChunks++; else { failChunks++; if (sampleDiags.Count < 8) sampleDiags.Add($"{m.MmbId}: {m.Diag}"); }
        foreach (var mesh in m.Meshes)
        {
            models++; verts += mesh.VertexCount; tris += mesh.TriangleCount;
            // per-mesh bbox diagonal (a legit triangle can't have an edge longer than this)
            float mnx = float.MaxValue, mny = float.MaxValue, mnz = float.MaxValue;
            float mxx = float.MinValue, mxy = float.MinValue, mxz = float.MinValue;
            for (int v = 0; v < mesh.VertexCount; v++)
            {
                float x = mesh.Positions[v * 3], y = mesh.Positions[v * 3 + 1], z = mesh.Positions[v * 3 + 2];
                if (x < minX) minX = x; if (y < minY) minY = y; if (z < minZ) minZ = z;
                if (x > maxX) maxX = x; if (y > maxY) maxY = y; if (z > maxZ) maxZ = z;
                if (x < mnx) mnx = x; if (y < mny) mny = y; if (z < mnz) mnz = z;
                if (x > mxx) mxx = x; if (y > mxy) mxy = y; if (z > mxz) mxz = z;
            }
            double diag = Math.Sqrt((mxx-mnx)*(mxx-mnx) + (mxy-mny)*(mxy-mny) + (mxz-mnz)*(mxz-mnz)) + 1e-3;
            // bad normals (zero / non-finite)
            if (mesh.Normals is { } nrm)
                for (int v = 0; v < mesh.VertexCount; v++)
                {
                    float nx = nrm[v*3], ny = nrm[v*3+1], nz = nrm[v*3+2];
                    if (!float.IsFinite(nx+ny+nz) || (nx*nx+ny*ny+nz*nz) < 1e-6f) badNormals++;
                }
            // spurious triangles: any edge longer than the mesh itself
            var idx = mesh.Indices;
            for (int t = 0; t + 2 < idx.Length; t += 3)
                if (EdgeLen(mesh.Positions, idx[t], idx[t+1]) > diag ||
                    EdgeLen(mesh.Positions, idx[t+1], idx[t+2]) > diag ||
                    EdgeLen(mesh.Positions, idx[t], idx[t+2]) > diag)
                    spuriousTris++;
        }
    }

    Console.WriteLine($"MMB chunks   : {chunks.Count}  (clean={okChunks} failed={failChunks})");
    Console.WriteLine($"models       : {models}");
    Console.WriteLine($"vertices     : {verts:N0}");
    Console.WriteLine($"triangles    : {tris:N0}");
    Console.WriteLine($"spurious tris: {spuriousTris:N0}   (edge longer than the model itself = strip bridge artifact)");
    Console.WriteLine($"bad normals  : {badNormals:N0}   (zero/NaN → renders black)");
    if (verts > 0)
        Console.WriteLine($"position AABB: X[{minX:0.#}..{maxX:0.#}] Y[{minY:0.#}..{maxY:0.#}] Z[{minZ:0.#}..{maxZ:0.#}]");
    if (sampleDiags.Count > 0) { Console.WriteLine("failure samples:"); sampleDiags.ForEach(d => Console.WriteLine("  " + d)); }
    return 0;
}

static double EdgeLen(float[] p, int a, int b)
{
    double dx = p[a*3] - p[b*3], dy = p[a*3+1] - p[b*3+1], dz = p[a*3+2] - p[b*3+2];
    return Math.Sqrt(dx*dx + dy*dy + dz*dz);
}

// ---- mode: mzb ------------------------------------------------------------
int Mzb(string? file)
{
    if (file is null || !File.Exists(file)) { Console.Error.WriteLine("usage: mzb <file.DAT>"); return 1; }
    var data = File.ReadAllBytes(file);
    var mzbChunk = ChunkReader.Walk(data).FirstOrDefault(c => c.Type == 0x1c);
    if (mzbChunk.LengthBytes == 0) { Console.Error.WriteLine("no MZB (0x1c) chunk"); return 1; }
    var payload = data.AsSpan(mzbChunk.PayloadOffset, mzbChunk.PayloadLength).ToArray();
    var insts = MzbDecoder.Decode(payload); // decrypts payload in place
    // The MZB header may hold offsets to OTHER record arrays (terrain grid) beyond the
    // SMZBBlock100 object list. Dump the header + what follows the object list to find them.
    Console.WriteLine($"payload len  : {payload.Length:N0}");
    Console.WriteLine("header (decrypted, first 48 bytes as u32 LE):");
    for (int o = 0; o + 4 <= 48; o += 4)
        Console.WriteLine($"  +0x{o:x2} = {BitConverter.ToUInt32(payload, o),12}  (0x{BitConverter.ToUInt32(payload, o):x8})");
    int objEnd = 32 + insts.Count * 100;
    Console.WriteLine($"object list ends at 0x{objEnd:x} ({objEnd:N0}); {payload.Length - objEnd:N0} bytes remain after it");
    if (objEnd + 32 <= payload.Length)
    {
        Console.Write("  bytes after object list: ");
        for (int i = 0; i < 32; i++) Console.Write($"{payload[objEnd + i]:x2} ");
        Console.WriteLine();
    }

    Console.WriteLine($"MZB instances: {insts.Count}");
    Console.WriteLine($"unique ids   : {insts.Select(i => i.Id).Distinct().Count()}");
    // Outliers: extreme/degenerate transforms that could stretch one model across the view.
    float maxAbsScale = insts.Count == 0 ? 0 : insts.Max(i => Math.Max(Math.Abs(i.ScaleX), Math.Max(Math.Abs(i.ScaleY), Math.Abs(i.ScaleZ))));
    int nonFinite = insts.Count(i => !float.IsFinite(i.PosX+i.PosY+i.PosZ+i.RotX+i.RotY+i.RotZ+i.ScaleX+i.ScaleY+i.ScaleZ));
    int bigScale = insts.Count(i => Math.Abs(i.ScaleX) > 4 || Math.Abs(i.ScaleY) > 4 || Math.Abs(i.ScaleZ) > 4);
    int negScale = insts.Count(i => i.ScaleX * i.ScaleY * i.ScaleZ < 0);
    Console.WriteLine($"max |scale|  : {maxAbsScale:0.##}   non-finite xf: {nonFinite}   |scale|>4: {bigScale}   neg-scale (mirrored): {negScale}");

    // Cross-reference against the MMB ids present in this same file to see what's unresolved.
    var mmbIds = new HashSet<string>();
    foreach (var c in ChunkReader.Walk(data).Where(c => c.Type == 0x2e))
        mmbIds.Add(MmbDecoder.Decode(data.AsSpan(c.PayloadOffset, c.PayloadLength).ToArray()).MmbId);
    var unresolved = insts.Where(i => !mmbIds.Contains(i.Id)).ToList();
    Console.WriteLine($"unresolved   : {unresolved.Count} placements, {unresolved.Select(i => i.Id).Distinct().Count()} distinct ids");
    Console.WriteLine("  unresolved id samples: " + string.Join(", ",
        unresolved.Select(i => i.Id).Distinct().Take(12).Select(s => $"'{s}'")));
    if (insts.Count > 0)
    {
        Console.WriteLine($"pos AABB     : X[{insts.Min(i => i.PosX):0.#}..{insts.Max(i => i.PosX):0.#}] " +
                          $"Y[{insts.Min(i => i.PosY):0.#}..{insts.Max(i => i.PosY):0.#}] " +
                          $"Z[{insts.Min(i => i.PosZ):0.#}..{insts.Max(i => i.PosZ):0.#}]");
        Console.WriteLine("samples:");
        foreach (var i in insts.Take(6))
            Console.WriteLine($"  id='{i.Id}' pos=({i.PosX:0.#},{i.PosY:0.#},{i.PosZ:0.#}) " +
                              $"rot=({i.RotX:0.##},{i.RotY:0.##},{i.RotZ:0.##}) scale=({i.ScaleX:0.##},{i.ScaleY:0.##},{i.ScaleZ:0.##})");
    }
    return 0;
}

// ---- mode: hex ------------------------------------------------------------
// hex <file> <typeHex> <which> [payloadBytes] — dump the payload head of the
// `which`-th chunk of a given type, as hex + ascii, to eyeball a raw structure.
int Hex(string[] a)
{
    if (a.Length < 4) { Console.Error.WriteLine("usage: hex <file> <typeHex> <which> [bytes]"); return 1; }
    string file = a[1];
    int wantType = Convert.ToInt32(a[2], 16);
    int which = int.Parse(a[3]);
    int nBytes = a.Length > 4 ? int.Parse(a[4]) : 96;
    if (!File.Exists(file)) { Console.Error.WriteLine("no such file"); return 1; }

    var data = File.ReadAllBytes(file);
    var chunks = ChunkReader.Walk(data);
    var matches = chunks.Where(c => c.Type == wantType).ToList();
    if (which >= matches.Count) { Console.Error.WriteLine($"only {matches.Count} chunks of type 0x{wantType:x2}"); return 1; }
    var ch = matches[which];
    Console.WriteLine($"chunk '{ch.Name}' type=0x{ch.Type:x2} len={ch.LengthBytes} payload@{ch.PayloadOffset} payloadLen={ch.PayloadLength}");
    int start = ch.PayloadOffset, count = Math.Min(nBytes, ch.PayloadLength);
    for (int row = 0; row < count; row += 16)
    {
        var hex = new System.Text.StringBuilder();
        var asc = new System.Text.StringBuilder();
        for (int i = 0; i < 16 && row + i < count; i++)
        {
            byte b = data[start + row + i];
            hex.Append($"{b:x2} ");
            asc.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
        }
        Console.WriteLine($"  +{row,-4:D4} {hex,-48} {asc}");
    }
    // Also interpret a few little-endian scalars at the front to help spot counts.
    Console.WriteLine($"  as LE: i16@+0={BitConverter.ToInt16(data, start)}  " +
                      $"i16@+16={BitConverter.ToInt16(data, start + 16)}  " +
                      $"i32@+16={BitConverter.ToInt32(data, start + 16)}  " +
                      $"f32@+20={BitConverter.ToSingle(data, start + 20):0.###}");
    return 0;
}

// ---- mode: dump -----------------------------------------------------------
int Dump(string? file)
{
    if (file is null || !File.Exists(file)) { Console.Error.WriteLine("usage: dump <file.DAT>"); return 1; }
    var data = File.ReadAllBytes(file);
    var chunks = ChunkReader.Walk(data);
    Console.WriteLine($"{file}  ({data.Length} bytes)  chunked={ChunkReader.LooksChunked(data)}");
    Console.WriteLine($"{"#",-5}{"name",-8}{"type",-8}{"bytes",-10}offset");
    for (int i = 0; i < chunks.Count; i++)
    {
        var c = chunks[i];
        Console.WriteLine($"{i,-5}{c.Name,-8}0x{c.Type:x2}    {c.LengthBytes,-10}{c.Offset}");
    }
    return 0;
}

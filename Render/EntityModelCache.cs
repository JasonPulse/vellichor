using System.Collections.Generic;
using System.IO;
using Vellichor.Dat;
using XiHeadless.Game;

namespace Vellichor.Render;

/// <summary>
/// Resolves a live entity's network "look" to a decoded, instanceable <see cref="CharacterModel"/>,
/// caching one decode per resolved model so a zone full of the same mob/gear decodes once. Uses
/// <see cref="ModelResolver"/> (community model tables) to turn a look into ROM DAT paths:
/// - monster / simple NPC (type 0): one self-contained DAT (skeleton+mesh+anim) -> CharacterModel.Decode.
/// - PC / humanoid NPC (type 1): race skeleton + per-slot equipment/face parts -> DecodeAssembled.
/// Falls back to null (=> capsule) when a look can't be resolved. VELLICHOR_FORCE_MODEL forces one DAT
/// onto every entity for pipeline validation.
/// </summary>
public sealed class EntityModelCache
{
    private readonly DatArchive? _dat;
    private readonly string _corpusDir;
    private readonly string? _forcePath;
    private readonly ModelResolver _resolver;
    private readonly Dictionary<string, CharacterModel?> _cache = new();

    /// Monster fallback: when name lookup misses, try file id (creature band base + wire ModelId).
    private readonly int _mobBase =
        int.TryParse(System.Environment.GetEnvironmentVariable("VELLICHOR_MOB_BASE"), out var mb) ? mb : 52795;

    public bool HasForce => _forcePath is not null;

    public EntityModelCache(DatArchive? dat, string corpusDir, string dataDir)
    {
        _dat = dat;
        _corpusDir = corpusDir;
        _resolver = new ModelResolver(corpusDir, dataDir);
        var force = System.Environment.GetEnvironmentVariable("VELLICHOR_FORCE_MODEL");
        if (!string.IsNullOrEmpty(force))
            _forcePath = Path.Combine(corpusDir, force.Replace('/', Path.DirectorySeparatorChar));
    }

    /// Decoded model for this entity, or null (=> capsule). Cached by a key derived from the resolution.
    public CharacterModel? Get(in EntityLook look, string name)
    {
        if (_forcePath is not null) return SelfContained("force:" + _forcePath, _forcePath);
        if (!look.Known) return null;

        // Equipped humanoid (PC / dressed NPC): assemble skeleton + parts.
        if (look.Type == 1 && look.Race is >= 1 and <= 8)
        {
            var recipe = _resolver.PcRecipe(look);
            if (recipe is { } r)
            {
                string key = "p:" + r.skeleton + "|" + string.Join(',', r.parts);
                if (_cache.TryGetValue(key, out var pc)) return pc;
                CharacterModel? model = null;
                try
                {
                    var skelBytes = File.ReadAllBytes(r.skeleton);
                    var partBytes = new List<byte[]>(r.parts.Count);
                    foreach (var p in r.parts) partBytes.Add(File.ReadAllBytes(p));
                    model = CharacterModel.DecodeAssembled(skelBytes, partBytes);
                }
                catch { }
                _cache[key] = model;
                return model;
            }
            return null;
        }

        // Standard / monster (type 0): self-contained DAT, resolved by name then by creature-band file id.
        string? path = _resolver.MonsterPath(name);
        if (path is null && _dat is not null)
        {
            var byId = _dat.ResolveFileId(_mobBase + look.ModelId);
            if (byId is not null && File.Exists(byId)) path = byId;
        }
        return path is null ? null : SelfContained("m:" + path, path);
    }

    private CharacterModel? SelfContained(string key, string path)
    {
        if (_cache.TryGetValue(key, out var cached)) return cached;
        CharacterModel? model = null;
        try { if (File.Exists(path)) model = CharacterModel.Decode(File.ReadAllBytes(path)); } catch { }
        _cache[key] = model;
        return model;
    }
}

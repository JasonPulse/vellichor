using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Vellichor.UI.Menus;

/// <summary>
/// Loads the client's static game tables (spell / ability / item names + item details) once, into
/// fast lookups the menus share. Data files live in res://data (converted from the decoded DATs).
/// </summary>
public static class GameData
{
    public readonly record struct ItemInfo(string Name, string Desc, string Type, int Stack, string Level);

    public static readonly Dictionary<ushort, string> Spells = new();
    public static readonly Dictionary<ushort, string> Abilities = new();
    public static readonly Dictionary<ushort, ItemInfo> Items = new();

    private static bool _loaded;

    public static void Load(string dataDir)
    {
        if (_loaded) return;
        _loaded = true;
        LoadNames(Path.Combine(dataDir, "spell_names.json"), Spells);
        LoadNames(Path.Combine(dataDir, "ability_names.json"), Abilities);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dataDir, "item_details.json")));
            foreach (var kv in doc.RootElement.EnumerateObject())
            {
                if (!ushort.TryParse(kv.Name, out var id)) continue;
                var a = kv.Value; // [name, desc, type, stack, level]
                int len = a.GetArrayLength();
                string Str(int i) => len > i && a[i].ValueKind == JsonValueKind.String ? a[i].GetString() ?? "" : "";
                string LevelStr() => len > 4 ? (a[4].ValueKind == JsonValueKind.Number ? a[4].GetInt32().ToString() : a[4].GetString() ?? "") : "";
                int stack = len > 3 && a[3].ValueKind == JsonValueKind.Number ? a[3].GetInt32() : 1;
                Items[id] = new ItemInfo(Str(0), Str(1), Str(2), stack, LevelStr());
            }
        }
        catch { }
    }

    public static string ItemName(ushort id) => Items.TryGetValue(id, out var i) ? i.Name : $"item#{id}";

    private static void LoadNames(string path, Dictionary<ushort, string> into)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var kv in doc.RootElement.EnumerateObject())
                if (ushort.TryParse(kv.Name, out var id) && kv.Value.GetString() is { } n) into[id] = n;
        }
        catch { }
    }

    // FFXI job ids -> abbreviation (0 = none/monster).
    private static readonly string[] JobAbbr =
    {
        "---","WAR","MNK","WHM","BLM","RDM","THF","PLD","DRK","BST","BRD","RNG","SAM","NIN","DRG","SMN",
        "BLU","COR","PUP","DNC","SCH","GEO","RUN",
    };
    public static string Job(int id) => id >= 0 && id < JobAbbr.Length ? JobAbbr[id] : $"job{id}";
}

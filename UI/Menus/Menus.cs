using System.Collections.Generic;
using System.Linq;
using Vellichor.Render;
using XiHeadless.Game;

namespace Vellichor.UI.Menus;

/// Top-level menu — the retail main menu structure. Each row drills into a submenu.
public partial class MainMenu : MenuPanel
{
    private readonly MenuContext _ctx;
    public MainMenu(MenuContext ctx) { _ctx = ctx; TitleText = "Menu"; }
    protected override void Populate() => SetEntries(new[]
    {
        new MenuEntry("Magic",     () => Manager.Open(new MagicMenu(_ctx))),
        new MenuEntry("Abilities", () => Manager.Open(new AbilitiesMenu(_ctx))),
        new MenuEntry("Items",     () => Manager.Open(new ItemsMenu(_ctx))),
        new MenuEntry("Equipment", () => Manager.Open(new EquipmentMenu(_ctx))),
        new MenuEntry("Status",    () => Manager.Open(new StatusMenu(_ctx))),
        new MenuEntry("Logout",    () => { Manager.CloseAll(); _ctx.Logout(); }),
    });
}

/// Known spells only (filtered by the 0x0AA learned-spell bitmap), alphabetical; cast on confirm.
public partial class MagicMenu : MenuPanel
{
    private readonly MenuContext _ctx;
    public MagicMenu(MenuContext ctx) { _ctx = ctx; TitleText = "Magic"; }
    protected override void Populate()
    {
        var ws = _ctx.State();
        var list = new List<MenuEntry>();
        if (ws is not null)
            foreach (var kv in GameData.Spells)
            {
                if (!ws.KnowsSpell(kv.Key)) continue;
                ushort id = kv.Key;
                list.Add(new MenuEntry(kv.Value, () => { _ctx.CastSpell(id); Manager.CloseAll(); }));
            }
        list.Sort((a, b) => string.CompareOrdinal(a.Label, b.Label));
        if (list.Count == 0)
            list.Add(new MenuEntry("(no spells known — cast a spell in-game to populate)").Off());
        SetEntries(list);
        SetTitle($"Magic  ({list.Count(e => e.Enabled)} known)");
    }
}

/// Job abilities + weapon skills. The server doesn't send a "known abilities" list the way it does spells,
/// so this is a placeholder until we infer them from job/level; keeps the full menu structure in place.
public partial class AbilitiesMenu : MenuPanel
{
    private readonly MenuContext _ctx;
    public AbilitiesMenu(MenuContext ctx) { _ctx = ctx; TitleText = "Abilities"; }
    protected override void Populate() => SetEntries(new[]
    {
        new MenuEntry("(job abilities / weapon skills — not yet wired)").Off(),
    });
}

/// Weapon skills for the equipped weapon at the current TP. Needs the equipped-weapon skill list (from the
/// equip packet) to populate for real; structural for now so Ctrl+W matches retail.
public partial class WeaponSkillsMenu : MenuPanel
{
    private readonly MenuContext _ctx;
    public WeaponSkillsMenu(MenuContext ctx) { _ctx = ctx; TitleText = "Weapon Skills"; }
    protected override void Populate()
    {
        var ws = _ctx.State();
        int tp = (int)(ws?.Tp ?? 0);
        var list = new List<MenuEntry>
        {
            new MenuEntry($"TP: {tp}" + (tp < 1000 ? "  (need 1000+)" : "  ready")).Off(),
            new MenuEntry("(weapon skills — populate from equipped weapon, not yet wired)").Off(),
        };
        SetEntries(list);
        SetTitle("Weapon Skills");
    }
}

/// Inventory (main bag) with a live detail pane: name, type, level, stack, and the item description.
public partial class ItemsMenu : MenuPanel
{
    private readonly MenuContext _ctx;
    public ItemsMenu(MenuContext ctx) { _ctx = ctx; TitleText = "Items"; }
    protected override bool UseDetail => true;
    protected override void Populate()
    {
        var ws = _ctx.State();
        var list = new List<MenuEntry>();
        if (ws is not null)
            try
            {
                foreach (var kv in ws.Inventory)
                {
                    if (kv.Key.container != 0 || kv.Value == 0) continue;
                    ushort itemId = kv.Value;
                    int qty = ws.InventoryQty.TryGetValue(kv.Key, out var q) ? q : 1;
                    var info = GameData.Items.TryGetValue(itemId, out var ii)
                        ? ii : new GameData.ItemInfo($"item#{itemId}", "", "", 1, "");
                    string label = qty > 1 ? $"{info.Name}  x{qty}" : info.Name;
                    list.Add(new MenuEntry(label, null, ItemDetail(info, qty)));
                }
            }
            catch { }
        if (list.Count == 0)
            list.Add(new MenuEntry("(inventory empty / not yet received)").Off());
        SetEntries(list);
        SetTitle($"Items  ({list.Count(e => e.Enabled)})");
    }

    private static string ItemDetail(GameData.ItemInfo i, int qty)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"[b]{i.Name}[/b]\n");
        var meta = new List<string>();
        if (!string.IsNullOrEmpty(i.Type)) meta.Add(i.Type);
        if (!string.IsNullOrEmpty(i.Level) && i.Level != "0") meta.Add($"Lv{i.Level}");
        if (i.Stack > 1) meta.Add($"stacks to {i.Stack}");
        meta.Add($"have {qty}");
        sb.Append($"[color=#aaa]{string.Join("  ·  ", meta)}[/color]\n\n");
        if (!string.IsNullOrEmpty(i.Desc)) sb.Append(i.Desc);
        return sb.ToString();
    }
}

/// The eight equipment slots and the model id currently worn (item-id resolution needs the 0x050 equip
/// list — shown as model ids for now).
public partial class EquipmentMenu : MenuPanel
{
    private readonly MenuContext _ctx;
    public EquipmentMenu(MenuContext ctx) { _ctx = ctx; TitleText = "Equipment"; }
    protected override void Populate()
    {
        var ws = _ctx.State();
        var list = new List<MenuEntry>();
        if (ws is not null && ws.MyLook.Known)
        {
            var l = ws.MyLook;
            void Row(string slot, int id) => list.Add(new MenuEntry($"{slot,-8} {(id == 0 ? "—" : id.ToString())}").Off());
            Row("Head", l.Head & 0x0FFF); Row("Body", l.Body & 0x0FFF); Row("Hands", l.Hands & 0x0FFF);
            Row("Legs", l.Legs & 0x0FFF); Row("Feet", l.Feet & 0x0FFF);
            Row("Main", l.Main & 0x0FFF); Row("Sub", l.Sub & 0x0FFF); Row("Ranged", l.Ranged & 0x0FFF);
        }
        else list.Add(new MenuEntry("(appearance not yet received)").Off());
        SetEntries(list);
        SetTitle("Equipment");
    }
}

/// Character status: name, job/level, vitals, gil, location.
public partial class StatusMenu : MenuPanel
{
    private readonly MenuContext _ctx;
    public StatusMenu(MenuContext ctx) { _ctx = ctx; TitleText = "Status"; }
    protected override void Populate()
    {
        var ws = _ctx.State();
        var list = new List<MenuEntry>();
        if (ws is not null)
        {
            string job = $"{GameData.Job(ws.MainJob)}{ws.MainJobLevel}" +
                         (ws.SubJob > 0 ? $" / {GameData.Job(ws.SubJob)}{ws.SubJobLevel}" : "");
            string zone = ZoneCatalog.NameFor(ws.ZoneId) ?? (ws.ZoneId > 0 ? $"Zone {ws.ZoneId}" : "—");
            list.Add(new MenuEntry($"Name:  {ws.MyName}").Off());
            list.Add(new MenuEntry($"Job:   {job}").Off());
            list.Add(new MenuEntry($"HP:    {ws.Hp} / {ws.MaxHp}").Off());
            list.Add(new MenuEntry($"MP:    {ws.Mp} / {ws.MaxMp}").Off());
            list.Add(new MenuEntry($"TP:    {ws.Tp}").Off());
            list.Add(new MenuEntry($"Gil:   {ws.Gil:n0}").Off());
            list.Add(new MenuEntry($"Zone:  {zone}").Off());
            list.Add(new MenuEntry($"Pos:   ({ws.X:0}, {ws.Y:0}, {ws.Z:0})").Off());
        }
        SetEntries(list);
        SetTitle("Status");
    }
}

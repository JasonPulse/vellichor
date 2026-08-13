using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using XiHeadless.Net;
using XiHeadless.Game;

namespace Vellichor.Net;

/// <summary>
/// Live server bridge over the shared XiProtocol stack, split into UI-drivable steps:
///   1. <see cref="LoginAndListAsync"/> — TLS login + lobby, then list the account's characters.
///   2. <see cref="EnterWorldAsync"/> — select ONE existing character and zone in; the packet loop
///      then keeps <see cref="State"/> (WorldState) tracking live entities for the renderer.
/// <see cref="ConnectAsync"/> chains both for the headless/env-var path (auto-picks the char).
///
/// SAFETY (XiHeadless/ACCOUNTS.md + CLAUDE.md): only ever selects EXISTING characters (GetCharacters
/// returns named slots; there is NO create path here). <see cref="Shutdown"/> sends 0x0E7 and holds
/// ~40s for a clean logout — always call it before exit, then wait before reconnecting. A FAILED
/// zone-in leaves a non-clean session (needs ~5 min to clear), so EnterWorld defaults to ONE attempt.
/// </summary>
public sealed class EntityBridge
{
    private XiClient? _client;
    private MapConnection? _conn;
    private string _resDir = "";

    public WorldState? State => _conn?.State;
    public string Status { get; private set; } = "idle";
    public bool InWorld => _conn is not null;
    public IReadOnlyList<XiClient.CharSlot> Characters { get; private set; } = Array.Empty<XiClient.CharSlot>();

    /// Step 1 — TLS login + lobby handshake, then read the account's character list into
    /// <see cref="Characters"/>. Returns false (with a reason in <see cref="Status"/>) on any
    /// failure or an account with no characters.
    public async Task<bool> LoginAndListAsync(string host, string clientVer, string account, string password, string resDir)
    {
        try
        {
            _resDir = resDir;
            Status = "logging in (TLS)…";
            _client = new XiClient(host, clientVer);
            await _client.LoginAsync(account, password);
            Status = "lobby handshake…";
            _client.LobbyDataConnect();
            _client.LobbyView_0x26();
            _client.LobbyView_0x1F();
            Status = "fetching characters…";
            Characters = _client.GetCharacters();
            if (Characters.Count == 0)
            {
                Status = $"account '{account}' has no characters (this client never creates one)";
                return false;
            }
            Status = $"{Characters.Count} character(s) on '{account}'";
            return true;
        }
        catch (Exception ex)
        {
            Status = $"login failed: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// Step 2 — select an EXISTING character and zone in. On success the receive loop runs and
    /// <see cref="State"/> tracks live entities. attempts &gt; 1 re-tries the whole handoff after a
    /// wait (only safe for a transient map restart — a stuck session needs ~5 min, so default 1).
    public async Task<bool> EnterWorldAsync(XiClient.CharSlot ch, int attempts = 1, int retryDelayMs = 75_000)
    {
        if (_client is null) { Status = "not logged in"; return false; }
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                _client.SelectCharacter(ch.Id, ch.Name);
                _client.RequestZoneServer(); // 0xA2 — throws on a stale/duplicate session
                _conn = new MapConnection(_client.MapServer, _client.CharId, new byte[20], _resDir);
                _conn.State.MyName = _client.CharName;
                Status = $"zoning in as '{ch.Name}'…";
                if (_conn.ZoneInSync()) // map-server 0x0A handshake
                {
                    _conn.Start(); // receive loop -> populates WorldState
                    Status = $"IN ZONE {_conn.State.ZoneId} as '{ch.Name}'";
                    return true;
                }
                Status = $"map zone-in not received for '{ch.Name}' — server didn't answer";
            }
            catch (Exception ex)
            {
                Status = $"enter world failed: {ex.GetType().Name}: {ex.Message}";
            }
            if (attempt < attempts)
            {
                Status += $" (attempt {attempt}/{attempts}, waiting {retryDelayMs / 1000}s)";
                try { _conn?.Stop(); } catch { }
                _conn = null;
                await Task.Delay(retryDelayMs);
            }
        }
        return false;
    }

    /// Headless/env-var path: login, auto-pick the highest-id existing character, enter world.
    public async Task ConnectAsync(string host, string clientVer, string account, string password, string resDir, int attempts = 1)
    {
        if (!await LoginAndListAsync(host, clientVer, account, password, resDir)) return;
        var best = Characters[0];
        foreach (var c in Characters) if (c.Id >= best.Id) best = c; // mirror TrySelectBest
        await EnterWorldAsync(best, attempts);
    }

    /// Send a chat message on a channel (0=say, 1=shout, 4=party). 0x0B5 GP_CLI_COMMAND_MESSAGE:
    /// hdr(4) + type@4 + text@6 (null-terminated, word-padded). Uses the shared SubPacket + the map
    /// connection's Enqueue. (Auto-translate {tokens} not encoded here — plain ASCII.)
    public void SendChat(byte mode, string msg)
    {
        if (_conn is null || string.IsNullOrWhiteSpace(msg)) return;
        var text = System.Text.Encoding.ASCII.GetBytes(msg);
        var p = new byte[(6 + text.Length + 1 + 3) & ~3];
        SubPacket.WriteHeader(p, 0x0B5);
        p[4] = mode;
        text.CopyTo(p, 6);
        _conn.Enqueue(p);
    }

    // Combat action categories for the 0x01A action packet (server resolves the target by its ActIndex).
    public const ushort ActEngage = 0x02, ActCastMagic = 0x03, ActDisengage = 0x04, ActWeaponskill = 0x07, ActJobAbility = 0x09, ActShoot = 0x10;

    /// Send a 0x01A action: hdr(4) UniqueNo(target)@4 ActIndex@8 category@10 param@12 (SpellId/WS/ability id).
    /// This is the client ACTING (engage/attack, WS, cast, ability) — a normal player action, not a position write.
    public void SendAction(ushort category, uint targetId, ushort targetIndex, uint param = 0)
    {
        if (_conn is null || targetId == 0) return;
        var p = new byte[28];
        SubPacket.WriteHeader(p, 0x01A);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), targetId);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(8), targetIndex);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(10), category);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(12), param);
        _conn.Enqueue(p);
    }

    /// Engage (start auto-attacking) the target. AttackOff (0x04) disengages.
    public void EngageTarget(uint targetId, ushort targetIndex, bool engage = true)
        => SendAction(engage ? ActEngage : ActDisengage, targetId, targetIndex);

    /// /heal — toggle the resting stance (HP+MP regen): 0x0E8 hdr(4) mode@4 (0=toggle, 1=on, 2=off). The
    /// server refuses it while engaged. A normal player action.
    public void SendRest(uint mode = 0)
    {
        if (_conn is null) return;
        var p = new byte[8];
        SubPacket.WriteHeader(p, 0x0E8);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), mode);
        _conn.Enqueue(p);
    }

    /// /check (con) the target: 0x0DD hdr(4) mobId@4 mobIndex@8 kind@12=0. The server replies with a 0x029
    /// carrying the mob's level + difficulty, which PacketParsers records into WorldState.ConMobLevel/ConDifficulty
    /// (it only captures a reply while ConTargetId is set + ConDifficulty<0, so the caller arms those first).
    public void SendCheck(uint targetId, uint targetIndex)
    {
        if (_conn is null || targetId == 0) return;
        var p = new byte[16];
        SubPacket.WriteHeader(p, 0x0DD);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), targetId);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8), targetIndex);
        p[12] = 0x00; // Kind = Check
        _conn.Enqueue(p);
    }

    /// Graceful logout (0x0E7, ~40s server-side). MUST run before process exit.
    public void Shutdown()
    {
        if (_conn is null) return;
        Status = "graceful logout (0x0E7, ~40s)…";
        try { _conn.Stop(); } catch { }
        _conn = null;
        Status = "logged out";
    }
}

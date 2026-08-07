using System;
using System.Threading.Tasks;
using XiHeadless.Net;
using XiHeadless.Game;

namespace Vellichor.Net;

/// <summary>
/// Live server bridge: logs in to the LSB server via the shared XiProtocol stack, selects the
/// account's EXISTING character (never creates), zones in, and runs the packet loop so
/// <see cref="State"/> (WorldState) tracks live entities for the renderer to draw.
///
/// SAFETY (matches XiHeadless/ACCOUNTS.md + CLAUDE.md rules):
///  - uses SelectExisting() — aborts if the account has no char; NEVER provisions one;
///  - Shutdown() calls MapConnection.Stop(), which sends 0x0E7 and holds ~40s for the server to
///    complete a clean logout — always call it before the process exits, then wait before any
///    reconnect, to avoid stale-session / junk-char problems.
/// </summary>
public sealed class EntityBridge
{
    private MapConnection? _conn;
    public WorldState? State => _conn?.State;
    public string Status { get; private set; } = "idle";
    public bool Connected => _conn is not null;

    public async Task ConnectAsync(string host, string clientVer, string account, string password, string resDir)
    {
        try
        {
            Status = "resolving host";
            var client = new XiClient(host, clientVer);
            Status = "login (TLS 0x54231)";
            await client.LoginAsync(account, password);
            Status = "lobby data connect";
            client.LobbyDataConnect();
            Status = "lobby view 0x26";
            client.LobbyView_0x26();
            Status = "lobby view 0x1F";
            client.LobbyView_0x1F();
            Status = "fetch char list";
            client.InitialCharList();

            Status = "select existing char";
            if (!client.SelectExisting())
            {
                Status = $"account '{account}' has NO character — aborting (will not create one)";
                return;
            }

            client.RequestZoneServer(); // 0xA2 — throws on a stale/duplicate session
            _conn = new MapConnection(client.MapServer, client.CharId, new byte[20], resDir);
            _conn.State.MyName = client.CharName;
            _conn.ZoneInSync();
            _conn.Start();
            Status = $"in zone {_conn.State.ZoneId} as '{client.CharName}' (id {client.CharId})";
        }
        catch (Exception ex)
        {
            Status = $"connect failed: {ex.GetType().Name}: {ex.Message}";
            try { _conn?.Stop(); } catch { }
            _conn = null;
        }
    }

    /// Graceful logout (0x0E7, ~40s server-side). MUST run before process exit.
    public void Shutdown()
    {
        if (_conn is null) return;
        Status = "graceful logout (0x0E7, ~40s)...";
        try { _conn.Stop(); } catch { }
        _conn = null;
        Status = "logged out";
    }
}

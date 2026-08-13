using System;
using System.Threading.Tasks;
using Godot;
using Vellichor.Net;

namespace Vellichor.UI;

/// <summary>
/// Front-end for the live client: a LOGIN panel (server / account / password) → a CHARACTER-SELECT
/// panel (the account's existing characters — never a create option) → enter world. Drives
/// <see cref="EntityBridge"/> on background tasks and reflects <c>bridge.Status</c> live.
///
/// On a successful zone-in it fires <see cref="EnteredWorld"/> (with the chosen character name) and
/// frees itself, handing the screen to the 3D world. Async bridge calls run off-thread; results are
/// picked up on the main thread in <see cref="_Process"/> (Godot nodes are single-threaded).
/// </summary>
public partial class ClientUi : CanvasLayer
{
    public event Action? EnteredWorld;

    private readonly EntityBridge _bridge;
    private readonly string _host, _clientVer, _resDir;

    private LineEdit _account = null!, _password = null!, _server = null!;
    private Button _connectBtn = null!, _enterBtn = null!, _backBtn = null!;
    private Label _status = null!, _title = null!;
    private ItemList _charList = null!;
    private VBoxContainer _loginBox = null!, _charBox = null!;

    private volatile bool _busy;
    private volatile bool _loginDone, _loginOk;
    private volatile bool _enterDone, _enterOk;
    private string _enteringName = "";

    public ClientUi(EntityBridge bridge, string host, string clientVer, string resDir)
    {
        _bridge = bridge; _host = host; _clientVer = clientVer; _resDir = resDir;
    }

    public override void _Ready()
    {
        var root = new CenterContainer();
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);

        var panel = new PanelContainer { CustomMinimumSize = new Vector2(460, 0) };
        root.AddChild(panel);

        var margin = new MarginContainer();
        foreach (var s in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(s, 24);
        panel.AddChild(margin);

        var outer = new VBoxContainer();
        outer.AddThemeConstantOverride("separation", 12);
        margin.AddChild(outer);

        _title = new Label { Text = "Vellichor", HorizontalAlignment = HorizontalAlignment.Center };
        _title.AddThemeFontSizeOverride("font_size", 28);
        outer.AddChild(_title);

        // --- login panel ---
        _loginBox = new VBoxContainer();
        _loginBox.AddThemeConstantOverride("separation", 6);
        outer.AddChild(_loginBox);

        _server = AddField(_loginBox, "Server", _host);
        _account = AddField(_loginBox, "Account", System.Environment.GetEnvironmentVariable("VELLICHOR_ACCOUNT") ?? "");
        _password = AddField(_loginBox, "Password", System.Environment.GetEnvironmentVariable("VELLICHOR_PASSWORD") ?? "", secret: true);
        _connectBtn = new Button { Text = "Connect" };
        _connectBtn.Pressed += OnConnect;
        _loginBox.AddChild(_connectBtn);

        // --- character-select panel (hidden until logged in) ---
        _charBox = new VBoxContainer { Visible = false };
        _charBox.AddThemeConstantOverride("separation", 6);
        outer.AddChild(_charBox);

        _charBox.AddChild(new Label { Text = "Select a character:" });
        _charList = new ItemList { CustomMinimumSize = new Vector2(0, 220), AllowReselect = true };
        _charList.ItemActivated += _ => OnEnter(); // double-click = enter
        _charBox.AddChild(_charList);
        var row = new HBoxContainer();
        _enterBtn = new Button { Text = "Enter World", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _enterBtn.Pressed += OnEnter;
        _backBtn = new Button { Text = "Back" };
        _backBtn.Pressed += OnBack;
        row.AddChild(_enterBtn); row.AddChild(_backBtn);
        _charBox.AddChild(row);

        _status = new Label { Text = "Enter your LSB account to connect.", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _status.CustomMinimumSize = new Vector2(0, 40);
        outer.AddChild(_status);
    }

    private static LineEdit AddField(VBoxContainer parent, string label, string value, bool secret = false)
    {
        parent.AddChild(new Label { Text = label });
        var le = new LineEdit { Text = value, Secret = secret, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        parent.AddChild(le);
        return le;
    }

    private void OnConnect()
    {
        if (_busy) return;
        _busy = true; _loginDone = false;
        _connectBtn.Disabled = true;
        string host = _server.Text.Trim(), acct = _account.Text.Trim(), pass = _password.Text;
        _ = Task.Run(async () =>
        {
            _loginOk = await _bridge.LoginAndListAsync(host, _clientVer, acct, pass, _resDir);
            _loginDone = true;
        });
    }

    private void OnEnter()
    {
        if (_busy) return;
        int idx = _charList.IsAnythingSelected() ? _charList.GetSelectedItems()[0] : (_charList.ItemCount > 0 ? 0 : -1);
        if (idx < 0 || idx >= _bridge.Characters.Count) return;
        var ch = _bridge.Characters[idx];
        _enteringName = ch.Name;
        _busy = true; _enterDone = false;
        _enterBtn.Disabled = true; _backBtn.Disabled = true;
        _ = Task.Run(async () => { _enterOk = await _bridge.EnterWorldAsync(ch); _enterDone = true; });
    }

    private void OnBack()
    {
        if (_busy) return;
        _charBox.Visible = false;
        _loginBox.Visible = true;
        _connectBtn.Disabled = false;
    }

    public override void _Process(double delta)
    {
        // Reflect the bridge's live status text.
        if (_status.Text != _bridge.Status && _bridge.Status.Length > 0) _status.Text = _bridge.Status;

        if (_loginDone)
        {
            _loginDone = false; _busy = false;
            if (_loginOk)
            {
                _loginBox.Visible = false;
                _charBox.Visible = true;
                _charList.Clear();
                foreach (var c in _bridge.Characters) _charList.AddItem($"{c.Name}   (id {c.Id})");
                if (_charList.ItemCount > 0) _charList.Select(0);
            }
            else _connectBtn.Disabled = false; // stay on login; status shows the reason
        }

        if (_enterDone)
        {
            _enterDone = false; _busy = false;
            if (_enterOk)
            {
                EnteredWorld?.Invoke();
                QueueFree(); // hand the screen to the 3D world
            }
            else { _enterBtn.Disabled = false; _backBtn.Disabled = false; } // stay; status shows why
        }
    }
}

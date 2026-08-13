using System;
using Godot;
using XiHeadless.Game;

namespace Vellichor.UI;

/// <summary>
/// In-world HUD (phase 1): HP/MP/TP bars + name/job/level (bottom-left), a live chat window
/// (all channels, color-coded), and a target readout. Fed each frame from the live WorldState.
/// Styling is deliberately minimal — flagged for visual review. All data is real (verified headless).
/// </summary>
public partial class GameHud : CanvasLayer
{
    private Label _name = null!, _job = null!, _hp = null!, _mp = null!, _tp = null!, _target = null!, _event = null!, _location = null!;
    private ProgressBar _hpBar = null!, _mpBar = null!, _tpBar = null!;
    private RichTextLabel _chat = null!;
    private LineEdit _chatInput = null!;
    private Radar _radar = null!;
    private int _lastChatCount = -1;

    // Logout button + on-screen countdown (so you never need the terminal to know it's logging out).
    private Control _logoutOverlay = null!;
    private Label _logoutLabel = null!;
    private double _logoutRemaining = -1; // seconds left in the graceful-logout hold; <0 = inactive
    public System.Action? OnLogout;       // wired by Main to the graceful RequestQuit path

    private static Color AllegianceColor(byte a) => a switch
    {
        0 => new Color(0.9f, 0.3f, 0.25f),      // mob (red)
        1 => new Color(0.35f, 0.7f, 1f),        // player (blue)
        >= 2 and <= 4 => new Color(0.4f, 0.9f, 0.45f), // npc (green)
        _ => new Color(0.95f, 0.65f, 0.15f),    // beastmen/other (orange)
    };

    /// Wired by Main to actually send (channel byte, text). Send is flagged for live test.
    public Action<byte, string>? OnSend;
    /// True while the chat box has keyboard focus — Main pauses movement so WASD types, not walks.
    public bool ChatFocused => _chatInput is not null && _chatInput.HasFocus();
    public void FocusChat() => _chatInput?.GrabFocus();

    private static readonly string[] JobAbbr =
        { "—", "WAR", "MNK", "WHM", "BLM", "RDM", "THF", "PLD", "DRK", "BST", "BRD", "RNG",
          "SAM", "NIN", "DRG", "SMN", "BLU", "COR", "PUP", "DNC", "SCH", "GEO", "RUN" };
    private static string Job(byte id) => id < JobAbbr.Length ? JobAbbr[id] : $"job{id}";

    // FFXI /check difficulty (0..6) -> the familiar con label.
    private static string ConName(int d) => d switch
    {
        0 => "Too Weak", 1 => "Easy Prey", 2 => "Decent Challenge", 3 => "Even Match",
        4 => "Tough", 5 => "Very Tough", 6 => "Incredibly Tough", _ => $"con{d}",
    };

    private static Color KindColor(byte k) => k switch
    {
        1 => new Color(1f, 0.6f, 0.2f),   // shout
        3 => new Color(1f, 0.5f, 0.8f),   // tell
        4 or 15 => new Color(0.4f, 0.8f, 1f), // party
        5 => new Color(0.5f, 1f, 0.5f),   // linkshell
        _ => new Color(0.9f, 0.9f, 0.9f), // say/area/system
    };

    private Label _help = null!;

    /// F1: toggle the on-screen controls cheat-sheet.
    public void ToggleHelp() => _help.Visible = !_help.Visible;

    /// Show the graceful-logout countdown overlay (called when a logout / window-close begins). The
    /// ~40s hold is the server clearing the session; closing early orphans it and crashes the next login.
    public void StartLogoutCountdown(double seconds)
    {
        _logoutRemaining = seconds;
        _logoutOverlay.Visible = true;
    }

    public override void _Process(double delta)
    {
        if (_logoutRemaining < 0) return;
        _logoutRemaining -= delta;
        int s = System.Math.Max(0, (int)System.Math.Ceiling(_logoutRemaining));
        _logoutLabel.Text = s > 0
            ? $"Logging out…\n\nSaving your character with the server.\nPlease wait {s}s — do not force-quit.\n(closing early orphans the session)"
            : "Logout complete. Closing…";
    }

    public override void _Ready()
    {
        // Controls cheat-sheet (F1), hidden by default.
        _help = new Label
        {
            Text = "Controls (keyboard)\n" +
                   "WASD move · Shift sprint · ←/→ camera · PageUp/Dn zoom\n" +
                   "-  (minus): main menu     Esc: cancel / deselect\n" +
                   "Tab: cycle target\n" +
                   "Ctrl+M magic · Ctrl+J abilities · Ctrl+W weapon skill\n" +
                   "Ctrl+I items · Ctrl+A attack · Ctrl+C check\n" +
                   "in menus: ↑↓ move · →/Enter select · ←/Esc back\n" +
                   "Enter: chat (/p party, /sh shout) · F1: this help",
            Visible = false,
        };
        _help.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
        _help.OffsetLeft = -220; _help.OffsetRight = 220; _help.OffsetTop = 80;
        _help.AddThemeFontSizeOverride("font_size", 15);
        _help.AddThemeColorOverride("font_color", new Color(0.95f, 0.95f, 0.8f));
        AddChild(_help);

        // --- stats panel (bottom-left) ---
        var panel = new PanelContainer();
        panel.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
        panel.Position = new Vector2(16, -150);
        panel.CustomMinimumSize = new Vector2(280, 0);
        AddChild(panel);
        var vb = new VBoxContainer(); vb.AddThemeConstantOverride("separation", 3);
        var mc = new MarginContainer();
        foreach (var s in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" }) mc.AddThemeConstantOverride(s, 10);
        mc.AddChild(vb); panel.AddChild(mc);

        _name = new Label { Text = "—" }; _name.AddThemeFontSizeOverride("font_size", 18); vb.AddChild(_name);
        _job = new Label { Text = "" }; vb.AddChild(_job);
        (_hpBar, _hp) = Bar(vb, "HP", new Color(0.85f, 0.25f, 0.2f));
        (_mpBar, _mp) = Bar(vb, "MP", new Color(0.3f, 0.5f, 0.95f));
        (_tpBar, _tp) = Bar(vb, "TP", new Color(0.2f, 0.8f, 0.5f));

        // --- target readout (top-center) ---
        _target = new Label { Text = "", HorizontalAlignment = HorizontalAlignment.Center };
        _target.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
        _target.Position = new Vector2(-100, 24); _target.CustomMinimumSize = new Vector2(200, 0);
        _target.AddThemeFontSizeOverride("font_size", 16);
        AddChild(_target);

        // Cutscene/event banner (top-center). Phase 1: indicator only; full playback is a milestone.
        _event = new Label { Text = "", HorizontalAlignment = HorizontalAlignment.Center };
        _event.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
        _event.Position = new Vector2(-220, 52); _event.CustomMinimumSize = new Vector2(440, 0);
        _event.AddThemeColorOverride("font_color", new Color(1f, 0.9f, 0.5f));
        _event.AddThemeFontSizeOverride("font_size", 15);
        AddChild(_event);

        // Zone name + coordinates, top-right (below the radar).
        _location = new Label { Text = "", HorizontalAlignment = HorizontalAlignment.Right };
        _location.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        _location.OffsetLeft = -320; _location.OffsetRight = -16; _location.OffsetTop = 210;
        _location.AddThemeFontSizeOverride("font_size", 14);
        _location.AddThemeColorOverride("font_color", new Color(0.9f, 0.92f, 0.8f));
        AddChild(_location);

        // Logout button, top-right corner.
        var logoutBtn = new Button { Text = "Logout" };
        logoutBtn.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        logoutBtn.OffsetLeft = -104; logoutBtn.OffsetRight = -16; logoutBtn.OffsetTop = 12;
        logoutBtn.FocusMode = Control.FocusModeEnum.None; // keyboard nav shouldn't land here
        logoutBtn.Pressed += () => OnLogout?.Invoke();
        AddChild(logoutBtn);

        // Full-screen logout countdown overlay (hidden until a graceful logout starts).
        _logoutOverlay = new ColorRect { Color = new Color(0, 0, 0, 0.75f), Visible = false };
        _logoutOverlay.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _logoutOverlay.MouseFilter = Control.MouseFilterEnum.Stop;
        AddChild(_logoutOverlay);
        _logoutLabel = new Label { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _logoutLabel.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _logoutLabel.AddThemeFontSizeOverride("font_size", 26);
        _logoutLabel.AddThemeColorOverride("font_color", new Color(1f, 0.95f, 0.7f));
        _logoutOverlay.AddChild(_logoutLabel);

        // --- chat window (bottom, above stats) ---
        var chatPanel = new PanelContainer();
        chatPanel.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
        chatPanel.Position = new Vector2(16, -430); chatPanel.CustomMinimumSize = new Vector2(460, 250);
        chatPanel.Modulate = new Color(1, 1, 1, 0.9f);
        AddChild(chatPanel);
        _chat = new RichTextLabel { BbcodeEnabled = true, ScrollFollowing = true, FitContent = false };
        var cm = new MarginContainer();
        foreach (var s in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" }) cm.AddThemeConstantOverride(s, 8);
        cm.AddChild(_chat); chatPanel.AddChild(cm);

        // Chat input (Enter to focus/send). Prefix /p party, /sh shout, else say.
        _chatInput = new LineEdit { PlaceholderText = "Press Enter to chat  (/p party, /sh shout)" };
        _chatInput.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
        _chatInput.Position = new Vector2(16, -176); _chatInput.CustomMinimumSize = new Vector2(460, 26);
        _chatInput.Size = new Vector2(460, 26);
        _chatInput.TextSubmitted += t =>
        {
            t = t.Trim();
            if (t.Length > 0 && OnSend is not null)
            {
                byte mode = 0; string msg = t;
                if (t.StartsWith("/p ")) { mode = 4; msg = t[3..]; }
                else if (t.StartsWith("/sh ")) { mode = 1; msg = t[4..]; }
                else if (t.StartsWith("/s ")) { mode = 0; msg = t[3..]; }
                OnSend(mode, msg);
            }
            _chatInput.Clear();
            _chatInput.ReleaseFocus();
        };
        AddChild(_chatInput);

        _radar = new Radar();
        AddChild(_radar);
    }

    private static (ProgressBar, Label) Bar(VBoxContainer parent, string tag, Color col)
    {
        var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 6);
        var t = new Label { Text = tag, CustomMinimumSize = new Vector2(28, 0) }; row.AddChild(t);
        var bar = new ProgressBar { CustomMinimumSize = new Vector2(160, 14), ShowPercentage = false, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var sb = new StyleBoxFlat { BgColor = col }; bar.AddThemeStyleboxOverride("fill", sb);
        row.AddChild(bar);
        var val = new Label { Text = "0", CustomMinimumSize = new Vector2(90, 0) }; row.AddChild(val);
        parent.AddChild(row);
        return (bar, val);
    }

    public void Update(WorldState ws, Vector3 playerGodot)
    {
        // radar: nearby entities relative to the player, in Godot world XZ
        var dots = new System.Collections.Generic.List<(Vector2, Color)>();
        try
        {
            foreach (var e in System.Linq.Enumerable.ToArray(ws.Entities.Values))
            {
                if (!string.IsNullOrEmpty(ws.MyName) && e.Name == ws.MyName) continue;
                var g = new Vector3(-e.X, Vellichor.Render.EntityRenderer.YSign * e.Y, e.Z);
                dots.Add((new Vector2(g.X - playerGodot.X, g.Z - playerGodot.Z), AllegianceColor(e.Allegiance)));
            }
        }
        catch { }
        _radar.SetDots(dots);

        _name.Text = string.IsNullOrEmpty(ws.MyName) ? "—" : ws.MyName;
        _job.Text = ws.MainJob > 0
            ? $"Lv{ws.MainJobLevel} {Job(ws.MainJob)}/{Job(ws.SubJob)} · {ws.Gil:N0} gil"
            : "";
        _hpBar.MaxValue = Math.Max(1, ws.MaxHp); _hpBar.Value = ws.Hp; _hp.Text = $"{ws.Hp}/{ws.MaxHp}";
        _mpBar.MaxValue = Math.Max(1, ws.MaxMp); _mpBar.Value = ws.Mp; _mp.Text = $"{ws.Mp}/{ws.MaxMp}";
        _tpBar.MaxValue = 3000; _tpBar.Value = ws.Tp; _tp.Text = $"{ws.Tp}";

        // target
        _target.Text = "";
        if (ws.CurrentTargetId != 0)
        {
            try
            {
                foreach (var e in System.Linq.Enumerable.ToArray(ws.Entities.Values))
                    if (e.Id == ws.CurrentTargetId)
                    {
                        string con = ws.ConTargetId == e.Id && ws.ConDifficulty >= 0
                            ? $"  Lv{ws.ConMobLevel} ({ConName(ws.ConDifficulty)})" : "";
                        _target.Text = $"» {e.Name}  {e.Hpp}%{con}";
                        break;
                    }
            }
            catch { }
        }

        _event.Text = ws.EventActive ? $"◈ In cutscene / event #{ws.EventId}  (playback: phase-2)" : "";

        string zname = Vellichor.Render.ZoneCatalog.NameFor(ws.ZoneId) ?? (ws.ZoneId > 0 ? $"Zone {ws.ZoneId}" : "");
        _location.Text = zname.Length == 0 ? "" : $"{zname}\n({ws.X:0}, {ws.Y:0}, {ws.Z:0})";

        // chat — rebuild only when it changed
        int count;
        try { count = ws.ChatLog.Count; } catch { return; }
        if (count == _lastChatCount) return;
        _lastChatCount = count;
        ChatLine[] lines;
        try { lines = System.Linq.Enumerable.ToArray(ws.ChatLog); } catch { return; }
        var sb = new System.Text.StringBuilder();
        int start = Math.Max(0, lines.Length - 14);
        for (int i = start; i < lines.Length; i++)
        {
            var l = lines[i];
            var c = KindColor(l.Kind);
            string who = string.IsNullOrEmpty(l.Sender) ? "" : $"{l.Sender}: ";
            sb.Append($"[color=#{c.ToRgba32():x8}]{who}{l.Message.Replace("[", "(")}[/color]\n");
        }
        _chat.Text = sb.ToString();
    }
}

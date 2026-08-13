using System;
using System.Collections.Generic;
using Godot;
using XiHeadless.Game;

namespace Vellichor.UI.Menus;

/// <summary>One selectable row in a menu.</summary>
public sealed class MenuEntry
{
    public string Label = "";
    public string? Detail;          // BBCode shown in the detail pane while highlighted
    public Action? Confirm;         // invoked on Enter / click
    public bool Enabled = true;

    public MenuEntry() { }
    public MenuEntry(string label, Action? confirm = null, string? detail = null)
    { Label = label; Confirm = confirm; Detail = detail; }
    public MenuEntry Off() { Enabled = false; return this; } // convenience for info rows
}

/// <summary>Shared references the menus need (live/offline world state + action hooks + the manager).</summary>
public sealed class MenuContext
{
    public Func<WorldState?> State = () => null;
    public Action<ushort> CastSpell = _ => { };
    public Action Logout = () => { };
    public MenuManager Manager = null!;
}

/// <summary>
/// A titled list menu with a keyboard/mouse cursor and an optional right-hand detail pane. Subclasses
/// override <see cref="Populate"/> to fill entries via <see cref="SetEntries"/>. Navigation + confirm/back
/// are driven by <see cref="MenuManager"/>, so every menu behaves identically.
/// </summary>
public partial class MenuPanel : PanelContainer
{
    protected MenuManager Manager = null!;
    protected string TitleText = "Menu";
    protected virtual bool UseDetail => false;

    private Label _title = null!;
    private VBoxContainer _list = null!;
    private RichTextLabel? _detail;
    private readonly List<Button> _buttons = new();
    private readonly List<string> _labels = new();
    private readonly List<MenuEntry> _entries = new();
    public int Cursor { get; private set; }

    public void Init(MenuManager mgr) => Manager = mgr;

    public override void _Ready()
    {
        AddThemeConstantOverride("margin_left", 10);
        var outer = new VBoxContainer();
        AddChild(outer);
        _title = new Label { Text = TitleText };
        _title.AddThemeFontSizeOverride("font_size", 20);
        outer.AddChild(_title);
        outer.AddChild(new HSeparator());

        var body = new HBoxContainer();
        outer.AddChild(body);
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(300, 440), SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddChild(scroll);
        _list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(_list);
        if (UseDetail)
        {
            _detail = new RichTextLabel { BbcodeEnabled = true, CustomMinimumSize = new Vector2(340, 440), FitContent = false };
            body.AddChild(_detail);
        }

        Populate();
    }

    protected virtual void Populate() { }

    protected void SetTitle(string t) { TitleText = t; if (_title is not null) _title.Text = t; }

    protected void SetEntries(IEnumerable<MenuEntry> entries)
    {
        _entries.Clear(); _entries.AddRange(entries);
        foreach (var c in _list.GetChildren()) c.QueueFree();
        _buttons.Clear();
        int idx = 0;
        foreach (var e in _entries)
        {
            var b = new Button
            {
                Text = "     " + e.Label,
                Alignment = HorizontalAlignment.Left,
                Disabled = !e.Enabled,
                FocusMode = e.Enabled ? Control.FocusModeEnum.All : Control.FocusModeEnum.None,
            };
            int i = idx;
            b.Pressed += () => SetCursor(i);
            b.FocusEntered += () => SetCursor(i);
            _list.AddChild(b);
            _buttons.Add(b);
            _labels.Add(e.Label);
            idx++;
        }
        Cursor = FirstEnabled(0, 1);
        if (Cursor >= 0) _buttons[Cursor].CallDeferred(Control.MethodName.GrabFocus);
        RefreshCursor();
        UpdateDetail();
    }

    public void Move(int delta)
    {
        if (_buttons.Count == 0) return;
        int next = FirstEnabled(Cursor + delta, delta == 0 ? 1 : delta);
        if (next < 0) return;
        SetCursor(next);
        _buttons[Cursor].GrabFocus();
    }

    private void SetCursor(int i)
    {
        if (i < 0 || i >= _buttons.Count) return;
        Cursor = i;
        RefreshCursor();
        UpdateDetail();
    }

    // Draw the keyboard cursor (▶) on the current row so keyboard-only players can see the selection.
    private void RefreshCursor()
    {
        for (int i = 0; i < _buttons.Count; i++)
            _buttons[i].Text = (i == Cursor ? "▶  " : "     ") + _labels[i];
    }

    public void Confirm()
    {
        if (Cursor >= 0 && Cursor < _entries.Count && _entries[Cursor].Enabled) _entries[Cursor].Confirm?.Invoke();
    }

    public virtual void OnBack() => Manager.Back();

    private int FirstEnabled(int start, int dir)
    {
        if (_entries.Count == 0) return -1;
        for (int step = 0; step < _entries.Count; step++)
        {
            int i = Mathf.PosMod(start + step * dir, _entries.Count);
            if (_entries[i].Enabled) return i;
        }
        return -1;
    }

    private void UpdateDetail()
    {
        if (_detail is null) return;
        _detail.Text = Cursor >= 0 && Cursor < _entries.Count ? _entries[Cursor].Detail ?? "" : "";
    }
}

/// <summary>
/// Owns a stack of <see cref="MenuPanel"/>s (breadcrumb navigation), dims the game behind, and routes
/// Up/Down/Enter/Escape to the top panel. One instance lives for the session; open/close is cheap.
/// </summary>
public partial class MenuManager : CanvasLayer
{
    private ColorRect _dim = null!;
    private CenterContainer _center = null!;
    private readonly List<MenuPanel> _stack = new();

    public bool AnyOpen => _stack.Count > 0;

    public override void _Ready()
    {
        Layer = 20;
        _dim = new ColorRect { Color = new Color(0, 0, 0, 0.5f), Visible = false };
        _dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _dim.MouseFilter = Control.MouseFilterEnum.Stop;
        AddChild(_dim);
        _center = new CenterContainer();
        _center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_center);
    }

    public void Open(MenuPanel p)
    {
        if (_stack.Count > 0) _center.RemoveChild(_stack[^1]); // hide current, keep in stack
        p.Init(this);
        _stack.Add(p);
        _center.AddChild(p);
        _dim.Visible = true;
    }

    public void Back()
    {
        if (_stack.Count == 0) return;
        var top = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        _center.RemoveChild(top);
        top.QueueFree();
        if (_stack.Count > 0) _center.AddChild(_stack[^1]);
        else _dim.Visible = false;
    }

    public void CloseAll() { while (_stack.Count > 0) Back(); }

    public override void _Input(InputEvent e)
    {
        if (_stack.Count == 0 || e is not InputEventKey k || !k.Pressed || k.Echo) return;
        var top = _stack[^1];
        switch (k.Keycode)
        {
            case Key.Up: top.Move(-1); break;
            case Key.Down: top.Move(1); break;
            case Key.Right or Key.Enter or Key.KpEnter: top.Confirm(); break;   // Right/Enter = into / confirm
            case Key.Left or Key.Escape or Key.Backspace: top.OnBack(); break;  // Left/Esc = back / cancel
            default: return;
        }
        GetViewport().SetInputAsHandled(); // keyboard-only: fully consume nav so the game never also reacts
    }

    /// Close the whole stack (the `-` menu toggle / a confirmed action calls this).
    public void Toggle(MenuPanel rootIfClosed)
    {
        if (AnyOpen) CloseAll();
        else Open(rootIfClosed);
    }
}

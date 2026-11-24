using Godot;
using Drycastle.Shooter.Menu;
using Drycastle.ShooterCore.Hud;
using Drycastle.ShooterCore.Progression;

namespace Drycastle.Shooter.HeadsUp.RoundFlow;

/// <summary>
/// The respawn screen: a scrim over the world with the killer, the seconds left and the five loadouts, the active one marked in words as well as colour. Number keys 1-5
/// pick the slot for the next spawn (polled, so rebinding works); Edit opens the loadout editor. The death screen fades the scrim in (#283).
/// </summary>
internal sealed class RespawnScreen
{
    readonly Func<bool> _keysFree;
    readonly Action<int> _pickSlot;
    readonly Control _scrim;
    readonly Label _title, _by, _in;
    readonly VBoxContainer _loadoutBox;
    (string Text, bool Active)[] _loadoutRows = Array.Empty<(string, bool)>();
    bool _waits;
    double _at;
    int _shown = -1;

    /// <param name="keysFree">False while a menu screen is open, so the number keys belong to that screen.</param>
    /// <param name="pickSlot">Picks the slot the next spawn uses (the owner sends it to the server).</param>
    /// <param name="editLoadouts">Opens the loadout editor from the respawn screen.</param>
    public RespawnScreen(Control root, Func<bool> keysFree, Action<int> pickSlot, Action editLoadouts)
    {
        _keysFree = keysFree; _pickSlot = pickSlot;
        _scrim = HudUi.P("HudScrim");
        _scrim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _scrim.Visible = false;
        root.AddChild(_scrim);
        var c = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _scrim.AddChild(c);
        var v = new VBoxContainer { CustomMinimumSize = new Vector2(640, 0), MouseFilter = Control.MouseFilterEnum.Ignore };
        c.AddChild(v);
        _title = HudUi.L(Strings.T("hud.respawn.title"), "HudHead", HorizontalAlignment.Center); v.AddChild(_title);
        _by = HudUi.L("", "HudBig", HorizontalAlignment.Center); _by.ThemeTypeVariation = "HudDanger"; _by.ClipText = true; v.AddChild(_by);
        _in = HudUi.L("", "HudDisplay", HorizontalAlignment.Center); v.AddChild(_in);
        v.AddChild(Gap(ShooterTheme.Space * 2));
        v.AddChild(HudUi.L(Strings.T("respawn.loadouts"), "HudSmall", HorizontalAlignment.Center));
        _loadoutBox = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        v.AddChild(_loadoutBox);
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore });
        row.AddChild(Ui.Btn(Strings.T("loadout.edit"), editLoadouts, null, 240));
        row.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore });
        v.AddChild(row);
        RebuildLoadoutRows();
    }

    public bool Dead { get; private set; }

    static Control Gap(float h) => new Control { CustomMinimumSize = new Vector2(0, h), MouseFilter = Control.MouseFilterEnum.Ignore };

    /// <summary>
    /// Shows the screen with the killer and seconds left; <paramref name="killer"/> empty = no killer line. <paramref name="fadeIn"/>: the scrim starts clear and the caller fades it in
    /// with <see cref="SetScrim"/> (the death screen); the countdown runs from now either way, and the respawn keys work while it is still clear.
    /// </summary>
    public void SetDead(bool dead, string killer, float secondsLeft, bool fadeIn)
    {
        Dead = dead;
        _scrim.Visible = dead;
        _scrim.Modulate = new Color(1, 1, 1, dead && fadeIn ? 0f : 1f);
        if (!dead) return;
        _waits = secondsLeft < 0; // an admin took the player out of the round (#312): no countdown
        HudUi.Set(_title, Strings.T(_waits ? "hud.respawn.out" : "hud.respawn.title"));
        if (_waits) { HudUi.Set(_by, ""); HudUi.Set(_in, Strings.T("hud.respawn.waits")); return; }
        _at = HudUi.Now + secondsLeft; _shown = -1;
        HudUi.Set(_by, killer == "" ? Strings.T("hud.respawn.self") : Strings.F("hud.respawn.by", killer));
        UpdateCountdown();
    }

    /// <summary>The scrim's opacity 0..1 (its text, the countdown and the loadout fade with it); only while dead.</summary>
    public void SetScrim(float alpha)
    {
        if (Dead) _scrim.Modulate = new Color(1, 1, 1, Mathf.Clamp(alpha, 0f, 1f));
    }

    /// <summary>The five loadout rows (text and whether active); redrawn when the server sends a new profile, never per frame.</summary>
    public void SetLoadoutRows(IReadOnlyList<(string Text, bool Active)> rows)
    {
        _loadoutRows = rows.ToArray();
        RebuildLoadoutRows();
    }

    void RebuildLoadoutRows()
    {
        foreach (var c in _loadoutBox.GetChildren()) c.QueueFree();
        foreach (var (text, active) in _loadoutRows)
        {
            var label = HudUi.L(active ? text + "   " + Strings.T("loadout.active") : text, active ? "HudAccent" : "HudDim", HorizontalAlignment.Left);
            label.ClipText = true;
            _loadoutBox.AddChild(label);
        }
    }

    public void Tick()
    {
        if (!Dead) return;
        UpdateCountdown();
        PollKeys();
    }

    void UpdateCountdown()
    {
        if (_waits) return;
        var s = HudLogic.CeilSeconds(_at - HudUi.Now);
        if (s == _shown) return;
        _shown = s;
        HudUi.Set(_in, Strings.F("hud.respawn.in", s));
    }

    /// <summary>Number keys pick a slot while dead (the match's weapon keys are off then).</summary>
    void PollKeys()
    {
        if (_keysFree() == false) return;
        for (var k = 0; k < Loadout.SlotCount; k++)
        {
            if (!Input.IsActionJustPressed($"slot_{k + 1}")) continue;
            _pickSlot(k);
            return;
        }
    }
}

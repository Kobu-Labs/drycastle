using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>
/// The respawn loadout list and the post-match XP panel (ticket #118). The respawn list shows the five loadouts, the active
/// one marked in words as well as colour; number keys 1-5 pick the slot for the next spawn; Edit opens the loadout editor.
/// The XP panel sits under the scoreboard in the ending phase, one line per reason with its count and XP.
/// </summary>
public partial class Hud
{
    /// <summary>Picks the slot the next spawn uses (the owner sends it to the server).</summary>
    public Action<int>? PickSlot { get; set; }
    /// <summary>Opens the loadout editor from the respawn screen.</summary>
    public Action? EditLoadouts { get; set; }
    /// <summary>False while a menu screen is open, so the number keys belong to that screen.</summary>
    public Func<bool>? KeysFree { get; set; }

    (string Text, bool Active)[] _loadoutRows = Array.Empty<(string, bool)>();
    VBoxContainer? _loadoutBox;
    PanelContainer _xp = null!;
    VBoxContainer _xpBox = null!;
    XpSummaryInfo? _xpInfo;

    void BuildRespawnLoadouts(VBoxContainer v)
    {
        v.AddChild(Gap(ShooterTheme.Space * 2));
        v.AddChild(L(Strings.T("respawn.loadouts"), "HudSmall", HorizontalAlignment.Center));
        _loadoutBox = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        v.AddChild(_loadoutBox);
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore });
        row.AddChild(Ui.Btn(Strings.T("loadout.edit"), () => EditLoadouts?.Invoke(), null, 240));
        row.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore });
        v.AddChild(row);
        RebuildLoadoutRows();
    }

    static Control Gap(float h) => new Control { CustomMinimumSize = new Vector2(0, h), MouseFilter = Control.MouseFilterEnum.Ignore };

    /// <summary>The five loadout rows (text and whether active); redrawn when the server sends a new profile, never per frame.</summary>
    public void SetLoadoutRows(IReadOnlyList<(string Text, bool Active)> rows)
    {
        _loadoutRows = rows.ToArray();
        RebuildLoadoutRows();
    }

    void RebuildLoadoutRows()
    {
        if (_loadoutBox == null) return;
        foreach (var c in _loadoutBox.GetChildren()) c.QueueFree();
        foreach (var (text, active) in _loadoutRows)
        {
            var label = L(active ? text + "   " + Strings.T("loadout.active") : text, active ? "HudAccent" : "HudDim", HorizontalAlignment.Left);
            label.ClipText = true;
            _loadoutBox.AddChild(label);
        }
    }

    /// <summary>Number keys pick a slot while dead (the match's weapon keys are off then). Polled, so rebinding still works.</summary>
    void PollRespawnKeys()
    {
        if (KeysFree?.Invoke() == false) return;
        for (var k = 0; k < Loadout.SlotCount; k++)
        {
            if (!Input.IsActionJustPressed($"slot_{k + 1}")) continue;
            PickSlot?.Invoke(k);
            return;
        }
    }

    void BuildXpPanel()
    {
        _xp = Place(P("HudPanel"), 0.5f, 1, 0, -ShooterTheme.Margin - 200, 720, 0);
        _xp.GrowVertical = Control.GrowDirection.Begin; // bottom-anchored: grows upward as lines are added
        _xp.Visible = false;
        _xpBox = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _xp.AddChild(_xpBox);
        _root.AddChild(_xp);
    }

    /// <summary>The round's XP summary from the server; shown in the ending phase.</summary>
    public void SetXpSummary(XpSummaryInfo? summary)
    {
        _xpInfo = summary;
        RebuildXpPanel();
        RefreshXpPanel();
    }

    void RefreshXpPanel() => _xp.Visible = _phase == MatchPhase.Ending && _xpInfo != null;

    void RebuildXpPanel()
    {
        foreach (var c in _xpBox.GetChildren()) c.QueueFree();
        if (_xpInfo is not { } info) return;
        _xpBox.AddChild(L(Strings.T("xp.title"), "HudHead"));
        // one line per reason: "Kill x3   +300 XP"
        foreach (var g in info.Lines.GroupBy(l => l.Reason))
        {
            var name = Strings.T("xp.reason." + g.Key) + (g.Count() > 1 ? $" x{g.Count()}" : "");
            _xpBox.AddChild(L(Strings.F("xp.line", name, g.Sum(l => l.Amount)), "HudDim"));
        }
        _xpBox.AddChild(L(Strings.F("xp.total", info.Total), "HudBig", HorizontalAlignment.Center));
        _xpBox.AddChild(L(Strings.F("xp.level", info.Level), "HudDim", HorizontalAlignment.Center));
    }
}

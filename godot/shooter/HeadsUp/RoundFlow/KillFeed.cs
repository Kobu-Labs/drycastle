using Godot;
using Drycastle.Shooter.Hunts;
using Drycastle.Shooter.Menu;
using Drycastle.ShooterCore.Hud;
using Drycastle.ShooterCore.Match;

namespace Drycastle.Shooter.HeadsUp.RoundFlow;

/// <summary>One line of the kill feed: a kill, or (<see cref="Hunt"/>) a Hunter's Log announcement.</summary>
internal sealed record FeedEntry(KillMsg Msg, string Killer, string Victim, HuntFeedLine? Hunt = null);

/// <summary>
/// A Hunter's Log announcement as the feed shows it: <see cref="Tag"/> ("NEW RECORD" in the warn colour, "FIRST" in the good one; also words, so not colour alone), then
/// <see cref="Before"/> + the species + <see cref="After"/>. A <see cref="Hidden"/> species is not in the text: the feed draws the blurred placeholder between the halves.
/// </summary>
public sealed record HuntFeedLine(string Tag, bool Record, bool Mine, string Before, string Species, string After, bool Hidden);

/// <summary>
/// The kill feed, top right: the last kills and Hunter's Log announcements as pre-built rows, rewritten only when the window of lines changes. It sits beside the panels root,
/// not under it, so a control session can hide the panels and keep the feed (ticket #154): there it is compact and shows only the owner's kills.
/// </summary>
internal sealed class KillFeed
{
    const int Rows = HudLogic.FeedMax, MaxKept = 64;
    const int BoxWidth = 480, RowHeight = 40, RowGap = 4, BoxHeight = Rows * RowHeight + (Rows - 1) * RowGap;

    readonly HudState _state;
    readonly Func<byte, string?> _weaponName;
    readonly Control _root;
    readonly VBoxContainer _box;
    readonly PanelContainer[] _row = new PanelContainer[Rows];
    readonly Label[] _killer = new Label[Rows], _weapon = new Label[Rows], _victim = new Label[Rows];
    readonly BlurText[] _blur = new BlurText[Rows];
    readonly List<FeedEntry> _kills = new();
    readonly List<double> _killTimes = new();
    /// <summary>The owner's kills, kept apart from everyone's, for the compact feed of a control session.</summary>
    readonly List<FeedEntry> _mineKills = new();
    readonly List<double> _mineTimes = new();
    (int, int) _shown = (-1, -1);

    /// <param name="layer">The HUD layer: the feed's root is its own child, after the panels root.</param>
    /// <param name="weaponName">Display name of a weapons.json index in a kill message (the killer's weapon, not the viewer's hotbar); null when unknown.</param>
    public KillFeed(Node layer, Theme theme, HudState state, Func<byte, string?> weaponName)
    {
        _state = state; _weaponName = weaponName;
        _root = new Control { Theme = theme, MouseFilter = Control.MouseFilterEnum.Ignore };
        layer.AddChild(_root);
        var v = HudUi.Place(new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }, 1, 0, -ShooterTheme.Margin, ShooterTheme.Margin, BoxWidth, BoxHeight);
        v.PivotOffset = new Vector2(BoxWidth, 0); // the compact feed shrinks towards the top right corner
        v.AddThemeConstantOverride("separation", RowGap);
        _root.AddChild(v);
        _box = v;
        for (var i = 0; i < Rows; i++)
        {
            var row = HudUi.P("HudRow"); row.CustomMinimumSize = new Vector2(0, RowHeight); row.Visible = false;
            var h = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End, MouseFilter = Control.MouseFilterEnum.Ignore };
            h.AddThemeConstantOverride("separation", ShooterTheme.Space);
            row.AddChild(h);
            _killer[i] = HudUi.L("");
            _weapon[i] = HudUi.L("", "HudDim");
            _victim[i] = HudUi.L("");
            _blur[i] = new BlurText(Strings.T("hunt.hidden.name")) { Visible = false }; // a hunt line's Mystery creature the viewer has not caught
            h.AddChild(_killer[i]); h.AddChild(_weapon[i]); h.AddChild(_blur[i]); h.AddChild(_victim[i]);
            v.AddChild(row); _row[i] = row;
        }
    }

    /// <summary>The feed's own root, which follows the resolution and the UI scale like the panels root.</summary>
    public Control Root => _root;

    /// <summary>Shows the compact feed of a control session, or the full one again.</summary>
    public void SetCompact(bool on)
    {
        _box.Scale = on ? new Vector2(ShooterTheme.FeedCompactScale, ShooterTheme.FeedCompactScale) : Vector2.One;
        Invalidate();
    }

    /// <summary>Moves the feed down by <paramref name="shift"/> (clear of the FPS card).</summary>
    public void Shift(float shift)
    {
        _box.OffsetTop = ShooterTheme.Margin + shift;
        _box.OffsetBottom = _box.OffsetTop + BoxHeight;
    }

    /// <summary>The next frame redraws the rows (the lines, the teams or the list shown changed).</summary>
    public void Invalidate() => _shown = (-1, -1);

    public void AddKill(KillMsg k, string killer, string victim)
    {
        Keep(new FeedEntry(k, killer, victim));
        // the owner's kills for the compact feed (a suicide is not a kill)
        if (k.Killer == _state.MyId && k.Killer != k.Victim) KeepMine(new FeedEntry(k, killer, victim));
        Invalidate();
    }

    /// <summary>A server-wide Hunter's Log announcement (a record or a first); the owner's own are kept for the compact feed too.</summary>
    public void AddHuntLine(HuntFeedLine line)
    {
        var e = new FeedEntry(new KillMsg(0, 0, 0), "", "", line); // a hunt line has no kill: the message is a placeholder the feed never reads
        Keep(e);
        if (line.Mine) KeepMine(e);
        Invalidate();
    }

    public void Clear()
    {
        _kills.Clear(); _killTimes.Clear(); _mineKills.Clear(); _mineTimes.Clear();
        Invalidate();
    }

    void Keep(FeedEntry e)
    {
        _kills.Add(e); _killTimes.Add(HudUi.Now);
        if (_kills.Count > MaxKept) { _kills.RemoveRange(0, 32); _killTimes.RemoveRange(0, 32); }
    }

    void KeepMine(FeedEntry e)
    {
        _mineKills.Add(e); _mineTimes.Add(HudUi.Now);
        if (_mineKills.Count > MaxKept) { _mineKills.RemoveRange(0, 32); _mineTimes.RemoveRange(0, 32); }
    }

    public void Tick(double now)
    {
        // a control session shows only the owner's kills, compactly (ticket #154); the rows and their text are the same
        var compact = _state.ControlMode;
        var w = compact ? HudLogic.FeedWindow(_mineTimes, now, ShooterTheme.FeedCompactRows) : HudLogic.FeedWindow(_killTimes, now);
        if (w == _shown) return;
        _shown = w;
        var list = compact ? _mineKills : _kills;
        for (var i = 0; i < Rows; i++)
        {
            if (i >= w.Count) { _row[i].Visible = false; continue; }
            var (k, killer, victim, hunt) = list[w.Start + i];
            if (hunt != null) { ShowHunt(i, hunt); continue; }
            ShowKill(i, k, killer, victim);
        }
    }

    void ShowKill(int i, KillMsg k, string killer, string victim)
    {
        ResetRow(i);
        var mine = k.Killer == _state.MyId || k.Victim == _state.MyId;
        var suicide = k.Killer == k.Victim;
        _row[i].ThemeTypeVariation = mine ? "HudRowMine" : "HudRow";
        // your own rows say so in words as well as colour (as the scoreboard does)
        var killerTeam = _state.TeamOf(k.Killer); var victimTeam = _state.TeamOf(k.Victim);
        HudUi.Set(_killer[i], _state.Tagged(WithYou(suicide ? victim : killer, k.Killer == _state.MyId), killerTeam));
        HudUi.Set(_weapon[i], $"[{(k.Cause == KillCause.Roadkill ? Strings.T("feed.roadkill") : WeaponName(k.Weapon))}]");
        HudUi.Set(_victim[i], suicide ? "" : _state.Tagged(WithYou(victim, k.Victim == _state.MyId), victimTeam));
        HudUi.Tint(_killer[i], _state.RelationColor(killerTeam)); HudUi.Tint(_victim[i], _state.RelationColor(victimTeam));
        _row[i].Visible = true;
    }

    /// <summary>A hunt line: the tag in its colour (and words), then the sentence; a Mystery creature the viewer has not caught is the blurred placeholder between its two halves.</summary>
    void ShowHunt(int i, HuntFeedLine l)
    {
        _row[i].ThemeTypeVariation = l.Mine ? "HudRowMine" : "HudRow";
        HudUi.Set(_killer[i], l.Tag);
        HudUi.Tint(_killer[i], l.Record ? ShooterTheme.Warn : ShooterTheme.Good);
        _weapon[i].ThemeTypeVariation = "Label";
        HudUi.Set(_weapon[i], l.Hidden ? l.Before : l.Before + l.Species + l.After);
        HudUi.Tint(_weapon[i], null);
        _blur[i].Visible = l.Hidden;
        HudUi.Set(_victim[i], l.Hidden ? l.After : "");
        HudUi.Tint(_victim[i], null);
        // a sentence is longer than a kill: the part after the tag wraps inside the feed's width instead of widening it past the screen's edge
        Wrap(_weapon[i], !l.Hidden);
        Wrap(_victim[i], l.Hidden);
        _row[i].Visible = true;
    }

    static void Wrap(Label l, bool on)
    {
        l.AutowrapMode = on ? TextServer.AutowrapMode.WordSmart : TextServer.AutowrapMode.Off;
        l.SizeFlagsHorizontal = on ? Control.SizeFlags.ExpandFill : Control.SizeFlags.Fill;
    }

    /// <summary>A kill row after a hunt row used it: the dim weapon tag, no blur, no wrapping.</summary>
    void ResetRow(int i)
    {
        Wrap(_weapon[i], false); Wrap(_victim[i], false);
        if (_weapon[i].ThemeTypeVariation != "HudDim") _weapon[i].ThemeTypeVariation = "HudDim";
        if (_blur[i].Visible) _blur[i].Visible = false;
    }

    static string WithYou(string name, bool mine) => mine ? Strings.F("score.you", name) : name;

    string WeaponName(byte id) => id switch
    {
        SpecialWeapons.Fall => Strings.T("weapon.fall"),
        SpecialWeapons.Bigfoot => Strings.T("weapon.bigfoot"),
        SpecialWeapons.Gang => Strings.T("weapon.gang"),
        SpecialWeapons.Void => Strings.T("weapon.void"),
        SpecialWeapons.Debug => Strings.T("weapon.debug"),
        SpecialWeapons.Redeploy => Strings.T("weapon.redeploy"),
        SpecialWeapons.Harvester => Strings.T("weapon.harvester"),
        SpecialWeapons.Vehicle => Strings.T("weapon.vehicle"),
        SpecialWeapons.VehicleGun => Strings.T("weapon.vehicle_gun"),
        SpecialWeapons.Flak => Strings.T("weapon.flak"),
        SpecialWeapons.Burn => Strings.T("weapon.burn"),
        SpecialWeapons.Moab => Strings.T("weapon.moab"),
        SpecialWeapons.Scud => Strings.T("weapon.scud"),
        SpecialWeapons.Missile => Strings.T("weapon.attack_missile"),
        SpecialWeapons.Turret => Strings.T("weapon.turret"),
        SpecialWeapons.AttackHelicopter => Strings.T("heli.weapon"),
        SpecialWeapons.Ac130 => Strings.T("ac130.kill"),
        SpecialWeapons.Helper => Strings.T("helper.weapon"),
        _ => _weaponName(id) ?? Strings.T("weapon.unknown"),
    };
}

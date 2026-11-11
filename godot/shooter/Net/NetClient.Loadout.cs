using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>
/// The client's copy of the player's loadout (ticket #117): the hotbar (primary, secondary, knife) and perk the server applies
/// at each spawn, the progression events (level-up, round XP summary). The server is authoritative; the copy only drives
/// keys, the HUD and prediction, and is refreshed at each spawn so it matches the server's copy.
/// </summary>
public partial class NetClient
{
    /// <summary>perks.json, to turn the loadout's perk id into its modifiers (prediction).</summary>
    public PerksFile PerkData = null!;

    /// <summary>Raised when the server says this player reached a new level.</summary>
    public event Action<LevelUpInfo>? LevelUpReceived;
    /// <summary>Raised at the end of each round with the XP this player earned in it.</summary>
    public event Action<XpSummaryInfo>? XpSummaryReceived;
    /// <summary>The last round's XP summary (null until the first round ends).</summary>
    public XpSummaryInfo? LastXpSummary { get; private set; }
    /// <summary>Raised whenever the server sends this player's profile (after a save, a slot change, a level-up or a spawn).</summary>
    public event Action<ProfileMsg>? ProfileChanged;
    /// <summary>Opens the loadout editor from the respawn screen (the owner pushes it on the menu shell).</summary>
    public Action? OpenLoadouts { get; set; }
    /// <summary>True while a menu screen is open; the respawn number keys then leave the menu alone.</summary>
    public Func<bool>? MenuOpen { get; set; }

    /// <summary>weapons.json indexes of the spawn hotbar: primary, secondary, knife (keys 1-3 and the cycle).</summary>
    int[] _hotbar = Array.Empty<int>();

    /// <summary>Takes the active loadout (as the server does at the spawn): the hotbar, the perk, and the hotbar names on the HUD.</summary>
    void ApplySpawnLoadout()
    {
        var p = Profile;
        var active = p is { } pr && pr.ActiveSlot >= 0 && pr.ActiveSlot < pr.Loadouts.Count ? pr.Loadouts[pr.ActiveSlot] : null;
        if (Weapons != null) _hotbar = LoadoutHotbar.Build(Weapons, active);
        Perks = PerkModifiers.Of(PerkData?.Find(active?.Perk ?? ""));
        if (Weapons != null && _hud != null) _hud.SetWeapons(_hotbar.Select(i => WeaponName(Weapons.Weapons[i].Id)).ToList());
        ArmStreaks(); // the HUD tracker's counter starts at this spawn (ticket #119)
    }

    /// <summary>Position of a weapons.json slot (1-based) on the hotbar, 0-based; -1 when it is not on the hotbar.</summary>
    int HotbarPos(int slot) => Array.IndexOf(_hotbar, slot - 1);

    /// <summary>The first hotbar slot (the primary), as a 1-based weapons.json slot.</summary>
    int StartSlot() => _hotbar.Length > 0 ? _hotbar[0] + 1 : 1;

    /// <summary>The next (dir = 1) or previous (dir = -1) hotbar slot after <paramref name="slot"/>, wrapping around.</summary>
    int CycleSlot(int slot, int dir)
    {
        if (_hotbar.Length == 0) return slot;
        var i = HotbarPos(slot);
        var next = i < 0 ? 0 : ((i + dir) % _hotbar.Length + _hotbar.Length) % _hotbar.Length;
        return _hotbar[next] + 1;
    }

    /// <summary>A profile arrived: the editor and the respawn list redraw from it.</summary>
    void OnProfileReceived(ProfileMsg p)
    {
        ProfileChanged?.Invoke(p);
        _hud?.SetLoadoutRows(LoadoutRows(p));
    }

    /// <summary>Hands the respawn screen its hooks: pick a slot (number keys or click), open the editor, and the rows now.</summary>
    void WireLoadoutHud(Hud hud)
    {
        hud.PickSlot = SelectSlot;
        hud.EditLoadouts = () => OpenLoadouts?.Invoke();
        hud.KeysFree = () => !(MenuOpen?.Invoke() ?? false);
        if (Profile is { } p) hud.SetLoadoutRows(LoadoutRows(p));
    }

    /// <summary>The five loadouts as respawn rows: the text, and whether it is the slot the next spawn uses.</summary>
    public IReadOnlyList<(string Text, bool Active)> LoadoutRows(ProfileMsg p)
    {
        var rows = new List<(string, bool)>(Loadout.SlotCount);
        for (var s = 0; s < Loadout.SlotCount; s++)
        {
            var l = p.Loadouts.FirstOrDefault(x => x.Slot == s) ?? Loadout.Empty(s);
            rows.Add(($"{s + 1}   {LoadoutLine(l)}", p.ActiveSlot == s));
        }
        return rows;
    }

    /// <summary>One loadout in words: primary / secondary / perk / streaks.</summary>
    public string LoadoutLine(Loadout l)
    {
        var parts = new List<string>
        {
            l.PrimaryWeapon == "" ? Strings.T("loadout.empty") : WeaponName(l.PrimaryWeapon),
            l.SecondaryWeapon == "" ? Strings.T("loadout.empty") : WeaponName(l.SecondaryWeapon),
            l.Perk == "" ? Strings.T("loadout.none") : PerkData?.Find(l.Perk)?.Name ?? l.Perk,
        };
        var streaks = new[] { l.Streak1, l.Streak2, l.Streak3 }.Where(s => s != "").Select(StreakName);
        parts.Add(string.Join(", ", streaks));
        return string.Join("  /  ", parts);
    }

    static string StreakName(string id)
    {
        var k = "streak." + id;
        var t = Strings.T(k);
        return t == k ? id : t;
    }

    void OnProgressionEvent(EventKind kind, byte[] payload)
    {
        switch (kind)
        {
            case EventKind.Hotbar when CorpseCodec.TryDecodeHotbar(payload, out var hot):
                ApplyHotbar(hot);
                break;
            case EventKind.LevelUp when ProgressionEvents.DecodeLevelUp(payload) is { } lu:
                if (Profile is { } p) Profile = p with { Level = lu.Level, Xp = lu.Xp };
                GD.Print($"client {PlayerName}: level up to {lu.Level}");
                LevelUpReceived?.Invoke(lu);
                break;
            case EventKind.StreakEarned or EventKind.StreakUsed or EventKind.StreakEnded:
                OnStreakEvent(kind, payload);
                break;
            case EventKind.XpSummary when ProgressionEvents.DecodeXpSummary(payload) is { } xs:
                LastXpSummary = xs;
                if (Profile is { } q) Profile = q with { Level = xs.Level, Xp = xs.Xp };
                GD.Print($"client {PlayerName}: round XP {xs.Total}, level {xs.Level}");
                _hud?.SetXpSummary(xs);
                XpSummaryReceived?.Invoke(xs);
                break;
        }
    }
}

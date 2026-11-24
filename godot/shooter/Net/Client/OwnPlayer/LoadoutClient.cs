using Godot;
using Drycastle.Shooter.HeadsUp;
using Drycastle.Shooter.Menu;
using Drycastle.Shooter.Net.Client.HudFeeds;
using Drycastle.ShooterCore.Corpses;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Perks;
using Drycastle.ShooterCore.Progression;

namespace Drycastle.Shooter.Net.Client.OwnPlayer;

/// <summary>
/// The client's copy of the player's profile and loadout (ticket #117): the hotbar (primary, secondary, knife) and perk the server applies
/// at each spawn, the progression events (level-up, round XP summary), and the player's requests about their loadouts. The server is authoritative; the copy only drives
/// keys, the HUD and prediction, and is refreshed at each spawn so it matches the server's copy.
/// </summary>
public sealed class LoadoutClient
{
    readonly ClientContext _ctx;
    readonly PerksFile _perkData;
    readonly ChatClient _chat;

    public LoadoutClient(ClientContext ctx, PerksFile perkData, ChatClient chat)
    {
        _ctx = ctx;
        _perkData = perkData;
        _chat = chat;
    }

    /// <summary>The server's profile for this player (level, XP, loadouts, active slot); null until it arrives.</summary>
    public ProfileMsg? Profile { get; private set; }
    /// <summary>The active loadout's perk, as modifiers (prediction and the streaks' cost).</summary>
    public PerkModifiers Perks { get; private set; } = PerkModifiers.None;
    /// <summary>The loadout of the active slot, or null before the profile.</summary>
    public Loadout? Active => Profile is { } p && p.ActiveSlot >= 0 && p.ActiveSlot < p.Loadouts.Count ? p.Loadouts[p.ActiveSlot] : null;

    /// <summary>Raised when the server says this player reached a new level.</summary>
    public event Action<LevelUpInfo>? LevelUpReceived;
    /// <summary>Raised whenever the server sends this player's profile (after a save, a slot change, a level-up or a spawn).</summary>
    public event Action<ProfileMsg>? ProfileChanged;
    /// <summary>Raised for every answer to a save (ok or the reason), so the editor can show a rejection.</summary>
    public event Action<LoadoutAckInfo>? Acked;
    /// <summary>Opens the loadout editor from the respawn screen (the owner pushes it on the menu shell).</summary>
    public Action? OpenEditor { get; set; }

    /// <summary>
    /// Loadout autosave state (#145). It lives on the connection, not the screen, so a save in flight is still answered when
    /// the player leaves the editor. The screen reads it; <see cref="PumpAutosave"/> sends it.
    /// </summary>
    public LoadoutAutosave Autosave { get; } = new();

    // ---- requests ----

    /// <summary>Asks the server to replace a loadout slot. The server answers with a <see cref="LoadoutAckInfo"/> (matched by seq) and the new profile. False when not connected.</summary>
    public bool Save(Loadout loadout, uint seq) => _ctx.SendJoined(new SaveLoadoutMsg(loadout, seq));

    /// <summary>Asks the server to make a loadout slot the active one. Answered like <see cref="Save"/>.</summary>
    public void SelectSlot(int slot) => _ctx.SendJoined(new SelectSlotMsg(slot));

    /// <summary>Asks the server to copy one loadout slot (and its camouflage) into another. The answer is a new profile, or a server error.</summary>
    public bool Copy(int from, int to) => _ctx.SendJoined(new EventMsg((byte)EventKind.CopyLoadout, LoadoutEvents.EncodeCopy(from, to)));

    /// <summary>Asks the server to restore one loadout slot to the starter kit. The answer is a new profile, or a server error.</summary>
    public bool Reset(int slot) => _ctx.SendJoined(new EventMsg((byte)EventKind.ResetLoadout, LoadoutEvents.EncodeReset(slot)));

    /// <summary>Sends every save whose debounce has run out (called each frame), and times out the ones that got no answer.</summary>
    public void PumpAutosave()
    {
        var now = (long)Time.GetTicksMsec();
        foreach (var (slot, seq, value) in Autosave.Poll(now))
            if (!Save(value, seq)) Autosave.Abandon(slot, now);
    }

    /// <summary>Leaving the editor: every unsent edit goes out now, so nothing waits on the debounce.</summary>
    public void FlushAutosave()
    {
        Autosave.Flush((long)Time.GetTicksMsec());
        PumpAutosave();
    }

    // ---- what the server says ----

    /// <summary>Takes the active loadout (as the server does at the spawn): the hotbar, the perk, and the hotbar names on the HUD.</summary>
    public void ApplySpawn()
    {
        var active = Active;
        if (_ctx.Weapons != null) _ctx.Hotbar.Set(LoadoutHotbar.Build(_ctx.Weapons, active));
        Perks = PerkModifiers.Of(_perkData?.Find(active?.Perk ?? ""));
        ShowHotbarNames();
    }

    /// <summary>The server's hotbar after a corpse pickup: the HUD's names follow.</summary>
    void OnHotbar(int[] hot)
    {
        if (_ctx.Weapons == null || hot.Length == 0) return;
        _ctx.Hotbar.Replace(hot);
        ShowHotbarNames();
    }

    void ShowHotbarNames()
    {
        if (_ctx.Weapons != null && _ctx.Hud != null) _ctx.Hud.SetWeapons(_ctx.Hotbar.Slots.Select(i => ClientText.WeaponName(_ctx.Weapons.Weapons[i].Id)).ToList());
    }

    /// <summary>A profile arrived: the editor and the respawn list redraw from it.</summary>
    public void OnProfile(ProfileMsg p)
    {
        Profile = p;
        foreach (var l in p.Loadouts) Autosave.Seed(l); // the editor's server copy; a draft still being saved stays on screen
        ProfileChanged?.Invoke(p);
        _ctx.Hud?.SetLoadoutRows(Rows(p));
    }

    /// <summary>Hands the respawn screen its hooks: pick a slot (number keys or click), open the editor, and the rows now.</summary>
    public void WireHud(Hud hud)
    {
        hud.PickSlot = SelectSlot;
        hud.EditLoadouts = () => OpenEditor?.Invoke();
        hud.KeysFree = () => !_ctx.MenuIsOpen && !hud.ChatOpen; // the number keys are typing while the chat box is open
        _chat.WireHud(hud);
        if (Profile is { } p) hud.SetLoadoutRows(Rows(p));
    }

    /// <summary>The five loadouts as respawn rows: the text, and whether it is the slot the next spawn uses.</summary>
    public IReadOnlyList<(string Text, bool Active)> Rows(ProfileMsg p)
    {
        var rows = new List<(string, bool)>(Loadout.SlotCount);
        for (var s = 0; s < Loadout.SlotCount; s++)
        {
            var l = p.Loadouts.FirstOrDefault(x => x.Slot == s) ?? Loadout.Empty(s);
            rows.Add(($"{s + 1}   {Line(l)}", p.ActiveSlot == s));
        }
        return rows;
    }

    /// <summary>One loadout in words: primary / secondary / perk / streaks.</summary>
    string Line(Loadout l)
    {
        var parts = new List<string>
        {
            l.PrimaryWeapon == "" ? Strings.T("loadout.empty") : ClientText.WeaponName(l.PrimaryWeapon),
            l.SecondaryWeapon == "" ? Strings.T("loadout.empty") : ClientText.WeaponName(l.SecondaryWeapon),
            l.Perk == "" ? Strings.T("loadout.none") : _perkData?.Find(l.Perk)?.Name ?? l.Perk,
        };
        var streaks = new[] { l.Streak1, l.Streak2, l.Streak3 }.Where(s => s != "").Select(ClientText.StreakName);
        parts.Add(string.Join(", ", streaks));
        return string.Join("  /  ", parts);
    }

    /// <summary>The hotbar, level-up, save answer and round XP summary events.</summary>
    public void OnEvent(EventKind kind, byte[] payload)
    {
        switch (kind)
        {
            case EventKind.Hotbar when CorpseCodec.TryDecodeHotbar(payload, out var hot):
                OnHotbar(hot);
                break;
            case EventKind.LevelUp when ProgressionEvents.DecodeLevelUp(payload) is { } lu:
                if (Profile is { } p) Profile = p with { Level = lu.Level, Xp = lu.Xp };
                GD.Print($"client {_ctx.PlayerName}: level up to {lu.Level}");
                LevelUpReceived?.Invoke(lu);
                break;
            case EventKind.LoadoutAck when LoadoutEvents.DecodeAck(payload) is { } ack:
                Autosave.Acknowledge(ack);
                Acked?.Invoke(ack);
                break;
            case EventKind.XpSummary when ProgressionEvents.DecodeXpSummary(payload) is { } xs:
                if (Profile is { } q) Profile = q with { Level = xs.Level, Xp = xs.Xp };
                GD.Print($"client {_ctx.PlayerName}: round XP {xs.Total}, level {xs.Level}");
                _ctx.Hud?.SetXpSummary(xs);
                break;
        }
    }
}

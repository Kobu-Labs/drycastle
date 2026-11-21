using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The Hunter's Log on the client (#336, <see cref="HuntersLogScreen"/>): its key (J, rebindable "hunters_log"), the three queries and their redacted answers
/// (<see cref="HuntLogData"/> sends and matches them), and the server-wide announcements, shown as a kill feed line and, for the player's own record or first, a toast.
/// Esc goes through <see cref="CloseHuntersLog"/>; while the screen is open the shell leaves the mouse free (<see cref="HuntersLogOpen"/>) and the legs keep working.
/// </summary>
public partial class NetClient
{
    HuntLogData? _huntData;

    /// <summary>The queries of the log, and what they last answered.</summary>
    public HuntLogData HuntData => _huntData ??= new HuntLogData((kind, payload) => SendItem(kind, payload));

    /// <summary>The Hunter's Log is up: the mouse is released for it.</summary>
    public bool HuntersLogOpen => _hud?.HuntersLogOpen ?? false;

    /// <summary>Esc: goes up one level of the log (another player's log to My Log), else closes it. True when it was open (the shell then does not open Pause for this Esc).</summary>
    public bool CloseHuntersLog() => _hud?.BackHuntersLog() ?? false;

    /// <summary>Opens the log (the pause menu's entry).</summary>
    public void OpenHuntersLog() => _hud?.OpenHuntersLog();

    /// <summary>The key toggles the screen, but not while a menu, the chat box or the corpse chooser is up (<see cref="OverlayArbiter"/>). True when the key was used.</summary>
    bool HandleHuntersLogInput(InputEvent e)
    {
        if (_hud == null || Bot || e.IsEcho() || !e.IsActionPressed("hunters_log")) return false;
        if (!_hud.ToggleHuntersLog(MenuOpen?.Invoke() == true, ChooserOpen)) return false;
        GetViewport().SetInputAsHandled();
        return true;
    }

    /// <summary>What the screen draws with and asks through.</summary>
    HuntLogEnv HuntLogEnvironment() => new(HuntData, HuntArtOf, HuntSpeciesName, WeaponName, () => Weapons?.Weapons.Select(w => w.Id).ToList() ?? new List<string>(), () => SandboxGranted, () => Weapons != null && ItemData != null ? HuntRewards.For(Weapons, ItemData) : Array.Empty<HuntReward>(), RewardIcon);

    /// <summary>The icon of a reward (a weapon's or an item's), null when the file is not there.</summary>
    static Texture2D? RewardIcon(HuntReward r)
    {
        var path = IconRegistry.PathFor(r.Kind == "weapon" ? IconKind.Weapon : IconKind.Item, r.Id);
        return ResourceLoader.Exists(path) ? ResourceLoader.Load<Texture2D>(path) : null;
    }

    /// <summary>The name of a species key of the log: a Mystery creature's, or the animal's. Never asked for a hidden id.</summary>
    string HuntSpeciesName(string key) => Mysteries.Is(key) ? Strings.T("hunt.species." + key) : AnimalData.Find(key)?.Name ?? key;

    void OnHuntEvent(EventKind kind, byte[] payload)
    {
        switch (kind)
        {
            case EventKind.PlayerLog when HuntEvents.DecodePlayerLog(payload) is { } log: HuntData.OnLog(log); break;
            case EventKind.Records when HuntEvents.DecodeRecords(payload) is { } rec: HuntData.OnRecords(rec); break;
            case EventKind.HuntsPage when HuntEvents.DecodePage(payload) is { } page: HuntData.OnPage(page); break;
            case EventKind.HuntAnnounce when HuntEvents.DecodeAnnounce(payload) is { } a: OnHuntAnnounce(a); break;
        }
    }

    /// <summary>
    /// A record or a first, to everyone: a line of the kill feed ("NEW RECORD  Vesna: Stag, 231 kg", "FIRST  Ilka hunted a Fox for the first time"; a Mystery creature this player has not
    /// caught is the blurred placeholder, the server sent no name) and, when it is this player's own, a toast too. The player's id in the log is learned from the first answer.
    /// </summary>
    void OnHuntAnnounce(HuntAnnounceMsg m)
    {
        if (_hud == null) return;
        const string Mark = "\u0001"; // where the species goes: the feed draws the placeholder there when it is hidden
        var line = HuntLine.Of(m, HuntData.ViewerId);
        if (!HuntData.ViewerKnown)
        {
            // the player's id in the log is not known yet (no answer so far): their own hunt is told by the name, and the id asked for
            if (m.Hunt.Hunter == PlayerName) line = line with { Mine = true };
            if (!HuntersLogOpen) HuntData.RequestLog(0);
        }
        var h = line.Hunt;
        var hunter = line.Mine ? Strings.F("score.you", h.Hunter) : h.Hunter;
        var species = line.Hidden ? "" : HuntSpeciesName(h.Species);
        var text = line.Kind switch
        {
            HuntLineKind.RecordHeaviest => Strings.F("hunt.line.kg", hunter, Mark, HuntFormat.Kg(h.Kg ?? 0)),
            HuntLineKind.RecordLongest => Strings.F("hunt.line.shot", hunter, Mark, HuntFormat.Number(h.DistanceM ?? 0)),
            HuntLineKind.RecordPlayerKill => Strings.F("hunt.line.kill", hunter, HuntFormat.Number(h.DistanceM ?? 0)),
            _ => Strings.F(line.Mystery ? "hunt.line.first.mystery" : "hunt.line.first", hunter, Mark),
        };
        var cut = text.IndexOf(Mark, StringComparison.Ordinal);
        var (before, after) = cut < 0 ? (text, "") : (text[..cut], text[(cut + Mark.Length)..]);
        _hud.AddHuntLine(new HuntFeedLine(Strings.T(line.IsRecord ? "hunt.tag.record" : "hunt.tag.first"), line.IsRecord, line.Mine, before, species, after, line.Hidden));
        if (!line.Mine || line.Hidden) return;
        UiShell.Instance?.Toast(line.Kind switch
        {
            HuntLineKind.RecordHeaviest => Strings.F("toast.hunt.kg", species, HuntFormat.Kg(h.Kg ?? 0)),
            HuntLineKind.RecordLongest => Strings.F("toast.hunt.shot", species, HuntFormat.Number(h.DistanceM ?? 0)),
            HuntLineKind.RecordPlayerKill => Strings.F("toast.hunt.kill", HuntFormat.Number(h.DistanceM ?? 0)),
            _ => Strings.F(line.Mystery ? "toast.hunt.first.mystery" : "toast.hunt.first", species),
        });
    }
}

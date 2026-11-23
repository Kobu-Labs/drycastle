using Godot;
using Drycastle.Shooter.HeadsUp;
using Drycastle.Shooter.Hunts;
using Drycastle.Shooter.Menu;
using Drycastle.Shooter.Wildlife;
using Drycastle.ShooterCore.Companions;
using Drycastle.ShooterCore.Hunts;
using Drycastle.ShooterCore.Icons;
using Drycastle.ShooterCore.Items;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Wildlife;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// The Hunter's Log on the client (#336, <see cref="HuntersLogScreen"/>): the three queries and their redacted answers (<see cref="HuntLogData"/> sends and matches them), what the screen draws with,
/// the server-wide announcements, shown as a kill feed line and, for the player's own record or first, a toast, and the hit diagram of an animal the player just killed with a sniper rifle.
/// The key and Esc are <see cref="ScreensClient"/>'s.
/// </summary>
public sealed class HuntsClient
{
    readonly ClientContext _ctx;
    readonly SandboxClient _sandbox;
    readonly ItemsFile _items;
    readonly CompanionsFile _companions;
    readonly AnimalsFile _animals;
    readonly AnimalShapes _shapes;
    HuntArtResolver? _art;

    public HuntsClient(ClientContext ctx, SandboxClient sandbox, ItemsFile items, CompanionsFile companions, AnimalsFile animals, AnimalShapes shapes)
    {
        _ctx = ctx;
        _sandbox = sandbox;
        _items = items;
        _companions = companions;
        _animals = animals;
        _shapes = shapes;
        Data = new HuntLogData(_ctx.SendHuman);
    }

    /// <summary>The queries of the log, and what they last answered.</summary>
    public HuntLogData Data { get; }

    HuntArtResolver Art => _art ??= new HuntArtResolver(_shapes, _animals);

    /// <summary>What the screen draws with and asks through.</summary>
    public HuntLogEnv Environment() => new(Data, Art, SpeciesName, ClientText.WeaponName, () => _ctx.Weapons?.Weapons.Select(w => w.Id).ToList() ?? new List<string>(), () => _sandbox.Granted, () => _ctx.Weapons != null && _items != null ? HuntRewards.For(_ctx.Weapons, _items, _companions) : Array.Empty<HuntReward>(), RewardIcon);

    /// <summary>The icon of a reward (a weapon's, an item's or a Companion's), null when the file is not there.</summary>
    static Texture2D? RewardIcon(HuntReward r)
    {
        var path = r.Kind == CompanionRules.UnlockKind ? IconRegistry.CompanionPath(r.Id) : IconRegistry.PathFor(r.Kind == "weapon" ? IconKind.Weapon : IconKind.Item, r.Id);
        return ResourceLoader.Exists(path) ? ResourceLoader.Load<Texture2D>(path) : null;
    }

    /// <summary>The name of a species key of the log: a Mystery creature's, or the animal's. Never asked for a hidden id.</summary>
    string SpeciesName(string key) => Mysteries.Is(key) ? Strings.T("hunt.species." + key) : _animals.Find(key)?.Name ?? key;

    public void OnEvent(EventKind kind, byte[] payload)
    {
        switch (kind)
        {
            case EventKind.PlayerLog when HuntEvents.DecodePlayerLog(payload) is { } log: Data.OnLog(log); break;
            case EventKind.Records when HuntEvents.DecodeRecords(payload) is { } rec: Data.OnRecords(rec); break;
            case EventKind.HuntsPage when HuntEvents.DecodePage(payload) is { } page: Data.OnPage(page); break;
            case EventKind.HuntAnnounce when HuntEvents.DecodeAnnounce(payload) is { } a: OnAnnounce(a); break;
        }
    }

    /// <summary>
    /// A record or a first, to everyone: a line of the kill feed ("NEW RECORD  Vesna: Stag, 231 kg", "FIRST  Ilka hunted a Fox for the first time"; a Mystery creature this player has not
    /// caught is the blurred placeholder, the server sent no name) and, when it is this player's own, a toast too. The player's id in the log is learned from the first answer.
    /// </summary>
    void OnAnnounce(HuntAnnounceMsg m)
    {
        if (_ctx.Hud is not { } hud) return;
        const string Mark = "\u0001"; // where the species goes: the feed draws the placeholder there when it is hidden
        var line = HuntLine.Of(m, Data.ViewerId);
        if (!Data.ViewerKnown)
        {
            // the player's id in the log is not known yet (no answer so far): their own hunt is told by the name, and the id asked for
            if (m.Hunt.Hunter == _ctx.PlayerName) line = line with { Mine = true };
            if (!hud.HuntersLogOpen) Data.RequestLog(0);
        }
        var h = line.Hunt;
        var hunter = line.Mine ? Strings.F("score.you", h.Hunter) : h.Hunter;
        var species = line.Hidden ? "" : SpeciesName(h.Species);
        var text = line.Kind switch
        {
            HuntLineKind.RecordHeaviest => Strings.F("hunt.line.kg", hunter, Mark, HuntFormat.Kg(h.Kg ?? 0)),
            HuntLineKind.RecordLongest => Strings.F("hunt.line.shot", hunter, Mark, HuntFormat.Number(h.DistanceM ?? 0)),
            HuntLineKind.RecordPlayerKill => Strings.F("hunt.line.kill", hunter, HuntFormat.Number(h.DistanceM ?? 0)),
            _ => Strings.F(line.Mystery ? "hunt.line.first.mystery" : "hunt.line.first", hunter, Mark),
        };
        var cut = text.IndexOf(Mark, StringComparison.Ordinal);
        var (before, after) = cut < 0 ? (text, "") : (text[..cut], text[(cut + Mark.Length)..]);
        hud.AddHuntLine(new HuntFeedLine(Strings.T(line.IsRecord ? "hunt.tag.record" : "hunt.tag.first"), line.IsRecord, line.Mine, before, species, after, line.Hidden));
        if (!line.Mine || line.Hidden) return;
        UiShell.Instance?.Toast(line.Kind switch
        {
            HuntLineKind.RecordHeaviest => Strings.F("toast.hunt.kg", species, HuntFormat.Kg(h.Kg ?? 0)),
            HuntLineKind.RecordLongest => Strings.F("toast.hunt.shot", species, HuntFormat.Number(h.DistanceM ?? 0)),
            HuntLineKind.RecordPlayerKill => Strings.F("toast.hunt.kill", HuntFormat.Number(h.DistanceM ?? 0)),
            _ => Strings.F(line.Mystery ? "toast.hunt.first.mystery" : "toast.hunt.first", species),
        });
    }

    /// <summary>The hit diagram of an animal this player just killed with a sniper rifle.</summary>
    public void OnShot(HuntShotMsg m)
    {
        if (_ctx.Hud is not { } hud || m.Species >= _animals.Animals.Count) return;
        var def = _animals.Animals[m.Species];
        var weapon = _ctx.Weapons != null && m.Weapon < _ctx.Weapons.Weapons.Count ? ClientText.WeaponName(_ctx.Weapons.Weapons[m.Weapon].Id) : "";
        // always the look's one standing picture, whatever the animal was doing: the hit is moved from the shape the server tested the bullet
        // against (the pose it was in) onto the standing shape, head to head and body to body. Without shapes: the species' baked picture
        var key = def.ShapeKey(m.Variant);
        var standing = _shapes.Frame(key, AnimalPose.Idle, 0);
        var (along, height) = (m.Along, m.Height);
        if (standing != null && m.Pose != HuntShot.NoShape && _shapes.Frame(key, (AnimalPose)m.Pose, m.Frame) is { } hitOn)
            (along, height) = hitOn.MapOnto(standing, m.Along, m.Height, m.Head);
        else if (m.Pose == HuntShot.NoShape) standing = null; // tested against the body box: its picture, by the box
        var line = Strings.F(m.Head ? "hud.hunt.line.head" : "hud.hunt.line", m.DistanceM, weapon);
        // the species and what it weighed (a species with no weight: the name alone)
        var name = def.Name.ToUpperInvariant();
        var title = m.WeightKg > 0 ? Strings.F("hud.hunt.title", name, m.WeightKg.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)) : name;
        hud.ShowHuntShot(title, Art.Art(standing, def.Id), along, height, line);
    }
}

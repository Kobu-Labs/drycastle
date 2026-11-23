using Godot;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Weapons;
using Drycastle.ShooterCore.Wildlife;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server;

/// <summary>
/// Wildlife on the server (GLOSSARY "Wildlife"): wild animals (wire type 50, like pets) that stand in the zones of their species' kind
/// (ADR 0022), run by a <see cref="WildlifeKeeper"/>. Each round, at its start, the keeper puts the species of <c>wildlife.json</c> at random
/// candidate points of the map (zone_points.json); no map, no points or an arena mode means no wildlife. A wild animal flees from living players and
/// moving vehicles, is thrown by a vehicle that runs it over (<see cref="RunOverTargets"/>), is a hit target under ids of its own (<see cref="WildlifeRules.FirstHitId"/> up) and, like a pet, scores nothing and is not in the kill feed.
/// </summary>
public partial class NetServer
{
    /// <summary>wildlife.json: which wild species the map holds and how many (set by the host).</summary>
    public WildlifeFile WildlifeData = new();

    WildlifeKeeper? _wildlife;


    /// <summary>Starts the keeper. Called once from <c>_Ready</c>, after the match exists (the animal type is registered by <c>InitPets</c>).</summary>
    void InitWildlife()
    {
        FlockRules.Register(WorldEntityRegistry.Shared);
        _wildlife = new WildlifeKeeper(WildlifeData, AnimalData, _match.Entities, System.Environment.TickCount) { Log = s => GD.PrintErr($"server: {s}"), Shapes = AnimalShapes, Crowd = _animals.Crowd };
        _match.Wildlife = _wildlife;
        _wildlife.Killed += (a, by) => { GD.Print($"server: #{by} killed a wild {a.Def.Id}"); OnWildKilled(a, by); };
        _wildlife.PhoenixFell += (entity, from, site, seconds) => BroadcastEvent(EventKind.PhoenixFall, PhoenixFallCodec.Encode(entity, from, site, seconds));
        // The dead Phoenix's mineral lies where it burned (WildlifeKeeper.Drops) as a mineral node anybody can take (#401).
        _wildlife.MineralDropped += _minerals.OnDropped;
    }

    /// <summary>A round starts: the wildlife of the last one is gone with the entities; the species of wildlife.json move to their habitat points.</summary>
    void WildlifeRoundStart()
    {
        if (_wildlife == null) return;
        _wildlife.Clear();
        _minerals.RoundStart();
        _animals.ForgetBuildings();
        if (_match.Phase != MatchPhase.Running || ArenaMode || MapDir == "" || Map == null || (WildlifeData.MaxAnimals <= 0 || WildlifeData.Species.Count == 0 && WildlifeData.Herds.Count == 0 && WildlifeData.Rare.Count == 0 && WildlifeData.Phoenix == null) && WildlifeData.Flocks.Count == 0) return;
        var points = MapZonePoints;
        var n = _wildlife.Populate(species => points.Points(species), _animals.Wild);
        GD.Print($"server: {n} wild animals, {_wildlife.Flocks.Count} bird flocks");
    }

    /// <summary>Once a tick, after the entities: the herds drift, a rare species counts down to its next animal, and the day clock's hour goes to the Phoenix's night counter (the clock the clients show: <see cref="ChatTimeCommand"/>'s).</summary>
    void WildlifeTick(float dt)
    {
        if (_wildlife is { Nights: not null } sky) sky.UpdateSky(Clock.HourAt(_tick / (double)Wire.TickRate), _animals.Wild);
        if (_wildlife != null && (_wildlife.Count > 0 || _wildlife.RareWaiting > 0)) _wildlife.Update(dt, _animals.Wild);
    }

    /// <summary>What the hit diagram shows of a bullet that met the wild animal <paramref name="hitId"/> at <paramref name="point"/>; null when the animal or its target is not there.</summary>
    HuntShotMsg? HuntShotOf(IEnumerable<HitTarget> targets, ushort hitId, NVec3 point, float distance, byte weapon, bool head)
    {
        if (_wildlife?.AnimalOf(hitId) is not { } animal) return null;
        foreach (var t in targets)
            if (t.Id == hitId) return HuntShot.From(t, point, animal.Species, distance, weapon, head, animal.Variant, animal.WeightKg);
        return null;
    }

    /// <summary>The living wild animals as hit targets, only while damage can apply to them.</summary>
    void AddWildlifeTargets(List<HitTarget> into, float backSeconds = 0)
    {
        if (_match.Phase == MatchPhase.Running) _wildlife?.AddTargets(into, backSeconds);
    }
}

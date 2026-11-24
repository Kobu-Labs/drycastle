using Godot;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Hunts;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Scenery;
using Drycastle.ShooterCore.Weapons;
using Drycastle.ShooterCore.Wildlife;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Fauna;

/// <summary>
/// Wildlife on the server (GLOSSARY "Wildlife"): wild animals (wire type 50, like pets) that stand in the zones of their species' kind
/// (ADR 0022), run by a <see cref="WildlifeKeeper"/>. Each round, at its start, the keeper puts the species of <c>wildlife.json</c> at random
/// candidate points of the map (zone_points.json); no map, no points or an arena mode means no wildlife. A wild animal flees from living players and
/// moving vehicles, is thrown by a vehicle that runs it over, is a hit target under ids of its own (<see cref="WildlifeRules.FirstHitId"/> up) and, like a pet, scores nothing and is not in the kill feed.
/// </summary>
internal sealed class WildlifeServer
{
    readonly ServerContext _ctx;
    readonly WildlifeFile _data;
    readonly AnimalWorld _world;
    readonly Func<ZonePoints> _zonePoints;
    readonly Func<double> _hour;
    readonly Func<WorldQuery> _query;
    readonly bool _arenaMode;
    readonly string _mapDir;
    readonly Drycastle.MapLoader.MapLoader? _map;
    readonly WildlifeKeeper _keeper;

    /// <param name="zonePoints">The map's baked zone points (the habitat of each species).</param>
    /// <param name="hour">The hour of the day clock the clients show, which the Phoenix's night counter follows.</param>
    /// <param name="killed">A player killed a weighed animal (or the Unicorn, or burned the Phoenix with the second hit).</param>
    /// <param name="mineralDropped">The dead Phoenix's mineral lies where it burned (WildlifeKeeper.Drops), to be taken as a mineral node (#401).</param>
    public WildlifeServer(ServerContext ctx, WildlifeFile data, AnimalsFile animals, AnimalShapes shapes, AnimalWorld world, Func<ZonePoints> zonePoints, Func<double> hour,
        Func<WorldQuery> query, bool arenaMode, string mapDir, Drycastle.MapLoader.MapLoader? map, Action<Animal, ushort> killed, Action<MineralDrop> mineralDropped)
    {
        _ctx = ctx; _data = data; _world = world; _zonePoints = zonePoints; _hour = hour; _query = query; _arenaMode = arenaMode; _mapDir = mapDir; _map = map;
        FlockRules.Register(WorldEntityRegistry.Shared);
        _keeper = new WildlifeKeeper(data, animals, ctx.Match.Entities, System.Environment.TickCount) { Log = s => GD.PrintErr($"server: {s}"), Shapes = shapes, Crowd = world.Crowd };
        ctx.Match.Wildlife = _keeper;
        _keeper.Killed += (a, by) => { GD.Print($"server: #{by} killed a wild {a.Def.Id}"); killed(a, by); };
        _keeper.PhoenixFell += (entity, from, site, seconds) => ctx.Broadcast(EventKind.PhoenixFall, PhoenixFallCodec.Encode(entity, from, site, seconds));
        _keeper.MineralDropped += mineralDropped;
    }

    /// <summary>The keeper, for what asks about a wild animal by its hit id (a run-over) or what it dropped.</summary>
    public WildlifeKeeper Keeper => _keeper;

    /// <summary>A round starts: the wildlife of the last one is gone with the entities.</summary>
    public void Clear()
    {
        _keeper.Clear();
        _world.ForgetBuildings();
    }

    /// <summary>A round has started: the species of wildlife.json move to their habitat points.</summary>
    public void Populate()
    {
        if (_ctx.Match.Phase != MatchPhase.Running || _arenaMode || _mapDir == "" || _map == null || (_data.MaxAnimals <= 0 || _data.Species.Count == 0 && _data.Herds.Count == 0 && _data.Rare.Count == 0 && _data.Phoenix == null) && _data.Flocks.Count == 0) return;
        var points = _zonePoints();
        var n = _keeper.Populate(species => points.Points(species), _world.Wild);
        GD.Print($"server: {n} wild animals, {_keeper.Flocks.Count} bird flocks");
    }

    /// <summary>Once a tick, after the entities: the herds drift, a rare species counts down to its next animal, and the day clock's hour goes to the Phoenix's night counter.</summary>
    public void Tick(float dt)
    {
        if (_keeper is { Nights: not null } sky) sky.UpdateSky(_hour(), _world.Wild);
        if (_keeper.Count > 0 || _keeper.RareWaiting > 0) _keeper.Update(dt, _world.Wild);
    }

    /// <summary>What the hit diagram shows of a bullet that met the wild animal <paramref name="hitId"/> at <paramref name="point"/>; null when the animal or its target is not there.</summary>
    public HuntShotMsg? HuntShotOf(IEnumerable<HitTarget> targets, ushort hitId, NVec3 point, float distance, byte weapon, bool head)
    {
        if (_keeper.AnimalOf(hitId) is not { } animal) return null;
        foreach (var t in targets)
            if (t.Id == hitId) return HuntShot.From(t, point, animal.Species, distance, weapon, head, animal.Variant, animal.WeightKg);
        return null;
    }

    /// <summary>The living wild animals as hit targets, only while damage can apply to them.</summary>
    public void AddTargets(List<HitTarget> into, float backSeconds = 0)
    {
        if (_ctx.Match.Phase == MatchPhase.Running) _keeper.AddTargets(into, backSeconds);
    }

    /// <summary>"/phoenix" (admins, a test): a Phoenix now, whatever the hour (<see cref="WildlifeKeeper.SpawnPhoenix"/>); the night counter is not touched, so the scheduled one still comes.</summary>
    public void ChatPhoenix(ServerClient c, IReadOnlyList<string> args)
    {
        if (!_keeper.Active) { _ctx.ChatTo(c, ChatLineKind.Error, "There is no wildlife in this round."); return; }
        if (_keeper.SpawnPhoenix(_world.Wild)) _ctx.ChatTo(c, ChatLineKind.Info, "A Phoenix flies over the map.");
        else _ctx.ChatTo(c, ChatLineKind.Error, "No Phoenix: wildlife.json has none, or the map is full.");
    }

    /// <summary>"/unicorn" (admins, a test): an extra Unicorn on the ground 50 m in front of the player, past its flee_radius (40 m, animals.json) so it is not startled (see <see cref="WildlifeKeeper.SpawnExtra"/>).</summary>
    public void ChatUnicorn(ServerClient c, IReadOnlyList<string> args)
    {
        if (!_ctx.IsAlive(c.Id)) { _ctx.ChatTo(c, ChatLineKind.Error, "You must be alive to spawn a Unicorn."); return; }
        var yaw = c.Body.Yaw;
        var at = c.Body.GlobalPosition + new Vector3(-Mathf.Sin(yaw), 0, -Mathf.Cos(yaw)) * 50f;
        if (_query().Ground(MoverKind.Player, at, 60f, 200f) is { } ground) at = ground.Position;
        if (_keeper.SpawnExtra(Mysteries.Unicorn, at.ToNumerics(), _world.Wild))
            _ctx.ChatTo(c, ChatLineKind.Info, "A Unicorn stands in front of you.");
        else _ctx.ChatTo(c, ChatLineKind.Error, "No room for a Unicorn here (no ground, or the map is full).");
    }
}

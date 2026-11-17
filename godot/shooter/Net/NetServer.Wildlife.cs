using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

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
    WildlifeGround? _wildlifeGround;

    /// <summary>A vehicle slower than this frightens nothing (m/s).</summary>
    const float WildlifeCarThreatSpeed = 1.5f;

    sealed class WildlifeGround : AnimalGroundBase
    {
        public WildlifeGround(NetServer server) : base(server) { }

        protected override float WadeM => PetWadeM;

        /// <summary>The nearest living player or moving vehicle within the radius (flat distance).</summary>
        public override NVec3? NearestThreat(NVec3 from, float radius)
        {
            var t = Prof.Begin(Prof.S.AnimalThreat);
            NVec3? best = null;
            var bestSq = radius * radius;
            void Consider(NVec3 p)
            {
                var dx = p.X - from.X; var dz = p.Z - from.Z;
                var d = dx * dx + dz * dz;
                if (d < bestSq) { bestSq = d; best = p; }
            }
            if (_s._animalsPrepared)
            {
                foreach (var a in _s._animalPlayers)
                    if (a.Alive) Consider(a.Pos);
                foreach (var p in _s._animalCarThreats) Consider(p);
            }
            else
            {
                foreach (var c in _s._byId.Values)
                    if (_s._match.Get(c.Id)?.Life == PlayerLife.Alive) Consider(N(c.Body.GlobalPosition));
                foreach (var car in _s._cars)
                    if (!car.Destroyed && MathF.Abs(car.State.Speed) > WildlifeCarThreatSpeed) Consider(N(car.GlobalPosition));
            }
            Prof.End(Prof.S.AnimalThreat, t);
            return best;
        }
    }

    /// <summary>
    /// Whether any player (a bot too, dead or spectating too) is within the world interest's leave distance of <paramref name="at"/>, flat: the
    /// distance beyond which no client is sent the animal (ADR 0025), so one farther than that from everybody can stand still unseen (#280).
    /// </summary>
    bool AnimalWatched(NVec3 at)
    {
        const float r2 = WorldInterest.LeaveM * WorldInterest.LeaveM;
        if (_animalsPrepared)
        {
            foreach (var a in _animalPlayers)
            {
                var dx = a.Pos.X - at.X; var dz = a.Pos.Z - at.Z;
                if (dx * dx + dz * dz <= r2) return true;
            }
            return false;
        }
        foreach (var c in _byId.Values)
        {
            var p = c.Body.GlobalPosition;
            var dx = p.X - at.X; var dz = p.Z - at.Z;
            if (dx * dx + dz * dz <= r2) return true;
        }
        return false;
    }

    /// <summary>Starts the keeper. Called once from <c>_Ready</c>, after the match exists (the animal type is registered by <c>InitPets</c>).</summary>
    void InitWildlife()
    {
        FlockRules.Register(WorldEntityRegistry.Shared);
        _wildlife = new WildlifeKeeper(WildlifeData, AnimalData, _match.Entities, System.Environment.TickCount) { Log = s => GD.PrintErr($"server: {s}"), Shapes = AnimalShapes, Crowd = _animalCrowd };
        _match.Wildlife = _wildlife;
        _wildlife.Killed += (a, by) => { GD.Print($"server: #{by} killed a wild {a.Def.Id}"); OnWildKilled(a, by); };
    }

    /// <summary>A round starts: the wildlife of the last one is gone with the entities; the species of wildlife.json move to their habitat points.</summary>
    void WildlifeRoundStart()
    {
        if (_wildlife == null) return;
        _wildlife.Clear();
        if (_match.Phase != MatchPhase.Running || ArenaMode || MapDir == "" || Map == null || (WildlifeData.MaxAnimals <= 0 || WildlifeData.Species.Count == 0 && WildlifeData.Herds.Count == 0) && WildlifeData.Flocks.Count == 0) return;
        var points = MapZonePoints;
        var n = _wildlife.Populate(species => points.Points(species), _wildlifeGround ??= new WildlifeGround(this));
        GD.Print($"server: {n} wild animals, {_wildlife.Flocks.Count} bird flocks");
    }

    /// <summary>Once a tick, after the entities: the herds drift.</summary>
    void WildlifeTick(float dt)
    {
        if (_wildlife != null && _wildlife.Count > 0 && _wildlifeGround != null) _wildlife.Update(dt, _wildlifeGround);
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

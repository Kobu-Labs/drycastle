using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// Wildlife on the server (GLOSSARY "Wildlife"): wild animals (wire type 50, like pets) that stand in the zones of their species' kind
/// (ADR 0022), run by a <see cref="WildlifeKeeper"/>. Each round, at its start, the keeper puts the species of <c>wildlife.json</c> at random
/// candidate points of the map (zone_points.json); no map, no points or an arena mode means no wildlife. A wild animal flees from living players and
/// moving vehicles, is a hit target under ids of its own (<see cref="WildlifeRules.FirstHitId"/> up) and, like a pet, scores nothing and is not in the kill feed.
/// </summary>
public partial class NetServer
{
    /// <summary>wildlife.json: which wild species the map holds and how many (set by the host).</summary>
    public WildlifeFile WildlifeData = new();

    WildlifeKeeper? _wildlife;
    WildlifeGround? _wildlifeGround;

    /// <summary>A vehicle slower than this frightens nothing (m/s).</summary>
    const float WildlifeCarThreatSpeed = 1.5f;

    sealed class WildlifeGround : IAnimalWorld
    {
        readonly NetServer _s;
        public WildlifeGround(NetServer server) => _s = server;

        public float? GroundY(float x, float z)
        {
            if (_s.Map == null || _s.Water.Depth(x, z) > PetWadeM) return null;
            var y = _s.Map.GroundHeight(x, z);
            return float.IsNaN(y) ? null : y;
        }

        /// <summary>The nearest living player or moving vehicle within the radius (flat distance).</summary>
        public NVec3? NearestThreat(NVec3 from, float radius)
        {
            NVec3? best = null;
            var bestSq = radius * radius;
            void Consider(NVec3 p)
            {
                var dx = p.X - from.X; var dz = p.Z - from.Z;
                var d = dx * dx + dz * dz;
                if (d < bestSq) { bestSq = d; best = p; }
            }
            foreach (var c in _s._byId.Values)
                if (_s._match.Get(c.Id)?.Life == PlayerLife.Alive) Consider(N(c.Body.GlobalPosition));
            foreach (var car in _s._cars)
                if (!car.Destroyed && MathF.Abs(car.State.Speed) > WildlifeCarThreatSpeed) Consider(N(car.GlobalPosition));
            return best;
        }

        public NVec3? PositionOf(ushort player) => _s._byId.TryGetValue(player, out var c) ? N(c.Body.GlobalPosition) : null;
    }

    /// <summary>Starts the keeper. Called once from <c>_Ready</c>, after the match exists (the animal type is registered by <c>InitPets</c>).</summary>
    void InitWildlife()
    {
        FlockRules.Register(WorldEntityRegistry.Shared);
        _wildlife = new WildlifeKeeper(WildlifeData, AnimalData, _match.Entities, System.Environment.TickCount) { Log = s => GD.PrintErr($"server: {s}") };
        _match.Wildlife = _wildlife;
        _wildlife.Killed += (a, by) => GD.Print($"server: #{by} killed a wild {a.Def.Id}");
    }

    /// <summary>A round starts: the wildlife of the last one is gone with the entities; the species of wildlife.json move to their habitat points.</summary>
    void WildlifeRoundStart()
    {
        if (_wildlife == null) return;
        _wildlife.Clear();
        if (_match.Phase != MatchPhase.Running || ArenaMode || MapDir == "" || Map == null || (WildlifeData.MaxAnimals <= 0 || WildlifeData.Species.Count == 0) && WildlifeData.Flocks.Count == 0) return;
        var points = MapZonePoints;
        var n = _wildlife.Populate(species => points.Points(species), _wildlifeGround ??= new WildlifeGround(this));
        GD.Print($"server: {n} wild animals, {_wildlife.Flocks.Count} bird flocks");
    }

    /// <summary>Once a tick, after the entities: the herds drift.</summary>
    void WildlifeTick(float dt)
    {
        if (_wildlife != null && _wildlife.Count > 0 && _wildlifeGround != null) _wildlife.Update(dt, _wildlifeGround);
    }

    /// <summary>The living wild animals as hit targets, only while damage can apply to them.</summary>
    void AddWildlifeTargets(List<HitTarget> into)
    {
        if (_match.Phase == MatchPhase.Running) _wildlife?.AddTargets(into);
    }
}

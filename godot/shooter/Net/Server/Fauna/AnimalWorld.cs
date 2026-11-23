using Godot;
using Drycastle.Shooter.Characters;
using Drycastle.Shooter.Net.Server.CarPool;
using Drycastle.Shooter.Net.Server.Terrain;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Companions;
using Drycastle.ShooterCore.Diagnostics;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Wildlife;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Fauna;

/// <summary>
/// Animals meet the world (ADR 0028, the Animal row of the rule table): what stops a wild animal or a pet is asked of <see cref="WorldQuery"/> by the
/// <see cref="MoverKind.Animal"/> row; a player and another animal are footprints in ShooterCore (<see cref="Animal"/>, <see cref="AnimalCrowd"/>), not bodies.
/// The two keepers (wildlife and pets) are given their view of the world here: <see cref="Wild"/> and <see cref="Pets"/>, which share the <see cref="Crowd"/>.
/// <para>
/// Every player (bots too, in the order of the server's table) and every moving vehicle as they stand while the entities tick is read once (<see cref="Prepare"/>)
/// instead of once per animal: each animal asked the match and the engine about every player, every tick. That is good between <see cref="Prepare"/> and
/// <see cref="Done"/>, and only until a player is hurt (<see cref="PlayerHurt"/>) or teleported in between: the players are simulated later in
/// the tick, so the animals get the answers they got before. Otherwise the world is asked as before.
/// </para>
/// </summary>
internal sealed class AnimalWorld
{
    /// <summary>A vehicle slower than this frightens nothing (m/s).</summary>
    const float CarThreatSpeed = 1.5f;

    /// <summary>The deepest water a pet or a wild animal wades in.</summary>
    const float WadeM = 0.3f;

    /// <summary>A player as the animals see them this tick.</summary>
    readonly record struct AnimalPlayer(NVec3 Pos, bool Alive, bool OnFoot);

    readonly ServerContext _ctx;
    readonly Fleet _fleet;
    readonly Drycastle.MapLoader.MapLoader? _map;
    readonly Drycastle.ShooterCore.Waters.Water _water;
    readonly Func<WorldQuery> _query;
    readonly Func<FlightBounds?> _mapBounds;

    readonly List<AnimalPlayer> _players = new();
    readonly List<NVec3> _carThreats = new();
    bool _fresh;
    int _teleports;

    BuildingFootprints? _buildings;

    public AnimalWorld(ServerContext ctx, Fleet fleet, Drycastle.MapLoader.MapLoader? map, Drycastle.ShooterCore.Waters.Water water, Func<WorldQuery> query, Func<FlightBounds?> mapBounds)
    {
        _ctx = ctx; _fleet = fleet; _map = map; _water = water; _query = query; _mapBounds = mapBounds;
        Wild = new Ground(this, threats: true);
        Pets = new Ground(this, threats: false);
        Companions = new CompanionGround(this);
    }

    /// <summary>Every animal of the match, wildlife and pets: who an animal runs into (the two keepers put theirs in and take them out).</summary>
    public AnimalCrowd Crowd { get; } = new();

    /// <summary>The wild animals' world: they flee from living players and moving vehicles.</summary>
    public IAnimalWorld Wild { get; }

    /// <summary>The pets' world: a pet fears nothing.</summary>
    public IAnimalWorld Pets { get; }

    /// <summary>The Companions' world: the terrain, as shallow as an animal wades; it fears nothing, and can say who its owner's enemies are (a scout marks them).</summary>
    public ICompanionWorld Companions { get; }

    /// <summary>Whether the list is the world as it is: made this tick, and since then nobody was hurt (a life may have ended) or put somewhere else (the Bigfoot throws, the Gang shoots while the entities tick).</summary>
    bool Prepared => _fresh && Player.TeleportsAll == _teleports;

    /// <summary>Before the entities tick: the players and the moving vehicles the animals look at this tick.</summary>
    public void Prepare()
    {
        _players.Clear();
        _carThreats.Clear();
        foreach (var c in _ctx.Players.Values)
            _players.Add(new AnimalPlayer(c.Body.GlobalPosition.ToNumerics(), _ctx.IsAlive(c.Id), c.Car == null));
        foreach (var car in _fleet.Cars)
            if (!car.Destroyed && MathF.Abs(car.State.Speed) > CarThreatSpeed) _carThreats.Add(car.GlobalPosition.ToNumerics());
        _teleports = Player.TeleportsAll;
        _fresh = true;
    }

    /// <summary>After the animals have ticked: from here on the players move, so the world is asked directly again.</summary>
    public void Done() => _fresh = false;

    /// <summary>A life may end now: the animals' list of players is asked anew.</summary>
    public void PlayerHurt() => _fresh = false;

    /// <summary>The parcels that hold a building are read again at the next ask (the round starts).</summary>
    public void ForgetBuildings() => _buildings = null;

    /// <summary>The parcels that hold a building, as polygons (what a rare animal keeps away from, <see cref="IAnimalWorld.NearestBuildingM"/>).</summary>
    BuildingFootprints Buildings
    {
        get
        {
            if (_buildings != null) return _buildings;
            var f = new BuildingFootprints();
            if (_map != null)
                foreach (var p in _map.Parcels.Values)
                    if (p.BuildingIds.Count > 0 && p.Rings.Count > 0)
                        f.Add(p.Rings.Select(r => r.Select(v => (v.X, v.Y)).ToArray()).ToArray());
            return _buildings = f;
        }
    }

    /// <summary>
    /// Whether any player (a bot too, dead or spectating too) is within the world interest's leave distance of <paramref name="at"/>, flat: the
    /// distance beyond which no client is sent the animal (ADR 0025), so one farther than that from everybody can stand still unseen (#280).
    /// </summary>
    bool Watched(NVec3 at)
    {
        const float r2 = WorldInterest.LeaveM * WorldInterest.LeaveM;
        if (Prepared)
        {
            foreach (var a in _players)
            {
                var dx = a.Pos.X - at.X; var dz = a.Pos.Z - at.Z;
                if (dx * dx + dz * dz <= r2) return true;
            }
            return false;
        }
        foreach (var c in _ctx.Players.Values)
        {
            var p = c.Body.GlobalPosition;
            var dx = p.X - at.X; var dz = p.Z - at.Z;
            if (dx * dx + dz * dz <= r2) return true;
        }
        return false;
    }

    /// <summary>
    /// The geometry both kinds of animal ask: ground (the heightmap, as before: no ray), where a solid thing is (a bare ray, rarely, see <see cref="ClearPath"/>),
    /// whether a body fits at a spot and where the players on foot stand. The decisions (stop, steer, who yields) are the animal's own.
    /// </summary>
    class Ground : IAnimalWorld
    {
        protected readonly AnimalWorld _w;
        readonly bool _threats;

        public Ground(AnimalWorld world, bool threats) { _w = world; _threats = threats; }

        public float? GroundY(float x, float z)
        {
            var t = Prof.Begin(Prof.S.AnimalGround);
            float? ground = null;
            if (_w._map != null && _w._water.Depth(x, z) <= WadeM)
            {
                var y = _w._map.GroundHeight(x, z);
                if (!float.IsNaN(y)) ground = y;
            }
            Prof.End(Prof.S.AnimalGround, t);
            return ground;
        }

        /// <summary>The map's box, for the spawn point of a rare species.</summary>
        public FlightBounds? MapBounds => _w._map == null ? null : _w._mapBounds();

        public float NearestBuildingM(float x, float z, float maxM) => _w.Buildings.Nearest(x, z, maxM);

        /// <summary>The nearest living player or moving vehicle within the radius (flat distance); a pet fears nothing.</summary>
        public NVec3? NearestThreat(NVec3 from, float radius)
        {
            if (!_threats) return null;
            var t = Prof.Begin(Prof.S.AnimalThreat);
            NVec3? best = null;
            var bestSq = radius * radius;
            void Consider(NVec3 p)
            {
                var dx = p.X - from.X; var dz = p.Z - from.Z;
                var d = dx * dx + dz * dz;
                if (d < bestSq) { bestSq = d; best = p; }
            }
            if (_w.Prepared)
            {
                foreach (var a in _w._players)
                    if (a.Alive) Consider(a.Pos);
                foreach (var p in _w._carThreats) Consider(p);
            }
            else
            {
                foreach (var c in _w._ctx.Players.Values)
                    if (_w._ctx.IsAlive(c.Id)) Consider(c.Body.GlobalPosition.ToNumerics());
                foreach (var car in _w._fleet.Cars)
                    if (!car.Destroyed && MathF.Abs(car.State.Speed) > CarThreatSpeed) Consider(car.GlobalPosition.ToNumerics());
            }
            Prof.End(Prof.S.AnimalThreat, t);
            return best;
        }

        public NVec3? PositionOf(ushort player) => _w._ctx.Players.TryGetValue(player, out var c) ? c.Body.GlobalPosition.ToNumerics() : null;

        public bool Watched(NVec3 at)
        {
            var t = Prof.Begin(Prof.S.AnimalWatched);
            var watched = _w.Watched(at);
            Prof.End(Prof.S.AnimalWatched, t);
            return watched;
        }

        /// <summary>
        /// One ray at <paramref name="height"/> above the ground, along the ground: it runs from the ground height under the start to the ground height under the end, raised
        /// where the heightmap between bulges above that line, so a rise or a hollow is never mistaken for a wall.
        /// </summary>
        public float SolidAhead(NVec3 from, NVec3 dir, float maxM, float height)
        {
            var t = Prof.Begin(Prof.S.AnimalSolid);
            var d = GroundHug.Ray(_w._map, from, dir, maxM, height, out var a, out var b) ? _w._query().SolidDistance(MoverKind.Animal, a, b) : float.PositiveInfinity;
            Prof.End(Prof.S.AnimalSolid, t);
            return d;
        }

        /// <summary>Ground, and a sphere of the body's radius resting on it touches no building, fence, placed solid, trunk, closed door, parked vehicle or player.</summary>
        public bool Fits(float x, float z, float radius)
        {
            var t = Prof.Begin(Prof.S.AnimalFits);
            var fits = GroundY(x, z) is { } y && !_w._query().Overlaps(MoverKind.Animal, new Vector3(x, y + radius + 0.2f, z), radius);
            Prof.End(Prof.S.AnimalFits, t);
            return fits;
        }

        /// <summary>The players on foot and alive (one in a seat is part of its vehicle, whose solid box the ray finds).</summary>
        public void PlayersNear(NVec3 at, float radius, List<NVec3> into)
        {
            var t = Prof.Begin(Prof.S.AnimalPlayersNear);
            var r2 = radius * radius;
            if (_w.Prepared)
            {
                foreach (var a in _w._players)
                {
                    if (!a.OnFoot || !a.Alive) continue;
                    var dx = a.Pos.X - at.X; var dz = a.Pos.Z - at.Z;
                    if (dx * dx + dz * dz <= r2) into.Add(a.Pos);
                }
            }
            else
            {
                foreach (var c in _w._ctx.Players.Values)
                {
                    if (c.Car != null || !_w._ctx.IsAlive(c.Id)) continue;
                    var p = c.Body.GlobalPosition.ToNumerics();
                    var dx = p.X - at.X; var dz = p.Z - at.Z;
                    if (dx * dx + dz * dz <= r2) into.Add(p);
                }
            }
            Prof.End(Prof.S.AnimalPlayersNear, t);
        }
    }

    sealed class CompanionGround : Ground, ICompanionWorld
    {
        public CompanionGround(AnimalWorld world) : base(world, threats: false) { }

        /// <summary>Every living player who is not the owner and not on their team (everyone else in a free-for-all), bots too, on foot or in a seat.</summary>
        public void EnemiesNear(ushort owner, NVec3 at, float radius, List<CompanionSighting> into)
        {
            foreach (var c in _w._ctx.Players.Values)
            {
                if (c.Id == owner || _w._ctx.Match.AreFriends(owner, c.Id) || !_w._ctx.IsAlive(c.Id)) continue;
                var p = c.Body.GlobalPosition;
                var dx = p.X - at.X; var dz = p.Z - at.Z;
                if (dx * dx + dz * dz <= radius * radius) into.Add(new CompanionSighting(c.Id, p.ToNumerics()));
            }
        }
    }
}

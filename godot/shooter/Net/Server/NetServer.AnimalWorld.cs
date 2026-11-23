using Godot;
using Drycastle.Shooter.Characters;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Diagnostics;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Wildlife;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server;

/// <summary>
/// Animals meet the world (ADR 0028, the Animal row of the rule table): what stops a wild animal or a pet is asked of <see cref="WorldQuery"/> by the
/// <see cref="MoverKind.Animal"/> row; a player and another animal are footprints in ShooterCore (<see cref="Animal"/>, <see cref="AnimalCrowd"/>), not bodies.
/// </summary>
public partial class NetServer
{
    /// <summary>Every animal of the match, wildlife and pets: who an animal runs into (the two keepers put theirs in and take them out).</summary>
    readonly AnimalCrowd _animalCrowd = new();

    /// <summary>
    /// The geometry both kinds of animal ask: ground (the heightmap, as before: no ray), where a solid thing is (a bare ray, rarely, see <see cref="ClearPath"/>),
    /// whether a body fits at a spot and where the players on foot stand. The decisions (stop, steer, who yields) are the animal's own.
    /// </summary>
    BuildingFootprints? _buildingFootprints;

    /// <summary>The parcels that hold a building, as polygons (what a rare animal keeps away from, <see cref="IAnimalWorld.NearestBuildingM"/>). Built once per round start (<see cref="WildlifeRoundStart"/>).</summary>
    BuildingFootprints BuildingFootprints
    {
        get
        {
            if (_buildingFootprints != null) return _buildingFootprints;
            var f = new BuildingFootprints();
            if (Map != null)
                foreach (var p in Map.Parcels.Values)
                    if (p.BuildingIds.Count > 0 && p.Rings.Count > 0)
                        f.Add(p.Rings.Select(r => r.Select(v => (v.X, v.Y)).ToArray()).ToArray());
            return _buildingFootprints = f;
        }
    }

    abstract class AnimalGroundBase : IAnimalWorld
    {
        protected readonly NetServer _s;
        protected AnimalGroundBase(NetServer server) => _s = server;

        /// <summary>The deepest water the animal wades in.</summary>
        protected abstract float WadeM { get; }

        public float? GroundY(float x, float z)
        {
            var t = Prof.Begin(Prof.S.AnimalGround);
            float? ground = null;
            if (_s.Map != null && _s.Water.Depth(x, z) <= WadeM)
            {
                var y = _s.Map.GroundHeight(x, z);
                if (!float.IsNaN(y)) ground = y;
            }
            Prof.End(Prof.S.AnimalGround, t);
            return ground;
        }

        /// <summary>The map's box, for the spawn point of a rare species.</summary>
        public FlightBounds? MapBounds => _s.Map == null ? null : _s.MapFlightBounds;

        public float NearestBuildingM(float x, float z, float maxM) => _s.BuildingFootprints.Nearest(x, z, maxM);

        public abstract NVec3? NearestThreat(NVec3 from, float radius);

        public NVec3? PositionOf(ushort player) => _s._byId.TryGetValue(player, out var c) ? N(c.Body.GlobalPosition) : null;

        public bool Watched(NVec3 at)
        {
            var t = Prof.Begin(Prof.S.AnimalWatched);
            var watched = _s.AnimalWatched(at);
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
            var d = _s.GroundHugRay(from, dir, maxM, height, out var a, out var b) ? _s.Query.SolidDistance(MoverKind.Animal, a, b) : float.PositiveInfinity;
            Prof.End(Prof.S.AnimalSolid, t);
            return d;
        }

        /// <summary>Ground, and a sphere of the body's radius resting on it touches no building, fence, placed solid, trunk, closed door, parked vehicle or player.</summary>
        public bool Fits(float x, float z, float radius)
        {
            var t = Prof.Begin(Prof.S.AnimalFits);
            var fits = GroundY(x, z) is { } y && !_s.Query.Overlaps(MoverKind.Animal, new Vector3(x, y + radius + 0.2f, z), radius);
            Prof.End(Prof.S.AnimalFits, t);
            return fits;
        }

        /// <summary>The players on foot and alive (one in a seat is part of its vehicle, whose solid box the ray finds).</summary>
        public void PlayersNear(NVec3 at, float radius, List<NVec3> into)
        {
            var t = Prof.Begin(Prof.S.AnimalPlayersNear);
            var r2 = radius * radius;
            if (_s._animalsPrepared)
            {
                foreach (var a in _s._animalPlayers)
                {
                    if (!a.OnFoot || !a.Alive) continue;
                    var dx = a.Pos.X - at.X; var dz = a.Pos.Z - at.Z;
                    if (dx * dx + dz * dz <= r2) into.Add(a.Pos);
                }
            }
            else
            {
                foreach (var c in _s._byId.Values)
                {
                    if (c.Car != null || _s._match.Get(c.Id)?.Life != PlayerLife.Alive) continue;
                    var p = N(c.Body.GlobalPosition);
                    var dx = p.X - at.X; var dz = p.Z - at.Z;
                    if (dx * dx + dz * dz <= r2) into.Add(p);
                }
            }
            Prof.End(Prof.S.AnimalPlayersNear, t);
        }
    }

    /// <summary>A player as the animals see them this tick.</summary>
    readonly record struct AnimalPlayer(NVec3 Pos, bool Alive, bool OnFoot);

    /// <summary>
    /// Every player (bots too, in the order of <c>_byId</c>) and every moving vehicle as they stand while the entities tick, read once (<see cref="AnimalsPrepare"/>)
    /// instead of once per animal: each animal asked the match and the engine about every player, every tick. Only good between <see cref="AnimalsPrepare"/> and
    /// <see cref="AnimalsDone"/>, and only until a player is hurt or teleported in between (<see cref="_animalsPrepared"/>): the players are simulated later in
    /// the tick, so the animals get the answers they got before. Otherwise the world is asked as before.
    /// </summary>
    readonly List<AnimalPlayer> _animalPlayers = new();
    readonly List<NVec3> _animalCarThreats = new();
    bool _animalsFresh;
    int _animalsTeleports;
    /// <summary>Whether the list is the world as it is: made this tick, and since then nobody was hurt (a life may have ended) or put somewhere else (the Bigfoot throws, the Gang shoots while the entities tick).</summary>
    bool _animalsPrepared => _animalsFresh && Player.TeleportsAll == _animalsTeleports;

    /// <summary>Before the entities tick, with <see cref="BigfootPrepare"/> and <see cref="GangsPrepare"/>: the players and the moving vehicles the animals look at this tick.</summary>
    void AnimalsPrepare()
    {
        _animalPlayers.Clear();
        _animalCarThreats.Clear();
        foreach (var c in _byId.Values)
            _animalPlayers.Add(new AnimalPlayer(N(c.Body.GlobalPosition), _match.Get(c.Id)?.Life == PlayerLife.Alive, c.Car == null));
        foreach (var car in _fleet.Cars)
            if (!car.Destroyed && MathF.Abs(car.State.Speed) > WildlifeCarThreatSpeed) _animalCarThreats.Add(N(car.GlobalPosition));
        _animalsTeleports = Player.TeleportsAll;
        _animalsFresh = true;
    }

    /// <summary>After the animals have ticked: from here on the players move, so the world is asked directly again.</summary>
    void AnimalsDone() => _animalsFresh = false;
}

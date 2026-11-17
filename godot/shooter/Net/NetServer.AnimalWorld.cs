using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

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
            if (_s.Map == null || _s.Water.Depth(x, z) > WadeM) return null;
            var y = _s.Map.GroundHeight(x, z);
            return float.IsNaN(y) ? null : y;
        }

        /// <summary>The map's box, for the spawn point of a rare species.</summary>
        public FlightBounds? MapBounds => _s.Map == null ? null : _s.MapFlightBounds;

        public float NearestBuildingM(float x, float z, float maxM) => _s.BuildingFootprints.Nearest(x, z, maxM);

        public abstract NVec3? NearestThreat(NVec3 from, float radius);

        public NVec3? PositionOf(ushort player) => _s._byId.TryGetValue(player, out var c) ? N(c.Body.GlobalPosition) : null;

        public bool Watched(NVec3 at) => _s.AnimalWatched(at);

        /// <summary>
        /// One ray at <paramref name="height"/> above the ground, along the ground: it runs from the ground height under the start to the ground height under the end, raised
        /// where the heightmap between bulges above that line, so a rise or a hollow is never mistaken for a wall.
        /// </summary>
        public float SolidAhead(NVec3 from, NVec3 dir, float maxM, float height)
        {
            if (!_s.GroundHugRay(from, dir, maxM, height, out var a, out var b)) return float.PositiveInfinity;
            return _s.Query.SolidDistance(MoverKind.Animal, a, b);
        }

        /// <summary>Ground, and a sphere of the body's radius resting on it touches no building, fence, placed solid, trunk, closed door, parked vehicle or player.</summary>
        public bool Fits(float x, float z, float radius) =>
            GroundY(x, z) is { } y && !_s.Query.Overlaps(MoverKind.Animal, new Vector3(x, y + radius + 0.2f, z), radius);

        /// <summary>The players on foot and alive (one in a seat is part of its vehicle, whose solid box the ray finds).</summary>
        public void PlayersNear(NVec3 at, float radius, List<NVec3> into)
        {
            var r2 = radius * radius;
            foreach (var c in _s._byId.Values)
            {
                if (c.Car != null || _s._match.Get(c.Id)?.Life != PlayerLife.Alive) continue;
                var p = N(c.Body.GlobalPosition);
                var dx = p.X - at.X; var dz = p.Z - at.Z;
                if (dx * dx + dz * dz <= r2) into.Add(p);
            }
        }
    }
}

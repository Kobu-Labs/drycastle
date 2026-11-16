using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>
/// The footprint a vehicle runs a body over by (ADR 0028, GLOSSARY "Body shape"): the victim's Body footprint scaled to <see cref="Scale"/> (85%) about its centre, so a
/// bumper that only grazes a body's edge goes past. An animal's is its <see cref="BodyBox"/>; a player, citizen, gang member, worker or target dummy has the Body capsule's circle
/// (<see cref="MoveConsts.CapsuleRadius"/>). Tested with <see cref="CarFootprint"/>, swept along the tick's path.
/// </summary>
public readonly struct RunOverFootprint
{
    /// <summary>Share of the Body footprint that counts for run-over.</summary>
    public const float Scale = 0.85f;

    readonly Vector3 _feet;
    readonly float _radius;
    readonly BodyBox _box;
    readonly bool _isBox;

    RunOverFootprint(Vector3 feet, float radius, in BodyBox box, bool isBox) { _feet = feet; _radius = radius; _box = box; _isBox = isBox; }

    /// <summary>The Body capsule's circle round <paramref name="feet"/>, scaled.</summary>
    public static RunOverFootprint OfCapsule(Vector3 feet, float capsuleRadius = MoveConsts.CapsuleRadius) => new(feet, capsuleRadius * Scale, default, false);

    /// <summary>An animal's Body box, scaled about its centre; <paramref name="feetY"/> is the height of its feet.</summary>
    public static RunOverFootprint OfBody(in BodyBox box, float feetY) =>
        new(new Vector3(box.X, feetY, box.Z), 0, new BodyBox(box.X, box.Z, box.HalfW * Scale, box.HalfL * Scale, box.Yaw), true);

    /// <summary>The scaled circle's radius, 0 for a box.</summary>
    public float Radius => _radius;

    /// <summary>Whether the vehicle (box of half sizes <paramref name="halfWidth"/> and <paramref name="halfLength"/>, yaw <paramref name="yaw"/>) that went from <paramref name="from"/> to <paramref name="to"/> this tick touches it.</summary>
    public bool Hits(Vector3 from, Vector3 to, float yaw, float halfWidth, float halfLength) =>
        _isBox ? CarFootprint.Hits(from, to, yaw, halfWidth, halfLength, _box, _feet.Y) : CarFootprint.Hits(from, to, yaw, halfWidth, halfLength, _feet, _radius);
}

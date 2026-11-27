using System.Numerics;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Intel;
using Drycastle.ShooterCore.Scenery;

namespace Drycastle.ShooterCore.Cartel;

/// <summary>
/// The names the map gives the cartel's data (docs/cartel.md "Contact", "Simple jobs"): the two Runtime zone kinds, baked as candidate
/// points into zone_points.json (pipeline/config/rules.yaml <c>zones</c>), and the assets of the Contact's and the pallet's markers, placed objects of the map.
/// </summary>
public static class CartelZones
{
    /// <summary>Drop points: where a Delivery's Duffel or a loaded Cartel van is handed over.</summary>
    public const string DropKind = "cartel_drop";
    /// <summary>Hit spots: where a Hit's Rival dealer stands outdoors with his guards.</summary>
    public const string HitKind = "cartel_hit";
    /// <summary>
    /// The Contact's marker (pipeline primitive, kits/primitives.py): the first placed object of it (lowest id) is the Contact; a map without one has no
    /// cartel. It stops nothing (<see cref="Colliders.ObjectColliders.IsPassThrough(string)"/>), so no weapon is tested against him; the game hides it and draws him.
    /// </summary>
    public const string ContactAsset = "prim:cartel_contact";
    /// <summary>
    /// The Cartel pallet's marker (pipeline primitive, kits/primitives.py): the first placed object of it (lowest id) is the pallet a Delivery's Duffel lies on; a map without one has no Delivery.
    /// Unlike the Contact's it is solid like any placed prop (a low box the size of the pallet); the game hides it and draws the Gang Warfare pallet there.
    /// </summary>
    public const string PalletAsset = "prim:cartel_pallet";
}

/// <summary>
/// Where the Contact stands and the way he looks, in Godot ground axes (x east, y the terrain height of his marker, z = -north).
/// <see cref="Facing"/> is a unit vector on the ground (x, z).
/// </summary>
public readonly record struct ContactPose(Vector3 Position, Vector2 Facing)
{
    /// <summary>The turn about the vertical axis, degrees, of a node whose forward (-Z) looks along <see cref="Facing"/> (Godot's Y rotation).</summary>
    public float YawDeg => MathF.Atan2(-Facing.X, -Facing.Y) * 180f / MathF.PI;

    /// <summary>
    /// The pose of the Contact whose marker stands at <paramref name="origin"/> turned <paramref name="objectYawDeg"/> (the placed object's Godot Y rotation):
    /// he faces <see cref="ContactFile.Facing"/> of the marker's own frame, turned with it.
    /// </summary>
    public static ContactPose FromObject(Vector3 origin, float objectYawDeg, ContactFile data)
        => new(origin, Vector2.Normalize(IntelRoom.Rotate(new Vector2(data.Facing[0], data.Facing[1]), objectYawDeg)));

    /// <summary>The distance on the ground (x, z) from the Contact to <paramref name="p"/>, metres.</summary>
    public float GroundDistance(Vector3 p) => Vector2.Distance(new Vector2(Position.X, Position.Z), new Vector2(p.X, p.Z));
}

/// <summary>
/// The cartel's map data as the server needs it (static, never sent: a client reads the same map): the Contact's and the pallet's pose and the candidate points of the
/// Drop point and Hit spot zones. Pure, like <see cref="ZonePoints"/>: the server owns the random generator; a Job claims its point with <see cref="CartelSpots.Claim"/>. A map
/// without a Contact has no cartel (<see cref="HasCartel"/> false, no error); a map without a zone of a kind has no point of it, and a claim refuses (the Job does not start).
/// <para>
/// Points of one kind can lie in one drawn zone, so "two jobs never share a drop point" is kept by distance too: the claim's filter is <see cref="Apart"/> (the running jobs'
/// points) with its other rules (far from the Contact or the objective).
/// </para>
/// </summary>
public sealed class CartelMap
{
    public static readonly CartelMap Empty = new(null, Array.Empty<Vector3>(), Array.Empty<Vector3>(), null);

    CartelMap(ContactPose? contact, IReadOnlyList<Vector3> drops, IReadOnlyList<Vector3> hits, PalletPose? pallet)
    {
        Contact = contact; DropPoints = drops; HitSpots = hits; Pallet = pallet;
    }

    /// <summary>The Contact, or null on a map that has none (then there is no cartel).</summary>
    public ContactPose? Contact { get; }
    public bool HasCartel => Contact != null;
    /// <summary>The Cartel pallet a Delivery's Duffel lies on (the first placed marker, <see cref="CartelZones.PalletAsset"/>), or null on a map without one (then there is no Delivery).</summary>
    public PalletPose? Pallet { get; }
    /// <summary>The candidate points of the <c>cartel_drop</c> zones, Godot ground axes; empty when none is drawn.</summary>
    public IReadOnlyList<Vector3> DropPoints { get; }
    /// <summary>The candidate points of the <c>cartel_hit</c> zones, Godot ground axes; empty when none is drawn.</summary>
    public IReadOnlyList<Vector3> HitSpots { get; }

    public static CartelMap From(ZonePoints points, ContactPose? contact, PalletPose? pallet = null)
        => new(contact, points.Points(CartelZones.DropKind), points.Points(CartelZones.HitKind), pallet);

    /// <summary>A uniform pick among the points of <paramref name="pool"/> that <paramref name="allowed"/> accepts; null when none does.</summary>
    public static Vector3? Pick(IReadOnlyList<Vector3> pool, Random rng, Func<Vector3, bool>? allowed = null)
    {
        var count = 0;
        foreach (var p in pool) if (allowed == null || allowed(p)) count++;
        if (count == 0) return null;
        var k = rng.Next(count);
        foreach (var p in pool)
            if ((allowed == null || allowed(p)) && k-- == 0) return p;
        return null;
    }

    /// <summary>A filter for a pick: true for a point at least <paramref name="minM"/> on the ground (x, z) from every one of <paramref name="others"/>.</summary>
    public static Func<Vector3, bool> Apart(IEnumerable<Vector3> others, float minM)
    {
        var list = others.Select(o => new Vector2(o.X, o.Z)).ToArray();
        var min2 = minM * minM;
        return p =>
        {
            var g = new Vector2(p.X, p.Z);
            foreach (var o in list) if (Vector2.DistanceSquared(g, o) < min2) return false;
            return true;
        };
    }
}

/// <summary>
/// contact.json: how the Contact stands on his marker and how a client draws him. Every number of the file lives there with its comment.
/// </summary>
public sealed class ContactFile : IVersioned
{
    public int Version { get; set; }
    /// <summary>The way he looks: a unit vector [x, z] in the marker's own frame (Godot axes, before its turn). [0, 1] is the marker's front (its arrow).</summary>
    public float[] Facing { get; set; } = { 0f, 1f };
    public ContactLook View { get; set; } = new();

    public static ContactFile Parse(string json) => Validate(DataFile.Parse<ContactFile>(json, "contact.json"));

    public static ContactFile Validate(ContactFile f)
    {
        void Need(bool ok, string what) { if (!ok) throw new DataFileException($"contact.json: {what}"); }
        Need(f.Facing.Length == 2 && MathF.Abs(f.Facing[0] * f.Facing[0] + f.Facing[1] * f.Facing[1] - 1f) <= 0.05f, "facing must be a unit vector [x, z]");
        var v = f.View;
        Need(v.Model != "" && v.Atlas != "", "view.model and view.atlas are named");
        Need(v.Scale > 0 && v.CapsuleHeightM > 0, "view.scale and view.capsule_height_m are positive");
        return f;
    }
}

/// <summary>
/// The Contact's look (client only): a Gang Warfare character (assets-private/synty/glb, as a Gang member's) in one of the pack's atlases, playing an
/// idle clip; without the pack a capsule of <see cref="CapsuleColour"/> with a nose on the side he faces.
/// </summary>
public sealed class ContactLook
{
    public string Model { get; set; } = "gang_boss_01";
    public string Atlas { get; set; } = "gang-warfare/textures/PolygonGangWarfare_02_B.png";
    /// <summary>The looping clip under anim/baked ("" plays nothing: the rest pose).</summary>
    public string Idle { get; set; } = "idle_standing_masc_g";
    public float Scale { get; set; } = 1f;
    public string CapsuleColour { get; set; } = "#e8e2d0";
    public float CapsuleHeightM { get; set; } = 1.8f;
}

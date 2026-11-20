using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// The physics space as a trailer's step sees it (<see cref="ITrailerWorld"/>), as <see cref="DriveProbe"/> is for a driven vehicle: is the place the trailer would stand in solid
/// (ground vehicle row of the rule table: terrain, buildings, trunks, placed solids, closed doors, the demon), and what is the ground under a point. It asks the static world and the demon
/// only, never the vehicles' solid boxes: the trailer's own tractor (whose box its footprint overlaps at a deep articulation) and every other vehicle are the vehicle contact pass's business
/// (<c>NetServer.ResolveVehicleContacts</c>, which skips a hitched pair). The footprint is the whole box (drawbar included) swept clear of the ground it stands on
/// (<see cref="Clearance"/>) and tilted with its slope (the ground under its two ends), so a slope or a kerb passes under it and a wall or a tree does not. It is on no layer: nothing hits it.
/// </summary>
public sealed partial class TrailerProbe : Node3D, ITrailerWorld
{
    /// <summary>The box is swept this far above the ground under it, metres (the wheels' business: kerbs and banks pass under).</summary>
    public const float Clearance = 0.5f;
    float _clearance = Clearance; // the type's own (trailer.probe_clearance), above its low parts
    const float RayLift = 1.5f, RayReach = 8f;
    /// <summary>The ground is sampled this far apart at most along a box or a walk, metres: with <see cref="RayLift"/> that follows a slope of about 55 degrees.</summary>
    const float SampleEvery = 1f;

    static readonly uint WorldMask = CollisionRules.StaticMask(MoverKind.GroundVehicle);
    static readonly uint SweepMask = CollisionRules.SweepMask(MoverKind.GroundVehicle);

    VehicleDef _def = null!;
    BoxShape3D _box = null!;
    readonly PhysicsShapeQueryParameters3D _query = new();
    readonly PhysicsRayQueryParameters3D _ray = new();

    public static TrailerProbe Make(VehicleDef def) => new() { Name = "TrailerProbe", _def = def };

    public override void _Ready()
    {
        if (_def.Trailer is { ProbeClearance: > 0 } t) _clearance = t.ProbeClearance;
        var height = MathF.Max(0.2f, _def.Height - _clearance);
        _box = new BoxShape3D { Size = new Vector3(_def.HalfWidth * 2, height, _def.HalfLength * 2) };
        _query.Shape = _box; _query.CollisionMask = SweepMask;
        _ray.CollisionMask = WorldMask;
    }

    /// <summary>
    /// The height of the ground at (<paramref name="x"/>, <paramref name="z"/>), searched from <paramref name="hint"/> upwards a little (<see cref="RayLift"/>) and down a long way; the hint itself
    /// when there is none. The hint must be near the ground there: a ray that starts under the terrain finds nothing. For a point some metres from where the height is known, use <see cref="GroundWalk"/>.
    /// </summary>
    public float GroundY(float x, float z, float hint)
    {
        _ray.From = new Vector3(x, hint + RayLift, z); _ray.To = new Vector3(x, hint - RayReach, z);
        var r = GetWorld3D().DirectSpaceState.IntersectRay(_ray);
        if (r.Count == 0) { _misses++; return hint; }
        return r["position"].AsVector3().Y;
    }

    /// <summary>
    /// The height of the ground at (<paramref name="x1"/>, <paramref name="z1"/>), found by walking there from (<paramref name="x0"/>, <paramref name="z0"/>) where it is about <paramref name="hint"/>:
    /// a sample every <see cref="SampleEvery"/> at most, each searched from the one before. It follows a slope of any length up to about 55 degrees (#377: the far end of a 12.6 m cart on a dyke is
    /// metres above its near end), and never jumps onto a roof or an overhang as one ray from high up would. A sheer face higher than <see cref="RayLift"/> is not climbed: the height stays the foot's.
    /// </summary>
    public float GroundWalk(float x0, float z0, float x1, float z1, float hint)
    {
        var n = Math.Max(1, (int)MathF.Ceiling(MathF.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0)) / SampleEvery));
        var h = hint;
        for (var i = 1; i <= n; i++) { var t = i / (float)n; h = GroundY(x0 + (x1 - x0) * t, z0 + (z1 - z0) * t, h); }
        return h;
    }

    /// <summary>The ground under a box along its length: at its two ends, the most it stands over the line between them (a crest), and the slope of that line.</summary>
    public readonly record struct GroundFit(float Front, float Rear, float Lift, float Pitch)
    {
        /// <summary>The height the box's underside is measured from at its centre: the line between the ends, raised over the crest.</summary>
        public float Mid => (Front + Rear) / 2 + Lift;
    }

    /// <summary>
    /// The ground under <paramref name="box"/>, walked outwards from its centre (where it is about <paramref name="y"/>) to both ends, a sample every <see cref="SampleEvery"/> at most, each searched
    /// from its neighbour's height. The server's tilt of a trailer (its hit volume, #482) is this fit's too.
    /// </summary>
    public GroundFit Fit(in BodyBox box, float y)
    {
        var (s, c) = MathF.SinCos(box.Yaw);
        var m = Math.Max(1, (int)MathF.Ceiling(box.HalfL / SampleEvery));
        var step = box.HalfL / m;
        var gc = GroundY(box.X, box.Z, y);
        Span<float> front = stackalloc float[m], rear = stackalloc float[m];
        var hf = gc; var hr = gc;
        for (var k = 1; k <= m; k++)
        {
            var d = k * step; // forward is -Z turned by the yaw
            front[k - 1] = hf = GroundY(box.X - s * d, box.Z - c * d, hf);
            rear[k - 1] = hr = GroundY(box.X + s * d, box.Z + c * d, hr);
        }
        var gf = front[m - 1]; var gr = rear[m - 1];
        // a long box over a crest or a dyke between its ends would sit in the ground: it is lifted by the most any sample stands over the line between the two ends
        var lift = MathF.Max(0f, gc - (gf + gr) / 2);
        for (var k = 1; k < m; k++)
        {
            var t = k / (float)m / 2; // 0 at the centre, 0.5 at an end
            lift = MathF.Max(lift, front[k - 1] - (gr + (gf - gr) * (0.5f + t)));
            lift = MathF.Max(lift, rear[k - 1] - (gr + (gf - gr) * (0.5f - t)));
        }
        return new GroundFit(gf, gr, lift, MathF.Atan2(gf - gr, 2 * box.HalfL));
    }

    /// <summary>
    /// How a trailer whose origin is at (<paramref name="x"/>, <paramref name="z"/>), about <paramref name="y"/> high, facing <paramref name="yaw"/>, lies on the ground (<see cref="TrailerAttitude"/>). With
    /// <paramref name="hitchY"/> (the height of the tractor's hitch, world) it is pulled: the axle's ground is walked to from the eye, and the pitch is the hitch's height over it; without, it is loose: the
    /// axle's ground is walked to from the origin and the eye's from the axle. The server's hull and the client's drawing and its copy of the hull both ask this (#482).
    /// </summary>
    public TrailerAttitude.Result Attitude(in TowGeometry g, float x, float z, float y, float yaw, float? hitchY)
    {
        var (s, c) = MathF.SinCos(yaw);
        var back = g.EyeZ + g.Drawbar; // the axle's distance behind the origin
        float ax = x + back * s, az = z + back * c, ex = x + g.EyeZ * s, ez = z + g.EyeZ * c;
        float gAxle, eyeAbove;
        if (hitchY is { } h)
        {
            gAxle = GroundWalk(ex, ez, ax, az, h - g.EyeY); // the axle's ground is metres off the hitch's on a dyke, further than one ray finds (#377)
            eyeAbove = h - gAxle;
        }
        else
        {
            gAxle = GroundWalk(x, z, ax, az, y);
            eyeAbove = g.EyeY + GroundWalk(ax, az, ex, ez, gAxle) - gAxle;
        }
        var half = g.HalfWidth * 0.9f;
        var gl = GroundY(ax - c * half, az + s * half, gAxle + 0.5f);
        var gr = GroundY(ax + c * half, az - s * half, gAxle + 0.5f);
        return TrailerAttitude.Of(g, yaw, ax, az, gAxle, eyeAbove, gl, gr, half);
    }

    // what the last query was, for the block log (#377: why a towed cart sticks on a dyke) and the volume overlay
    int _misses;
    double _lastLogS = -10;
    /// <summary>The last swept box: where it was, how it was turned and its size.</summary>
    public Transform3D LastBoxAt { get; private set; }
    public Vector3 LastBoxSize => _box.Size;
    public bool LastBlocked { get; private set; }

    /// <summary>The box (the footprint lying along the ground under it, clear of it by the clearance) at <paramref name="at"/> is in something solid.</summary>
    Godot.Collections.Dictionary? Overlap(in BodyBox at, float y, out GroundFit fit, out Vector3 centre)
    {
        fit = Fit(at, y);
        centre = new Vector3(at.X, fit.Mid + _clearance + _box.Size.Y / 2, at.Z);
        _query.Transform = new Transform3D(Basis.FromEuler(new Vector3(fit.Pitch, at.Yaw, 0)), centre);
        var hits = GetWorld3D().DirectSpaceState.IntersectShape(_query, 1);
        return hits.Count > 0 ? hits[0] : null;
    }

    /// <summary>
    /// The trailer's new place is in something solid, and the place it stands in is not. A trailer that already stands in a solid (shoved there by a bump, set down there by the Bigfoot, a fit
    /// gone wrong) is not held: holding it holds its tractor too, for good, since only the next place is asked about (#377). It is pulled out instead.
    /// </summary>
    public bool Blocked(in BodyBox from, in BodyBox to, float y)
    {
        _misses = 0;
        var hit = Overlap(to, y, out var fit, out var centre);
        LastBoxAt = _query.Transform;
        LastBlocked = hit != null;
        if (hit == null) return false;
        var misses = _misses;
        var stuck = Overlap(from, y, out _, out _) != null;
        _misses = misses;
        LogBlock(hit, to, y, fit, centre, stuck);
        LastBlocked = !stuck;
        return LastBlocked;
    }

    /// <summary>One line a second at most when the box is blocked: what it hit, and what the ground samples said (rays that found nothing read as flat ground at the hint).</summary>
    void LogBlock(Godot.Collections.Dictionary hit, in BodyBox to, float y, GroundFit fit, Vector3 centre, bool stuck)
    {
        var now = Time.GetTicksMsec() / 1000.0;
        if (now - _lastLogS < 1) return;
        _lastLogS = now;
        var who = hit.TryGetValue("collider", out var c) && c.AsGodotObject() is Node n ? $"{n.Name} ({n.GetType().Name}, layer {(n as CollisionObject3D)?.CollisionLayer})" : "?";
        GD.Print($"server: trailer {_def.Id} {(stuck ? "already in" : "blocked by")} {who} at ({to.X:F1},{to.Z:F1}) yaw {to.Yaw * 180f / MathF.PI:F0}{(stuck ? " (not held: it stands in it)" : "")}: hint y {y:F2}, ground front {fit.Front:F2} rear {fit.Rear:F2}, lift {fit.Lift:F2}, pitch {fit.Pitch * 180f / MathF.PI:F1} deg, box centre y {centre.Y:F2}, ray misses {_misses}");
    }
}

using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>
/// What of a vehicle touches another (#253): its hit box (<see cref="VehicleBox"/>: an upright box over the footprint, centre on the ground under
/// the origin, yaw as in <see cref="CarState"/>) and, for a helicopter, its rotor disc (a thin horizontal disc <see cref="RotorHeight"/> above the
/// origin; the same disc the flight probe runs through the world). A type with no rotor has a radius of 0. A type with a <see cref="VehicleDef.Hull"/> (<paramref name="Hull"/>) has those boxes
/// instead of the one, turned by <paramref name="Pitch"/> and <paramref name="Roll"/> as well as the yaw; the rotor disc stays level.
/// </summary>
public readonly record struct ContactShape(Vector3 Feet, float Yaw, float HalfWidth, float HalfLength, float Height, float RotorRadius = 0, float RotorHeight = 0, float RotorThickness = 0,
    float Pitch = 0, float Roll = 0, HullBox[]? Hull = null);

/// <summary>Two vehicles overlap. <see cref="Normal"/> is a unit vector from the first to the second, the way out that needs the least movement; <see cref="Depth"/> is how far in they are, metres.</summary>
public readonly record struct VehicleContactHit(Vector3 Normal, float Depth);

/// <summary>The bump of one contact: how far each vehicle is moved apart, and the velocity each one gains.</summary>
public readonly record struct VehicleBump(Vector3 MoveA, Vector3 MoveB, Vector3 DeltaVA, Vector3 DeltaVB)
{
    /// <summary>Speed the first vehicle lost in the bump, m/s: what the crash damage rules read (<see cref="VehiclesFile.CrashMinSpeedLoss"/>).</summary>
    public float SpeedLossA => DeltaVA.Length();
    public float SpeedLossB => DeltaVB.Length();
}

/// <summary>One side of a bump: its mass, its velocity, and whether it stays on the ground (a ground vehicle cannot be pushed up or down).</summary>
public readonly record struct BumpBody(float Mass, Vector3 Velocity, bool Flies);

/// <summary>
/// Vehicle-vs-vehicle collision (#253), server side: the overlap test between two <see cref="ContactShape"/>s and the bump that follows. Ground
/// vehicles meet in the plane only; a pair with a helicopter in it may also meet vertically (a skid on a roof or a bonnet; a rotor disc, being thin, is always pushed aside). Nothing here
/// knows Godot, so the rules are tested without a scene.
/// </summary>
public static class VehicleContact
{
    /// <summary>The way out of the overlap of <paramref name="a"/> and <paramref name="b"/> that needs the least movement, or null when they do not touch. <paramref name="vertical"/>: allow the way out to be up or down (a pair with a helicopter in it).</summary>
    public static VehicleContactHit? Test(in ContactShape a, in ContactShape b, bool vertical)
    {
        VehicleContactHit? best = null;
        foreach (var pa in Parts(a))
        foreach (var pb in Parts(b))
        {
            var hit = Overlap(pa, pb, vertical);
            if (hit is { } h && (best == null || h.Depth > best.Value.Depth)) best = h; // the deepest part pair decides
        }
        return best;
    }

    /// <summary>
    /// The bump of the contact <paramref name="hit"/> between <paramref name="a"/> and <paramref name="b"/>: the two are moved apart in inverse
    /// proportion to their mass, and the closing speed is exchanged along the normal with <paramref name="restitution"/> (0 = stick, 1 = bounce).
    /// A vehicle that is already moving apart gets no velocity change, only the move.
    /// </summary>
    public static VehicleBump Bump(in VehicleContactHit hit, in BumpBody a, in BumpBody b, float restitution)
    {
        var n = hit.Normal;
        var up = MathF.Abs(n.Y) > 0.5f;
        var ia = InverseMass(a, up); var ib = InverseMass(b, up);
        var sum = ia + ib;
        if (sum <= 0) return default;
        var moveA = -n * (hit.Depth * ia / sum);
        var moveB = n * (hit.Depth * ib / sum);
        var closing = Vector3.Dot(b.Velocity - a.Velocity, n); // negative: coming together
        if (closing >= 0) return new VehicleBump(moveA, moveB, Vector3.Zero, Vector3.Zero);
        var j = -(1 + restitution) * closing / sum;
        return new VehicleBump(moveA, moveB, -n * (j * ia), n * (j * ib));
    }

    static float InverseMass(in BumpBody body, bool vertical) => !body.Flies && vertical ? 0 : 1f / MathF.Max(body.Mass, 1f);

    // ---- parts

    /// <summary>A box of any attitude: its centre, its three unit axes (as turned) and its half sizes along them.</summary>
    readonly record struct Obb(Vector3 Centre, Vector3 X, Vector3 Y, Vector3 Z, Vector3 Half)
    {
        /// <summary>Half the box's extent along a unit direction: how far it reaches from its centre that way.</summary>
        public float Support(Vector3 dir) => Half.X * MathF.Abs(Vector3.Dot(dir, X)) + Half.Y * MathF.Abs(Vector3.Dot(dir, Y)) + Half.Z * MathF.Abs(Vector3.Dot(dir, Z));
    }

    readonly record struct Part(Vector3 Centre, float Yaw, float HalfWidth, float HalfLength, float Radius, float Y0, float Y1, Obb? Tilted = null)
    {
        public bool Disc => Radius > 0;
    }

    /// <summary>One box of a hull, in the world: turned by the shape's whole attitude (<see cref="FlightMath.Rotate"/>).</summary>
    static Part TiltedPart(in ContactShape s, in HullBox b)
    {
        var centre = s.Feet + FlightMath.Rotate(new Vector3(b.Cx, (b.Y0 + b.Y1) / 2, b.Cz), s.Yaw, s.Pitch, s.Roll);
        var half = new Vector3(b.HalfWidth, (b.Y1 - b.Y0) / 2, b.HalfLength);
        var box = new Obb(centre, FlightMath.Rotate(Vector3.UnitX, s.Yaw, s.Pitch, s.Roll), FlightMath.Rotate(Vector3.UnitY, s.Yaw, s.Pitch, s.Roll), FlightMath.Rotate(Vector3.UnitZ, s.Yaw, s.Pitch, s.Roll), half);
        var up = box.Support(Vector3.UnitY); // the box's reach up and down: the height band it spans
        return new Part(centre, s.Yaw, half.X, half.Z, 0, centre.Y - up, centre.Y + up, box);
    }

    static Obb ObbOf(in Part p)
    {
        if (p.Tilted is { } t) return t;
        var (right, fwd) = Axes(p.Yaw);
        return new Obb(new Vector3(p.Centre.X, (p.Y0 + p.Y1) / 2, p.Centre.Z), right, Vector3.UnitY, fwd, new Vector3(p.HalfWidth, (p.Y1 - p.Y0) / 2, p.HalfLength));
    }

    static IEnumerable<Part> Parts(ContactShape s)
    {
        if (s.Hull is { } hull)
            foreach (var b in hull) yield return TiltedPart(s, b);
        else
            yield return new Part(s.Feet, s.Yaw, s.HalfWidth, s.HalfLength, 0, s.Feet.Y, s.Feet.Y + s.Height);
        if (s.RotorRadius > 0)
        {
            var y = s.Feet.Y + s.RotorHeight;
            yield return new Part(s.Feet, s.Yaw, 0, 0, s.RotorRadius, y - s.RotorThickness / 2, y + s.RotorThickness / 2);
        }
    }

    static VehicleContactHit? Overlap(in Part a, in Part b, bool vertical)
    {
        var dy = MathF.Min(a.Y1, b.Y1) - MathF.Max(a.Y0, b.Y0);
        if (dy <= 0) return null;
        if (a.Tilted != null || b.Tilted != null) return TiltedOverlap(a, b); // a box of a tilting hull: the way out may be any way (only helicopters have one, and a pair with one may meet vertically)
        var plane = a.Disc switch
        {
            true when b.Disc => DiscDisc(a, b),
            true => Flip(BoxDisc(b, a)),
            false when b.Disc => BoxDisc(a, b),
            _ => BoxBox(a, b),
        };
        if (plane == null) return null;
        var h = plane.Value;
        // hulls only: a rotor disc is thin, so it would always be pushed up or down by its thickness; it is pushed aside like the rest
        if (!vertical || a.Disc || b.Disc || h.Depth <= dy) return h;
        // less to move vertically: up when the second part is the higher one
        var up = (b.Y0 + b.Y1) >= (a.Y0 + a.Y1);
        return new VehicleContactHit(up ? Vector3.UnitY : -Vector3.UnitY, dy);
    }

    /// <summary>Two parts of which one is a box of a tilting hull: boxes by the separating axis test in 3D, a rotor disc against a box by the box's nearest point to the disc's centre.</summary>
    static VehicleContactHit? TiltedOverlap(in Part a, in Part b)
    {
        if (a.Disc) return Flip(DiscObb(a, ObbOf(b)));
        if (b.Disc) return DiscObb(b, ObbOf(a)); // its normal points from the box to the disc: here the box is first
        return ObbObb(ObbOf(a), ObbOf(b));
    }

    /// <summary>The way out of the overlap of two boxes of any attitude that needs the least movement (the normal points from the first to the second), or null when they do not touch: 15 axes.</summary>
    static VehicleContactHit? ObbObb(in Obb a, in Obb b)
    {
        var d = b.Centre - a.Centre;
        var best = float.PositiveInfinity; var normal = Vector3.UnitY;
        Span<Vector3> axes = stackalloc Vector3[15];
        Span<Vector3> ea = stackalloc Vector3[] { a.X, a.Y, a.Z };
        Span<Vector3> eb = stackalloc Vector3[] { b.X, b.Y, b.Z };
        var n = 0;
        for (var i = 0; i < 3; i++) { axes[n++] = ea[i]; axes[n++] = eb[i]; }
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++) axes[n++] = Vector3.Cross(ea[i], eb[j]);
        for (var k = 0; k < n; k++)
        {
            var len = axes[k].Length();
            if (len < 1e-4f) continue; // parallel edges: no axis of their own
            var axis = axes[k] / len;
            var along = Vector3.Dot(d, axis);
            var overlap = a.Support(axis) + b.Support(axis) - MathF.Abs(along);
            if (overlap <= 0) return null; // a separating axis
            if (overlap < best) { best = overlap; normal = along < 0 ? -axis : axis; }
        }
        return new VehicleContactHit(normal, best);
    }

    /// <summary>
    /// A level rotor disc against a box of any attitude: the box's point nearest the disc's centre, within the disc's height band and its radius. The way out is sideways, from that point to the
    /// centre (the normal points from the box to the disc). An approximation: a box that crosses the band only away from that nearest point is missed (a rotor that meets a tilted hull is not a case that matters).
    /// </summary>
    static VehicleContactHit? DiscObb(in Part disc, in Obb box)
    {
        var pd = new Vector3(disc.Centre.X, (disc.Y0 + disc.Y1) / 2, disc.Centre.Z);
        var rel = pd - box.Centre;
        var l = new Vector3(Vector3.Dot(rel, box.X), Vector3.Dot(rel, box.Y), Vector3.Dot(rel, box.Z));
        var c = new Vector3(Math.Clamp(l.X, -box.Half.X, box.Half.X), Math.Clamp(l.Y, -box.Half.Y, box.Half.Y), Math.Clamp(l.Z, -box.Half.Z, box.Half.Z));
        var q = box.Centre + box.X * c.X + box.Y * c.Y + box.Z * c.Z;
        if (MathF.Abs(q.Y - pd.Y) > (disc.Y1 - disc.Y0) / 2) return null;
        var flat = Flat(pd - q);
        var dist = flat.Length();
        if (dist > 1e-4f)
        {
            var depth = disc.Radius - dist;
            return depth <= 0 ? null : new VehicleContactHit(flat / dist, depth);
        }
        // the disc's centre is over the box: out through the side its centre is nearest
        var away = Flat(rel);
        var alen = away.Length();
        away = alen < 1e-4f ? Vector3.UnitX : away / alen;
        return new VehicleContactHit(away, disc.Radius + box.Support(away) - MathF.Max(0, Vector3.Dot(rel, away)));
    }

    static VehicleContactHit? Flip(VehicleContactHit? h) => h is { } x ? new VehicleContactHit(-x.Normal, x.Depth) : null;

    static (Vector3 Right, Vector3 Fwd) Axes(float yaw) => (new Vector3(MathF.Cos(yaw), 0, -MathF.Sin(yaw)), new Vector3(-MathF.Sin(yaw), 0, -MathF.Cos(yaw)));

    static VehicleContactHit? DiscDisc(in Part a, in Part b)
    {
        var d = Flat(b.Centre - a.Centre);
        var dist = d.Length();
        var depth = a.Radius + b.Radius - dist;
        if (depth <= 0) return null;
        return new VehicleContactHit(dist < 1e-4f ? Vector3.UnitX : d / dist, depth);
    }

    /// <summary>A box (first) and a disc (second); the normal points from the box to the disc.</summary>
    static VehicleContactHit? BoxDisc(in Part box, in Part disc)
    {
        var (right, fwd) = Axes(box.Yaw);
        var d = Flat(disc.Centre - box.Centre);
        var lx = Vector3.Dot(d, right); var lz = Vector3.Dot(d, fwd);
        var cx = Math.Clamp(lx, -box.HalfWidth, box.HalfWidth); var cz = Math.Clamp(lz, -box.HalfLength, box.HalfLength);
        var ox = lx - cx; var oz = lz - cz;
        var dist = MathF.Sqrt(ox * ox + oz * oz);
        if (dist > 1e-4f)
        {
            var depth = disc.Radius - dist;
            if (depth <= 0) return null;
            return new VehicleContactHit((right * ox + fwd * oz) / dist, depth);
        }
        // the disc's centre is inside the box: out through the nearest side
        var px = box.HalfWidth - MathF.Abs(lx); var pz = box.HalfLength - MathF.Abs(lz);
        return px < pz
            ? new VehicleContactHit(right * (lx < 0 ? -1 : 1), px + disc.Radius)
            : new VehicleContactHit(fwd * (lz < 0 ? -1 : 1), pz + disc.Radius);
    }

    static VehicleContactHit? BoxBox(in Part a, in Part b)
    {
        var (ra, fa) = Axes(a.Yaw); var (rb, fb) = Axes(b.Yaw);
        var d = Flat(b.Centre - a.Centre);
        var best = float.PositiveInfinity; var normal = Vector3.UnitX;
        foreach (var axis in new[] { ra, fa, rb, fb })
        {
            var radA = a.HalfWidth * MathF.Abs(Vector3.Dot(axis, ra)) + a.HalfLength * MathF.Abs(Vector3.Dot(axis, fa));
            var radB = b.HalfWidth * MathF.Abs(Vector3.Dot(axis, rb)) + b.HalfLength * MathF.Abs(Vector3.Dot(axis, fb));
            var along = Vector3.Dot(d, axis);
            var overlap = radA + radB - MathF.Abs(along);
            if (overlap <= 0) return null; // a separating axis
            if (overlap < best) { best = overlap; normal = along < 0 ? -axis : axis; }
        }
        return new VehicleContactHit(normal, best);
    }

    static Vector3 Flat(Vector3 v) => new(v.X, 0, v.Z);
}

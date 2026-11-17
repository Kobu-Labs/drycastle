using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>
/// An animal's Body shape on the ground plane (ADR 0028, GLOSSARY "Body shape"): a box centred <c>ahead</c> of its position, its length along the heading, its
/// width across. What contact is tested against (an animal that walks into a player or another animal); a weapon is tested against the Hit shape instead.
/// Flat: heights are not looked at. Godot axes (x east, z south); yaw 0 faces -Z.
/// </summary>
public readonly struct BodyBox
{
    public readonly float X, Z, HalfW, HalfL, Yaw;

    public BodyBox(float x, float z, float halfW, float halfL, float yaw) { X = x; Z = z; HalfW = halfW; HalfL = halfL; Yaw = yaw; }

    /// <summary>The box of a body of <paramref name="width"/> x <paramref name="length"/> metres whose centre lies <paramref name="ahead"/> of <paramref name="position"/> (negative: behind).</summary>
    public static BodyBox Of(Vector3 position, float yaw, float width, float length, float ahead)
    {
        var (s, c) = MathF.SinCos(yaw);
        return new BodyBox(position.X - s * ahead, position.Z - c * ahead, width / 2, length / 2, yaw);
    }

    /// <summary>Unit vectors of the heading and of its right hand side.</summary>
    Vector2 Fwd { get { var (s, c) = MathF.SinCos(Yaw); return new Vector2(-s, -c); } }
    Vector2 Right { get { var (s, c) = MathF.SinCos(Yaw); return new Vector2(c, -s); } }

    /// <summary>Flat distance from a point to the box (0 inside it).</summary>
    public float DistanceTo(float x, float z)
    {
        float dx = x - X, dz = z - Z;
        var (s, c) = MathF.SinCos(Yaw);
        var side = MathF.Abs(dx * c - dz * s);          // along the right hand
        var along = MathF.Abs(-dx * s - dz * c);        // along the heading
        var ex = MathF.Max(side - HalfW, 0); var ez = MathF.Max(along - HalfL, 0);
        return MathF.Sqrt(ex * ex + ez * ez);
    }

    /// <summary>Whether the two boxes touch (separating axis test over the four edge directions).</summary>
    public bool Overlaps(in BodyBox o)
    {
        Span<Vector2> axes = stackalloc Vector2[4];
        axes[0] = Right; axes[1] = Fwd; axes[2] = o.Right; axes[3] = o.Fwd;
        var (ar, af, br, bf) = (axes[0], axes[1], axes[2], axes[3]);
        var d = new Vector2(o.X - X, o.Z - Z);
        foreach (var u in axes)
        {
            var ra = HalfW * MathF.Abs(Vector2.Dot(ar, u)) + HalfL * MathF.Abs(Vector2.Dot(af, u));
            var rb = o.HalfW * MathF.Abs(Vector2.Dot(br, u)) + o.HalfL * MathF.Abs(Vector2.Dot(bf, u));
            if (MathF.Abs(Vector2.Dot(d, u)) > ra + rb) return false;
        }
        return true;
    }

    /// <summary>
    /// Where a body of <paramref name="radius"/> that went from (<paramref name="fromX"/>, <paramref name="fromZ"/>) to (<paramref name="x"/>, <paramref name="z"/>) has to be so as not to have walked into
    /// this box: unchanged when it is clear of it or was already as close (it stands where something stopped for it; nothing pushes it out), else back out to the surface (or, when it was
    /// closer than the radius already, no closer than it was), along the way from the box. Moving along the face keeps its sideways part: it slides. <paramref name="moved"/> says it was changed.
    /// </summary>
    public (float X, float Z) KeepOut(float x, float z, float fromX, float fromZ, float radius, out bool moved)
    {
        moved = false;
        var now = DistanceTo(x, z);
        if (now >= radius) return (x, z);
        var before = DistanceTo(fromX, fromZ);
        if (now >= before - 1e-4f) return (x, z); // not closing in
        var (s, c) = MathF.SinCos(Yaw);
        float dx = x - X, dz = z - Z;
        float side = dx * c - dz * s, along = -dx * s - dz * c;
        float qs = Math.Clamp(side, -HalfW, HalfW), qa = Math.Clamp(along, -HalfL, HalfL); // the nearest point of the box
        float ns = side - qs, na = along - qa;
        var len = MathF.Sqrt(ns * ns + na * na);
        if (len > 1e-5f) { ns /= len; na /= len; }
        else // centre inside the box: back out through the face it came in by
        {
            float ox = fromX - X, oz = fromZ - Z;
            float fside = ox * c - oz * s, falong = -ox * s - oz * c;
            if (MathF.Abs(fside) - HalfW > MathF.Abs(falong) - HalfL) { ns = fside >= 0 ? 1 : -1; na = 0; qs = ns * HalfW; qa = Math.Clamp(along, -HalfL, HalfL); }
            else { na = falong >= 0 ? 1 : -1; ns = 0; qa = na * HalfL; qs = Math.Clamp(side, -HalfW, HalfW); }
        }
        var want = MathF.Min(radius, before); // out to the radius, or no closer than it was when it already was nearer
        float os = qs + ns * want, oa = qa + na * want;
        moved = true;
        return (X + os * c - oa * s, Z - os * s - oa * c);
    }

    public float DistanceBetweenCentres(in BodyBox o) { var dx = o.X - X; var dz = o.Z - Z; return MathF.Sqrt(dx * dx + dz * dz); }
}

/// <summary>A standing body a player on foot cannot walk through: its footprint and the heights it spans (feet to top), metres.</summary>
public readonly record struct BodyObstacle(BodyBox Box, float Y0, float Y1);

/// <summary>
/// The bodies that stop a player on foot and are not physics bodies (an animal that stands, ADR 0028). The server answers with the animals' own boxes, a client with the newest it was sent,
/// so the player's predicted move and the server's meet the same ones (<see cref="PlayerBodyBlock"/>).
/// </summary>
public interface IStandingBodies
{
    /// <summary>Adds to <paramref name="into"/> every standing body that may be within <paramref name="radius"/> of (<paramref name="x"/>, <paramref name="z"/>) (a cheap overestimate; the test is exact).</summary>
    void Near(float x, float z, float radius, List<BodyObstacle> into);
}

/// <summary>
/// A player on foot against the standing animals (ADR 0028: player x wildlife, pet = Collide; the animal is never pushed, and nor is the player by it). Pure: the Godot player calls it after its
/// physics move, on the server and in the client's prediction and replay alike, so both stop at the same place.
/// </summary>
public static class PlayerBodyBlock
{
    /// <summary>
    /// The place a player whose feet went from <paramref name="from"/> to <paramref name="to"/> ends up when standing animals are in the way: each box it walked into puts it back out (<see cref="BodyBox.KeepOut"/>).
    /// A box it passes over (its feet above the top) or under (its head below the underside) is no obstacle. <paramref name="radius"/> and <paramref name="height"/> are the capsule's.
    /// </summary>
    public static Vector3 Resolve(Vector3 from, Vector3 to, float radius, float height, IReadOnlyList<BodyObstacle> bodies)
    {
        var (x, z) = (to.X, to.Z);
        foreach (var b in bodies)
        {
            if (to.Y + height <= b.Y0 || to.Y >= b.Y1) continue;
            (x, z) = b.Box.KeepOut(x, z, from.X, from.Z, radius, out _);
        }
        return new Vector3(x, to.Y, z);
    }
}

/// <summary>
/// What an animal's last solid-world ray found (the cache that keeps the rays rare): a straight run that is clear, from where it was cast, along that heading,
/// for <c>metres</c>, or that the way is blocked. While the animal keeps that heading (within <see cref="TurnTolerance"/>) and stays inside the clear length
/// (or at the blocked spot), nothing has to be asked again. Reset when its behaviour changes (a state change is a new walk) and, for a block, after a moment (a door opens, a car drives off).
/// </summary>
public struct ClearPath
{
    /// <summary>Radians the heading may drift from the one cast before the run is asked again, at most; and how far (metres) the animal may stray sideways from the cast line, the drift of the heading over the way counted.</summary>
    public const float TurnTolerance = 0.4f, LateralSlackM = 0.15f;

    float _x, _z, _yaw, _metres;

    public void Set(float x, float z, float yaw, float metres) { _x = x; _z = z; _yaw = yaw; _metres = metres; }
    /// <summary>The way ahead is blocked here: the same answer holds until <see cref="Reset"/>.</summary>
    public void SetBlocked(float x, float z, float yaw) { _x = x; _z = z; _yaw = yaw; _metres = -1; }
    public void Reset() => _metres = 0;

    /// <summary>Whether the way was found blocked at (x, z) along <paramref name="yaw"/> and the animal has not moved on.</summary>
    public readonly bool KnownBlocked(float x, float z, float yaw) =>
        _metres < 0 && MathF.Abs(TurretMath.AngleDiff(yaw, _yaw)) <= 0.1f && MathF.Abs(x - _x) + MathF.Abs(z - _z) < 0.05f;

    /// <summary>Whether a step of <paramref name="step"/> metres from (x, z) along <paramref name="yaw"/> is still inside the known-clear run.</summary>
    public readonly bool Covers(float x, float z, float yaw, float step)
    {
        var turn = MathF.Abs(TurretMath.AngleDiff(yaw, _yaw));
        if (!(_metres > 0) || turn > TurnTolerance) return false;
        var (s, c) = MathF.SinCos(_yaw);
        float dx = x - _x, dz = z - _z;
        var along = -dx * s - dz * c;     // along the cast heading
        var side = MathF.Abs(dx * c - dz * s);
        return along + step <= _metres && side + (MathF.Abs(along) + step) * MathF.Sin(turn) <= LateralSlackM;
    }
}

/// <summary>
/// Every animal of the match, wild and pet, for animal-against-animal contact (ADR 0028: an animal collides with a pet and with wildlife of another herd, passes
/// its own herd). No physics bodies: <see cref="Blocks"/> tests the box an animal would have after a step against the others' boxes. The count is capped
/// by the map (a few hundred), a cheap flat distance rejects nearly all of them, and only an animal that steps asks.
/// </summary>
public sealed class AnimalCrowd : IStandingBodies
{
    /// <summary>Side of a cell of the grid the animals are filed in, metres: a few bodies wide, so a question reads four to nine cells.</summary>
    const float CellM = 4f;

    readonly List<Animal> _all = new();
    /// <summary>The animals by the cell they stand in (kept as they move, <see cref="Moved"/>): a question looks at the cells its reach covers, not at every animal. The answers are the ones the whole list gave.</summary>
    readonly Dictionary<long, List<Animal>> _cells = new(CellKeys.Instance);
    readonly List<Animal> _found = new();
    /// <summary>The widest <see cref="Animal.BodyReach"/> that ever joined: how far past its own reach a question has to look.</summary>
    float _maxReach;
    int _seq;

    public int Count => _all.Count;

    static int Cell(float v) => (int)MathF.Floor(v / CellM);
    static long Key(int cx, int cz) => ((long)cx << 32) | (uint)cz;

    sealed class CellKeys : IEqualityComparer<long>
    {
        public static readonly CellKeys Instance = new();
        public bool Equals(long a, long b) => a == b;
        public int GetHashCode(long key) => (int)(((ulong)key * 0x9E3779B97F4A7C15UL) >> 32); // the default folds the two halves together, and neighbours collide
    }

    void File(Animal a)
    {
        var p = a.Position;
        a.CrowdCx = Cell(p.X); a.CrowdCz = Cell(p.Z);
        var key = Key(a.CrowdCx, a.CrowdCz);
        if (!_cells.TryGetValue(key, out var cell)) _cells[key] = cell = new List<Animal>(4);
        cell.Add(a);
    }

    void Unfile(Animal a)
    {
        var key = Key(a.CrowdCx, a.CrowdCz);
        if (!_cells.TryGetValue(key, out var cell)) return;
        cell.Remove(a);
        if (cell.Count == 0) _cells.Remove(key);
    }

    /// <summary>An animal of this crowd moved (its <see cref="Animal.Position"/> was set): filed under its new cell when it crossed into one.</summary>
    internal void Moved(Animal a)
    {
        var p = a.Position;
        if (Cell(p.X) == a.CrowdCx && Cell(p.Z) == a.CrowdCz) return;
        Unfile(a);
        File(a);
    }

    /// <summary>The animals near a point, as boxes with the heights they span, for those that stand (<see cref="Animal.Stands"/>): what stops a player on foot. A walking or fleeing animal is not one, see <see cref="AnimalRules.Stands"/>. In the order they joined.</summary>
    public void Near(float x, float z, float radius, List<BodyObstacle> into)
    {
        var r = radius + _maxReach;
        int x0 = Cell(x - r), x1 = Cell(x + r), z0 = Cell(z - r), z1 = Cell(z + r);
        _found.Clear();
        for (var cx = x0; cx <= x1; cx++)
            for (var cz = z0; cz <= z1; cz++)
            {
                if (!_cells.TryGetValue(Key(cx, cz), out var cell)) continue;
                foreach (var a in cell)
                {
                    var reach = a.BodyReach + radius;
                    var p = a.Position;
                    float dx = p.X - x, dz = p.Z - z;
                    if (dx * dx + dz * dz > reach * reach) continue;
                    if (!a.Stands) continue;
                    _found.Add(a);
                }
            }
        if (_found.Count > 1) _found.Sort(static (a, b) => a.CrowdSeq.CompareTo(b.CrowdSeq)); // the order matters to who pushes the player out last
        foreach (var a in _found) into.Add(new BodyObstacle(a.Body, a.Position.Y, a.Position.Y + a.HitBox[1]));
        _found.Clear();
    }

    public void Add(Animal a)
    {
        if (ReferenceEquals(a.Crowd, this)) return;
        a.Crowd?.Remove(a);
        _all.Add(a);
        a.CrowdSeq = _seq++;
        _maxReach = MathF.Max(_maxReach, a.BodyReach);
        File(a);
        a.Crowd = this;
    }

    public void Remove(Animal a)
    {
        if (!ReferenceEquals(a.Crowd, this)) return;
        _all.Remove(a);
        Unfile(a);
        a.Crowd = null;
    }

    /// <summary>
    /// Whether <paramref name="self"/>, stepping from <paramref name="from"/> to <paramref name="to"/>, would run into another animal: its new box touches
    /// the other's, and it was not already touching (or it is closing in). Another of its herd, one that lies, flies or is held, never blocks. Nobody is pushed.
    /// </summary>
    public bool Blocks(Animal self, in BodyBox from, in BodyBox to)
    {
        var selfReach = self.BodyReach;
        var herd = self.Herd;
        var r = selfReach + _maxReach + 0.5f;
        int x0 = Cell(to.X - r), x1 = Cell(to.X + r), z0 = Cell(to.Z - r), z1 = Cell(to.Z + r);
        for (var cx = x0; cx <= x1; cx++)
            for (var cz = z0; cz <= z1; cz++)
            {
                if (!_cells.TryGetValue(Key(cx, cz), out var cell)) continue;
                foreach (var o in cell)
                {
                    var p = o.Position;
                    var dx = p.X - to.X; var dz = p.Z - to.Z;
                    var reach = selfReach + o.BodyReach + 0.5f;
                    if (dx * dx + dz * dz > reach * reach) continue;
                    if (ReferenceEquals(o, self) || !o.Obstructs || (herd != null && ReferenceEquals(o.Herd, herd))) continue;
                    var theirs = o.Body;
                    if (!to.Overlaps(theirs)) continue;
                    if (!from.Overlaps(theirs) || to.DistanceBetweenCentres(theirs) < from.DistanceBetweenCentres(theirs)) return true;
                }
            }
        return false;
    }
}

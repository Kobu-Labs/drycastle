using System.Numerics;
using Drycastle.ShooterCore.Fsm;

namespace Drycastle.ShooterCore.Buildings.Destruction;

/// <summary>Where one piece of house debris is in its life (docs/house-destruction.md "Debris machine", WP-G). Client-side and cosmetic only.</summary>
public enum DebrisPhase : byte { Falling, Broken, Resting, Fading }

/// <summary>The spec of <see cref="DebrisPhase"/>. Its owner is the shooter's <c>DebrisNode</c> (godot/shooter/Buildings/Destruction).</summary>
public static class DebrisMachine
{
    public static readonly StateMachineSpec<DebrisPhase> Spec = StateMachineSpec<DebrisPhase>
        .Define("House.Debris", "A part that fell off a hollow house: one rigid body falling whole, split into pieces on its first hard impact, resting once it sleeps, then sinking away (fade mode) or frozen into the house's rubble mesh (stay mode, leaves the machine).", DebrisPhase.Falling)
        .Allow(DebrisPhase.Falling, DebrisPhase.Broken, DebrisPhase.Resting)
        .Allow(DebrisPhase.Broken, DebrisPhase.Resting)
        .Allow(DebrisPhase.Resting, DebrisPhase.Fading)
        .Note(DebrisPhase.Falling, "A whole fallen component, one body. An impact whose speed change is over debris.break_impulse (after a short grace) splits it with Fracture.Split; the body is replaced by its pieces.")
        .Note(DebrisPhase.Broken, "A piece of a split (entered by every new piece). With debris.second_break a large piece of the first split may split once more on its next hard impact.")
        .Note(DebrisPhase.Resting, "The body sleeps, or has fallen for too long. Fade mode: frozen, waits debris.fade_after_s (clock). Stay mode: frozen into the house's rubble mesh and freed (leaves the machine).")
        .Note(DebrisPhase.Fading, "Collision off, sinks and turns transparent over debris.fade_s (clock), then freed. Also how evicted stay rubble (debris.stay_cap_cells) leaves.")
        .Build();
}

/// <summary>
/// The pure rules of house debris (WP-G): its frame, when it breaks, which pieces get a body, how pieces move off. The view (godot/shooter/Buildings/Destruction/DebrisNode)
/// does the Godot side. A debris body's local frame is grid-aligned and in metres: local = (gx, gz, -gy) * cell, so its world basis is a turn of the house's yaw about +Y
/// and its origin is the cells' centroid (Godot axes: x east, y up, z south).
/// </summary>
public static class DebrisRules
{
    /// <summary>Mass of one 0.25 m cell (kg): masonry. Only the ratio matters (debris collides with the static world only).</summary>
    public const float CellMassKg = 25f;
    /// <summary>A body's collider is its box cover when that has at most this many boxes, else one box over its bounds.</summary>
    public const int MaxBoxes = 16;
    /// <summary>No break in the first seconds of a body: the overlap with the house it fell from must not count as an impact.</summary>
    public const float GraceS = 0.12f;
    /// <summary>A body still moving after this long rests anyway (stuck, rolling forever, fell off the map).</summary>
    public const float MaxMovingS = 12f;
    /// <summary>Slower than this (m/s, rad/s) for <see cref="StillS"/> counts as asleep, whatever the engine says.</summary>
    public const float StillSpeed = 0.2f, StillSpin = 0.4f, StillS = 0.6f;
    /// <summary>Outward speed a new piece gets from its parent's centroid (m/s), plus this much up.</summary>
    public const float KickMps = 1.5f, KickUpMps = 0.5f;
    /// <summary>Push a fresh component gets away from the blast (m/s at the blast, falling off with distance in metres).</summary>
    public const float BlastPushMps = 3f, BlastPushFalloffM = 4f;

    /// <summary>Map local metres (x east, y north, z up) to Godot axes (x east, y up, z south).</summary>
    public static Vector3 GodotOf(Vector3 map) => new(map.X, map.Z, -map.Y);

    /// <summary>Grid units to a debris body's local axes in metres (before taking off the centroid).</summary>
    public static Vector3 LocalOf(Vector3 grid, float cell) => new Vector3(grid.X, grid.Z, -grid.Y) * cell;

    /// <summary><see cref="LocalOf"/>'s inverse.</summary>
    public static Vector3 GridOf(Vector3 local, float cell) => new Vector3(local.X, -local.Z, local.Y) / cell;

    /// <summary>The centroid of <paramref name="cells"/> in grid units (cell centres).</summary>
    public static Vector3 Centroid(HouseGrid g, ReadOnlySpan<int> cells)
    {
        if (cells.Length == 0) return Vector3.Zero;
        double x = 0, y = 0, z = 0;
        foreach (var i in cells)
        {
            var (cx, cy, cz) = g.Coords(i);
            x += cx; y += cy; z += cz;
        }
        var n = cells.Length;
        return new Vector3((float)(x / n) + 0.5f, (float)(y / n) + 0.5f, (float)(z / n) + 0.5f);
    }

    /// <summary>The cell bounds of <paramref name="cells"/> (min inclusive, max exclusive), in cells.</summary>
    public static (int X0, int Y0, int Z0, int X1, int Y1, int Z1) Bounds(HouseGrid g, ReadOnlySpan<int> cells)
    {
        int x0 = int.MaxValue, y0 = int.MaxValue, z0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue, z1 = int.MinValue;
        foreach (var i in cells)
        {
            var (x, y, z) = g.Coords(i);
            if (x < x0) x0 = x; if (y < y0) y0 = y; if (z < z0) z0 = z;
            if (x > x1) x1 = x; if (y > y1) y1 = y; if (z > z1) z1 = z;
        }
        return cells.Length == 0 ? (0, 0, 0, 0, 0, 0) : (x0, y0, z0, x1 + 1, y1 + 1, z1 + 1);
    }

    public static float Mass(int cells) => MathF.Max(1, cells) * CellMassKg;

    /// <summary>
    /// Whether a body splits now: past the grace, an impact whose speed change (m/s, i.e. impulse per kg: debris.break_impulse) is over the threshold, and the body may still
    /// split: a whole component always, a piece of the first split only with second_break when it is large (at least twice fracture.cells_per_piece), nothing after that.
    /// </summary>
    public static bool Breaks(int generation, int cells, float elapsedS, float speedChange, DebrisConfig debris, FractureConfig fracture) =>
        elapsedS >= GraceS && speedChange > debris.BreakImpulse && CanSplit(generation, cells, debris, fracture);

    public static bool CanSplit(int generation, int cells, DebrisConfig debris, FractureConfig fracture) => generation switch
    {
        0 => cells >= 2,
        1 => debris.SecondBreak && cells >= 2 * fracture.CellsPerPiece,
        _ => false,
    };

    /// <summary>Whether a moving body counts as at rest: the engine put it to sleep, it has been still for <see cref="StillS"/>, or it has moved for <see cref="MaxMovingS"/>.</summary>
    public static bool Rests(bool sleeping, float stillForS, float elapsedS) => sleeping || stillForS >= StillS || elapsedS >= MaxMovingS;

    public static bool Still(Vector3 linear, Vector3 angular) => linear.LengthSquared() < StillSpeed * StillSpeed && angular.LengthSquared() < StillSpin * StillSpin;

    /// <summary>
    /// Whether group <paramref name="index"/> of <paramref name="count"/> from <see cref="Fracture.Split"/> is dust (no body): under fracture.dust_below_cells, or the last group
    /// and not one 6-connected piece (the dust group gathers small pieces from all over; a real piece is always connected).
    /// </summary>
    public static bool IsDust(HouseGrid g, int[] group, int index, int count, FractureConfig fracture) =>
        group.Length < Math.Max(1, fracture.DustBelowCells) || (index == count - 1 && count > 1 && !Connected(g, group));

    /// <summary>Whether <paramref name="cells"/> are one 6-connected component.</summary>
    public static bool Connected(HouseGrid g, int[] cells)
    {
        if (cells.Length <= 1) return true;
        var set = new HashSet<int>(cells);
        var seen = new HashSet<int> { cells[0] };
        var stack = new Stack<int>();
        stack.Push(cells[0]);
        int sx = 1, sy = g.Nx, sz = g.Nx * g.Ny;
        while (stack.Count > 0)
        {
            var i = stack.Pop();
            var (x, y, z) = g.Coords(i);
            void Try(bool ok, int j) { if (ok && set.Contains(j) && seen.Add(j)) stack.Push(j); }
            Try(x > 0, i - sx); Try(x < g.Nx - 1, i + sx);
            Try(y > 0, i - sy); Try(y < g.Ny - 1, i + sy);
            Try(z > 0, i - sz); Try(z < g.Nz - 1, i + sz);
        }
        return seen.Count == cells.Length;
    }

    /// <summary>
    /// Which of the pieces get a body when only <paramref name="free"/> bodies are left: the largest first (ties by order). Fills <paramref name="bodies"/> with their
    /// indices into <paramref name="sizes"/> and <paramref name="dust"/> with the rest.
    /// </summary>
    public static void Budget(ReadOnlySpan<int> sizes, int free, List<int> bodies, List<int> dust)
    {
        bodies.Clear(); dust.Clear();
        for (var i = 0; i < sizes.Length; i++) bodies.Add(i);
        var s = sizes.ToArray();
        bodies.Sort((a, b) => s[b] != s[a] ? s[b].CompareTo(s[a]) : a.CompareTo(b));
        var keep = Math.Clamp(free, 0, bodies.Count);
        for (var i = keep; i < bodies.Count; i++) dust.Add(bodies[i]);
        bodies.RemoveRange(keep, bodies.Count - keep);
    }

    /// <summary>
    /// The start velocity of a piece at <paramref name="offset"/> (metres, world) from its parent's centroid: the parent's velocity at that point plus an outward kick.
    /// <paramref name="outward"/> is the kick's direction when the offset is about zero.
    /// </summary>
    public static Vector3 PieceVelocity(Vector3 parentLinear, Vector3 parentAngular, Vector3 offset, Vector3 outward)
    {
        var dir = offset.LengthSquared() > 1e-6f ? Vector3.Normalize(offset) : outward;
        return parentLinear + Vector3.Cross(parentAngular, offset) + dir * KickMps + new Vector3(0, KickUpMps, 0);
    }

    /// <summary>The push a fresh fallen component at <paramref name="centroid"/> gets from a blast at <paramref name="blast"/> (both Godot world metres).</summary>
    public static Vector3 BlastPush(Vector3 centroid, Vector3 blast)
    {
        var d = centroid - blast;
        var len = d.Length();
        if (len < 1e-4f) return Vector3.Zero;
        return d / len * (BlastPushMps * BlastPushFalloffM / (BlastPushFalloffM + len));
    }

    /// <summary>The cell of <paramref name="cells"/> lying furthest along <paramref name="towardGrid"/> (grid frame): where a body moving that way hit. Its centre, grid units.</summary>
    public static Vector3 Leading(HouseGrid g, ReadOnlySpan<int> cells, Vector3 towardGrid)
    {
        var best = float.NegativeInfinity;
        var at = Vector3.Zero;
        foreach (var i in cells)
        {
            var (x, y, z) = g.Coords(i);
            var c = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
            var d = Vector3.Dot(c, towardGrid);
            if (d > best) { best = d; at = c; }
        }
        return at;
    }

    /// <summary>A seed for a split of the body whose lowest cell is <paramref name="lowest"/>, from the fall's seed and the split's generation (cosmetic only, stable per piece).</summary>
    public static uint SplitSeed(uint fallSeed, int lowest, int generation)
    {
        var h = 2166136261u;
        foreach (var v in new[] { fallSeed, (uint)lowest, (uint)generation })
            for (var k = 0; k < 4; k++) h = (h ^ (byte)(v >> (8 * k))) * 16777619u;
        return h;
    }
}

/// <summary>
/// Stay-mode rubble of the whole map against debris.stay_cap_cells: pieces in the order they froze. Adding one over the cap hands back the oldest ones to fade
/// (the newest stays even when alone over the cap).
/// </summary>
public sealed class RubbleLedger
{
    readonly LinkedList<(long Id, int Cells)> _order = new();
    readonly Dictionary<long, LinkedListNode<(long Id, int Cells)>> _byId = new();

    public int Cells { get; private set; }
    public int Count => _order.Count;

    /// <summary>Adds piece <paramref name="id"/>; fills <paramref name="evicted"/> with the oldest pieces that no longer fit under <paramref name="cap"/> (0 = no cap).</summary>
    public void Add(long id, int cells, int cap, List<long> evicted)
    {
        evicted.Clear();
        if (_byId.ContainsKey(id)) return;
        _byId[id] = _order.AddLast((id, cells));
        Cells += cells;
        if (cap <= 0) return;
        while (Cells > cap && _order.First is { } first && first.Value.Id != id)
        {
            evicted.Add(first.Value.Id);
            Remove(first.Value.Id);
        }
    }

    public bool Remove(long id)
    {
        if (!_byId.Remove(id, out var node)) return false;
        Cells -= node.Value.Cells;
        _order.Remove(node);
        return true;
    }

    public void Clear() { _order.Clear(); _byId.Clear(); Cells = 0; }
}

using System.Numerics;

namespace Drycastle.ShooterCore.Buildings.Destruction;

/// <summary>
/// One drive step's question to the hollow houses (Godot frame, y up): the vehicle stands with its feet at <paramref name="Feet"/> facing
/// <paramref name="Yaw"/> and is about to move by <paramref name="Motion"/> (this step's) at <paramref name="Speed"/> m/s. <paramref name="Lift"/>: the
/// cells lower than this over the feet stay (a stub a wheel rolls over). <paramref name="Ramming"/>: it cut a house a moment ago, so it goes on
/// cutting down to <see cref="RamConfig.KeepSpeed"/>. <paramref name="Seq"/>: the driver's input it steps (the client's guess is keyed by it);
/// <paramref name="Car"/>: the vehicle (the probe fills it in).
/// </summary>
public readonly record struct PloughQuery(Vector3 Feet, float Yaw, Vector3 Motion, float Speed, float Mass, float HalfWidth, float HalfLength, float Height, float Lift,
    bool Ramming = false, ushort Seq = 0, ushort Car = 0);

/// <summary>
/// The houses a driven vehicle cuts its way through (<see cref="HousePlough"/>): the server's are the true ones, the driver's client guesses with its copy.
/// The drive model asks before each move; the answer is the speed the cells took.
/// </summary>
public interface IHousePlough
{
    /// <summary>Cuts what the vehicle sweeps next (what it can afford) and returns the speed that took, m/s; 0: nothing cut.</summary>
    float Plough(in PloughQuery q);
    /// <summary>The server has stepped the driver's inputs up to <paramref name="ack"/> (the client forgets old guesses).</summary>
    void PloughAcked(ushort ack) { }
    /// <summary>The prediction replays the inputs after <paramref name="ack"/>: their guesses are taken back unless the replay cuts them again.</summary>
    void PloughReplay(ushort ack) { }
    /// <summary>The replay is over.</summary>
    void PloughReplayDone() { }
}

/// <summary>A cell a plough cuts: house <paramref name="House"/>'s cell <paramref name="Cell"/>, reached <paramref name="At"/> metres along the sweep, of resist <paramref name="Resist"/> (1/16 units).</summary>
public readonly record struct PloughCell(int House, int Cell, float At, int Resist);

/// <summary>What one plough step cuts: <paramref name="Count"/> cells (all it met when <paramref name="Through"/>), the speed that took (<paramref name="Loss"/>, m/s; all of it when not through).</summary>
public readonly record struct PloughPlan(float Loss, int Count, bool Through);

/// <summary>
/// A vehicle cutting its way through a hollow house (docs/house-destruction.md "Ramming"). Before each move the vehicle's upright box (its hull plus
/// <see cref="RamConfig.MarginM"/> on the sides and on top, from <see cref="PloughQuery.Lift"/> over its feet) is swept along the motion of
/// <see cref="LookaheadSteps"/> steps plus <see cref="RamConfig.LeadM"/>; every live cell whose centre it meets (any material with a resist, foundation never)
/// is cut, nearest first, while the momentum lasts: each costs <see cref="RamConfig.MomentumPerCell"/> times its resist, and the vehicle loses cost / mass.
/// It never stops at the wall: the cells are gone a step before it gets there (the colliders follow at the end of the tick). Short of momentum it cuts
/// what it can afford and stops in the wall. It starts at <see cref="RamConfig.MinSpeed"/> and, once cutting, goes on down to <see cref="RamConfig.KeepSpeed"/>.
/// The server sends the cells it cut as one op (HouseCut, 254) per house; the cells are the shape of the vehicle at the angle it came in.
/// </summary>
public static class HousePlough
{
    /// <summary>The sweep covers this many steps of motion: the cells of the next step are gone before the step (the colliders are rebuilt between ticks).</summary>
    public const float LookaheadSteps = 2f;
    /// <summary>A vehicle that cut a house goes on cutting at <see cref="RamConfig.KeepSpeed"/> for this long after its last cut, seconds.</summary>
    public const float RammingHoldS = 0.6f;

    /// <summary>The vehicle's box (map local): its hull, <paramref name="margin"/> wider on each side and higher, from the lift over its feet up.</summary>
    public static RamBox Box(in PloughQuery q, float margin)
    {
        var (s, c) = MathF.SinCos(q.Yaw);
        var feet = RamBox.ToMap(q.Feet);
        float lo = q.Lift, hi = MathF.Max(q.Lift + 0.1f, q.Height + margin);
        return new RamBox(feet + Vector3.UnitZ * ((lo + hi) / 2), new Vector3(c, s, 0), Vector3.UnitZ, new Vector3(-s, c, 0), new Vector3(q.HalfWidth + margin, (hi - lo) / 2, q.HalfLength));
    }

    /// <summary>How far the box is swept, metres.</summary>
    public static float Reach(in PloughQuery q, RamConfig rc) => q.Motion.Length() * LookaheadSteps + rc.LeadM;

    /// <summary>The radius around the box's centre (map local) the sweep can reach: a house whose bounding sphere is farther is skipped.</summary>
    public static float ReachRadius(in PloughQuery q, RamConfig rc) => Reach(q, rc) + new Vector3(q.HalfWidth + rc.MarginM, q.HalfLength, q.Height + rc.MarginM).Length();

    /// <summary>
    /// What the step cuts from the houses <paramref name="houses"/> (indices; <paramref name="cellsOf"/> gives each one's cells, <paramref name="present"/>
    /// says whether a cell is still there to cut): <paramref name="cut"/> gets the cells, sorted by house then cell. Nothing (an empty plan) below the
    /// speeds, or when it meets nothing.
    /// </summary>
    public static PloughPlan Plan(in PloughQuery q, DestructionConfig cfg, IReadOnlyList<int> houses, Func<int, HouseCells> cellsOf, Func<int, int, bool> present, List<PloughCell> cut)
    {
        cut.Clear();
        var rc = cfg.Ram;
        var len = q.Motion.Length();
        if (len < 1e-5f || !(q.Speed >= rc.MinSpeed || q.Ramming && q.Speed >= rc.KeepSpeed)) return default;
        var box = Box(q, rc.MarginM);
        var dir = RamBox.ToMap(q.Motion) / len;
        var reach = Reach(q, rc);
        foreach (var h in houses) Collect(cellsOf(h), h, box, dir, reach, cfg, present, cut);
        if (cut.Count == 0) return default;
        cut.Sort(static (a, b) => a.At != b.At ? a.At.CompareTo(b.At) : a.House != b.House ? a.House.CompareTo(b.House) : a.Cell.CompareTo(b.Cell));
        var budget = q.Mass * q.Speed;
        var cost = 0f;
        var n = 0;
        for (; n < cut.Count; n++)
        {
            var c = cut[n].Resist / 16f * rc.MomentumPerCell;
            if (cost + c > budget) break;
            cost += c;
        }
        var through = n == cut.Count;
        cut.RemoveRange(n, cut.Count - n);
        cut.Sort(static (a, b) => a.House != b.House ? a.House.CompareTo(b.House) : a.Cell.CompareTo(b.Cell));
        return new PloughPlan(through ? cost / MathF.Max(q.Mass, 1) : q.Speed, n, through);
    }

    /// <summary>The cells of <paramref name="c"/> (house <paramref name="house"/>) still present that the box meets swept by up to <paramref name="reach"/> along <paramref name="dir"/>.</summary>
    static void Collect(HouseCells c, int house, in RamBox box, Vector3 dir, float reach, DestructionConfig cfg, Func<int, int, bool> present, List<PloughCell> into)
    {
        var g = c.Grid;
        if (!Bounds(g, box, dir, reach, out var lo, out var hi)) return;
        Span<Vector3> axes = [box.Right, box.Up, box.Fwd];
        Span<float> half = [box.Half.X, box.Half.Y, box.Half.Z];
        var cells = g.Cells.Span;
        var pal = g.Palette.Span;
        for (var z = lo.Z; z <= hi.Z; z++)
        for (var y = lo.Y; y <= hi.Y; y++)
        for (var x = lo.X; x <= hi.X; x++)
        {
            var i = g.IndexOf(x, y, z);
            var p = cells[i];
            if (p == 0) continue;
            var r = cfg.Resist(pal[p].Material);
            if (r == 0 || !present(house, i)) continue;
            var t = Swept(g.ToMap(new Vector3(x + 0.5f, y + 0.5f, z + 0.5f)) - box.Centre, axes, half, dir, reach);
            if (t >= 0) into.Add(new PloughCell(house, i, t, r));
        }
    }

    /// <summary>The first shift t * dir, t in [0, probe], at which the point <paramref name="rel"/> (from the box's centre) is inside the box; -1 when none.</summary>
    static float Swept(Vector3 rel, ReadOnlySpan<Vector3> axes, ReadOnlySpan<float> half, Vector3 dir, float probe)
    {
        float t0 = 0, t1 = probe;
        for (var k = 0; k < 3; k++)
        {
            float q = Vector3.Dot(rel, axes[k]), s = Vector3.Dot(dir, axes[k]), h = half[k];
            if (MathF.Abs(s) < 1e-6f) { if (MathF.Abs(q) > h) return -1; continue; }
            float a = (q - h) / s, b = (q + h) / s;
            if (a > b) (a, b) = (b, a);
            t0 = MathF.Max(t0, a); t1 = MathF.Min(t1, b);
            if (t0 > t1) return -1;
        }
        return t0;
    }

    /// <summary>The grid cells the box can reach over the sweep, clamped to the grid; false when that misses it.</summary>
    static bool Bounds(HouseGrid g, in RamBox box, Vector3 dir, float probe, out (int X, int Y, int Z) lo, out (int X, int Y, int Z) hi)
    {
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        for (var k = 0; k < 16; k++)
        {
            var p = box.Centre + box.Right * (box.Half.X * ((k & 1) == 0 ? -1 : 1)) + box.Up * (box.Half.Y * ((k & 2) == 0 ? -1 : 1))
                    + box.Fwd * (box.Half.Z * ((k & 4) == 0 ? -1 : 1)) + dir * ((k & 8) == 0 ? 0 : probe);
            var q = g.ToGrid(p);
            mn = Vector3.Min(mn, q); mx = Vector3.Max(mx, q);
        }
        lo = (Math.Max(0, (int)MathF.Floor(mn.X)), Math.Max(0, (int)MathF.Floor(mn.Y)), Math.Max(0, (int)MathF.Floor(mn.Z)));
        hi = (Math.Min(g.Nx - 1, (int)MathF.Floor(mx.X)), Math.Min(g.Ny - 1, (int)MathF.Floor(mx.Y)), Math.Min(g.Nz - 1, (int)MathF.Floor(mx.Z)));
        return lo.X <= hi.X && lo.Y <= hi.Y && lo.Z <= hi.Z;
    }
}

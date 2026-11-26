using System.Numerics;

namespace Drycastle.ShooterCore.Buildings.Destruction;

/// <summary>A vehicle's box for a ram, map local (x east, y north, z up): its centre, unit axes and half sizes along them (x right, y up, z forward).</summary>
public readonly record struct RamBox(Vector3 Centre, Vector3 Right, Vector3 Up, Vector3 Fwd, Vector3 Half)
{
    /// <summary>An upright box over a footprint, from Godot's frame (y up, -z north; yaw 0 faces -z, positive turns left): the feet on the ground under the centre.</summary>
    public static RamBox FromGodot(Vector3 feet, float yaw, float halfWidth, float halfLength, float height)
    {
        var (s, c) = MathF.SinCos(yaw);
        return new RamBox(new Vector3(feet.X, -feet.Z, feet.Y + height / 2), new Vector3(c, s, 0), Vector3.UnitZ, new Vector3(-s, c, 0), new Vector3(halfWidth, height / 2, halfLength));
    }

    /// <summary>Godot's frame (y up, -z north) to map local.</summary>
    public static Vector3 ToMap(Vector3 godot) => new(godot.X, -godot.Z, godot.Y);
    /// <summary>Map local to Godot's frame.</summary>
    public static Vector3 ToGodot(Vector3 map) => new(map.X, map.Z, -map.Y);

    public float Extent(Vector3 dir) => Half.X * MathF.Abs(Vector3.Dot(dir, Right)) + Half.Y * MathF.Abs(Vector3.Dot(dir, Up)) + Half.Z * MathF.Abs(Vector3.Dot(dir, Fwd));
}

/// <summary>Where a vehicle's box meets a house: the mean of the cells it meets and how far ahead of the box's centre the nearest one's face is, along the ram (metres, map local).</summary>
public readonly record struct RamContact(Vector3 At, float Front);

/// <summary>
/// A vehicle driven into a hollow house (docs/house-destruction.md "Ramming"). The momentum it loses to the wall (mass times the speed the wall took)
/// is set against what the wall would take to break: <see cref="RamConfig.MomentumPerCell"/> for every cell of a hole the vehicle's cross-section
/// would need, times the cell's resist. More than that: the hole is cut and the vehicle goes through, slowed by what the hole took. Less: a dent at the
/// contact, its radius by the share of the momentum, and the vehicle stops as at any wall. The server decides and sends the ops as carve ops (242); the
/// clients replay them as any blast's.
/// </summary>
public static class HouseRam
{
    /// <summary>
    /// The live cells (not foundation, not floor: the ground) ahead of the box's centre that <paramref name="box"/> meets moved along <paramref name="dir"/>
    /// (unit) by up to <paramref name="probeM"/>: their mean, and the nearest face along <paramref name="dir"/>. Null when it meets none.
    /// </summary>
    public static RamContact? Contact(HouseCells c, in RamBox box, Vector3 dir, float probeM)
    {
        var g = c.Grid;
        if (!Bounds(g, box, dir, probeM, out var lo, out var hi)) return null;
        var sum = Vector3.Zero; var n = 0; var front = float.PositiveInfinity;
        var axes = new[] { box.Right, box.Up, box.Fwd };
        var half = new[] { box.Half.X, box.Half.Y, box.Half.Z };
        for (var z = lo.Z; z <= hi.Z; z++)
        for (var y = lo.Y; y <= hi.Y; y++)
        for (var x = lo.X; x <= hi.X; x++)
        {
            var i = g.IndexOf(x, y, z);
            if (!c.Solid(i)) continue;
            var m = g.Material(i);
            if (m is CellMaterial.Foundation or CellMaterial.Floor) continue;
            var p = g.ToMap(new Vector3(x + 0.5f, y + 0.5f, z + 0.5f));
            var along = Vector3.Dot(p - box.Centre, dir);
            if (along < 0 || !Swept(p - box.Centre, axes, half, dir, probeM)) continue; // behind the centre: not what it drove into
            sum += p; n++;
            front = MathF.Min(front, along - g.Cell / 2);
        }
        return n == 0 ? null : new RamContact(sum / n, front);
    }

    /// <summary>
    /// The hole the vehicle needs to go through: spheres tiling its cross-section square to <paramref name="dir"/> (grown by the margin), centred
    /// <see cref="RamConfig.DepthM"/> / 2 past the wall's face. Ops that miss the grid are left out.
    /// </summary>
    public static CarveOp[] Hole(HouseGrid g, in RamBox box, Vector3 dir, float front, RamConfig cfg)
    {
        var u = MathF.Abs(dir.Z) < 0.9f ? Vector3.Normalize(Vector3.Cross(dir, Vector3.UnitZ)) : box.Right;
        var v = Vector3.Normalize(Vector3.Cross(u, dir));
        float eu = box.Extent(u) + cfg.MarginM, ev = box.Extent(v) + cfg.MarginM;
        // circles of radius rho at the centres of an a x b tiling cover it when rho >= half the diagonal
        var target = MathF.Min(MathF.Min(eu, ev), cfg.MaxSphereM) * MathF.Sqrt(2);
        int nu = Math.Max(1, (int)MathF.Ceiling(2 * eu / target)), nv = Math.Max(1, (int)MathF.Ceiling(2 * ev / target));
        while (nu * nv > cfg.MaxOps) { if (nu >= nv) nu--; else nv--; }
        float a = 2 * eu / nu, b = 2 * ev / nv;
        var half = cfg.DepthM / 2;
        var r = MathF.Sqrt((a * a + b * b) / 4 + half * half);
        var centre = box.Centre + dir * (front + half);
        var ops = new List<CarveOp>(nu * nv);
        for (var iv = 0; iv < nv; iv++)
        for (var iu = 0; iu < nu; iu++)
        {
            var at = centre + u * (-eu + a * (iu + 0.5f)) + v * (-ev + b * (iv + 0.5f));
            if (CarveOp.From(g, at, r) is { } op) ops.Add(op);
        }
        return ops.ToArray();
    }

    /// <summary>A dent at the contact: <see cref="RamConfig.DentMaxM"/> times the share of the momentum the hole needs. Null when it rounds to nothing.</summary>
    public static CarveOp? Dent(HouseGrid g, Vector3 at, float momentum, float cost, RamConfig cfg) =>
        cost > 0 ? CarveOp.From(g, at, cfg.DentMaxM * MathF.Min(1, momentum / cost)) : null;

    /// <summary>The momentum (kg m/s) cutting <paramref name="ops"/> takes: <see cref="RamConfig.MomentumPerCell"/> times the resist of every live cell they would remove, each once.</summary>
    public static float Cost(HouseCells c, ReadOnlySpan<CarveOp> ops, DestructionConfig cfg)
    {
        if (ops.Length == 0) return 0;
        var g = c.Grid;
        var pal = g.Palette.Span;
        Span<int> resist = stackalloc int[pal.Length];
        var minResist = int.MaxValue;
        for (var k = 1; k < pal.Length; k++)
        {
            var m = pal[k].Material;
            resist[k] = m == CellMaterial.Foundation ? 0 : cfg.Resist(m);
            if (resist[k] > 0) minResist = Math.Min(minResist, resist[k]);
        }
        if (minResist == int.MaxValue) return 0;
        // the box of every op's reach (quarter-cells: R * 16 / min resist)
        long qx0 = long.MaxValue, qy0 = long.MaxValue, qz0 = long.MaxValue, qx1 = long.MinValue, qy1 = long.MinValue, qz1 = long.MinValue;
        foreach (var op in ops)
        {
            var reach = ((long)op.RadiusQ * 16 + minResist - 1) / minResist;
            qx0 = Math.Min(qx0, op.Cx - reach); qx1 = Math.Max(qx1, op.Cx + reach);
            qy0 = Math.Min(qy0, op.Cy - reach); qy1 = Math.Max(qy1, op.Cy + reach);
            qz0 = Math.Min(qz0, op.Cz - reach); qz1 = Math.Max(qz1, op.Cz + reach);
        }
        int x0 = Lo(qx0), x1 = Hi(qx1, g.Nx), y0 = Lo(qy0), y1 = Hi(qy1, g.Ny), z0 = Lo(qz0), z1 = Hi(qz1, g.Nz);
        var cells = g.Cells.Span;
        long sum = 0;
        for (var z = z0; z <= z1; z++)
        for (var y = y0; y <= y1; y++)
        for (var x = x0; x <= x1; x++)
        {
            var i = g.IndexOf(x, y, z);
            var r = resist[cells[i]];
            if (r == 0 || !c.Solid(i)) continue;
            long px = 4L * x + 2, py = 4L * y + 2, pz = 4L * z + 2;
            foreach (var op in ops)
            {
                long dx = px - op.Cx, dy = py - op.Cy, dz = pz - op.Cz;
                if ((dx * dx + dy * dy + dz * dz) * r * r > (long)op.RadiusQ * op.RadiusQ * 256) continue;
                sum += r;
                break;
            }
        }
        return sum / 16f * cfg.Ram.MomentumPerCell;
    }

    /// <summary>The speed left after going through: <paramref name="speed"/> less what the hole took (<paramref name="cost"/> / <paramref name="mass"/>), never below 0.</summary>
    public static float SpeedThrough(float speed, float cost, float mass) => MathF.Max(0, speed - cost / MathF.Max(mass, 1));

    // cells whose centre 4x + 2 lies within [q0, q1] (quarter-cells), clamped to the grid, as HouseCells.Carve
    static int Lo(long q0) => (int)Math.Max(0, FloorDiv(q0 + 1, 4));
    static int Hi(long q1, int n) => (int)Math.Min(n - 1, FloorDiv(q1 - 2, 4));
    static long FloorDiv(long a, long b) => a >= 0 ? a / b : -((-a + b - 1) / b);

    /// <summary>Whether the point <paramref name="rel"/> (from the box's centre) is inside the box at some shift t * dir, t in [0, probe].</summary>
    static bool Swept(Vector3 rel, Vector3[] axes, float[] half, Vector3 dir, float probe)
    {
        float t0 = 0, t1 = probe;
        for (var k = 0; k < 3; k++)
        {
            float q = Vector3.Dot(rel, axes[k]), s = Vector3.Dot(dir, axes[k]), h = half[k];
            if (MathF.Abs(s) < 1e-6f) { if (MathF.Abs(q) > h) return false; continue; }
            float a = (q - h) / s, b = (q + h) / s;
            if (a > b) (a, b) = (b, a);
            t0 = MathF.Max(t0, a); t1 = MathF.Min(t1, b);
            if (t0 > t1) return false;
        }
        return true;
    }

    /// <summary>The grid cells the box can reach over the probe, clamped to the grid; false when that misses it.</summary>
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

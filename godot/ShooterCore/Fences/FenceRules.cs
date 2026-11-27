using System.Numerics;

namespace Drycastle.ShooterCore.Fences;

/// <summary>One piece's share of a blast.</summary>
public readonly record struct PieceHit(ushort Section, byte Piece, int Damage);

/// <summary>A vehicle's upright contact box, as ServerCar.ContactShape: feet (Godot, map-root local), yaw, half sizes and height.</summary>
public readonly record struct FenceRamBox(Vector3 Feet, float Yaw, float HalfWidth, float HalfLength, float Height);

/// <summary>What a round, a blast and a ram do to the thin fences (docs/fence-destruction.md F3, F6). Pure; the server applies the result to <see cref="FenceState"/>.</summary>
public static class FenceRules
{
    /// <summary>How far past the near face of a solid thin fence a stopped round's path is followed, beyond half its thickness, metres.</summary>
    public const float ShotSlackM = 0.08f;

    // how far past a section's centre line a lookup reaches for its thickness (Near measures to the centre line)
    const float ReachM = 0.5f;

    /// <summary>
    /// Every standing thin piece the path of a round crosses: from <paramref name="from"/> along unit <paramref name="dir"/> for <paramref name="length"/> m, the flat path against each
    /// section's centre line, the round's height there between Bottom and Top. Thin fences need not be solid (the village's are not): a round flies on through them, so it takes
    /// every piece it passes; where they are solid it stops on the near face, so the caller lengthens the path by <see cref="ShotSlackM"/> plus half a thickness. In the order the round meets them.
    /// </summary>
    public static void PathPieces(IReadOnlyList<FenceSectionInfo> s, FenceIndex idx, FenceState state, Vector3 from, Vector3 dir, float length, List<(ushort Section, byte Piece)> into)
    {
        if (!(length > 0)) return;
        var p0 = new Vector2(from.X, from.Z);
        var d = new Vector2(dir.X, dir.Z);
        if (d.LengthSquared() < 1e-12f) return; // straight up or down: it crosses no fence line
        var near = Scratch();
        idx.AlongSegment(p0, p0 + d * length, near);
        var start = into.Count;
        var at = ScratchT();
        foreach (var i in near)
        {
            var sec = s[i];
            var e = sec.B - sec.A;
            var den = Cross(d, e);
            if (MathF.Abs(den) < 1e-9f) continue; // parallel to the fence
            var w = sec.A - p0;
            var t = Cross(w, e) / den;   // metres along the round's path
            var u = Cross(w, d) / den;   // 0..1 along the section
            if (t < 0 || t > length || u < 0 || u > 1) continue;
            var y = from.Y + dir.Y * t;
            if (y < sec.Bottom || y > sec.Top) continue;
            if (sec.PieceAt(u * sec.Length) is not { } piece || (state.Broken(i) >> piece & 1) != 0) continue;
            into.Add((sec.Index, (byte)piece));
            at.Add(t);
        }
        // nearest first (a handful of crossings: insertion sort)
        for (var k = 1; k < at.Count; k++)
            for (var m = k; m > 0 && at[m] < at[m - 1]; m--)
            {
                (at[m], at[m - 1]) = (at[m - 1], at[m]);
                (into[start + m], into[start + m - 1]) = (into[start + m - 1], into[start + m]);
            }
    }

    static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    // the lookups of one call; the rules run on the server's thread only, so one list each suffices
    [ThreadStatic] static List<int>? _scratch;
    [ThreadStatic] static List<float>? _scratchT;
    static List<int> Scratch() { var l = _scratch ??= new List<int>(16); l.Clear(); return l; }
    static List<float> ScratchT() { var l = _scratchT ??= new List<float>(8); l.Clear(); return l; }

    /// <summary>Every standing piece whose centre is within <paramref name="radius"/> of <paramref name="at"/> takes <paramref name="damage"/> * (1 - d / radius), rounded, at least 1.</summary>
    public static void Blast(IReadOnlyList<FenceSectionInfo> s, FenceIndex idx, FenceState state, Vector3 at, float radius, int damage, List<PieceHit> into)
    {
        if (!(radius > 0)) return;
        var near = Scratch();
        idx.Near(new Vector2(at.X, at.Z), radius, near);
        foreach (var i in near)
        {
            var sec = s[i];
            var broken = state.Broken(i);
            for (var j = 0; j < sec.Pieces; j++)
            {
                if ((broken >> j & 1) != 0) continue;
                var (f, t) = sec.PieceSpan(j);
                if (t <= f) continue;
                var d = Vector3.Distance(at, sec.PieceCentre(j));
                if (d >= radius) continue;
                into.Add(new PieceHit(sec.Index, (byte)j, Math.Max(1, (int)MathF.Round(damage * (1 - d / radius)))));
            }
        }
    }

    /// <summary>The standing pieces <paramref name="box"/> touches, shifted <paramref name="probe"/> along unit <paramref name="dir"/>: the flat box against each piece's segment widened by thickness / 2, heights overlapping.</summary>
    public static void RamPieces(IReadOnlyList<FenceSectionInfo> s, FenceIndex idx, FenceState state, FenceRamBox box, Vector3 dir, float probe, List<(ushort Section, byte Piece)> into)
    {
        var (sin, cos) = MathF.SinCos(box.Yaw);
        var right = new Vector2(cos, -sin);   // Godot's flat frame: yaw 0 faces -z, positive turns left
        var fwd = new Vector2(-sin, -cos);
        var centre = new Vector2(box.Feet.X, box.Feet.Z);
        var shift = new Vector2(dir.X, dir.Z) * probe;
        var sweep = new Sweep(centre, right, fwd, box.HalfWidth, box.HalfLength, shift);
        var shiftDir = shift.LengthSquared() > 1e-8f ? Vector2.Normalize(shift) : Vector2.Zero;
        var top = box.Feet.Y + box.Height;
        var near = Scratch();
        idx.Near(centre + shift * 0.5f, MathF.Sqrt(box.HalfWidth * box.HalfWidth + box.HalfLength * box.HalfLength) + probe * 0.5f + ReachM, near);
        foreach (var i in near)
        {
            var sec = s[i];
            if (box.Feet.Y >= sec.Top || top <= sec.Bottom) continue;
            var broken = state.Broken(i);
            var d = sec.Dir;
            var n = new Vector2(-d.Y, d.X);
            var half = sec.ThicknessM * 0.5f;
            for (var j = 0; j < sec.Pieces; j++)
            {
                if ((broken >> j & 1) != 0) continue;
                var (f, t) = sec.PieceSpan(j);
                if (t - f < 1e-4f) continue;
                var pc = sec.A + d * ((f + t) * 0.5f);
                var along = (t - f) * 0.5f;
                var rect = new Rect(pc, d, n, along, half);
                if (sweep.Separated(right, rect) || sweep.Separated(fwd, rect) || sweep.Separated(d, rect) || sweep.Separated(n, rect)) continue;
                if (shiftDir != Vector2.Zero && sweep.Separated(new Vector2(-shiftDir.Y, shiftDir.X), rect)) continue;
                into.Add((sec.Index, (byte)j));
            }
        }
    }

    // a piece seen from above: centre, its two unit axes and half sizes along them
    readonly record struct Rect(Vector2 Centre, Vector2 Along, Vector2 Across, float HalfAlong, float HalfAcross);

    // the vehicle's box from above and the shift it is probed along: the separating-axis test of two convex polygons, the sweep being a Minkowski sum with the shift
    readonly record struct Sweep(Vector2 Centre, Vector2 Right, Vector2 Fwd, float HalfWidth, float HalfLength, Vector2 Shift)
    {
        public bool Separated(Vector2 ax, in Rect r)
        {
            var c = Vector2.Dot(Centre, ax);
            var e = HalfWidth * MathF.Abs(Vector2.Dot(Right, ax)) + HalfLength * MathF.Abs(Vector2.Dot(Fwd, ax));
            var s = Vector2.Dot(Shift, ax);
            float lo = c - e + MathF.Min(0, s), hi = c + e + MathF.Max(0, s);
            var pc = Vector2.Dot(r.Centre, ax);
            var pe = r.HalfAlong * MathF.Abs(Vector2.Dot(r.Along, ax)) + r.HalfAcross * MathF.Abs(Vector2.Dot(r.Across, ax));
            return pc + pe < lo || pc - pe > hi;
        }
    }
}

using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>Every number of the harvester event (#331). Metres, seconds, m/s. A host may replace the instance (<c>NetServer.Harvester</c>) to tune.</summary>
public sealed class HarvesterConfig
{
    /// <summary>After a harvester is done (left the field, lost its driver, was destroyed) no new one comes for this long: 12 minutes.</summary>
    public float CooldownS { get; set; } = 12 * 60;
    /// <summary>No automatic harvester this long after the server starts or a round begins.</summary>
    public float InitialDelayS { get; set; } = 10 * 60;
    /// <summary>The automatic spawn rolls this often (seconds) once the cooldown is over, and succeeds with <see cref="AutoChance"/>: rare, a few times an hour.</summary>
    public float AutoRollS { get; set; } = 60;
    public float AutoChance { get; set; } = 0.12f;
    /// <summary>A field is "near players" when a human is within this of its outline; the harvester picks among those.</summary>
    public float NearPlayerM { get; set; } = 350;
    /// <summary>Fields smaller than this are no harvester job (they would give no pass).</summary>
    public float MinFieldAreaM2 { get; set; } = 1500;

    /// <summary>The cutting width of the header (the reel) and how much neighbouring passes overlap.</summary>
    public float HeaderWidthM { get; set; } = 4.8f;
    public float PassOverlapM { get; set; } = 0.3f;
    /// <summary>The body stays this far inside the outline while it cuts; passes shorter than <see cref="MinPassM"/> are not driven.</summary>
    public float EdgeMarginM { get; set; } = 3.5f;
    public float MinPassM { get; set; } = 8;
    /// <summary>How far the header is in front of the vehicle's origin (the reel's middle), and the headland a turn uses past the pass end.</summary>
    public float HeaderForwardM { get; set; } = 3.66f;
    public float HeadlandM { get; set; } = 9;
    /// <summary>The machine's tightest turn (vehicles.json harvester <c>turn_radius</c>): the turn between two passes is a half circle of half their distance when that is not tighter (the planner skips lanes so it is), else a bulb turn of three arcs of this radius.</summary>
    public float TurnRadiusM { get; set; } = 7f;

    public float CutSpeedMps { get; set; } = 3.2f;
    public float TransitSpeedMps { get; set; } = 5.0f;
    public float TurnSpeedMps { get; set; } = 2.4f;
    /// <summary>A waypoint counts as reached within this; a leg that takes longer than <see cref="LegTimeoutS"/> is given up (a stuck machine goes on).</summary>
    public float ArriveM { get; set; } = 3.0f;
    public float LegTimeoutS { get; set; } = 90;

    public float ReelDamage { get; set; } = 500;

    /// <summary>A corpse drawn in at the front (it orbits the reel for this long, #351), and the time it spends in the machine before the bale leaves the unloader.</summary>
    public float DrawSeconds { get; set; } = 3.6f;
    public float ChewSeconds { get; set; } = 2.5f;
    /// <summary>The reel's axis in machine space (x 0, y up, z ahead: the model's reel is 3.66 m ahead, 0.81 m up, 0.68 m radius) and the radius the drawn-in body orbits it at.</summary>
    public float ReelAxisUpM { get; set; } = 0.81f;
    public float OrbitRadiusM { get; set; } = 0.95f;
    /// <summary>
    /// The bale of a victim leaves the unloader (behind and to the left of the machine, this high) thrown: backwards, to the left and up (m/s) on top of the machine's own velocity, and rolls on
    /// after it lands (<see cref="CorpseRules.RollFriction"/>).
    /// </summary>
    public float UnloaderHeightM { get; set; } = 1.6f;
    public float BaleThrowBackMps { get; set; } = 7f;
    public float BaleThrowLeftMps { get; set; } = 2.5f;
    public float BaleThrowUpMps { get; set; } = 5f;

    /// <summary>A bale burns this long and ignites players within <see cref="BaleFireRadiusM"/> of it; the burn of such a player (<see cref="BaleBurn"/>) is credited to nobody.</summary>
    public float BaleBurnSeconds { get; set; } = 6;
    public float BaleFireRadiusM { get; set; } = 2.6f;
    /// <summary>A shot ignites a bale it passes within this of its centre.</summary>
    public float BaleHitRadiusM { get; set; } = 0.9f;
    /// <summary>A corpse bale never expires (infinity): burnt to ash is the only way it goes, besides the round and the safety cap <see cref="CorpseBook.MaxBales"/>.</summary>
    public float BaleLifetimeS { get; set; } = float.PositiveInfinity;
    /// <summary>
    /// The real path cuts (<see cref="FieldSwath"/>): the header's position is sampled every <see cref="SwathSampleM"/> of travel; a stretch is kept when at least <see cref="SwathNewShare"/> of its swath is ground
    /// not cut before. A field keeps at most <see cref="MaxSwathPoints"/> points of history (all fields <see cref="MaxSwathPointsTotal"/>), so a late joiner's replay is bounded.
    /// </summary>
    public float SwathSampleM { get; set; } = 2;
    public float SwathNewShare { get; set; } = 0.3f;
    public int MaxSwathPoints { get; set; } = 6000;
    public int MaxSwathPointsTotal { get; set; } = 40000;
    /// <summary>The machine cuts (and drops bales) only while it moves forward faster than this.</summary>
    public float CutMinSpeedMps { get; set; } = 0.4f;
    /// <summary>One round bale per this many metres of cut on a grain or rapeseed field, at most <see cref="MaxDroppedBales"/> per field.</summary>
    public float DroppedBaleEveryM { get; set; } = 60;
    public int MaxDroppedBales { get; set; } = 24;
    /// <summary>The unloader's spot relative to the reel: this far behind it (the reel's 3.66 + the machine's half length 4.3 + 1.6) and this far to the left (the corpse bale's spot).</summary>
    public float DroppedBaleBehindM { get; set; } = 9.5f;
    public float DroppedBaleLeftM { get; set; } = 1.6f;
    /// <summary>A field's dropped bales fade out this long (10 minutes) after the last cut there, for the server and every client; a round reset removes them sooner.</summary>
    public float DroppedBaleFadeS { get; set; } = 10 * 60;
    /// <summary>
    /// The reel: the only part of the machine that cuts and catches (SM_Veh_Harvester_Reel: 4.85 m wide, 3.66 m ahead of the origin, a 0.68 m radius, plus a hand of margin). Machine space.
    /// </summary>
    public ReelBox Reel { get; set; } = new(3.66f, 2.5f, 0.9f, -0.3f, 2.2f);
    public BurnDef BaleBurn { get; set; } = new() { Dps = 3f, DurationS = 4f, TickIntervalS = 1f };
}

/// <summary>One straight run across a field, cutting from <see cref="Start"/> to <see cref="End"/>.</summary>
public readonly record struct HarvestPass(Vector2 Start, Vector2 End)
{
    public float Length => Vector2.Distance(Start, End);
    public Vector2 Dir => Length < 1e-4f ? Vector2.UnitX : Vector2.Normalize(End - Start);
}

/// <summary>Where on its drive a leg is: heading for a point, in the phase (<see cref="HarvesterPhase"/>) the machine is in while it drives it.</summary>
public readonly record struct HarvestLeg(Vector2 Target, HarvesterPhase Phase, int Pass, float SpeedMps);

/// <summary>The plan of one harvester job: the passes across the field, where it comes in and goes out, and the legs between.</summary>
public sealed class HarvestPlan
{
    public HarvestPlan(IReadOnlyList<HarvestPass> passes, Vector2 entry, float entryYaw, IReadOnlyList<HarvestLeg> legs)
    {
        Passes = passes; Entry = entry; EntryYaw = entryYaw; Legs = legs;
    }

    public IReadOnlyList<HarvestPass> Passes { get; }
    /// <summary>Where the machine appears (just inside the outline) and the way it faces (towards the first pass).</summary>
    public Vector2 Entry { get; }
    public float EntryYaw { get; }
    public IReadOnlyList<HarvestLeg> Legs { get; }
    public float TotalCutM => Passes.Sum(p => p.Length);
}

/// <summary>Plans the passes across a field and the way out along its edge. Pure; the client replays the same plan from the same ring to draw the stubble.</summary>
public static class FieldPlanner
{
    /// <summary>Yaw (game convention: 0 faces -Z, positive turns left) of the direction <paramref name="d"/> on the XZ plane.</summary>
    public static float YawOf(Vector2 d) => MathF.Atan2(-d.X, -d.Y);

    public static Vector2 Forward(float yaw) => new(-MathF.Sin(yaw), -MathF.Cos(yaw));

    public static float Wrap(float a)
    {
        while (a > MathF.PI) a -= MathF.Tau;
        while (a < -MathF.PI) a += MathF.Tau;
        return a;
    }

    /// <summary>Plans a job on a ring. Null when no pass fits (a sliver, or a field too small).</summary>
    public static HarvestPlan? Plan(IReadOnlyList<Vector2> ring, HarvesterConfig cfg) =>
        Plan(ring, cfg.HeaderWidthM, cfg.PassOverlapM, cfg.EdgeMarginM, cfg.MinPassM, cfg.HeadlandM, cfg);

    public static HarvestPlan? Plan(IReadOnlyList<Vector2> ring, float header, float overlap, float margin, float minPass, float headland, HarvesterConfig cfg)
    {
        if (ring.Count < 3) return null;
        var u = LongestEdgeDir(ring);
        var v = new Vector2(-u.Y, u.X);
        var vmin = float.MaxValue; var vmax = float.MinValue;
        foreach (var p in ring) { var b = Vector2.Dot(p, v); vmin = MathF.Min(vmin, b); vmax = MathF.Max(vmax, b); }
        var step = Math.Max(0.5f, header - overlap);
        // the lanes (a lane is a line across the field, cut in one or more spans), then the order they are driven in: lane i, then one at least k lanes away (k = the lanes a half circle of the
        // machine's turning radius spans), so every turn is a smooth arc at or above the radius; the lanes in between come on the way back. A pass runs the other way than the one before.
        var pieces = new List<(int Lane, float A0, float A1, float B)>();
        var line = 0;
        for (var b = vmin + margin + header / 2f; b <= vmax - margin + 1e-3f; b += step, line++)
            foreach (var sp in Spans(ring, u, v, b).Where(x => x.To - x.From - 2 * margin >= minPass).OrderBy(x => x.From))
                pieces.Add((line, sp.From + margin, sp.To - margin, b));
        var passes = new List<HarvestPass>();
        foreach (var pc in OrderPieces(pieces, SkipLanes(cfg.TurnRadiusM, step)))
        {
            var p0 = u * pc.A0 + v * pc.B; var p1 = u * pc.A1 + v * pc.B;
            passes.Add(passes.Count % 2 == 0 ? new HarvestPass(p0, p1) : new HarvestPass(p1, p0));
        }
        if (passes.Count == 0) return null;

        var centroid = Poly.Centroid(ring);
        var entryIdx = Nearest(ring, passes[0].Start);
        var entry = Inset(ring[entryIdx], centroid, margin);
        var legs = new List<HarvestLeg>
        {
            new(passes[0].Start, HarvesterPhase.Approach, -1, cfg.TransitSpeedMps),
        };
        for (var i = 0; i < passes.Count; i++)
        {
            legs.Add(new HarvestLeg(passes[i].End, HarvesterPhase.Cut, i, cfg.CutSpeedMps));
            if (i + 1 >= passes.Count) break;
            var next = passes[i + 1];
            AddTurn(legs, passes[i], next, i, headland, cfg);
        }
        // the way out: along the edge (the inset outline) the shorter way round to where it came in, then past it
        var lastEnd = passes[^1].End;
        var from = Nearest(ring, lastEnd);
        foreach (var idx in Walk(ring.Count, from, entryIdx))
            legs.Add(new HarvestLeg(Inset(ring[idx], centroid, margin), HarvesterPhase.Leave, passes.Count - 1, cfg.TransitSpeedMps));
        var outward = ring[entryIdx] - centroid;
        outward = outward.LengthSquared() < 1e-6f ? Vector2.UnitX : Vector2.Normalize(outward);
        legs.Add(new HarvestLeg(ring[entryIdx] + outward * 6f, HarvesterPhase.Leave, passes.Count - 1, cfg.TransitSpeedMps));

        var toFirst = passes[0].Start - entry;
        return new HarvestPlan(passes, entry, YawOf(toFirst.LengthSquared() < 1e-4f ? passes[0].Dir : toFirst), legs);
    }

    /// <summary>The lanes between two passes that a half circle of <paramref name="radius"/> needs: ceil(2R / lane spacing).</summary>
    public static int SkipLanes(float radius, float spacing) => Math.Max(1, (int)MathF.Ceiling(2f * radius / Math.Max(0.1f, spacing) - 1e-3f));

    /// <summary>
    /// Greedy lane order: from the lane just cut the nearest lane that is at least <paramref name="skip"/> lanes away (so the turn is a half circle of the turning radius or wider); when none is
    /// left (the last lanes) the nearest one, which gets a bulb turn.
    /// </summary>
    static List<(int Lane, float A0, float A1, float B)> OrderPieces(List<(int Lane, float A0, float A1, float B)> pieces, int skip)
    {
        var left = pieces.ToList();
        var order = new List<(int Lane, float A0, float A1, float B)>();
        if (left.Count == 0) return order;
        var cur = left[0]; left.RemoveAt(0); order.Add(cur);
        while (left.Count > 0)
        {
            var far = left.Where(x => Math.Abs(x.Lane - cur.Lane) >= skip).OrderBy(x => Math.Abs(x.Lane - cur.Lane)).ThenBy(x => x.Lane).Cast<(int Lane, float A0, float A1, float B)?>().FirstOrDefault();
            cur = far ?? left.OrderBy(x => Math.Abs(x.Lane - cur.Lane)).ThenBy(x => x.Lane).First();
            left.Remove(cur); order.Add(cur);
        }
        return order;
    }

    /// <summary>
    /// The turn from the end of <paramref name="a"/> to the start of <paramref name="b"/>: a half circle past the end of the pass when the passes are far enough apart for the machine's turning
    /// radius (<see cref="HarvesterConfig.TurnRadiusM"/>), else a bulb turn (<see cref="AddBulb"/>: three arcs of that radius), and for a pass on the same line a run out past the end, across and back in.
    /// </summary>
    static void AddTurn(List<HarvestLeg> legs, HarvestPass a, HarvestPass b, int pass, float headland, HarvesterConfig cfg)
    {
        var d = a.Dir;
        var sideways = b.Start - a.End;
        var gap = sideways.Length();
        var lateral = MathF.Abs(sideways.X * -d.Y + sideways.Y * d.X);
        if (gap >= 2f * cfg.TurnRadiusM * 0.999f && MathF.Abs(Vector2.Dot(sideways, d)) < gap * 0.3f)
        {
            var mid = (a.End + b.Start) / 2f; var r = gap / 2f;
            var half = (a.End - mid); // from the middle to where the pass ends: the lateral of the half circle
            for (var k = 1; k <= 5; k++)
            {
                var phi = k * MathF.PI / 6f;
                legs.Add(new HarvestLeg(mid + d * (r * MathF.Sin(phi)) + half * MathF.Cos(phi), HarvesterPhase.Turn, pass, cfg.TurnSpeedMps));
            }
        }
        else if (lateral > 0.5f && lateral < 2f * cfg.TurnRadiusM && Dot(b.Dir, d) < -0.99f) AddBulb(legs, a, b, pass, cfg);
        else
        {
            legs.Add(new HarvestLeg(a.End + d * headland, HarvesterPhase.Turn, pass, cfg.TurnSpeedMps));
            legs.Add(new HarvestLeg(b.Start - b.Dir * headland, HarvesterPhase.Turn, pass, cfg.TurnSpeedMps));
        }
        legs.Add(new HarvestLeg(b.Start, HarvesterPhase.Turn, pass, cfg.TurnSpeedMps));
    }

    static float Dot(Vector2 p, Vector2 q) => Vector2.Dot(p, q);

    /// <summary>
    /// A bulb (omega) turn between two parallel passes less than two radii apart: straight on past the end, then a right, a left and a right arc (the first away from the next pass) of the turning
    /// radius, ending on the next pass's line heading the other way. The arcs are sampled every 15 degrees.
    /// </summary>
    static void AddBulb(List<HarvestLeg> legs, HarvestPass a, HarvestPass b, int pass, HarvesterConfig cfg)
    {
        var r = cfg.TurnRadiusM;
        var d = a.Dir;
        var toward = b.Start - a.End;
        var delta = Dot(toward, new Vector2(-d.Y, d.X)); // signed lateral distance to the next pass
        var lat = new Vector2(-d.Y, d.X) * MathF.Sign(delta);
        var dy = MathF.Abs(delta);
        // the first and the last arc turn alpha, the middle one pi + 2 alpha: find the alpha that ends dy to the side
        float lo = 0, hi = MathF.PI;
        for (var i = 0; i < 50; i++)
        {
            var mid = (lo + hi) / 2f;
            if (BulbEnd(mid, r).Y > dy) lo = mid; else hi = mid;
        }
        var alpha = (lo + hi) / 2f;
        var end = BulbEnd(alpha, r);
        var along = Dot(b.Start - a.End, d); // where the next pass starts along the old direction
        var lead = MathF.Max(0f, along - end.X + 1f); // straight on first, so the turn ends beyond where the next pass starts
        Vector2 At(float x, float y) => a.End + d * x + lat * y;
        var x0 = lead; var y0 = 0f; var th = 0f;
        if (lead > 0.5f) legs.Add(new HarvestLeg(At(x0, y0), HarvesterPhase.Turn, pass, cfg.TurnSpeedMps));
        foreach (var (turn, sweep) in new[] { (-1f, alpha), (1f, MathF.PI + 2f * alpha), (-1f, alpha) })
        {
            var steps = Math.Max(1, (int)MathF.Ceiling(sweep / (MathF.PI / 12f)));
            var dth = turn * sweep / steps;
            for (var k = 0; k < steps; k++)
            {
                // a chord of the arc: heading changes by dth over arc length r * |dth|; the chord leaves at the mean heading
                var h = th + dth / 2f;
                var chord = 2f * r * MathF.Sin(MathF.Abs(dth) / 2f);
                x0 += MathF.Cos(h) * chord; y0 += MathF.Sin(h) * chord; th += dth;
                legs.Add(new HarvestLeg(At(x0, y0), HarvesterPhase.Turn, pass, cfg.TurnSpeedMps));
            }
        }
    }

    /// <summary>Where the three arcs of a bulb turn with first and last arc <paramref name="alpha"/> end, relative to where they began (heading +x, +y to the next pass).</summary>
    public static Vector2 BulbEnd(float alpha, float r)
    {
        float x = 0, y = 0, th = 0;
        foreach (var (turn, sweep) in new[] { (-1f, alpha), (1f, MathF.PI + 2f * alpha), (-1f, alpha) })
        {
            // centre is to the left of the heading for a left turn (turn = 1), to the right for a right turn
            var cxx = x + (-MathF.Sin(th)) * r * turn; var cyy = y + MathF.Cos(th) * r * turn;
            th += turn * sweep;
            x = cxx + MathF.Sin(th) * r * turn; y = cyy - MathF.Cos(th) * r * turn;
        }
        return new Vector2(x, y);
    }

    /// <summary>The direction of the ring's longest edge: fields run along it.</summary>
    public static Vector2 LongestEdgeDir(IReadOnlyList<Vector2> ring)
    {
        var best = 0f; var dir = Vector2.UnitX;
        for (var i = 0; i < ring.Count; i++)
        {
            var e = ring[(i + 1) % ring.Count] - ring[i];
            var l = e.Length();
            if (l > best) { best = l; dir = e / l; }
        }
        return dir;
    }

    /// <summary>The stretches of the line <c>dot(p, v) == b</c> inside the ring, as intervals of <c>dot(p, u)</c>.</summary>
    public static List<(float From, float To)> Spans(IReadOnlyList<Vector2> ring, Vector2 u, Vector2 v, float b)
    {
        var xs = new List<float>();
        for (var i = 0; i < ring.Count; i++)
        {
            var p = ring[i]; var q = ring[(i + 1) % ring.Count];
            var pb = Vector2.Dot(p, v); var qb = Vector2.Dot(q, v);
            if ((pb <= b && qb > b) || (qb <= b && pb > b))
            {
                var t = (b - pb) / (qb - pb);
                xs.Add(Vector2.Dot(p, u) + t * (Vector2.Dot(q, u) - Vector2.Dot(p, u)));
            }
        }
        xs.Sort();
        var spans = new List<(float, float)>();
        for (var i = 0; i + 1 < xs.Count; i += 2) spans.Add((xs[i], xs[i + 1]));
        return spans;
    }

    static int Nearest(IReadOnlyList<Vector2> ring, Vector2 p)
    {
        var best = 0; var bd = float.MaxValue;
        for (var i = 0; i < ring.Count; i++) { var d = Vector2.DistanceSquared(ring[i], p); if (d < bd) { bd = d; best = i; } }
        return best;
    }

    static Vector2 Inset(Vector2 v, Vector2 centre, float by)
    {
        var d = centre - v;
        var l = d.Length();
        return l < 1e-3f ? v : v + d / l * MathF.Min(by, l * 0.5f);
    }

    /// <summary>The vertex indices from <paramref name="from"/> to <paramref name="to"/> (both included) the shorter way round a ring of <paramref name="n"/>.</summary>
    public static List<int> Walk(int n, int from, int to)
    {
        var fwd = ((to - from) % n + n) % n;
        var back = n - fwd;
        var step = fwd <= back ? 1 : -1;
        var list = new List<int> { from };
        for (var i = from; i != to;) { i = ((i + step) % n + n) % n; list.Add(i); }
        return list;
    }
}

/// <summary>How far the cut has come: the pass being cut and the metres done on it. Only ever grows. This is all the harvester sends about the field (the passes are replayed from the plan).</summary>
public readonly record struct HarvestProgress(int Pass, float Along)
{
    public static readonly HarvestProgress None = new(-1, 0);

    /// <summary>The straight cuts from <paramref name="from"/> to <paramref name="to"/> as segments along the passes: what the client cuts into the crop since it last drew.</summary>
    public static List<(Vector2 A, Vector2 B)> Cuts(IReadOnlyList<HarvestPass> passes, HarvestProgress from, HarvestProgress to)
    {
        var list = new List<(Vector2, Vector2)>();
        if (to.Pass < 0 || passes.Count == 0) return list;
        var last = Math.Min(to.Pass, passes.Count - 1);
        for (var p = Math.Max(0, from.Pass); p <= last; p++)
        {
            var pass = passes[p];
            var a = p == from.Pass ? from.Along : 0f;
            var b = p == to.Pass ? to.Along : pass.Length;
            a = Math.Clamp(a, 0, pass.Length); b = Math.Clamp(b, 0, pass.Length);
            if (b - a < 1e-3f) continue;
            list.Add((pass.Start + pass.Dir * a, pass.Start + pass.Dir * b));
        }
        return list;
    }

    /// <summary>The progress after the header reached <paramref name="header"/> on pass <paramref name="pass"/>; never behind <paramref name="now"/>.</summary>
    public static HarvestProgress Advance(HarvestProgress now, IReadOnlyList<HarvestPass> passes, int pass, Vector2 header)
    {
        if (pass < 0 || pass >= passes.Count) return now;
        var p = passes[pass];
        var along = Math.Clamp(Vector2.Dot(header - p.Start, p.Dir), 0, p.Length);
        var next = new HarvestProgress(pass, along);
        if (now.Pass > pass || (now.Pass == pass && now.Along >= along)) return now;
        return next;
    }
}

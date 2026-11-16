using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>
/// The round bales a harvester drops while it cuts grain or rapeseed (#331). Cosmetic and free: none is sent. Bale <c>i</c> (0-based) lies where the unloader was when <c>(i+1) * DroppedBaleEveryM</c>
/// metres of swath were finished, which depends on the field's plan only; how many exist depends on the progress (<see cref="HarvestProgress"/>) the server already sends. So the server (for the shot test)
/// and every client (for the picture) derive the same list from the same plan and progress. Their fire is <see cref="HayBale"/>, per index.
/// </summary>
public static class DroppedBales
{
    /// <summary>Metres of swath finished at <paramref name="p"/> (the passes before it whole, the pass being cut as far as it is).</summary>
    public static float MetresDone(IReadOnlyList<HarvestPass> passes, HarvestProgress p)
    {
        if (p.Pass < 0) return 0;
        var sum = 0f;
        for (var i = 0; i < passes.Count && i < p.Pass; i++) sum += passes[i].Length;
        if (p.Pass >= passes.Count) return sum;
        return sum + Math.Clamp(p.Along, 0, passes[p.Pass].Length);
    }

    /// <summary>How many bales exist at <paramref name="p"/>: one per <see cref="HarvesterConfig.DroppedBaleEveryM"/> done, at most <see cref="HarvesterConfig.MaxDroppedBales"/>.</summary>
    public static int Count(IReadOnlyList<HarvestPass> passes, HarvestProgress p, HarvesterConfig cfg) =>
        cfg.DroppedBaleEveryM <= 0 ? 0 : Math.Clamp((int)MathF.Floor(MetresDone(passes, p) / cfg.DroppedBaleEveryM), 0, Math.Max(0, cfg.MaxDroppedBales));

    /// <summary>Where bale <paramref name="index"/> lies (game x, z) and which way it faces (yaw, the pass's direction); null when the passes are too short for it.</summary>
    public static (Vector2 Pos, float Yaw)? At(IReadOnlyList<HarvestPass> passes, int index, HarvesterConfig cfg)
    {
        var target = (index + 1) * cfg.DroppedBaleEveryM;
        var done = 0f;
        foreach (var pass in passes)
        {
            var len = pass.Length;
            if (target > done + len + 1e-3f) { done += len; continue; }
            var dir = pass.Dir;
            var header = pass.Start + dir * Math.Clamp(target - done, 0, len);
            var left = new Vector2(dir.Y, -dir.X); // game: forward (0,-1) is north, its left is west (-1,0)
            return (header - dir * cfg.DroppedBaleBehindM + left * cfg.DroppedBaleLeftM, FieldPlanner.YawOf(dir));
        }
        return null;
    }

    /// <summary>All the bales that exist at <paramref name="p"/>, in drop order.</summary>
    public static List<(Vector2 Pos, float Yaw)> Derive(IReadOnlyList<HarvestPass> passes, HarvestProgress p, HarvesterConfig cfg)
    {
        var list = new List<(Vector2, float)>();
        for (int i = 0, n = Count(passes, p, cfg); i < n; i++)
            if (At(passes, i, cfg) is { } b) list.Add(b);
        return list;
    }
}

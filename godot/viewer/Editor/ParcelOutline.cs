using Drycastle.MapLoader.Core;
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Drycastle.Viewer.Editor;

/// <summary>The edges of parcels and of property groups as ground segments (x, z pairs of the map).</summary>
public static class ParcelOutline
{
    public static IEnumerable<(Vector2, Vector2)> Edges(ParcelInfo p)
    {
        foreach (var ring in p.Rings)
            for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
                if (ring[i].DistanceSquaredTo(ring[j]) > 1e-8f) yield return (ring[j], ring[i]);
    }

    static (long, long) Q(Vector2 v) => ((long)Math.Round(v.X * 100.0), (long)Math.Round(v.Y * 100.0));

    /// <summary>
    /// Outer shape of a group: all member edges, minus those that occur in two members with endpoints
    /// matching to 1 cm (the shared boundaries).
    /// </summary>
    public static List<(Vector2, Vector2)> GroupPerimeter(IReadOnlyList<ParcelInfo> members)
    {
        var count = new Dictionary<((long, long), (long, long)), int>();
        var edges = new List<((long, long), (long, long), (Vector2, Vector2))>();
        foreach (var m in members)
            foreach (var (a, b) in Edges(m))
            {
                var qa = Q(a); var qb = Q(b);
                if (qa == qb) continue;
                var key = qa.CompareTo(qb) <= 0 ? (qa, qb) : (qb, qa);
                count[key] = count.GetValueOrDefault(key) + 1;
                edges.Add((key.Item1, key.Item2, (a, b)));
            }
        return edges.Where(e => count[(e.Item1, e.Item2)] == 1).Select(e => e.Item3).ToList();
    }
}

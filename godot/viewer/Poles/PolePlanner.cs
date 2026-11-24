using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Drycastle.Viewer.Poles;

/// <summary>
/// Turns a plan (points for utility poles, points on houses) into poles and their links. No Godot types: map metres,
/// x east, y north, like roads.json.
///
/// Links follow the roads: each point belongs to its nearest road and to one side of it. The poles of one road on one
/// side are a **stream**: each is linked to the next along the road (a street with poles on both sides has two streams,
/// and the wires run along each side, they do not zigzag across). The two ends of a stream are linked to the nearest
/// stream of the next road through the junction. A point further than <see cref="OffRoadM"/> from every road is off-road
/// and takes its two nearest points as neighbours. Of all these neighbour pairs the shortest are kept that close no loop
/// (so three poles round a junction become a star, not a triangle); there is no limit on how far apart two poles may be.
/// Where the two streams of one road face each other, a wire now and then crosses the road (a crossing, one per
/// <see cref="CrossEveryM"/> of road at most, between poles that stand across from each other); the nearest pair across
/// the road also joins two streams that nothing else joins, and the end of a stream that would otherwise stop dead
/// continues across the road to the nearest pole there. A house goes to its nearest pole.
/// The pole at each point follows from its place in the line (see <see cref="Solve"/>), and it is turned so that its
/// crossarm stands across the line and its lamp is over its road.
/// </summary>
public static class PolePlanner
{
    public const double OffRoadM = 25, MegaphoneEveryM = 150, CornerDeg = 30, CrossEveryM = 80;
    /// <summary>Ordering weight (metres) that puts a crossing of the road after every link along the roads, so a crossing is used only when nothing else joins the two streams.</summary>
    const double CrossPenalty = 1e6;

    public sealed record Road(string From, string To, (double x, double y)[] Line, string Highway, double Length);
    public sealed record Graph(List<Road> Roads, Dictionary<string, List<int>> At);
    public sealed record Pole(double X, double Y, string Asset, double RotationDeg, int Degree);
    /// <summary>Links and Crossings (the links that cross a road between its two streams) are pairs of pole indexes; HouseTo is the pole of each house, -1 when there is no pole at all.</summary>
    public sealed record Result(List<Pole> Poles, List<(int a, int b)> Links, List<(int a, int b)> Crossings, int[] HouseTo);

    public static Graph Parse(string roadsJson)
    {
        var roads = new List<Road>();
        var at = new Dictionary<string, List<int>>();
        if (roadsJson == "") return new Graph(roads, at);
        using var doc = JsonDocument.Parse(roadsJson);
        foreach (var e in doc.RootElement.GetProperty("edges").EnumerateArray())
        {
            var line = e.GetProperty("polyline").EnumerateArray().Select(p => (p[0].GetDouble(), p[1].GetDouble())).ToArray();
            if (line.Length < 2) continue;
            var len = 0.0;
            for (var i = 1; i < line.Length; i++) len += Dist(line[i - 1], line[i]);
            var road = new Road(e.GetProperty("from").GetString()!, e.GetProperty("to").GetString()!, line,
                e.TryGetProperty("highway", out var h) ? h.GetString() ?? "" : "", len);
            foreach (var node in new[] { road.From, road.To }.Distinct())
            {
                if (!at.TryGetValue(node, out var l)) at[node] = l = new();
                l.Add(roads.Count);
            }
            roads.Add(road);
        }
        return new Graph(roads, at);
    }

    static double Dist((double x, double y) a, (double x, double y) b) => Math.Sqrt((a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y));

    /// <summary>The nearest point of a road's line: how far it is, how far along the line, the point and the line's direction there.</summary>
    static (double dist, double s, (double x, double y) foot, (double x, double y) dir) Project(Road r, (double x, double y) p)
    {
        var best = (dist: double.MaxValue, s: 0.0, foot: p, dir: (1.0, 0.0));
        var along = 0.0;
        for (var i = 1; i < r.Line.Length; i++)
        {
            var a = r.Line[i - 1]; var b = r.Line[i];
            var len = Dist(a, b);
            if (len < 1e-9) continue;
            var t = Math.Clamp(((p.x - a.x) * (b.x - a.x) + (p.y - a.y) * (b.y - a.y)) / (len * len), 0, 1);
            var f = (a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t);
            var d = Dist(p, f);
            if (d < best.dist) best = (d, along + len * t, f, ((b.x - a.x) / len, (b.y - a.y) / len));
            along += len;
        }
        return best;
    }

    static string Pick(string kind, int count, (double x, double y) p)
    {
        var h = (uint)((long)Math.Round(p.x * 2) * 73856093L) ^ (uint)((long)Math.Round(p.y * 2) * 19349663L);
        h ^= h >> 13; h *= 2654435761u; h ^= h >> 16;
        return $"prim:pole_{kind}_{h % (uint)count + 1}";
    }

    /// <summary>Where each pole stands against the roads: its road, the place along it, and the streams that follow.</summary>
    sealed record Snapped(Graph G, IReadOnlyList<(double x, double y)> P, int[] Road, double[] S, (double x, double y)[] Foot, (double x, double y)[] Dir,
        bool[] OnRoad, Dictionary<(int road, int side), List<int>> Streams, Dictionary<int, List<List<int>>> ByRoad);

    /// <summary>The kept links (pairs of pole indexes, smaller first), which of them cross a road, and the length of each.</summary>
    sealed class Wires
    {
        public readonly List<(int a, int b)> Links = new(), Crossings = new();
        public readonly Dictionary<(int, int), double> Length = new();
        public readonly HashSet<(int, int)> Have = new();

        public void Link((int a, int b) key, double len, bool crossing)
        {
            Have.Add(key);
            Links.Add(key);
            if (crossing) Crossings.Add(key);
            Length[key] = len;
        }
    }

    static (int, int) Key(int a, int b) => a < b ? (a, b) : (b, a);

    public static Result Solve(Graph g, IReadOnlyList<(double x, double y)> poles, IReadOnlyList<(double x, double y)> houses)
    {
        var n = poles.Count;
        var sn = Snap(g, poles);
        var w = Join(sn, Candidates(sn));
        AddFacingCrossings(sn, w);
        AddDeadEndCrossings(sn, w);

        var next = Enumerable.Range(0, n).Select(_ => new List<int>()).ToArray();
        foreach (var (a, b) in w.Links) { next[a].Add(b); next[b].Add(a); }
        var megaphone = Megaphones(sn, next, w.Length);
        var built = Enumerable.Range(0, n).Select(i => BuildPole(sn, next, megaphone, i)).ToList();

        var houseTo = houses.Select(h => n == 0 ? -1 : Enumerable.Range(0, n).MinBy(i => Dist(poles[i], h))).ToArray();
        return new Result(built, w.Links, w.Crossings, houseTo);
    }

    static Snapped Snap(Graph g, IReadOnlyList<(double x, double y)> poles)
    {
        var n = poles.Count;
        // each pole's road, and the side of it the pole stands on (+1 left of the road's direction, -1 right)
        var road = new int[n]; var s = new double[n]; var foot = new (double x, double y)[n]; var dir = new (double x, double y)[n]; var onRoad = new bool[n];
        var side = new int[n];
        for (var i = 0; i < n; i++)
        {
            road[i] = -1;
            var bd = double.MaxValue;
            for (var r = 0; r < g.Roads.Count; r++)
            {
                var pr = Project(g.Roads[r], poles[i]);
                if (pr.dist < bd) { bd = pr.dist; road[i] = r; s[i] = pr.s; foot[i] = pr.foot; dir[i] = pr.dir; }
            }
            onRoad[i] = road[i] >= 0 && bd <= OffRoadM;
            if (onRoad[i]) side[i] = dir[i].x * (poles[i].y - foot[i].y) - dir[i].y * (poles[i].x - foot[i].x) >= 0 ? 1 : -1;
        }
        // a stream: the poles of one road on one side of it, in order along it
        var streams = Enumerable.Range(0, n).Where(i => onRoad[i]).GroupBy(i => (road: road[i], side: side[i]))
            .ToDictionary(x => x.Key, x => x.OrderBy(i => s[i]).ToList());
        var byRoad = streams.GroupBy(kv => kv.Key.road).ToDictionary(x => x.Key, x => x.Select(kv => kv.Value).ToList());
        return new Snapped(g, poles, road, s, foot, dir, onRoad, streams, byRoad);
    }

    /// <summary>Neighbour pairs, each with an ordering cost (the length along the road, plus a penalty for a crossing) and the length.</summary>
    static Dictionary<(int, int), (double cost, double len)> Candidates(Snapped sn)
    {
        var (g, poles, n) = (sn.G, sn.P, sn.P.Count);
        var cand = new Dictionary<(int, int), (double cost, double len)>();
        void Add(int a, int b, double len, double penalty = 0)
        {
            if (a == b) return;
            var key = Key(a, b);
            if (!cand.TryGetValue(key, out var old) || len + penalty < old.cost) cand[key] = (len + penalty, len);
        }
        // from the end of a stream through the junctions, over roads without poles, to the nearest stream of the next road with some
        void Walk(int from, string node, int came, double dist, HashSet<string> seen)
        {
            if (!seen.Add(node) || !g.At.TryGetValue(node, out var roads)) return;
            foreach (var e in roads)
            {
                if (e == came) continue;
                var r = g.Roads[e];
                if (r.From == r.To) continue;
                if (sn.ByRoad.TryGetValue(e, out var lists))
                    foreach (var pts in lists)
                    {
                        var j = r.From == node ? pts[0] : pts[^1];
                        Add(from, j, dist + (r.From == node ? sn.S[j] : r.Length - sn.S[j]));
                    }
                else Walk(from, r.From == node ? r.To : r.From, e, dist + r.Length, seen);
            }
        }
        foreach (var (key, pts) in sn.Streams)
        {
            for (var k = 1; k < pts.Count; k++) Add(pts[k - 1], pts[k], sn.S[pts[k]] - sn.S[pts[k - 1]]);
            Walk(pts[0], g.Roads[key.road].From, key.road, sn.S[pts[0]], new HashSet<string>());
            Walk(pts[^1], g.Roads[key.road].To, key.road, g.Roads[key.road].Length - sn.S[pts[^1]], new HashSet<string>());
        }
        // the two streams of one road: each pole's nearest across the road, a candidate of last resort
        foreach (var lists in sn.ByRoad.Values.Where(l => l.Count > 1))
            foreach (var mine in lists)
                foreach (var a in mine)
                    foreach (var other in lists.Where(o => o != mine))
                    {
                        var b = other.MinBy(j => Dist(poles[a], poles[j]));
                        Add(a, b, Dist(poles[a], poles[b]), CrossPenalty);
                    }
        for (var i = 0; i < n; i++)
            if (!sn.OnRoad[i])
                foreach (var j in Enumerable.Range(0, n).Where(j => j != i).OrderBy(j => Dist(poles[i], poles[j])).Take(2))
                    Add(i, j, Dist(poles[i], poles[j]));
        return cand;
    }

    /// <summary>The shortest pairs that close no loop, however far apart.</summary>
    static Wires Join(Snapped sn, Dictionary<(int, int), (double cost, double len)> cand)
    {
        var set = Enumerable.Range(0, sn.P.Count).ToArray();
        int Find(int x) { while (set[x] != x) x = set[x] = set[set[x]]; return x; }
        var w = new Wires();
        foreach (var (pair, c) in cand.OrderBy(c => c.Value.cost).ThenBy(c => c.Key))
        {
            var (a, b) = pair;
            if (Find(a) == Find(b)) continue;
            set[Find(a)] = Find(b);
            w.Link(pair, c.len, c.cost >= CrossPenalty);
        }
        return w;
    }

    /// <summary>Where the two streams of a road face each other, a wire now and then crosses it: mutually nearest poles that
    /// stand across from each other (not further along the road than across it), at most one per CrossEveryM of road.</summary>
    static void AddFacingCrossings(Snapped sn, Wires w)
    {
        var (poles, s) = (sn.P, sn.S);
        foreach (var lists in sn.ByRoad.Values.Where(l => l.Count == 2))
        {
            var (plus, minus) = (lists[0], lists[1]);
            var facing = new List<(int a, int b, double d)>();
            foreach (var a in plus)
            {
                var b = minus.MinBy(j => Dist(poles[a], poles[j]));
                var d = Dist(poles[a], poles[b]);
                if (plus.MinBy(j => Dist(poles[j], poles[b])) == a && Math.Abs(s[a] - s[b]) <= d) facing.Add((a, b, d));
            }
            var at = new List<double>();
            foreach (var (a, b, d) in facing.OrderBy(f => f.d))
            {
                var here = (s[a] + s[b]) / 2;
                if (at.Any(x => Math.Abs(x - here) < CrossEveryM)) continue;
                at.Add(here);
                var key = Key(a, b);
                if (!w.Have.Contains(key)) w.Link(key, d, true); // else joined already (by the last-resort crossing)
            }
        }
    }

    /// <summary>The end of a stream that stops dead (one wire at most) goes on across the road, to the nearest pole of the other stream.</summary>
    static void AddDeadEndCrossings(Snapped sn, Wires w)
    {
        var poles = sn.P;
        var wires = new int[poles.Count];
        foreach (var (a, b) in w.Links) { wires[a]++; wires[b]++; }
        foreach (var lists in sn.ByRoad.Values.Where(l => l.Count == 2))
            for (var k = 0; k < 2; k++)
            {
                var (mine, other) = (lists[k], lists[1 - k]);
                foreach (var a in mine.Count == 1 ? mine : new List<int> { mine[0], mine[^1] })
                {
                    if (wires[a] > 1) continue;
                    var b = other.MinBy(j => Dist(poles[a], poles[j]));
                    var key = Key(a, b);
                    if (w.Have.Contains(key)) continue;
                    w.Link(key, Dist(poles[a], poles[b]), true);
                    wires[a]++; wires[b]++;
                }
            }
    }

    static (double x, double y) Toward(Snapped sn, int i, int j)
    {
        var d = Dist(sn.P[i], sn.P[j]);
        return d < 1e-9 ? (1, 0) : ((sn.P[j].x - sn.P[i].x) / d, (sn.P[j].y - sn.P[i].y) / d);
    }

    /// <summary>The line through a pole: towards its only neighbour, else across the two neighbours that are most opposite.</summary>
    static (double x, double y) LineAt(Snapped sn, List<int>[] next, int i)
    {
        if (next[i].Count == 0) return sn.OnRoad[i] ? sn.Dir[i] : (1, 0);
        if (next[i].Count == 1) return Toward(sn, i, next[i][0]);
        var best = (a: next[i][0], b: next[i][1], dot: double.MaxValue);
        foreach (var a in next[i])
            foreach (var b in next[i])
            {
                var (ua, ub) = (Toward(sn, i, a), Toward(sn, i, b));
                var dot = ua.x * ub.x + ua.y * ub.y;
                if (a < b && dot < best.dot) best = (a, b, dot);
            }
        var (u, v) = (Toward(sn, i, best.a), Toward(sn, i, best.b));
        var (tx, ty) = (u.x - v.x, u.y - v.y);
        var l = Math.Sqrt(tx * tx + ty * ty);
        return l < 1e-6 ? u : (tx / l, ty / l);
    }

    static bool IsCorner(Snapped sn, List<int>[] next, int i)
    {
        if (next[i].Count != 2) return false;
        var (u, v) = (Toward(sn, i, next[i][0]), Toward(sn, i, next[i][1]));
        return Math.Acos(Math.Clamp(-(u.x * v.x + u.y * v.y), -1, 1)) * 180 / Math.PI > CornerDeg;
    }

    static bool IsStreet(Snapped sn, int i) => sn.OnRoad[i] && sn.G.Roads[sn.Road[i]].Highway is not ("track" or "path");

    /// <summary>A megaphone wherever the last one is far enough back along the line, on plain stretches of a street only.</summary>
    static bool[] Megaphones(Snapped sn, List<int>[] next, Dictionary<(int, int), double> length)
    {
        var n = sn.P.Count;
        var megaphone = new bool[n];
        var visited = new bool[n];
        foreach (var start in Enumerable.Range(0, n).OrderBy(i => next[i].Count == 1 ? 0 : 1).ThenBy(i => i))
        {
            if (visited[start]) continue;
            var stack = new Stack<(int v, double since)>();
            stack.Push((start, MegaphoneEveryM * 0.6));
            while (stack.Count > 0)
            {
                var (v, since) = stack.Pop();
                if (visited[v]) continue;
                visited[v] = true;
                if (since >= MegaphoneEveryM && IsStreet(sn, v) && next[v].Count == 2 && !IsCorner(sn, next, v)) { megaphone[v] = true; since = 0; }
                foreach (var w in next[v])
                    if (!visited[w]) stack.Push((w, since + length[Key(v, w)]));
            }
        }
        return megaphone;
    }

    static Pole BuildPole(Snapped sn, List<int>[] next, bool[] megaphone, int i)
    {
        var p = sn.P[i];
        var deg = next[i].Count;
        var asset = !IsStreet(sn, i) ? Pick("field", 2, p)
            : deg >= 3 ? Pick("junction", 3, p)
            : deg <= 1 || IsCorner(sn, next, i) ? (Pick("x", 3, p).EndsWith("3") ? "prim:pole_twin_riser_1" : Pick("twin_lamp", 2, p))
            : megaphone[i] ? Pick("megaphone", 2, p)
            : Pick("x", 5, p).EndsWith("1") || Pick("x", 5, p).EndsWith("2") ? Pick("plain", 2, p) : Pick("lamp", 3, p);
        var (lx, ly) = LineAt(sn, next, i);
        var rot = Math.Atan2(ly, lx) * 180 / Math.PI;
        // the lamp is on the pole's right (its -y): turn the pole round when its road is on the left
        if (sn.OnRoad[i])
        {
            var (rx, ry) = (sn.Foot[i].x - p.x, sn.Foot[i].y - p.y);
            if (ly * rx - lx * ry < 0) rot += 180;
        }
        return new Pole(p.x, p.y, asset, Math.Round(((rot % 360) + 360) % 360, 1), deg);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

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

    public static Result Solve(Graph g, IReadOnlyList<(double x, double y)> poles, IReadOnlyList<(double x, double y)> houses)
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

        // neighbour pairs: an ordering cost (the length along the road, plus a penalty for a crossing) and the length
        var cand = new Dictionary<(int, int), (double cost, double len)>();
        void Add(int a, int b, double len, double penalty = 0)
        {
            if (a == b) return;
            var key = a < b ? (a, b) : (b, a);
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
                if (byRoad.TryGetValue(e, out var lists))
                    foreach (var pts in lists)
                    {
                        var j = r.From == node ? pts[0] : pts[^1];
                        Add(from, j, dist + (r.From == node ? s[j] : r.Length - s[j]));
                    }
                else Walk(from, r.From == node ? r.To : r.From, e, dist + r.Length, seen);
            }
        }
        foreach (var (key, pts) in streams)
        {
            for (var k = 1; k < pts.Count; k++) Add(pts[k - 1], pts[k], s[pts[k]] - s[pts[k - 1]]);
            Walk(pts[0], g.Roads[key.road].From, key.road, s[pts[0]], new HashSet<string>());
            Walk(pts[^1], g.Roads[key.road].To, key.road, g.Roads[key.road].Length - s[pts[^1]], new HashSet<string>());
        }
        // the two streams of one road: each pole's nearest across the road, a candidate of last resort
        foreach (var lists in byRoad.Values.Where(l => l.Count > 1))
            foreach (var mine in lists)
                foreach (var a in mine)
                    foreach (var other in lists.Where(o => o != mine))
                    {
                        var b = other.MinBy(j => Dist(poles[a], poles[j]));
                        Add(a, b, Dist(poles[a], poles[b]), CrossPenalty);
                    }
        for (var i = 0; i < n; i++)
            if (!onRoad[i])
                foreach (var j in Enumerable.Range(0, n).Where(j => j != i).OrderBy(j => Dist(poles[i], poles[j])).Take(2))
                    Add(i, j, Dist(poles[i], poles[j]));

        // the shortest pairs that close no loop, however far apart
        var set = Enumerable.Range(0, n).ToArray();
        int Find(int x) { while (set[x] != x) x = set[x] = set[set[x]]; return x; }
        var links = new List<(int a, int b)>();
        var crossings = new List<(int a, int b)>();
        var length = new Dictionary<(int, int), double>();
        foreach (var (pair, c) in cand.OrderBy(c => c.Value.cost).ThenBy(c => c.Key))
        {
            var (a, b) = pair;
            if (Find(a) == Find(b)) continue;
            set[Find(a)] = Find(b);
            links.Add(pair);
            if (c.cost >= CrossPenalty) crossings.Add(pair);
            length[pair] = c.len;
        }
        // where the two streams of a road face each other, a wire now and then crosses it: mutually nearest poles that
        // stand across from each other (not further along the road than across it), at most one per CrossEveryM of road
        var have = new HashSet<(int, int)>(links);
        foreach (var lists in byRoad.Values.Where(l => l.Count == 2))
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
                var key = a < b ? (a, b) : (b, a);
                if (!have.Add(key)) { at.Add(here); continue; } // joined already (by the last-resort crossing above)
                at.Add(here);
                links.Add(key);
                crossings.Add(key);
                length[key] = d;
            }
        }
        // the end of a stream that stops dead (one wire at most) goes on across the road, to the nearest pole of the other stream
        var wires = new int[n];
        foreach (var (a, b) in links) { wires[a]++; wires[b]++; }
        foreach (var lists in byRoad.Values.Where(l => l.Count == 2))
            for (var k = 0; k < 2; k++)
            {
                var (mine, other) = (lists[k], lists[1 - k]);
                foreach (var a in mine.Count == 1 ? mine : new List<int> { mine[0], mine[^1] })
                {
                    if (wires[a] > 1) continue;
                    var b = other.MinBy(j => Dist(poles[a], poles[j]));
                    var key = a < b ? (a, b) : (b, a);
                    if (!have.Add(key)) continue;
                    links.Add(key);
                    crossings.Add(key);
                    length[key] = Dist(poles[a], poles[b]);
                    wires[a]++; wires[b]++;
                }
            }

        var next = Enumerable.Range(0, n).Select(_ => new List<int>()).ToArray();
        foreach (var (a, b) in links) { next[a].Add(b); next[b].Add(a); }
        (double x, double y) To(int i, int j)
        {
            var d = Dist(poles[i], poles[j]);
            return d < 1e-9 ? (1, 0) : ((poles[j].x - poles[i].x) / d, (poles[j].y - poles[i].y) / d);
        }
        // the line through a pole: towards its only neighbour, else across the two neighbours that are most opposite
        (double x, double y) Line(int i)
        {
            if (next[i].Count == 0) return onRoad[i] ? dir[i] : (1, 0);
            if (next[i].Count == 1) return To(i, next[i][0]);
            var best = (a: next[i][0], b: next[i][1], dot: double.MaxValue);
            foreach (var a in next[i])
                foreach (var b in next[i])
                {
                    var (ua, ub) = (To(i, a), To(i, b));
                    var dot = ua.x * ub.x + ua.y * ub.y;
                    if (a < b && dot < best.dot) best = (a, b, dot);
                }
            var (u, v) = (To(i, best.a), To(i, best.b));
            var (tx, ty) = (u.x - v.x, u.y - v.y);
            var l = Math.Sqrt(tx * tx + ty * ty);
            return l < 1e-6 ? u : (tx / l, ty / l);
        }
        bool Corner(int i)
        {
            if (next[i].Count != 2) return false;
            var (u, v) = (To(i, next[i][0]), To(i, next[i][1]));
            return Math.Acos(Math.Clamp(-(u.x * v.x + u.y * v.y), -1, 1)) * 180 / Math.PI > CornerDeg;
        }
        bool Street(int i) => onRoad[i] && g.Roads[road[i]].Highway is not ("track" or "path");

        // a megaphone wherever the last one is far enough back along the line, on plain stretches of a street only
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
                if (since >= MegaphoneEveryM && Street(v) && next[v].Count == 2 && !Corner(v)) { megaphone[v] = true; since = 0; }
                foreach (var w in next[v])
                    if (!visited[w]) stack.Push((w, since + length[v < w ? (v, w) : (w, v)]));
            }
        }

        var built = new List<Pole>();
        for (var i = 0; i < n; i++)
        {
            var deg = next[i].Count;
            var asset = !Street(i) ? Pick("field", 2, poles[i])
                : deg >= 3 ? Pick("junction", 3, poles[i])
                : deg <= 1 || Corner(i) ? (Pick("x", 3, poles[i]).EndsWith("3") ? "prim:pole_twin_riser_1" : Pick("twin_lamp", 2, poles[i]))
                : megaphone[i] ? Pick("megaphone", 2, poles[i])
                : Pick("x", 5, poles[i]).EndsWith("1") || Pick("x", 5, poles[i]).EndsWith("2") ? Pick("plain", 2, poles[i]) : Pick("lamp", 3, poles[i]);
            var (lx, ly) = Line(i);
            var rot = Math.Atan2(ly, lx) * 180 / Math.PI;
            // the lamp is on the pole's right (its -y): turn the pole round when its road is on the left
            if (onRoad[i])
            {
                var (rx, ry) = (foot[i].x - poles[i].x, foot[i].y - poles[i].y);
                if (ly * rx - lx * ry < 0) rot += 180;
            }
            built.Add(new Pole(poles[i].x, poles[i].y, asset, Math.Round(((rot % 360) + 360) % 360, 1), deg));
        }

        var houseTo = houses.Select(h =>
        {
            return n == 0 ? -1 : Enumerable.Range(0, n).MinBy(i => Dist(poles[i], h));
        }).ToArray();
        return new Result(built, links, crossings, houseTo);
    }
}
